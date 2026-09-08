using JPVOS.Services.Outbound;

namespace JPVOS.Tests;

public sealed class DirectConversationTests
{
    [Fact]
    public async Task FounderCanSendFreeformMessageToConnorConversation()
    {
        var store = new InMemoryDirectConversationStore();
        var transport = new FakeSmsTransport();
        var service = new DirectConversationService(new FakeBindingResolver(), transport, store);

        var result = await service.SendAsync(new DirectConversationSendRequest(
            PrincipalSmsBindingResolver.ConnorPrincipalId,
            "founder:jay",
            "Are you available to talk?"), CancellationToken.None);

        Assert.True(result.Success);
        var transcript = await store.GetConversationAsync(DirectConversationService.ConnorConversationId, CancellationToken.None);
        Assert.Single(transcript);
        Assert.Equal(ConversationDirection.Outbound, transcript[0].Direction);
        Assert.Equal("Are you available to talk?", transcript[0].Body);
    }

    [Fact]
    public async Task AttributableInboundTextIsStoredAsConnorReply()
    {
        var store = new InMemoryDirectConversationStore();
        var service = new DirectConversationService(new FakeBindingResolver(), new FakeSmsTransport(), store);

        var result = await service.RecordInboundAsync(new InboundDirectMessage(
            "+15551234567",
            "SM-IN-1",
            "Yes, I can talk."), CancellationToken.None);

        Assert.True(result.Success);
        var transcript = await store.GetConversationAsync(DirectConversationService.ConnorConversationId, CancellationToken.None);
        Assert.Single(transcript);
        Assert.Equal(ConversationDirection.Inbound, transcript[0].Direction);
        Assert.Equal(PrincipalSmsBindingResolver.ConnorPrincipalId, transcript[0].PrincipalId);
        Assert.Equal("Yes, I can talk.", transcript[0].Body);
    }

    [Fact]
    public async Task InboundTextFromUnboundNumberIsRejected()
    {
        var service = new DirectConversationService(new FakeBindingResolver(), new FakeSmsTransport(), new InMemoryDirectConversationStore());
        var result = await service.RecordInboundAsync(new InboundDirectMessage("+15550000000", "SM-IN-2", "hello"), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("principal_mismatch", result.ErrorCode);
    }

    [Fact]
    public async Task DuplicateInboundProviderMessageIsIdempotent()
    {
        var store = new InMemoryDirectConversationStore();
        var service = new DirectConversationService(new FakeBindingResolver(), new FakeSmsTransport(), store);
        var inbound = new InboundDirectMessage("+15551234567", "SM-IN-3", "same message");

        Assert.True((await service.RecordInboundAsync(inbound, CancellationToken.None)).Success);
        Assert.True((await service.RecordInboundAsync(inbound, CancellationToken.None)).Success);
        Assert.Single(await store.GetConversationAsync(DirectConversationService.ConnorConversationId, CancellationToken.None));
    }

    [Fact]
    public async Task ReviewAcknowledgmentRequiresItsOwnCorrelationToken()
    {
        var store = new InMemoryDirectConversationStore();
        var transport = new FakeSmsTransport();
        var service = new DirectConversationService(new FakeBindingResolver(), transport, store);
        var token = ReviewAcknowledgmentToken.Create("message-123");

        var wrong = await service.ParseReviewAcknowledgmentAsync("ACK DEADCODE", token, CancellationToken.None);
        var right = await service.ParseReviewAcknowledgmentAsync($"ACK {token.Code}", token, CancellationToken.None);

        Assert.False(wrong);
        Assert.True(right);
    }

    private sealed class FakeBindingResolver : IPrincipalSmsBindingResolver
    {
        public PrincipalSmsBindingResult Resolve(string principalId, DateTimeOffset now) =>
            principalId == PrincipalSmsBindingResolver.ConnorPrincipalId
                ? PrincipalSmsBindingResult.Ok(new PrincipalSmsBinding(principalId, "sms", "+15551234567", "v1", now, false, null))
                : PrincipalSmsBindingResult.Denied("principal_mismatch");
    }

    private sealed class FakeSmsTransport : ISmsTransport
    {
        public Task<SmsSendResult> SendAsync(SmsSendCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(SmsSendResult.Accepted("SM-OUT-1", OutboundMessageState.Queued));
    }
}
