using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// 建立不適合映射成 EF entity 的少數資料表。
/// 本次尚未發布前採乾淨 schema，不再保留逐欄 ALTER 與舊 Run／Plugin 相容邏輯。
/// </summary>
public static class AgentSchemaMigrator
{
    public static Task ApplyAsync(AppDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("""
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

            """, ct);
}
