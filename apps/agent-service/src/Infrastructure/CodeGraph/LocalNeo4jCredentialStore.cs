using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AgentService.Infrastructure.CodeGraph;

/// <summary>
/// Stores the bundled local Neo4j credential outside source control, protected by
/// the current Windows user's DPAPI key. An explicitly supplied environment value
/// always takes precedence for managed deployments.
/// </summary>
public static class LocalNeo4jCredentialStore
{
    private const string EnvironmentVariableName = "WINGMAN_NEO4J_PASSWORD";
    private const string FileName = "neo4j-password.dpapi";
    private const string Scheme = "dpapi-current-user-v1";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
        "ModernWingman.Neo4j.LocalCredential.v1");

    public static string Resolve(IConfiguration configuration)
    {
        var environmentPassword = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(environmentPassword))
            return environmentPassword;

        var configuredPassword = configuration["Neo4j:Password"];
        if (!string.IsNullOrWhiteSpace(configuredPassword))
            return configuredPassword;

        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException(
                $"Neo4j password is required. Set the {EnvironmentVariableName} environment variable.");

        var path = GetCredentialPath();
        if (File.Exists(path))
            return Read(path);

        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Write(path, generated);
        return generated;
    }

    private static string GetCredentialPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModernWingman", "secrets", FileName);

    [SupportedOSPlatform("windows")]
    private static string Read(string path)
    {
        var payload = File.ReadAllText(path, Encoding.UTF8);
        var separator = payload.IndexOf(':');
        if (separator <= 0 || !string.Equals(payload[..separator], Scheme, StringComparison.Ordinal))
            throw new InvalidOperationException($"Invalid local Neo4j credential format: {path}");

        try
        {
            var encrypted = Convert.FromBase64String(payload[(separator + 1)..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                encrypted, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "The local Neo4j credential cannot be read by the current Windows user.", ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Write(string path, string password)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath,
                $"{Scheme}:{Convert.ToBase64String(encrypted)}", Encoding.UTF8);
            File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
