namespace AgentService.Domain.Models;

public enum VcsType { Git, Svn }
public enum VcsServerType { BitbucketServer, Svn }
public enum VcsSecretType { AccessToken, Password }
public enum VcsOperationStatus { Running, Succeeded, Failed, Cancelled }

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

public sealed class VcsProtectedRef
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string? ProjectId { get; init; }
    public VcsType VcsType { get; init; }
    public required string Pattern { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed class VcsOperation
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string? RunId { get; init; }
    public string? ProjectId { get; init; }
    public string? ConnectionProfileId { get; init; }
    public VcsType VcsType { get; init; }
    public required string Operation { get; init; }
    public string? TargetRef { get; init; }
    public VcsOperationStatus Status { get; set; } = VcsOperationStatus.Running;
    public string? BeforeRevision { get; set; }
    public string? AfterRevision { get; set; }
    public string? ErrorSanitized { get; set; }
    public bool SslVerificationEnabled { get; init; } = true;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
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
    public string? CommitAuthorName { get; set; }
    public string? CommitAuthorEmail { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Username { get; set; }
    public VcsSecretType? SecretType { get; set; }
    public string? SecretValue { get; set; }
    public bool HasSecret => !string.IsNullOrEmpty(SecretValue);
    public string? LastTestStatus { get; set; }
    public string? LastTestError { get; set; }
    public DateTimeOffset? LastTestedAt { get; set; }
}
