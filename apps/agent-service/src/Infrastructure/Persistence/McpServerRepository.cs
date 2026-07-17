using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

public sealed class McpServerRepository(IDbContextFactory<AppDbContext> factory) : IMcpServerRepository
{
    public async Task<IReadOnlyList<McpServerDefinition>> ListEnabledAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT id,name,transport,command,args,url,env,enabled FROM mcp_servers WHERE enabled=1 ORDER BY name";
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<McpServerDefinition>();
        while (await reader.ReadAsync(ct)) result.Add(Map(reader));
        return result;
    }

    public async Task<McpServerDefinition?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT id,name,transport,command,args,url,env,enabled FROM mcp_servers WHERE id=$id";
        var parameter = command.CreateParameter(); parameter.ParameterName="$id"; parameter.Value=id; command.Parameters.Add(parameter);
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    private static McpServerDefinition Map(System.Data.Common.DbDataReader reader)
    {
        var transport = Enum.Parse<McpTransport>(reader.GetString(2), true);
        var args = JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [];
        var env = JsonSerializer.Deserialize<Dictionary<string,string>>(reader.GetString(6)) ?? [];
        return new(reader.GetInt64(0), reader.GetString(1), transport, reader.IsDBNull(3)?null:reader.GetString(3), args, reader.IsDBNull(5)?null:reader.GetString(5), env, reader.GetInt64(7)!=0);
    }
}
