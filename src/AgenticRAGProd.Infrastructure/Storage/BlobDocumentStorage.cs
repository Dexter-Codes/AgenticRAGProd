using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Infrastructure.Configuration;

namespace AgenticRAGProd.Infrastructure.Storage;

internal sealed class BlobDocumentStorage(
    BlobContainerClient container,
    IOptions<AzureStorageOptions> options,
    ILogger<BlobDocumentStorage> logger)
    : IDocumentStorage
{
    public async Task UploadAsync(
        Guid documentId,
        Stream content,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
            throw new ArgumentException(
                "Document ID cannot be empty.", nameof(documentId));

        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanRead)
            throw new ArgumentException(
                "The document stream must be readable.", nameof(content));

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(TimeSpan.FromSeconds(
            options.Value.RequestTimeoutSeconds));

        var blobName = $"documents/{documentId:N}/original.pdf";
        var blob = container.GetBlobClient(blobName);

        await blob.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "application/pdf"
                },
                Conditions = new BlobRequestConditions
                {
                    IfNoneMatch = ETag.All
                }
            },
            cancellationToken: timeout.Token);

        logger.LogInformation(
            "Stored original PDF for document {DocumentId}",
            documentId);
    }

    public async Task<Stream> OpenReadAsync(
    Guid documentId,
    CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException(
                "Document ID cannot be empty.",
                nameof(documentId));
        }

        var blobName = $"documents/{documentId:N}/original.pdf";
        var blob = container.GetBlobClient(blobName);

        return await blob.OpenReadAsync(
            cancellationToken: cancellationToken);
    }
}