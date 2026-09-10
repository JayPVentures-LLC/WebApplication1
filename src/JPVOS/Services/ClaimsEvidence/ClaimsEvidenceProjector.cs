using System.Text.Json;

namespace JPVOS.Services.ClaimsEvidence;

public sealed class ClaimsEvidenceProjector
{
    public CaseStatusResult? ProjectPublicStatus(IReadOnlyList<ClaimsEvidenceEvent> events)
    {
        if (events.Count == 0) return null;

        var status = CasePublicStatus.Received;
        var additionalEvidenceRequested = false;
        var lastUpdated = events[0].OccurredAtUtc;
        var caseId = events[0].CaseId;

        foreach (var @event in events.OrderBy(e => e.Sequence))
        {
            lastUpdated = @event.OccurredAtUtc;
            switch (@event.Type)
            {
                case ClaimsEvidenceEventType.StatusChanged:
                    TryApplyStatus(@event.PayloadJson, ref status, ref additionalEvidenceRequested);
                    break;
                case ClaimsEvidenceEventType.Routed:
                    status = CasePublicStatus.Routed;
                    break;
                case ClaimsEvidenceEventType.CaseClosed:
                    status = CasePublicStatus.Closed;
                    break;
                case ClaimsEvidenceEventType.CaseReopened:
                    status = CasePublicStatus.Reopened;
                    break;
            }
        }

        return new CaseStatusResult(caseId, status.ToWireValue(), lastUpdated, additionalEvidenceRequested);
    }

    private static void TryApplyStatus(string payloadJson, ref CasePublicStatus status, ref bool additionalEvidenceRequested)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("publicStatus", out var property)) return;
            var value = property.GetString();
            status = value switch
            {
                "received" => CasePublicStatus.Received,
                "verification-in-progress" => CasePublicStatus.VerificationInProgress,
                "additional-evidence-requested" => CasePublicStatus.AdditionalEvidenceRequested,
                "routed" => CasePublicStatus.Routed,
                "action-in-progress" => CasePublicStatus.ActionInProgress,
                "closed" => CasePublicStatus.Closed,
                "resolved" => CasePublicStatus.Resolved,
                "reopened" => CasePublicStatus.Reopened,
                _ => status
            };
            additionalEvidenceRequested = status == CasePublicStatus.AdditionalEvidenceRequested;
        }
        catch (JsonException)
        {
            // Malformed internal payloads do not leak through the public projection.
        }
    }
}
