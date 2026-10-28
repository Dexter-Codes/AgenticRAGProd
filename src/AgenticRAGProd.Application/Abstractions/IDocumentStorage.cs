namespace AgenticRAGProd.Application.Abstractions;

public interface IDocumentStorage
{
    Task UploadAsync(
        Guid documentId,
        Stream content,
        CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(
    Guid documentId,
    CancellationToken cancellationToken);
}