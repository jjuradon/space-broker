using FlowX.Upload.Application.Abstractions.Scheduling;
using FlowX.Upload.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowX.Upload.Infrastructure.Scheduling;

/// <summary>
/// Default IUploadExecutor: an in-process, single-pod, in-memory queue driven
/// by a BackgroundService. KNOWN LIMITATION: work items queued here (in
/// particular, raw file bytes for a not-yet-parsed upload) do not survive a
/// pod restart. StaleSessionReconciler mitigates this for every stage EXCEPT
/// the very first parse — see its remarks. Replace this implementation (only)
/// with a durable-queue-backed IUploadExecutor when horizontal scaling or a
/// stronger durability guarantee becomes available; nothing else in the
/// package needs to change.
/// </summary>
public sealed class BackgroundServiceUploadExecutor : BackgroundService, IUploadExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackgroundServiceUploadExecutor> _logger;
    private readonly System.Threading.Channels.Channel<UploadWorkItem> _queue =
        System.Threading.Channels.Channel.CreateUnbounded<UploadWorkItem>();

    public BackgroundServiceUploadExecutor(IServiceScopeFactory scopeFactory, ILogger<BackgroundServiceUploadExecutor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task EnqueuePlanningAsync(Guid sessionId, byte[] fileContent, CancellationToken ct) =>
        _queue.Writer.WriteAsync(new UploadWorkItem.Plan(sessionId, fileContent), ct).AsTask();

    public Task EnqueueRevalidationAsync(Guid sessionId, CancellationToken ct) =>
        _queue.Writer.WriteAsync(new UploadWorkItem.Revalidate(sessionId), ct).AsTask();

    public Task EnqueueProcessingAsync(Guid sessionId, CancellationToken ct) =>
        _queue.Writer.WriteAsync(new UploadWorkItem.Process(sessionId), ct).AsTask();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var scope = _scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<UploadOrchestrator>();

            try
            {
                switch (item)
                {
                    case UploadWorkItem.Plan p:
                        await orchestrator.RunPlanningAsync(p.SessionId, p.FileContent, stoppingToken);
                        break;
                    case UploadWorkItem.Revalidate r:
                        await orchestrator.RunRevalidationAsync(r.SessionId, stoppingToken);
                        break;
                    case UploadWorkItem.Process pr:
                        await orchestrator.RunProcessingAsync(pr.SessionId, stoppingToken);
                        break;
                }
            }
            catch (Exception ex)
            {
                // UploadOrchestrator already persists Failed for domain-level failures inside
                // its own try/catch; this is the last line of defense for truly unexpected exceptions.
                _logger.LogError(ex, "Unhandled error processing upload work item {WorkItem}.", item);
            }
        }
    }
}
