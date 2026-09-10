using System.Text.Json.Serialization;

namespace JPVOS.Services.ClaimsEvidence;

public enum ClaimsIdentityMode
{
    Anonymous,
    Pseudonymous,
    VerifiedConfidential
}

public static class ClaimsIdentityModeExtensions
{
    public static string ToWireValue(this ClaimsIdentityMode mode) => mode switch
    {
        ClaimsIdentityMode.Anonymous => "anonymous",
        ClaimsIdentityMode.Pseudonymous => "pseudonymous",
        ClaimsIdentityMode.VerifiedConfidential => "verified-confidential",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static bool TryParseWireValue(string? value, out ClaimsIdentityMode mode)
    {
        mode = value?.Trim().ToLowerInvariant() switch
        {
            "anonymous" => ClaimsIdentityMode.Anonymous,
            "pseudonymous" => ClaimsIdentityMode.Pseudonymous,
            "verified-confidential" => ClaimsIdentityMode.VerifiedConfidential,
            _ => (ClaimsIdentityMode)(-1)
        };
        return Enum.IsDefined(mode);
    }
}

public enum ClaimsEvidenceEventType
{
    CaseReceived,
    EvidenceAdded,
    VerificationUpdated,
    CorrectionRequested,
    CorrectionResolved,
    StatusChanged,
    ConflictRecorded,
    Routed,
    RemediationRecorded,
    StewardshipReviewed,
    PublicationReviewed,
    FounderCommentaryRecorded,
    EvidenceDispositionRecorded,
    CaseClosed,
    CaseReopened
}

public enum CasePublicStatus
{
    Received,
    VerificationInProgress,
    AdditionalEvidenceRequested,
    Routed,
    ActionInProgress,
    Closed,
    Resolved,
    Reopened
}

public static class CasePublicStatusExtensions
{
    public static string ToWireValue(this CasePublicStatus status) => status switch
    {
        CasePublicStatus.Received => "received",
        CasePublicStatus.VerificationInProgress => "verification-in-progress",
        CasePublicStatus.AdditionalEvidenceRequested => "additional-evidence-requested",
        CasePublicStatus.Routed => "routed",
        CasePublicStatus.ActionInProgress => "action-in-progress",
        CasePublicStatus.Closed => "closed",
        CasePublicStatus.Resolved => "resolved",
        CasePublicStatus.Reopened => "reopened",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}

public sealed record ClaimsEvidenceEvent(
    string EventId,
    string CaseId,
    long Sequence,
    DateTime OccurredAtUtc,
    ClaimsEvidenceEventType Type,
    string ActorClass,
    string? IdempotencyKey,
    string Sensitivity,
    string PayloadJson);

public sealed record EvidenceMetadata(
    string Description,
    string SourceDescription,
    string? DateCreated,
    string Sensitivity,
    string? AuthenticityLimitations,
    string? RelationshipToClaim,
    string? Sha256Digest = null,
    string? StorageReference = null,
    bool IncludesBinaryContent = false);

public sealed record UrgencyIndicators(
    bool ImmediateSafetyThreat = false,
    bool EvidenceDestructionRisk = false,
    bool ExpiringLegalDeadline = false,
    bool OngoingSeriousHarm = false,
    bool CredibleRetaliationRisk = false);
