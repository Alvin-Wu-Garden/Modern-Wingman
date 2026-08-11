using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// 專案原始碼的共用唯讀索引。
///
/// 索引在同一個專案根目錄的多次工具呼叫之間共用：文字搜尋使用三字元倒排索引
/// 先縮小候選檔案，C# 符號搜尋則重用每個檔案建立過的 Roslyn 語法資料，避免每次
/// Agent 呼叫都重新讀取全部檔案及逐行掃描。索引有記憶體上限，且可由檔案 watcher
/// 透過 <see cref="InvalidateForPath"/> 失效。
/// </summary>
internal sealed class ProjectSourceIndex
{
    private const int MaximumSearchFileBytes = 4 * 1024 * 1024;
    private const long MaximumIndexedSourceBytes = 64 * 1024 * 1024;
    private const int TrigramLength = 3;
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromSeconds(30);
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
    private static readonly ConcurrentDictionary<string, Lazy<Task<ProjectSourceIndex>>> Indexes = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<IndexedFile> _files;
    private readonly IReadOnlyDictionary<string, int> _fileNumbers;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<int>> _trigramPostings;
    private readonly object _symbolGate = new();
    private Task _symbolIndexTask = Task.CompletedTask;
    private bool _symbolIndexStarted;
    private Dictionary<string, IReadOnlyList<SymbolOccurrence>>? _symbols;

    private ProjectSourceIndex(
        IReadOnlyList<IndexedFile> files,
        IReadOnlyDictionary<string, int> fileNumbers,
        IReadOnlyDictionary<string, IReadOnlyList<int>> trigramPostings)
    {
        _files = files;
        _fileNumbers = fileNumbers;
        _trigramPostings = trigramPostings;
    }

    /// <summary>目前索引包含的檔案數；超過大小限制而未載入的檔案也會列在清單中。</summary>
    public int FileCount => _files.Count;

    /// <summary>目前實際載入內容的檔案數。</summary>
    public int IndexedFileCount => _files.Count(file => file.Content is not null);

    /// <summary>目前索引建立時間，供 watcher 不可用時的安全生命週期判斷。</summary>
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>
    /// 取得單一專案的共享索引。相同 root 的併發請求只會建立一次索引工作。
    /// </summary>
    public static async Task<ProjectSourceIndex> GetOrCreateAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        while (Indexes.TryGetValue(rootPath, out var existing))
        {
            ProjectSourceIndex index;
            try
            {
                index = await existing.Value.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 建置失敗不可把 faulted Lazy 永久留在全域快取；下一次工具呼叫
                // 應能重新建立索引，而不是每次都重播同一個例外。
                Indexes.TryRemove(new KeyValuePair<string, Lazy<Task<ProjectSourceIndex>>>(
                    rootPath,
                    existing));
                throw;
            }
            if (DateTime.UtcNow - index.CreatedAtUtc <= IndexLifetime)
                return index;
            Indexes.TryRemove(new KeyValuePair<string, Lazy<Task<ProjectSourceIndex>>>(
                rootPath,
                existing));
        }

        var created = new Lazy<Task<ProjectSourceIndex>>(
            () => BuildAsync(rootPath),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var selected = Indexes.GetOrAdd(rootPath, created);
        ProjectSourceIndex result;
        try
        {
            result = await selected.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            Indexes.TryRemove(new KeyValuePair<string, Lazy<Task<ProjectSourceIndex>>>(
                rootPath,
                selected));
            throw;
        }
        if (DateTime.UtcNow - result.CreatedAtUtc > IndexLifetime)
        {
            Indexes.TryRemove(new KeyValuePair<string, Lazy<Task<ProjectSourceIndex>>>(
                rootPath,
                selected));
            return await GetOrCreateAsync(rootPath, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>
    /// 只取得已完成且仍有效的索引，不觸發全專案建立工作。
    /// 讀取單一檔案區段時優先使用此方法，避免一次讀入整個專案。
    /// </summary>
    public static async Task<ProjectSourceIndex?> GetExistingAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Indexes.TryGetValue(rootPath, out var lazy) || !lazy.IsValueCreated)
            return null;
        var task = lazy.Value;
        if (!task.IsCompletedSuccessfully)
            return null;
        var value = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (DateTime.UtcNow - value.CreatedAtUtc > IndexLifetime)
        {
            Indexes.TryRemove(new KeyValuePair<string, Lazy<Task<ProjectSourceIndex>>>(
                rootPath,
                lazy));
            return null;
        }
        return value;
    }

    /// <summary>
    /// 由檔案 watcher 通知索引失效。整個 root 一次移除可避免只更新一個檔案時留下
    /// 部分舊倒排索引；下一次工具呼叫會建立一致的新快照。
    /// </summary>
    public static void InvalidateForPath(string changedPath)
    {
        if (string.IsNullOrWhiteSpace(changedPath))
            return;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(changedPath);
        }
        catch (ArgumentException)
        {
            // watcher 的異常路徑不應中斷檔案索引或對話回合。
            return;
        }
        foreach (var pair in Indexes)
        {
            if (!IsInsideRoot(pair.Key, fullPath))
                continue;
            Indexes.TryRemove(new KeyValuePair<string, Lazy<Task<ProjectSourceIndex>>>(
                pair.Key,
                pair.Value));
        }
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

    /// <summary>從索引讀取檔案區段；檔案不存在或尚未被索引時回傳 null。</summary>
    public sealed record FileRangeResult(
        IReadOnlyList<FileLine> Lines,
        bool HasMore);

    /// <summary>一基行號的檔案內容。</summary>
    public sealed record FileLine(int Line, string Text);

    /// <summary>
    /// 搜尋專案文字。長查詢會以三字元倒排索引縮小檔案候選，只有候選檔案需要逐行比對；
    /// 小於三字元的查詢仍會受有界索引檔案清單限制，並回報被大小上限略過的檔案數。
    /// </summary>
    public TextSearchResult SearchText(
        string query,
        string? extension,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query.Trim();
        var candidates = FindTextCandidates(normalizedQuery);
        var matches = new List<TextMatch>(Math.Min(maximumResults, 100));
        var filesScanned = 0;
        foreach (var fileNumber in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = _files[fileNumber];
            if (extension is not null &&
                !Path.GetExtension(file.FullPath).Equals(extension, StringComparison.OrdinalIgnoreCase))
                continue;
            if (file.Content is null)
                continue;

            filesScanned++;
            for (var index = 0; index < file.Lines.Length; index++)
            {
                var line = file.Lines[index];
                var column = line.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase);
                if (column < 0)
                    continue;
                matches.Add(new TextMatch(
                    file.FullPath,
                    index + 1,
                    column + 1,
                    Truncate(line.Trim(), 500)));
                if (matches.Count >= maximumResults)
                {
                    return new TextSearchResult(
                        matches,
                        filesScanned,
                        _files.Count(candidate => candidate.Content is null),
                        true);
                }
            }
        }

        return new TextSearchResult(
            matches,
            filesScanned,
            _files.Count(candidate => candidate.Content is null),
            false);
    }

    /// <summary>
    /// 由索引讀取檔案區段。已建立內容快取時不再從第一行讀到 startLine，
    /// 讓讀取大型檔案的高行號區段保持有界且快速。
    /// </summary>
    public FileRangeResult? ReadFileRange(
        string fullPath,
        int startLine,
        int lineCount)
    {
        if (!_fileNumbers.TryGetValue(fullPath, out var fileNumber))
            return null;
        var file = _files[fileNumber];
        if (file.Content is null)
            return null;
        try
        {
            var info = new FileInfo(fullPath);
            // watcher 可能遺漏極短暫的儲存事件；讀檔時再做一次輕量 metadata
            // 驗證，避免把舊 snapshot 的原始碼行號回傳給模型。
            if (!info.Exists ||
                info.Length != file.ByteLength ||
                info.LastWriteTimeUtc != file.LastWriteUtc)
                return null;
        }
        catch (IOException)
        {
            return null;
        }

        var lines = file.Lines;
        if (startLine > lines.Length)
            return new FileRangeResult([], false);
        var first = startLine - 1;
        var count = Math.Min(lineCount, lines.Length - first);
        var result = new FileLine[count];
        for (var index = 0; index < count; index++)
            result[index] = new FileLine(startLine + index, lines[first + index]);
        return new FileRangeResult(result, first + count < lines.Length);
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
                _files.Count(file => file.IsCSharp && file.Content is not null),
                false);
        }

        var matches = new List<SymbolMatch>(Math.Min(maximumResults, occurrences.Count));
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
            if (matches.Count >= maximumResults)
                break;
        }

        return new SymbolSearchResult(
            matches,
            _files.Count(file => file.IsCSharp && file.Content is not null),
            matches.Count >= maximumResults);
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
                return [];
            candidates ??= new HashSet<int>(postings);
            if (candidates.Count == 0)
                return [];
            candidates.IntersectWith(postings);
        }
        return candidates?.OrderBy(value => value).ToArray() ?? [];
    }

    private async Task EnsureSymbolIndexAsync(CancellationToken cancellationToken)
    {
        Task build;
        lock (_symbolGate)
        {
            if (!_symbolIndexStarted)
            {
                _symbolIndexStarted = true;
                _symbolIndexTask = BuildSymbolIndexAsync();
            }
            build = _symbolIndexTask;
        }
        await build.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task BuildSymbolIndexAsync() => Task.Run(() =>
    {
        var symbols = new Dictionary<string, List<SymbolOccurrence>>(StringComparer.Ordinal);
        foreach (var file in _files.Where(file => file.IsCSharp && file.Content is not null))
        {
            var tree = CSharpSyntaxTree.ParseText(file.Content!);
            var root = tree.GetRoot();
            file.SyntaxTree = tree;
            foreach (var token in root.DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.IdentifierToken))
                    continue;
                var occurrence = new SymbolOccurrence(
                    file.FullPath,
                    tree.GetLineSpan(token.Span).StartLinePosition.Line + 1,
                    tree.GetLineSpan(token.Span).StartLinePosition.Character + 1,
                    ClassifyIdentifier(token),
                    GetContainerName(token.Parent),
                    Truncate(token.Parent?.Parent?.ToString()
                        .ReplaceLineEndings(" ").Trim() ?? token.ValueText, 500));
                if (!symbols.TryGetValue(token.ValueText, out var list))
                {
                    list = [];
                    symbols[token.ValueText] = list;
                }
                list.Add(occurrence);
            }
        }

        lock (_symbolGate)
        {
            _symbols = symbols.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<SymbolOccurrence>)pair.Value,
                StringComparer.Ordinal);
        }
    });

    private static async Task<ProjectSourceIndex> BuildAsync(string rootPath)
    {
        var files = new List<IndexedFile>();
        var fileNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var postings = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        long indexedBytes = 0;
        foreach (var fullPath in EnumerateSearchableFiles(rootPath))
        {
            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (!info.Exists)
                    continue;
            }
            catch (IOException)
            {
                continue;
            }

            var isCSharp = Path.GetExtension(fullPath).Equals(".cs", StringComparison.OrdinalIgnoreCase);
            string? content = null;
            ImmutableArray<string> lines = [];
            if (info.Length <= MaximumSearchFileBytes &&
                indexedBytes + info.Length <= MaximumIndexedSourceBytes)
            {
                try
                {
                    content = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
                    lines = SplitLines(content);
                    indexedBytes += Encoding.UTF8.GetByteCount(content);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    content = null;
                }
            }

            var file = new IndexedFile(
                fullPath,
                info.LastWriteTimeUtc,
                info.Length,
                isCSharp,
                content,
                lines);
            fileNumbers[fullPath] = files.Count;
            files.Add(file);
            if (content is null)
                continue;
            var fileNumber = files.Count - 1;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var trigram in GetTrigrams(content.ToUpperInvariant()))
            {
                if (!seen.Add(trigram))
                    continue;
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
            fileNumbers,
            postings.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<int>)pair.Value.ToArray(),
                StringComparer.Ordinal));
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

    private static ImmutableArray<string> SplitLines(string content)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
            builder.Add(line);
        return builder.ToImmutable();
    }

    private static IEnumerable<string> GetTrigrams(string value)
    {
        if (value.Length < TrigramLength)
            yield break;
        for (var index = 0; index <= value.Length - TrigramLength; index++)
            yield return value.Substring(index, TrigramLength);
    }

    private static bool IsInsideRoot(string rootPath, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(candidatePath);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
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

    private sealed class IndexedFile(
        string fullPath,
        DateTime lastWriteUtc,
        long byteLength,
        bool isCSharp,
        string? content,
        ImmutableArray<string> lines)
    {
        public string FullPath { get; } = fullPath;
        public DateTime LastWriteUtc { get; } = lastWriteUtc;
        public long ByteLength { get; } = byteLength;
        public bool IsCSharp { get; } = isCSharp;
        public string? Content { get; } = content;
        public ImmutableArray<string> Lines { get; } = lines;
        public SyntaxTree? SyntaxTree { get; set; }
    }

    private sealed record SymbolOccurrence(
        string FilePath,
        int Line,
        int Column,
        string Classification,
        string? Container,
        string Preview);
}
