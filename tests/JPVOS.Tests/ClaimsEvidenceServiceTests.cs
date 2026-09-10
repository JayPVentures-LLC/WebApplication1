using JPVOS.Services.ClaimsEvidence;
using Microsoft.AspNetCore.DataProtection;

namespace JPVOS.Tests;

public sealed class ClaimsEvidenceServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"jpv-claims-service-{Guid.NewGuid():N}");

    [Fact]
    public void TrackingCredential_IsHighEntropyAndVerifiable()
    {
        var service = new TrackingCredentialService();
        var issued = service.Issue();

        Assert.True(issued.Plaintext.Length >= 40);
        Assert.DoesNotContain(issued.Plaintext, issued.Verifier, StringComparison.Ordinal);
        Assert.True(service.Verify(issued.Plaintext, issued.Verifier));
        Assert.False(service.Verify("wrong-token", issued.Verifier));
    }

    [Theory]
    [InlineData("anonymous", ClaimsIdentityMode.Anonymous)]
    [InlineData("pseudonymous", ClaimsIdentityMode.Pseudonymous)]
    [InlineData("verified-confidential", ClaimsIdentityMode.VerifiedConfidential)]
    public void IdentityModes_HaveExplicitWireValues(string wire, ClaimsIdentityMode expected)
    {
        Assert.True(ClaimsIdentityModeExtensions.TryParseWireValue(wire, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(wire, parsed.ToWireValue());
    }

    [Fact]
    public async Task CreateCase_IsIdempotentAndReturnsOriginalTrackingCredential()
    {
        var service = CreateService();
        var command = ValidCommand(ClaimsIdentityMode.Anonymous);

        var first = await service.CreateCaseAsync(command, "idem-create-0001", default);
        var retry = await service.CreateCaseAsync(command, "idem-create-0001", default);

        Assert.Equal(first.CaseId, retry.CaseId);
        Assert.Equal(first.TrackingCredential, retry.TrackingCredential);
        Assert.Equal("received", retry.Status);
    }

    [Theory]
    [InlineData(ClaimsIdentityMode.Anonymous)]
    [InlineData(ClaimsIdentityMode.Pseudonymous)]
    [InlineData(ClaimsIdentityMode.VerifiedConfidential)]
    public async Task CreateCase_AcceptsAllApprovedIdentityModes(ClaimsIdentityMode mode)
    {
        var result = await CreateService().CreateCaseAsync(ValidCommand(mode), $"idem-{mode}-0001", default);
        Assert.StartsWith("jpv_case_", result.CaseId, StringComparison.Ordinal);
        Assert.Equal("received", result.Status);
    }

    [Fact]
    public async Task CreateCase_PermitsUnknownDatesAndJurisdiction()
    {
        var command = ValidCommand(ClaimsIdentityMode.Pseudonymous) with { RelevantDates = "unknown", Jurisdictions = "unknown" };
        var result = await CreateService().CreateCaseAsync(command, "idem-unknown-0001", default);
        Assert.Equal("received", result.Status);
    }

    [Fact]
    public async Task AddEvidence_RequiresValidTrackingCredential()
    {
        var service = CreateService();
        var created = await service.CreateCaseAsync(ValidCommand(ClaimsIdentityMode.Anonymous), "idem-auth-0001", default);
        var evidence = new AddEvidenceCommand(new EvidenceMetadata("document", "submitter", "unknown", "restricted", null, "supports claim"));

        await Assert.ThrowsAsync<ClaimsEvidenceAuthorizationException>(() =>
            service.AddEvidenceAsync(created.CaseId, evidence, "idem-auth-0002", "wrong", default));

        var accepted = await service.AddEvidenceAsync(created.CaseId, evidence, "idem-auth-0002", created.TrackingCredential, default);
        Assert.StartsWith("jpv_ev_", accepted.EvidenceId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusProjection_ContainsOnlySubmitterSafeShape()
    {
        var service = CreateService();
        var created = await service.CreateCaseAsync(ValidCommand(ClaimsIdentityMode.VerifiedConfidential), "idem-status-0001", default);
        var status = await service.GetStatusAsync(created.CaseId, created.TrackingCredential, default);

        Assert.NotNull(status);
        Assert.Equal(created.CaseId, status!.CaseId);
        Assert.Equal("received", status.Status);
        Assert.False(status.AdditionalEvidenceRequested);
    }

    [Fact]
    public async Task BinaryEvidence_FailsClosed()
    {
        var service = CreateService();
        var command = ValidCommand(ClaimsIdentityMode.Anonymous) with
        {
            Evidence = new[] { new EvidenceMetadata("binary", "submitter", null, "confidential", null, null, IncludesBinaryContent: true) }
        };

        await Assert.ThrowsAsync<ClaimsEvidenceBinaryUnsupportedException>(() =>
            service.CreateCaseAsync(command, "idem-binary-0001", default));
    }

    [Fact]
    public async Task PublicStorageReference_IsRejected()
    {
        var service = CreateService();
        var command = ValidCommand(ClaimsIdentityMode.Anonymous) with
        {
            Evidence = new[] { new EvidenceMetadata("link", "submitter", null, "restricted", null, null, StorageReference: "https://example.com/file") }
        };

        await Assert.ThrowsAsync<ClaimsEvidenceValidationException>(() =>
            service.CreateCaseAsync(command, "idem-publicref-0001", default));
    }

    private ClaimsEvidenceService CreateService()
    {
        Directory.CreateDirectory(_directory);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_directory, "keys")));
        var store = new SqliteClaimsEvidenceEventStore(Path.Combine(_directory, "claims.db"), provider);
        return new ClaimsEvidenceService(store, new TrackingCredentialService(), new DisabledEvidenceBlobStore(), new ClaimsEvidenceProjector());
    }

    private static CreateCaseCommand ValidCommand(ClaimsIdentityMode mode) => new(
        mode,
        "A factual allegation requiring verification.",
        "Example subject",
        "unknown",
        "unknown",
        null,
        mode == ClaimsIdentityMode.VerifiedConfidential,
        new UrgencyIndicators(),
        Array.Empty<EvidenceMetadata>(),
        true);

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { }
    }
}
