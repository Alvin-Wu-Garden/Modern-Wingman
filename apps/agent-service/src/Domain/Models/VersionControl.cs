namespace AgentService.Domain.Models;

public enum VcsType { Git, Svn }
public enum VcsServerType { BitbucketServer, Svn }
public enum VcsSecretType { AccessToken, Password }

public sealed class ProjectVcsBinding
{
    public required string ProjectId { get; init; }
    public VcsType VcsType { get; set; }
    public string? ConnectionProfileId { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? RepositoryPath { get; set; }
    public string? CurrentRef { get; set; }
    public string? Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class VcsConnectionProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; set; }
    public VcsType VcsType { get; set; }
    public VcsServerType ServerType { get; set; }
    public required string BaseUrl { get; set; }
    public bool SslVerificationEnabled { get; set; } = true;
    public string? DefaultWorkspaceRoot { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Username { get; set; }
    public VcsSecretType? SecretType { get; set; }
    public string? SecretValue { get; set; }
    public bool HasSecret => !string.IsNullOrEmpty(SecretValue);
}
