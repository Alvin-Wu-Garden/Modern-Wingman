using AgentService.Application.Contracts;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Skills;

public sealed class SqliteSkillRiskProvider(IDbContextFactory<AppDbContext> factory) : ISkillRiskProvider
{
    public async Task<SkillRiskAssessment?> GetAsync(
        string skillName,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT risk_level, risk_notes
            FROM library_skills
            WHERE name = $name
            LIMIT 1
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = skillName;
        command.Parameters.Add(parameter);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            return new SkillRiskAssessment(
                reader.IsDBNull(0) ? "low" : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Older/local databases may not have the central library table yet.
            return null;
        }
    }
}
