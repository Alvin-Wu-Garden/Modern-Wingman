using System.Security.Cryptography;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;
using AgentService.Infrastructure.CodeAnalysis;
using AgentService.Infrastructure.CodeGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class GraphMixedGoldenTests : IDisposable
{
    private static readonly DateTimeOffset GoldenCreatedAt =
        DateTimeOffset.Parse("2026-07-17T00:00:00Z");

    private static readonly GraphAnalysisProfile Profile = new(
        "mixed-golden-indexer-v1",
        GraphSchemaV2.Version,
        [
            new("java-build-model", "1.0.0"),
            new("java-structural-parser", "1.0.0"),
            new("roslyn", "1.0.0"),
            new("wingman.orm-mapping", "1.0.0"),
            new("wingman.sql-schema", "1.0.0"),
        ]);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wingman-mixed-golden-" + Guid.NewGuid().ToString("N"));

    public GraphMixedGoldenTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task MixedFullIndex_IsByteAndHashEquivalentAndPreservesGraphFacts()
    {
        var files = await WriteFixtureAsync();

        var first = await AnalyzeAndCanonicalizeAsync(files, reverseInput: false, "run-one");
        var second = await AnalyzeAndCanonicalizeAsync(files, reverseInput: true, "run-two");

        Assert.Equal(first.Snapshot.AnalysisSnapshotHash, second.Snapshot.AnalysisSnapshotHash);
        var comparison = GraphSnapshotComparer.Compare(first, second);
        Assert.True(comparison.IsEquivalent, FormatDifferences(comparison));
        Assert.Equal(SemanticCanonicalJson(first), SemanticCanonicalJson(second));

        AssertNodeKinds(first,
            CodeNodeKind.File,
            CodeNodeKind.Namespace,
            CodeNodeKind.Type,
            CodeNodeKind.Method,
            CodeNodeKind.Property,
            CodeNodeKind.Field,
            CodeNodeKind.Route,
            CodeNodeKind.Endpoint,
            CodeNodeKind.RequestContract,
            CodeNodeKind.ResponseContract,
            CodeNodeKind.Module,
            CodeNodeKind.Dependency,
            CodeNodeKind.DataStore,
            CodeNodeKind.Schema,
            CodeNodeKind.Table,
            CodeNodeKind.Column,
            CodeNodeKind.PrimaryKey,
            CodeNodeKind.ForeignKey,
            CodeNodeKind.Query,
            CodeNodeKind.Migration);
        AssertEdgeKinds(first,
            CodeEdgeKind.Contains,
            CodeEdgeKind.DeclaredIn,
            CodeEdgeKind.Calls,
            CodeEdgeKind.Implements,
            CodeEdgeKind.DispatchesTo,
            CodeEdgeKind.Handles,
            CodeEdgeKind.Consumes,
            CodeEdgeKind.Produces,
            CodeEdgeKind.DependsOnPackage,
            CodeEdgeKind.MapsTo,
            CodeEdgeKind.SerializesTo,
            CodeEdgeKind.Reads,
            CodeEdgeKind.Migrates,
            CodeEdgeKind.ForeignKeyTo,
            CodeEdgeKind.References);

        Assert.All(first.Nodes, node => Assert.NotEmpty(node.Evidence));
        Assert.All(first.Edges, edge => Assert.NotEmpty(edge.Evidence));
        Assert.All(first.Edges, edge => Assert.True(edge.Directed));

        var orderType = Assert.Single(first.Nodes, node => node.Id == "Mixed.OrderDto");
        Assert.Contains(orderType.Evidence, evidence => evidence.Extractor.Id == "roslyn");
        Assert.Contains(orderType.Evidence, evidence => evidence.Extractor.Id == "wingman.orm-mapping");
        Assert.Contains(orderType.Locations, location => location.ArtifactId == "file:src/OrderApi.cs");

        var ormTable = Assert.Single(first.Nodes, node => node.Id == "table:sales.orm_orders");
        Assert.Contains(ormTable.Evidence, evidence => evidence.Extractor.Id == "wingman.orm-mapping");
        Assert.Contains(ormTable.Locations, location => location.ArtifactId == "file:src/OrderApi.cs");

        var sqlTable = Assert.Single(first.Nodes, node => node.Id == "table:sales.orders");
        Assert.Contains(sqlTable.Evidence, evidence => evidence.Extractor.Id == "wingman.sql-schema");
        Assert.Contains(sqlTable.Locations, location => location.ArtifactId == "file:db/V001__orders.sql");

        var mapping = Assert.Single(first.Edges, edge =>
            edge.SourceId == "Mixed.OrderDto" &&
            edge.Kind == CodeEdgeKind.MapsTo &&
            edge.TargetId == "table:sales.orm_orders");
        var mappingEvidence = Assert.Single(mapping.Evidence,
            evidence => evidence.Extractor.Id == "wingman.orm-mapping");
        Assert.Contains("file:src/OrderApi.cs", mappingEvidence.ArtifactIds);
        Assert.False(string.IsNullOrWhiteSpace(mappingEvidence.ContentHash));

        var javaDispatch = Assert.Single(first.Edges, edge =>
            edge.SourceId == "mixed.PaymentGateway.charge()" &&
            edge.Kind == CodeEdgeKind.DispatchesTo &&
            edge.TargetId == "mixed.StripeGateway.charge()");
        Assert.Contains(javaDispatch.Evidence, evidence =>
            evidence.Extractor.Id == "java-structural-parser" &&
            evidence.ArtifactIds.Contains("file:src/Checkout.java"));

        var csharpCall = Assert.Single(first.Edges, edge =>
            edge.SourceId == "Mixed.OrdersController.Create(Mixed.OrderDto)" &&
            edge.Kind == CodeEdgeKind.Calls &&
            edge.TargetId == "Mixed.OrderService.Save(Mixed.OrderDto)");
        Assert.Contains(csharpCall.Evidence, evidence =>
            evidence.Extractor.Id == "roslyn" &&
            evidence.ArtifactIds.Contains("file:src/OrderApi.cs"));
    }

    private async Task<GraphSnapshotV2> AnalyzeAndCanonicalizeAsync(
        FixtureFiles files,
        bool reverseInput,
        string manifestVersion)
    {
        var roslyn = new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance);
        var java = new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance);
        var data = new DataSchemaExtractor(
            new IDataArtifactAdapter[] { new SqlDataArtifactAdapter(), new OrmDataArtifactAdapter() });

        var csharpResult = await roslyn.AnalyzeAsync(_root, [files.CSharp]);
        var javaResult = await java.AnalyzeAsync(_root, [files.Java]);
        var dataResult = await data.ExtractAsync(_root);
        Assert.DoesNotContain(dataResult.Diagnostics, item =>
            string.Equals(item.Severity, "error", StringComparison.OrdinalIgnoreCase));

        var results = new[] { csharpResult, javaResult, dataResult.Graph };
        var aggregate = new CodeAnalysisResult();
        foreach (var result in reverseInput ? results.Reverse() : results)
        {
            var nodes = reverseInput ? result.Nodes.AsEnumerable().Reverse() : result.Nodes;
            var edges = reverseInput ? result.Edges.AsEnumerable().Reverse() : result.Edges;
            aggregate.Nodes.AddRange(nodes);
            aggregate.Edges.AddRange(edges);
        }

        var artifacts = await BuildArtifactManifestAsync(files.All);
        if (reverseInput) artifacts.Reverse();
        return GraphSnapshotCanonicalizer.Create(
            "mixed-golden-project",
            manifestVersion,
            manifestVersion == "run-one" ? GoldenCreatedAt : GoldenCreatedAt.AddDays(1),
            Profile,
            "mixed-fixture-working-tree-v1",
            "full",
            artifacts,
            aggregate,
            dataResult.Diagnostics.Select(item =>
                $"{item.FilePath}|{item.AdapterId}|{item.Severity}|{item.Message}").ToList(),
            dataResult.CapabilityGaps);
    }

    private async Task<FixtureFiles> WriteFixtureAsync()
    {
        var sourceDirectory = Path.Combine(_root, "src");
        var databaseDirectory = Path.Combine(_root, "db");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(databaseDirectory);

        var csharp = Path.Combine(sourceDirectory, "OrderApi.cs");
        await File.WriteAllTextAsync(csharp, """
            using System.ComponentModel.DataAnnotations.Schema;
            namespace Mixed;

            [Table("orm_orders", Schema = "sales")]
            public sealed class OrderDto
            {
                public int Id { get; set; }
                public int CustomerId { get; set; }
            }

            public sealed class OrderService
            {
                public OrderDto Save(OrderDto request) => request;
            }

            [ApiController]
            [Route("api/orders")]
            public sealed class OrdersController
            {
                private readonly OrderService _service = new();

                [HttpPost]
                public OrderDto Create(OrderDto request) => _service.Save(request);
            }
            """);

        var java = Path.Combine(sourceDirectory, "Checkout.java");
        await File.WriteAllTextAsync(java, """
            package mixed;

            interface PaymentGateway {
                void charge();
            }

            final class StripeGateway implements PaymentGateway {
                public void charge() { }
            }

            final class Checkout {
                private PaymentGateway gateway;

                Checkout(PaymentGateway gateway) {
                    this.gateway = gateway;
                }

                void run() {
                    gateway.charge();
                }
            }
            """);

        var pom = Path.Combine(_root, "pom.xml");
        await File.WriteAllTextAsync(pom, """
            <project>
              <modelVersion>4.0.0</modelVersion>
              <groupId>mixed</groupId>
              <artifactId>mixed-java</artifactId>
              <version>1.0.0</version>
              <dependencies>
                <dependency>
                  <groupId>org.slf4j</groupId>
                  <artifactId>slf4j-api</artifactId>
                  <version>2.0.13</version>
                </dependency>
              </dependencies>
            </project>
            """);

        var sql = Path.Combine(databaseDirectory, "V001__orders.sql");
        await File.WriteAllTextAsync(sql, """
            CREATE TABLE sales.customers (
                id INT PRIMARY KEY
            );

            CREATE TABLE sales.orders (
                id INT PRIMARY KEY,
                customer_id INT,
                CONSTRAINT fk_orders_customer
                    FOREIGN KEY (customer_id) REFERENCES sales.customers(id)
            );

            SELECT id, customer_id FROM sales.orders;
            """);

        return new FixtureFiles(csharp, java, pom, sql);
    }

    private async Task<List<IndexedFileManifest>> BuildArtifactManifestAsync(
        IReadOnlyList<string> files)
    {
        var result = new List<IndexedFileManifest>();
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file);
            var relative = Path.GetRelativePath(_root, file).Replace('\\', '/');
            var kind = Path.GetFileName(file).Equals("pom.xml", StringComparison.OrdinalIgnoreCase)
                ? "maven"
                : Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            result.Add(new IndexedFileManifest(
                relative,
                kind,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }
        return result;
    }

    private static string SemanticCanonicalJson(GraphSnapshotV2 snapshot) =>
        GraphSnapshotCanonicalizer.Serialize(snapshot with
        {
            ManifestVersion = "golden",
            CreatedAt = GoldenCreatedAt,
        }, indented: false);

    private static void AssertNodeKinds(GraphSnapshotV2 snapshot, params CodeNodeKind[] expected)
    {
        foreach (var kind in expected)
            Assert.Contains(snapshot.Nodes, node => node.Kind == kind);
    }

    private static void AssertEdgeKinds(GraphSnapshotV2 snapshot, params CodeEdgeKind[] expected)
    {
        foreach (var kind in expected)
            Assert.Contains(snapshot.Edges, edge => edge.Kind == kind);
    }

    private static string FormatDifferences(GraphComparisonResult comparison) => string.Join(
        Environment.NewLine,
        comparison.Differences.Select(item =>
            $"{item.EntityType}|{item.Identity}|{item.Kind}|expected={item.Expected}|actual={item.Actual}"));

    private sealed record FixtureFiles(string CSharp, string Java, string Pom, string Sql)
    {
        public IReadOnlyList<string> All => [CSharp, Java, Pom, Sql];
    }
}
