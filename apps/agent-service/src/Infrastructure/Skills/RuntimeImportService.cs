using System.IO.Compression;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Skills;

public sealed class RuntimeImportService(
    IConfiguration configuration,
    IAuditEventRecorder audit) : IRuntimeImportService
{
    public async Task<RuntimeImportResult> ImportRuntimeAsync(
        SkillRuntimeKind kind,
        string sourcePath,
        CancellationToken ct = default)
    {
        var destination = NewDestination(GetRuntimeRoot(), kind);
        await ImportAsync(sourcePath, destination, ct);
        var executableName = kind switch
        {
            SkillRuntimeKind.Python => "python.exe",
            SkillRuntimeKind.Node => "node.exe",
            _ => "pwsh.exe",
        };
        var executable = Directory.EnumerateFiles(
            destination,
            executableName,
            SearchOption.AllDirectories).FirstOrDefault();
        if (executable is null)
        {
            Directory.Delete(destination, recursive: true);
            throw new InvalidDataException(
                $"Imported archive does not contain {executableName}.");
        }

        var result = new RuntimeImportResult(
            kind.ToString().ToLowerInvariant(),
            destination,
            executable,
            Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Count());
        await RecordAsync("runtime_imported", result, ct);
        return result;
    }

    public async Task<RuntimeImportResult> ImportPackageCacheAsync(
        SkillRuntimeKind kind,
        string sourcePath,
        CancellationToken ct = default)
    {
        if (kind == SkillRuntimeKind.PowerShell)
            throw new InvalidOperationException("PowerShell package cache import is not supported.");
        var root = configuration["Runtime:PackageCacheRoot"];
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".Wingman",
                "package-cache");
        }
        var destination = Path.Combine(Path.GetFullPath(root), kind.ToString().ToLowerInvariant());
        Directory.CreateDirectory(destination);
        await ImportAsync(sourcePath, destination, ct, merge: true);
        var result = new RuntimeImportResult(
            kind.ToString().ToLowerInvariant(),
            destination,
            null,
            Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Count());
        await RecordAsync("package_cache_imported", result, ct);
        return result;
    }

    private string GetRuntimeRoot()
    {
        var configured = configuration["Runtime:ManagedRoot"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".Wingman",
                "runtimes")
            : Path.GetFullPath(configured);
    }

    private static string NewDestination(string root, SkillRuntimeKind kind)
    {
        var destination = Path.Combine(
            Path.GetFullPath(root),
            kind.ToString().ToLowerInvariant(),
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(destination);
        return destination;
    }

    private static async Task ImportAsync(
        string sourcePath,
        string destination,
        CancellationToken ct,
        bool merge = false)
    {
        var source = Path.GetFullPath(sourcePath);
        if (File.Exists(source))
        {
            if (!string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Offline import accepts a directory or ZIP archive.");
            await ExtractSafeAsync(source, destination, ct, merge);
            return;
        }
        if (!Directory.Exists(source))
            throw new FileNotFoundException("Offline import source does not exist.", source);
        await CopyTreeAsync(source, destination, ct, merge);
    }

    private static async Task ExtractSafeAsync(
        string archivePath,
        string destination,
        CancellationToken ct,
        bool overwrite)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP entry escapes the import destination.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && !overwrite)
                throw new IOException($"Import target already exists: {entry.FullName}");
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, ct);
        }
    }

    private static async Task CopyTreeAsync(
        string source,
        string destination,
        CancellationToken ct,
        bool overwrite)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
            await Task.Yield();
        }
    }

    private Task RecordAsync(string eventType, RuntimeImportResult result, CancellationToken ct) =>
        audit.RecordAsync(new AuditEventWrite(
            eventType,
            "runtime",
            result.Kind,
            "import",
            DetailsJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                result.DestinationPath,
                result.FileCount,
            })), ct);
}
