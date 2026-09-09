using JPVOS.Services.Outbound;
using JPVOS.Infrastructure.Twilio;
using Microsoft.Extensions.Configuration;

namespace JPVOS.Tests;

public sealed class OutboundInfrastructureTests
{
    [Fact]
    public async Task JsonlStorePersistsWithoutPhoneNumberAndRejectsDuplicateProviderEvent()
    {
        var path = Path.ChangeExtension(Path.GetTempFileName(), ".jsonl");
        try
        {
            var store = new JsonlOutboundReceiptStore(path);
            var receipt = new OutboundMessageReceipt("m1", "github:jaypventuresllc-admin", "github_exact_head_review_request", "o/r", 1, "abc", "fake", "p1", OutboundMessageState.Queued, DateTimeOffset.UtcNow, AcknowledgmentCode: "ABC12345");
            await store.SaveAsync(receipt, CancellationToken.None);
            Assert.NotNull(await store.GetAsync("m1", CancellationToken.None));
            Assert.Equal("m1", (await store.FindByAcknowledgmentCodeAsync("github:jaypventuresllc-admin", "ABC12345", CancellationToken.None))?.MessageId);
            Assert.True(await store.TryMarkProviderEventProcessedAsync("evt-1", CancellationToken.None));
            Assert.False(await store.TryMarkProviderEventProcessedAsync("evt-1", CancellationToken.None));
            Assert.DoesNotContain("+15551234567", await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".events")) File.Delete(path + ".events");
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public async Task ReceiptStatusTransitionAndReplayClaimAreAtomicAtStoreBoundary()
    {
        var store = new InMemoryOutboundReceiptStore();
        await store.SaveAsync(new OutboundMessageReceipt("m2", PrincipalSmsBindingResolver.ConnorPrincipalId, "github_exact_head_review_request", "o/r", 2, "head", "twilio", "SM2", OutboundMessageState.Queued, DateTimeOffset.UtcNow), CancellationToken.None);
        var first = await store.ApplyProviderStatusAsync("SM2", "status:SM2:delivered", OutboundMessageState.Delivered, DateTimeOffset.UtcNow, CancellationToken.None);
        var duplicate = await store.ApplyProviderStatusAsync("SM2", "status:SM2:delivered", OutboundMessageState.Delivered, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(ProviderStatusApplyDisposition.Updated, first.Disposition);
        Assert.Equal(ProviderStatusApplyDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(OutboundMessageState.Delivered, (await store.GetAsync("m2", CancellationToken.None))!.State);
    }

    [Fact]
    public async Task AcknowledgedReceiptCannotBeOverwrittenByLateFailure()
    {
        var store = new InMemoryOutboundReceiptStore();
        await store.SaveAsync(new OutboundMessageReceipt("m3", PrincipalSmsBindingResolver.ConnorPrincipalId, "github_exact_head_review_request", "o/r", 3, "head", "twilio", "SM3", OutboundMessageState.Acknowledged, DateTimeOffset.UtcNow, AcknowledgedAtUtc: DateTimeOffset.UtcNow), CancellationToken.None);
        var result = await store.ApplyProviderStatusAsync("SM3", "status:SM3:failed", OutboundMessageState.Failed, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(ProviderStatusApplyDisposition.Ignored, result.Disposition);
        Assert.Equal(OutboundMessageState.Acknowledged, (await store.GetAsync("m3", CancellationToken.None))!.State);
    }

    [Fact]
    public async Task DeliveredReceiptCannotBeOverwrittenByLateFailure()
    {
        var store = new InMemoryOutboundReceiptStore();
        await store.SaveAsync(new OutboundMessageReceipt("m4", PrincipalSmsBindingResolver.ConnorPrincipalId, "github_exact_head_review_request", "o/r", 3, "head", "twilio", "SM4", OutboundMessageState.Delivered, DateTimeOffset.UtcNow, DeliveredAtUtc: DateTimeOffset.UtcNow), CancellationToken.None);
        var result = await store.ApplyProviderStatusAsync("SM4", "status:SM4:failed", OutboundMessageState.Failed, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(ProviderStatusApplyDisposition.Ignored, result.Disposition);
        Assert.Equal(OutboundMessageState.Delivered, (await store.GetAsync("m4", CancellationToken.None))!.State);
    }

    [Fact]
    public async Task JsonlDeliveredReceiptCannotBeOverwrittenByLateFailure()
    {
        var path = Path.ChangeExtension(Path.GetTempFileName(), ".jsonl");
        try
        {
            var store = new JsonlOutboundReceiptStore(path);
            await store.SaveAsync(new OutboundMessageReceipt("m5", PrincipalSmsBindingResolver.ConnorPrincipalId, "github_exact_head_review_request", "o/r", 3, "head", "twilio", "SM5", OutboundMessageState.Delivered, DateTimeOffset.UtcNow, DeliveredAtUtc: DateTimeOffset.UtcNow), CancellationToken.None);
            var result = await store.ApplyProviderStatusAsync("SM5", "status:SM5:failed", OutboundMessageState.Failed, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(ProviderStatusApplyDisposition.Ignored, result.Disposition);
            Assert.Equal(OutboundMessageState.Delivered, (await store.GetAsync("m5", CancellationToken.None))!.State);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".events")) File.Delete(path + ".events");
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            if (File.Exists(path + ".events.tmp")) File.Delete(path + ".events.tmp");
        }
    }

    [Fact]
    public void TwilioSignatureValidationRejectsInvalidSignature()
    {
        Assert.False(TwilioRequestSignature.Validate("https://example.test/api/outbound/providers/twilio/status", new Dictionary<string,string> { ["MessageSid"] = "SM1", ["MessageStatus"] = "delivered" }, "not-a-real-signature", "secret"));
    }

    [Fact]
    public void TwilioSignatureValidationAcceptsCanonicalSignature()
    {
        const string url = "https://example.test/api/outbound/providers/twilio/status";
        var form = new Dictionary<string,string> { ["MessageSid"] = "SM1", ["MessageStatus"] = "delivered" };
        var signature = TwilioRequestSignature.Compute(url, form, "secret");
        Assert.True(TwilioRequestSignature.Validate(url, form, signature, "secret"));
    }

    [Fact]
    public void TwilioCallbackBuilderPreservesConfiguredPathPrefix()
    {
        Assert.True(TwilioSmsTransport.TryBuildCallbackUrl("https://example.test/jpv", "status", out var status));
        Assert.True(TwilioSmsTransport.TryBuildCallbackUrl("https://example.test/jpv/", "inbound", out var inbound));
        Assert.Equal("https://example.test/jpv/api/outbound/providers/twilio/status", status);
        Assert.Equal("https://example.test/jpv/api/outbound/providers/twilio/inbound", inbound);
    }

    [Fact]
    public void ReviewAcknowledgmentTokensUseAtLeast128BitsOfCorrelationSpace()
    {
        var first = ReviewAcknowledgmentToken.Create(Guid.NewGuid().ToString("N"));
        var second = ReviewAcknowledgmentToken.Create(Guid.NewGuid().ToString("N"));
        Assert.True(first.Code.Length >= 32);
        Assert.NotEqual(first.Code, second.Code);
    }

    [Theory]
    [InlineData("queued", OutboundMessageState.Queued)]
    [InlineData("sent", OutboundMessageState.Sent)]
    [InlineData("delivered", OutboundMessageState.Delivered)]
    [InlineData("failed", OutboundMessageState.Failed)]
    [InlineData("undelivered", OutboundMessageState.Failed)]
    public void TwilioStatusNormalizes(string providerStatus, OutboundMessageState expected) => Assert.Equal(expected, TwilioSmsTransport.NormalizeStatus(providerStatus));

    [Fact]
    public async Task TwilioSendFailsClosedWhenWebhookBaseUrlIsMissing()
    {
        using var client = new HttpClient(new RejectingHttpHandler());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TWILIO_ACCOUNT_SID"] = "AC123",
            ["TWILIO_AUTH_TOKEN"] = "auth",
            ["TWILIO_FROM_NUMBER"] = "+15551234567"
        }).Build();
        var transport = new TwilioSmsTransport(client, config);

        var result = await transport.SendAsync(new SmsSendCommand("+15557654321", "hello", "corr-1"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("provider_unavailable", result.ErrorCode);
    }

    private sealed class RejectingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("HTTP should not be called for invalid callback config.");
    }
}
