using System.Security.Cryptography;
using System.Text;
using AgentService.Application.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ModernWingman.VcsCredentials.v1");

    public ProtectedSecret Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows()) return new(plaintext, "plaintext");
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            Entropy,
            DataProtectionScope.CurrentUser);
        return new(Convert.ToBase64String(encrypted), "dpapi-current-user-v1");
    }

    public string Unprotect(string value, string scheme) => scheme switch
    {
        "plaintext" or "" => value,
        "dpapi-current-user-v1" when OperatingSystem.IsWindows() => Encoding.UTF8.GetString(
            ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)),
        "dpapi-current-user-v1" => throw new PlatformNotSupportedException("Windows DPAPI credentials can only be read by the Windows user that saved them."),
        _ => throw new InvalidOperationException($"Unsupported credential encryption scheme: {scheme}"),
    };
}

public sealed class VcsCredentialProtectionMigration(
    IDbContextFactory<AppDbContext> factory,
    ISecretProtector protector) : IVcsCredentialProtectionMigration
{
    public async Task<int> MigrateAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.VcsCredentials
            .Where(row => row.EncryptionScheme == "plaintext" && row.SecretValue != "")
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            var protectedSecret = protector.Protect(row.SecretValue);
            row.SecretValue = protectedSecret.Value;
            row.EncryptionScheme = protectedSecret.Scheme;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
