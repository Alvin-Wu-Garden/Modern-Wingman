using System.Data;
using AgentService.Application.Contracts;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wingman.Marketplace.Contracts;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>
/// Stores Plugin configuration in the existing Wingman database. Values are protected
/// with the current Windows user's DPAPI scope and are never exposed through read APIs.
/// </summary>
public sealed class MarketplacePluginConfigurationSqliteStore(
    IDbContextFactory<AppDbContext> factory,
    ISecretProtector protector) : IMarketplacePluginConfigurationStore
{
    public async Task<IReadOnlyDictionary<string, string>> GetValuesAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Name,ProtectedValue,EncryptionScheme FROM plugin_configuration WHERE PluginId=$plugin;";
        Add(command, "$plugin", pluginId);
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            try { values[reader.GetString(0)] = protector.Unprotect(reader.GetString(1), reader.GetString(2)); }
            catch { /* A damaged or unreadable value is treated as not configured. */ }
        }
        return values;
    }

    public async Task SaveValuesAsync(string pluginId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var (name, value) in values)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            if (string.IsNullOrWhiteSpace(value))
            {
                command.CommandText = "DELETE FROM plugin_configuration WHERE PluginId=$plugin AND Name=$name;";
                Add(command, "$plugin", pluginId); Add(command, "$name", name);
            }
            else
            {
                var protectedValue = protector.Protect(value);
                command.CommandText = """
                    INSERT INTO plugin_configuration (PluginId,Name,ProtectedValue,EncryptionScheme,UpdatedAt)
                    VALUES ($plugin,$name,$value,$scheme,$updated)
                    ON CONFLICT(PluginId,Name) DO UPDATE SET ProtectedValue=excluded.ProtectedValue,
                        EncryptionScheme=excluded.EncryptionScheme,UpdatedAt=excluded.UpdatedAt;
                    """;
                Add(command, "$plugin", pluginId); Add(command, "$name", name); Add(command, "$value", protectedValue.Value);
                Add(command, "$scheme", protectedValue.Scheme); Add(command, "$updated", DateTimeOffset.UtcNow.ToString("O"));
            }
            if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }
}
