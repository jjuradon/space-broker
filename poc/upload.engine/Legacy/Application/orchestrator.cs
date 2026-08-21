namespace FlowX.Upload.Application;

/// Coordinates the Parse → PreProcess → [wait] → Confirm → Process lifecycle for any
/// registered pipeline. Called from the API layer (Start/Confirm/Retry) and from an
/// IUploadExecutor implementation (RunPreProcessingAsync/RunProcessingAsync).
public sealed class UploadOrchestrator(
    IUploadPipelineRegistry registry,
    IUploadStore store,
    IUploadExecutor executor)
{
    public async Task<Guid> StartAsync(string pipelineKey, string ownerContext, string initiatedBy, byte[] fileContent, CancellationToken ct)
    {
        var session = UploadSession.Start(pipelineKey, ownerContext, initiatedBy);
        await store.SaveAsync(session, ct);
        await executor.EnqueuePreProcessingAsync(session.Id, fileContent, ct);
        return session.Id;
    }

    public async Task RunPreProcessingAsync(Guid sessionId, byte[] fileContent, CancellationToken ct)
    {
        var session = await store.GetAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Upload session '{sessionId}' not found.");
        var pipeline = registry.Resolve(session.PipelineKey);

        try
        {
            using var stream = new MemoryStream(fileContent);
            var parsed = await pipeline.ParseAsync(stream, ct);
            var result = await pipeline.PreProcessAsync(parsed, ct);

            if (result.IsValidationFailure)
                session.FailValidation(result.Errors);
            else
                session.SetPlan(result.Plan);
        }
        catch (Exception ex)
        {
            session.MarkFailed($"Pre-processing failed: {ex.Message}");
        }

        await store.SaveAsync(session, ct);
    }

    public async Task ConfirmAsync(Guid sessionId, string principalId, IReadOnlyCollection<Guid> selectedItemIds, CancellationToken ct)
    {
        var session = await store.GetAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Upload session '{sessionId}' not found.");
        session.Confirm(principalId, selectedItemIds);
        await store.SaveAsync(session, ct);
        await executor.EnqueueProcessingAsync(sessionId, ct);
    }

    public async Task RunProcessingAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await store.GetAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Upload session '{sessionId}' not found.");
        var pipeline = registry.Resolve(session.PipelineKey);

        try
        {
            session.MarkProcessing();
            await store.SaveAsync(session, ct);

            var results = await pipeline.ProcessAsync(session.Plan, ct);
            session.MarkCompleted(results);
        }
        catch (Exception ex)
        {
            session.MarkFailed($"Processing failed: {ex.Message}");
        }

        await store.SaveAsync(session, ct);
    }

    public async Task RetryValidationAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await store.GetAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Upload session '{sessionId}' not found.");
        session.RetryValidation();
        await store.SaveAsync(session, ct);
        // App-specific: re-run PreProcess only (using screen-corrected data), a variant
        // not detailed here — extension point, see Guidelines.
    }

    public async Task RetryAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await store.GetAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Upload session '{sessionId}' not found.");
        session.PrepareForRetry();
        await store.SaveAsync(session, ct);
    }
}