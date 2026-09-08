using JPVOS.Services.Outbound;

namespace JPVOS.Tests;

public sealed class OutboundTransportTests
{
    [Fact]
    public void VerifiedConnorBindingResolves()
    {
        var config = new Dictionary<string, string?>
        {
            ["JPV_PRINCIPAL_CONNOR_SMS_E164"] = "+15551234567",
            ["JPV_PRINCIPAL_CONNOR_SMS_VERIFIED_AT"] = "2026-09-08T00:00:00Z",
            ["JPV_PRINCIPAL_CONNOR_SMS_BINDING_VERSION"] = "v1"
        };
        var resolver = PrincipalSmsBindingResolver.FromDictionary(config);

        var result = resolver.Resolve("github:jaypventuresllc-admin", DateTimeOffset.Parse("2026-09-08T12:00:00Z"));

        Assert.True(result.Success);
        Assert.Equal("github:jaypventuresllc-admin", result.Binding!.PrincipalId);
        Assert.Equal("sms", result.Binding.Channel);
        Assert.Equal("v1", result.Binding.Version);
    }

    [Theory]
    [InlineData(null, "principal_binding_missing")]
    [InlineData("", "principal_binding_missing")]
    [InlineData("not-e164", "principal_binding_unverified")]
    public void MissingOrMalformedBindingFailsClosed(string? endpoint, string expectedError)
    {
        var config = new Dictionary<string, string?>
        {
            ["JPV_PRINCIPAL_CONNOR_SMS_E164"] = endpoint,
            ["JPV_PRINCIPAL_CONNOR_SMS_VERIFIED_AT"] = "2026-09-08T00:00:00Z",
            ["JPV_PRINCIPAL_CONNOR_SMS_BINDING_VERSION"] = "v1"
        };
        var resolver = PrincipalSmsBindingResolver.FromDictionary(config);

        var result = resolver.Resolve("github:jaypventuresllc-admin", DateTimeOffset.Parse("2026-09-08T12:00:00Z"));

        Assert.False(result.Success);
        Assert.Equal(expectedError, result.ErrorCode);
    }

    [Fact]
    public void MismatchedPrincipalFailsClosed()
    {
        var resolver = PrincipalSmsBindingResolver.FromDictionary(new Dictionary<string, string?>
        {
            ["JPV_PRINCIPAL_CONNOR_SMS_E164"] = "+15551234567",
            ["JPV_PRINCIPAL_CONNOR_SMS_VERIFIED_AT"] = "2026-09-08T00:00:00Z",
            ["JPV_PRINCIPAL_CONNOR_SMS_BINDING_VERSION"] = "v1"
        });

        var result = resolver.Resolve("github:someone-else", DateTimeOffset.Parse("2026-09-08T12:00:00Z"));

        Assert.False(result.Success);
        Assert.Equal("principal_mismatch", result.ErrorCode);
    }

    [Fact]
    public async Task StaleExactHeadIsRejectedBeforeProviderSend()
    {
        var resolver = new FakeBindingResolver();
        var transport = new FakeSmsTransport();
        var receipts = new InMemoryOutboundReceiptStore();
        var github = new FakeHeadReader("new-head");
        var service = new OutboundTransportService(resolver, transport, receipts, github);

        var result = await service.SendGithubReviewAsync(new GithubExactHeadReviewRequest(
            "JayPVentures-LLC/jpv-governance",
            437,
            "old-head",
            "https://github.com/JayPVentures-LLC/jpv-governance/pull/437",
            "github:jaypventuresllc-admin",
            "founder:jay"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("exact_head_stale", result.ErrorCode);
        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task SuccessfulSendPersistsReceiptWithoutDestinationNumber()
    {
        var resolver = new FakeBindingResolver();
        var transport = new FakeSmsTransport();
        var receipts = new InMemoryOutboundReceiptStore();
        var github = new FakeHeadReader("head-123");
        var service = new OutboundTransportService(resolver, transport, receipts, github);

        var result = await service.SendGithubReviewAsync(new GithubExactHeadReviewRequest(
            "JayPVentures-LLC/jpv-governance",
            437,
            "head-123",
            "https://github.com/JayPVentures-LLC/jpv-governance/pull/437",
            "github:jaypventuresllc-admin",
            "founder:jay"), CancellationToken.None);

        Assert.True(result.Success);
        var receipt = await receipts.GetAsync(result.MessageId!, CancellationToken.None);
        Assert.NotNull(receipt);
        Assert.Equal("github:jaypventuresllc-admin", receipt!.TargetPrincipalId);
        Assert.Equal("github_exact_head_review_request", receipt.Purpose);
        Assert.DoesNotContain("+15551234567", System.Text.Json.JsonSerializer.Serialize(receipt));
    }

    [Fact]
    public void SmsAcknowledgmentStateIsNotGithubApprovalState()
    {
        Assert.NotEqual(OutboundMessageState.Acknowledged.ToString(), "APPROVED");
    }

    private sealed class FakeBindingResolver : IPrincipalSmsBindingResolver
    {
        public PrincipalSmsBindingResult Resolve(string principalId, DateTimeOffset now) =>
            PrincipalSmsBindingResult.Ok(new PrincipalSmsBinding(principalId, "sms", "+15551234567", "v1", now, false, null));
    }

    private sealed class FakeSmsTransport : ISmsTransport
    {
        public int SendCount { get; private set; }

        public Task<SmsSendResult> SendAsync(SmsSendCommand command, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(SmsSendResult.Accepted("provider-123", OutboundMessageState.Queued));
        }
    }

    private sealed class FakeHeadReader(string head) : IGitHubExactHeadReader
    {
        public Task<string> GetHeadShaAsync(string repositoryFullName, int pullRequestNumber, CancellationToken cancellationToken) => Task.FromResult(head);
    }
}
