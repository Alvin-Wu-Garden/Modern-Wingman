using AgentService.Modules.GraphRAG;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class TextGraphExtractorsV3Tests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"wingman-text-v3-{Guid.NewGuid():N}");

    public TextGraphExtractorsV3Tests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task JavaExtractor_CreatesSpringEntryAndTypeLevelCall()
    {
        var service = Path.Combine(_root, "OrderService.java");
        var controller = Path.Combine(_root, "OrderController.java");
        await File.WriteAllTextAsync(service, """
            package com.demo;
            public class OrderService {
                public void save() {}
            }
            """);
        await File.WriteAllTextAsync(controller, """
            package com.demo;
            @RestController
            @RequestMapping("/orders")
            public class OrderController {
                private OrderService service;

                @PostMapping("/save")
                public void persist() {
                    service.save();
                }
            }
            """);

        var fragment = await new JavaGraphExtractor(NullLogger<JavaGraphExtractor>.Instance)
            .ExtractAsync(_root, [service, controller]);
        var snapshot = Assemble(fragment, "java-source-v3");

        Assert.Equal(2, snapshot.Nodes.Count(node => node.Kind == GraphNodeKind.Code));
        Assert.Contains(snapshot.Nodes, node =>
            node.Kind == GraphNodeKind.EntryPoint &&
            node.Id == "entry:web:order/persist");
        Assert.Contains(snapshot.Edges, edge =>
            edge.Kind == GraphEdgeKind.Handles &&
            edge.TargetId == "code:java:com.demo.ordercontroller");
        Assert.Contains(snapshot.Edges, edge =>
            edge.Kind == GraphEdgeKind.Calls &&
            edge.SourceId == "code:java:com.demo.ordercontroller" &&
            edge.TargetId == "code:java:com.demo.orderservice");
        Assert.DoesNotContain(snapshot.Nodes,
            node => node.Role.Contains("method", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FrontendExtractor_ConnectsPageToBackendWithoutComponentNodes()
    {
        var directory = Path.Combine(_root, "views", "orders");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "index.js");
        await File.WriteAllTextAsync(file, """
            Ext.define('RiskMaster.view.orders.Index', {
                save: function () {
                    Ext.Ajax.request({ url: '/Orders/Save' });
                    fetch('/api/Orders/Preview');
                    axios.get('/Orders/Lookup');
                }
            });
            """);

        var fragment = await new FrontendGraphExtractor(NullLogger<FrontendGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);
        var snapshot = Assemble(fragment, "frontend-source-v3");

        var page = Assert.Single(snapshot.Nodes, node => node.Role == GraphRoles.FrontendPage);
        var code = Assert.Single(snapshot.Nodes, node => node.Kind == GraphNodeKind.Code);
        Assert.Contains(snapshot.Edges, edge =>
            edge.Kind == GraphEdgeKind.Handles &&
            edge.SourceId == page.Id &&
            edge.TargetId == code.Id);
        Assert.Equal(3, snapshot.Edges.Count(edge => edge.Kind == GraphEdgeKind.RoutesTo));
        Assert.Contains(snapshot.Nodes, node => node.Id == "entry:web:orders/save");
        Assert.Contains(snapshot.Nodes, node => node.Id == "entry:web:orders/preview");
        Assert.Contains(snapshot.Nodes, node => node.Id == "entry:web:orders/lookup");
    }

    [Fact]
    public async Task FrontendExtractor_ExtractsExtStoreAndFblAjaxWrapperUrls()
    {
        var directory = Path.Combine(_root, "Scripts", "FormCollection");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "frmBond.js");
        await File.WriteAllTextAsync(file, """
            var store = new Ext.data.JsonStore({
                proxy: new Ext.data.HttpProxy({
                    url: RMSystemData.UrlRoot + '/BondTrade/LoadData/'
                })
            });
            RM.Ext.AjaxRequest({
                url: RMSystemData.UrlRoot + '/BondTrade/Void/'
            });
            RMCommonLib.Fetch('/BondTrade/Preview');
            """);

        var fragment = await new FrontendGraphExtractor(NullLogger<FrontendGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);
        var snapshot = Assemble(fragment, "frontend-source-v3");

        Assert.Contains(snapshot.Nodes, node => node.Id == "entry:web:bondtrade/loaddata");
        Assert.Contains(snapshot.Nodes, node => node.Id == "entry:web:bondtrade/void");
        Assert.Contains(snapshot.Nodes, node => node.Id == "entry:web:bondtrade/preview");
        Assert.Equal(3, snapshot.Edges.Count(edge => edge.Kind == GraphEdgeKind.RoutesTo));
        Assert.Contains(snapshot.Edges.SelectMany(edge => edge.Evidence), evidence =>
            evidence.Details?.GetValueOrDefault("caller") == "root-prefixed-url");
        Assert.Contains(snapshot.Edges.SelectMany(edge => edge.Evidence), evidence =>
            evidence.Details?.GetValueOrDefault("caller") == "wrapper");
    }

    [Fact]
    public async Task FrontendExtractor_IgnoresMinifiedAndVendorFiles()
    {
        var minified = Path.Combine(_root, "app.min.js");
        var vendorDirectory = Path.Combine(_root, "vendor");
        Directory.CreateDirectory(vendorDirectory);
        var vendor = Path.Combine(vendorDirectory, "client.js");
        await File.WriteAllTextAsync(minified, "fetch('/Orders/Save')");
        await File.WriteAllTextAsync(vendor, "fetch('/Orders/Save')");

        var fragment = await new FrontendGraphExtractor(NullLogger<FrontendGraphExtractor>.Instance)
            .ExtractAsync(_root, [minified, vendor]);
        Assert.Empty(fragment.Nodes);
        Assert.Empty(fragment.Edges);
    }

    private static GraphSnapshot Assemble(GraphFragment fragment, string extractor) =>
        GraphAssembler.Assemble(
            "project", "manifest", DateTimeOffset.UnixEpoch,
            new GraphIndexerDescriptor("test",
                new Dictionary<string, string> { [extractor] = "3.0.0" }),
            "tree", "full", [], [fragment]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
