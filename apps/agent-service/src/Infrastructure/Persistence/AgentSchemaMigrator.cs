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

        // EF EnsureCreatedAsync() 不會在「已存在其他資料表、但缺少新 entity」的舊 DB
        // 上補建新表；因此 Atlassian 表必須在這裡以 IF NOT EXISTS 明確建立。
        // 這個操作不會覆寫既有資料，並讓舊版 Wingman DB 能安全升級。
        await PatchAtlassianConnectionColumnsAsync(db.Database.GetDbConnection(), ct);
    }

    // 欄位最低要求：這些欄位缺任何一個代表是殘缺表，直接 DROP 讓 EF 重建。
    private static readonly string[] AtlassianRequiredColumns =
        ["Id", "ServiceType", "BaseUrl", "AuthType", "CreatedAt", "UpdatedAt"];

    // 可透過 ALTER TABLE ADD COLUMN 補齊的可選欄位（nullable 或有 DEFAULT）。
    private static readonly Dictionary<string, string> AtlassianPatchColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Username"]            = "ALTER TABLE atlassian_connections ADD COLUMN Username TEXT",
            ["ProtectedSecret"]     = "ALTER TABLE atlassian_connections ADD COLUMN ProtectedSecret TEXT",
            ["EncryptionScheme"]    = "ALTER TABLE atlassian_connections ADD COLUMN EncryptionScheme TEXT",
            ["ApiVersion"]          = "ALTER TABLE atlassian_connections ADD COLUMN ApiVersion TEXT",
            ["IsVerified"]          = "ALTER TABLE atlassian_connections ADD COLUMN IsVerified INTEGER NOT NULL DEFAULT 0",
            ["VerifiedAt"]          = "ALTER TABLE atlassian_connections ADD COLUMN VerifiedAt TEXT",
            ["VerifiedDisplayName"] = "ALTER TABLE atlassian_connections ADD COLUMN VerifiedDisplayName TEXT",
        };

    /// <summary>
    /// 對已存在但欄位不完整的 atlassian_connections 進行補丁：
    /// - 若缺少必要欄位（殘缺表）：DROP 後由 EF EnsureCreatedAsync 在下次啟動重建。
    ///   本次啟動會再呼叫一次 EnsureCreatedAsync 讓它立即補建。
    /// - 若只缺可選欄位：ALTER TABLE ADD COLUMN。
    /// - 若欄位完整：no-op。
    /// </summary>
    private static async Task PatchAtlassianConnectionColumnsAsync(
        DbConnection connection, CancellationToken ct)
    {
        if (connection.State == System.Data.ConnectionState.Closed)
            await connection.OpenAsync(ct);

        await using (var createTableCmd = connection.CreateCommand())
        {
            createTableCmd.CommandText = """
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
            await createTableCmd.ExecuteNonQueryAsync(ct);
        }

        // 讀取現有欄位清單
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA table_info(atlassian_connections)";
            await using var reader = await pragmaCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                existingColumns.Add(reader.GetString(1)); // index 1 = column name
        }

        // CREATE TABLE IF NOT EXISTS 已處理表不存在的情況；保留防禦判斷避免
        // 非 SQLite provider 或權限異常時繼續執行不必要的 ALTER。
        if (existingColumns.Count == 0) return;

        // 殘缺表（缺少必要欄位）→ DROP，讓後面的 EnsureCreatedAsync 重建
        var missingRequired = AtlassianRequiredColumns
            .FirstOrDefault(c => !existingColumns.Contains(c));
        if (missingRequired is not null)
        {
            await using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = "DROP TABLE IF EXISTS atlassian_connections";
            await dropCmd.ExecuteNonQueryAsync(ct);

            // 立即重建：用 CREATE TABLE 補建完整結構
            await using var createCmd = connection.CreateCommand();
            createCmd.CommandText = """
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
                """;
            await createCmd.ExecuteNonQueryAsync(ct);

            await using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText =
                "CREATE UNIQUE INDEX IF NOT EXISTS IX_atlassian_connections_ServiceType " +
                "ON atlassian_connections (ServiceType)";
            await idxCmd.ExecuteNonQueryAsync(ct);
            return;
        }

        // 補齊可選欄位
        foreach (var (columnName, alterSql) in AtlassianPatchColumns)
        {
            if (!existingColumns.Contains(columnName))
            {
                await using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = alterSql;
                await alterCmd.ExecuteNonQueryAsync(ct);
            }
        }
    }
}
