using AgentService.Modules.GraphRAG;

namespace AgentService.UnitTests;

/// <summary>驗證 Repository 問題規劃、source evidence 安全邊界與 context 編譯結果。</summary>
public sealed class GraphAnswerContextTests
{
    /// <summary>五種固定問題意圖都應選到對應方向，且業務詞會展開成技術別名。</summary>
    [Theory]
    [InlineData("補登債券交易在哪裡實作？", RepositoryQuestionIntent.LocateFeature,
        RepositoryTraversalDirection.Outgoing)]
    [InlineData("補登交易存檔流程怎麼走？", RepositoryQuestionIntent.ExplainFlow,
        RepositoryTraversalDirection.Outgoing)]
    [InlineData("修改交割日會影響哪些地方？", RepositoryQuestionIntent.AnalyzeImpact,
        RepositoryTraversalDirection.Both)]
    [InlineData("BondTrade 資料表被誰讀取？", RepositoryQuestionIntent.FindDataUsage,
        RepositoryTraversalDirection.Incoming)]
    [InlineData("整個系統有哪些模組？", RepositoryQuestionIntent.SystemOverview,
        RepositoryTraversalDirection.Outgoing)]
    public void Planner_ClassifiesFiveIntentsAndSelectsBoundedDirection(
        string question,
        RepositoryQuestionIntent expectedIntent,
        RepositoryTraversalDirection expectedDirection)
    {
        var plan = new RepositoryQuestionPlanner().Plan(question);

        Assert.Equal(expectedIntent, plan.Intent);
        Assert.Equal(expectedDirection, plan.Direction);
        Assert.InRange(plan.MaximumDepth, 1, 3);
        Assert.InRange(plan.SearchTerms.Count, 1, 12);
    }

    /// <summary>交割日與補登應展開常見英文識別碼，讓 Graph 搜尋不依賴中文命名。</summary>
    [Fact]
    public void Planner_ExpandsInvestmentAliasesWithoutLlm()
    {
        var plan = new RepositoryQuestionPlanner()
            .Plan("修改債券補登交易的交割日會影響哪些地方？");

        Assert.Contains("SettlementDate", plan.SearchTerms);
        Assert.Contains("SettleDate", plan.SearchTerms);
        Assert.Contains("BackEntry", plan.SearchTerms);
        Assert.Contains("Bond", plan.SearchTerms);
        Assert.True(plan.SearchTerms.Count <= 12);
    }

    /// <summary>登入流程必須展開到真正的驗證入口，不能只命中登入資訊管理畫面。</summary>
    [Fact]
    public void Planner_ExpandsLoginFlowToAuthenticationSymbols()
    {
        var plan = new RepositoryQuestionPlanner().Plan("登入流程是什麼？");

        Assert.Equal(RepositoryQuestionIntent.ExplainFlow, plan.Intent);
        Assert.Contains("AccountController", plan.SearchTerms);
        Assert.Contains("LoginAndPasswordProcess", plan.SearchTerms);
        Assert.Contains("ProcessLogin", plan.SearchTerms);
        Assert.Contains("LoginUtility", plan.SearchTerms);
    }

    /// <summary>跨整個系統的批次報表問題應走 Community overview，而不是誤走局部程式搜尋。</summary>
    [Fact]
    public void Planner_SystemBatchReportQuestion_UsesSystemOverview()
    {
        var plan = new RepositoryQuestionPlanner().Plan("整個系統有哪些批次與報表流程？");

        Assert.Equal(RepositoryQuestionIntent.SystemOverview, plan.Intent);
    }

    /// <summary>流程 coverage 缺少入口時只允許補查一次，第二次仍不足必須停止。</summary>
    [Fact]
    public void Planner_FallbackRunsAtMostOnceAndReportsMissingEvidence()
    {
        var planner = new RepositoryQuestionPlanner();
        var plan = planner.Plan("補登交易存檔流程怎麼走？");
        var coverage = new RepositoryRetrievalCoverage(
            HasFeature: true,
            HasEntryPoint: false,
            HasCode: true,
            HasData: false,
            HasRelationship: true);

        var first = planner.PlanFallback(plan, coverage, ["BondTradeService.Save"], 0);
        var second = planner.PlanFallback(plan, coverage, ["BondTradeService.Save"], 1);

        Assert.True(first.ShouldRun);
        Assert.Equal(1, first.Attempt);
        Assert.Contains("BondTradeService.Save", first.SearchTerms);
        Assert.Contains("EntryPoint", first.MissingEvidence);
        Assert.False(second.ShouldRun);
        Assert.Equal(1, second.Attempt);
    }

    /// <summary>C# evidence 應展開到包含 member，但即使 member 很長也不得超過 120 行。</summary>
    [Fact]
    public async Task SourceReader_ExpandsContainingMemberAndCapsAtOneHundredTwentyLines()
    {
        using var directory = new TemporaryDirectory();
        var body = string.Join(Environment.NewLine,
            Enumerable.Range(1, 160).Select(index => $"        var value{index} = {index};"));
        var source = $$"""
            namespace Demo;
            public sealed class BondTradeService
            {
                public void Save()
                {
            {{body}}
                }
            }
            """;
        var path = Path.Combine(directory.Path, "BondTradeService.cs");
        await File.WriteAllTextAsync(path, source);
        var reader = new SourceEvidenceReader(directory.Path);

        var result = await reader.ReadAsync(
        [
            Candidate("BondTradeService.cs", line: 80, score: 10, "BondTradeService.Save"),
        ], RepositoryQuestionIntent.ExplainFlow, "manifest-1");

        var snippet = Assert.Single(result.Snippets);
        Assert.True(snippet.EndLine - snippet.StartLine + 1 <= 120);
        Assert.Contains("value", snippet.Content);
        Assert.Contains("BondTradeService.Save", snippet.Symbol);
    }

    /// <summary>型別行號搭配精確方法名稱時，必須直接讀取該方法而非類別開頭。</summary>
    [Fact]
    public async Task SourceReader_UsesNamedMethodWhenTypeEvidenceStartsAtClass()
    {
        using var directory = new TemporaryDirectory();
        var source = """
            public sealed class LoginAndPasswordProcess
            {
                public void ConstructorArea()
                {
                    var marker = "class beginning";
                }

                public bool ProcessLogin(string userName, string password)
                {
                    var validated = LoginValidation(userName, password);
                    return validated;
                }

                private bool LoginValidation(string userName, string password) => true;
            }
            """;
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "LoginAndPassword.cs"),
            source);

        var result = await new SourceEvidenceReader(directory.Path).ReadAsync(
        [
            Candidate(
                "LoginAndPassword.cs",
                line: 1,
                score: 10,
                "ProcessLogin",
                endLine: 15),
        ], RepositoryQuestionIntent.ExplainFlow, "manifest-login");

        var snippet = Assert.Single(result.Snippets);
        Assert.Equal("ProcessLogin", snippet.Symbol);
        Assert.Contains("LoginValidation", snippet.Content);
        Assert.DoesNotContain("class beginning", snippet.Content);
    }

    /// <summary>同一檔案中互相重疊的 evidence 範圍應合併，避免重複消耗 LLM context。</summary>
    [Fact]
    public async Task SourceReader_MergesOverlappingRanges()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "query.sql");
        await File.WriteAllLinesAsync(path,
            Enumerable.Range(1, 100).Select(index => $"SELECT {index};"));
        var reader = new SourceEvidenceReader(directory.Path);

        var result = await reader.ReadAsync(
        [
            Candidate("query.sql", 30, 10, "usp_SaveBondTrade", endLine: 40),
            Candidate("query.sql", 35, 9, "usp_SaveBondTrade", endLine: 45),
        ], RepositoryQuestionIntent.FindDataUsage, "manifest-1");

        Assert.Single(result.Snippets);
        Assert.True(result.Snippets[0].StartLine <= 22);
        Assert.True(result.Snippets[0].EndLine >= 53);
    }

    /// <summary>根目錄外、generated output 與非文字副檔名都必須被拒絕且留下診斷。</summary>
    [Fact]
    public async Task SourceReader_RejectsUnsafeAndGeneratedArtifacts()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "obj"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "obj", "Generated.cs"), "class A {}");
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "secret.dll"), [1, 2, 3]);
        var reader = new SourceEvidenceReader(directory.Path);

        var result = await reader.ReadAsync(
        [
            Candidate("../outside.cs", 1, 10),
            Candidate("obj/Generated.cs", 1, 9),
            Candidate("secret.dll", 1, 8),
        ], RepositoryQuestionIntent.ExplainFlow, "manifest-1");

        Assert.Empty(result.Snippets);
        Assert.Contains(result.Diagnostics, value => value.Contains("根目錄外", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, value => value.Contains("generated", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, value => value.Contains("副檔名", StringComparison.Ordinal));
    }

    /// <summary>專案內的 symbolic link 或 junction 不得繞過 root boundary 讀取外部檔案。</summary>
    [Fact]
    public async Task SourceReader_RejectsReparsePointPath()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(outside.Path, "Outside.cs"),
            "public sealed class Outside {}");
        var link = Path.Combine(directory.Path, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside.Path);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // 執行環境禁止建立 link 時，production 的 attribute 檢查仍由 Windows 測試機驗證。
        }

        var result = await new SourceEvidenceReader(directory.Path).ReadAsync(
        [
            Candidate("linked/Outside.cs", 1, 10),
        ], RepositoryQuestionIntent.ExplainFlow, "manifest-1");

        Assert.Empty(result.Snippets);
        Assert.Contains(result.Diagnostics,
            value => value.Contains("reparse-point", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>即使副檔名在白名單，包含 NUL 的內容仍應視為 binary 並拒絕。</summary>
    [Fact]
    public async Task SourceReader_RejectsBinaryContentWithAllowedExtension()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "fake.cs"),
            [65, 0, 66]);

        var result = await new SourceEvidenceReader(directory.Path).ReadAsync(
        [
            Candidate("fake.cs", 1, 10),
        ], RepositoryQuestionIntent.ExplainFlow, "manifest-1");

        Assert.Empty(result.Snippets);
        Assert.Contains(result.Diagnostics,
            value => value.Contains("binary", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>來源片段總量必須受 20K budget 限制，超限候選以診斷呈現而非塞入 Prompt。</summary>
    [Fact]
    public async Task SourceReader_RespectsTwentyThousandCharacterBudget()
    {
        using var directory = new TemporaryDirectory();
        var candidates = new List<SourceEvidenceCandidate>();
        for (var index = 0; index < 10; index++)
        {
            var name = $"source{index}.sql";
            await File.WriteAllLinesAsync(
                Path.Combine(directory.Path, name),
                Enumerable.Range(1, 80).Select(line => $"SELECT '{new string('x', 80)}'; -- {line}"));
            candidates.Add(Candidate(name, 20, 100 - index, endLine: 75));
        }

        var result = await new SourceEvidenceReader(directory.Path).ReadAsync(
            candidates, RepositoryQuestionIntent.ExplainFlow, "manifest-1");

        Assert.True(result.Snippets.Sum(snippet => snippet.Content.Length) <= 20_000);
        Assert.True(result.WasTruncated);
        Assert.Contains(result.Diagnostics,
            value => value.Contains("20,000", StringComparison.Ordinal));
    }

    /// <summary>ExplainFlow 即使 Code 分數較高，也必須保留存在的 EntryPoint、Code、Data 各一份。</summary>
    [Fact]
    public async Task SourceReader_ExplainFlowPreservesLayerCoverageBeforeScoreOnlyResults()
    {
        using var directory = new TemporaryDirectory();
        var candidates = new List<SourceEvidenceCandidate>();
        for (var index = 0; index < 10; index++)
        {
            var name = $"HighScoreCode{index}.cs";
            await File.WriteAllTextAsync(
                Path.Combine(directory.Path, name),
                $"public sealed class HighScoreCode{index} {{ }}");
            candidates.Add(Candidate(name, 1, 100 - index, kind: GraphNodeKind.Code));
        }
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "BondTrade.aspx"), "<html />");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "BondTrade.sql"), "SELECT 1;");
        candidates.Add(Candidate(
            "BondTrade.aspx", 1, 2, kind: GraphNodeKind.EntryPoint));
        candidates.Add(Candidate(
            "BondTrade.sql", 1, 1, kind: GraphNodeKind.Data));

        var result = await new SourceEvidenceReader(directory.Path).ReadAsync(
            candidates, RepositoryQuestionIntent.ExplainFlow, "manifest-coverage");

        Assert.Contains(result.Snippets, value => value.NodeKind == GraphNodeKind.EntryPoint);
        Assert.Contains(result.Snippets, value => value.NodeKind == GraphNodeKind.Code);
        Assert.Contains(result.Snippets, value => value.NodeKind == GraphNodeKind.Data);
        Assert.True(result.Snippets.Count <= 10);
    }

    /// <summary>Cache 身分必須同時區分 manifest、相對路徑、內容 SHA-256 與實際範圍。</summary>
    [Fact]
    public void SourceReader_CacheKeyIncludesAllRequiredIdentityParts()
    {
        var baseline = SourceEvidenceReader.BuildCacheKey(
            "manifest-a", "src/Bond.cs", "HASH-A", 10, 20);

        Assert.NotEqual(baseline, SourceEvidenceReader.BuildCacheKey(
            "manifest-b", "src/Bond.cs", "HASH-A", 10, 20));
        Assert.NotEqual(baseline, SourceEvidenceReader.BuildCacheKey(
            "manifest-a", "src/Stock.cs", "HASH-A", 10, 20));
        Assert.NotEqual(baseline, SourceEvidenceReader.BuildCacheKey(
            "manifest-a", "src/Bond.cs", "HASH-B", 10, 20));
        Assert.NotEqual(baseline, SourceEvidenceReader.BuildCacheKey(
            "manifest-a", "src/Bond.cs", "HASH-A", 11, 20));
    }

    /// <summary>內容即使長度與 mtime 相同，只要 bytes 改變就不得命中舊 snippet cache。</summary>
    [Fact]
    public async Task SourceReader_ContentHashInvalidatesSameMetadataCacheEntry()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "Rule.sql");
        var timestamp = DateTime.UtcNow.AddMinutes(-5);
        await File.WriteAllTextAsync(path, "SELECT 'ALPHA';");
        File.SetLastWriteTimeUtc(path, timestamp);
        var reader = new SourceEvidenceReader(directory.Path);
        var candidate = Candidate("Rule.sql", 1, 10, kind: GraphNodeKind.Data);
        var first = await reader.ReadAsync(
            [candidate], RepositoryQuestionIntent.FindDataUsage, "manifest-a");

        await File.WriteAllTextAsync(path, "SELECT 'OMEGA';");
        File.SetLastWriteTimeUtc(path, timestamp);
        var second = await reader.ReadAsync(
            [candidate], RepositoryQuestionIntent.FindDataUsage, "manifest-a");

        Assert.Contains("ALPHA", Assert.Single(first.Snippets).Content);
        Assert.Contains("OMEGA", Assert.Single(second.Snippets).Content);
        Assert.DoesNotContain("ALPHA", second.Snippets[0].Content);
    }

    /// <summary>Graph 沒有 seed 時，唯一一次全文補查應回傳有界片段且不建立 Graph 關係。</summary>
    [Fact]
    public async Task SourceReader_GraphMissFallbackReturnsBoundedTextCandidate()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "BondTradeService.cs"),
            "public class BondTradeService\n{\n    // 交割日重算現金流\n    public void Save() { }\n}\n");
        var reader = new SourceEvidenceReader(directory.Path);

        var result = await reader.SearchFallbackAsync(["交割日", "SettlementDate"]);

        var snippet = Assert.Single(result.Snippets);
        Assert.Equal("BondTradeService.cs", snippet.RelativePath);
        Assert.Contains("交割日", snippet.Content);
        Assert.Contains(result.Diagnostics,
            value => value.Contains("唯一一次", StringComparison.Ordinal));
    }

    /// <summary>Context 應固定分區 facts、inference、source 與 unknown，且不超過指定 budget。</summary>
    [Fact]
    public void ContextCompiler_SeparatesEvidenceClassesAndRespectsBudget()
    {
        var feature = Node(
            "feature:menu:1", GraphNodeKind.Feature, GraphRoles.MenuFeature,
            "債券補登交易", GraphConfidence.Exact);
        var code = Node(
            "code:csharp:demo.bondtradeservice", GraphNodeKind.Code,
            GraphRoles.BusinessService, "BondTradeService", GraphConfidence.Heuristic);
        var edge = new GraphEdge(
            "edge:1", feature.Id, GraphEdgeKind.RoutesTo, code.Id,
            [Evidence("mapping.sql", GraphConfidence.Exact)]);
        var graph = new GraphRetrievalContext(
            "補登交易",
            [new ScoredGraphNode(feature, 10, 0, true), new ScoredGraphNode(code, 8, 1, false)],
            [edge],
            [new GraphCommunityReport("community:1", "primary", "摘要", "不可當 source fact", [])],
            ["Reflection 尚未索引"]);
        var source = new SourceEvidenceReadResult(
        [
            new SourceEvidenceSnippet(
                "BondTradeService.cs", 10, 12, "BondTradeService.Save",
                "   10 | public void Save() {}", 10, GraphNodeKind.Code),
        ], [], false);

        var context = new GraphContextCompiler().Compile(
            new RepositoryQuestionPlanner().Plan("補登交易存檔流程怎麼走？"),
            graph,
            source,
            maximumCharacters: 4_000);

        Assert.Contains("# Canonical Graph 事實", context);
        Assert.Contains("# 推論", context);
        Assert.Contains("# 已讀取的原始碼證據", context);
        Assert.Contains("BondTradeService.cs:10-12", context);
        Assert.Contains("Reflection 尚未索引", context);
        Assert.DoesNotContain("不可當 source fact", context);
        Assert.True(context.Length <= 4_000);
    }

    /// <summary>Source 讀取失敗時，Context 必須明說未驗證，且不得使用「已確認事實」標題。</summary>
    [Fact]
    public void ContextCompiler_SourceFailureDoesNotClaimConfirmedProgramBehavior()
    {
        var feature = Node(
            "feature:menu:1", GraphNodeKind.Feature, GraphRoles.MenuFeature,
            "債券補登交易", GraphConfidence.Exact);
        var graph = new GraphRetrievalContext(
            "補登交易",
            [new ScoredGraphNode(feature, 10, 0, true)],
            [],
            [],
            []);
        var source = new SourceEvidenceReadResult(
            [], ["Evidence 檔案不存在。"], false);

        var context = new GraphContextCompiler().Compile(
            new RepositoryQuestionPlanner().Plan("補登交易在哪裡？"),
            graph,
            source,
            maximumCharacters: 4_000);

        Assert.Contains("未取得可讀 source", context);
        Assert.Contains("不代表 source 行為已驗證", context);
        Assert.DoesNotContain("# 已確認事實", context);
    }

    /// <summary>Context 超限時只能捨棄完整 section，不得產生半個 code fence 或硬切字串。</summary>
    [Fact]
    public void ContextCompiler_BudgetDropsWholeSectionsWithoutBreakingCodeFence()
    {
        var graph = new GraphRetrievalContext("流程", [], [], [], []);
        var snippets = Enumerable.Range(0, 4).Select(index =>
            new SourceEvidenceSnippet(
                $"Source{index}.cs", 1, 20, $"Source{index}.Run",
                new string((char)('A' + index), 900), 10 - index, GraphNodeKind.Code)).ToArray();

        var context = new GraphContextCompiler().Compile(
            new RepositoryQuestionPlanner().Plan("交易流程怎麼走？"),
            graph,
            new SourceEvidenceReadResult(snippets, [], false),
            maximumCharacters: 2_000);

        Assert.True(context.Length <= 2_000);
        Assert.Equal(0, CountOccurrences(context, "```") % 2);
        Assert.DoesNotContain("…（Context 已截斷）", context);
        Assert.Contains("# Context 截斷", context);
    }

    private static SourceEvidenceCandidate Candidate(
        string artifact,
        int line,
        double score,
        string? symbol = null,
        int? endLine = null,
        GraphNodeKind kind = GraphNodeKind.Code) =>
        new(
            new GraphEvidence(
                GraphEvidenceSource.Ast,
                GraphConfidence.Exact,
                artifact,
                "測試 evidence",
                line,
                endLine ?? line),
            score,
            kind,
            symbol);

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }
        return count;
    }

    private static GraphNode Node(
        string id,
        GraphNodeKind kind,
        string role,
        string name,
        GraphConfidence confidence) =>
        new(
            id,
            kind,
            role,
            name,
            name,
            kind == GraphNodeKind.Data ? "sql" : "csharp",
            null,
            "active",
            [],
            kind == GraphNodeKind.Data ? null : $"{name}.cs",
            1,
            10,
            new Dictionary<string, string>(),
            [Evidence($"{name}.cs", confidence)]);

    private static GraphEvidence Evidence(string artifact, GraphConfidence confidence) =>
        new(
            confidence is GraphConfidence.Exact or GraphConfidence.Resolved
                ? GraphEvidenceSource.Compiler
                : GraphEvidenceSource.Heuristic,
            confidence,
            artifact,
            confidence == GraphConfidence.Heuristic ? "命名規則候選" : "編譯器已確認",
            1,
            2);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"modern-wingman-graph-context-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
