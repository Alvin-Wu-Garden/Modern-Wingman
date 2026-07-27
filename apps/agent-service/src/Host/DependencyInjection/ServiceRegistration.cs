using AgentService.Application.Contracts;
using AgentService.Host.RestEndpoints;
using AgentService.Infrastructure.AgentFramework;
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
        services.AddCors(opts =>
            opts.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
        // Enum 序列化為字串（"CopilotDefault" 而非 0），讓前端 kind === 'CopilotDefault' 正確比對
        services.ConfigureHttpJsonOptions(opts =>
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        // ── HttpClient（供 Provider 與 Skills GitHub PAT 後端驗證）─────────────
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
        services.AddScoped<WingmanChatAgent>();

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

        // ── GraphRAG V3：四種節點、九種關係、單一無 profile 模組 ─────────────
        services.AddSingleton<IProjectRepository, ProjectRepository>();
        services.AddSingleton<IProjectIndexManifestStore, ProjectIndexManifestStore>();
        services.AddSingleton<ILlmCompletionService, CopilotCompletionService>();
        services.AddGraphRagV3(configuration);

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

        return services;
    }

    public static WebApplication MapAgentEndpoints(this WebApplication app)
    {
        app.UseCors();

        // ── REST ───────────────────────────────────────────────────────────────
        app.MapConversationEndpoints();
        app.MapProviderEndpoints();
        app.MapProjectEndpoints();
        app.MapProjectDatabaseEndpoints();
        app.MapProjectIndexDiagnosticsEndpoints();
        app.MapSpeechEndpoints();
        app.MapVcsProfileEndpoints();
        app.MapMarketplaceEndpoints();

        app.MapGet("/", () => "Modern Wingman Agent Service — REST on :5002");
        return app;
    }
}
