using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>
/// One-way, versioned import of the former Tauri records. It intentionally never enables
/// legacy ownership for destructive operations: imported deployments remain visible but are
/// marked ImportedLegacy until the user redeploys them through Marketplace.
/// </summary>
public sealed class MarketplaceLegacyMigration(
    IDbContextFactory<AppDbContext> factory,
    IMarketplaceArtifactService artifactService,
    IMarketplaceDeploymentStore deploymentStore,
    IMarketplaceActivityRecorder? activity = null)
{
    private const string MigrationId = "marketplace-legacy-tauri-v2";
    private static readonly IReadOnlyDictionary<string, string> AgentTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["wingman"] = "wingman-desktop", ["claude-code"] = "claude-code-cli", ["codex"] = "codex-cli",
        ["copilot"] = "github-copilot-vscode", ["cursor"] = "cursor-windows",
    };

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await IsCompletedAsync(db, cancellationToken) || !await TableExistsAsync(db, "library_skills", cancellationToken)) return;

        var errors = new List<string>();
        var importedSkills = 0; var importedMcp = 0; var importedLinks = 0; var manualLinks = 0;
        var skills = await ReadSkillsAsync(db, cancellationToken);
        var artifactByLegacySkillId = new Dictionary<long, MarketplaceArtifact>();
        foreach (var skill in skills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(skill.LibraryPath) || !File.Exists(Path.Combine(skill.LibraryPath, "SKILL.md")))
            { errors.Add($"skill:{skill.Id}:path-missing"); continue; }
            try
            {
                var import = await artifactService.ImportFolderAsync(skill.LibraryPath, cancellationToken);
                var artifact = import.Artifacts.FirstOrDefault(item => item.Kind == MarketplaceArtifactKind.Skill);
                if (artifact is null) { errors.Add($"skill:{skill.Id}:not-resolved"); continue; }
                artifactByLegacySkillId[skill.Id] = artifact; importedSkills++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            { errors.Add($"skill:{skill.Id}:{Sanitize(ex.Message)}"); }
        }

        if (await TableExistsAsync(db, "skill_agent_links", cancellationToken))
        {
            foreach (var link in await ReadSkillLinksAsync(db, cancellationToken))
            {
                if (!artifactByLegacySkillId.TryGetValue(link.SkillId, out var artifact)) { errors.Add($"skill-link:{link.SkillId}:artifact-missing"); continue; }
                if (!AgentTargets.TryGetValue(link.AgentId, out var target) || !TryScope(link.Scope, out var scope) || string.IsNullOrWhiteSpace(link.TargetPath))
                { manualLinks++; errors.Add($"skill-link:{link.SkillId}:manual-review"); continue; }
                try
                {
                    // Preserve known ownership but never treat it as a Wingman-created deployment.
                    await deploymentStore.SaveDeploymentAsync(artifact.Id, new(artifact.Id, target, scope, link.ProjectPath), link.TargetPath, artifact.ContentHash, "ImportedLegacy", cancellationToken);
                    importedLinks++;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { errors.Add($"skill-link:{link.SkillId}:{Sanitize(ex.Message)}"); }
            }
        }

        if (await TableExistsAsync(db, "mcp_servers", cancellationToken))
        {
            foreach (var mcp in await ReadMcpServersAsync(db, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var staging = Path.Combine(Path.GetTempPath(), "wingman-legacy-mcp-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(staging);
                    var definition = new JsonObject();
                    if (!string.IsNullOrWhiteSpace(mcp.Command)) definition["command"] = mcp.Command;
                    if (!string.IsNullOrWhiteSpace(mcp.Url)) definition["url"] = mcp.Url;
                    definition["args"] = ParseArray(mcp.Args);
                    definition["env"] = SanitizeLegacyEnv(mcp.Env);
                    var servers = new JsonObject { [mcp.Name] = definition };
                    await File.WriteAllTextAsync(Path.Combine(staging, ".mcp.json"), new JsonObject { ["mcpServers"] = servers }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
                    var import = await artifactService.ImportFolderAsync(staging, cancellationToken);
                    var artifact = import.Artifacts.FirstOrDefault(item => item.Kind == MarketplaceArtifactKind.McpServer);
                    if (artifact is null) { errors.Add($"mcp:{mcp.Id}:not-resolved"); continue; }
                    importedMcp++;
                    if (await TableExistsAsync(db, "mcp_agent_links", cancellationToken))
                    {
                        foreach (var link in await ReadMcpLinksAsync(db, mcp.Id, cancellationToken))
                        {
                            if (!AgentTargets.TryGetValue(link.AgentId, out var target) || string.IsNullOrWhiteSpace(link.ConfigPath)) { manualLinks++; errors.Add($"mcp-link:{mcp.Id}:manual-review"); continue; }
                            await deploymentStore.SaveDeploymentAsync(artifact.Id, new(artifact.Id, target, MarketplaceDeploymentScope.Global), link.ConfigPath, artifact.ContentHash, "ImportedLegacy", cancellationToken);
                            importedLinks++;
                        }
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { errors.Add($"mcp:{mcp.Id}:{Sanitize(ex.Message)}"); }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            }
        }

        // Tauri has no Plugin installation table. Do not infer packages from arbitrary folders.
        var summary = $"skills={importedSkills};mcp={importedMcp};links={importedLinks};manualReview={manualLinks};errors={errors.Count};plugins=not-present";
        await CompleteAsync(db, summary + (errors.Count == 0 ? string.Empty : ";" + string.Join("|", errors.Take(50))), cancellationToken);
        if (activity is not null)
            await activity.RecordAsync(new(Guid.NewGuid().ToString("N"), MigrationId, "legacy-migration", errors.Count == 0 ? "Completed" : "CompletedWithWarnings", null, null, summary, DateTimeOffset.UtcNow), cancellationToken);
    }

    private static async Task<IReadOnlyList<LegacySkill>> ReadSkillsAsync(AppDbContext db, CancellationToken ct)
    {
        var output = new List<LegacySkill>(); await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT id,library_path FROM library_skills WHERE library_path IS NOT NULL AND library_path <> '';";
        await OpenAsync(command, ct); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) output.Add(new(reader.GetInt64(0), reader.GetString(1))); return output;
    }
    private static async Task<IReadOnlyList<LegacySkillLink>> ReadSkillLinksAsync(AppDbContext db, CancellationToken ct)
    {
        var output = new List<LegacySkillLink>(); await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT skill_id,agent_id,scope,project_path,target_path FROM skill_agent_links;"; await OpenAsync(command, ct); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) output.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4))); return output;
    }
    private static async Task<IReadOnlyList<LegacyMcp>> ReadMcpServersAsync(AppDbContext db, CancellationToken ct)
    {
        var output = new List<LegacyMcp>(); await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT id,name,command,args,url,env FROM mcp_servers;"; await OpenAsync(command, ct); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) output.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5))); return output;
    }
    private static async Task<IReadOnlyList<LegacyMcpLink>> ReadMcpLinksAsync(AppDbContext db, long serverId, CancellationToken ct)
    {
        if (!await TableExistsAsync(db, "agents", ct)) return [];
        var output = new List<LegacyMcpLink>(); await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT l.agent_id,a.mcp_config_path FROM mcp_agent_links l LEFT JOIN agents a ON a.id=l.agent_id WHERE l.server_id=$id;"; Add(command, "$id", serverId); await OpenAsync(command, ct); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) output.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))); return output;
    }
    private static JsonArray ParseArray(string json) { try { return JsonNode.Parse(json) as JsonArray ?? []; } catch (JsonException) { return []; } }
    private static JsonObject SanitizeLegacyEnv(string json)
    {
        JsonObject env; try { env = JsonNode.Parse(json) as JsonObject ?? []; } catch (JsonException) { env = []; }
        foreach (var (name, value) in env.ToList()) if (value is not null && LooksSensitive(name)) env[name] = "REPLACE_WITH_YOUR_API_KEY";
        return env;
    }
    private static bool LooksSensitive(string name) => name.Contains("key", StringComparison.OrdinalIgnoreCase) || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase) || name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("credential", StringComparison.OrdinalIgnoreCase);
    private static bool TryScope(string value, out MarketplaceDeploymentScope scope) => Enum.TryParse(value, true, out scope);
    private static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(value.Length, 160)];
    private static async Task CompleteAsync(AppDbContext db, string summary, CancellationToken ct) { await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "INSERT INTO marketplace_data_migrations (MigrationId,CompletedAt,Summary) VALUES ($id,$completed,$summary);"; Add(command, "$id", MigrationId); Add(command, "$completed", DateTimeOffset.UtcNow.ToString("O")); Add(command, "$summary", summary); await OpenAsync(command, ct); await command.ExecuteNonQueryAsync(ct); }
    private static async Task<bool> IsCompletedAsync(AppDbContext db, CancellationToken ct) { await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "SELECT 1 FROM marketplace_data_migrations WHERE MigrationId=$id LIMIT 1;"; Add(command, "$id", MigrationId); await OpenAsync(command, ct); return await command.ExecuteScalarAsync(ct) is not null; }
    private static async Task<bool> TableExistsAsync(AppDbContext db, string table, CancellationToken ct) { await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;"; Add(command, "$name", table); await OpenAsync(command, ct); return await command.ExecuteScalarAsync(ct) is not null; }
    private static async Task OpenAsync(System.Data.Common.DbCommand command, CancellationToken ct) { if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(ct); }
    private static void Add(System.Data.Common.DbCommand command, string name, object? value) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter); }
    private sealed record LegacySkill(long Id, string LibraryPath); private sealed record LegacySkillLink(long SkillId, string AgentId, string Scope, string? ProjectPath, string TargetPath); private sealed record LegacyMcp(long Id, string Name, string? Command, string Args, string? Url, string Env); private sealed record LegacyMcpLink(string AgentId, string? ConfigPath);
}
