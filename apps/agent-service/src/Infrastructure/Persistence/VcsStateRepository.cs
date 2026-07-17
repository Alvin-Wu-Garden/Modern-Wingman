using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

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

public sealed class VcsProtectedRefRecord
{
    public string Id { get; set; } = "";
    public string? ProjectId { get; set; }
    public string VcsType { get; set; } = "Git";
    public string Pattern { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class VcsOperationRecord
{
    public string Id { get; set; } = "";
    public string? RunId { get; set; }
    public string? ProjectId { get; set; }
    public string? ConnectionProfileId { get; set; }
    public string VcsType { get; set; } = "Git";
    public string Operation { get; set; } = "";
    public string? TargetRef { get; set; }
    public string Status { get; set; } = "Running";
    public string? BeforeRevision { get; set; }
    public string? AfterRevision { get; set; }
    public string? ErrorSanitized { get; set; }
    public bool SslVerificationEnabled { get; set; } = true;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public sealed class VcsStateRepository(IDbContextFactory<AppDbContext> factory) : IVcsStateRepository
{
    public async Task<ProjectVcsBinding?> GetBindingAsync(string projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.ProjectVcsBindings.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);
        return row is null ? null : new ProjectVcsBinding { ProjectId=row.ProjectId, VcsType=Enum.Parse<VcsType>(row.VcsType), ConnectionProfileId=row.ConnectionProfileId, RepositoryUrl=row.RepositoryUrl, RepositoryPath=row.RepositoryPath, CurrentRef=row.CurrentRef, Revision=row.Revision, UpdatedAt=row.UpdatedAt };
    }

    public async Task SaveBindingAsync(ProjectVcsBinding binding, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.ProjectVcsBindings.FindAsync([binding.ProjectId], ct);
        if (row is null) { row = new ProjectVcsBindingRecord { ProjectId=binding.ProjectId }; db.ProjectVcsBindings.Add(row); }
        row.VcsType=binding.VcsType.ToString(); row.ConnectionProfileId=binding.ConnectionProfileId; row.RepositoryUrl=binding.RepositoryUrl; row.RepositoryPath=binding.RepositoryPath; row.CurrentRef=binding.CurrentRef; row.Revision=binding.Revision; row.UpdatedAt=DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<VcsProtectedRef>> ListProtectedRefsAsync(VcsType type, string? projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var typeName = type.ToString();
        var rows = await db.VcsProtectedRefs.AsNoTracking().Where(x => x.VcsType == typeName && x.Enabled && (x.ProjectId == null || x.ProjectId == projectId)).ToListAsync(ct);
        return rows.Select(x => new VcsProtectedRef { Id=x.Id, ProjectId=x.ProjectId, VcsType=type, Pattern=x.Pattern, Enabled=x.Enabled }).ToList();
    }

    public async Task SaveProtectedRefAsync(VcsProtectedRef rule, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.VcsProtectedRefs.Update(new VcsProtectedRefRecord { Id=rule.Id, ProjectId=rule.ProjectId, VcsType=rule.VcsType.ToString(), Pattern=rule.Pattern, Enabled=rule.Enabled });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteProtectedRefAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.VcsProtectedRefs.FindAsync([id], ct);
        if (row is null) return;
        db.VcsProtectedRefs.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveOperationAsync(VcsOperation operation, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.VcsOperations.Update(new VcsOperationRecord { Id=operation.Id, RunId=operation.RunId, ProjectId=operation.ProjectId, ConnectionProfileId=operation.ConnectionProfileId, VcsType=operation.VcsType.ToString(), Operation=operation.Operation, TargetRef=operation.TargetRef, Status=operation.Status.ToString(), BeforeRevision=operation.BeforeRevision, AfterRevision=operation.AfterRevision, ErrorSanitized=operation.ErrorSanitized, SslVerificationEnabled=operation.SslVerificationEnabled, StartedAt=operation.StartedAt, EndedAt=operation.EndedAt });
        await db.SaveChangesAsync(ct);
    }
}
