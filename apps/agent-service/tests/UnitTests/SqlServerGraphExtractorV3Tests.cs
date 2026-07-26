using System.Text.Json;
using AgentService.Modules.GraphRAG;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class SqlServerGraphExtractorV3Tests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"wingman-sql-v3-{Guid.NewGuid():N}");

    public SqlServerGraphExtractorV3Tests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ExtractAsync_UsesScriptDomToSeparateReadsAndWrites()
    {
        var file = Path.Combine(_root, "SaveOrder.sql");
        await File.WriteAllTextAsync(file, """
            CREATE PROCEDURE dbo.usp_SaveOrder
            AS
            BEGIN
                UPDATE dbo.tblOrders
                   SET Status = 1
                 WHERE ID IN (SELECT OrderID FROM dbo.tblOrderItems);

                SELECT CustomerID FROM dbo.tblCustomers;
            END
            """);

        var fragment = await new SqlServerGraphExtractor(
                NullLogger<SqlServerGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);
        var snapshot = Assemble(fragment);

        var procedure = Assert.Single(snapshot.Nodes, node => node.Role == GraphRoles.Procedure);
        Assert.Equal("data:sql:project-db/dbo/procedure/usp_saveorder", procedure.Id);
        Assert.Contains(snapshot.Edges, edge =>
            edge.SourceId == procedure.Id &&
            edge.Kind == GraphEdgeKind.Writes &&
            edge.TargetId.EndsWith("/table/tblorders", StringComparison.Ordinal));
        Assert.Contains(snapshot.Edges, edge =>
            edge.SourceId == procedure.Id &&
            edge.Kind == GraphEdgeKind.Reads &&
            edge.TargetId.EndsWith("/table/tblorderitems", StringComparison.Ordinal));
        Assert.Contains(snapshot.Edges, edge =>
            edge.SourceId == procedure.Id &&
            edge.Kind == GraphEdgeKind.Reads &&
            edge.TargetId.EndsWith("/table/tblcustomers", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Nodes,
            node => node.Name.StartsWith("#", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_UsesMigrationCodeOwnerForAdHocScriptAndIgnoresTempTables()
    {
        var file = Path.Combine(_root, "20260725.sql");
        await File.WriteAllTextAsync(file, """
            SELECT ID INTO #Selected FROM dbo.tblOrders;
            UPDATE dbo.tblPositions SET State = 2;
            """);

        var fragment = await new SqlServerGraphExtractor(
                NullLogger<SqlServerGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);
        var snapshot = Assemble(fragment);

        var migration = Assert.Single(snapshot.Nodes, node => node.Role == GraphRoles.Migration);
        Assert.Equal("code:sql:20260725.sql", migration.Id);
        Assert.DoesNotContain(snapshot.Nodes, node => node.Name == "#Selected");
        Assert.Contains(snapshot.Edges, edge =>
            edge.SourceId == migration.Id && edge.Kind == GraphEdgeKind.Writes);
    }

    [Fact]
    public async Task ExtractAsync_ParseErrorBecomesWarningInsteadOfCrashingRun()
    {
        var file = Path.Combine(_root, "Broken.sql");
        await File.WriteAllTextAsync(file, "SELECT FROM dbo.tblOrders WHERE");

        var fragment = await new SqlServerGraphExtractor(
                NullLogger<SqlServerGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);

        Assert.Contains(fragment.Diagnostics,
            diagnostic => diagnostic.Code == "SQL_PARSE_PARTIAL");
        Assert.Contains(fragment.Nodes, node => node.Role == GraphRoles.Migration);
    }

    [Fact]
    public async Task ExtractAsync_CSharpOrmAndEmbeddedSqlCreateTypeLevelDataPaths()
    {
        var file = Path.Combine(_root, "OrderRepository.cs");
        await File.WriteAllTextAsync(file, """
            using System.ComponentModel.DataAnnotations.Schema;
            namespace Demo.Data;

            [Table("tblOrders", Schema = "risk")]
            public sealed class OrderRow { public int Id { get; set; } }

            public sealed class OrderRepository
            {
                public object Load(dynamic connection) =>
                    connection.Query<OrderRow>(
                        "SELECT ID FROM risk.tblOrders WHERE State = 1");

                public void Save(dynamic connection) =>
                    connection.Execute(
                        "UPDATE risk.tblOrders SET State = 2 WHERE ID = 1");
            }
            """);

        var fragment = await new SqlServerGraphExtractor(
                NullLogger<SqlServerGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);
        var snapshot = Assemble(fragment);

        var model = Assert.Single(
            snapshot.Nodes,
            node => node.Id == "code:csharp:demo.data.orderrow");
        var repository = Assert.Single(
            snapshot.Nodes,
            node => node.Id == "code:csharp:demo.data.orderrepository");
        var table = Assert.Single(
            snapshot.Nodes,
            node => node.Id.EndsWith(
                "/risk/table/tblorders", StringComparison.Ordinal));
        Assert.Contains(snapshot.Edges, edge =>
            edge.SourceId == model.Id &&
            edge.Kind == GraphEdgeKind.MapsTo &&
            edge.TargetId == table.Id);
        Assert.Contains(snapshot.Edges, edge =>
            edge.SourceId == repository.Id &&
            edge.Kind == GraphEdgeKind.Reads &&
            edge.TargetId == table.Id);
        Assert.Contains(snapshot.Edges, edge =>
            edge.SourceId == repository.Id &&
            edge.Kind == GraphEdgeKind.Writes &&
            edge.TargetId == table.Id);
        Assert.DoesNotContain(snapshot.Nodes,
            node => node.Role.Contains("method", StringComparison.OrdinalIgnoreCase) ||
                    node.Role.Contains("column", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAsync_ResolvesGeneratedDataTableConstantsForDynamicSqlWrites()
    {
        var definition = Path.Combine(_root, "DDAsyncConfirm.cs");
        var dataAccess = Path.Combine(_root, "DALAsyncConfirmBase.cs");
        await File.WriteAllTextAsync(definition, """
            namespace Demo.DbDefinition;
            public static class DDAsyncConfirm
            {
                public const string DataTableName = "tblAsyncConfirm";
                public static class ScriptFieldNameList
                {
                    public const string Status = "[Status]";
                }
            }
            """);
        await File.WriteAllTextAsync(dataAccess, """
            namespace Demo.Data;
            using Demo.DbDefinition;

            public sealed class DALAsyncConfirmBase
            {
                public string Save(int status) =>
                    " UPDATE " + DDAsyncConfirm.DataTableName +
                    " SET " + DDAsyncConfirm.ScriptFieldNameList.Status +
                    " = " + status;
            }
            """);

        var fragment = await new SqlServerGraphExtractor(
                NullLogger<SqlServerGraphExtractor>.Instance)
            .ExtractAsync(_root, [definition, dataAccess]);
        var snapshot = Assemble(fragment);

        var owner = Assert.Single(snapshot.Nodes, node =>
            node.Id == "code:csharp:demo.data.dalasyncconfirmbase");
        var table = Assert.Single(snapshot.Nodes, node =>
            node.Id == "data:sql:project-db/dbo/table/tblasyncconfirm");
        var write = Assert.Single(snapshot.Edges, edge =>
            edge.SourceId == owner.Id &&
            edge.Kind == GraphEdgeKind.Writes &&
            edge.TargetId == table.Id);
        Assert.Contains("資料寫入", owner.SearchableText);
        Assert.Contains("Update", owner.SearchableText);
        Assert.Contains(write.Evidence, evidence =>
            evidence.Confidence == GraphConfidence.Resolved &&
            evidence.Details!["definitionType"] == "DDAsyncConfirm");
    }

    [Fact]
    public async Task ExtractAsync_JavaJpaAndJdbcCreateDataPaths()
    {
        var file = Path.Combine(_root, "PositionRepository.java");
        await File.WriteAllTextAsync(file, """
            package com.demo.data;
            @Table(name = "tblPosition", schema = "risk")
            public class PositionRepository {
                public void save(JdbcTemplate jdbc) {
                    jdbc.update("UPDATE risk.tblPosition SET State = 2");
                }
            }
            """);

        var fragment = await new SqlServerGraphExtractor(
                NullLogger<SqlServerGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);
        var snapshot = Assemble(fragment);

        var code = Assert.Single(
            snapshot.Nodes,
            node => node.Id == "code:java:com.demo.data.positionrepository");
        var table = Assert.Single(
            snapshot.Nodes,
            node => node.Id.EndsWith(
                "/risk/table/tblposition", StringComparison.Ordinal));
        Assert.Contains(snapshot.Edges, edge =>
            edge.SourceId == code.Id &&
            edge.Kind == GraphEdgeKind.MapsTo &&
            edge.TargetId == table.Id);
        Assert.Contains(snapshot.Edges, edge =>
            edge.SourceId == code.Id &&
            edge.Kind == GraphEdgeKind.Writes &&
            edge.TargetId == table.Id);
    }

    [Fact]
    public async Task ExtractAsync_DynamicIdentifierProducesCapabilityDiagnostic()
    {
        var file = Path.Combine(_root, "Dynamic.sql");
        await File.WriteAllTextAsync(
            file,
            "DECLARE @sql nvarchar(max); EXEC(@sql);");

        var fragment = await new SqlServerGraphExtractor(
                NullLogger<SqlServerGraphExtractor>.Instance)
            .ExtractAsync(_root, [file]);

        Assert.Contains(
            fragment.Diagnostics,
            diagnostic => diagnostic.Code == "DYNAMIC_SQL_IDENTIFIER");
        Assert.Contains(
            fragment.CapabilityGaps,
            gap => gap.Contains("動態 SQL", StringComparison.Ordinal));
    }

    [Fact]
    public void SqlServerGraphSource_DoesNotSerializeConnectionString()
    {
        var source = new SqlServerGraphSource(
            "Data Source=server;User ID=user;Password=secret",
            "LogicalDb");

        var json = JsonSerializer.Serialize(source);

        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", json, StringComparison.Ordinal);
        Assert.Contains("LogicalDb", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractDatabaseAsync_WhenFblFixtureIsConfigured_ProducesBusinessPaths()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "WINGMAN_TEST_SQLSERVER_CONNECTION");
        var database = Environment.GetEnvironmentVariable(
            "WINGMAN_TEST_SQLSERVER_DATABASE");
        if (string.IsNullOrWhiteSpace(connectionString) ||
            string.IsNullOrWhiteSpace(database))
            return;

        var fragment = await new SqlServerGraphExtractor(
                NullLogger<SqlServerGraphExtractor>.Instance)
            .ExtractDatabaseAsync(new SqlServerGraphSource(connectionString, database, 60));
        var snapshot = Assemble(fragment);

        GraphAssembler.ValidateSnapshot(snapshot);
        Assert.Contains(snapshot.Nodes, node =>
            node.Kind == GraphNodeKind.Feature && node.Role == GraphRoles.MenuFeature);
        Assert.Contains(snapshot.Nodes, node => node.Role == GraphRoles.CustomReport);
        Assert.Contains(snapshot.Nodes, node => node.Role == GraphRoles.Schedule);
        Assert.Contains(snapshot.Nodes, node => node.Role == GraphRoles.BatchReport);
        Assert.Contains(snapshot.Nodes, node => node.Role == GraphRoles.CustomEnum);
        Assert.Contains(snapshot.Nodes, node => node.Role == GraphRoles.CsvFormat);
        Assert.Contains(snapshot.Edges, edge => edge.Kind == GraphEdgeKind.RoutesTo);
        Assert.Contains(snapshot.Edges, edge => edge.Kind == GraphEdgeKind.Triggers);
        Assert.Contains(snapshot.Edges, edge => edge.Kind == GraphEdgeKind.MapsTo);
        var canonicalJson = JsonSerializer.Serialize(snapshot);
        var password = new SqlConnectionStringBuilder(connectionString).Password;
        if (!string.IsNullOrWhiteSpace(password))
            Assert.DoesNotContain(password, canonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Source=", canonicalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("User ID=", canonicalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(
            @"(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])",
            canonicalJson);
    }

    private static GraphSnapshot Assemble(GraphFragment fragment) =>
        GraphAssembler.Assemble(
            "project", "manifest", DateTimeOffset.UnixEpoch,
            new GraphIndexerDescriptor("test",
                new Dictionary<string, string> { ["sqlserver-scriptdom-v3"] = "3.0.0" }),
            "tree", "full", [], [fragment]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
