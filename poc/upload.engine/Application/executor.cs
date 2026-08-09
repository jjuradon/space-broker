namespace FlowX.Upload.Application;

public interface IUploadStore
{
    Task SaveAsync(UploadSession session, CancellationToken ct);
    Task<UploadSession?> GetAsync(Guid sessionId, CancellationToken ct);
    Task<IReadOnlyCollection<UploadSession>> GetByStatusOlderThanAsync(UploadSessionStatus status, TimeSpan olderThan, CancellationToken ct);
}

public interface IUploadExecutor
{
    Task EnqueuePreProcessingAsync(Guid sessionId, byte[] fileContent, CancellationToken ct);
    Task EnqueueProcessingAsync(Guid sessionId, CancellationToken ct);
}