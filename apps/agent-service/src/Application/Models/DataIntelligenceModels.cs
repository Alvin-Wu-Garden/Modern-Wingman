namespace AgentService.Application.Models;

public enum GlossaryProposalStatus { Proposed, Confirmed, Rejected }

public enum GlossarySensitivity { Unknown, Public, Internal, Confidential, PersonalData, Secret }

public sealed record DomainGlossaryEntry(
    string Id,
    string ProjectId,
    string Term,
    string Definition,
    IReadOnlyList<string> Aliases,
    GlossarySensitivity Sensitivity,
    GlossaryProposalStatus Status,
    IReadOnlyList<string> EvidenceKeys,
    string ProposedBy,
    string? ReviewedBy,
    string? ReviewComment,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProposeGlossaryEntryRequest(
    string Term,
    string Definition,
    IReadOnlyList<string>? Aliases,
    GlossarySensitivity Sensitivity,
    IReadOnlyList<string>? EvidenceKeys,
    string ProposedBy = "agent");

public sealed record ReviewGlossaryEntryRequest(
    bool Confirm,
    string ReviewedBy,
    string? Definition = null,
    IReadOnlyList<string>? Aliases = null,
    GlossarySensitivity? Sensitivity = null,
    string? Comment = null);

public sealed record DatabaseRuntimeProviderStatus(
    string PluginId,
    string DatabaseIdentity,
    IReadOnlySet<DatabaseRuntimeCapability> Capabilities,
    bool Available,
    string? Error = null);
