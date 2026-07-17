using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Tools;
using AgentService.Infrastructure.Marketplace;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.AgentFramework.Plugins;

/// <summary>
/// Parses the Wingman-only portion of an installed plugin. It deliberately accepts
/// only declarative components: no assembly loading, shell strings, or implicit
/// environment values are supported.
/// </summary>
public sealed class PluginRuntimeManifestLoader
{
    private static readonly Regex ComponentId = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public PluginRuntimeManifest Load(EnabledPluginCapabilities plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.InstalledPath))
            throw new InvalidDataException("Enabled plugin has no installed package path.");
        var root = Path.GetFullPath(plugin.InstalledPath);
        var path = Path.Combine(root, "wingman.json");
        if (!File.Exists(path)) throw new InvalidDataException("Plugin is missing wingman.json.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("wingman.json must be a JSON object.");

        return new PluginRuntimeManifest(
            plugin.PluginId,
            plugin.Version,
            root,
            ReadFunctions(document.RootElement, root),
            ReadHooks(document.RootElement, root));
    }

    private static IReadOnlyList<PluginFunctionComponent> ReadFunctions(JsonElement root, string pluginRoot)
    {
        if (!root.TryGetProperty("functions", out var functions)) return [];
        if (functions.ValueKind != JsonValueKind.Array) throw new InvalidDataException("wingman.json functions must be an array.");
        return functions.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Plugin function must be an object.");
            var id = Required(item, "id"); ValidateId(id, "function");
            var hasRuntime = item.TryGetProperty("runtime", out _);
            var hasExecutable = HasNonEmptyString(item, "executable") || HasNonEmptyString(item, "command");
            if (hasRuntime == hasExecutable)
                throw new InvalidDataException("Plugin function must declare exactly one of runtime or executable.");

            PluginRuntimeRequirement? runtime = null;
            string? executable = null;
            string? entrypointPath = null;
            if (hasRuntime)
            {
                runtime = ReadRuntime(item, "function");
                entrypointPath = ReadEntrypoint(item, pluginRoot, runtime.Kind, "function");
            }
            else
            {
                executable = Required(item, "executable", "command");
                ValidateExecutable(executable, "function");
            }
            var args = ReadStrings(item, "arguments");
            var workingDirectory = ReadWorkingDirectory(item, pluginRoot);
            var timeout = ReadTimeout(item);
            var schema = ReadSchema(item);
            var description = Optional(item, "description") ?? $"Run plugin function {id}.";
            return new PluginFunctionComponent(id, description, executable, runtime, entrypointPath, args, workingDirectory, timeout, schema);
        }).ToList();
    }

    private static IReadOnlyList<PluginHookComponent> ReadHooks(JsonElement root, string pluginRoot)
    {
        if (!root.TryGetProperty("hooks", out var hooks)) return [];
        if (hooks.ValueKind != JsonValueKind.Array) throw new InvalidDataException("wingman.json hooks must be an array.");
        return hooks.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Plugin hook must be an object.");
            var id = Required(item, "id"); ValidateId(id, "hook");
            var eventName = Required(item, "event");
            if (!TryParseStage(eventName, out var stage)) throw new InvalidDataException($"Plugin hook {id} uses an unsupported event: {eventName}.");
            var hasRuntime = item.TryGetProperty("runtime", out _);
            var hasExecutable = HasNonEmptyString(item, "executable") || HasNonEmptyString(item, "command");
            if (hasRuntime == hasExecutable)
                throw new InvalidDataException("Plugin hook must declare exactly one of runtime or executable.");

            PluginRuntimeRequirement? runtime = null;
            string? executable = null;
            string? entrypointPath = null;
            if (hasRuntime)
            {
                runtime = ReadRuntime(item, "hook");
                entrypointPath = ReadEntrypoint(item, pluginRoot, runtime.Kind, "hook");
            }
            else
            {
                executable = Required(item, "executable", "command");
                ValidateExecutable(executable, "hook");
            }
            var environment = ReadStrings(item, "environment");
            if (environment.Any(name => !Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)))
                throw new InvalidDataException($"Plugin hook {id} contains an invalid environment variable name.");
            return new PluginHookComponent(id, stage, executable, runtime, entrypointPath, ReadStrings(item, "arguments"), ReadWorkingDirectory(item, pluginRoot), ReadTimeout(item), environment);
        }).ToList();
    }

    internal static bool TryParseStage(string value, out AgentHookStage stage)
    {
        switch (value)
        {
            case "beforeTool": stage = AgentHookStage.BeforeTool; return true;
            case "afterTool": stage = AgentHookStage.AfterTool; return true;
            case "afterFileChange": stage = AgentHookStage.AfterFileChange; return true;
            case "beforeRunComplete": stage = AgentHookStage.BeforeRunComplete; return true;
            default: stage = default; return false;
        }
    }

    private static string Required(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!;
        throw new InvalidDataException($"Plugin component is missing required property '{names[0]}'.");
    }
    private static bool HasNonEmptyString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString());
    private static string? Optional(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static IReadOnlyList<string> ReadStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidDataException($"Plugin component {name} must be an array of strings.");
        return value.EnumerateArray().Select(item => item.GetString()!).ToList();
    }
    private static string ReadWorkingDirectory(JsonElement element, string root)
    {
        var relative = Optional(element, "workingDirectory") ?? ".";
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Plugin workingDirectory must be relative to the plugin root.");
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.Equals(root, StringComparison.OrdinalIgnoreCase) && !resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin workingDirectory escapes its package.");
        if (!Directory.Exists(resolved)) throw new InvalidDataException("Plugin workingDirectory does not exist.");
        return resolved;
    }
    private static PluginRuntimeRequirement ReadRuntime(JsonElement element, string componentKind)
    {
        if (!element.TryGetProperty("runtime", out var runtime) || runtime.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Plugin {componentKind} runtime must be an object.");
        var kind = Required(runtime, "kind");
        var parsedKind = kind.Trim().ToLowerInvariant() switch
        {
            "python" => SkillRuntimeKind.Python,
            "node" or "nodejs" => SkillRuntimeKind.Node,
            "powershell" or "pwsh" => SkillRuntimeKind.PowerShell,
            _ => throw new InvalidDataException($"Plugin {componentKind} runtime is unsupported: {kind}."),
        };
        return new(parsedKind, Optional(runtime, "version"));
    }
    private static string ReadEntrypoint(JsonElement element, string root, SkillRuntimeKind kind, string componentKind)
    {
        var relative = Required(element, "entrypoint");
        if (Path.IsPathRooted(relative)) throw new InvalidDataException($"Plugin {componentKind} entrypoint must be relative to the plugin root.");
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
            throw new InvalidDataException($"Plugin {componentKind} entrypoint does not exist: {relative}.");
        var extensions = kind switch
        {
            SkillRuntimeKind.Python => new[] { ".py" },
            SkillRuntimeKind.Node => new[] { ".js", ".cjs", ".mjs" },
            SkillRuntimeKind.PowerShell => new[] { ".ps1" },
            _ => Array.Empty<string>(),
        };
        if (!extensions.Contains(Path.GetExtension(resolved), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Plugin {componentKind} entrypoint extension does not match the declared runtime.");
        return resolved;
    }
    private static TimeSpan ReadTimeout(JsonElement element)
    {
        var seconds = element.TryGetProperty("timeoutSeconds", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 60;
        if (seconds is < 1 or > 600) throw new InvalidDataException("Plugin component timeoutSeconds must be between 1 and 600.");
        return TimeSpan.FromSeconds(seconds);
    }
    private static string ReadSchema(JsonElement element)
    {
        if (!element.TryGetProperty("inputSchema", out var schema)) return "{\"type\":\"object\"}";
        if (schema.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Plugin function inputSchema must be an object.");
        return schema.GetRawText();
    }
    private static void ValidateId(string id, string kind) { if (!ComponentId.IsMatch(id)) throw new InvalidDataException($"Plugin {kind} id is invalid: {id}."); }
    private static void ValidateExecutable(string executable, string kind)
    {
        if (executable.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new InvalidDataException($"Plugin {kind} executable is invalid.");
        // No shell command is accepted. A program name or an explicit existing executable path is passed to ProcessStartInfo.ArgumentList.
        if (executable.Contains("&&", StringComparison.Ordinal) || executable.Contains('|')) throw new InvalidDataException($"Plugin {kind} executable cannot contain shell operators.");
    }
}

public sealed record PluginRuntimeManifest(string PluginId, string Version, string RootPath, IReadOnlyList<PluginFunctionComponent> Functions, IReadOnlyList<PluginHookComponent> Hooks);
public sealed record PluginRuntimeRequirement(SkillRuntimeKind Kind, string? VersionConstraint);
/// <summary>Function execution is either legacy direct executable or a script run by a Wingman-resolved runtime.</summary>
public sealed record PluginFunctionComponent(string Id, string Description, string? Executable, PluginRuntimeRequirement? Runtime, string? EntrypointPath, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout, string InputSchemaJson);
public sealed record PluginHookComponent(string Id, AgentHookStage Stage, string? Executable, PluginRuntimeRequirement? Runtime, string? EntrypointPath, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout, IReadOnlyList<string> VisibleEnvironmentNames);

/// <summary>Registers only enabled plugin functions, under a source that can be atomically replaced on enable/disable.</summary>
public sealed class PluginRuntimeToolRegistrar(
    IManagedToolRegistry registry,
    PluginRuntimeManifestLoader manifestLoader,
    IAgentPolicyEngine policy,
    IApprovalCoordinator approvals,
    IProcessRunner processes,
    IRuntimeResolver runtimeResolver,
    ISensitiveDataRedactor redactor,
    IRunEventBus events)
{
    public void Reconcile(IReadOnlyList<EnabledPluginCapabilities> plugins)
    {
        var expectedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
        {
            var source = Source(plugin.PluginId); expectedSources.Add(source);
            try
            {
                var manifest = manifestLoader.Load(plugin);
                var tools = manifest.Functions.Select(function => (IAgentTool)new PluginFunctionTool(plugin, function, source, policy, approvals, processes, runtimeResolver, redactor, events)).ToList();
                registry.ReplaceSource(source, tools);
            }
            catch (Exception) when (!string.IsNullOrWhiteSpace(plugin.InstalledPath))
            {
                // A damaged package is unavailable, but must not take down unrelated plugins.
                registry.ReplaceSource(source, []);
            }
        }
        foreach (var source in registry.ListTools().Select(tool => tool.Source).Where(source => source.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Where(source => !expectedSources.Contains(source)).ToList())
            registry.ReplaceSource(source, []);
    }

    public void Clear() => Reconcile([]);
    public void Remove(string pluginId) => registry.ReplaceSource(Source(pluginId), []);
    private static string Source(string pluginId) => "plugin:" + pluginId;
}

internal sealed class PluginFunctionTool : PolicyEnforcedAgentTool
{
    private readonly EnabledPluginCapabilities _plugin;
    private readonly PluginFunctionComponent _component;
    private readonly IProcessRunner _processes;
    private readonly IRuntimeResolver _runtimeResolver;
    private readonly ISensitiveDataRedactor _redactor;
    private readonly IRunEventBus _events;
    public PluginFunctionTool(EnabledPluginCapabilities plugin, PluginFunctionComponent component, string source, IAgentPolicyEngine policy, IApprovalCoordinator approvals, IProcessRunner processes, IRuntimeResolver runtimeResolver, ISensitiveDataRedactor redactor, IRunEventBus events) : base(policy, approvals)
    {
        _plugin = plugin; _component = component; _processes = processes; _runtimeResolver = runtimeResolver; _redactor = redactor; _events = events;
        Descriptor = new($"plugin:{plugin.PluginId}:{component.Id}", component.Description, AgentCapability.Execute | AgentCapability.ExternalSideEffect, AgentRiskLevel.High, component.Timeout, component.InputSchemaJson, source);
    }
    public override ToolDescriptor Descriptor { get; }
    protected override AgentPermissionRequest BuildPermissionRequest(ToolExecutionRequest request) => new(Descriptor.Name, Descriptor.Capabilities, Descriptor.RiskLevel, _component.EntrypointPath ?? _component.Executable, _component.WorkingDirectory, Descriptor.Description);
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request, CancellationToken ct)
    {
        var invocation = await ResolveInvocationAsync(request, ct);
        if (invocation is null)
        {
            const string error = "No compatible bundled or managed runtime was found for this Plugin function.";
            await PublishAsync(request.Context.RunId, "plugin.function_failed", new { pluginId = _plugin.PluginId, pluginVersion = _plugin.Version, componentId = _component.Id, error }, CancellationToken.None);
            return new(false, "", error);
        }
        await PublishAsync(request.Context.RunId, "plugin.function_started", new { pluginId = _plugin.PluginId, pluginVersion = _plugin.Version, componentId = _component.Id, runtime = invocation.RuntimeKind, runtimeSource = invocation.RuntimeSource }, ct);
        var result = await _processes.RunAsync(new(invocation.Executable, invocation.Arguments, _component.WorkingDirectory, _component.Timeout, MaxOutputCharacters: 100_000), ct);
        var success = result.ExitCode == 0 && !result.TimedOut;
        await PublishAsync(request.Context.RunId, success ? "plugin.function_completed" : "plugin.function_failed", new { pluginId = _plugin.PluginId, pluginVersion = _plugin.Version, componentId = _component.Id, result.ExitCode, result.TimedOut, result.DurationMs, runtime = invocation.RuntimeKind, runtimeSource = invocation.RuntimeSource }, CancellationToken.None);
        return new(success, _redactor.Redact(result.StandardOutput), success ? null : _redactor.Redact(result.StandardError), result.ExitCode, result.TimedOut, result.DurationMs, MetadataJson: JsonSerializer.Serialize(new { pluginId = _plugin.PluginId, pluginVersion = _plugin.Version, componentId = _component.Id, capabilityKind = "function", runtime = invocation.RuntimeKind, runtimeSource = invocation.RuntimeSource }));
    }
    private async Task<ResolvedPluginInvocation?> ResolveInvocationAsync(ToolExecutionRequest request, CancellationToken ct)
    {
        var boundArguments = BindArguments(_component.Arguments, request.Arguments);
        if (_component.Runtime is null)
            return string.IsNullOrWhiteSpace(_component.Executable) ? null : new(_component.Executable, boundArguments, null, "external");
        if (string.IsNullOrWhiteSpace(_component.EntrypointPath) || string.IsNullOrWhiteSpace(_plugin.InstalledPath)) return null;
        var workspace = string.IsNullOrWhiteSpace(request.Context.WorkspacePath) ? _component.WorkingDirectory : request.Context.WorkspacePath;
        var runtime = await _runtimeResolver.ResolveAsync(new(_component.Runtime.Kind, _component.Runtime.VersionConstraint, _plugin.InstalledPath, workspace), ct);
        if (runtime is null) return null;
        var arguments = (runtime.PrefixArguments ?? []).Concat([_component.EntrypointPath]).Concat(boundArguments).ToList();
        return new(runtime.ExecutablePath, arguments, runtime.Kind.ToString(), runtime.Source);
    }
    private sealed record ResolvedPluginInvocation(string Executable, IReadOnlyList<string> Arguments, string? RuntimeKind, string RuntimeSource);
    private ValueTask PublishAsync(string runId, string type, object payload, CancellationToken ct) => _events.PublishAsync(new RunStreamEvent { RunId = runId, EventType = type, PayloadJson = JsonSerializer.Serialize(payload), Timestamp = DateTimeOffset.UtcNow }, ct);
    internal static IReadOnlyList<string> BindArguments(IReadOnlyList<string> templates, IReadOnlyDictionary<string, object?> input) => templates.Select(template =>
    {
        if (template == "{{input}}") return JsonSerializer.Serialize(input);
        const string prefix = "{{input.";
        if (template.StartsWith(prefix, StringComparison.Ordinal) && template.EndsWith("}}", StringComparison.Ordinal))
        {
            var name = template[prefix.Length..^2];
            if (!input.TryGetValue(name, out var value)) throw new ArgumentException($"Plugin function argument '{name}' is required.");
            return value is JsonElement json ? json.GetRawText() : value is string text ? text : JsonSerializer.Serialize(value);
        }
        if (template.Contains("{{", StringComparison.Ordinal)) throw new ArgumentException("Plugin argument templates must occupy the whole argument.");
        return template;
    }).ToList();
}

/// <summary>Runs manifest-declared hooks only for currently enabled plugins. Disable cancels any in-flight plugin process.</summary>
public sealed class MarketplacePluginHookDispatcher(
    IEnabledPluginCapabilitySource capabilities,
    PluginRuntimeManifestLoader manifestLoader,
    IProcessRunner processes,
    IRuntimeResolver runtimeResolver,
    IRunEventBus events,
    ILogger<MarketplacePluginHookDispatcher> logger) : IAgentHook, IPluginRuntimeEnablementObserver
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pluginCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _concurrency = new(4, 4);
    public string Name => "marketplace-plugin-hooks";

    public async ValueTask InvokeAsync(AgentHookContext context, CancellationToken ct = default)
    {
        var snapshot = await capabilities.GetSnapshotAsync(ct);
        foreach (var plugin in snapshot)
        {
            PluginRuntimeManifest manifest;
            try { manifest = manifestLoader.Load(plugin); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning(ex, "Plugin {PluginId} hook manifest is unavailable", plugin.PluginId); continue; }
            foreach (var hook in manifest.Hooks.Where(item => item.Stage == context.Stage))
                await InvokeHookAsync(plugin, hook, context, ct);
        }
    }

    public void OnPluginEnablementChanged(string pluginId, bool enabled)
    {
        if (enabled) return;
        if (_pluginCancellations.TryRemove(pluginId, out var cancellation)) { cancellation.Cancel(); cancellation.Dispose(); }
    }

    private async Task InvokeHookAsync(EnabledPluginCapabilities plugin, PluginHookComponent hook, AgentHookContext context, CancellationToken ct)
    {
        var pluginCancellation = _pluginCancellations.GetOrAdd(plugin.PluginId, _ => new CancellationTokenSource());
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, pluginCancellation.Token);
        await _concurrency.WaitAsync(linked.Token);
        try
        {
            var invocation = await ResolveHookInvocationAsync(plugin, hook, context, linked.Token);
            if (invocation is null)
            {
                await PublishAsync(context.RunId, "plugin.hook_failed", new { pluginId = plugin.PluginId, pluginVersion = plugin.Version, componentId = hook.Id, error = "No compatible bundled or managed runtime was found for this Plugin hook." }, CancellationToken.None);
                return;
            }
            await PublishAsync(context.RunId, "plugin.hook_started", new { pluginId = plugin.PluginId, pluginVersion = plugin.Version, componentId = hook.Id, stage = context.Stage.ToString(), runtime = invocation.RuntimeKind, runtimeSource = invocation.RuntimeSource }, linked.Token);
            var result = await processes.RunAsync(new(invocation.Executable, invocation.Arguments, hook.WorkingDirectory, hook.Timeout, MaxOutputCharacters: 100_000), linked.Token);
            var success = result.ExitCode == 0 && !result.TimedOut;
            await PublishAsync(context.RunId, success ? "plugin.hook_completed" : "plugin.hook_failed", new { pluginId = plugin.PluginId, pluginVersion = plugin.Version, componentId = hook.Id, result.ExitCode, result.TimedOut, result.DurationMs, runtime = invocation.RuntimeKind, runtimeSource = invocation.RuntimeSource }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (pluginCancellation.IsCancellationRequested)
        {
            await PublishAsync(context.RunId, "plugin.hook_completed", new { pluginId = plugin.PluginId, pluginVersion = plugin.Version, componentId = hook.Id, cancelled = true }, CancellationToken.None);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Plugin hook {PluginId}/{HookId} failed", plugin.PluginId, hook.Id);
            await PublishAsync(context.RunId, "plugin.hook_failed", new { pluginId = plugin.PluginId, pluginVersion = plugin.Version, componentId = hook.Id, error = "Hook execution failed." }, CancellationToken.None);
        }
        finally { _concurrency.Release(); }
    }
    private async Task<ResolvedPluginHookInvocation?> ResolveHookInvocationAsync(EnabledPluginCapabilities plugin, PluginHookComponent hook, AgentHookContext context, CancellationToken ct)
    {
        var boundArguments = PluginFunctionTool.BindArguments(hook.Arguments, HookInput(context));
        if (hook.Runtime is null)
            return string.IsNullOrWhiteSpace(hook.Executable) ? null : new(hook.Executable, boundArguments, null, "external");
        if (string.IsNullOrWhiteSpace(hook.EntrypointPath) || string.IsNullOrWhiteSpace(plugin.InstalledPath)) return null;
        var workspace = string.IsNullOrWhiteSpace(context.WorkspacePath) ? hook.WorkingDirectory : context.WorkspacePath;
        var runtime = await runtimeResolver.ResolveAsync(new(hook.Runtime.Kind, hook.Runtime.VersionConstraint, plugin.InstalledPath, workspace), ct);
        if (runtime is null) return null;
        var arguments = (runtime.PrefixArguments ?? []).Concat([hook.EntrypointPath]).Concat(boundArguments).ToList();
        return new(runtime.ExecutablePath, arguments, runtime.Kind.ToString(), runtime.Source);
    }
    private static IReadOnlyDictionary<string, object?> HookInput(AgentHookContext context) => new Dictionary<string, object?> { ["stage"] = context.Stage.ToString(), ["toolName"] = context.ToolName, ["workspacePath"] = context.WorkspacePath, ["arguments"] = context.Arguments, ["success"] = context.Result?.Success };
    private ValueTask PublishAsync(string runId, string type, object payload, CancellationToken ct) => events.PublishAsync(new RunStreamEvent { RunId = runId, EventType = type, PayloadJson = JsonSerializer.Serialize(payload), Timestamp = DateTimeOffset.UtcNow }, ct);
    private sealed record ResolvedPluginHookInvocation(string Executable, IReadOnlyList<string> Arguments, string? RuntimeKind, string RuntimeSource);
}
