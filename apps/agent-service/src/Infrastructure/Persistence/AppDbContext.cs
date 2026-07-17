using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<ProviderSettingEntity> ProviderSettings => Set<ProviderSettingEntity>();
    public DbSet<RunRecord> Runs => Set<RunRecord>();
    public DbSet<ApprovalRecord> AgentApprovals => Set<ApprovalRecord>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<AiProviderProfileRecord> AiProviderProfiles => Set<AiProviderProfileRecord>();
    public DbSet<AiModelRecord> AiModels => Set<AiModelRecord>();
    public DbSet<AiRequestLogRecord> AiRequestLogs => Set<AiRequestLogRecord>();
    public DbSet<AiRequestAttemptRecord> AiRequestAttempts => Set<AiRequestAttemptRecord>();
    public DbSet<AiToolCallLogRecord> AiToolCallLogs => Set<AiToolCallLogRecord>();
    public DbSet<AuditEventRecord> AuditEvents => Set<AuditEventRecord>();
    public DbSet<VcsConnectionProfileRecord> VcsConnectionProfiles => Set<VcsConnectionProfileRecord>();
    public DbSet<VcsCredentialRecord> VcsCredentials => Set<VcsCredentialRecord>();
    public DbSet<ProjectVcsBindingRecord> ProjectVcsBindings => Set<ProjectVcsBindingRecord>();
    public DbSet<VcsProtectedRefRecord> VcsProtectedRefs => Set<VcsProtectedRefRecord>();
    public DbSet<VcsOperationRecord> VcsOperations => Set<VcsOperationRecord>();
    public DbSet<RunEventRecord> RunEvents => Set<RunEventRecord>();
    public DbSet<ContextSnapshotRecord> ContextSnapshots => Set<ContextSnapshotRecord>();
    public DbSet<RunStepRecord> RunSteps => Set<RunStepRecord>();
    public DbSet<AgentSettingRecord> AgentSettings => Set<AgentSettingRecord>();
    public DbSet<AgentScheduleRecord> AgentSchedules => Set<AgentScheduleRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<ConversationEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.CreatedAt).HasConversion<string>();
            e.Property(x => x.UpdatedAt).HasConversion<string>();
        });

        builder.Entity<MessageEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasConversion<string>();
            e.Property(x => x.CreatedAt).HasConversion<string>();
            e.HasOne(x => x.Conversation)
             .WithMany(c => c.Messages)
             .HasForeignKey(x => x.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProviderSettingEntity>(e =>
        {
            e.HasKey(x => x.ProfileId);
            e.Property(x => x.ProfileId).HasMaxLength(100);
            e.Property(x => x.BaseUrl).HasMaxLength(500);
            e.Property(x => x.ApiKey).HasMaxLength(500);
            e.Property(x => x.UpdatedAt).HasConversion<string>();
            e.HasIndex(x => x.SortOrder);
        });

        builder.Entity<RunRecord>(e =>
        {
            e.ToTable("Runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.CheckpointId).HasMaxLength(64);
            e.Property(x => x.TraceId).HasMaxLength(64);
            e.Property(x=>x.ExecutionWorkspacePath).HasMaxLength(600);e.Property(x=>x.Branch).HasMaxLength(300);e.Property(x=>x.BaseRevision).HasMaxLength(100);
            e.Property(x=>x.ParentRunId).HasMaxLength(64);e.Property(x=>x.AgentRole).HasMaxLength(100);e.HasIndex(x=>x.ParentRunId);
            e.Property(x => x.CreatedAt).HasConversion<string>();
            e.Property(x => x.StartedAt).HasConversion<string>();
            e.Property(x => x.EndedAt).HasConversion<string>();
            e.HasIndex(x => x.SessionId);
        });

        builder.Entity<ApprovalRecord>(e =>
        {
            e.ToTable("agent_approvals");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.RunId).HasMaxLength(64);
            e.Property(x => x.Operation).HasMaxLength(200);
            e.Property(x => x.RiskLevel).HasMaxLength(30);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.Scope).HasMaxLength(30);
            e.Property(x => x.CreatedAt).HasConversion<string>();
            e.Property(x => x.ResolvedAt).HasConversion<string>();
            e.HasIndex(x => new { x.RunId, x.Status, x.CreatedAt });
        });
        builder.Entity<VcsConnectionProfileRecord>(e => { e.ToTable("vcs_connection_profiles"); e.HasKey(x=>x.Id); e.HasIndex(x=>x.Name).IsUnique(); e.Property(x=>x.CreatedAt).HasConversion<string>(); e.Property(x=>x.UpdatedAt).HasConversion<string>();e.Property(x=>x.LastTestedAt).HasConversion<string>(); });
        builder.Entity<VcsCredentialRecord>(e => { e.ToTable("vcs_credentials"); e.HasKey(x=>x.Id); e.HasIndex(x=>x.ConnectionProfileId).IsUnique(); e.Property(x=>x.UpdatedAt).HasConversion<string>(); e.HasOne(x=>x.Profile).WithOne(x=>x.Credential).HasForeignKey<VcsCredentialRecord>(x=>x.ConnectionProfileId).OnDelete(DeleteBehavior.Cascade); });
        builder.Entity<ProjectVcsBindingRecord>(e => { e.ToTable("project_vcs_bindings"); e.HasKey(x=>x.ProjectId); e.Property(x=>x.UpdatedAt).HasConversion<string>(); });
        builder.Entity<VcsProtectedRefRecord>(e => { e.ToTable("vcs_protected_refs"); e.HasKey(x=>x.Id); e.HasIndex(x=>new{x.ProjectId,x.VcsType,x.Pattern}).IsUnique(); });
        builder.Entity<VcsOperationRecord>(e => { e.ToTable("vcs_operations"); e.HasKey(x=>x.Id); e.Property(x=>x.StartedAt).HasConversion<string>(); e.Property(x=>x.EndedAt).HasConversion<string>(); e.HasIndex(x=>new{x.ProjectId,x.StartedAt}); });
        builder.Entity<RunEventRecord>(e=>{e.ToTable("run_events");e.HasKey(x=>x.Sequence);e.Property(x=>x.Timestamp).HasConversion<string>();e.HasIndex(x=>new{x.RunId,x.Sequence});});
        builder.Entity<ContextSnapshotRecord>(e=>{e.ToTable("context_snapshots");e.HasKey(x=>x.Id);e.Property(x=>x.CreatedAt).HasConversion<string>();e.HasIndex(x=>new{x.RunId,x.CreatedAt});});
        builder.Entity<RunStepRecord>(e=>{e.ToTable("run_steps");e.HasKey(x=>x.Id);e.Property(x=>x.StartedAt).HasConversion<string>();e.Property(x=>x.EndedAt).HasConversion<string>();e.HasIndex(x=>new{x.RunId,x.StartedAt});});
        builder.Entity<AgentSettingRecord>(e=>{e.ToTable("agent_settings");e.HasKey(x=>x.Key);e.Property(x=>x.UpdatedAt).HasConversion<string>();});
        builder.Entity<AgentScheduleRecord>(e=>{e.ToTable("agent_schedules");e.HasKey(x=>x.Id);e.Property(x=>x.NextRunAt).HasConversion<string>();e.Property(x=>x.CreatedAt).HasConversion<string>();e.Property(x=>x.UpdatedAt).HasConversion<string>();e.HasIndex(x=>new{x.Enabled,x.NextRunAt});});

        builder.Entity<ProjectRecord>(e =>
        {
            e.ToTable("Projects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.RootPath).HasMaxLength(500);
            e.Property(x => x.IndexStatus).HasMaxLength(20);
            e.Property(x => x.IndexedAt).HasConversion<string>();
            e.Property(x => x.CreatedAt).HasConversion<string>();
        });

        builder.Entity<AiProviderProfileRecord>(e =>
        {
            e.ToTable("ai_provider_profiles");
            e.HasKey(x => x.ProfileId);
            e.Property(x => x.ProfileId).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Kind).HasMaxLength(50);
            e.Property(x => x.ProviderType).HasMaxLength(50);
            e.Property(x => x.BaseUrlHost).HasMaxLength(200);
            e.Property(x => x.WireApi).HasMaxLength(50);
            e.Property(x => x.CreatedAt).HasConversion<string>();
            e.Property(x => x.UpdatedAt).HasConversion<string>();
        });

        builder.Entity<AiModelRecord>(e =>
        {
            e.ToTable("ai_models");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.ProviderProfileId).HasMaxLength(100);
            e.Property(x => x.ModelId).HasMaxLength(200);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.ModelFamily).HasMaxLength(80);
            e.Property(x => x.CreatedAt).HasConversion<string>();
            e.Property(x => x.UpdatedAt).HasConversion<string>();
            e.HasIndex(x => new { x.ProviderProfileId, x.ModelId }).IsUnique();
            e.HasOne(x => x.ProviderProfile)
             .WithMany(p => p.Models)
             .HasForeignKey(x => x.ProviderProfileId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AiRequestLogRecord>(e =>
        {
            e.ToTable("ai_request_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.TraceId).HasMaxLength(64);
            e.Property(x => x.ParentRequestId).HasMaxLength(64);
            e.Property(x => x.FeatureArea).HasMaxLength(80);
            e.Property(x => x.ConversationId).HasMaxLength(64);
            e.Property(x => x.MessageId).HasMaxLength(64);
            e.Property(x => x.ProjectId).HasMaxLength(64);
            e.Property(x => x.RunId).HasMaxLength(64);
            e.Property(x => x.ProviderProfileId).HasMaxLength(100);
            e.Property(x => x.RequestedModelRecordId).HasMaxLength(64);
            e.Property(x => x.ResolvedModelRecordId).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.TimeoutKind).HasMaxLength(50);
            e.Property(x => x.ErrorType).HasMaxLength(80);
            e.Property(x => x.ErrorCode).HasMaxLength(120);
            e.Property(x => x.StartedAt).HasConversion<string>();
            e.Property(x => x.FirstTokenAt).HasConversion<string>();
            e.Property(x => x.CompletedAt).HasConversion<string>();
            e.Property(x => x.CreatedAt).HasConversion<string>();
            e.HasIndex(x => x.TraceId);
            e.HasIndex(x => new { x.FeatureArea, x.Status, x.StartedAt });
            e.HasIndex(x => new { x.ProviderProfileId, x.StartedAt });
            e.HasIndex(x => new { x.Status, x.TimeoutKind, x.StartedAt });
            e.HasOne(x => x.ProviderProfile)
             .WithMany()
             .HasForeignKey(x => x.ProviderProfileId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.RequestedModel)
             .WithMany()
             .HasForeignKey(x => x.RequestedModelRecordId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ResolvedModel)
             .WithMany()
             .HasForeignKey(x => x.ResolvedModelRecordId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AiRequestAttemptRecord>(e =>
        {
            e.ToTable("ai_request_attempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.RequestLogId).HasMaxLength(64);
            e.Property(x => x.ProviderProfileId).HasMaxLength(100);
            e.Property(x => x.RequestedModelRecordId).HasMaxLength(64);
            e.Property(x => x.ResolvedModelRecordId).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.ErrorCode).HasMaxLength(120);
            e.Property(x => x.ErrorType).HasMaxLength(80);
            e.Property(x => x.TimeoutKind).HasMaxLength(50);
            e.Property(x => x.RetryReason).HasMaxLength(120);
            e.Property(x => x.StartedAt).HasConversion<string>();
            e.Property(x => x.FirstTokenAt).HasConversion<string>();
            e.Property(x => x.EndedAt).HasConversion<string>();
            e.HasIndex(x => new { x.RequestLogId, x.AttemptNo }).IsUnique();
            e.HasIndex(x => new { x.ProviderProfileId, x.Status, x.StartedAt });
            e.HasOne(x => x.RequestLog)
             .WithMany(r => r.Attempts)
             .HasForeignKey(x => x.RequestLogId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ProviderProfile)
             .WithMany()
             .HasForeignKey(x => x.ProviderProfileId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.RequestedModel)
             .WithMany()
             .HasForeignKey(x => x.RequestedModelRecordId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ResolvedModel)
             .WithMany()
             .HasForeignKey(x => x.ResolvedModelRecordId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AiToolCallLogRecord>(e =>
        {
            e.ToTable("ai_tool_call_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.RequestLogId).HasMaxLength(64);
            e.Property(x => x.ToolCallId).HasMaxLength(120);
            e.Property(x => x.ToolName).HasMaxLength(200);
            e.Property(x => x.ToolType).HasMaxLength(80);
            e.Property(x => x.McpServerId).HasMaxLength(120);
            e.Property(x => x.SkillId).HasMaxLength(120);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.ApprovalResult).HasMaxLength(50);
            e.Property(x => x.StartedAt).HasConversion<string>();
            e.Property(x => x.EndedAt).HasConversion<string>();
            e.HasIndex(x => new { x.RequestLogId, x.StartedAt });
            e.HasIndex(x => new { x.ToolType, x.ToolName });
            e.HasOne(x => x.RequestLog)
             .WithMany()
             .HasForeignKey(x => x.RequestLogId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditEventRecord>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.TraceId).HasMaxLength(64);
            e.Property(x => x.ActorType).HasMaxLength(50);
            e.Property(x => x.ActorId).HasMaxLength(120);
            e.Property(x => x.EventType).HasMaxLength(120);
            e.Property(x => x.TargetType).HasMaxLength(80);
            e.Property(x => x.TargetId).HasMaxLength(120);
            e.Property(x => x.Action).HasMaxLength(50);
            e.Property(x => x.Result).HasMaxLength(50);
            e.Property(x => x.MachineName).HasMaxLength(200);
            e.Property(x => x.AppVersion).HasMaxLength(80);
            e.Property(x => x.CreatedAt).HasConversion<string>();
            e.HasIndex(x => x.TraceId);
            e.HasIndex(x => new { x.EventType, x.CreatedAt });
            e.HasIndex(x => new { x.TargetType, x.TargetId, x.CreatedAt });
        });
    }
}
