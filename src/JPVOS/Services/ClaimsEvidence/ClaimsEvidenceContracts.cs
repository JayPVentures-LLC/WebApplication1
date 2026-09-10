namespace JPVOS.Services.ClaimsEvidence;

public sealed record CreateCaseCommand(
    ClaimsIdentityMode IdentityMode,
    string ClaimStatement,
    string SubjectDescription,
    string? RelevantDates,
    string? Jurisdictions,
    string? AffectedParties,
    bool ConfidentialityRequested,
    UrgencyIndicators Urgency,
    IReadOnlyList<EvidenceMetadata> Evidence,
    bool SubmissionAcknowledged);

public sealed record AddEvidenceCommand(EvidenceMetadata Evidence);

public sealed record CreateCaseResult(
    string CaseId,
    string TrackingCredential,
    string Status,
    DateTime ReceivedAtUtc);

public sealed record AddEvidenceResult(
    string CaseId,
    string EvidenceId,
    string Status,
    DateTime ReceivedAtUtc);

public sealed record CaseStatusResult(
    string CaseId,
    string Status,
    DateTime LastUpdatedAtUtc,
    bool AdditionalEvidenceRequested = false);

public sealed record TrackingCredentialIssue(string Plaintext, string Verifier);
public sealed record IdempotentOperationResult(bool IsReplay, string ResultJson);

public interface ITrackingCredentialService
{
    TrackingCredentialIssue Issue();
    bool Verify(string candidate, string verifier);
}

public interface IEvidenceBlobStore
{
    bool IsEnabled { get; }
    Task<string> StoreAsync(Stream content, string contentType, CancellationToken cancellationToken);
}

public interface IClaimsEvidenceEventStore
{
    Task<IdempotentOperationResult> CreateCaseAtomicallyAsync(
        string operationScope,
        string idempotencyKey,
        string requestHash,
        string caseId,
        string trackingVerifier,
        IReadOnlyList<ClaimsEvidenceEvent> events,
        string resultJson,
        CancellationToken cancellationToken);

    Task<IdempotentOperationResult> AppendAtomicallyAsync(
        string operationScope,
        string idempotencyKey,
        string requestHash,
        ClaimsEvidenceEvent @event,
        string resultJson,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ClaimsEvidenceEvent>> ReadStreamAsync(string caseId, CancellationToken cancellationToken);
    Task<string?> GetTrackingVerifierAsync(string caseId, CancellationToken cancellationToken);
}

public interface IClaimsEvidenceService
{
    Task<CreateCaseResult> CreateCaseAsync(CreateCaseCommand command, string idempotencyKey, CancellationToken cancellationToken);
    Task<AddEvidenceResult> AddEvidenceAsync(string caseId, AddEvidenceCommand command, string idempotencyKey, string trackingCredential, CancellationToken cancellationToken);
    Task<CaseStatusResult?> GetStatusAsync(string caseId, string trackingCredential, CancellationToken cancellationToken);
}

public sealed class ClaimsEvidenceValidationException(string message) : Exception(message);
public sealed class ClaimsEvidenceAuthorizationException(string message) : Exception(message);
public sealed class ClaimsEvidenceIdempotencyConflictException(string message) : Exception(message);
public sealed class ClaimsEvidenceBinaryUnsupportedException(string message) : Exception(message);
public sealed class ClaimsEvidencePersistenceException(string message, Exception? inner = null) : Exception(message, inner);
