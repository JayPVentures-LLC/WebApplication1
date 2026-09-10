using JPVOS.Api;
using JPVOS.Services.ClaimsEvidence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JPVOS.Tests;

public sealed class ClaimsEvidenceControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"jpv-claims-controller-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateCase_MissingIdempotencyKey_ReturnsBadRequest()
    {
        var controller = CreateController();
        var request = ValidRequest();

        var result = await controller.CreateCase(request, default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateCase_Returns201WithoutEchoingClaim()
    {
        var controller = CreateController();
        controller.Request.Headers["Idempotency-Key"] = "idem-controller-0001";
        var request = ValidRequest();

        var result = await controller.CreateCase(request, default);

        var created = Assert.IsType<CreatedResult>(result);
        var body = Assert.IsType<CreateCaseResult>(created.Value);
        Assert.StartsWith("jpv_case_", body.CaseId, StringComparison.Ordinal);
        Assert.Equal("received", body.Status);
        Assert.DoesNotContain(request.ClaimStatement!, System.Text.Json.JsonSerializer.Serialize(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_MissingCredential_ReturnsForbidden()
    {
        var controller = CreateController();
        controller.Request.Headers["Idempotency-Key"] = "idem-controller-0002";
        var createdResult = Assert.IsType<CreatedResult>(await controller.CreateCase(ValidRequest(), default));
        var created = Assert.IsType<CreateCaseResult>(createdResult.Value);

        var status = await controller.GetStatus(created.CaseId, default);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(status).StatusCode);
    }

    [Fact]
    public async Task Status_WithCredential_ReturnsSafeProjection()
    {
        var controller = CreateController();
        controller.Request.Headers["Idempotency-Key"] = "idem-controller-0003";
        var createdResult = Assert.IsType<CreatedResult>(await controller.CreateCase(ValidRequest(), default));
        var created = Assert.IsType<CreateCaseResult>(createdResult.Value);
        controller.Request.Headers["X-JPV-Case-Tracking"] = created.TrackingCredential;

        var statusResult = await controller.GetStatus(created.CaseId, default);

        var ok = Assert.IsType<OkObjectResult>(statusResult);
        var status = Assert.IsType<CaseStatusResult>(ok.Value);
        Assert.Equal(new[] { "CaseId", "Status", "LastUpdatedAtUtc", "AdditionalEvidenceRequested" },
            typeof(CaseStatusResult).GetProperties().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Controller_HasPublicRateLimitAndRequestSizeGuards()
    {
        var type = typeof(ClaimsEvidenceController);
        var rate = type.GetCustomAttributes(typeof(EnableRateLimitingAttribute), true).Cast<EnableRateLimitingAttribute>().Single();
        var size = type.GetCustomAttributes(typeof(RequestSizeLimitAttribute), true).Cast<RequestSizeLimitAttribute>().Single();

        Assert.Equal("ClaimsEvidencePublic", rate.PolicyName);
        Assert.Equal(1_048_576, size.Bytes);
    }

    private ClaimsEvidenceController CreateController()
    {
        Directory.CreateDirectory(_directory);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_directory, "keys")));
        var store = new SqliteClaimsEvidenceEventStore(Path.Combine(_directory, "claims.db"), provider);
        var service = new ClaimsEvidenceService(store, new TrackingCredentialService(), new DisabledEvidenceBlobStore(), new ClaimsEvidenceProjector());
        var controller = new ClaimsEvidenceController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    private static ClaimsEvidenceController.CreateCaseRequest ValidRequest() => new(
        "anonymous",
        "Sensitive allegation text that must not be echoed.",
        "Example subject",
        "unknown",
        "unknown",
        null,
        false,
        new UrgencyIndicators(),
        Array.Empty<EvidenceMetadata>(),
        true);

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { }
    }
}
