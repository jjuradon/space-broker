namespace FlowX.Upload.Infrastructure;

internal abstract record UploadWorkItem
{
    public sealed record PreProcess(Guid SessionId, byte[] FileContent) : UploadWorkItem;
    public sealed record Process(Guid SessionId) : UploadWorkItem;
}

public sealed class BackgroundServiceUploadExecutor(
    IServiceScopeFactory scopeFactory,
    ILogger<BackgroundServiceUploadExecutor> logger) : BackgroundService, IUploadExecutor
{
    private readonly Channel<UploadWorkItem> _queue = Channel.CreateUnbounded<UploadWorkItem>();

    public Task EnqueuePreProcessingAsync(Guid sessionId, byte[] fileContent, CancellationToken ct) =>
        _queue.Writer.WriteAsync(new UploadWorkItem.PreProcess(sessionId, fileContent), ct).AsTask();

    public Task EnqueueProcessingAsync(Guid sessionId, CancellationToken ct) =>
        _queue.Writer.WriteAsync(new UploadWorkItem.Process(sessionId), ct).AsTask();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<UploadOrchestrator>();

            try
            {
                switch (item)
                {
                    case UploadWorkItem.PreProcess p:
                        await orchestrator.RunPreProcessingAsync(p.SessionId, p.FileContent, stoppingToken);
                        break;
                    case UploadWorkItem.Process pr:
                        await orchestrator.RunProcessingAsync(pr.SessionId, stoppingToken);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Orchestrator already persists Failed for domain-level failures;
                // this catch is the last line of defense for genuinely unexpected exceptions.
                logger.LogError(ex, "Unhandled error processing upload work item.");
            }
        }
    }
}

/// Startup sweep, compensating for the in-memory queue's loss of state across restarts.
/// - Processing (session-level, no resumability): fail it — matches the "fail whole session,
///   re-confirm" contract. Nothing was partially applied because IUploadProcessor commits
///   per-chunk; a session caught here means the crash landed before any chunk committed,
///   or between chunks — either way, re-confirming and re-running is safe.
/// - Confirmed (enqueued but the in-memory item was lost before a worker picked it up):
///   the plan itself is durable, so this is safely re-enqueued rather than failed.
public sealed class StaleSessionReconciler(
    IUploadStore store,
    IUploadExecutor executor,
    ILogger<StaleSessionReconciler> logger) : IHostedService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    public async Task StartAsync(CancellationToken ct)
    {
        var stuckProcessing = await store.GetByStatusOlderThanAsync(UploadSessionStatus.Processing, StaleAfter, ct);
        foreach (var session in stuckProcessing)
        {
            logger.LogWarning("Failing orphaned session {SessionId}: interrupted mid-processing by restart.", session.Id);
            session.MarkFailed("Interrupted by process restart during execution.");
            await store.SaveAsync(session, ct);
        }

        var stuckConfirmed = await store.GetByStatusOlderThanAsync(UploadSessionStatus.Confirmed, StaleAfter, ct);
        foreach (var session in stuckConfirmed)
        {
            logger.LogWarning("Re-enqueueing session {SessionId}: enqueue was lost by restart before processing started.", session.Id);
            await executor.EnqueueProcessingAsync(session.Id, ct);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}