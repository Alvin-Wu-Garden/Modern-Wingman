using AgentService.Domain.Models;

namespace AgentService.Application.Models;

public sealed record DataArtifact(string FilePath, string RelativePath, string Content, string ContentHash);

public sealed record DataExtractionDiagnostic(
    string FilePath,
    string AdapterId,
    string Severity,
    string Message);

public sealed record DataArtifactScanRecord(
    string Path,
    string Technology,
    string Status,
    string? Reason,
    string ContentHash);

public sealed record DataExtractionResult(
    CodeAnalysisResult Graph,
    IReadOnlyList<DataExtractionDiagnostic> Diagnostics,
    IReadOnlyList<string> CapabilityGaps,
    IReadOnlyList<DataArtifactScanRecord>? ScannedFiles = null,
    IReadOnlyList<DataArtifactScanRecord>? SkippedFiles = null);

public sealed record DataScanReport(
    int NodeCount,
    int EdgeCount,
    IReadOnlyList<DataExtractionDiagnostic> Diagnostics,
    IReadOnlyList<string> CapabilityGaps,
    IReadOnlyList<DataArtifactScanRecord> ScannedFiles,
    IReadOnlyList<DataArtifactScanRecord> SkippedFiles);

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
