using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

public sealed class VcsConnectionProfileRecord
{
    public string Id { get; set; } = ""; public string Name { get; set; } = "";
    public string VcsType { get; set; } = "Git"; public string ServerType { get; set; } = "BitbucketServer";
    public string BaseUrl { get; set; } = ""; public bool SslVerificationEnabled { get; set; } = true;
    public string? DefaultWorkspaceRoot { get; set; } public bool Enabled { get; set; } = true;
    public string? CommitAuthorName { get; set; } public string? CommitAuthorEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; }
    public VcsCredentialRecord? Credential { get; set; }
    public string? LastTestStatus { get; set; } public string? LastTestError { get; set; } public DateTimeOffset? LastTestedAt { get; set; }
}

public sealed class VcsCredentialRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConnectionProfileId { get; set; } = ""; public string Username { get; set; } = "";
    public string SecretType { get; set; } = "AccessToken"; public string SecretValue { get; set; } = "";
    public string EncryptionScheme { get; set; } = "plaintext"; public DateTimeOffset UpdatedAt { get; set; }
    public VcsConnectionProfileRecord? Profile { get; set; }
}

public sealed class VcsProfileRepository(
    IDbContextFactory<AppDbContext> factory,
    ISecretProtector secretProtector) : IVcsProfileRepository
{
    public async Task<IReadOnlyList<VcsConnectionProfile>> ListAsync(CancellationToken ct = default)
    { await using var db = await factory.CreateDbContextAsync(ct); return (await db.VcsConnectionProfiles.AsNoTracking().Include(x => x.Credential).OrderBy(x => x.Name).ToListAsync(ct)).Select(Map).ToList(); }
    public async Task<VcsConnectionProfile?> GetAsync(string id, CancellationToken ct = default)
    { await using var db = await factory.CreateDbContextAsync(ct); var row = await db.VcsConnectionProfiles.AsNoTracking().Include(x => x.Credential).FirstOrDefaultAsync(x => x.Id == id, ct); return row is null ? null : Map(row); }
    public async Task SaveAsync(VcsConnectionProfile profile, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var row = await db.VcsConnectionProfiles.Include(x => x.Credential).FirstOrDefaultAsync(x => x.Id == profile.Id, ct);
        if (row is null) { row = new VcsConnectionProfileRecord { Id = profile.Id, CreatedAt = profile.CreatedAt }; db.VcsConnectionProfiles.Add(row); }
        row.Name=profile.Name; row.VcsType=profile.VcsType.ToString(); row.ServerType=profile.ServerType.ToString(); row.BaseUrl=profile.BaseUrl; row.SslVerificationEnabled=profile.SslVerificationEnabled; row.DefaultWorkspaceRoot=profile.DefaultWorkspaceRoot; row.CommitAuthorName=profile.CommitAuthorName; row.CommitAuthorEmail=profile.CommitAuthorEmail; row.Enabled=profile.Enabled; row.UpdatedAt=DateTimeOffset.UtcNow;row.LastTestStatus=profile.LastTestStatus;row.LastTestError=profile.LastTestError;row.LastTestedAt=profile.LastTestedAt;
        if (profile.SecretValue is not null) { var protectedSecret=secretProtector.Protect(profile.SecretValue);row.Credential ??= new VcsCredentialRecord { ConnectionProfileId=profile.Id }; row.Credential.Username=profile.Username??""; row.Credential.SecretType=(profile.SecretType??VcsSecretType.Password).ToString(); row.Credential.SecretValue=protectedSecret.Value;row.Credential.EncryptionScheme=protectedSecret.Scheme; row.Credential.UpdatedAt=DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
    }
    public async Task DeleteAsync(string id, CancellationToken ct = default) { await using var db=await factory.CreateDbContextAsync(ct); var row=await db.VcsConnectionProfiles.FindAsync([id],ct); if(row is not null){db.Remove(row);await db.SaveChangesAsync(ct);} }
    private VcsConnectionProfile Map(VcsConnectionProfileRecord x)=>new(){Id=x.Id,Name=x.Name,VcsType=Enum.Parse<VcsType>(x.VcsType),ServerType=Enum.Parse<VcsServerType>(x.ServerType),BaseUrl=x.BaseUrl,SslVerificationEnabled=x.SslVerificationEnabled,DefaultWorkspaceRoot=x.DefaultWorkspaceRoot,CommitAuthorName=x.CommitAuthorName,CommitAuthorEmail=x.CommitAuthorEmail,Enabled=x.Enabled,CreatedAt=x.CreatedAt,UpdatedAt=x.UpdatedAt,Username=x.Credential?.Username,SecretType=x.Credential is null?null:Enum.Parse<VcsSecretType>(x.Credential.SecretType),SecretValue=x.Credential is null?null:secretProtector.Unprotect(x.Credential.SecretValue,x.Credential.EncryptionScheme),LastTestStatus=x.LastTestStatus,LastTestError=x.LastTestError,LastTestedAt=x.LastTestedAt};
}
