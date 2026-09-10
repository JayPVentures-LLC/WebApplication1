using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JPVOS.Services.ClaimsEvidence;

public sealed class ClaimsEvidenceService : IClaimsEvidenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IClaimsEvidenceEventStore _store;
    private readonly ITrackingCredentialService _trackingCredentials;
    private readonly IEvidenceBlobStore _blobStore;
    private readonly ClaimsEvidenceProjector _projector;

    public ClaimsEvidenceService(
        IClaimsEvidenceEventStore store,
        ITrackingCredentialService trackingCredentials,
        IEvidenceBlobStore blobStore,
        ClaimsEvidenceProjector projector)
    {
        _store = store;
        _trackingCredentials = trackingCredentials;
        _blobStore = blobStore;
        _projector = projector;
    }

    public async Task<CreateCaseResult> CreateCaseAsync(CreateCaseCommand command, string idempotencyKey, CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        ValidateCreate(command);

        if (command.Evidence.Any(e => e.IncludesBinaryContent))
            throw new ClaimsEvidenceBinaryUnsupportedException("Binary evidence ingestion is not enabled for this intake contract.");

        var caseId = $"jpv_case_{Guid.NewGuid():N}";
        var issued = _trackingCredentials.Issue();
        var receivedAt = DateTime.UtcNow;
        var events = new List<ClaimsEvidenceEvent>
        {
            NewEvent(caseId, ClaimsEvidenceEventType.CaseReceived, idempotencyKey, command.ConfidentialityRequested ? "confidential" : "restricted",
                JsonSerializer.Serialize(new
                {
                    identityMode = command.IdentityMode.ToWireValue(),
                    command.ClaimStatement,
                    command.SubjectDescription,
                    command.RelevantDates,
                    command.Jurisdictions,
                    command.AffectedParties,
                    command.ConfidentialityRequested,
                    command.Urgency,
                    submissionAcknowledged = command.SubmissionAcknowledged,
                    publicStatus = CasePublicStatus.Received.ToWireValue()
                }, JsonOptions), receivedAt)
        };

        foreach (var evidence in command.Evidence)
        {
            var evidenceId = $"jpv_ev_{Guid.NewGuid():N}";
            events.Add(NewEvent(caseId, ClaimsEvidenceEventType.EvidenceAdded, idempotencyKey, NormalizeSensitivity(evidence.Sensitivity),
                JsonSerializer.Serialize(new { evidenceId, evidence }, JsonOptions), receivedAt));
        }

        var result = new CreateCaseResult(caseId, issued.Plaintext, CasePublicStatus.Received.ToWireValue(), receivedAt);
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        var operation = await _store.CreateCaseAtomicallyAsync(
            "create-case",
            idempotencyKey,
            ComputeRequestHash(command),
            caseId,
            issued.Verifier,
            events,
            resultJson,
            cancellationToken);

        return JsonSerializer.Deserialize<CreateCaseResult>(operation.ResultJson, JsonOptions)
            ?? throw new ClaimsEvidencePersistenceException("Stored create-case result could not be reconstructed.");
    }

    public async Task<AddEvidenceResult> AddEvidenceAsync(string caseId, AddEvidenceCommand command, string idempotencyKey, string trackingCredential, CancellationToken cancellationToken)
    {
        ValidateCaseId(caseId);
        ValidateIdempotencyKey(idempotencyKey);
        await AuthorizeCaseAsync(caseId, trackingCredential, cancellationToken);
        ValidateEvidence(command.Evidence);

        if (command.Evidence.IncludesBinaryContent)
            throw new ClaimsEvidenceBinaryUnsupportedException(_blobStore.IsEnabled
                ? "Binary evidence requires the dedicated authorized upload path."
                : "Binary evidence ingestion is not enabled because no approved private evidence store is configured.");

        var evidenceId = $"jpv_ev_{Guid.NewGuid():N}";
        var receivedAt = DateTime.UtcNow;
        var @event = NewEvent(
            caseId,
            ClaimsEvidenceEventType.EvidenceAdded,
            idempotencyKey,
            NormalizeSensitivity(command.Evidence.Sensitivity),
            JsonSerializer.Serialize(new { evidenceId, evidence = command.Evidence }, JsonOptions),
            receivedAt);
        var result = new AddEvidenceResult(caseId, evidenceId, "received", receivedAt);
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        var operation = await _store.AppendAtomicallyAsync(
            $"add-evidence:{caseId}",
            idempotencyKey,
            ComputeRequestHash(command),
            @event,
            resultJson,
            cancellationToken);

        return JsonSerializer.Deserialize<AddEvidenceResult>(operation.ResultJson, JsonOptions)
            ?? throw new ClaimsEvidencePersistenceException("Stored evidence result could not be reconstructed.");
    }

    public async Task<CaseStatusResult?> GetStatusAsync(string caseId, string trackingCredential, CancellationToken cancellationToken)
    {
        ValidateCaseId(caseId);
        await AuthorizeCaseAsync(caseId, trackingCredential, cancellationToken);
        var events = await _store.ReadStreamAsync(caseId, cancellationToken);
        return _projector.ProjectPublicStatus(events);
    }

    private async Task AuthorizeCaseAsync(string caseId, string trackingCredential, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trackingCredential))
            throw new ClaimsEvidenceAuthorizationException("Case tracking credential is required.");

        var verifier = await _store.GetTrackingVerifierAsync(caseId, cancellationToken);
        if (verifier is null || !_trackingCredentials.Verify(trackingCredential, verifier))
            throw new ClaimsEvidenceAuthorizationException("Case tracking credential is invalid.");
    }

    private static void ValidateCreate(CreateCaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(command.IdentityMode)) throw new ClaimsEvidenceValidationException("Identity mode is invalid.");
        if (string.IsNullOrWhiteSpace(command.ClaimStatement)) throw new ClaimsEvidenceValidationException("Claim statement is required.");
        if (string.IsNullOrWhiteSpace(command.SubjectDescription)) throw new ClaimsEvidenceValidationException("Subject description is required.");
        if (!command.SubmissionAcknowledged) throw new ClaimsEvidenceValidationException("Submission acknowledgment is required.");
        if (command.ClaimStatement.Length > 20_000) throw new ClaimsEvidenceValidationException("Claim statement exceeds the allowed length.");
        if (command.SubjectDescription.Length > 4_000) throw new ClaimsEvidenceValidationException("Subject description exceeds the allowed length.");
        foreach (var evidence in command.Evidence) ValidateEvidence(evidence);
    }

    private static void ValidateEvidence(EvidenceMetadata evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(evidence.Description)) throw new ClaimsEvidenceValidationException("Evidence description is required.");
        if (string.IsNullOrWhiteSpace(evidence.SourceDescription)) throw new ClaimsEvidenceValidationException("Evidence source description is required.");
        if (!string.IsNullOrWhiteSpace(evidence.Sha256Digest) && (evidence.Sha256Digest.Length != 64 || evidence.Sha256Digest.Any(c => !Uri.IsHexDigit(c))))
            throw new ClaimsEvidenceValidationException("Evidence SHA-256 digest must contain exactly 64 hexadecimal characters.");
        if (!string.IsNullOrWhiteSpace(evidence.StorageReference) && !evidence.StorageReference.StartsWith("jpv-private://", StringComparison.Ordinal))
            throw new ClaimsEvidenceValidationException("Evidence storage reference must be an authorized private JPV reference.");
    }

    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ClaimsEvidenceValidationException("Idempotency-Key is required.");
        if (idempotencyKey.Length is < 8 or > 200) throw new ClaimsEvidenceValidationException("Idempotency-Key must be between 8 and 200 characters.");
    }

    private static void ValidateCaseId(string caseId)
    {
        if (string.IsNullOrWhiteSpace(caseId) || !caseId.StartsWith("jpv_case_", StringComparison.Ordinal) || caseId.Length != 41)
            throw new ClaimsEvidenceValidationException("Case ID is invalid.");
    }

    private static ClaimsEvidenceEvent NewEvent(string caseId, ClaimsEvidenceEventType type, string? idempotencyKey, string sensitivity, string payloadJson, DateTime occurredAtUtc) =>
        new($"jpv_evt_{Guid.NewGuid():N}", caseId, 0, occurredAtUtc, type, "submitter", idempotencyKey, sensitivity, payloadJson);

    private static string ComputeRequestHash<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static string NormalizeSensitivity(string? sensitivity) => sensitivity?.Trim().ToLowerInvariant() switch
    {
        "public" => "restricted",
        "restricted" => "restricted",
        "confidential" => "confidential",
        "highly-confidential" => "highly-confidential",
        _ => "restricted"
    };
}
