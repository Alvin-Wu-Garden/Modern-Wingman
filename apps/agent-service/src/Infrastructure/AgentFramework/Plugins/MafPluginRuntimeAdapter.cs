using System.Text;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;
using AgentService.Infrastructure.Marketplace;

namespace AgentService.Infrastructure.AgentFramework.Plugins;

/// <summary>
/// MAF 與 Marketplace 的唯一橋接點。Marketplace 不直接依賴 MAF；每次建立 Agent 時使用一份 immutable snapshot。
/// </summary>
public sealed class MafPluginRuntimeAdapter(
    IEnabledPluginCapabilitySource capabilitySource,
    PluginRuntimeToolRegistrar toolRegistrar) : IPluginCapabilitySnapshotInvalidator, IPluginRuntimeEnablementObserver
{
    private readonly object _gate = new();
    private IReadOnlyList<EnabledPluginCapabilities>? _snapshot;

    public IReadOnlyList<EnabledPluginCapabilities> GetSnapshot()
    {
        lock (_gate)
        {
            if (_snapshot is null)
            {
                _snapshot = capabilitySource.GetSnapshotAsync().ConfigureAwait(false).GetAwaiter().GetResult().ToArray();
                // The registry is reconciled only while creating an Agent. A running Agent keeps its
                // current tool snapshot; enable/disable takes effect for the next creation.
                toolRegistrar.Reconcile(_snapshot);
            }
            return _snapshot;
        }
    }

    public void Invalidate()
    {
        lock (_gate) _snapshot = null;
    }

    public void OnPluginEnablementChanged(string pluginId, bool enabled)
    {
        // Existing Agent instances deliberately retain their immutable prompt/snapshot. The
        // global dispatch registry must nevertheless reject a disabled function immediately.
        if (!enabled) toolRegistrar.Remove(pluginId);
    }

    public string BuildContextPrompt()
    {
        var snapshot = GetSnapshot();
        if (snapshot.Count == 0) return string.Empty;
        var prompt = new StringBuilder("\n## Enabled Wingman Plugins\n");
        foreach (var plugin in snapshot)
        {
            prompt.Append("- ").Append(plugin.PluginId).Append("@").Append(plugin.Version);
            if (plugin.SkillPaths.Count > 0) prompt.Append(" (skills: ").Append(string.Join(", ", plugin.SkillPaths)).Append(')');
            if (plugin.McpPaths.Count > 0) prompt.Append(" (MCP configured by plugin)");
            if (plugin.FunctionIds.Count > 0) prompt.Append(" (functions: ").Append(string.Join(", ", plugin.FunctionIds)).Append(')');
            prompt.AppendLine();
        }
        return prompt.ToString();
    }
}
