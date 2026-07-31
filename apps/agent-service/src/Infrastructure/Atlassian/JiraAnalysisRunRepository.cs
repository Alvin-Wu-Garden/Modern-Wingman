using Microsoft.EntityFrameworkCore;
using AgentService.Infrastructure.Persistence;

namespace AgentService.Infrastructure.Atlassian;

/// <summary>
/// 管理 jira_analysis_runs 追蹤表的 CRUD 操作。
/// 使用 EF Core ExecuteSqlRawAsync 執行參數化 SQL，
/// 與 AgentSchemaMigrator 建立的 SQL DDL 表一致。
/// </summary>
public sealed class JiraAnalysisRunRepository(
    IDbContextFactory<AppDbContext> factory)
{
    public sealed record JiraAnalysisRun
    {
        public required string Id { get; init; }

        public required string WingmanProjectId { get; init; }

        public string? ConversationId { get; set; }

        public required string JiraKey { get; init; }

        public required string JiraSummary { get; init; }

        public string? JiraUpdatedAt { get; init; }

        // running | completed | failed | cancelled
        public required string Status { get; set; }

        public string? ErrorCode { get; set; }

        public required string CreatedAt { get; init; }

        public string? CompletedAt { get; set; }
    }

    public async Task<string> CreateAsync(
        string wingmanProjectId,
        string jiraKey,
        string jiraSummary,
        string? jiraUpdatedAt,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("o");

        await using var db = await factory.CreateDbContextAsync(ct);

        const string sql = """
            INSERT INTO "jira_analysis_runs"
              (
                "Id",
                "WingmanProjectId",
                "JiraKey",
                "JiraSummary",
                "JiraUpdatedAt",
                "Status",
                "CreatedAt"
              )
            VALUES
              ({0}, {1}, {2}, {3}, {4}, 'running', {5})
            """;

        object[] parameters =
        [
            id,
            wingmanProjectId,
            jiraKey,
            jiraSummary,
            jiraUpdatedAt ?? (object)DBNull.Value,
            now
        ];

        await db.Database.ExecuteSqlRawAsync(
            sql,
            parameters,
            ct);

        return id;
    }

    public async Task SetConversationAsync(
        string id,
        string conversationId,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        const string sql = """
            UPDATE "jira_analysis_runs"
            SET "ConversationId" = {0}
            WHERE "Id" = {1}
            """;

        object[] parameters =
        [
            conversationId,
            id
        ];

        await db.Database.ExecuteSqlRawAsync(
            sql,
            parameters,
            ct);
    }

    public async Task CompleteAsync(
        string id,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");

        await using var db = await factory.CreateDbContextAsync(ct);

        const string sql = """
            UPDATE "jira_analysis_runs"
            SET
                "Status" = 'completed',
                "CompletedAt" = {0}
            WHERE "Id" = {1}
            """;

        object[] parameters =
        [
            now,
            id
        ];

        await db.Database.ExecuteSqlRawAsync(
            sql,
            parameters,
            ct);
    }

    public async Task FailAsync(
        string id,
        string errorCode,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");

        await using var db = await factory.CreateDbContextAsync(ct);

        const string sql = """
            UPDATE "jira_analysis_runs"
            SET
                "Status" = 'failed',
                "ErrorCode" = {0},
                "CompletedAt" = {1}
            WHERE "Id" = {2}
            """;

        object[] parameters =
        [
            errorCode,
            now,
            id
        ];

        await db.Database.ExecuteSqlRawAsync(
            sql,
            parameters,
            ct);
    }
}
