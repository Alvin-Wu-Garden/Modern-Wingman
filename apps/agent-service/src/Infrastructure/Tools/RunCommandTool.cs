using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Tools;

public sealed class RunCommandTool(
    IAgentPolicyEngine policyEngine,
    IApprovalCoordinator approvalCoordinator,
    IProcessRunner processRunner,
    ISensitiveDataRedactor redactor,
    IRunEventBus eventBus) : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "run_command",
        "Run a structured local development command in the active workspace.",
        AgentCapability.Execute,
        AgentRiskLevel.Medium,
        TimeSpan.FromMinutes(10));

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var executable = RequireString(request.Arguments, "executable");
            var arguments = ReadArguments(request.Arguments);
            var workingDirectory = request.Arguments.TryGetValue("workingDirectory", out var cwd) &&
                                   cwd is string cwdText &&
                                   !string.IsNullOrWhiteSpace(cwdText)
                ? WorkspacePathGuard.Resolve(request.Context.WorkspacePath, cwdText)
                : Path.GetFullPath(request.Context.WorkspacePath);
            var timeout = ReadTimeout(request.Arguments, Descriptor.DefaultTimeout);
            var (capabilities, risk) = CommandPolicyClassifier.Classify(executable, arguments);
            var permission = new AgentPermissionRequest(
                Descriptor.Name,
                capabilities,
                risk,
                executable + " " + string.Join(' ', arguments),
                workingDirectory,
                Descriptor.Description);
            var decision = policyEngine.Evaluate(
                new AgentPolicyContext(request.Context.Mode, request.Context.WorkspacePath),
                permission);
            if (decision.Kind == PolicyDecisionKind.Deny)
                return Failure(decision.Reason);
            var approvalRequired = decision.Kind == PolicyDecisionKind.RequireApproval;
            if (approvalRequired)
            {
                var approval = await approvalCoordinator.RequestAsync(
                    request.Context.RunId,
                    permission,
                    ct);
                if (!approval.Approved)
                    return Failure(approval.Comment ?? "Command rejected by the user.") with
                    {
                        ApprovalRequired = true,
                        ApprovalResult = "rejected",
                    };
            }

            var result = await processRunner.RunAsync(
                new ProcessInvocation(
                    executable,
                    arguments,
                    workingDirectory,
                    timeout,
                    OnOutput: (line, token) => eventBus.PublishAsync(
                        RunStreamEvent.ToolOutput(request.Context.RunId, Descriptor.Name, line),
                        token)),
                ct);
            var success = result.ExitCode == 0 && !result.TimedOut;
            return new ToolExecutionResult(
                success,
                redactor.Redact(result.StandardOutput),
                success ? null : redactor.Redact(result.StandardError),
                result.ExitCode,
                result.TimedOut,
                result.DurationMs,
                approvalRequired,
                approvalRequired ? "approved" : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure(ex.Message);
        }
    }

    private static IReadOnlyList<string> ReadArguments(
        IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue("arguments", out var value) || value is null)
            return [];
        if (value is IEnumerable<string> strings)
            return strings.ToList();
        if (value is JsonElement { ValueKind: JsonValueKind.Array } json)
            return json.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        throw new ArgumentException("Argument 'arguments' must be an array of strings.");
    }

    private static TimeSpan ReadTimeout(
        IReadOnlyDictionary<string, object?> values,
        TimeSpan fallback)
    {
        if (!values.TryGetValue("timeoutSeconds", out var value) || value is null)
            return fallback;
        var seconds = value switch
        {
            int number => number,
            long number => checked((int)number),
            JsonElement { ValueKind: JsonValueKind.Number } json => json.GetInt32(),
            _ => throw new ArgumentException("timeoutSeconds must be an integer."),
        };
        if (seconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(values), "Timeout must be 1-3600 seconds.");
        return TimeSpan.FromSeconds(seconds);
    }

    private static string RequireString(
        IReadOnlyDictionary<string, object?> arguments,
        string name)
    {
        if (!arguments.TryGetValue(name, out var value) ||
            value is not string text ||
            string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException($"Argument '{name}' is required.");
        }
        return text;
    }

    private static ToolExecutionResult Failure(string error) => new(false, "", error);
}
