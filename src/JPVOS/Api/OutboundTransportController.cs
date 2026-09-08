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
    private readonly ReviewAcknowledgmentService _reviewAcknowledgments;
    private readonly IDirectConversationStore _conversationStore;
    private readonly IOutboundReceiptStore _receipts;
    private readonly TwilioSmsTransport _twilio;
    private readonly IConfiguration _configuration;

    public OutboundTransportController(OutboundTransportService service, DirectConversationService conversation, ReviewAcknowledgmentService reviewAcknowledgments, IDirectConversationStore conversationStore, IOutboundReceiptStore receipts, TwilioSmsTransport twilio, IConfiguration configuration)
    {
        _service = service; _conversation = conversation; _reviewAcknowledgments = reviewAcknowledgments; _conversationStore = conversationStore; _receipts = receipts; _twilio = twilio; _configuration = configuration;
    }

    [Authorize(Policy = "FounderOnly")]
    [HttpPost("github-review")]
    public async Task<IActionResult> SendGithubReview([FromBody] GithubReviewApiRequest request, CancellationToken cancellationToken)
    {
        var authority = User.Identity?.Name; if (string.IsNullOrWhiteSpace(authority)) return Forbid();
        var result = await _service.SendGithubReviewAsync(new GithubExactHeadReviewRequest(request.RepositoryFullName, request.PullRequestNumber, request.ExactHeadSha, request.PullRequestUrl, PrincipalSmsBindingResolver.ConnorPrincipalId, $"founder:{authority}"), cancellationToken);
        return result.Success ? Accepted(new { messageId = result.MessageId, state = "queued" }) : Conflict(new { error = result.ErrorCode });
    }

    [Authorize(Policy = "FounderOnly")]
    [HttpPost("conversation/connor/messages")]
    public async Task<IActionResult> SendConnorMessage([FromBody] DirectMessageApiRequest request, CancellationToken cancellationToken)
    {
        var authority = User.Identity?.Name; if (string.IsNullOrWhiteSpace(authority)) return Forbid();
        var result = await _conversation.SendAsync(new DirectConversationSendRequest(PrincipalSmsBindingResolver.ConnorPrincipalId, $"founder:{authority}", request.Body), cancellationToken);
        return result.Success ? Accepted(new { messageId = result.MessageId }) : Conflict(new { error = result.ErrorCode });
    }

    [Authorize(Policy = "FounderOnly")]
    [HttpGet("conversation/connor/messages")]
    public async Task<IActionResult> GetConnorConversation(CancellationToken cancellationToken) => Ok(await _conversationStore.GetConversationAsync(DirectConversationService.ConnorConversationId, cancellationToken));

    [Authorize(Policy = "FounderOnly")]
    [HttpGet("receipts/{messageId}")]
    public async Task<IActionResult> GetReceipt(string messageId, CancellationToken cancellationToken)
    {
        var receipt = await _receipts.GetAsync(messageId, cancellationToken); return receipt is null ? NotFound(new { error = "receipt_not_found" }) : Ok(receipt);
    }

    [AllowAnonymous]
    [HttpPost("providers/twilio/status")]
    public async Task<IActionResult> TwilioStatus(CancellationToken cancellationToken)
    {
        var form = await ReadFormAsync(cancellationToken);
        if (!ValidateTwilio(form, "status")) return Unauthorized(new { error = "webhook_auth_invalid" });
        if (!form.TryGetValue("MessageSid", out var sid) || string.IsNullOrWhiteSpace(sid)) return BadRequest();
        var status = form.GetValueOrDefault("MessageStatus") ?? "queued";
        var next = TwilioSmsTransport.NormalizeStatus(status);
        var eventId = $"status:{sid}:{status}";

        var receiptResult = await _receipts.ApplyProviderStatusAsync(sid, eventId, next, DateTimeOffset.UtcNow, cancellationToken);
        if (receiptResult.Disposition != ProviderStatusApplyDisposition.NotFound)
            return Ok(new { target = "review_receipt", disposition = receiptResult.Disposition.ToString().ToLowerInvariant(), state = receiptResult.Receipt?.State.ToString().ToUpperInvariant() });

        var conversationResult = await _conversationStore.ApplyProviderStatusAsync(sid, eventId, next, DateTimeOffset.UtcNow, cancellationToken);
        if (conversationResult.Disposition != ProviderStatusApplyDisposition.NotFound)
            return Ok(new { target = "direct_conversation", disposition = conversationResult.Disposition.ToString().ToLowerInvariant(), state = conversationResult.Message?.DeliveryState?.ToString().ToUpperInvariant() });

        return NotFound(new { error = "receipt_not_found" });
    }

    [AllowAnonymous]
    [HttpPost("providers/twilio/inbound")]
    public async Task<IActionResult> TwilioInbound(CancellationToken cancellationToken)
    {
        var form = await ReadFormAsync(cancellationToken);
        if (!ValidateTwilio(form, "inbound")) return Unauthorized(new { error = "webhook_auth_invalid" });
        var from = form.GetValueOrDefault("From"); var providerEventId = form.GetValueOrDefault("MessageSid"); var body = form.GetValueOrDefault("Body") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(providerEventId) || string.IsNullOrWhiteSpace(body)) return BadRequest();
        var direct = await _conversation.RecordInboundAsync(new InboundDirectMessage(from, providerEventId, body), cancellationToken);
        if (!direct.Success) return Unauthorized(new { error = direct.ErrorCode });
        var acknowledgment = await _reviewAcknowledgments.ApplyAsync(from, providerEventId, body, cancellationToken);
        if (!string.IsNullOrWhiteSpace(acknowledgment.ErrorCode)) return Unauthorized(new { error = acknowledgment.ErrorCode });
        if (acknowledgment.Matched) return Ok(new { received = true, acknowledged = true, messageId = acknowledgment.MessageId, githubApproval = false });
        return Ok(new { received = true, conversationMessageId = direct.MessageId });
    }

    private bool ValidateTwilio(IReadOnlyDictionary<string, string> form, string callbackKind)
    {
        var signature = Request.Headers["X-Twilio-Signature"].ToString(); var baseUrl = _configuration["JPV_OUTBOUND_WEBHOOK_BASE_URL"]?.TrimEnd('/'); if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        return _twilio.ValidateWebhook($"{baseUrl}/api/outbound/providers/twilio/{callbackKind}", form, signature);
    }
    private async Task<Dictionary<string, string>> ReadFormAsync(CancellationToken cancellationToken) { var form = await Request.ReadFormAsync(cancellationToken); return form.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.Ordinal); }
}

public sealed record GithubReviewApiRequest(string RepositoryFullName, int PullRequestNumber, string ExactHeadSha, string PullRequestUrl);
public sealed record DirectMessageApiRequest(string Body);
