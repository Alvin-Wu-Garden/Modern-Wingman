using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AgentService.UnitTests;

/// <summary>
/// Exercises the complete DI/host startup path using a clean, test-only SQLite
/// database. It proves that no PAT means no local Copilot credential fallback.
/// </summary>
public sealed class AgentServiceStartupTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), "ModernWingmanTests", Guid.NewGuid().ToString("N"), "wingman.db");
    private AgentServiceFactory? _factory;

    public Task InitializeAsync()
    {
        _factory = new AgentServiceFactory(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FreshDatabase_ReportsCopilotAsNotConfiguredWithoutStartingLocalLogin()
    {
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync("/api/providers/copilot-default/key-status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.False(document.RootElement.GetProperty("hasStoredKey").GetBoolean());
        Assert.Equal("not_configured", document.RootElement.GetProperty("runtimeStatus").GetProperty("state").GetString());
        Assert.False(document.RootElement.GetProperty("runtimeStatus").GetProperty("isAuthenticated").GetBoolean());
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    private sealed class AgentServiceFactory(string databasePath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:WingmanDb"] = $"Data Source={databasePath}",
                    ["Neo4j:Enabled"] = "false",
                }));
        }
    }
}
