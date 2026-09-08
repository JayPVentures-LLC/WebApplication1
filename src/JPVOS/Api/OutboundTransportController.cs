using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using JPVOS.Infrastructure.Twilio;
using JPVOS.Services.Outbound;

namespace JPVOS.Api;

[ApiController]
[Route("api/outbound")]
public sealed class OutboundTransportController : ControllerBase
{
    private readonly OutboundTransportService _service;
    private readonly IOutboundReceiptStore _receipts;
    private readonly IPrincipalSmsBindingResolver _bindings;
    private readonly TwilioSmsTransport _twilio;

    public OutboundTransportController(OutboundTransportService service, IOutboundReceiptStore receipts, IPrincipalSmsBindingResolver bindings, TwilioSmsTransport twilio)
    {
        _service = service;
        _receipts = receipts;
        _bindings = bindings;
        _twilio = twilio;
    }

    [Authorize(Policy = "FounderOnly")]
    [HttpPost("github-review")]
    public async Task<IActionResult> SendGithubReview([FromBody] GithubReviewApiRequest request, CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { error = "purpose_not_supported" });
        var authority = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(authority)) return Forbid();
        var result = await _service.SendGithubReviewAsync(new GithubExactHeadReviewRequest(
            request.RepositoryFullName,
            request.PullRequestNumber,
            request.ExactHeadSha,
            request.PullRequestUrl,
            PrincipalSmsBindingResolver.ConnorPrincipalId,
            $"founder:{authority}"), cancellationToken);
        if (!result.Success) return Conflict(new { error = result.ErrorCode });
        return Accepted(new { messageId = result.MessageId, state = "queued" });
    }

    [Authorize(Policy = "FounderOnly")]
    [HttpGet("receipts/{messageId}")]
    public async Task<IActionResult> GetReceipt(string messageId, CancellationToken cancellationToken)
    {
        var receipt = await _receipts.GetAsync(messageId, cancellationToken);
        return receipt is null ? NotFound(new { error = "receipt_not_found" }) : Ok(receipt);
    }

    [AllowAnonymous]
    [HttpPost("providers/twilio/status")]
    public async Task<IActionResult> TwilioStatus(CancellationToken cancellationToken)
    {
        var form = await ReadFormAsync(cancellationToken);
        if (!ValidateTwilio(form)) return Unauthorized(new { error = "webhook_auth_invalid" });
        if (!form.TryGetValue("MessageSid", out var sid) || string.IsNullOrWhiteSpace(sid)) return BadRequest();
        var status = form.GetValueOrDefault("MessageStatus") ?? "queued";
        var eventId = $"status:{sid}:{status}";
        if (!await _receipts.TryMarkProviderEventProcessedAsync(eventId, cancellationToken)) return Ok(new { duplicate = true });
        var receipt = await _receipts.FindByProviderMessageIdAsync(sid, cancellationToken);
        if (receipt is null) return NotFound(new { error = "receipt_not_found" });
        var next = TwilioSmsTransport.NormalizeStatus(status);
        if (Rank(next) < Rank(receipt.State) && next != OutboundMessageState.Failed) return Ok(new { ignored = "stale" });
        var now = DateTimeOffset.UtcNow;
        receipt = receipt with
        {
            State = next,
            SentAtUtc = next == OutboundMessageState.Sent && receipt.SentAtUtc is null ? now : receipt.SentAtUtc,
            DeliveredAtUtc = next == OutboundMessageState.Delivered ? now : receipt.DeliveredAtUtc,
            FailedAtUtc = next == OutboundMessageState.Failed ? now : receipt.FailedAtUtc
        };
        await _receipts.SaveAsync(receipt, cancellationToken);
        return Ok(new { state = next.ToString().ToUpperInvariant() });
    }

    [AllowAnonymous]
    [HttpPost("providers/twilio/inbound")]
    public async Task<IActionResult> TwilioInbound(CancellationToken cancellationToken)
    {
        var form = await ReadFormAsync(cancellationToken);
        if (!ValidateTwilio(form)) return Unauthorized(new { error = "webhook_auth_invalid" });
        var from = form.GetValueOrDefault("From");
        var providerEventId = form.GetValueOrDefault("MessageSid");
        var body = form.GetValueOrDefault("Body") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(providerEventId)) return BadRequest();
        if (!body.Trim().StartsWith("ACK", StringComparison.OrdinalIgnoreCase)) return Ok(new { acknowledged = false });
        if (!await _receipts.TryMarkProviderEventProcessedAsync($"inbound:{providerEventId}", cancellationToken)) return Ok(new { duplicate = true });
        var binding = _bindings.Resolve(PrincipalSmsBindingResolver.ConnorPrincipalId, DateTimeOffset.UtcNow);
        if (!binding.Success || !string.Equals(binding.Binding!.EndpointE164, from, StringComparison.Ordinal)) return Unauthorized(new { error = "acknowledgment_not_attributable" });
        var receipt = await _receipts.FindLatestForPrincipalAsync(PrincipalSmsBindingResolver.ConnorPrincipalId, cancellationToken);
        if (receipt is null) return NotFound(new { error = "receipt_not_found" });
        receipt = receipt with
        {
            State = OutboundMessageState.Acknowledged,
            AcknowledgedAtUtc = DateTimeOffset.UtcNow,
            AcknowledgmentEvidenceType = "attributable_inbound_sms",
            AcknowledgmentEvidenceReference = providerEventId
        };
        await _receipts.SaveAsync(receipt, cancellationToken);
        return Ok(new { acknowledged = true, messageId = receipt.MessageId });
    }

    private bool ValidateTwilio(IReadOnlyDictionary<string, string> form)
    {
        var signature = Request.Headers["X-Twilio-Signature"].ToString();
        var url = $"{Request.Scheme}://{Request.Host}{Request.PathBase}{Request.Path}{Request.QueryString}";
        return _twilio.ValidateWebhook(url, form, signature);
    }

    private async Task<Dictionary<string, string>> ReadFormAsync(CancellationToken cancellationToken)
    {
        var form = await Request.ReadFormAsync(cancellationToken);
        return form.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.Ordinal);
    }

    private static int Rank(OutboundMessageState state) => state switch
    {
        OutboundMessageState.Queued => 0,
        OutboundMessageState.Sent => 1,
        OutboundMessageState.Delivered => 2,
        OutboundMessageState.Failed => 3,
        OutboundMessageState.Acknowledged => 4,
        _ => 0
    };
}

public sealed record GithubReviewApiRequest(string RepositoryFullName, int PullRequestNumber, string ExactHeadSha, string PullRequestUrl);
