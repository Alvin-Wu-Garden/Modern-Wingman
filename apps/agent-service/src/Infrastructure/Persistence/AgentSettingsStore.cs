using AgentService.Application.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

public sealed class AgentSettingRecord
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AgentSettingsStore(IDbContextFactory<AppDbContext> factory) : IAgentSettingsStore
{
    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AgentSettings.AsNoTracking().ToDictionaryAsync(row => row.Key, row => row.Value, ct);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AgentSettings.AsNoTracking()
            .Where(row => row.Key == key)
            .Select(row => row.Value)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AgentSettings.FindAsync([key], ct);
        if (row is null)
        {
            row = new AgentSettingRecord { Key = key };
            db.AgentSettings.Add(row);
        }
        row.Value = value;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
