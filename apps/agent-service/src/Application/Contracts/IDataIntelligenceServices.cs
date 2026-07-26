using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public interface IDomainGlossaryStore
{
    Task<IReadOnlyList<DomainGlossaryEntry>> ListAsync(string projectId, GlossaryProposalStatus? status = null, CancellationToken cancellationToken = default);
    Task<DomainGlossaryEntry?> GetAsync(string projectId, string id, CancellationToken cancellationToken = default);
    Task<DomainGlossaryEntry> ProposeAsync(string projectId, ProposeGlossaryEntryRequest request, CancellationToken cancellationToken = default);
    Task<DomainGlossaryEntry> ReviewAsync(string projectId, string id, ReviewGlossaryEntryRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Discovers enabled Database Runtime Plugins and invokes only their safe, structured capabilities.</summary>
public interface IDatabaseRuntimeEvidenceCoordinator
{
    Task<IReadOnlyList<DatabaseRuntimeProviderStatus>> GetStatusAsync(string projectId, bool forceRefresh = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeEvidence>> FindConfigurationAsync(string projectId, DatabaseConfigurationLookup lookup, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeEvidence>> ReadConfigurationAsync(string projectId, DatabaseConfigurationLookup lookup, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeEvidence>> InspectSchemaAsync(string projectId, DatabaseSchemaInspectionRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeEvidence>> ExecuteReadOnlyQueryAsync(string projectId, DatabaseReadOnlyQueryPlan plan, IRuntimeQueryBindingSource bindings, CancellationToken cancellationToken = default);
}
