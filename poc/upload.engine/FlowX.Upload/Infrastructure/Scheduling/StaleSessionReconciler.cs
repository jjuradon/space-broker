using FlowX.Upload.Application.Abstractions.Persistence;
using FlowX.Upload.Application.Abstractions.Scheduling;
using FlowX.Upload.Domain.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowX.Upload.Infrastructure.Scheduling;

/// <summary>
/// Startup sweep compensating for the in-memory executor's loss of state
/// across restarts. Three cases (branch names updated: PreProcessing -> Planning):
///   - Processing (stale): no partial-resumability by design -> fail the session;
///     the user re-confirms and retries from the same plan.
///   - Confirmed (stale): the plan is durable, only the in-memory enqueue was
///     lost -> safely re-enqueue processing.
///   - Planning (stale) WITH ContextJson/ParsedDataJson set: Prepare+Parse
///     already succeeded and only the Plan step was interrupted -> re-enqueue
///     revalidation, no file needed.
///   - Planning (stale) WITHOUT them: the raw file bytes only ever lived in the
///     in-memory queue and are unrecoverable -> fail, ask the user to re-upload.
/// </summary>
public sealed class StaleSessionReconciler : IHostedService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly IUploadStore _store;
    private readonly IUploadExecutor _executor;
    private readonly ILogger<StaleSessionReconciler> _logger;

    public StaleSessionReconciler(IUploadStore store, IUploadExecutor executor, ILogger<StaleSessionReconciler> logger)
    {
        _store = store;
        _executor = executor;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await ReconcileStaleProcessingAsync(ct);
        await ReconcileStaleConfirmedAsync(ct);
        await ReconcileStalePlanningAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task ReconcileStaleProcessingAsync(CancellationToken ct)
    {
        var sessions = await _store.GetByStatusOlderThanAsync(UploadSessionStatus.Processing, StaleAfter, ct);
        foreach (var session in sessions)
        {
            _logger.LogWarning("Failing orphaned session {SessionId}: interrupted mid-processing by restart.", session.Id);
            session.MarkFailed("Interrupted by process restart during execution.");
            await _store.SaveAsync(session, ct);
        }
    }

    private async Task ReconcileStaleConfirmedAsync(CancellationToken ct)
    {
        var sessions = await _store.GetByStatusOlderThanAsync(UploadSessionStatus.Confirmed, StaleAfter, ct);
        foreach (var session in sessions)
        {
            _logger.LogWarning("Re-enqueueing session {SessionId}: enqueue was lost by restart before processing started.", session.Id);
            await _executor.EnqueueProcessingAsync(session.Id, ct);
        }
    }

    private async Task ReconcileStalePlanningAsync(CancellationToken ct)
    {
        var sessions = await _store.GetByStatusOlderThanAsync(UploadSessionStatus.Planning, StaleAfter, ct);
        foreach (var session in sessions)
        {
            if (session.ContextJson is not null && session.ParsedDataJson is not null)
            {
                _logger.LogWarning("Re-enqueueing revalidation for {SessionId}: context and parsed data are durable.", session.Id);
                await _executor.EnqueueRevalidationAsync(session.Id, ct);
            }
            else
            {
                _logger.LogWarning("Failing orphaned session {SessionId}: never completed Prepare+Parse.", session.Id);
                session.MarkFailed("Interrupted before the file could be processed; please re-upload.");
                await _store.SaveAsync(session, ct);
            }
        }
    }
}
