using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// 建立不適合映射成 EF entity 的少數資料表，並對已存在的舊 DB 補齊欄位。
/// EnsureCreatedAsync() 只在 DB 檔案不存在時建立 schema；本 Migrator 負責
/// 讓已存在的舊 DB 也能跟上新欄位定義。
/// </summary>
public static class AgentSchemaMigrator
{
    public static async Task ApplyAsync(AppDbContext db, CancellationToken ct = default)
    {
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

        await EnsureProjectDatabaseConfigurationSchemaAsync(db.Database.GetDbConnection(), ct);

        // 目前尚未發布，不需要多版本 schema migration；只需確保目前版本的
        // Atlassian 連線表存在，且不覆寫既有本機資料。
        await EnsureAtlassianConnectionTableAsync(db.Database.GetDbConnection(), ct);
    }

    /// <summary>建立目前版本唯一需要額外確認的 Atlassian 連線表。</summary>
    private static async Task EnsureAtlassianConnectionTableAsync(
        DbConnection connection,
        CancellationToken ct)
    {
        if (connection.State == System.Data.ConnectionState.Closed)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS atlassian_connections (
                Id TEXT NOT NULL PRIMARY KEY,
                ServiceType TEXT NOT NULL,
                BaseUrl TEXT NOT NULL,
                AuthType TEXT NOT NULL,
                Username TEXT,
                ProtectedSecret TEXT,
                EncryptionScheme TEXT,
                ApiVersion TEXT,
                IsVerified INTEGER NOT NULL DEFAULT 0,
                VerifiedAt TEXT,
                VerifiedDisplayName TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_atlassian_connections_ServiceType
                ON atlassian_connections (ServiceType);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 將舊版每專案單列設定表升級為 Project＋Provider 複合鍵。
    /// 這是 Modern Wingman 自身的 SQLite 設定庫，不是使用者選擇的外部資料庫；
    /// 升級只搬移既有設定資料，不執行任何外部資料庫操作。
    /// </summary>
    private static async Task EnsureProjectDatabaseConfigurationSchemaAsync(
        DbConnection connection,
        CancellationToken ct)
    {
        if (connection.State == System.Data.ConnectionState.Closed)
        {
            await connection.OpenAsync(ct);
        }

        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(\"project_database_configurations\");";
        var columns = new List<(string Name, int PrimaryKeyOrder)>();
        await using (var reader = await inspect.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                columns.Add((reader.GetString(1), reader.GetInt32(5)));
            }
        }

        if (columns.Count == 0)
        {
            // 以防 EnsureCreated 尚未建立 DbSet 對應表，這裡仍補建目前的複合鍵 schema。
            await using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS "project_database_configurations" (
                    "ProjectId" TEXT NOT NULL,
                    "Provider" TEXT NOT NULL,
                    "Server" TEXT,
                    "Port" INTEGER,
                    "DatabaseName" TEXT,
                    "Authentication" TEXT,
                    "Username" TEXT,
                    "ProtectedPassword" TEXT,
                    "EncryptionScheme" TEXT,
                    "TrustServerCertificate" INTEGER NOT NULL DEFAULT 1,
                    "SqlitePath" TEXT,
                    "UpdatedAt" TEXT NOT NULL,
                    PRIMARY KEY ("ProjectId", "Provider"),
                    FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE
                );
                """;
            await create.ExecuteNonQueryAsync(ct);
            return;
        }

        var isCompositeKey = columns.Any(column =>
                column.Name.Equals("ProjectId", StringComparison.OrdinalIgnoreCase) &&
                column.PrimaryKeyOrder > 0)
            && columns.Any(column =>
                column.Name.Equals("Provider", StringComparison.OrdinalIgnoreCase) &&
                column.PrimaryKeyOrder > 0);
        if (isCompositeKey)
        {
            return;
        }

        var hasProviderColumn = columns.Any(column =>
            column.Name.Equals("Provider", StringComparison.OrdinalIgnoreCase));

        await using var transaction = await connection.BeginTransactionAsync(ct);
        await ExecuteSchemaCommandAsync(connection, transaction, "ALTER TABLE \"project_database_configurations\" RENAME TO \"project_database_configurations_legacy\";", ct);
        await ExecuteSchemaCommandAsync(connection, transaction, """
            CREATE TABLE "project_database_configurations" (
                "ProjectId" TEXT NOT NULL,
                "Provider" TEXT NOT NULL,
                "Server" TEXT,
                "Port" INTEGER,
                "DatabaseName" TEXT,
                "Authentication" TEXT,
                "Username" TEXT,
                "ProtectedPassword" TEXT,
                "EncryptionScheme" TEXT,
                "TrustServerCertificate" INTEGER NOT NULL DEFAULT 1,
                "SqlitePath" TEXT,
                "UpdatedAt" TEXT NOT NULL,
                PRIMARY KEY ("ProjectId", "Provider"),
                FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE
            );
            """, ct);
        var providerExpression = hasProviderColumn
            ? "COALESCE(NULLIF(\"Provider\", ''), 'SqlServer')"
            : "'SqlServer'";
        await ExecuteSchemaCommandAsync(connection, transaction, $"""
            INSERT INTO "project_database_configurations" (
                "ProjectId", "Provider", "Server", "Port", "DatabaseName", "Authentication",
                "Username", "ProtectedPassword", "EncryptionScheme", "TrustServerCertificate",
                "SqlitePath", "UpdatedAt")
            SELECT "ProjectId", {providerExpression}, "Server", "Port",
                   "DatabaseName", "Authentication", "Username", "ProtectedPassword",
                   "EncryptionScheme", "TrustServerCertificate", "SqlitePath", "UpdatedAt"
            FROM "project_database_configurations_legacy";
            """, ct);
        await ExecuteSchemaCommandAsync(connection, transaction, "DROP TABLE \"project_database_configurations_legacy\";", ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>在指定交易中執行內部設定庫的固定 schema 指令。</summary>
    private static async Task ExecuteSchemaCommandAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
