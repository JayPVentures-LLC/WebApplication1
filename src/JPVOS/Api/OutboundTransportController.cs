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
    private readonly DirectConversationService _conversation;
    private readonly IDirectConversationStore _conversationStore;
    private readonly IOutboundReceiptStore _receipts;
    private readonly IPrincipalSmsBindingResolver _bindings;
    private readonly TwilioSmsTransport _twilio;
    private readonly IConfiguration _configuration;

    public OutboundTransportController(
        OutboundTransportService service,
        DirectConversationService conversation,
        IDirectConversationStore conversationStore,
        IOutboundReceiptStore receipts,
        IPrincipalSmsBindingResolver bindings,
        TwilioSmsTransport twilio,
        IConfiguration configuration)
    {
        _service = service;
        _conversation = conversation;
        _conversationStore = conversationStore;
        _receipts = receipts;
        _bindings = bindings;
        _twilio = twilio;
        _configuration = configuration;
    }

    [Authorize(Policy = "FounderOnly")]
    [HttpPost("github-review")]
    public async Task<IActionResult> SendGithubReview([FromBody] GithubReviewApiRequest request, CancellationToken cancellationToken)
    {
        var authority = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(authority)) return Forbid();
        var result = await _service.SendGithubReviewAsync(new GithubExactHeadReviewRequest(
            request.RepositoryFullName, request.PullRequestNumber, request.ExactHeadSha, request.PullRequestUrl,
            PrincipalSmsBindingResolver.ConnorPrincipalId, $"founder:{authority}"), cancellationToken);
        return result.Success ? Accepted(new { messageId = result.MessageId, state = "queued" }) : Conflict(new { error = result.ErrorCode });
    }

    [Authorize(Policy = "FounderOnly")]
    [HttpPost("conversation/connor/messages")]
    public async Task<IActionResult> SendConnorMessage([FromBody] DirectMessageApiRequest request, CancellationToken cancellationToken)
    {
        var authority = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(authority)) return Forbid();
        var result = await _conversation.SendAsync(new DirectConversationSendRequest(
            PrincipalSmsBindingResolver.ConnorPrincipalId, $"founder:{authority}", request.Body), cancellationToken);
        return result.Success ? Accepted(new { messageId = result.MessageId }) : Conflict(new { error = result.ErrorCode });
    }

    [Authorize(Policy = "FounderOnly")]
    [HttpGet("conversation/connor/messages")]
    public async Task<IActionResult> GetConnorConversation(CancellationToken cancellationToken)
        => Ok(await _conversationStore.GetConversationAsync(DirectConversationService.ConnorConversationId, cancellationToken));

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
        if (!ValidateTwilio(form, "status")) return Unauthorized(new { error = "webhook_auth_invalid" });
        if (!form.TryGetValue("MessageSid", out var sid) || string.IsNullOrWhiteSpace(sid)) return BadRequest();
        var receipt = await _receipts.FindByProviderMessageIdAsync(sid, cancellationToken);
        if (receipt is null) return NotFound(new { error = "receipt_not_found" });

        var status = form.GetValueOrDefault("MessageStatus") ?? "queued";
        var eventId = $"status:{sid}:{status}";
        if (!await _receipts.TryMarkProviderEventProcessedAsync(eventId, cancellationToken)) return Ok(new { duplicate = true });
        var next = TwilioSmsTransport.NormalizeStatus(status);
        if (receipt.State == OutboundMessageState.Acknowledged) return Ok(new { ignored = "acknowledgment_is_stronger" });
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
        if (!ValidateTwilio(form, "inbound")) return Unauthorized(new { error = "webhook_auth_invalid" });
        var from = form.GetValueOrDefault("From");
        var providerEventId = form.GetValueOrDefault("MessageSid");
        var body = form.GetValueOrDefault("Body") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(providerEventId) || string.IsNullOrWhiteSpace(body)) return BadRequest();

        var direct = await _conversation.RecordInboundAsync(new InboundDirectMessage(from, providerEventId, body), cancellationToken);
        if (!direct.Success) return Unauthorized(new { error = direct.ErrorCode });

        var parts = body.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && string.Equals(parts[0], "ACK", StringComparison.OrdinalIgnoreCase))
        {
            var receipt = await _receipts.FindByAcknowledgmentCodeAsync(PrincipalSmsBindingResolver.ConnorPrincipalId, parts[1], cancellationToken);
            if (receipt is not null && receipt.State != OutboundMessageState.Acknowledged)
            {
                receipt = receipt with
                {
                    State = OutboundMessageState.Acknowledged,
                    AcknowledgedAtUtc = DateTimeOffset.UtcNow,
                    AcknowledgmentEvidenceType = "attributable_inbound_sms",
                    AcknowledgmentEvidenceReference = providerEventId
                };
                await _receipts.SaveAsync(receipt, cancellationToken);
                return Ok(new { received = true, acknowledged = true, messageId = receipt.MessageId });
            }
        }

        return Ok(new { received = true, conversationMessageId = direct.MessageId });
    }

    private bool ValidateTwilio(IReadOnlyDictionary<string, string> form, string callbackKind)
    {
        var signature = Request.Headers["X-Twilio-Signature"].ToString();
        var baseUrl = _configuration["JPV_OUTBOUND_WEBHOOK_BASE_URL"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        var url = $"{baseUrl}/api/outbound/providers/twilio/{callbackKind}";
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
public sealed record DirectMessageApiRequest(string Body);
