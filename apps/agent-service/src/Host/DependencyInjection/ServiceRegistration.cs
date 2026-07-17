using AgentService.Application.Contracts;
using AgentService.Host.GrpcServices;
using AgentService.Host.RestEndpoints;
using AgentService.Infrastructure.AgentFramework;
using AgentService.Infrastructure.ChangeIntelligence;
using AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;
using AgentService.Infrastructure.CodeAnalysis;
using AgentService.Infrastructure.CodeGraph;
using AgentService.Infrastructure.Changes;
using AgentService.Infrastructure.Orchestration;
using AgentService.Infrastructure.Mcp;
using AgentService.Infrastructure.Context;
using AgentService.Infrastructure.Persistence;
using AgentService.Infrastructure.Providers;
using AgentService.Infrastructure.Speech;
using AgentService.Infrastructure.Skills;
using AgentService.Infrastructure.Streaming;
using AgentService.Infrastructure.Telemetry;
using AgentService.Infrastructure.Tools;
using AgentService.Infrastructure.Workflow;
using AgentService.Infrastructure.VersionControl;
using AgentService.Infrastructure.Marketplace;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace AgentService.Host.DependencyInjection;

public static class ServiceRegistration
{
    public static IServiceCollection AddAgentServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // ── gRPC ──────────────────────────────────────────────────────────────
        services.AddGrpc();

        // ── REST / JSON ────────────────────────────────────────────────────────
        services.AddCors(opts =>
            opts.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
        // Enum 序列化為字串（"CopilotDefault" 而非 0），讓前端 kind === 'CopilotDefault' 正確比對
        services.ConfigureHttpJsonOptions(opts =>
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        // ── HttpClient（供 validate-key 端點代理外部 API 驗證）────────────────
        // CheckCertificateRevocationList = false：繞過企業環境 CRL/OCSP 查詢失敗問題
        services.AddHttpClient("key-validator", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("User-Agent", "Modern-Wingman/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                CheckCertificateRevocationList = false,
            });

        // ── Options / BYOK 設定綁定 ────────────────────────────────────────────
        services.Configure<AgentServiceOptions>(
            configuration.GetSection(AgentServiceOptions.SectionName));
        services.Configure<LlmTelemetryOptions>(
            configuration.GetSection(LlmTelemetryOptions.SectionName));

        // ── SQLite / EF Core ──────────────────────────────────────────────────
        var connectionString = DatabasePathResolver.ResolveConnectionString(configuration, environment);

        // 只用 Factory，避免同時 AddDbContext + AddDbContextFactory 造成 Scoped/Singleton 衝突
        // Scoped services 透過 factory.CreateDbContext() 取得獨立的 DbContext instance
        services.AddDbContextFactory<AppDbContext>(opts => opts.UseSqlite(connectionString));

        // ── Persistence ────────────────────────────────────────────────────────
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddSingleton<IRunRepository, RunRepository>();
        services.AddSingleton<IApprovalRepository, ApprovalRepository>();
        services.AddSingleton<IRunEventRepository, RunEventRepository>();
        services.AddSingleton<IContextSnapshotRepository, ContextSnapshotRepository>();
        services.AddSingleton<IRunStepRepository, RunStepRepository>();
        services.AddSingleton<IAgentScheduleStore, AgentScheduleStore>();
        services.AddSingleton<IVcsProfileRepository, VcsProfileRepository>();
        services.AddSingleton<IProviderSettingStore, ProviderSettingStore>();
        services.AddSingleton<IApiKeyStore, ApiKeyStore>();

        // ── Copilot CLI 生命週期（Singleton Hosted Service）───────────────────
        services.AddSingleton<CopilotClientService>();
        services.AddHostedService(sp => sp.GetRequiredService<CopilotClientService>());

        // ── BYOK Provider ─────────────────────────────────────────────────────
        services.AddSingleton<ProviderConfigResolver>();
        services.AddSingleton<IModelProviderService, ModelProviderService>();
        services.AddSingleton<ILlmTelemetryRecorder, LlmTelemetryRecorder>();
        services.AddSingleton<IAuditEventRecorder, AuditEventRecorder>();
        services.AddSingleton<ISensitiveDataRedactor, SensitiveDataRedactor>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IVcsCredentialProtectionMigration, VcsCredentialProtectionMigration>();
        services.AddSingleton<IToolCallTelemetry, ToolCallTelemetry>();
        services.AddSingleton<IAuditQueryService, AuditQueryService>();
        services.AddSingleton<IAuditMaintenanceService, AuditMaintenanceService>();
        services.AddSingleton<IAgentEvalService, AgentEvalService>();

        // ── Skills（中央 Skill Library → Wingman Agent，progressive disclosure）─
        services.AddSingleton<ISkillProvider, FileSystemSkillProvider>();
        services.AddSingleton<ISkillManifestLoader, YamlSkillManifestLoader>();
        services.AddSingleton<IRuntimeResolver, LocalRuntimeResolver>();
        services.AddSingleton<ISkillRiskProvider, SqliteSkillRiskProvider>();
        services.AddSingleton<IPluginCatalog, PluginCatalog>();
        services.AddSingleton<IRuntimeImportService, RuntimeImportService>();
        services.AddMarketplaceServices();

        // ── MAF Agent（Strategy：依 ProviderKind 選擇工廠，OCP）────────────────
        services.AddScoped<IAgentFactory, CopilotAgentFactory>();
        services.AddScoped<IAgentFactory, ByokAgentFactory>();
        services.AddScoped<WingmanChatAgent>();

        // ── Streaming / Orchestration ─────────────────────────────────────────
        services.AddSingleton<IRunEventBus, RunEventBus>();
        services.AddSingleton<CopilotEventBridge>();
        services.AddSingleton<IAgentPolicyProfileProvider, ConfigurationPolicyProfileProvider>();
        services.AddSingleton<IAgentPolicyEngine, DefaultAgentPolicyEngine>();
        services.AddSingleton<IApprovalCoordinator, ApprovalCoordinator>();
        services.AddSingleton<CopilotPermissionHandlerFactory>();

        // ── Provider-agnostic Tool Runtime ───────────────────────────────────
        services.AddSingleton<IProcessRunner, ManagedProcessRunner>();
        services.AddSingleton<IAgentTool, ReadFileTool>();
        services.AddSingleton<IAgentTool, SearchFilesTool>();
        services.AddSingleton<IAgentTool, RunCommandTool>();
        services.AddSingleton<ISkillScriptRunner, SkillScriptRunner>();
        services.AddSingleton<IAgentTool, RunSkillScriptTool>();
        services.AddSingleton<IAgentTool, ListDirectoryTool>();
        services.AddSingleton<IAgentTool, ReadFileRangeTool>();
        services.AddSingleton<IAgentTool, ApplyPatchTool>();
        services.AddSingleton<IAgentTool, DeleteFileTool>();
        services.AddSingleton<IAgentTool, RunBuildTool>();
        services.AddSingleton<IAgentTool, RunTestTool>();
        services.AddSingleton<IAgentTool, GitStatusTool>();
        services.AddSingleton<IAgentTool, GitDiffTool>();
        services.AddSingleton<IAgentTool, GitBranchTool>();
        services.AddSingleton<IAgentTool, SvnStatusTool>();
        services.AddSingleton<IAgentTool, SvnDiffTool>();
        services.AddSingleton<IAgentTool, GitCommitTool>();
        services.AddSingleton<IAgentTool, GitPushTool>();
        services.AddSingleton<IAgentTool, SvnCommitTool>();
        services.AddSingleton<IAgentTool, ReadSkillTool>();
        services.AddSingleton<IAgentTool, QueryCodeGraphTool>();
        services.AddSingleton<IAgentTool, AnalyzeImpactTool>();
        services.AddSingleton<IAgentTool, CallMcpTool>();
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<IToolRegistry>(sp => sp.GetRequiredService<ToolRegistry>());
        services.AddSingleton<IManagedToolRegistry>(sp => sp.GetRequiredService<ToolRegistry>());
        services.AddSingleton<IChangeSetService, FileSystemChangeSetService>();
        services.AddSingleton<IAgentSettingsStore, AgentSettingsStore>();
        services.AddSingleton<IVcsRuntimeResolver, VcsRuntimeResolver>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IProjectImportProgressStore, ProjectImportProgressStore>();
        services.AddSingleton<IVcsStateRepository, VcsStateRepository>();
        services.AddSingleton<IProtectedRefMatcher, ProtectedRefMatcher>();
        services.AddSingleton<ISvnClient, SvnClient>();
        services.AddSingleton<IShadowGitService, ShadowGitService>();
        services.AddSingleton<IRunWorkspaceManager, RunWorkspaceManager>();
        services.AddSingleton<IRunWorkspaceLifecycleService, RunWorkspaceLifecycleService>();
        services.AddSingleton<IMcpServerRepository, McpServerRepository>();
        services.AddSingleton<IMcpClientRuntime, McpClientRuntime>();
        services.AddSingleton<IMcpToolCatalog, McpToolCatalog>();
        services.AddSingleton<IContextAssembler, WorkspaceContextAssembler>();
        services.AddSingleton<IIdeSelectionContextService, IdeSelectionContextService>();
        services.AddSingleton<RunExecutionQueue>();
        services.AddSingleton<IRunExecutionQueue>(sp=>sp.GetRequiredService<RunExecutionQueue>());
        services.AddHostedService(sp=>sp.GetRequiredService<RunExecutionQueue>());
        services.AddHostedService<RunRecoveryService>();
        services.AddHostedService<WorkspaceRecoveryService>();
        services.AddHostedService<AgentScheduleDispatcher>();
        services.AddSingleton<IRunOrchestrator, RunOrchestrator>();
        services.AddSingleton<ISubagentCoordinator, SubagentCoordinator>();
        services.AddSingleton<IRunReplayGuard, RunReplayGuard>();
        services.AddSingleton<IAgentHookDispatcher, AgentHookDispatcher>();

        // ── WS3: 企業程式碼解析（分析器 Strategy / Neo4j / GraphRAG）──────────
        services.Configure<Neo4jOptions>(configuration.GetSection(Neo4jOptions.SectionName));
        services.Configure<Neo4jLifecycleOptions>(configuration.GetSection(Neo4jLifecycleOptions.SectionName));
        services.Configure<ProjectIndexOptimizationOptions>(
            configuration.GetSection(ProjectIndexOptimizationOptions.SectionName));
        services.AddHttpClient("neo4j-download", client => client.Timeout = TimeSpan.FromMinutes(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                CheckCertificateRevocationList = false,
            });
        services.AddHttpClient("mcp", client => client.Timeout = TimeSpan.FromMinutes(2));

        services.AddSingleton<ICodeAnalyzer, RoslynCodeAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, JavaCodeAnalyzer>();
        services.AddSingleton<ICodeGraphStore, Neo4jCodeGraphStore>();
        services.AddSingleton<Neo4jLifecycleService>();
        services.AddSingleton<IProjectRepository, ProjectRepository>();
        services.AddSingleton<IProjectIndexManifestStore, ProjectIndexManifestStore>();
        services.AddSingleton<ProjectIndexService>();
        services.AddHostedService<ProjectIndexWatcherService>();
        services.AddSingleton<IChangeIntentClassifier, DeterministicChangeIntentClassifier>();
        services.AddSingleton<IChangeBriefBuilder, ChangeBriefBuilder>();
        services.AddSingleton<IClarificationPlanner, DeterministicClarificationPlanner>();
        services.AddSingleton<IEvidencePackBuilder, BoundedEvidencePackBuilder>();
        services.AddSingleton<IChangeAnalysisSessionStore, ChangeAnalysisSessionSqliteStore>();
        services.AddSingleton<IChangeAnalysisSessionService, ChangeAnalysisSessionService>();
        services.AddSingleton<IChangeImplementationPlanBuilder, ChangeImplementationPlanBuilder>();
        services.AddSingleton<ProjectEvidencePlanner>();
        services.AddSingleton<IReadOnlyDatabaseQueryPlanValidator, StrictReadOnlyDatabaseQueryPlanValidator>();
        services.AddSingleton<IDatabaseRuntimeEvidenceRequestValidator, DatabaseRuntimeEvidenceRequestValidator>();
        services.AddSingleton<IDataArtifactAdapter, SqlDataArtifactAdapter>();
        services.AddSingleton<IDataArtifactAdapter, OrmDataArtifactAdapter>();
        services.AddSingleton<IDataSchemaExtractor, DataSchemaExtractor>();
        services.AddSingleton<IDomainGlossaryStore, DomainGlossarySqliteStore>();
        services.AddSingleton<IDatabaseRuntimeEvidenceCoordinator, McpDatabaseRuntimeEvidenceProvider>();
        services.AddSingleton<ProjectDataEvidencePlanner>();
        services.AddSingleton<ILlmCompletionService, CopilotCompletionService>();
        services.AddSingleton<GraphRagService>();
        services.AddSingleton<RepoMapService>();
        services.AddSingleton<AgentsMdGenerator>();
        services.AddSingleton<ImpactAnalysisService>();

        // ── Speech-to-Text：本機 whisper.cpp（企業內網離線優先）──────────────
        services.Configure<SpeechToTextOptions>(
            configuration.GetSection(SpeechToTextOptions.SectionName));
        services.AddHttpClient("speech-download", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(20);
                client.DefaultRequestHeaders.Add("User-Agent", "Modern-Wingman/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                CheckCertificateRevocationList = false,
            });
        services.AddSingleton<SpeechPathResolver>();
        services.AddSingleton<SpeechSettingsStore>();
        services.AddSingleton<ISpeechModelManager, SpeechModelManager>();
        services.AddSingleton<ISpeechToTextService, WhisperCliSpeechToTextService>();

        // ── WS4: Explore→Plan→Code→Verify 工作流 ──────────────────────────────
        services.AddSingleton<VerificationService>();
        services.AddSingleton<IWorkflowCodeExecutor, CopilotWorkflowCodeExecutor>();
        services.AddSingleton<ExplorePlanCodeVerifyWorkflow>();

        return services;
    }

    public static WebApplication MapAgentEndpoints(this WebApplication app)
    {
        app.UseCors();

        // ── REST ───────────────────────────────────────────────────────────────
        app.MapConversationEndpoints();
        app.MapProviderEndpoints();
        app.MapProjectEndpoints();
        app.MapProjectIndexDiagnosticsEndpoints();
        app.MapSpeechEndpoints();
        app.MapWorkflowEndpoints();
        app.MapApprovalEndpoints();
        app.MapRunEndpoints();
        app.MapVcsProfileEndpoints();
        app.MapVcsProtectedRefEndpoints();
        app.MapVcsRuntimeEndpoints();
        app.MapGitEndpoints();
        app.MapSvnEndpoints();
        app.MapMcpRuntimeEndpoints();
        app.MapAuditEndpoints();
        app.MapSkillRuntimeEndpoints();
        app.MapSkillRuntimeStatusEndpoints();
        app.MapContextEndpoints();
        app.MapProviderHealthEndpoints();
        app.MapAgentSettingsEndpoints();
        app.MapExtensionEndpoints();
        app.MapAgentScheduleEndpoints();
        app.MapMarketplaceEndpoints();
        app.MapDataIntelligenceEndpoints();

        // ── gRPC ───────────────────────────────────────────────────────────────
        app.MapGrpcService<HealthGrpcService>();
        app.MapGrpcService<RunGrpcService>();

        app.MapGet("/", () => "Modern Wingman Agent Service — REST on :5002 / gRPC on :5001");
        return app;
    }
}
