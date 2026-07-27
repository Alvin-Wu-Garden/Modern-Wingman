using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// 專案與遠端版本控制來源的持久化記錄。
/// 只保存後續 update 所需資訊，不包含 commit、push、branch 或工作樹狀態。
/// </summary>
public sealed class ProjectVcsBindingRecord
{
    public string ProjectId { get; set; } = "";
    public string VcsType { get; set; } = "Git";
    public string? ConnectionProfileId { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? RepositoryPath { get; set; }
    public string? CurrentRef { get; set; }
    public string? Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 儲存遠端匯入專案的來源資訊，供後續 Git pull 或 SVN update 使用。
/// 不記錄 commit、push 或保護分支等已移除的寫入流程。
/// </summary>
public sealed class VcsStateRepository(IDbContextFactory<AppDbContext> factory) : IVcsStateRepository
{
    /// <summary>取得專案的遠端來源；本機資料夾專案或尚未綁定時回傳 null。</summary>
    public async Task<ProjectVcsBinding?> GetBindingAsync(string projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.ProjectVcsBindings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);
        return row is null
            ? null
            : new ProjectVcsBinding
            {
                ProjectId = row.ProjectId,
                VcsType = Enum.Parse<VcsType>(row.VcsType),
                ConnectionProfileId = row.ConnectionProfileId,
                RepositoryUrl = row.RepositoryUrl,
                RepositoryPath = row.RepositoryPath,
                CurrentRef = row.CurrentRef,
                Revision = row.Revision,
                UpdatedAt = row.UpdatedAt,
            };
    }

    /// <summary>新增或覆寫專案來源，供 Git pull 或 SVN update 使用。</summary>
    public async Task SaveBindingAsync(ProjectVcsBinding binding, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.ProjectVcsBindings.FindAsync([binding.ProjectId], ct);
        if (row is null)
        {
            row = new ProjectVcsBindingRecord { ProjectId = binding.ProjectId };
            db.ProjectVcsBindings.Add(row);
        }

        row.VcsType = binding.VcsType.ToString();
        row.ConnectionProfileId = binding.ConnectionProfileId;
        row.RepositoryUrl = binding.RepositoryUrl;
        row.RepositoryPath = binding.RepositoryPath;
        row.CurrentRef = binding.CurrentRef;
        row.Revision = binding.Revision;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
