namespace AgentService.Application.Contracts;

/// <summary>專案索引可連線的資料庫種類；目前刻意只支援兩種。</summary>
public enum ProjectDatabaseProvider
{
    SqlServer,
    Sqlite,
}

/// <summary>SQL Server 的兩種登入方式。</summary>
public enum SqlServerAuthentication
{
    SqlPassword,
    IntegratedSecurity,
}

/// <summary>
/// 專案資料庫設定的安全領域模型。
/// Password 只會在後端記憶體短暫存在，不會由 GET API 回傳。
/// </summary>
public sealed record ProjectDatabaseConfiguration(
    string ProjectId,
    ProjectDatabaseProvider Provider,
    string? Server,
    int? Port,
    string? DatabaseName,
    SqlServerAuthentication? Authentication,
    string? Username,
    string? Password,
    bool HasPassword,
    bool TrustServerCertificate,
    string? SqlitePath,
    DateTimeOffset UpdatedAt);

/// <summary>每個專案只保存一組主要資料庫設定。</summary>
public interface IProjectDatabaseConfigurationStore
{
    /// <summary>
    /// 取得專案設定。一般 API 必須使用 includePassword=false；
    /// 只有建立索引連線字串的後端流程可以要求解密密碼。
    /// </summary>
    Task<ProjectDatabaseConfiguration?> GetAsync(
        string projectId,
        bool includePassword = false,
        CancellationToken ct = default);

    /// <summary>
    /// 新增或更新設定。SQL 密碼為 null 時保留既有密碼；
    /// 切換為 Integrated Security 或 SQLite 時會清除既有密文。
    /// </summary>
    Task SaveAsync(
        ProjectDatabaseConfiguration configuration,
        CancellationToken ct = default);

    /// <summary>刪除專案設定與其密文，不接觸外部資料庫。</summary>
    Task DeleteAsync(string projectId, CancellationToken ct = default);
}
