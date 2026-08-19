using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// 專案原始碼的共用唯讀索引。
///
/// 索引在同一個專案根目錄的多次工具呼叫之間共用：文字搜尋使用三字元倒排索引
/// 先縮小候選檔案，C# 符號搜尋則重用已合併的 Roslyn 符號位置，避免每次 Agent
/// 呼叫都重新解析全部檔案。索引不保存整份來源內容，實際命中仍回讀本機檔案確認。
/// </summary>
internal sealed class ProjectSourceIndex
{
    // 單一原始碼檔案超過 16 MiB 時不建立 trigram 與 Roslyn 索引，
    // 但文字搜尋仍會以串流方式掃描，避免大型 SQL 或產生檔形成漏搜。
    private const int MaximumSearchFileBytes = 16 * 1024 * 1024;
    // 索引建立以 CPU 與磁碟都能承受的有界平行度執行。搜尋只平行讀取 trigram
    // 篩選後的候選檔案，不會在每次工具呼叫重新掃描整個專案。
    private static readonly int IndexBuildConcurrency = Math.Clamp(
        Environment.ProcessorCount,
        2,
        6);
    private const int CandidateSearchConcurrency = 8;
    private const int TrigramLength = 3;
    private static readonly HashSet<string> SearchableExtensions = new(
        [
            ".cs", ".csproj", ".sln",
            ".js", ".jsx", ".ts", ".tsx",
            ".aspx", ".ascx", ".master",
            ".sql", ".json", ".xml", ".config",
            ".md", ".txt", ".yaml", ".yml",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExcludedDirectories = new(
        [
            ".git", ".svn", ".vs", "bin", "obj", "node_modules",
            "packages", "dist", "build", "out", "target", "vendor",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<SourceIndexCacheKey, SourceIndexCacheEntry> Indexes = new();
    private static long _cacheSequence;

    private readonly IReadOnlyList<IndexedFile> _files;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<int>> _trigramPostings;
    private readonly IReadOnlyList<int> _unindexedFileNumbers;
    private readonly string _snapshotIdentity;
    private readonly CancellationToken _lifetimeToken;
    private readonly object _symbolGate = new();
    private Task _symbolIndexTask = Task.CompletedTask;
    private bool _symbolIndexStarted;
    private Dictionary<string, IReadOnlyList<SymbolOccurrence>>? _symbols;

    private ProjectSourceIndex(
        IReadOnlyList<IndexedFile> files,
        IReadOnlyDictionary<string, IReadOnlyList<int>> trigramPostings,
        IReadOnlyList<int> unindexedFileNumbers,
        string snapshotIdentity,
        CancellationToken lifetimeToken)
    {
        _files = files;
        _trigramPostings = trigramPostings;
        _unindexedFileNumbers = unindexedFileNumbers;
        _snapshotIdentity = snapshotIdentity;
        _lifetimeToken = lifetimeToken;
    }

    /// <summary>
    /// 取得單一專案版本的共享索引。相同 root 與 snapshot 的併發請求
    /// 只會建立一次索引工作。
    /// </summary>
    /// <param name="rootPath">專案原始碼根目錄。</param>
    /// <param name="expectedFiles">固定 Graph 版本的相對路徑與 SHA-256；未提供時使用當前檔案系統。</param>
    /// <param name="cancellationToken">取消當前等待的 token，不會取消其他呼叫者共用的建立工作。</param>
    /// <returns>與指定專案版本一致的唯讀索引。</returns>
    /// <exception cref="InvalidOperationException">檔案已刪除、內容與 snapshot 不一致，或 snapshot 路徑超出專案根目錄。</exception>
    public static async Task<ProjectSourceIndex> GetOrCreateAsync(
        string rootPath,
        IReadOnlyDictionary<string, string>? expectedFiles = null,
        CancellationToken cancellationToken = default)
    {
        rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var snapshotIdentity = BuildSnapshotIdentity(expectedFiles);
        var key = new SourceIndexCacheKey(rootPath, snapshotIdentity);
        var created = new SourceIndexCacheEntry(
            Interlocked.Increment(ref _cacheSequence),
            token => BuildAsync(rootPath, expectedFiles, token));
        var selected = Indexes.GetOrAdd(key, created);
        if (!ReferenceEquals(selected, created))
            created.Dispose();
        var selectedTask = selected.Build.Value;
        _ = selectedTask.ContinueWith(
            task => RemoveFailedBuild(key, selected, task),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        ProjectSourceIndex result;
        try
        {
            result = await selectedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
                RemoveEntry(key, selected);
            throw;
        }
        catch
        {
            RemoveEntry(key, selected);
            throw;
        }
        PruneOldSnapshots(key);
        return result;
    }

    private static void RemoveFailedBuild(
        SourceIndexCacheKey key,
        SourceIndexCacheEntry entry,
        Task<ProjectSourceIndex> failedTask)
    {
        _ = failedTask.Exception;
        RemoveEntry(key, entry);
    }

    /// <summary>
    /// 刪除專案時，明確移除該根目錄的所有版本索引。
    /// 系統不使用 TTL 或背景檔案偵測，其餘回收由 current／previous 保留策略處理。
    /// </summary>
    /// <param name="rootPath">要從記憶體移除的專案根目錄。</param>
    public static void Forget(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return;
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        foreach (var pair in Indexes.Where(pair =>
                     pair.Key.RootPath.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveEntry(pair.Key, pair.Value);
        }
    }

    /// <summary>每個專案只保留最新兩份來源索引，與 Graph 的 current／previous 保留策略一致。</summary>
    private static void PruneOldSnapshots(SourceIndexCacheKey currentKey)
    {
        var staleEntries = Indexes
            .Where(pair => pair.Key.RootPath.Equals(
                currentKey.RootPath,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Value.Sequence)
            .Skip(2)
            .ToArray();
        foreach (var pair in staleEntries)
            RemoveEntry(pair.Key, pair.Value);
    }

    private static void RemoveEntry(SourceIndexCacheKey key, SourceIndexCacheEntry entry)
    {
        if (!Indexes.TryRemove(new KeyValuePair<SourceIndexCacheKey, SourceIndexCacheEntry>(key, entry)))
            return;
        entry.Dispose();
    }

    /// <summary>文字搜尋的結果及實際檢查的檔案統計。</summary>
    public sealed record TextSearchResult(
        IReadOnlyList<TextMatch> Matches,
        int FilesScanned,
        int FilesSkipped,
        bool WasTruncated);

    /// <summary>一筆帶有來源行列的文字命中。</summary>
    public sealed record TextMatch(string FilePath, int Line, int Column, string Preview);

    /// <summary>C# 符號搜尋的結果及實際檢查的檔案統計。</summary>
    public sealed record SymbolSearchResult(
        IReadOnlyList<SymbolMatch> Matches,
        int FilesScanned,
        bool WasTruncated);

    /// <summary>一筆 Roslyn 語法層的符號命中。</summary>
    public sealed record SymbolMatch(
        string FilePath,
        int Line,
        int Column,
        string Classification,
        string? Container,
        string Preview);

    /// <summary>
    /// 搜尋專案文字。三字元倒排索引只負責選出候選檔案；真正命中仍從本機實體檔案
    /// 逐行確認，避免把索引內容誤當成最後答案。候選分批平行讀取，結果再按檔案與
    /// 行號確定性合併，因此不會因 worker 完成順序改變回答證據。
    /// </summary>
    public async Task<TextSearchResult> SearchTextAsync(
        string query,
        string? extension,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query.Trim();
        var candidates = FindTextCandidates(normalizedQuery)
            .Where(fileNumber => extension is null ||
                Path.GetExtension(_files[fileNumber].FullPath).Equals(
                    extension,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var matches = new List<TextMatch>(Math.Min(maximumResults + 1, 101));
        var filesScanned = 0;
        var filesSkipped = 0;

        // 以固定大小批次執行：同一批最多八個磁碟讀取，批次之間依路徑順序合併。
        // 這兼顧 SSD 與網路磁碟，不會像無界 Task.WhenAll 一次開啟數千個檔案。
        for (var offset = 0;
             offset < candidates.Length && matches.Count <= maximumResults;
             offset += CandidateSearchConcurrency)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = candidates
                .Skip(offset)
                .Take(CandidateSearchConcurrency)
                .Select(fileNumber => ScanTextFileAsync(
                    _files[fileNumber],
                    normalizedQuery,
                    maximumResults + 1,
                    cancellationToken))
                .ToArray();
            var results = await Task.WhenAll(batch).ConfigureAwait(false);
            foreach (var result in results)
            {
                if (result.Skipped)
                {
                    filesSkipped++;
                    continue;
                }
                filesScanned++;
                foreach (var match in result.Matches)
                {
                    if (matches.Count > maximumResults)
                        break;
                    matches.Add(match);
                }
            }
        }

        return new TextSearchResult(
            matches.Take(maximumResults).ToArray(),
            filesScanned,
            filesSkipped,
            matches.Count > maximumResults);
    }

    /// <summary>以已建立的 Roslyn 符號倒排索引尋找 C# 定義及引用。</summary>
    public async Task<SymbolSearchResult> SearchSymbolsAsync(
        string symbol,
        bool includeReferences,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        await EnsureSymbolIndexAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, IReadOnlyList<SymbolOccurrence>> symbols;
        lock (_symbolGate)
            symbols = _symbols!;

        var identifier = symbol.Split('.', StringSplitOptions.RemoveEmptyEntries).Last();
        if (!symbols.TryGetValue(identifier, out var occurrences))
        {
            return new SymbolSearchResult(
                [],
                _files.Count(file => file.IsCSharp && file.IsIndexed),
                false);
        }

        var matches = new List<SymbolMatch>(Math.Min(maximumResults + 1, occurrences.Count));
        foreach (var occurrence in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!includeReferences && occurrence.Classification == "reference")
                continue;
            if (symbol.Contains('.', StringComparison.Ordinal) &&
                occurrence.Classification != "reference" &&
                !QualifiedNameMatches(symbol, occurrence.Container, identifier))
                continue;
            matches.Add(new SymbolMatch(
                occurrence.FilePath,
                occurrence.Line,
                occurrence.Column,
                occurrence.Classification,
                occurrence.Container,
                occurrence.Preview));
            if (matches.Count > maximumResults)
                break;
        }

        return new SymbolSearchResult(
            matches.Take(maximumResults).ToArray(),
            _files.Count(file => file.IsCSharp && file.IsIndexed),
            matches.Count > maximumResults);
    }

    /// <summary>從單一候選檔案確認 literal 命中；檔案過大或無法讀取時回報 skipped。</summary>
    private static async Task<TextFileScanResult> ScanTextFileAsync(
        IndexedFile file,
        string query,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await VerifyStreamHashAsync(file, stream, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var matches = new List<TextMatch>(Math.Min(maximumResults, 100));
            var lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lineNumber++;
                var column = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (column < 0)
                    continue;
                matches.Add(new TextMatch(
                    file.FullPath,
                    lineNumber,
                    column + 1,
                    Truncate(line.Trim(), 500)));
                if (matches.Count >= maximumResults)
                    break;
            }
            return new TextFileScanResult(matches, false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new TextFileScanResult([], true);
        }
    }

    private IReadOnlyList<int> FindTextCandidates(string query)
    {
        if (query.Length < TrigramLength)
            return Enumerable.Range(0, _files.Count).ToArray();

        var normalized = query.ToUpperInvariant();
        HashSet<int>? candidates = null;
        foreach (var trigram in GetTrigrams(normalized))
        {
            if (!_trigramPostings.TryGetValue(trigram, out var postings))
                return _unindexedFileNumbers;
            candidates ??= new HashSet<int>(postings);
            if (candidates.Count == 0)
                return _unindexedFileNumbers;
            candidates.IntersectWith(postings);
        }
        if (_unindexedFileNumbers.Count > 0)
        {
            candidates ??= [];
            candidates.UnionWith(_unindexedFileNumbers);
        }
        return candidates?.OrderBy(value => value).ToArray() ?? _unindexedFileNumbers;
    }

    private async Task EnsureSymbolIndexAsync(CancellationToken cancellationToken)
    {
        Task build;
        lock (_symbolGate)
        {
            if (!_symbolIndexStarted)
            {
                _symbolIndexStarted = true;
                _symbolIndexTask = BuildSymbolIndexAsync(_lifetimeToken);
                var startedTask = _symbolIndexTask;
                _ = startedTask.ContinueWith(
                    _ => ResetFailedSymbolIndex(startedTask),
                    CancellationToken.None,
                    TaskContinuationOptions.NotOnRanToCompletion |
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            build = _symbolIndexTask;
        }
        await build.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>只有真正失敗或取消的共享工作能重設狀態；單一等待者取消不影響其他呼叫。</summary>
    private void ResetFailedSymbolIndex(Task failedTask)
    {
        _ = failedTask.Exception;
        lock (_symbolGate)
        {
            if (ReferenceEquals(_symbolIndexTask, failedTask))
            {
                _symbolIndexStarted = false;
                _symbolIndexTask = Task.CompletedTask;
                _symbols = null;
            }
        }
    }

    /// <summary>有界平行解析 C# 檔案，再依檔案順序合併，兼顧速度與結果確定性。</summary>
    private async Task BuildSymbolIndexAsync(CancellationToken cancellationToken)
    {
        var csharpFiles = _files
            .Where(file => file.IsCSharp && file.IsIndexed)
            .ToArray();
        var fileOccurrences = new IReadOnlyList<(string Identifier, SymbolOccurrence Occurrence)>?[csharpFiles.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, csharpFiles.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = IndexBuildConcurrency,
            },
            async (fileNumber, workerCancellationToken) =>
            {
                var file = csharpFiles[fileNumber];
                try
                {
                    var content = await ReadVerifiedTextAsync(
                        file,
                        workerCancellationToken).ConfigureAwait(false);
                    var tree = CSharpSyntaxTree.ParseText(
                        content,
                        cancellationToken: workerCancellationToken);
                    var root = await tree.GetRootAsync(workerCancellationToken).ConfigureAwait(false);
                    var occurrences = new List<(string, SymbolOccurrence)>();
                    foreach (var token in root.DescendantTokens())
                    {
                        workerCancellationToken.ThrowIfCancellationRequested();
                        if (!token.IsKind(SyntaxKind.IdentifierToken))
                            continue;
                        var position = tree.GetLineSpan(token.Span).StartLinePosition;
                        occurrences.Add((
                            token.ValueText,
                            new SymbolOccurrence(
                                file.FullPath,
                                position.Line + 1,
                                position.Character + 1,
                                ClassifyIdentifier(token),
                                GetContainerName(token.Parent),
                                Truncate(token.Parent?.Parent?.ToString()
                                    .ReplaceLineEndings(" ").Trim() ?? token.ValueText, 500))));
                    }
                    fileOccurrences[fileNumber] = occurrences;
                }
                catch (OperationCanceledException) when (workerCancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    // 單一檔案在索引後被鎖定或刪除時只略過該檔案，不讓整個符號工具失敗。
                    fileOccurrences[fileNumber] = [];
                }
            }).ConfigureAwait(false);

        var symbols = new Dictionary<string, List<SymbolOccurrence>>(StringComparer.Ordinal);
        foreach (var occurrences in fileOccurrences)
        {
            foreach (var item in occurrences ?? [])
            {
                if (!symbols.TryGetValue(item.Identifier, out var list))
                {
                    list = [];
                    symbols[item.Identifier] = list;
                }
                list.Add(item.Occurrence);
            }
        }

        lock (_symbolGate)
        {
            _symbols = symbols.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<SymbolOccurrence>)pair.Value,
                StringComparer.Ordinal);
        }
    }

    private static async Task<ProjectSourceIndex> BuildAsync(
        string rootPath,
        IReadOnlyDictionary<string, string>? expectedFiles,
        CancellationToken cancellationToken)
    {
        // 先建立確定性路徑清單，再平行處理內容；合併一律依陣列順序執行，
        // 因此相同 snapshot 不會因 worker 排程不同而改變 postings 順序。
        var sourceFiles = expectedFiles is null
            ? EnumerateSearchableFiles(rootPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : expectedFiles.Keys
                .Where(relativePath => SearchableExtensions.Contains(Path.GetExtension(relativePath)))
                .Select(relativePath => Path.GetFullPath(Path.Combine(rootPath, relativePath)))
                .Where(path => IsInsideRoot(rootPath, path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var builtFiles = new IndexedFileBuildResult?[sourceFiles.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, sourceFiles.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = IndexBuildConcurrency,
            },
            async (fileNumber, workerCancellationToken) =>
            {
                builtFiles[fileNumber] = await BuildFileAsync(
                    rootPath,
                    sourceFiles[fileNumber],
                    expectedFiles,
                    workerCancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);

        var files = new List<IndexedFile>(sourceFiles.Length);
        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var unindexedFileNumbers = new List<int>();
        foreach (var built in builtFiles)
        {
            if (built is null)
                continue;
            var fileNumber = files.Count;
            files.Add(built.File);
            if (!built.File.IsIndexed)
            {
                unindexedFileNumbers.Add(fileNumber);
                continue;
            }
            foreach (var trigram in built.Trigrams)
            {
                if (!postings.TryGetValue(trigram, out var list))
                {
                    list = [];
                    postings[trigram] = list;
                }
                list.Add(fileNumber);
            }
        }

        return new ProjectSourceIndex(
            files,
            postings.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<int>)pair.Value.ToArray(),
                StringComparer.Ordinal),
            unindexedFileNumbers,
            BuildSnapshotIdentity(expectedFiles),
            cancellationToken);
    }

    /// <summary>
    /// 單一 worker 只讀取檔案一次：同一份 bytes 同時計算 SHA-256、解碼文字並建立
    /// trigram，避免舊實作先雜湊再 ReadAllText 造成雙倍磁碟 I/O。
    /// </summary>
    private static async Task<IndexedFileBuildResult?> BuildFileAsync(
        string rootPath,
        string fullPath,
        IReadOnlyDictionary<string, string>? expectedFiles,
        CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                if (expectedFiles is not null)
                    throw CreateSnapshotFileUnavailableException(rootPath, fullPath);
                return null;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (expectedFiles is not null)
                throw CreateSnapshotFileUnavailableException(rootPath, fullPath, exception);
            return null;
        }

        var relativePath = Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
        string? expectedHash = null;
        if (expectedFiles is not null &&
            !expectedFiles.TryGetValue(relativePath, out expectedHash))
        {
            throw new InvalidOperationException(
                $"檔案不屬於目前圖譜版本：{relativePath}；請重新索引。");
        }

        var isCSharp = Path.GetExtension(fullPath)
            .Equals(".cs", StringComparison.OrdinalIgnoreCase);
        if (info.Length > MaximumSearchFileBytes)
        {
            return new IndexedFileBuildResult(
                new IndexedFile(fullPath, isCSharp, HasTrigramIndex: false, expectedHash),
                new HashSet<string>(StringComparer.Ordinal));
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            if (expectedFiles is not null)
            {
                var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"檔案已在索引後變更：{relativePath}；請重新索引。");
            }

            var content = DecodeText(bytes);
            var trigrams = GetTrigrams(content.ToUpperInvariant())
                .ToHashSet(StringComparer.Ordinal);
            return new IndexedFileBuildResult(
                new IndexedFile(fullPath, isCSharp, HasTrigramIndex: true, expectedHash),
                trigrams);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (expectedFiles is not null)
                throw CreateSnapshotFileUnavailableException(rootPath, fullPath, exception);
            return new IndexedFileBuildResult(
                new IndexedFile(fullPath, isCSharp, HasTrigramIndex: false, expectedHash),
                new HashSet<string>(StringComparer.Ordinal));
        }
    }

    private static InvalidOperationException CreateSnapshotFileUnavailableException(
        string rootPath,
        string fullPath,
        Exception? innerException = null)
    {
        var relativePath = Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
        return new InvalidOperationException(
            $"圖譜版本的檔案已刪除或無法讀取：{relativePath}；請重新索引。",
            innerException);
    }

    /// <summary>從實體檔案讀取文字，並在固定 Graph 版本下先驗證內容雜湊。</summary>
    private static async Task<string> ReadVerifiedTextAsync(
        IndexedFile file,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            file.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await VerifyStreamHashAsync(file, stream, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyStreamHashAsync(
        IndexedFile file,
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (file.ExpectedHash is not null)
        {
            var actualHash = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!actualHash.Equals(file.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"檔案已在索引後變更：{file.FullPath}；請重新索引。");
            }
        }
        stream.Position = 0;
    }

    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>將檔案清單壓成固定長度識別碼，避免把數千個路徑與雜湊長期保留成大字串。</summary>
    private static string BuildSnapshotIdentity(
        IReadOnlyDictionary<string, string>? expectedFiles)
    {
        if (expectedFiles is null)
            return "live";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in expectedFiles.OrderBy(
                     pair => pair.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(pair.Key));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(pair.Value));
            hash.AppendData([10]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool IsInsideRoot(string rootPath, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(candidatePath);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(
                   root + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateSearchableFiles(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            yield break;
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var child in directories)
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(child)))
                    pending.Push(child);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                if (SearchableExtensions.Contains(Path.GetExtension(file)))
                    yield return Path.GetFullPath(file);
            }
        }
    }

    private static IEnumerable<string> GetTrigrams(string value)
    {
        if (value.Length < TrigramLength)
            yield break;
        for (var index = 0; index <= value.Length - TrigramLength; index++)
            yield return value.Substring(index, TrigramLength);
    }

    private static string ClassifyIdentifier(SyntaxToken token) => token.Parent switch
    {
        BaseTypeDeclarationSyntax declaration when declaration.Identifier == token => "type-definition",
        MethodDeclarationSyntax declaration when declaration.Identifier == token => "method-definition",
        ConstructorDeclarationSyntax declaration when declaration.Identifier == token => "constructor-definition",
        PropertyDeclarationSyntax declaration when declaration.Identifier == token => "property-definition",
        EventDeclarationSyntax declaration when declaration.Identifier == token => "event-definition",
        VariableDeclaratorSyntax declaration when declaration.Identifier == token => "variable-definition",
        ParameterSyntax declaration when declaration.Identifier == token => "parameter-definition",
        EnumMemberDeclarationSyntax declaration when declaration.Identifier == token => "enum-member-definition",
        _ => "reference",
    };

    private static string? GetContainerName(SyntaxNode? node)
    {
        var names = node?.AncestorsAndSelf()
            .Where(item => item is BaseTypeDeclarationSyntax or MethodDeclarationSyntax)
            .Select(item => item switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                _ => string.Empty,
            })
            .Where(value => value.Length > 0)
            .Reverse()
            .ToList();
        return names is { Count: > 0 } ? string.Join('.', names) : null;
    }

    private static bool QualifiedNameMatches(string requested, string? container, string identifier)
    {
        var candidate = string.IsNullOrWhiteSpace(container)
            ? identifier
            : container.EndsWith(identifier, StringComparison.Ordinal)
                ? container
                : $"{container}.{identifier}";
        return candidate.EndsWith(requested, StringComparison.Ordinal);
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";

    private sealed record IndexedFile(
        string FullPath,
        bool IsCSharp,
        bool HasTrigramIndex,
        string? ExpectedHash)
    {
        public bool IsIndexed => HasTrigramIndex;
    }

    /// <summary>單一檔案建立完成後的局部結果；由主執行緒確定性合併。</summary>
    private sealed record IndexedFileBuildResult(
        IndexedFile File,
        IReadOnlySet<string> Trigrams);

    /// <summary>候選檔案的逐行確認結果。</summary>
    private sealed record TextFileScanResult(
        IReadOnlyList<TextMatch> Matches,
        bool Skipped);

    private sealed record SymbolOccurrence(
        string FilePath,
        int Line,
        int Column,
        string Classification,
        string? Container,
        string Preview);

    private sealed record SourceIndexCacheKey(string RootPath, string SnapshotIdentity)
    {
        public bool Equals(SourceIndexCacheKey? other) =>
            other is not null &&
            RootPath.Equals(other.RootPath, StringComparison.OrdinalIgnoreCase) &&
            SnapshotIdentity.Equals(other.SnapshotIdentity, StringComparison.Ordinal);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(RootPath),
            StringComparer.Ordinal.GetHashCode(SnapshotIdentity));
    }

    private sealed class SourceIndexCacheEntry : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();

        public SourceIndexCacheEntry(
            long sequence,
            Func<CancellationToken, Task<ProjectSourceIndex>> factory)
        {
            Sequence = sequence;
            Build = new Lazy<Task<ProjectSourceIndex>>(
                () => factory(_lifetime.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public long Sequence { get; }

        public Lazy<Task<ProjectSourceIndex>> Build { get; }

        public void Dispose()
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }
}
