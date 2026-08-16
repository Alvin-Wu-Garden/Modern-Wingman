using AgentService.Application.Contracts;
using AgentService.Application.Atlassian;
using AgentService.Host.RestEndpoints;
using AgentService.Infrastructure.Atlassian;
using AgentService.Infrastructure.AgentRuntime;
using AgentService.Infrastructure.AgentRuntime.Factories;
using AgentRuntimeService = AgentService.Infrastructure.AgentRuntime.AgentRuntime;
using AgentService.Infrastructure.Orchestration;
using AgentService.Infrastructure.Persistence;
using AgentService.Infrastructure.Providers;
using AgentService.Infrastructure.Speech;
using AgentService.Infrastructure.Skills;
using AgentService.Infrastructure.Telemetry;
using AgentService.Infrastructure.VersionControl;
using AgentService.Infrastructure.Marketplace;
using AgentService.Modules.GraphRAG;
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
        // ── REST / JSON ────────────────────────────────────────────────────────
        // 服務只供本機桌面 UI 使用；限制 Origin 可避免同機其他網站任意呼叫本機 Agent API。
        // 同時保留 Vite 開發來源與 Tauri production protocol，避免桌面與開發模式互相影響。
        var allowedOrigins = new[]
        {
            "http://127.0.0.1:4173",
            "http://localhost:4173",
            "tauri://localhost",
            "http://tauri.localhost",
        };
        services.AddCors(opts =>
            opts.AddDefaultPolicy(policy => policy
                .WithOrigins(allowedOrigins)
                // 僅允許目前 REST／SSE 客戶端實際使用的動詞與標頭，避免本機 API
                // 退化成可被任意跨來源網站呼叫的萬用 CORS 端點。
                .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                .WithHeaders("Accept", "Content-Type", "Cache-Control", "Last-Event-ID")));
        // Enum 序列化為字串（"CopilotDefault" 而非 0），讓前端 kind === 'CopilotDefault' 正確比對
        services.ConfigureHttpJsonOptions(opts =>
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        // ── HttpClient（供 Provider 與 Skills GitHub PAT 後端驗證）─────────────
        // 預設啟用憑證撤銷檢查；若企業 Proxy 導致外部驗證失敗，應在該環境處理
        // Proxy/CRL 設定，不在產品程式中永久關閉 TLS 安全檢查。
        services.AddHttpClient("key-validator", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("User-Agent", "Modern-Wingman/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                CheckCertificateRevocationList = true,
            });

        services.AddHttpClient("atlassian", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("User-Agent", "Modern-Wingman/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                CheckCertificateRevocationList = true,
            });

        // ── Options / BYOK 設定綁定 ────────────────────────────────────────────
        services.Configure<AgentServiceOptions>(
            configuration.GetSection(AgentServiceOptions.SectionName));
        services.AddOptions<ConversationRuntimeOptions>()
            .Bind(configuration.GetSection(ConversationRuntimeOptions.SectionName))
            .Validate(
                options => options.GeneralTimeoutSeconds is >= 30 and <= 1800,
                "ConversationRuntime:GeneralTimeoutSeconds 必須介於 30 到 1800 秒。")
            .Validate(
                options => options.ProjectAnalysisTimeoutSeconds is >= 30 and <= 1800,
                "ConversationRuntime:ProjectAnalysisTimeoutSeconds 必須介於 30 到 1800 秒。")
            .ValidateOnStart();

        // ── SQLite / EF Core ──────────────────────────────────────────────────
        var connectionString = DatabasePathResolver.ResolveConnectionString(configuration, environment);

        // 只用 Factory，避免同時 AddDbContext + AddDbContextFactory 造成 Scoped/Singleton 衝突
        // Scoped services 透過 factory.CreateDbContext() 取得獨立的 DbContext instance
        services.AddDbContextFactory<AppDbContext>(opts => opts.UseSqlite(connectionString));

        // ── Persistence ────────────────────────────────────────────────────────
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddSingleton<IProjectDatabaseConfigurationStore, ProjectDatabaseConfigurationStore>();
        services.AddSingleton<IVcsProfileRepository, VcsProfileRepository>();
        services.AddSingleton<IProviderSettingStore, ProviderSettingStore>();
        services.AddSingleton<IApiKeyStore, ApiKeyStore>();
        services.AddSingleton<IAtlassianConnectionRepository, AtlassianConnectionRepository>();
        services.AddSingleton<IJiraHttpClient, JiraHttpClient>();
        services.AddSingleton<JiraAnalysisRunRepository>();
        services.AddSingleton<JiraFeatureIdentifierExtractor>();
        services.AddSingleton<JiraGraphRagRetrievalService>();
        services.Configure<LocalJiraFileOptions>(
            configuration.GetSection(LocalJiraFileOptions.SectionName));
        services.AddSingleton<LocalJiraFileRepository>();

        // ── 對話執行 ─────────────────────────────────────────────────────────
        services.AddScoped<ConversationExecutionService>();
        services.AddScoped<ProjectConversationPreparationService>();

        // ── Copilot CLI 生命週期（Singleton Hosted Service）───────────────────
        services.AddSingleton<CopilotClientService>();
        services.AddSingleton<ICopilotCredentialRuntime>(
            sp => sp.GetRequiredService<CopilotClientService>());
        services.AddHostedService(sp => sp.GetRequiredService<CopilotClientService>());

        // ── BYOK Provider ─────────────────────────────────────────────────────
        services.AddSingleton<ProviderConfigResolver>();
        services.AddSingleton<IModelProviderService, ModelProviderService>();
        services.AddSingleton<IProviderApiKeyValidator, CopilotApiKeyValidator>();
        services.AddSingleton<IProviderApiKeyValidator, HttpProviderApiKeyValidator>();
        services.AddSingleton<IProviderCredentialService, ProviderCredentialService>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<ISensitiveDataRedactor, SensitiveDataRedactor>();

        // ── Skills：Modern Wingman 只讀取純指示內容，不執行 Skill script。──────
        services.AddSingleton<ISkillProvider, FileSystemSkillProvider>();
        services.AddMarketplaceServices();

        // ── MAF Agent（Strategy：依 ProviderKind 選擇工廠，OCP）────────────────
        services.AddScoped<IAgentFactory, CopilotAgentFactory>();
        services.AddScoped<IAgentFactory, ByokAgentFactory>();
        services.AddScoped<AgentRuntimeService>();

        // Copilot SDK 若要求工具權限，一律拒絕，確保聊天只產生文字回覆。
        services.AddSingleton<CopilotPermissionHandlerFactory>();

        // ── 專案匯入只保留 Git clone/update 與 SVN checkout/update 所需服務。───
        services.AddSingleton<IProcessRunner, ManagedProcessRunner>();
        services.AddSingleton<IVcsRuntimeResolver, VcsRuntimeResolver>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IProjectImportProgressStore, ProjectImportProgressStore>();
        services.AddSingleton<IVcsStateRepository, VcsStateRepository>();
        services.AddSingleton<ISvnClient, SvnClient>();
        services.AddSingleton<ProjectJobQueue>();
        services.AddSingleton<IProjectJobQueue>(
            provider => provider.GetRequiredService<ProjectJobQueue>());
        services.AddHostedService(
            provider => provider.GetRequiredService<ProjectJobQueue>());

        // ── GraphRAG V4：FBL 投資系統的節點、關係與 active graph 服務 ────────
        services.AddSingleton<IProjectRepository, ProjectRepository>();
        services.AddSingleton<IProjectIndexManifestStore, ProjectIndexManifestStore>();
        services.AddSingleton<ILlmCompletionService, CopilotCompletionService>();
        services.AddGraphRagV4(configuration);

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
                CheckCertificateRevocationList = true,
            });
        services.AddSingleton<SpeechPathResolver>();
        services.AddSingleton<SpeechSettingsStore>();
        services.AddSingleton<ISpeechModelManager, SpeechModelManager>();
        services.AddSingleton<ISpeechToTextService, WhisperCliSpeechToTextService>();

        return services;
    }

    public static WebApplication MapAgentEndpoints(this WebApplication app)
    {
        app.UseCors();

        // ── REST ───────────────────────────────────────────────────────────────
        app.MapGeneralConversationEndpoints();
        app.MapProjectConversationEndpoints();
        app.MapProviderEndpoints();
        app.MapProjectEndpoints();
        app.MapProjectDatabaseEndpoints();
        app.MapProjectIndexDiagnosticsEndpoints();
        app.MapSpeechEndpoints();
        app.MapVcsProfileEndpoints();
        app.MapMarketplaceEndpoints();
        app.MapAtlassianEndpoints();

        app.MapGet("/", () => "Modern Wingman Agent Service — REST on :5002");
        return app;
    }
}
