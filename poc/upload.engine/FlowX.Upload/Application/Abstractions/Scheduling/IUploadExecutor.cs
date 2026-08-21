namespace FlowX.Upload.Application.Abstractions.Scheduling;

/// <summary>Orchestration/scheduling port. BackgroundServiceUploadExecutor is the default,
/// single-pod, in-memory implementation; swap for a durable-queue implementation later
/// without changing anything above this port.</summary>
public interface IUploadExecutor
{
    /// <summary>RENAMED from EnqueuePreProcessingAsync — enqueues the combined
    /// Prepare -> Parse -> Plan pass.</summary>
    Task EnqueuePlanningAsync(Guid sessionId, byte[] fileContent, CancellationToken ct);
    Task EnqueueRevalidationAsync(Guid sessionId, CancellationToken ct);
    Task EnqueueProcessingAsync(Guid sessionId, CancellationToken ct);
}
