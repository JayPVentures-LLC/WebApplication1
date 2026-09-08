using JPVOS.Services.Outbound;
using JPVOS.Infrastructure.Twilio;

namespace JPVOS.Tests;

public sealed class OutboundInfrastructureTests
{
    [Fact]
    public async Task JsonlStorePersistsWithoutPhoneNumberAndRejectsDuplicateProviderEvent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jpv-outbound-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new JsonlOutboundReceiptStore(path);
            var receipt = new OutboundMessageReceipt("m1", "github:jaypventuresllc-admin", "github_exact_head_review_request", "o/r", 1, "abc", "fake", "p1", OutboundMessageState.Queued, DateTimeOffset.UtcNow);
            await store.SaveAsync(receipt, CancellationToken.None);

            var loaded = await store.GetAsync("m1", CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.True(await store.TryMarkProviderEventProcessedAsync("evt-1", CancellationToken.None));
            Assert.False(await store.TryMarkProviderEventProcessedAsync("evt-1", CancellationToken.None));
            Assert.DoesNotContain("+15551234567", await File.ReadAllTextAsync(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TwilioSignatureValidationRejectsInvalidSignature()
    {
        var result = TwilioRequestSignature.Validate("https://example.test/api/outbound/providers/twilio/status", new Dictionary<string,string> { ["MessageSid"] = "SM1", ["MessageStatus"] = "delivered" }, "not-a-real-signature", "secret");
        Assert.False(result);
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
    public void TwilioStatusNormalizes(string providerStatus, OutboundMessageState expected)
    {
        Assert.Equal(expected, TwilioSmsTransport.NormalizeStatus(providerStatus));
    }
}
