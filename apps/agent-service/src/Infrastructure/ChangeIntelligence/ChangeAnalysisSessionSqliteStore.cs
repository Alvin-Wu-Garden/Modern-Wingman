using System.Data;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.ChangeIntelligence;

/// <summary>以既有 App SQLite 保存 session；lazy schema 可相容既有安裝，不另引入資料庫。</summary>
public sealed class ChangeAnalysisSessionSqliteStore(IDbContextFactory<AppDbContext> factory) : IChangeAnalysisSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;

    public async Task<ChangeAnalysisSession?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var db = await factory.CreateDbContextAsync(ct);
        await EnsureSchemaAsync(db, ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT PayloadJson FROM project_change_analysis_sessions WHERE Id=$id LIMIT 1;";
        Add(command, "$id", sessionId);
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(ct);
        var payload = await command.ExecuteScalarAsync(ct) as string;
        return payload is null ? null : JsonSerializer.Deserialize<ChangeAnalysisSession>(payload, JsonOptions);
    }

    public async Task SaveAsync(ChangeAnalysisSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var db = await factory.CreateDbContextAsync(ct);
        await EnsureSchemaAsync(db, ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO project_change_analysis_sessions (Id,ProjectId,Status,UpdatedAt,PayloadJson)
            VALUES ($id,$project,$status,$updated,$payload)
            ON CONFLICT(Id) DO UPDATE SET
                ProjectId=excluded.ProjectId,
                Status=excluded.Status,
                UpdatedAt=excluded.UpdatedAt,
                PayloadJson=excluded.PayloadJson;
            """;
        Add(command, "$id", session.Id);
        Add(command, "$project", session.ProjectId);
        Add(command, "$status", session.Status.ToString());
        Add(command, "$updated", session.UpdatedAt.ToString("O"));
        Add(command, "$payload", JsonSerializer.Serialize(session, JsonOptions));
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(ct);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureSchemaAsync(AppDbContext db, CancellationToken ct)
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS project_change_analysis_sessions (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ProjectId TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PayloadJson TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_project_change_analysis_sessions_project_updated
                    ON project_change_analysis_sessions(ProjectId, UpdatedAt DESC);
                """;
            if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(ct);
            await command.ExecuteNonQueryAsync(ct);
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
