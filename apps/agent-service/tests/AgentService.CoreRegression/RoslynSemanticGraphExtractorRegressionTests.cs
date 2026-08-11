using System.Text;
using AgentService.Application.Contracts;
using AgentService.Modules.GraphRAG;
using AgentService.Modules.GraphRAG.FblAuthority;
using Microsoft.Data.SqlClient;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace AgentService.CoreRegression;

/// <summary>驗證 ParallelExtractor 語意抽取演算法已成為 canonical GraphDocument 的一部分。</summary>
public sealed class RoslynSemanticGraphExtractorRegressionTests
{
    [Fact]
    public async Task ExtendAsync_MsBuild無法載入時仍應保留靜態多專案結構()
    {
        var root = CreateTestRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "Fallback.sln"),
                "invalid solution header\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Core\", \"Core\\Core.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\nEndProject\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App\", \"App\\App.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\nEndProject\n",
                Encoding.UTF8);
            await File.WriteAllTextAsync(
                Path.Combine(root, "Core", "Core.csproj"),
                "<Project><PropertyGroup><AssemblyName>Core</AssemblyName></PropertyGroup>" +
                "<ItemGroup><Compile Include=\"Core.cs\" /></ItemGroup></Project>",
                Encoding.UTF8);
            await File.WriteAllTextAsync(
                Path.Combine(root, "App", "App.csproj"),
                "<Project><PropertyGroup><AssemblyName>App</AssemblyName></PropertyGroup>" +
                "<ItemGroup><Compile Include=\"App.cs\" />" +
                "<ProjectReference Include=\"..\\Core\\Core.csproj\" /></ItemGroup></Project>",
                Encoding.UTF8);
            await File.WriteAllTextAsync(
                Path.Combine(root, "Core", "Core.cs"),
                "namespace Demo; public static class Helper { public static string Format() => string.Empty; }",
                Encoding.UTF8);
            await File.WriteAllTextAsync(
                Path.Combine(root, "App", "App.cs"),
                "namespace Demo; public class Runner { public string Run() => Helper.Format(); }",
                Encoding.UTF8);

            var result = await new RoslynSemanticGraphExtractor().ExtendAsync(EmptyDocument(root), root);
            var solution = Assert.Single(result.Document.Nodes, node => node.Kind == GraphNodeKind.Solution);
            Assert.Equal("repository-fallback", Text(solution, "semantic_mode"));
            Assert.Equal(2, result.Document.Nodes.Count(node => node.Kind == GraphNodeKind.Project));
            Assert.Contains(result.Document.Relationships, edge => edge.Kind == GraphRelationshipKind.ReferencesProject);
            Assert.Contains(result.Document.Relationships, edge => edge.Kind == GraphRelationshipKind.CallsMethod);
            Assert.Contains(result.Issues, issue => issue.ReasonCode == PreflightReasonCode.SemanticExtractionDegraded);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task ExtendSolutionAsync_應建立跨專案方法呼叫與完整型別語意圖()
    {
        var root = CreateTestRoot();
        try
        {
            using var fixture = CreateSolutionFixture(root);
            var source = EmptyDocument(root);
            var extractor = new RoslynSemanticGraphExtractor();

            var first = await extractor.ExtendSolutionAsync(source, fixture.Solution, root);
            var second = await extractor.ExtendSolutionAsync(source, fixture.Solution, root);
            var document = first.Document;

            Assert.Contains(document.Nodes, node => node.Kind == GraphNodeKind.Solution);
            Assert.Equal(2, document.Nodes.Count(node => node.Kind == GraphNodeKind.Project));
            Assert.Contains(document.Relationships, edge => edge.Kind == GraphRelationshipKind.ReferencesProject);
            Assert.Contains(document.Nodes, node => IsType(node, "Demo.Core.TradeRecord") && Text(node, "type_kind") == "record");
            Assert.Contains(document.Nodes, node => IsType(node, "Demo.Core.Price") && Text(node, "type_kind") == "struct");
            Assert.Contains(document.Nodes, node => IsType(node, "Demo.Core.TradeStatus") && Text(node, "type_kind") == "enum");
            Assert.Contains(document.Nodes, node => IsType(node, "Demo.App.TradeController.NestedWorker"));

            var tradeController = SingleType(document, "Demo.App.TradeController");
            var sourceFiles = Assert.IsAssignableFrom<IEnumerable<string>>(
                tradeController.Properties["source_files"]);
            Assert.Equal(2, sourceFiles.Count());

            var runMethod = SingleMethod(document, "Demo.App.TradeController", "Run");
            var overloadMethod = SingleMethod(document, "Demo.App.TradeController", "Overload");
            var settlementType = SingleType(document, "Demo.App.SettlementService");
            AssertEdge(document, GraphRelationshipKind.DerivesFrom, tradeController.Key);
            AssertEdge(document, GraphRelationshipKind.ImplementsType, tradeController.Key);
            AssertEdge(document, GraphRelationshipKind.OverridesMethod, runMethod.Key);
            AssertEdge(document, GraphRelationshipKind.ImplementsMethod, runMethod.Key);
            Assert.Contains(document.Relationships, edge =>
                edge.Kind == GraphRelationshipKind.Instantiates &&
                edge.SourceKey == runMethod.Key &&
                edge.TargetKey == settlementType.Key);

            var overloadCall = Assert.Single(document.Relationships, edge =>
                edge.Kind == GraphRelationshipKind.CallsMethod &&
                edge.SourceKey == overloadMethod.Key);
            var overloadTarget = document.Nodes.Single(node => node.Key == overloadCall.TargetKey);
            Assert.Equal("Process", Text(overloadTarget, "name"));
            Assert.Contains("string", Text(overloadTarget, "signature"), StringComparison.OrdinalIgnoreCase);

            var helper = SingleMethod(document, "Demo.Core.Helper", "Format");
            var helperCall = Assert.Single(document.Relationships, edge =>
                edge.Kind == GraphRelationshipKind.CallsMethod &&
                edge.SourceKey == runMethod.Key &&
                edge.TargetKey == helper.Key);
            Assert.Equal(2, Convert.ToInt32(helperCall.Properties["occurrence_count"]));
            Assert.Equal(2, Assert.IsAssignableFrom<IEnumerable<string>>(
                helperCall.Properties["locations"]).Count());

            Assert.Contains(document.Nodes, node =>
                node.Kind == GraphNodeKind.ExternalSymbol &&
                Text(node, "display_name").Contains("OpenRead", StringComparison.Ordinal));
            Assert.All(document.Nodes.Where(node => node.Kind == GraphNodeKind.CodeChunk), chunk =>
            {
                Assert.False(chunk.Properties.ContainsKey("text"));
                Assert.False(chunk.Properties.ContainsKey("source_text"));
                Assert.Equal(false, chunk.Properties["contains_source_text"]);
            });

            Assert.DoesNotContain(document.Nodes, node =>
                node.Key.Contains(root, StringComparison.OrdinalIgnoreCase) ||
                node.Properties.Values.OfType<string>().Any(value =>
                    value.Contains(root, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(
                first.Document.Nodes.Select(node => node.Key),
                second.Document.Nodes.Select(node => node.Key));
            Assert.Equal(
                first.Document.Relationships.Select(edge => edge.Id),
                second.Document.Relationships.Select(edge => edge.Id));

            var validation = new GraphDocumentValidator(new PreflightValidatorOptions
            {
                ExpectedCenterMenuCount = null,
                RequiredDatabaseName = null,
                RequiredProvider = null,
                RequireCompleteExtraction = true,
            }).Validate(document, first.Issues);
            Assert.False(validation.HasBlockingErrors, string.Join(
                Environment.NewLine,
                validation.Issues.Select(issue => issue.Message)));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void DatabaseMetadataGraphResolver_只應使用使用者選定資料庫的通用系統目錄快照()
    {
        var catalog = new DatabaseMetadataCatalog(
            "SqlServer",
            "ConfiguredInvestmentDb",
            [
                new DatabaseObjectCatalogItem("dbo", "Orders", DatabaseObjectKind.Table, "SqlServer", "ConfiguredInvestmentDb"),
                new DatabaseObjectCatalogItem("dbo", "Customers", DatabaseObjectKind.Table, "SqlServer", "ConfiguredInvestmentDb"),
                new DatabaseObjectCatalogItem("dbo", "vwOrder", DatabaseObjectKind.View, "SqlServer", "ConfiguredInvestmentDb"),
                new DatabaseObjectCatalogItem("dbo", "usp_SaveOrder", DatabaseObjectKind.StoredProcedure, "SqlServer", "ConfiguredInvestmentDb"),
            ],
            [
                new DatabaseColumnCatalogItem("dbo", "Orders", 1, "OrderId", "int", 4, 10, 0, false, true, false, true),
                new DatabaseColumnCatalogItem("dbo", "Orders", 2, "CustomerId", "int", 4, 10, 0, false, false, false, false),
                new DatabaseColumnCatalogItem("dbo", "Customers", 1, "CustomerId", "int", 4, 10, 0, false, true, false, true),
            ],
            [
                new DatabaseParameterCatalogItem("dbo", "usp_SaveOrder", 1, "@CustomerId", "int", 4, 10, 0, false, false),
            ],
            [
                new DatabaseForeignKeyCatalogItem(
                    "FK_Orders_Customers", "dbo", "Orders", "CustomerId",
                    "dbo", "Customers", "CustomerId", 1),
            ],
            [
                new DatabaseDependencyCatalogItem("dbo", "vwOrder", "dbo", "Orders"),
            ]);

        var result = new DatabaseMetadataGraphResolver().Resolve(
            new ExtractionResult(EmptyDocument("D:/source"), []),
            catalog);

        Assert.Single(result.Document.Nodes, node => node.Kind == GraphNodeKind.Database);
        Assert.Equal(3, result.Document.Nodes.Count(node => node.Kind == GraphNodeKind.DatabaseColumn));
        Assert.Single(result.Document.Nodes, node => node.Kind == GraphNodeKind.StoredProcedureParameter);
        Assert.Contains(result.Document.Relationships, edge => edge.Kind == GraphRelationshipKind.ForeignKeyTo);
        Assert.Contains(result.Document.Relationships, edge => edge.Kind == GraphRelationshipKind.DependsOn);
        Assert.DoesNotContain(result.Document.Nodes, node =>
            node.Properties.Values.OfType<string>().Any(value =>
                value.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Password=", StringComparison.OrdinalIgnoreCase)));

        var validation = new GraphDocumentValidator(new PreflightValidatorOptions
        {
            ExpectedCenterMenuCount = null,
            RequiredDatabaseName = "ConfiguredInvestmentDb",
            RequiredProvider = "SqlServer",
            RequireCompleteExtraction = true,
        }).Validate(result.Document);
        Assert.False(validation.HasBlockingErrors, string.Join(
            Environment.NewLine,
            validation.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public void ProjectGraphDatabaseSourceProvider_應只從使用者設定建立唯讀SQL連線()
    {
        var configuration = new ProjectDatabaseConfiguration(
            "configured-project",
            ProjectDatabaseProvider.SqlServer,
            "user-configured.example.internal",
            1444,
            "ConfiguredInvestmentDb",
            SqlServerAuthentication.SqlPassword,
            "configured-user",
            "temporary-test-secret",
            HasPassword: true,
            TrustServerCertificate: false,
            SqlitePath: null,
            DateTimeOffset.UnixEpoch);

        var source = ProjectGraphDatabaseSourceProvider.Build(configuration);
        var connection = new SqlConnectionStringBuilder(source.ConnectionString);

        Assert.Equal("user-configured.example.internal,1444", connection.DataSource);
        Assert.Equal("ConfiguredInvestmentDb", connection.InitialCatalog);
        Assert.Equal("configured-user", connection.UserID);
        Assert.Equal("temporary-test-secret", connection.Password);
        Assert.Equal(ApplicationIntent.ReadOnly, connection.ApplicationIntent);
        Assert.True(connection.Encrypt);
        Assert.False(connection.PersistSecurityInfo);
    }

    private static SolutionFixture CreateSolutionFixture(string root)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var references = TrustedPlatformReferences();
        var coreId = ProjectId.CreateNewId("Core");
        var appId = ProjectId.CreateNewId("App");
        solution = solution.AddProject(ProjectInfo.Create(
            coreId,
            VersionStamp.Create(),
            "Core",
            "Core",
            LanguageNames.CSharp,
            filePath: Path.Combine(root, "Core", "Core.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            metadataReferences: references));
        solution = solution.AddProject(ProjectInfo.Create(
            appId,
            VersionStamp.Create(),
            "App",
            "App",
            LanguageNames.CSharp,
            filePath: Path.Combine(root, "App", "App.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            metadataReferences: references));
        solution = solution.AddProjectReference(appId, new ProjectReference(coreId));
        solution = AddDocument(solution, coreId, root, "Core/CoreTypes.cs", CoreSource);
        solution = AddDocument(solution, appId, root, "App/TradeController.cs", AppSource);
        solution = AddDocument(solution, appId, root, "App/TradeController.Partial.cs", AppPartialSource);
        return new SolutionFixture(workspace, solution);
    }

    private static Solution AddDocument(
        Solution solution,
        ProjectId projectId,
        string root,
        string relativePath,
        string source)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return solution.AddDocument(
            DocumentId.CreateNewId(projectId, relativePath),
            Path.GetFileName(path),
            SourceText.From(source, Encoding.UTF8),
            filePath: path);
    }

    private static IReadOnlyList<MetadataReference> TrustedPlatformReferences() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    private static GraphDocument EmptyDocument(string root) => new(
        new GraphRunMetadata(
            "regression-run",
            DateTimeOffset.UnixEpoch,
            root,
            root == "D:/source" ? "ConfiguredInvestmentDb" : string.Empty,
            GraphBuildStage.CompleteExtraction,
            null,
            null,
            root == "D:/source" ? "SqlServer" : "SourceOnly"),
        [],
        []);

    private static GraphNode SingleType(GraphDocument document, string fullName) =>
        Assert.Single(document.Nodes, node => IsType(node, fullName));

    private static GraphNode SingleMethod(GraphDocument document, string typeName, string methodName) =>
        Assert.Single(document.Nodes, node =>
            node.Kind == GraphNodeKind.CodeMethod &&
            Text(node, "containing_type_full_name") == typeName &&
            Text(node, "name") == methodName);

    private static bool IsType(GraphNode node, string fullName) =>
        node.Kind == GraphNodeKind.CodeType && Text(node, "full_name") == fullName;

    private static string Text(GraphNode node, string key) =>
        node.Properties.GetValueOrDefault(key)?.ToString() ?? string.Empty;

    private static void AssertEdge(
        GraphDocument document,
        GraphRelationshipKind kind,
        string sourceKey) =>
        Assert.Contains(document.Relationships, edge => edge.Kind == kind && edge.SourceKey == sourceKey);

    private static string CreateTestRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wingman-semantic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(path, "Core"));
        Directory.CreateDirectory(Path.Combine(path, "App"));
        return path;
    }

    private static void DeleteTestRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class SolutionFixture(AdhocWorkspace workspace, Solution solution) : IDisposable
    {
        public Solution Solution { get; } = solution;
        public void Dispose() => workspace.Dispose();
    }

    private const string CoreSource = """
        namespace Demo.Core;

        public interface IWorker
        {
            string Run(int value);
        }

        public abstract class BaseWorker
        {
            public virtual string Run(int value) => value.ToString();
        }

        public static class Helper
        {
            public static string Format(int value) => value.ToString();
        }

        public record TradeRecord(int Id);
        public struct Price { public decimal Value; }
        public enum TradeStatus { Pending, Completed }
        """;

    private const string AppSource = """
        using Demo.Core;

        namespace Demo.App;

        public partial class TradeController : BaseWorker, IWorker
        {
            public override string Run(int value)
            {
                var service = new SettlementService();
                var first = Helper.Format(value);
                var second = Helper.Format(value + 1);
                return service.Process(value) + first + second;
            }

            public string Overload() => Process("trade");
            private int Process(int value) => value;
            private string Process(string value) => value;

            public sealed class NestedWorker { }
        }

        public sealed class SettlementService
        {
            public string Process(int value) => value.ToString();
        }

        public sealed class ExplicitWorker : IWorker
        {
            string IWorker.Run(int value) => value.ToString();
        }

        public sealed class ExternalUse
        {
            public void Open() => System.IO.File.OpenRead("sample.txt").Dispose();
        }
        """;

    private const string AppPartialSource = """
        namespace Demo.App;

        public partial class TradeController
        {
            public string Description => "trade";
        }
        """;
}
