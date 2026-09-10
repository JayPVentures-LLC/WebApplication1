using JPVOS.Services.ClaimsEvidence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JPVOS.Api;

[ApiController]
[Route("api/claims-evidence")]
[EnableRateLimiting("ClaimsEvidencePublic")]
[RequestSizeLimit(1_048_576)]
public sealed class ClaimsEvidenceController : ControllerBase
{
    private const string TrackingHeader = "X-JPV-Case-Tracking";
    private readonly IClaimsEvidenceService _service;

    public ClaimsEvidenceController(IClaimsEvidenceService service) => _service = service;

    [HttpPost("cases")]
    public async Task<IActionResult> CreateCase([FromBody] CreateCaseRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
            if (!ClaimsIdentityModeExtensions.TryParseWireValue(request.IdentityMode, out var identityMode))
                return BadRequest(new { error = "invalid_identity_mode" });

            var command = new CreateCaseCommand(
                identityMode,
                request.ClaimStatement ?? string.Empty,
                request.SubjectDescription ?? string.Empty,
                request.RelevantDates,
                request.Jurisdictions,
                request.AffectedParties,
                request.ConfidentialityRequested,
                request.Urgency ?? new UrgencyIndicators(),
                request.Evidence ?? Array.Empty<EvidenceMetadata>(),
                request.SubmissionAcknowledged);

            var result = await _service.CreateCaseAsync(command, idempotencyKey, cancellationToken);
            return Created($"/api/claims-evidence/cases/{result.CaseId}/status", result);
        }
        catch (ClaimsEvidenceValidationException ex) { return BadRequest(new { error = "invalid_request", message = ex.Message }); }
        catch (ClaimsEvidenceIdempotencyConflictException) { return Conflict(new { error = "idempotency_conflict" }); }
        catch (ClaimsEvidenceBinaryUnsupportedException ex) { return UnprocessableEntity(new { error = "binary_evidence_unavailable", message = ex.Message }); }
        catch (ClaimsEvidencePersistenceException) { return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "persistence_unavailable" }); }
    }

    [HttpPost("cases/{caseId}/evidence")]
    public async Task<IActionResult> AddEvidence(string caseId, [FromBody] AddEvidenceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
            var trackingCredential = Request.Headers[TrackingHeader].ToString();
            var result = await _service.AddEvidenceAsync(
                caseId,
                new AddEvidenceCommand(request.Evidence ?? throw new ClaimsEvidenceValidationException("Evidence is required.")),
                idempotencyKey,
                trackingCredential,
                cancellationToken);
            return Ok(result);
        }
        catch (ClaimsEvidenceAuthorizationException) { return StatusCode(StatusCodes.Status403Forbidden, new { error = "case_access_denied" }); }
        catch (ClaimsEvidenceValidationException ex) { return BadRequest(new { error = "invalid_request", message = ex.Message }); }
        catch (ClaimsEvidenceIdempotencyConflictException) { return Conflict(new { error = "idempotency_conflict" }); }
        catch (ClaimsEvidenceBinaryUnsupportedException ex) { return UnprocessableEntity(new { error = "binary_evidence_unavailable", message = ex.Message }); }
        catch (ClaimsEvidencePersistenceException) { return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "persistence_unavailable" }); }
    }

    [HttpGet("cases/{caseId}/status")]
    public async Task<IActionResult> GetStatus(string caseId, CancellationToken cancellationToken)
    {
        try
        {
            var trackingCredential = Request.Headers[TrackingHeader].ToString();
            var result = await _service.GetStatusAsync(caseId, trackingCredential, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (ClaimsEvidenceAuthorizationException) { return StatusCode(StatusCodes.Status403Forbidden, new { error = "case_access_denied" }); }
        catch (ClaimsEvidenceValidationException ex) { return BadRequest(new { error = "invalid_request", message = ex.Message }); }
        catch (ClaimsEvidencePersistenceException) { return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "persistence_unavailable" }); }
    }

    public sealed record CreateCaseRequest(
        string? IdentityMode,
        string? ClaimStatement,
        string? SubjectDescription,
        string? RelevantDates,
        string? Jurisdictions,
        string? AffectedParties,
        bool ConfidentialityRequested,
        UrgencyIndicators? Urgency,
        IReadOnlyList<EvidenceMetadata>? Evidence,
        bool SubmissionAcknowledged);

    public sealed record AddEvidenceRequest(EvidenceMetadata? Evidence);
}
