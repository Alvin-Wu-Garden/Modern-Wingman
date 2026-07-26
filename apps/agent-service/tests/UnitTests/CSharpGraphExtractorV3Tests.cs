using AgentService.Modules.GraphRAG;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class CSharpGraphExtractorV3Tests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"wingman-csharp-v3-{Guid.NewGuid():N}");

    public CSharpGraphExtractorV3Tests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ExtractAsync_CreatesTypeLevelCallsAndControllerEntryWithoutMethodNodes()
    {
        var file = Path.Combine(_root, "OrdersController.cs");
        await File.WriteAllTextAsync(file, """
            namespace Demo;

            public sealed class OrderService
            {
                public void SaveOrder() { }
            }

            public sealed class OrdersController
            {
                private readonly OrderService _service = new();

                [ActionName("Save")]
                [HttpPost("orders/save")]
                public void Persist()
                {
                    _service.SaveOrder();
                }
            }
            """);

        var result = await new CSharpGraphExtractor(NullLogger<CSharpGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);

        Assert.Equal(3, result.Nodes.Count);
        Assert.Equal(2, result.Nodes.Count(node => node.Kind == GraphNodeKind.Code));
        Assert.Single(result.Nodes, node =>
            node.Kind == GraphNodeKind.EntryPoint &&
            node.Id == "entry:web:orders/save");
        Assert.Contains(result.Edges, edge =>
            edge.Kind == GraphEdgeKind.Handles &&
            edge.SourceId == "entry:web:orders/save" &&
            edge.TargetId == "code:csharp:demo.orderscontroller");
        var call = Assert.Single(result.Edges, edge =>
            edge.Kind == GraphEdgeKind.Calls &&
            edge.SourceId == "code:csharp:demo.orderscontroller" &&
            edge.TargetId == "code:csharp:demo.orderservice");
        Assert.Contains(call.Evidence, evidence =>
            evidence.Details!["sourceMethod"] == "Persist" &&
            evidence.Details["targetMethod"] == "SaveOrder");
        Assert.DoesNotContain(result.Nodes,
            node => node.Role.Contains("method", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAsync_MergesPartialTypeDeclarationsAsOneCodeNode()
    {
        var first = Path.Combine(_root, "Part1.cs");
        var second = Path.Combine(_root, "Part2.cs");
        await File.WriteAllTextAsync(first, """
            namespace Demo;
            public partial class PositionService { public void Load() { } }
            """);
        await File.WriteAllTextAsync(second, """
            namespace Demo;
            public partial class PositionService { public void Save() { } }
            """);

        var fragment = await new CSharpGraphExtractor(NullLogger<CSharpGraphExtractor>.Instance)
            .ExtractAsync(_root, [first, second]);
        var snapshot = GraphAssembler.Assemble(
            "project", "manifest", DateTimeOffset.UnixEpoch,
            new GraphIndexerDescriptor("test",
                new Dictionary<string, string> { ["csharp-roslyn-v3"] = "3.0.0" }),
            "tree", "full", [], [fragment]);

        var node = Assert.Single(snapshot.Nodes);
        Assert.Equal("code:csharp:demo.positionservice", node.Id);
        Assert.Equal(2, node.Evidence.Count);
        Assert.Contains("Load", node.SearchableText);
        Assert.Contains("Save", node.SearchableText);
    }

    [Fact]
    public async Task ExtractAsync_AspNetCoreCombinesControllerAndActionRoutes()
    {
        var file = Path.Combine(_root, "PositionsController.cs");
        await File.WriteAllTextAsync(file, """
            namespace Demo.Api;
            [Route("api/[controller]")]
            public sealed class PositionsController : ControllerBase
            {
                [HttpGet("{id}")]
                public object Get(int id) => new();
            }
            """);

        var fragment = await new CSharpGraphExtractor(
                NullLogger<CSharpGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);

        var entry = Assert.Single(
            fragment.Nodes,
            node => node.Id == "entry:web:positions/get");
        Assert.Contains("/api/Positions/{id}", entry.Aliases);
        Assert.Contains(entry.Evidence, evidence =>
            evidence.Details!["httpVerbs"] == "GET" &&
            evidence.Details["routes"].Contains(
                "/api/Positions/{id}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_AspNetMvcUsesConventionActionNameAndVerbEvidence()
    {
        var file = Path.Combine(_root, "TradeController.cs");
        await File.WriteAllTextAsync(file, """
            namespace Demo.Mvc;
            public sealed class TradeController : Controller
            {
                [ActionName("Approve")]
                [HttpPost]
                public void Confirm() { }

                [NonAction]
                public void Helper() { }
            }
            """);

        var fragment = await new CSharpGraphExtractor(
                NullLogger<CSharpGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);

        var entry = Assert.Single(
            fragment.Nodes,
            node => node.Kind == GraphNodeKind.EntryPoint);
        Assert.Equal("entry:web:trade/approve", entry.Id);
        Assert.Contains("/Trade/Approve", entry.Aliases);
        Assert.Contains(entry.Evidence, evidence =>
            evidence.Details!["httpVerbs"] == "POST");
        Assert.DoesNotContain(
            fragment.Nodes,
            node => node.Id.EndsWith("/helper", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_LinksStaticTaskNameToScheduledTaskImplementation()
    {
        var file = Path.Combine(_root, "IFRSImportTask.cs");
        await File.WriteAllTextAsync(file, """
            namespace RMScheduleTaskDefinition;

            public sealed class IFRSImportTask
            {
                public static string TaskName
                {
                    get { return "IFRS_IMPORT"; }
                }
            }

            public sealed class DailyCloseTask
            {
                public const string TaskName = "DAILY_CLOSE";
            }
            """);

        var fragment = await new CSharpGraphExtractor(
                NullLogger<CSharpGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);

        var ifrsEntry = Assert.Single(
            fragment.Nodes,
            node => node.Id == "entry:task:ifrs_import");
        Assert.Equal(GraphNodeKind.EntryPoint, ifrsEntry.Kind);
        Assert.Equal(GraphRoles.ScheduledTask, ifrsEntry.Role);
        Assert.Contains("IFRSImportTask", ifrsEntry.SearchableText);
        Assert.Equal("IFRS_IMPORT", ifrsEntry.Attributes["taskName"]);
        Assert.Contains(fragment.Edges, edge =>
            edge.SourceId == ifrsEntry.Id &&
            edge.Kind == GraphEdgeKind.Handles &&
            edge.TargetId == "code:csharp:rmscheduletaskdefinition.ifrsimporttask");

        Assert.Contains(fragment.Nodes, node =>
            node.Id == "entry:task:daily_close" &&
            node.Role == GraphRoles.ScheduledTask);
        Assert.Contains(fragment.Edges, edge =>
            edge.SourceId == "entry:task:daily_close" &&
            edge.Kind == GraphEdgeKind.Handles &&
            edge.TargetId == "code:csharp:rmscheduletaskdefinition.dailyclosetask");
    }

    [Fact]
    public async Task ExtractAsync_DoesNotLeakInvocationReceiverLiteralsIntoCallEvidence()
    {
        var file = Path.Combine(_root, "LegacySession.cs");
        await File.WriteAllTextAsync(file, """
            namespace Legacy;

            public sealed class LegacyString
            {
                public LegacyString(string value) { }
                public byte[] GetBytes() => [];
            }

            public sealed class Session
            {
                public byte[] KeepAlive() =>
                    new LegacyString("keepalive@example.com").GetBytes();
            }
            """);

        var fragment = await new CSharpGraphExtractor(
                NullLogger<CSharpGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);
        var call = Assert.Single(fragment.Edges, edge =>
            edge.Kind == GraphEdgeKind.Calls &&
            edge.SourceId == "code:csharp:legacy.session" &&
            edge.TargetId == "code:csharp:legacy.legacystring");

        Assert.DoesNotContain(
            "keepalive@example.com",
            string.Join(' ', call.Evidence.SelectMany(item =>
                item.Details?.Values ?? [])),
            StringComparison.OrdinalIgnoreCase);
        var snapshot = GraphAssembler.Assemble(
            "project", "manifest", DateTimeOffset.UnixEpoch,
            new GraphIndexerDescriptor("test",
                new Dictionary<string, string> { ["csharp-roslyn-v3"] = "3.1.0" }),
            "tree", "full", [], [fragment]);
        GraphAssembler.ValidateSnapshot(snapshot);
    }

    [Fact]
    public async Task ExtractAsync_IgnoresGeneratedAndOutOfRootFiles()
    {
        var generated = Path.Combine(_root, "Generated.g.cs");
        await File.WriteAllTextAsync(generated, """
            // <auto-generated />
            public sealed class GeneratedType { }
            """);
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(outside, "public sealed class OutsideType { }");
        try
        {
            var fragment = await new CSharpGraphExtractor(NullLogger<CSharpGraphExtractor>.Instance)
                .ExtractAsync(_root, [generated, outside]);
            Assert.Empty(fragment.Nodes);
            Assert.Empty(fragment.Edges);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
