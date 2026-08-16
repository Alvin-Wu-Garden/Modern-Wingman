using AgentService.Application.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// AgentService 啟動後在背景預熱 Neo4j，讓使用者首次開啟知識圖譜時不需要等待冷啟動。
///
/// 設計原則：
/// - 採 fire-and-forget 背景執行，不阻塞 HTTP pipeline 初始化。
/// - 短暫 delay 確保 ASP.NET Core 完成 endpoint mapping 再開始啟動 Neo4j（避免搶佔 port）。
/// - 僅當至少存在一個已完成索引的專案時才執行預熱，全新安裝不浪費資源下載 Neo4j。
/// - 失敗只記錄 log；<see cref="INeo4jRuntime.EnsureAvailableAsync"/> 已有重試保護，
///   使用者開啟知識圖譜時仍可 on-demand 再試。
/// </summary>
internal sealed class Neo4jWarmupService(
    INeo4jRuntime neo4jRuntime,
    IProjectRepository projectRepo,
    ILogger<Neo4jWarmupService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 讓 App 先完成 HTTP pipeline 初始化再開始預熱，避免與首次請求搶資源。
        await Task.Delay(StartupDelay, stoppingToken);

        IReadOnlyList<Domain.Models.ProjectEntity> projects;
        try
        {
            projects = await projectRepo.ListAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Neo4jWarmupService：無法讀取專案清單，略過預熱。");
            return;
        }

        // 只有「至少一個已完成索引的專案」才需要預熱；全新安裝不啟動 Neo4j。
        if (!projects.Any(p => p.IndexManifestVersion is not null))
        {
            logger.LogInformation("Neo4jWarmupService：無已索引專案，略過預熱。");
            return;
        }

        logger.LogInformation("Neo4jWarmupService：偵測到 {Count} 個已索引專案，開始背景預熱 Neo4j…",
            projects.Count(p => p.IndexManifestVersion is not null));

        try
        {
            var ok = await neo4jRuntime.EnsureAvailableAsync(
                progress: null,
                cancellationToken: stoppingToken);

            if (ok)
                logger.LogInformation("Neo4jWarmupService：Neo4j 預熱完成，知識圖譜已可立即使用。");
            else
                logger.LogWarning("Neo4jWarmupService：Neo4j 預熱未成功（{Status}），使用者開啟知識圖譜時將重試。",
                    neo4jRuntime.Status);
        }
        catch (OperationCanceledException)
        {
            // 服務正常關機，不視為錯誤。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Neo4jWarmupService：預熱過程發生未預期錯誤，使用者開啟知識圖譜時將重試。");
        }
    }
}
