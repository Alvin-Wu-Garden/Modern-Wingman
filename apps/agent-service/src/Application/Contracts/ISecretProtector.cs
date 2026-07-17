namespace AgentService.Application.Contracts;

public sealed record ProtectedSecret(string Value, string Scheme);

public interface ISecretProtector
{
    ProtectedSecret Protect(string plaintext);
    string Unprotect(string value, string scheme);
}

public interface IVcsCredentialProtectionMigration
{
    Task<int> MigrateAsync(CancellationToken ct = default);
}
