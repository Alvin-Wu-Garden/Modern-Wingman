using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// Host 啟動後，從 Modern Wingman SQLite manifest 還原每個專案的 active graphVersion，
/// 再恢復該版本的 Community 背景狀態。這個服務不重新索引、不掃描原始碼，也不切換版本。
/// </summary>
public sealed class GraphCommunityRecoveryService(
    IProjectRepository projects,
    IProjectIndexManifestStore manifests,
    INeo4jRuntime neo4jRuntime,
    GraphCommunityAiService communityAi,
    ILogger<GraphCommunityRecoveryService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 唯一負責啟動後 Neo4j 預熱與 Community 恢復。成功或失敗都不阻塞 Web Host；
    /// 沒有 active manifest 時不啟動 Neo4j。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 讓 HTTP pipeline 先完成啟動；避免 Hosted Service 與 endpoint mapping
            // 同時搶用磁碟與 CPU。這段取代舊 Neo4jWarmupService 的重複啟動責任。
            await Task.Delay(StartupDelay, stoppingToken);

            var indexedProjects = new List<(ProjectEntity Project, string GraphVersion)>();
            foreach (var project in await projects.ListAsync(stoppingToken))
            {
                stoppingToken.ThrowIfCancellationRequested();
                var current = await manifests.GetCurrentAsync(project.Id, stoppingToken);
                if (current is not null)
                    indexedProjects.Add((project, current.Version));
            }

            if (indexedProjects.Count == 0)
                return;

            logger.LogInformation(
                "偵測到 {Count} 個已索引專案，開始背景預熱 Neo4j 並恢復 Community 狀態。",
                indexedProjects.Count);
            if (!await neo4jRuntime.EnsureAvailableAsync(null, stoppingToken))
            {
                logger.LogWarning(
                    "無法恢復 Community 背景狀態；Neo4j 尚未可用。Reason={Reason}",
                    neo4jRuntime.LastError ?? "unknown");
                return;
            }

            logger.LogInformation("Neo4j 背景預熱完成，開始恢復 Community 狀態。");

            foreach (var item in indexedProjects)
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    await communityAi.ResumeAsync(
                        item.Project.Id,
                        item.GraphVersion,
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "Community 背景狀態恢復失敗；既有結構圖仍可查詢。ProjectId={ProjectId}, ExceptionType={ExceptionType}",
                        item.Project.Id,
                        exception.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host 正常關閉，不建立失敗狀態。
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Community 啟動恢復程序失敗；不影響 Agent Service 啟動。ExceptionType={ExceptionType}",
                exception.GetType().Name);
        }
    }
}
