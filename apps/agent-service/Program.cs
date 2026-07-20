using AgentService.Application.Contracts;
using AgentService.Host.DependencyInjection;
using AgentService.Infrastructure.Persistence;
using AgentService.Infrastructure.Providers;
using AgentService.Infrastructure.Marketplace;
using AgentService.Infrastructure.CodeGraph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// The bundled local Neo4j password is user-scoped DPAPI data, never a committed secret.
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Neo4j:Password"] = LocalNeo4jCredentialStore.Resolve(builder.Configuration),
});

builder.Services.AddAgentServices(builder.Configuration, builder.Environment);

var app = builder.Build();

// 自動建立 SQLite 資料庫結構（首次啟動時）
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();

    // EnsureCreated 只在全新 DB 才會建表；舊 DB 已存在時不會補建新資料表。
    // 用 EnsureCreated + 手動 ALTER 確保新資料表（ProviderSettings）存在。
    db.Database.EnsureCreated();

    // 若 Desktop/Tauri 先建立了 skills/MCP tables，EF EnsureCreated 會因 DB 非空而略過。
    // 因此所有 AgentService 核心資料表也要以 IF NOT EXISTS 方式補齊。
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "Conversations" (
            "Id"                TEXT NOT NULL CONSTRAINT "PK_Conversations" PRIMARY KEY,
            "Title"             TEXT NOT NULL DEFAULT '新對話',
            "ProviderProfileId" TEXT,
            "CreatedAt"         TEXT NOT NULL DEFAULT '',
            "UpdatedAt"         TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS "Messages" (
            "Id"             TEXT NOT NULL CONSTRAINT "PK_Messages" PRIMARY KEY,
            "ConversationId" TEXT NOT NULL,
            "Role"           TEXT NOT NULL,
            "Content"        TEXT NOT NULL,
            "CreatedAt"      TEXT NOT NULL DEFAULT '',
            CONSTRAINT "FK_Messages_Conversations_ConversationId"
                FOREIGN KEY ("ConversationId") REFERENCES "Conversations" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_Messages_ConversationId" ON "Messages" ("ConversationId");
        """);

    // 確保 ProviderSettings 資料表存在（相容舊 DB）
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "ProviderSettings" (
            "ProfileId"  TEXT NOT NULL CONSTRAINT "PK_ProviderSettings" PRIMARY KEY,
            "BaseUrl"    TEXT,
            "ApiKey"     TEXT,
            "SortOrder"  INTEGER NOT NULL DEFAULT 0,
            "UpdatedAt"  TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS "IX_ProviderSettings_SortOrder" ON "ProviderSettings" ("SortOrder");
        """);

    // 確保 Runs 資料表存在（WS2：Run 狀態持久化，相容舊 DB）
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "Runs" (
            "Id"                TEXT NOT NULL CONSTRAINT "PK_Runs" PRIMARY KEY,
            "SessionId"         TEXT NOT NULL,
            "UserMessage"       TEXT NOT NULL DEFAULT '',
            "ProviderProfileId" TEXT,
            "WorkspacePath"     TEXT,
            "Status"            TEXT NOT NULL DEFAULT 'Created',
            "Error"             TEXT,
            "CreatedAt"         TEXT NOT NULL DEFAULT '',
            "StartedAt"         TEXT,
            "EndedAt"           TEXT
        );
        CREATE INDEX IF NOT EXISTS "IX_Runs_SessionId" ON "Runs" ("SessionId");
        """);

    // 確保 Projects 資料表存在（WS3.1：企業程式碼專案管理，相容舊 DB）
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "Projects" (
            "Id"          TEXT NOT NULL CONSTRAINT "PK_Projects" PRIMARY KEY,
            "Name"        TEXT NOT NULL,
            "RootPath"    TEXT NOT NULL,
            "Languages"   TEXT NOT NULL DEFAULT '',
            "IndexStatus" TEXT NOT NULL DEFAULT 'NotIndexed',
            "IndexedAt"   TEXT,
            "IndexError"  TEXT,
            "NodeCount"   INTEGER NOT NULL DEFAULT 0,
            "EdgeCount"   INTEGER NOT NULL DEFAULT 0,
            "CreatedAt"   TEXT NOT NULL DEFAULT ''
        );
        """);

    // 確保 AI telemetry / audit 資料表存在（企業級 provider/model 逾時與審計追蹤）
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "ai_provider_profiles" (
            "ProfileId"    TEXT NOT NULL CONSTRAINT "PK_ai_provider_profiles" PRIMARY KEY,
            "DisplayName"  TEXT NOT NULL DEFAULT '',
            "Kind"         TEXT NOT NULL DEFAULT '',
            "ProviderType" TEXT,
            "BaseUrlHost"  TEXT,
            "WireApi"      TEXT,
            "CreatedAt"    TEXT NOT NULL DEFAULT '',
            "UpdatedAt"    TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS "ai_models" (
            "Id"                TEXT NOT NULL CONSTRAINT "PK_ai_models" PRIMARY KEY,
            "ProviderProfileId" TEXT NOT NULL,
            "ModelId"           TEXT NOT NULL,
            "DisplayName"       TEXT NOT NULL DEFAULT '',
            "ModelFamily"       TEXT,
            "SupportsStreaming" INTEGER,
            "ContextWindow"     INTEGER,
            "CreatedAt"         TEXT NOT NULL DEFAULT '',
            "UpdatedAt"         TEXT NOT NULL DEFAULT ''
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ai_models_ProviderProfileId_ModelId"
            ON "ai_models" ("ProviderProfileId", "ModelId");

        CREATE TABLE IF NOT EXISTS "ai_request_logs" (
            "Id"                       TEXT NOT NULL CONSTRAINT "PK_ai_request_logs" PRIMARY KEY,
            "TraceId"                  TEXT NOT NULL,
            "ParentRequestId"          TEXT,
            "FeatureArea"              TEXT NOT NULL DEFAULT '',
            "ConversationId"           TEXT,
            "MessageId"                TEXT,
            "ProjectId"                TEXT,
            "RunId"                    TEXT,
            "ProviderProfileId"        TEXT NOT NULL,
            "RequestedModelRecordId"   TEXT,
            "ResolvedModelRecordId"    TEXT,
            "IsStreaming"              INTEGER NOT NULL DEFAULT 0,
            "Status"                   TEXT NOT NULL DEFAULT 'running',
            "TimeoutKind"              TEXT,
            "StartedAt"                TEXT NOT NULL DEFAULT '',
            "FirstTokenAt"             TEXT,
            "CompletedAt"              TEXT,
            "DurationMs"               INTEGER,
            "TimeToFirstTokenMs"       INTEGER,
            "TimeToLastByteMs"         INTEGER,
            "AvgInterTokenMs"          INTEGER,
            "TokensPerSecond"          REAL,
            "InputTokens"              INTEGER,
            "OutputTokens"             INTEGER,
            "TotalTokens"              INTEGER,
            "CachedInputTokens"        INTEGER,
            "ReasoningTokens"          INTEGER,
            "EstimatedCostUsd"         REAL,
            "PromptHash"               TEXT,
            "ResponseHash"             TEXT,
            "PromptPreviewRedacted"    TEXT,
            "ResponsePreviewRedacted"  TEXT,
            "ContentStored"            INTEGER NOT NULL DEFAULT 0,
            "ErrorType"                TEXT,
            "ErrorCode"                TEXT,
            "HttpStatus"               INTEGER,
            "ErrorMessageSanitized"    TEXT,
            "ProviderSnapshotJson"     TEXT,
            "ModelSnapshotJson"        TEXT,
            "MetadataJson"             TEXT,
            "CreatedAt"                TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS "IX_ai_request_logs_TraceId"
            ON "ai_request_logs" ("TraceId");
        CREATE INDEX IF NOT EXISTS "IX_ai_request_logs_FeatureArea_Status_StartedAt"
            ON "ai_request_logs" ("FeatureArea", "Status", "StartedAt");
        CREATE INDEX IF NOT EXISTS "IX_ai_request_logs_ProviderProfileId_StartedAt"
            ON "ai_request_logs" ("ProviderProfileId", "StartedAt");
        CREATE INDEX IF NOT EXISTS "IX_ai_request_logs_Status_TimeoutKind_StartedAt"
            ON "ai_request_logs" ("Status", "TimeoutKind", "StartedAt");

        CREATE TABLE IF NOT EXISTS "ai_request_attempts" (
            "Id"                       TEXT NOT NULL CONSTRAINT "PK_ai_request_attempts" PRIMARY KEY,
            "RequestLogId"             TEXT NOT NULL,
            "AttemptNo"                INTEGER NOT NULL DEFAULT 1,
            "ProviderProfileId"        TEXT NOT NULL,
            "RequestedModelRecordId"   TEXT,
            "ResolvedModelRecordId"    TEXT,
            "Status"                   TEXT NOT NULL DEFAULT 'running',
            "StartedAt"                TEXT NOT NULL DEFAULT '',
            "FirstTokenAt"             TEXT,
            "EndedAt"                  TEXT,
            "DurationMs"               INTEGER,
            "TimeToFirstTokenMs"       INTEGER,
            "HttpStatus"               INTEGER,
            "ErrorCode"                TEXT,
            "ErrorType"                TEXT,
            "TimeoutKind"              TEXT,
            "ErrorMessageSanitized"    TEXT,
            "RetryReason"              TEXT,
            "ProviderSnapshotJson"     TEXT,
            "ModelSnapshotJson"        TEXT,
            "MetadataJson"             TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ai_request_attempts_RequestLogId_AttemptNo"
            ON "ai_request_attempts" ("RequestLogId", "AttemptNo");
        CREATE INDEX IF NOT EXISTS "IX_ai_request_attempts_ProviderProfileId_Status_StartedAt"
            ON "ai_request_attempts" ("ProviderProfileId", "Status", "StartedAt");

        CREATE TABLE IF NOT EXISTS "ai_tool_call_logs" (
            "Id"                     TEXT NOT NULL CONSTRAINT "PK_ai_tool_call_logs" PRIMARY KEY,
            "RequestLogId"           TEXT NOT NULL,
            "ToolCallId"             TEXT,
            "ToolName"               TEXT NOT NULL DEFAULT '',
            "ToolType"               TEXT NOT NULL DEFAULT '',
            "McpServerId"            TEXT,
            "SkillId"                TEXT,
            "Status"                 TEXT NOT NULL DEFAULT 'running',
            "StartedAt"              TEXT NOT NULL DEFAULT '',
            "EndedAt"                TEXT,
            "DurationMs"             INTEGER,
            "InputHash"              TEXT,
            "OutputHash"             TEXT,
            "InputPreviewRedacted"   TEXT,
            "OutputPreviewRedacted"  TEXT,
            "ApprovalRequired"       INTEGER NOT NULL DEFAULT 0,
            "ApprovalResult"         TEXT,
            "ErrorMessageSanitized"  TEXT,
            "MetadataJson"           TEXT
        );
        CREATE INDEX IF NOT EXISTS "IX_ai_tool_call_logs_RequestLogId_StartedAt"
            ON "ai_tool_call_logs" ("RequestLogId", "StartedAt");
        CREATE INDEX IF NOT EXISTS "IX_ai_tool_call_logs_ToolType_ToolName"
            ON "ai_tool_call_logs" ("ToolType", "ToolName");

        CREATE TABLE IF NOT EXISTS "audit_events" (
            "Id"          TEXT NOT NULL CONSTRAINT "PK_audit_events" PRIMARY KEY,
            "TraceId"     TEXT,
            "ActorType"   TEXT NOT NULL DEFAULT 'system',
            "ActorId"     TEXT,
            "EventType"   TEXT NOT NULL DEFAULT '',
            "TargetType"  TEXT NOT NULL DEFAULT '',
            "TargetId"    TEXT,
            "Action"      TEXT NOT NULL DEFAULT '',
            "Result"      TEXT NOT NULL DEFAULT 'success',
            "IpAddress"   TEXT,
            "MachineName" TEXT,
            "AppVersion"  TEXT,
            "BeforeHash"  TEXT,
            "AfterHash"   TEXT,
            "DetailsJson" TEXT,
            "CreatedAt"   TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS "IX_audit_events_TraceId"
            ON "audit_events" ("TraceId");
        CREATE INDEX IF NOT EXISTS "IX_audit_events_EventType_CreatedAt"
            ON "audit_events" ("EventType", "CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_audit_events_TargetType_TargetId_CreatedAt"
            ON "audit_events" ("TargetType", "TargetId", "CreatedAt");
        """);

    await AgentSchemaMigrator.ApplyAsync(db);

    var marketplaceLegacyMigration = scope.ServiceProvider.GetRequiredService<MarketplaceLegacyMigration>();
    await marketplaceLegacyMigration.MigrateAsync();

    var credentialMigration = scope.ServiceProvider.GetRequiredService<IVcsCredentialProtectionMigration>();
    await credentialMigration.MigrateAsync();

    var auditMaintenance = scope.ServiceProvider.GetRequiredService<IAuditMaintenanceService>();
    await auditMaintenance.DeleteExpiredAsync();

    // 確保 ProviderSettings 有所有 appsettings 中的 profile 種子資料
    var options = scope.ServiceProvider.GetRequiredService<IOptions<AgentServiceOptions>>().Value;
    var profileIds = options.ModelProviders.Select(p => p.Id).ToList();
    var settingStore = scope.ServiceProvider.GetRequiredService<IProviderSettingStore>();
    await settingStore.EnsureSeedAsync(profileIds);
}

app.MapAgentEndpoints();

app.Run();

// Exposes the minimal-host entry point to the isolated in-memory host tests.
public partial class Program;
