using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

public static class AgentSchemaMigrator
{
    public static async Task ApplyAsync(AppDbContext db, CancellationToken ct = default)
    {
        await EnsureColumnAsync(db, "Runs", "ProjectId", "TEXT", ct);
        await EnsureColumnAsync(db, "Runs", "Mode", "TEXT NOT NULL DEFAULT 'Plan'", ct);
        await EnsureColumnAsync(
            db,
            "Runs",
            "WorkspaceStrategy",
            "TEXT NOT NULL DEFAULT 'Direct'",
            ct);
        await EnsureColumnAsync(db, "Runs", "CheckpointId", "TEXT", ct);
        await EnsureColumnAsync(db, "Runs", "TraceId", "TEXT", ct);
        await EnsureColumnAsync(db,"Runs","ExecutionWorkspacePath","TEXT",ct);
        await EnsureColumnAsync(db,"Runs","Branch","TEXT",ct);
        await EnsureColumnAsync(db,"Runs","BaseRevision","TEXT",ct);
        await EnsureColumnAsync(db,"Runs","IncludeUncommittedChanges","INTEGER NOT NULL DEFAULT 1",ct);
        await EnsureColumnAsync(db,"Runs","ResolvedModelId","TEXT",ct);
        await EnsureColumnAsync(db,"Runs","ParentRunId","TEXT",ct);
        await EnsureColumnAsync(db,"Runs","AgentRole","TEXT",ct);
        await EnsureColumnAsync(db,"Projects","IndexManifestVersion","TEXT",ct);
        await EnsureColumnAsync(db,"Projects","PendingFileCount","INTEGER NOT NULL DEFAULT 0",ct);

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
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "agent_approvals" (
                "Id"              TEXT NOT NULL CONSTRAINT "PK_agent_approvals" PRIMARY KEY,
                "RunId"           TEXT NOT NULL,
                "Operation"       TEXT NOT NULL,
                "Target"          TEXT,
                "WorkingDirectory" TEXT,
                "Summary"         TEXT,
                "Capabilities"    INTEGER NOT NULL DEFAULT 0,
                "RiskLevel"       TEXT NOT NULL DEFAULT 'Low',
                "Status"          TEXT NOT NULL DEFAULT 'Pending',
                "Scope"           TEXT,
                "DecisionComment" TEXT,
                "CreatedAt"       TEXT NOT NULL DEFAULT '',
                "ResolvedAt"      TEXT
            );
            CREATE INDEX IF NOT EXISTS "IX_agent_approvals_RunId_Status_CreatedAt"
                ON "agent_approvals" ("RunId", "Status", "CreatedAt");
            """, ct);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "vcs_connection_profiles" (
                "Id" TEXT NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL UNIQUE,
                "VcsType" TEXT NOT NULL, "ServerType" TEXT NOT NULL, "BaseUrl" TEXT NOT NULL,
                "SslVerificationEnabled" INTEGER NOT NULL DEFAULT 1,
                "DefaultWorkspaceRoot" TEXT, "Enabled" INTEGER NOT NULL DEFAULT 1,
                "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "vcs_credentials" (
                "Id" TEXT NOT NULL PRIMARY KEY, "ConnectionProfileId" TEXT NOT NULL UNIQUE,
                "Username" TEXT NOT NULL, "SecretType" TEXT NOT NULL, "SecretValue" TEXT NOT NULL,
                "EncryptionScheme" TEXT NOT NULL DEFAULT 'plaintext', "UpdatedAt" TEXT NOT NULL,
                FOREIGN KEY("ConnectionProfileId") REFERENCES "vcs_connection_profiles"("Id") ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS "project_vcs_bindings" (
                "ProjectId" TEXT NOT NULL PRIMARY KEY, "VcsType" TEXT NOT NULL,
                "ConnectionProfileId" TEXT, "RepositoryUrl" TEXT, "RepositoryPath" TEXT,
                "CurrentRef" TEXT, "Revision" TEXT, "UpdatedAt" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "vcs_protected_refs" (
                "Id" TEXT NOT NULL PRIMARY KEY, "ProjectId" TEXT, "VcsType" TEXT NOT NULL,
                "Pattern" TEXT NOT NULL, "Enabled" INTEGER NOT NULL DEFAULT 1
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_vcs_protected_refs_ProjectId_VcsType_Pattern"
                ON "vcs_protected_refs" ("ProjectId", "VcsType", "Pattern");
            CREATE TABLE IF NOT EXISTS "vcs_operations" (
                "Id" TEXT NOT NULL PRIMARY KEY, "RunId" TEXT, "ProjectId" TEXT,
                "ConnectionProfileId" TEXT, "VcsType" TEXT NOT NULL, "Operation" TEXT NOT NULL,
                "TargetRef" TEXT, "Status" TEXT NOT NULL, "BeforeRevision" TEXT,
                "AfterRevision" TEXT, "ErrorSanitized" TEXT,
                "SslVerificationEnabled" INTEGER NOT NULL DEFAULT 1,
                "StartedAt" TEXT NOT NULL, "EndedAt" TEXT
            );
            CREATE INDEX IF NOT EXISTS "IX_vcs_operations_ProjectId_StartedAt"
                ON "vcs_operations" ("ProjectId", "StartedAt");
            CREATE TABLE IF NOT EXISTS "mcp_servers" (
                "id" INTEGER PRIMARY KEY AUTOINCREMENT, "name" TEXT NOT NULL UNIQUE,
                "transport" TEXT NOT NULL, "command" TEXT, "args" TEXT NOT NULL DEFAULT '[]',
                "url" TEXT, "env" TEXT NOT NULL DEFAULT '{{}}', "enabled" INTEGER NOT NULL DEFAULT 1,
                "created_at" INTEGER NOT NULL, "updated_at" INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "run_events" (
                "Sequence" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "RunId" TEXT NOT NULL, "EventType" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL DEFAULT '{{}}', "Timestamp" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_run_events_RunId_Sequence" ON "run_events" ("RunId","Sequence");
            CREATE TABLE IF NOT EXISTS "context_snapshots" (
                "Id" TEXT NOT NULL PRIMARY KEY, "RunId" TEXT,
                "OriginalHash" TEXT NOT NULL, "CompressedHash" TEXT NOT NULL,
                "OriginalCharacters" INTEGER NOT NULL, "CompressedCharacters" INTEGER NOT NULL,
                "SourcesJson" TEXT NOT NULL, "CompressedContent" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_context_snapshots_RunId_CreatedAt"
                ON "context_snapshots" ("RunId","CreatedAt");
            CREATE TABLE IF NOT EXISTS "run_steps" (
                "Id" TEXT NOT NULL PRIMARY KEY, "RunId" TEXT NOT NULL,
                "Phase" TEXT NOT NULL, "Attempt" INTEGER NOT NULL DEFAULT 1,
                "Status" TEXT NOT NULL, "CheckpointId" TEXT, "ErrorSanitized" TEXT,
                "StartedAt" TEXT NOT NULL, "EndedAt" TEXT
            );
            CREATE INDEX IF NOT EXISTS "IX_run_steps_RunId_StartedAt"
                ON "run_steps" ("RunId","StartedAt");
            CREATE TABLE IF NOT EXISTS "agent_settings" (
                "Key" TEXT NOT NULL PRIMARY KEY, "Value" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "agent_schedules" (
                "Id" TEXT NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL,
                "Task" TEXT NOT NULL, "WorkspacePath" TEXT NOT NULL,
                "ProjectId" TEXT, "ProviderProfileId" TEXT, "Mode" TEXT NOT NULL,
                "IntervalMinutes" INTEGER, "NextRunAt" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL DEFAULT 1, "LastRunId" TEXT,
                "LastError" TEXT, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_agent_schedules_Enabled_NextRunAt"
                ON "agent_schedules" ("Enabled", "NextRunAt");

            CREATE TABLE IF NOT EXISTS "marketplace_sources" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "Kind" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "Location" TEXT NOT NULL,
                "IsEnabled" INTEGER NOT NULL DEFAULT 1,
                "CreatedAt" TEXT NOT NULL,
                "LastSyncedAt" TEXT
            );
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
                "IsManualSource" INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY("SourceId") REFERENCES "marketplace_sources"("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_discovery_records_GitHubNodeId"
                ON "discovery_records" ("GitHubNodeId") WHERE "GitHubNodeId" IS NOT NULL;
            CREATE INDEX IF NOT EXISTS "IX_discovery_records_Search"
                ON "discovery_records" ("SuggestedKind", "Status", "DiscoveryScore" DESC, "LastSeenAt" DESC);
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
            CREATE INDEX IF NOT EXISTS "IX_discovery_score_snapshots_Record_Computed"
                ON "discovery_score_snapshots" ("DiscoveryRecordId", "ComputedAt" DESC);
            CREATE TABLE IF NOT EXISTS "artifact_score_snapshots" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ArtifactId" TEXT NOT NULL,
                "ProfileId" TEXT NOT NULL,
                "TotalScore" REAL NOT NULL,
                "ComponentsJson" TEXT NOT NULL,
                "EvidenceJson" TEXT NOT NULL,
                "ComputedAt" TEXT NOT NULL
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
            CREATE INDEX IF NOT EXISTS "IX_artifact_candidates_Kind_Status"
                ON "artifact_candidates" ("Kind", "Status", "CreatedAt" DESC);
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
            CREATE TABLE IF NOT EXISTS "artifact_versions" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ArtifactId" TEXT NOT NULL,
                "SourceRef" TEXT,
                "ResolvedCommitSha" TEXT,
                "ContentHash" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
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
            CREATE TABLE IF NOT EXISTS "plugin_installations" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ArtifactId" TEXT NOT NULL,
                "PluginId" TEXT NOT NULL,
                "Version" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL DEFAULT 0,
                "InstalledPath" TEXT NOT NULL,
                "InstalledAt" TEXT NOT NULL,
                FOREIGN KEY("ArtifactId") REFERENCES "artifacts"("Id") ON DELETE RESTRICT
            );
            CREATE TABLE IF NOT EXISTS "plugin_configuration" (
                "PluginId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "ProtectedValue" TEXT NOT NULL,
                "EncryptionScheme" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                PRIMARY KEY ("PluginId", "Name")
            );
            CREATE TABLE IF NOT EXISTS "marketplace_data_migrations" (
                "MigrationId" TEXT NOT NULL PRIMARY KEY,
                "CompletedAt" TEXT NOT NULL,
                "Summary" TEXT NOT NULL
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
            CREATE TABLE IF NOT EXISTS "marketplace_update_checks" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ArtifactId" TEXT NOT NULL,
                "SourceLocation" TEXT NOT NULL,
                "InstalledCommitSha" TEXT,
                "Status" TEXT NOT NULL,
                "AvailableCommitSha" TEXT,
                "Message" TEXT,
                "CheckedAt" TEXT NOT NULL,
                FOREIGN KEY("ArtifactId") REFERENCES "artifacts"("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_marketplace_update_checks_Artifact_Checked"
                ON "marketplace_update_checks" ("ArtifactId", "CheckedAt" DESC);
            CREATE TABLE IF NOT EXISTS "marketplace_activity_events" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "OperationId" TEXT NOT NULL,
                "EventType" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "ArtifactId" TEXT,
                "TargetId" TEXT,
                "Detail" TEXT,
                "OccurredAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_marketplace_activity_events_Occurred"
                ON "marketplace_activity_events" ("OccurredAt" DESC);
            INSERT OR IGNORE INTO "marketplace_sources"
                ("Id","Kind","DisplayName","Location","IsEnabled","CreatedAt")
            VALUES ('github-discovery','GitHubDiscovery','GitHub Discovery','https://api.github.com',1,
                strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """, ct);
        await EnsureColumnAsync(db,"vcs_connection_profiles","LastTestStatus","TEXT",ct);
        await EnsureColumnAsync(db,"vcs_connection_profiles","LastTestError","TEXT",ct);
        await EnsureColumnAsync(db,"vcs_connection_profiles","LastTestedAt","TEXT",ct);
        await EnsureColumnAsync(db,"agent_approvals","WorkingDirectory","TEXT",ct);
        await EnsureColumnAsync(db,"vcs_connection_profiles","CommitAuthorName","TEXT",ct);
        await EnsureColumnAsync(db,"vcs_connection_profiles","CommitAuthorEmail","TEXT",ct);
    }

    private static async Task EnsureColumnAsync(
        AppDbContext db,
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(ct);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var alterSql = (table, column) switch
        {
            ("Runs", "ProjectId") =>
                "ALTER TABLE \"Runs\" ADD COLUMN \"ProjectId\" TEXT",
            ("Runs", "Mode") =>
                "ALTER TABLE \"Runs\" ADD COLUMN \"Mode\" TEXT NOT NULL DEFAULT 'Plan'",
            ("Runs", "WorkspaceStrategy") =>
                "ALTER TABLE \"Runs\" ADD COLUMN \"WorkspaceStrategy\" TEXT NOT NULL DEFAULT 'Direct'",
            ("Runs", "CheckpointId") =>
                "ALTER TABLE \"Runs\" ADD COLUMN \"CheckpointId\" TEXT",
            ("Runs", "TraceId") =>
                "ALTER TABLE \"Runs\" ADD COLUMN \"TraceId\" TEXT",
            ("Runs", "ExecutionWorkspacePath") => "ALTER TABLE \"Runs\" ADD COLUMN \"ExecutionWorkspacePath\" TEXT",
            ("Runs", "Branch") => "ALTER TABLE \"Runs\" ADD COLUMN \"Branch\" TEXT",
            ("Runs", "BaseRevision") => "ALTER TABLE \"Runs\" ADD COLUMN \"BaseRevision\" TEXT",
            ("Runs", "IncludeUncommittedChanges") => "ALTER TABLE \"Runs\" ADD COLUMN \"IncludeUncommittedChanges\" INTEGER NOT NULL DEFAULT 1",
            ("Runs", "ResolvedModelId") => "ALTER TABLE \"Runs\" ADD COLUMN \"ResolvedModelId\" TEXT",
            ("Runs", "ParentRunId") => "ALTER TABLE \"Runs\" ADD COLUMN \"ParentRunId\" TEXT",
            ("Runs", "AgentRole") => "ALTER TABLE \"Runs\" ADD COLUMN \"AgentRole\" TEXT",
            ("Projects", "IndexManifestVersion") => "ALTER TABLE \"Projects\" ADD COLUMN \"IndexManifestVersion\" TEXT",
            ("Projects", "PendingFileCount") => "ALTER TABLE \"Projects\" ADD COLUMN \"PendingFileCount\" INTEGER NOT NULL DEFAULT 0",
            ("vcs_connection_profiles", "LastTestStatus") => "ALTER TABLE \"vcs_connection_profiles\" ADD COLUMN \"LastTestStatus\" TEXT",
            ("vcs_connection_profiles", "LastTestError") => "ALTER TABLE \"vcs_connection_profiles\" ADD COLUMN \"LastTestError\" TEXT",
            ("vcs_connection_profiles", "LastTestedAt") => "ALTER TABLE \"vcs_connection_profiles\" ADD COLUMN \"LastTestedAt\" TEXT",
            ("agent_approvals", "WorkingDirectory") => "ALTER TABLE \"agent_approvals\" ADD COLUMN \"WorkingDirectory\" TEXT",
            ("vcs_connection_profiles", "CommitAuthorName") => "ALTER TABLE \"vcs_connection_profiles\" ADD COLUMN \"CommitAuthorName\" TEXT",
            ("vcs_connection_profiles", "CommitAuthorEmail") => "ALTER TABLE \"vcs_connection_profiles\" ADD COLUMN \"CommitAuthorEmail\" TEXT",
            _ => throw new InvalidOperationException(
                $"Unsupported schema migration target: {table}.{column} ({definition})"),
        };
        await db.Database.ExecuteSqlRawAsync(alterSql, ct);
    }
}
