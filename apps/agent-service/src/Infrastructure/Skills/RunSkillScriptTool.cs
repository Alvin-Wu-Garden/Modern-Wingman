using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Skills;

public sealed class SkillScriptRunner(
    ISkillProvider skillProvider,
    ISkillManifestLoader manifestLoader,
    IRuntimeResolver runtimeResolver,
    IProcessRunner processRunner,
    IAgentPolicyEngine policyEngine,
    IApprovalCoordinator approvalCoordinator,
    ISkillRiskProvider? riskProvider = null,
    ISensitiveDataRedactor? redactor = null,
    IConfiguration? configuration = null) : ISkillScriptRunner
{
    public ToolDescriptor Descriptor { get; } = new(
        "run_skill_script",
        "Run a declared Python, Node.js, or PowerShell skill entrypoint.",
        AgentCapability.Execute,
        AgentRiskLevel.Medium,
        TimeSpan.FromSeconds(120));

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var skillName = RequireString(request.Arguments, "skillName");
            var entrypointName = RequireString(request.Arguments, "entrypoint");
            var skill = skillProvider.ListSkills().FirstOrDefault(x =>
                string.Equals(x.Name, skillName, StringComparison.OrdinalIgnoreCase));
            if (skill is null)
                return Failure($"Skill is not installed for Modern Wingman: {skillName}");

            var skillRoot = Path.GetDirectoryName(skill.SkillFilePath)!;
            var manifest = await manifestLoader.LoadAsync(skillRoot, ct);
            if (manifest?.Runtime is null)
            {
                return Failure(
                    $"Skill '{skillName}' has no wingman.yaml and is instruction-only.");
            }
            if (!manifest.Runtime.Entrypoints.TryGetValue(entrypointName, out var entrypoint))
                return Failure($"Entrypoint not declared by skill '{skillName}': {entrypointName}");
            if (!YamlSkillManifestLoader.TryParseRuntimeKind(
                    manifest.Runtime.Type,
                    out var runtimeKind))
            {
                return Failure($"Unsupported runtime type: {manifest.Runtime.Type}");
            }

            var scriptPath = Infrastructure.Tools.WorkspacePathGuard.Resolve(
                skillRoot,
                entrypoint.Path);
            if (!File.Exists(scriptPath))
                return Failure($"Skill script not found: {entrypoint.Path}");
            if (!ExtensionMatches(runtimeKind, scriptPath))
                return Failure("Skill script extension does not match the declared runtime.");

            var capabilities = AgentCapability.Execute | AgentCapability.Write;
            if (entrypoint.Network)
                capabilities |= AgentCapability.Network;
            var libraryRisk = riskProvider is null
                ? null
                : await riskProvider.GetAsync(skillName, ct);
            var risk = entrypoint.RequiresApproval || entrypoint.RequiredEnvironment.Count > 0
                ? AgentRiskLevel.High
                : AgentRiskLevel.Medium;
            risk = MaxRisk(risk, libraryRisk?.Level);
            var permission = new AgentPermissionRequest(
                Descriptor.Name,
                capabilities,
                risk,
                scriptPath,
                skillRoot,
                $"Run skill '{skillName}' entrypoint '{entrypointName}'.");
            var policyDecision = policyEngine.Evaluate(
                new AgentPolicyContext(request.Context.Mode, request.Context.WorkspacePath),
                permission);
            if (policyDecision.Kind == PolicyDecisionKind.Deny)
                return Failure(policyDecision.Reason);
            var approvalRequired = policyDecision.Kind == PolicyDecisionKind.RequireApproval || entrypoint.RequiresApproval;
            if (approvalRequired)
            {
                var approval = await approvalCoordinator.RequestAsync(
                    request.Context.RunId,
                    permission,
                    ct);
                if (!approval.Approved)
                    return Failure(approval.Comment ?? "Skill execution was rejected.") with
                    {
                        ApprovalRequired = true,
                        ApprovalResult = "rejected",
                    };
            }

            var runtime = await runtimeResolver.ResolveAsync(
                new RuntimeResolutionRequest(
                    runtimeKind,
                    manifest.Runtime.Version,
                    skillRoot,
                    request.Context.WorkspacePath),
                ct);
            if (runtime is null)
            {
                return Failure(
                    $"No compatible {runtimeKind} runtime was found for constraint " +
                    $"'{manifest.Runtime.Version ?? "*"}'.");
            }

            ValidateDependencyFiles(manifest.Runtime,skillRoot);
            if (!DependenciesReady(manifest.Runtime, skillRoot, runtimeKind))
            {
                if (!ReadBoolean(request.Arguments, "installDependencies"))
                {
                    return Failure(
                        "Skill dependencies are not installed. Retry with installDependencies=true " +
                        "to request a policy-controlled dependency restore.");
                }
                var install = await InstallDependenciesAsync(
                    request,
                    manifest.Runtime,
                    skillRoot,
                    runtime,
                    runtimeKind,
                    ct);
                if (!install.Success)
                    return install;
                approvalRequired |= install.ApprovalRequired;
                runtime = await runtimeResolver.ResolveAsync(
                    new RuntimeResolutionRequest(
                        runtimeKind,
                        manifest.Runtime.Version,
                        skillRoot,
                        request.Context.WorkspacePath),
                    ct) ?? throw new InvalidOperationException("Installed Skill runtime could not be resolved.");
            }
            var scriptArguments=entrypoint.Parameters.Count>0?ReadParameters(request.Arguments,entrypoint.Parameters):ReadArguments(request.Arguments);
            var arguments = BuildArguments(runtime, scriptPath, scriptArguments);
            var environment = BuildEnvironment(entrypoint.RequiredEnvironment);
            var workingDirectory=string.IsNullOrWhiteSpace(entrypoint.WorkingDirectory)?skillRoot:YamlSkillManifestLoader.ResolveSkillPath(skillRoot,entrypoint.WorkingDirectory);
            var result = await processRunner.RunAsync(
                new ProcessInvocation(
                    runtime.ExecutablePath,
                    arguments,
                    workingDirectory,
                    TimeSpan.FromSeconds(entrypoint.TimeoutSeconds),
                    environment),
                ct);
            var success = result.ExitCode == 0 && !result.TimedOut;
            return new ToolExecutionResult(
                success,
                redactor?.Redact(result.StandardOutput) ?? result.StandardOutput,
                success ? null : redactor?.Redact(result.StandardError) ?? result.StandardError,
                result.ExitCode,
                result.TimedOut,
                result.DurationMs,
                approvalRequired,
                approvalRequired ? "approved" : null,
                JsonSerializer.Serialize(new
                {
                    skillId = skillName,
                    entrypoint = entrypointName,
                    scriptHash = HashFile(scriptPath),
                    runtime = runtime.Kind.ToString(),
                    runtimeVersion = runtime.Version.ToString(),
                    runtimeSource = runtime.Source,
                    risk = risk.ToString(),
                    riskNotes = libraryRisk?.Notes,
                }));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure(ex.Message);
        }
    }

    private static IReadOnlyList<string> BuildArguments(
        ResolvedRuntime runtime,
        string scriptPath,
        IReadOnlyList<string> arguments)
    {
        var result = new List<string>();
        if (runtime.PrefixArguments is not null)
            result.AddRange(runtime.PrefixArguments);
        if (runtime.Kind == SkillRuntimeKind.PowerShell)
        {
            result.AddRange(["-NoProfile", "-NonInteractive", "-File"]);
        }
        result.Add(scriptPath);
        result.AddRange(arguments);
        return result;
    }

    private static IReadOnlyDictionary<string, string?> BuildEnvironment(
        IEnumerable<string> requiredNames)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in requiredNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('='))
                throw new InvalidDataException($"Invalid required environment variable: {name}");
            var value = Environment.GetEnvironmentVariable(name);
            if (value is null)
                throw new InvalidOperationException($"Required environment variable is missing: {name}");
            environment[name] = value;
        }
        return environment;
    }

    private static IReadOnlyList<string> ReadArguments(
        IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("arguments", out var value) || value is null)
            return [];
        if (value is string[] array)
            return array;
        if (value is IEnumerable<string> strings)
            return strings.ToList();
        if (value is JsonElement { ValueKind: JsonValueKind.Array } json)
            return json.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        throw new ArgumentException("Argument 'arguments' must be an array of strings.");
    }

    private static IReadOnlyList<string> ReadParameters(IReadOnlyDictionary<string,object?> arguments,IReadOnlyDictionary<string,SkillParameterManifest> schema)
    {
        var values=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
        if(arguments.TryGetValue("parameters",out var raw)&&raw is not null)
        {
            if(raw is JsonElement{ValueKind:JsonValueKind.Object} json)foreach(var property in json.EnumerateObject())values[property.Name]=property.Value;
            else if(raw is IReadOnlyDictionary<string,object?> dictionary)foreach(var item in dictionary)values[item.Key]=item.Value;
            else throw new ArgumentException("parameters must be an object.");
        }
        var unknown=values.Keys.FirstOrDefault(key=>!schema.ContainsKey(key));if(unknown is not null)throw new ArgumentException($"Undeclared skill parameter: {unknown}");
        var result=new List<string>();foreach(var(name,definition)in schema){if(!values.TryGetValue(name,out var value)||value is null){if(definition.Required)throw new ArgumentException($"Required skill parameter is missing: {name}");continue;}var text=ConvertParameter(value,definition.Type,name);if(definition.AllowedValues.Count>0&&!definition.AllowedValues.Contains(text,StringComparer.OrdinalIgnoreCase))throw new ArgumentException($"Parameter '{name}' has an unsupported value.");if(!string.IsNullOrWhiteSpace(definition.Flag))result.Add(definition.Flag);result.Add(text);}return result;
    }

    private static string ConvertParameter(object value,string type,string name)
    {
        if(value is JsonElement json)return type switch{"string" when json.ValueKind==JsonValueKind.String=>json.GetString()!,"number" when json.ValueKind==JsonValueKind.Number=>json.GetRawText(),"boolean" when json.ValueKind is JsonValueKind.True or JsonValueKind.False=>json.GetBoolean()?"true":"false",_=>throw new ArgumentException($"Parameter '{name}' must be {type}.")};
        return type switch{"string" when value is string text=>text,"number" when value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal=>Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture)!,"boolean" when value is bool boolean=>boolean?"true":"false",_=>throw new ArgumentException($"Parameter '{name}' must be {type}.")};
    }

    private static void ValidateDependencyFiles(SkillRuntimeManifest manifest,string root)
    {
        if(!string.IsNullOrWhiteSpace(manifest.DependencyFile)&&!File.Exists(YamlSkillManifestLoader.ResolveSkillPath(root,manifest.DependencyFile)))throw new InvalidOperationException($"Skill dependency file is missing: {manifest.DependencyFile}");
        if(!string.IsNullOrWhiteSpace(manifest.LockFile)&&!File.Exists(YamlSkillManifestLoader.ResolveSkillPath(root,manifest.LockFile)))throw new InvalidOperationException($"Skill lock file is missing: {manifest.LockFile}");
    }

    private static bool DependenciesReady(
        SkillRuntimeManifest manifest,
        string root,
        SkillRuntimeKind kind)
    {
        if (string.IsNullOrWhiteSpace(manifest.DependencyFile))
            return true;
        return kind switch
        {
            SkillRuntimeKind.Python => File.Exists(
                Path.Combine(root, ".wingman-runtime", "Scripts", "python.exe")),
            SkillRuntimeKind.Node => Directory.Exists(Path.Combine(root, "node_modules")),
            _ => true,
        };
    }

    private async Task<ToolExecutionResult> InstallDependenciesAsync(
        ToolExecutionRequest request,
        SkillRuntimeManifest manifest,
        string skillRoot,
        ResolvedRuntime runtime,
        SkillRuntimeKind kind,
        CancellationToken ct)
    {
        if (kind == SkillRuntimeKind.PowerShell)
            return new ToolExecutionResult(true, "No PowerShell dependency restore is required.");

        var capabilities = AgentCapability.Write | AgentCapability.Execute;
        if (manifest.InstallNetwork)
            capabilities |= AgentCapability.Network | AgentCapability.ExternalSideEffect;
        var permission = new AgentPermissionRequest(
            "install_skill_dependencies",
            capabilities,
            AgentRiskLevel.High,
            manifest.DependencyFile,
            skillRoot,
            $"Install declared {kind} dependencies for a Skill.");
        var decision = policyEngine.Evaluate(
            new AgentPolicyContext(request.Context.Mode, request.Context.WorkspacePath),
            permission);
        if (decision.Kind == PolicyDecisionKind.Deny)
            return Failure(decision.Reason);

        var approvalRequired = decision.Kind == PolicyDecisionKind.RequireApproval;
        if (approvalRequired)
        {
            var approval = await approvalCoordinator.RequestAsync(request.Context.RunId, permission, ct);
            if (!approval.Approved)
            {
                return Failure(approval.Comment ?? "Dependency installation was rejected.") with
                {
                    ApprovalRequired = true,
                    ApprovalResult = "rejected",
                };
            }
        }

        var cacheRoot = configuration?["Runtime:PackageCacheRoot"];
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".Wingman",
                "package-cache");
        }
        var cache = Path.Combine(Path.GetFullPath(cacheRoot), kind.ToString().ToLowerInvariant());
        Directory.CreateDirectory(cache);

        ProcessExecutionResult result;
        if (kind == SkillRuntimeKind.Python)
        {
            var venv = Path.Combine(skillRoot, ".wingman-runtime");
            var createArguments = new List<string>();
            if (runtime.PrefixArguments is not null)
                createArguments.AddRange(runtime.PrefixArguments);
            createArguments.AddRange(["-m", "venv", venv]);
            var create = await processRunner.RunAsync(new ProcessInvocation(
                runtime.ExecutablePath,
                createArguments,
                skillRoot,
                TimeSpan.FromMinutes(5)), ct);
            if (create.ExitCode != 0 || create.TimedOut)
                return ProcessFailure(create, "Unable to create the Skill Python environment.");

            var python = Path.Combine(venv, "Scripts", "python.exe");
            var dependencyFile = YamlSkillManifestLoader.ResolveSkillPath(
                skillRoot,
                manifest.DependencyFile!);
            var arguments = new List<string>
            {
                "-m", "pip", "install", "--disable-pip-version-check",
                "--cache-dir", cache,
            };
            if (!manifest.InstallNetwork)
                arguments.AddRange(["--no-index", "--find-links", cache]);
            arguments.AddRange(["-r", dependencyFile]);
            result = await processRunner.RunAsync(new ProcessInvocation(
                python,
                arguments,
                skillRoot,
                TimeSpan.FromMinutes(15)), ct);
        }
        else
        {
            var manager = (manifest.PackageManager ?? "npm").Trim().ToLowerInvariant();
            if (manager is not ("npm" or "pnpm" or "yarn"))
                return Failure($"Unsupported Node package manager: {manager}");
            var executable = ResolvePackageManager(runtime.ExecutablePath, manager);
            var arguments = BuildNodeInstallArguments(manager, manifest, cache);
            result = await processRunner.RunAsync(new ProcessInvocation(
                executable,
                arguments,
                skillRoot,
                TimeSpan.FromMinutes(15)), ct);
        }

        if (result.ExitCode != 0 || result.TimedOut)
            return ProcessFailure(result, "Skill dependency installation failed.");
        return new ToolExecutionResult(
            true,
            redactor?.Redact(result.StandardOutput) ?? result.StandardOutput,
            ExitCode: result.ExitCode,
            TimedOut: result.TimedOut,
            DurationMs: result.DurationMs,
            ApprovalRequired: approvalRequired,
            ApprovalResult: approvalRequired ? "approved" : null);
    }

    private static IReadOnlyList<string> BuildNodeInstallArguments(
        string manager,
        SkillRuntimeManifest manifest,
        string cache)
    {
        var args = manager switch
        {
            "npm" => new List<string> { string.IsNullOrWhiteSpace(manifest.LockFile) ? "install" : "ci", "--cache", cache },
            "pnpm" => ["install", "--frozen-lockfile", "--store-dir", cache],
            _ => ["install", "--immutable", "--cache-folder", cache],
        };
        if (!manifest.InstallNetwork)
            args.Add("--offline");
        return args;
    }

    private static string ResolvePackageManager(string nodeExecutable, string manager)
    {
        var sibling = Path.Combine(Path.GetDirectoryName(nodeExecutable)!, manager + ".cmd");
        return File.Exists(sibling) ? sibling : manager + ".cmd";
    }

    private static ToolExecutionResult ProcessFailure(ProcessExecutionResult result, string prefix) =>
        new(false, "", $"{prefix} {result.StandardError}", result.ExitCode, result.TimedOut, result.DurationMs);

    private static bool ReadBoolean(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null)
            return false;
        return value switch
        {
            bool boolean => boolean,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => throw new ArgumentException($"Argument '{name}' must be boolean."),
        };
    }

    private static AgentRiskLevel MaxRisk(AgentRiskLevel baseline, string? libraryLevel)
    {
        var libraryRisk = libraryLevel?.Trim().ToLowerInvariant() switch
        {
            "critical" => AgentRiskLevel.Critical,
            "high" => AgentRiskLevel.High,
            "medium" => AgentRiskLevel.Medium,
            _ => AgentRiskLevel.Low,
        };
        return libraryRisk > baseline ? libraryRisk : baseline;
    }

    private static string HashFile(string path) => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

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

    private static bool ExtensionMatches(SkillRuntimeKind kind, string path) =>
        Path.GetExtension(path).ToLowerInvariant() == kind switch
        {
            SkillRuntimeKind.Python => ".py",
            SkillRuntimeKind.Node => ".js",
            SkillRuntimeKind.PowerShell => ".ps1",
            _ => "",
        };

    private static ToolExecutionResult Failure(string error) =>
        new(false, "", error);
}

public sealed class RunSkillScriptTool(ISkillScriptRunner runner):IAgentTool
{
    public ToolDescriptor Descriptor { get; }=new("run_skill_script","Run a declared Python, Node.js, or PowerShell skill entrypoint.",AgentCapability.Execute,AgentRiskLevel.Medium,TimeSpan.FromSeconds(120),Source:"skill");
    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request,CancellationToken ct=default)=>runner.ExecuteAsync(request,ct);
}
