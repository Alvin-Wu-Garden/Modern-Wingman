using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;

/// <summary>
/// Adapts enabled Wingman Plugin MCP servers to P3 runtime evidence. Only readOnlyHint tools with
/// canonical capability names are called, and their output is projected to the value-free RuntimeEvidence model.
/// </summary>
public sealed class McpDatabaseRuntimeEvidenceProvider(
    IEnumerable<IPluginMcpServerSource> pluginSources,
    IMcpClientRuntime runtime,
    IProjectRepository projects,
    IDatabaseRuntimeEvidenceRequestValidator requestValidator,
    IReadOnlyDatabaseQueryPlanValidator queryValidator) : IDatabaseRuntimeEvidenceCoordinator
{
    private const int MaximumOutputBytes = 1_048_576;
    private static readonly TimeSpan DiscoveryTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumEvidenceTtl = TimeSpan.FromMinutes(15);
    private static readonly IReadOnlyDictionary<string, DatabaseRuntimeCapability> CapabilityNames =
        new Dictionary<string, DatabaseRuntimeCapability>(StringComparer.OrdinalIgnoreCase)
        {
            ["inspect_schema"] = DatabaseRuntimeCapability.InspectSchema,
            ["find_configuration"] = DatabaseRuntimeCapability.FindConfiguration,
            ["read_configuration"] = DatabaseRuntimeCapability.ReadConfiguration,
            ["validate_query_plan"] = DatabaseRuntimeCapability.ValidateQueryPlan,
            ["execute_readonly_query"] = DatabaseRuntimeCapability.ExecuteReadOnlyQuery,
        };
    private static readonly HashSet<string> ForbiddenPayloadNames = new(StringComparer.OrdinalIgnoreCase)
        { "value", "rawValue", "rows", "row", "records", "record", "data", "secret", "token", "password", "connectionString", "credentials" };

    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private ProviderSnapshot? _snapshot;

    public async Task<IReadOnlyList<DatabaseRuntimeProviderStatus>> GetStatusAsync(string projectId, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (forceRefresh) _snapshot = null;
        var providers = await DiscoverScopedAsync(projectId, cancellationToken);
        return providers.Select(provider => new DatabaseRuntimeProviderStatus(
            PluginId(provider.Server), provider.Server.Name, provider.Tools.Values.Select(tool => tool.Capability).ToHashSet(),
            provider.Error is null, provider.Error)).ToList();
    }

    public Task<IReadOnlyList<RuntimeEvidence>> FindConfigurationAsync(string projectId, DatabaseConfigurationLookup lookup, CancellationToken cancellationToken = default)
    {
        var validation = requestValidator.Validate(lookup);
        if (!validation.IsValid) throw new ArgumentException(string.Join(" ", validation.Errors), nameof(lookup));
        return InvokeAsync(projectId, DatabaseRuntimeCapability.FindConfiguration, new
        {
            projectId,
            key = lookup.Key, @namespace = lookup.Namespace, featureName = lookup.FeatureName,
            table = lookup.Table, column = lookup.Column, environment = lookup.Environment,
            tenantScope = lookup.TenantScope, maxResults = lookup.MaxResults,
        }, lookup.MaxResults, cancellationToken);
    }

    public Task<IReadOnlyList<RuntimeEvidence>> ReadConfigurationAsync(string projectId, DatabaseConfigurationLookup lookup, CancellationToken cancellationToken = default)
    {
        var validation = requestValidator.Validate(lookup);
        if (!validation.IsValid) throw new ArgumentException(string.Join(" ", validation.Errors), nameof(lookup));
        return InvokeAsync(projectId, DatabaseRuntimeCapability.ReadConfiguration, new
        {
            projectId,
            key = lookup.Key, @namespace = lookup.Namespace, featureName = lookup.FeatureName,
            table = lookup.Table, column = lookup.Column, environment = lookup.Environment,
            tenantScope = lookup.TenantScope, maxResults = lookup.MaxResults,
        }, lookup.MaxResults, cancellationToken);
    }

    public Task<IReadOnlyList<RuntimeEvidence>> InspectSchemaAsync(string projectId, DatabaseSchemaInspectionRequest request, CancellationToken cancellationToken = default)
    {
        var validation = requestValidator.Validate(request);
        if (!validation.IsValid) throw new ArgumentException(string.Join(" ", validation.Errors), nameof(request));
        return InvokeAsync(projectId, DatabaseRuntimeCapability.InspectSchema, new
        {
            projectId,
            schemas = request.Schemas, objectNames = request.ObjectNames, maxResults = request.MaxResults,
        }, request.MaxResults, cancellationToken);
    }

    public async Task<IReadOnlyList<RuntimeEvidence>> ExecuteReadOnlyQueryAsync(string projectId, DatabaseReadOnlyQueryPlan plan, IRuntimeQueryBindingSource bindings, CancellationToken cancellationToken = default)
    {
        var validation = queryValidator.Validate(plan);
        if (!validation.IsValid) throw new ArgumentException(string.Join(" ", validation.Errors), nameof(plan));
        foreach (var parameter in plan.Parameters)
            if (!bindings.Contains(parameter.Name)) throw new ArgumentException($"缺少參數 binding: {parameter.Name}", nameof(bindings));

        // Binding values exist only in this call object and are never logged, persisted, or returned.
        var values = plan.Parameters.ToDictionary(item => item.Name, item => bindings.GetValue(item.Name), StringComparer.OrdinalIgnoreCase);
        return await InvokeAsync(projectId, DatabaseRuntimeCapability.ExecuteReadOnlyQuery, new
        {
            projectId,
            statement = plan.Statement,
            parameters = values,
            objectAllowlist = plan.ObjectAllowlist.Select(item => new { schema = item.Schema, objectName = item.ObjectName, allowedColumns = item.AllowedColumns }),
            rowLimit = plan.RowLimit,
            timeoutMilliseconds = (int)plan.Timeout.TotalMilliseconds,
            maxResultBytes = plan.MaxResultBytes,
        }, plan.RowLimit, cancellationToken, plan.Timeout);
    }

    private async Task<IReadOnlyList<RuntimeEvidence>> InvokeAsync(string projectId, DatabaseRuntimeCapability capability, object arguments, int maxResults, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var providers = (await DiscoverScopedAsync(projectId, cancellationToken)).Where(provider => provider.Tools.ContainsKey(capability)).Take(4).ToList();
        if (providers.Count == 0) return [];
        var tasks = providers.Select(provider => InvokeProviderAsync(provider, capability, arguments, Math.Clamp(maxResults, 1, 100), timeout, cancellationToken));
        return (await Task.WhenAll(tasks)).SelectMany(items => items).Take(Math.Clamp(maxResults, 1, 100)).ToList();
    }

    private async Task<IReadOnlyList<Provider>> DiscoverScopedAsync(
        string projectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var project = await projects.GetAsync(projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"找不到專案: {projectId}");
        var root = Path.GetFullPath(project.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return (await DiscoverAsync(cancellationToken))
            .Where(provider => IsScopedToProject(provider.Server, projectId, root))
            .ToList();
    }

    internal static bool IsScopedToProject(McpServerDefinition server, string projectId, string projectRoot)
    {
        if (server.Environment.TryGetValue("WINGMAN_PROJECT_ID", out var configuredId))
            return string.Equals(configuredId?.Trim(), projectId, StringComparison.Ordinal);
        if (!server.Environment.TryGetValue("WINGMAN_PROJECT_PATH", out var configuredPath) ||
            string.IsNullOrWhiteSpace(configuredPath))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(configuredPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                projectRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<RuntimeEvidence>> InvokeProviderAsync(Provider provider, DatabaseRuntimeCapability capability, object arguments, int maxResults, TimeSpan? requestedTimeout, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestedTimeout is { } specified && specified < TimeSpan.FromSeconds(30) ? specified : TimeSpan.FromSeconds(30));
        var tool = provider.Tools[capability].Tool;
        var call = await runtime.CallToolAsync(provider.Server, tool.Name, JsonSerializer.SerializeToElement(arguments), timeout.Token);
        if (!call.Success || string.IsNullOrWhiteSpace(call.Output)) return [];
        if (System.Text.Encoding.UTF8.GetByteCount(call.Output) > MaximumOutputBytes)
            throw new InvalidDataException("Database Runtime Plugin 回應超過大小上限。");
        return ParseSafeEvidence(call.Output, provider.Server, capability, maxResults);
    }

    private async Task<IReadOnlyList<Provider>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var current = _snapshot;
        if (current is not null && current.ExpiresAt > DateTimeOffset.UtcNow) return current.Providers;
        await _discoveryGate.WaitAsync(cancellationToken);
        try
        {
            current = _snapshot;
            if (current is not null && current.ExpiresAt > DateTimeOffset.UtcNow) return current.Providers;
            var servers = new List<McpServerDefinition>();
            foreach (var source in pluginSources)
                servers.AddRange(await source.ListEnabledAsync(cancellationToken));

            var providers = new List<Provider>();
            foreach (var server in servers.Where(item => item.Enabled).GroupBy(item => item.Id).Select(group => group.First()))
            {
                IReadOnlyList<McpToolDefinition> tools;
                try { tools = await runtime.DiscoverToolsAsync(server, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    providers.Add(new(server, new Dictionary<DatabaseRuntimeCapability, CapabilityTool>(),
                        "Database Runtime Plugin tool discovery failed."));
                    continue;
                }
                var capabilities = tools.Where(tool => tool.ReadOnly)
                    .Select(tool => (Tool: tool, Name: CanonicalName(tool.Name)))
                    .Where(item => CapabilityNames.ContainsKey(item.Name))
                    .GroupBy(item => CapabilityNames[item.Name])
                    .ToDictionary(group => group.Key, group => new CapabilityTool(group.Key, group.First().Tool));
                if (capabilities.Count > 0) providers.Add(new(server, capabilities, null));
            }
            _snapshot = new(providers, DateTimeOffset.UtcNow.Add(DiscoveryTtl));
            return providers;
        }
        finally { _discoveryGate.Release(); }
    }

    private static IReadOnlyList<RuntimeEvidence> ParseSafeEvidence(string output, McpServerDefinition server, DatabaseRuntimeCapability capability, int maxResults)
    {
        var json = StripCodeFence(output.Trim());
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        var elements = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array
                ? evidence.EnumerateArray().ToList()
                : [root];
        var result = new List<RuntimeEvidence>();
        var now = DateTimeOffset.UtcNow;
        foreach (var item in elements.Take(maxResults))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            RejectRawPayload(item);
            var subject = RequiredString(item, "subject");
            var state = ParseEnum<RuntimeEvidenceState>(RequiredString(item, "state"));
            var redaction = ParseEnum<RuntimeEvidenceRedaction>(OptionalString(item, "redaction") ?? nameof(RuntimeEvidenceRedaction.DerivedOnly));
            if (redaction == RuntimeEvidenceRedaction.NotApplicable && capability is DatabaseRuntimeCapability.FindConfiguration or DatabaseRuntimeCapability.ReadConfiguration)
                redaction = RuntimeEvidenceRedaction.DerivedOnly;
            var observed = OptionalDate(item, "observedAt") ?? now;
            var requestedExpiry = OptionalDate(item, "expiresAt") ?? observed.AddMinutes(5);
            var expiry = requestedExpiry > observed.Add(MaximumEvidenceTtl) ? observed.Add(MaximumEvidenceTtl) : requestedExpiry;
            if (expiry <= observed || expiry <= now) continue;
            result.Add(new RuntimeEvidence(
                OptionalString(item, "id") ?? $"runtime:{server.Name}:{capability}:{result.Count}",
                PluginId(server), OptionalString(item, "databaseIdentity") ?? server.Name,
                CapabilityName(capability), subject, state, redaction, observed, expiry,
                OptionalInt(item, "matchedRecordCount") ?? 0, OptionalDate(item, "sourceUpdatedAt")));
        }
        return result;
    }

    private static void RejectRawPayload(JsonElement item)
    {
        RejectRawPayload(item, "$", 0);
    }

    private static void RejectRawPayload(JsonElement item, string path, int depth)
    {
        if (depth > 16)
            throw new InvalidDataException("Database Runtime Plugin 回應巢狀層級過深。");
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in item.EnumerateObject())
            {
                if (ForbiddenPayloadNames.Contains(property.Name))
                    throw new InvalidDataException($"Database Runtime Plugin 回應包含禁止欄位 '{path}.{property.Name}'；只允許衍生狀態證據。");
                RejectRawPayload(property.Value, $"{path}.{property.Name}", depth + 1);
            }
        }
        else if (item.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in item.EnumerateArray())
                RejectRawPayload(value, path, depth + 1);
        }
    }

    private static string CanonicalName(string name)
    {
        var index = name.LastIndexOfAny([':', '/', '.']);
        return (index >= 0 ? name[(index + 1)..] : name).Trim().ToLowerInvariant();
    }
    private static string CapabilityName(DatabaseRuntimeCapability capability) => CapabilityNames.Single(item => item.Value == capability).Key;
    private static string PluginId(McpServerDefinition server) => $"mcp:{server.Name}";
    private static string StripCodeFence(string value)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal)) return value;
        var newline = value.IndexOf('\n'); var end = value.LastIndexOf("```", StringComparison.Ordinal);
        return newline >= 0 && end > newline ? value[(newline + 1)..end].Trim() : value;
    }
    private static string RequiredString(JsonElement item, string name) => OptionalString(item, name) ?? throw new InvalidDataException($"Runtime evidence 缺少 {name}。");
    private static string? OptionalString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    private static int? OptionalInt(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;
    private static DateTimeOffset? OptionalDate(JsonElement item, string name) => OptionalString(item, name) is { } value && DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    private static T ParseEnum<T>(string value) where T : struct, Enum => Enum.TryParse<T>(value, true, out var parsed) ? parsed : throw new InvalidDataException($"Runtime evidence 狀態 '{value}' 無效。");
    private sealed record CapabilityTool(DatabaseRuntimeCapability Capability, McpToolDefinition Tool);
    private sealed record Provider(
        McpServerDefinition Server,
        IReadOnlyDictionary<DatabaseRuntimeCapability, CapabilityTool> Tools,
        string? Error);
    private sealed record ProviderSnapshot(IReadOnlyList<Provider> Providers, DateTimeOffset ExpiresAt);
}
