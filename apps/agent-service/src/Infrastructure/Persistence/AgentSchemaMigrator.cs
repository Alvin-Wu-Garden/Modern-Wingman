using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// 初始化 Modern Wingman 的本機 SQLite schema。
/// </summary>
/// <remarks>
/// 本機設定仍在開發階段，schema 不提供舊版欄位相容遷移。資料庫的
/// <c>PRAGMA user_version</c> 與目前版本不同時，會先刪除整個設定庫再由 EF Core
/// 與固定 DDL 重建，避免已移除的 <c>NOT NULL</c> 欄位繼續阻擋寫入。
/// </remarks>
public static class AgentSchemaMigrator
{
    // TurnId 與 ConversationTurns 是破壞式 schema 變更；舊設定庫會整庫重建。
    internal const int CurrentSchemaVersion = 2;

    /// <summary>套用目前唯一受支援的本機設定資料庫 schema。</summary>
    /// <param name="db">要初始化的 EF Core 資料庫內容。</param>
    /// <param name="ct">取消初始化作業的 Token。</param>
    /// <returns>代表非同步初始化作業的工作。</returns>
    public static async Task ApplyAsync(AppDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await RecreateIncompatibleDatabaseAsync(db, ct);
        await db.Database.EnsureCreatedAsync(ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "project_index_manifests" (
                "Version" TEXT NOT NULL PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "ManifestJson" TEXT NOT NULL,
                "StartedAt" TEXT NOT NULL,
                "CompletedAt" TEXT,
                "IsCurrent" INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY("ProjectId") REFERENCES "Projects"("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_project_index_manifests_Project_Started"
                ON "project_index_manifests" ("ProjectId", "StartedAt" DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_project_index_manifests_Current"
                ON "project_index_manifests" ("ProjectId") WHERE "IsCurrent" = 1;

            CREATE TABLE IF NOT EXISTS "project_index_files" (
                "ProjectId" TEXT NOT NULL,
                "GraphVersion" TEXT NOT NULL,
                "RelativePath" TEXT NOT NULL,
                "ContentHash" TEXT NOT NULL,
                PRIMARY KEY("ProjectId", "GraphVersion", "RelativePath"),
                FOREIGN KEY("ProjectId") REFERENCES "Projects"("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_project_index_files_Project_Version"
                ON "project_index_files" ("ProjectId", "GraphVersion");

            CREATE TABLE IF NOT EXISTS "discovery_records" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "SourceId" TEXT NOT NULL,
                "GitHubNodeId" TEXT,
                "CanonicalUrl" TEXT NOT NULL,
                "Owner" TEXT NOT NULL,
                "Repository" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Description" TEXT,
                "SuggestedKind" TEXT NOT NULL,
                "ClassificationConfidence" TEXT NOT NULL,
                "PrimaryCategory" TEXT NOT NULL,
                "SecondaryCategoriesJson" TEXT NOT NULL DEFAULT '[]',
                "TopicsJson" TEXT NOT NULL DEFAULT '[]',
                "License" TEXT,
                "IsArchived" INTEGER NOT NULL DEFAULT 0,
                "Stars" INTEGER NOT NULL DEFAULT 0,
                "Forks" INTEGER NOT NULL DEFAULT 0,
                "GitHubUpdatedAt" TEXT,
                "PushedAt" TEXT,
                "FirstSeenAt" TEXT NOT NULL,
                "LastSeenAt" TEXT NOT NULL,
                "ConsecutiveMissCount" INTEGER NOT NULL DEFAULT 0,
                "Status" TEXT NOT NULL,
                "MetadataFingerprint" TEXT NOT NULL,
                "DiscoveryScore" REAL NOT NULL DEFAULT 0,
                "DiscoveryScoreProfileId" TEXT NOT NULL,
                "ArtifactQualityScoreProfileId" TEXT,
                "ArtifactQualityScore" REAL,
                "IsFavorite" INTEGER NOT NULL DEFAULT 0,
                "IsManualSource" INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_discovery_records_GitHubNodeId"
                ON "discovery_records" ("GitHubNodeId") WHERE "GitHubNodeId" IS NOT NULL;
            CREATE INDEX IF NOT EXISTS "IX_discovery_records_Search"
                ON "discovery_records" ("SuggestedKind", "Status", "DiscoveryScore" DESC);

            CREATE TABLE IF NOT EXISTS "discovery_score_snapshots" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "DiscoveryRecordId" TEXT NOT NULL,
                "ScoreKind" TEXT NOT NULL,
                "ProfileId" TEXT NOT NULL,
                "TotalScore" REAL NOT NULL,
                "ComponentsJson" TEXT NOT NULL,
                "EvidenceJson" TEXT NOT NULL,
                "ComputedAt" TEXT NOT NULL,
                FOREIGN KEY("DiscoveryRecordId") REFERENCES "discovery_records"("Id") ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS "marketplace_sync_runs" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "Status" TEXT NOT NULL,
                "NewCount" INTEGER NOT NULL DEFAULT 0,
                "UpdatedCount" INTEGER NOT NULL DEFAULT 0,
                "UnchangedCount" INTEGER NOT NULL DEFAULT 0,
                "StaleCount" INTEGER NOT NULL DEFAULT 0,
                "PrunedCount" INTEGER NOT NULL DEFAULT 0,
                "SuccessfulQueries" INTEGER NOT NULL DEFAULT 0,
                "TotalQueries" INTEGER NOT NULL DEFAULT 0,
                "StartedAt" TEXT NOT NULL,
                "CompletedAt" TEXT,
                "Error" TEXT
            );

            CREATE TABLE IF NOT EXISTS "artifact_candidates" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "SourceLocation" TEXT NOT NULL,
                "ArtifactPath" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "ValidationProfileId" TEXT,
                "ValidationMessage" TEXT,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "artifacts" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "CandidateId" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "SnapshotPath" TEXT NOT NULL,
                "ContentHash" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "ValidationProfileId" TEXT,
                "ImportedAt" TEXT NOT NULL,
                FOREIGN KEY("CandidateId") REFERENCES "artifact_candidates"("Id") ON DELETE RESTRICT,
                UNIQUE("ContentHash", "Kind")
            );
            CREATE TABLE IF NOT EXISTS "artifact_score_snapshots" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ArtifactId" TEXT NOT NULL,
                "ProfileId" TEXT NOT NULL,
                "TotalScore" REAL NOT NULL,
                "ComponentsJson" TEXT NOT NULL,
                "EvidenceJson" TEXT NOT NULL,
                "ComputedAt" TEXT NOT NULL,
                FOREIGN KEY("ArtifactId") REFERENCES "artifacts"("Id") ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS "installability_results" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ArtifactId" TEXT NOT NULL,
                "TargetId" TEXT NOT NULL,
                "Scope" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "Reason" TEXT,
                "ComputedAt" TEXT NOT NULL,
                FOREIGN KEY("ArtifactId") REFERENCES "artifacts"("Id") ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS "deployments" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ArtifactId" TEXT NOT NULL,
                "TargetId" TEXT NOT NULL,
                "Scope" TEXT NOT NULL,
                "ProjectPath" TEXT,
                "TargetPath" TEXT NOT NULL,
                "DeployedHash" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                FOREIGN KEY("ArtifactId") REFERENCES "artifacts"("Id") ON DELETE RESTRICT
            );

            CREATE TABLE IF NOT EXISTS "jira_analysis_runs" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "WingmanProjectId" TEXT NOT NULL,
                "ConversationId" TEXT NULL,
                "JiraKey" TEXT NOT NULL,
                "JiraSummary" TEXT NOT NULL,
                "JiraUpdatedAt" TEXT NULL,
                "Status" TEXT NOT NULL,
                "ErrorCode" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_jira_analysis_runs_Project_Created"
                ON "jira_analysis_runs" ("WingmanProjectId", "CreatedAt" DESC);

            """, ct);
        await db.Database.ExecuteSqlRawAsync(
            $"PRAGMA user_version = {CurrentSchemaVersion};",
            ct);
    }

    /// <summary>刪除版本不相容的舊設定庫，讓後續流程建立單一乾淨 schema。</summary>
    private static async Task RecreateIncompatibleDatabaseAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State == System.Data.ConnectionState.Closed;
        if (openedHere)
            await connection.OpenAsync(cancellationToken);
        int schemaVersion;
        var userTables = new List<string>();
        var userViews = new List<string>();
        try
        {
            await using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version;";
            schemaVersion = Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(cancellationToken));

            await using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText = """
                SELECT type, name
                FROM sqlite_master
                WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%';
                """;
            await using (var reader = await tableCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var target = reader.GetString(0) == "view" ? userViews : userTables;
                    target.Add(reader.GetString(1));
                }
            }

            if (userTables.Count > 0 && schemaVersion != CurrentSchemaVersion)
            {
                await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;", cancellationToken);
                foreach (var view in userViews)
                    await ExecuteAsync(connection, $"DROP VIEW IF EXISTS {QuoteIdentifier(view)};", cancellationToken);
                foreach (var table in userTables)
                    await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {QuoteIdentifier(table)};", cancellationToken);
                await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
            }
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }

    private static async Task ExecuteAsync(
        System.Data.Common.DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";
}
