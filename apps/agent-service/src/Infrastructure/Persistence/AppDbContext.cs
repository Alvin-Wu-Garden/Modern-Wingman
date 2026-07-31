using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// Modern Wingman 的本機設定資料庫。
/// 僅保存對話、專案、Provider 與唯讀專案匯入所需資料；
/// 已移除的 Agent Run、審批、工具呼叫與遙測不再建立資料表。
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<ProviderSettingEntity> ProviderSettings => Set<ProviderSettingEntity>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<ProjectDatabaseConfigurationRecord> ProjectDatabaseConfigurations =>
        Set<ProjectDatabaseConfigurationRecord>();
    public DbSet<VcsConnectionProfileRecord> VcsConnectionProfiles => Set<VcsConnectionProfileRecord>();
    public DbSet<VcsCredentialRecord> VcsCredentials => Set<VcsCredentialRecord>();
    public DbSet<ProjectVcsBindingRecord> ProjectVcsBindings => Set<ProjectVcsBindingRecord>();
    public DbSet<AtlassianConnectionRecord> AtlassianConnections => Set<AtlassianConnectionRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<ConversationEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.ProjectId).HasMaxLength(64);
            entity.Property(x => x.CreatedAt).HasConversion<string>();
            entity.Property(x => x.UpdatedAt).HasConversion<string>();
            entity.HasIndex(x => new { x.Scope, x.ProjectId, x.UpdatedAt });
        });

        builder.Entity<MessageEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Role).HasConversion<string>();
            entity.Property(x => x.CreatedAt).HasConversion<string>();
            entity.HasOne(x => x.Conversation)
                .WithMany(x => x.Messages)
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProviderSettingEntity>(entity =>
        {
            entity.HasKey(x => x.ProfileId);
            entity.Property(x => x.ProfileId).HasMaxLength(100);
            entity.Property(x => x.BaseUrl).HasMaxLength(500);
            entity.Property(x => x.ProtectedApiKey).HasMaxLength(2000);
            entity.Property(x => x.EncryptionScheme).HasMaxLength(50);
            entity.Property(x => x.UpdatedAt).HasConversion<string>();
            entity.HasIndex(x => x.SortOrder);
        });

        builder.Entity<ProjectRecord>(entity =>
        {
            entity.ToTable("Projects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.RootPath).HasMaxLength(500);
            entity.Property(x => x.IndexStatus).HasMaxLength(20);
            entity.Property(x => x.IndexedAt).HasConversion<string>();
            entity.Property(x => x.CreatedAt).HasConversion<string>();
        });

        builder.Entity<ProjectDatabaseConfigurationRecord>(entity =>
        {
            entity.ToTable("project_database_configurations");
            entity.HasKey(x => x.ProjectId);
            entity.Property(x => x.ProjectId).HasMaxLength(64);
            entity.Property(x => x.Provider).HasMaxLength(20);
            entity.Property(x => x.Server).HasMaxLength(300);
            entity.Property(x => x.DatabaseName).HasMaxLength(300);
            entity.Property(x => x.Authentication).HasMaxLength(30);
            entity.Property(x => x.Username).HasMaxLength(200);
            entity.Property(x => x.EncryptionScheme).HasMaxLength(50);
            entity.Property(x => x.SqlitePath).HasMaxLength(600);
            entity.Property(x => x.UpdatedAt).HasConversion<string>();
            entity.HasOne<ProjectRecord>()
                .WithOne()
                .HasForeignKey<ProjectDatabaseConfigurationRecord>(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<VcsConnectionProfileRecord>(entity =>
        {
            entity.ToTable("vcs_connection_profiles");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.CreatedAt).HasConversion<string>();
            entity.Property(x => x.UpdatedAt).HasConversion<string>();
        });

        builder.Entity<VcsCredentialRecord>(entity =>
        {
            entity.ToTable("vcs_credentials");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ConnectionProfileId).IsUnique();
            entity.Property(x => x.UpdatedAt).HasConversion<string>();
            entity.HasOne(x => x.Profile)
                .WithOne(x => x.Credential)
                .HasForeignKey<VcsCredentialRecord>(x => x.ConnectionProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProjectVcsBindingRecord>(entity =>
        {
            entity.ToTable("project_vcs_bindings");
            entity.HasKey(x => x.ProjectId);
            entity.Property(x => x.UpdatedAt).HasConversion<string>();
        });

        builder.Entity<AtlassianConnectionRecord>(entity =>
        {
            entity.ToTable("atlassian_connections");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ServiceType).IsUnique();
            entity.Property(x => x.ServiceType).HasMaxLength(10);
            entity.Property(x => x.BaseUrl).HasMaxLength(500);
            entity.Property(x => x.AuthType).HasMaxLength(10);
            entity.Property(x => x.Username).HasMaxLength(200);
            entity.Property(x => x.ProtectedSecret).HasMaxLength(2000);
            entity.Property(x => x.EncryptionScheme).HasMaxLength(50);
            entity.Property(x => x.ApiVersion).HasMaxLength(10);
            entity.Property(x => x.VerifiedDisplayName).HasMaxLength(200);
            entity.Property(x => x.VerifiedAt).HasConversion<string>();
            entity.Property(x => x.CreatedAt).HasConversion<string>();
            entity.Property(x => x.UpdatedAt).HasConversion<string>();
        });
    }
}
