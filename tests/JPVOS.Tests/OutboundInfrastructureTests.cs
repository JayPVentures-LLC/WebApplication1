using JPVOS.Services.Outbound;
using JPVOS.Infrastructure.Twilio;

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

    [Theory]
    [InlineData("queued", OutboundMessageState.Queued)]
    [InlineData("sent", OutboundMessageState.Sent)]
    [InlineData("delivered", OutboundMessageState.Delivered)]
    [InlineData("failed", OutboundMessageState.Failed)]
    [InlineData("undelivered", OutboundMessageState.Failed)]
    public void TwilioStatusNormalizes(string providerStatus, OutboundMessageState expected) => Assert.Equal(expected, TwilioSmsTransport.NormalizeStatus(providerStatus));
}
