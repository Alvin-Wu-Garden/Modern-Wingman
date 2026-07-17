using System.Data;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

public sealed class MarketplacePluginSqliteStore(IDbContextFactory<AppDbContext> factory) : IMarketplacePluginStore
{
    public async Task SaveInstallationAsync(MarketplacePluginInstallation installation, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            UPDATE plugin_installations SET PluginId=$plugin,Version=$version,Enabled=$enabled,InstalledPath=$path,InstalledAt=$at WHERE ArtifactId=$artifact;
            """;
        Add(command, "$plugin", installation.PluginId); Add(command, "$version", installation.Version); Add(command, "$enabled", installation.Enabled ? 1 : 0); Add(command, "$path", installation.InstalledPath); Add(command, "$at", ToDb(installation.InstalledAt)); Add(command, "$artifact", installation.ArtifactId);
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await using var insert = db.Database.GetDbConnection().CreateCommand();
            insert.CommandText = "INSERT INTO plugin_installations (Id,ArtifactId,PluginId,Version,Enabled,InstalledPath,InstalledAt) VALUES ($id,$artifact,$plugin,$version,$enabled,$path,$at);";
            Add(insert, "$id", installation.Id); Add(insert, "$artifact", installation.ArtifactId); Add(insert, "$plugin", installation.PluginId); Add(insert, "$version", installation.Version); Add(insert, "$enabled", installation.Enabled ? 1 : 0); Add(insert, "$path", installation.InstalledPath); Add(insert, "$at", ToDb(installation.InstalledAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<MarketplacePluginInstallation>> ListInstallationsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken); await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Id,ArtifactId,PluginId,Version,Enabled,InstalledPath,InstalledAt FROM plugin_installations ORDER BY PluginId,Version;";
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        var list = new List<MarketplacePluginInstallation>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) list.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) != 0, reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return list;
    }

    public async Task SetEnabledAsync(string installationId, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var count = await db.Database.ExecuteSqlRawAsync("UPDATE plugin_installations SET Enabled={0} WHERE Id={1};", [enabled ? 1 : 0, installationId], cancellationToken);
        if (count == 0) throw new KeyNotFoundException("找不到 Plugin installation。");
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter); }
    private static string ToDb(DateTimeOffset value) => value.ToString("O");
}
