using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Tools;

public sealed class ListDirectoryTool(IAgentPolicyEngine policy, IApprovalCoordinator approvals)
    : PolicyEnforcedAgentTool(policy, approvals)
{
    public override ToolDescriptor Descriptor { get; } = new("list_directory", "List entries inside a workspace directory.", AgentCapability.Read, AgentRiskLevel.Low, TimeSpan.FromSeconds(30));
    protected override Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request, CancellationToken ct)
    {
        var relative = request.Arguments.TryGetValue("path", out var value) && value is string text ? text : ".";
        var path = WorkspacePathGuard.Resolve(request.Context.WorkspacePath, relative);
        if (!Directory.Exists(path)) return Task.FromResult(new ToolExecutionResult(false, "", "Directory not found."));
        var entries = Directory.EnumerateFileSystemEntries(path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(10_000).Select(x => new { name=Path.GetFileName(x), type=Directory.Exists(x) ? "directory" : "file" });
        return Task.FromResult(new ToolExecutionResult(true, JsonSerializer.Serialize(entries)));
    }
}

public sealed class ReadFileRangeTool(IAgentPolicyEngine policy, IApprovalCoordinator approvals)
    : PolicyEnforcedAgentTool(policy, approvals)
{
    public override ToolDescriptor Descriptor { get; } = new("read_file_range", "Read an inclusive line range from a workspace text file.", AgentCapability.Read, AgentRiskLevel.Low, TimeSpan.FromSeconds(30));
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request, CancellationToken ct)
    {
        var path = WorkspacePathGuard.ResolveReadable(request.Context.WorkspacePath, RequireString(request.Arguments, "path"));
        var start = ReadInt(request.Arguments, "startLine", 1);
        var end = ReadInt(request.Arguments, "endLine", start + 199);
        if (start < 1 || end < start || end - start > 5_000) return new(false, "", "Invalid line range.");
        var lines = await File.ReadAllLinesAsync(path, ct);
        var selected = lines.Skip(start - 1).Take(end - start + 1).Select((line, index) => $"{start + index}: {line}");
        return new(true, string.Join(Environment.NewLine, selected));
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?> args, string name, int fallback)
    {
        if (!args.TryGetValue(name, out var value) || value is null) return fallback;
        return value switch { int n => n, long n => checked((int)n), JsonElement { ValueKind: JsonValueKind.Number } json => json.GetInt32(), _ => throw new ArgumentException($"{name} must be an integer.") };
    }
}

public sealed class ApplyPatchTool(IAgentPolicyEngine policy, IApprovalCoordinator approvals)
    : PolicyEnforcedAgentTool(policy, approvals)
{
    public override ToolDescriptor Descriptor { get; } = new("apply_patch", "Replace one exact text region in a workspace file, or create a new file when expectedText is empty.", AgentCapability.Write, AgentRiskLevel.Medium, TimeSpan.FromSeconds(30));
    protected override AgentPermissionRequest BuildPermissionRequest(ToolExecutionRequest request) => new(Descriptor.Name, Descriptor.Capabilities, Descriptor.RiskLevel, WorkspacePathGuard.Resolve(request.Context.WorkspacePath, RequireString(request.Arguments, "path")), request.Context.WorkspacePath, Descriptor.Description);
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request, CancellationToken ct)
    {
        var path = WorkspacePathGuard.Resolve(request.Context.WorkspacePath, RequireString(request.Arguments, "path"));
        var expected = request.Arguments.TryGetValue("expectedText", out var old) && old is string oldText ? oldText : "";
        var replacement = request.Arguments.TryGetValue("replacement", out var next) && next is string nextText ? nextText : throw new ArgumentException("Argument 'replacement' is required.");
        var current = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        if (expected.Length == 0)
        {
            if (File.Exists(path) && current.Length > 0) return new(false, "", "Refusing to overwrite an existing non-empty file without expectedText.");
        }
        else
        {
            var first = current.IndexOf(expected, StringComparison.Ordinal);
            if (first < 0) return new(false, "", "Expected text was not found; file may have changed.");
            if (current.IndexOf(expected, first + expected.Length, StringComparison.Ordinal) >= 0) return new(false, "", "Expected text is not unique.");
            replacement = current[..first] + replacement + current[(first + expected.Length)..];
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, replacement, ct);
        return new(true, path);
    }
}

public sealed class DeleteFileTool(IAgentPolicyEngine policy, IApprovalCoordinator approvals)
    : PolicyEnforcedAgentTool(policy, approvals)
{
    public override ToolDescriptor Descriptor { get; } = new("delete_file", "Delete one file inside the active workspace.", AgentCapability.Write | AgentCapability.Destructive, AgentRiskLevel.High, TimeSpan.FromSeconds(30));
    protected override AgentPermissionRequest BuildPermissionRequest(ToolExecutionRequest request) => new(Descriptor.Name, Descriptor.Capabilities, Descriptor.RiskLevel, WorkspacePathGuard.Resolve(request.Context.WorkspacePath, RequireString(request.Arguments, "path")), request.Context.WorkspacePath, Descriptor.Description);
    protected override Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request, CancellationToken ct)
    {
        var path = WorkspacePathGuard.Resolve(request.Context.WorkspacePath, RequireString(request.Arguments, "path"));
        if (!File.Exists(path)) return Task.FromResult(new ToolExecutionResult(false, "", "File not found."));
        File.Delete(path);
        return Task.FromResult(new ToolExecutionResult(true, path));
    }
}
