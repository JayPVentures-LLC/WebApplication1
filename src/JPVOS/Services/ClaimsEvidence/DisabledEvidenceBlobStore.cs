namespace JPVOS.Services.ClaimsEvidence;

public sealed class DisabledEvidenceBlobStore : IEvidenceBlobStore
{
    public bool IsEnabled => false;

    public Task<string> StoreAsync(Stream content, string contentType, CancellationToken cancellationToken) =>
        throw new ClaimsEvidenceBinaryUnsupportedException("Binary evidence ingestion is not enabled because no approved private evidence store is configured.");
}
