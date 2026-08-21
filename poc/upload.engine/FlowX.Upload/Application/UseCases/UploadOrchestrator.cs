using FlowX.Upload.Application.Abstractions.Persistence;
using FlowX.Upload.Application.Abstractions.Scheduling;
using FlowX.Upload.Application.Models;
using FlowX.Upload.Application.Pipelines;
using FlowX.Upload.Domain.Aggregates;
using FlowX.Upload.Domain.ValueObjects;

namespace FlowX.Upload.Application.UseCases;

/// <summary>
/// Coordinates the full session lifecycle for any registered pipeline:
/// Start -> Plan (Prepare+Parse+Plan) -> [ValidationFailed -> SubmitCorrections -> Revalidate]*
/// -> Confirm -> Process.
/// This is the single place pipeline-agnostic orchestration logic lives; individual
/// pipelines never re-implement it.
/// RunPreProcessingAsync was RENAMED to RunPlanningAsync; ApplyPreProcessResult
/// was RENAMED to ApplyPlanResult — both now match the Plan stage's name.
/// </summary>
public sealed class UploadOrchestrator
{
    private readonly IUploadPipelineRegistry _registry;
    private readonly IUploadStore _store;
    private readonly IUploadExecutor _executor;

    public UploadOrchestrator(IUploadPipelineRegistry registry, IUploadStore store, IUploadExecutor executor)
    {
        _registry = registry;
        _store = store;
        _executor = executor;
    }

    /// <summary>Creates the session and enqueues the Plan work item. Returns immediately (async 202 flow).</summary>
    public async Task<Guid> StartAsync(
        string pipelineKey, string ownerContext, string initiatedBy, byte[] fileContent, bool isDryRun, CancellationToken ct)
    {
        var session = UploadSession.Start(pipelineKey, ownerContext, initiatedBy, isDryRun);
        await _store.SaveAsync(session, ct);
        await _executor.EnqueuePlanningAsync(session.Id, fileContent, ct);
        return session.Id;
    }

    /// <summary>Invoked by an IUploadExecutor worker. Runs Prepare then Parse in one pass
    /// while the file bytes are still available (they are never persisted — see
    /// docs/design.md), persists both outputs, then runs Plan (validation + plan-building).</summary>
    public async Task RunPlanningAsync(Guid sessionId, byte[] fileContent, CancellationToken ct)
    {
        var session = await GetSessionOrThrow(sessionId, ct);
        var pipeline = _registry.Resolve(session.PipelineKey);

        try
        {
            object context;
            using (var prepareStream = new MemoryStream(fileContent))
            {
                context = await pipeline.PrepareAsync(prepareStream, session.OwnerContext, session.InitiatedBy, ct);
            }
            session.SetContext(pipeline.SerializeContext(context));

            object parsed;
            using (var parseStream = new MemoryStream(fileContent)) // fresh stream — Prepare may have consumed the first
            {
                parsed = await pipeline.ParseAsync(parseStream, context, ct);
            }
            session.SetParsedData(pipeline.SerializeParsedData(parsed));

            var result = await pipeline.PlanAsync(parsed, ct);
            ApplyPlanResult(session, result);
        }
        catch (Exception ex)
        {
            session.MarkFailed($"Planning failed: {ex.Message}");
        }

        await _store.SaveAsync(session, ct);
    }

    /// <summary>Enhancement: submit field-level corrections for a validation failure, without
    /// needing the original file. Enqueues re-planning from the durably-stored context/parsed data.</summary>
    public async Task SubmitCorrectionsAsync(
        Guid sessionId, string principalId, IReadOnlyCollection<RowCorrection> corrections, CancellationToken ct)
    {
        var session = await GetSessionOrThrow(sessionId, ct);
        session.SubmitCorrections(principalId, corrections); // validates status/ownership, -> Planning
        await _store.SaveAsync(session, ct);
        await _executor.EnqueueRevalidationAsync(sessionId, ct);
    }

    /// <summary>Invoked by an IUploadExecutor worker. Re-runs Plan from the stored
    /// context/parsed data (with pending corrections applied, if any) — no file re-upload required.</summary>
    public async Task RunRevalidationAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await GetSessionOrThrow(sessionId, ct);
        var pipeline = _registry.Resolve(session.PipelineKey);

        try
        {
            var context = pipeline.DeserializeContext(session.ContextJson!);
            var parsed = pipeline.DeserializeParsedData(session.ParsedDataJson!);

            var corrected = session.PendingCorrections.Count > 0
                ? pipeline.ApplyCorrections(parsed, context, session.PendingCorrections)
                : parsed;

            session.SetParsedData(pipeline.SerializeParsedData(corrected));
            var result = await pipeline.PlanAsync(corrected, ct);
            ApplyPlanResult(session, result);
        }
        catch (Exception ex)
        {
            session.MarkFailed($"Re-validation failed: {ex.Message}");
        }

        await _store.SaveAsync(session, ct);
    }

    /// <summary>User confirms a (possibly partial) selection of the plan. Enqueues processing.</summary>
    public async Task ConfirmAsync(
        Guid sessionId, string principalId, IReadOnlyCollection<Guid> selectedItemIds, CancellationToken ct)
    {
        var session = await GetSessionOrThrow(sessionId, ct);
        session.Confirm(principalId, selectedItemIds);
        await _store.SaveAsync(session, ct);
        await _executor.EnqueueProcessingAsync(sessionId, ct);
    }

    /// <summary>Invoked by an IUploadExecutor worker. Executes the confirmed plan via the pipeline's processor.</summary>
    public async Task RunProcessingAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await GetSessionOrThrow(sessionId, ct);
        var pipeline = _registry.Resolve(session.PipelineKey);

        try
        {
            session.MarkProcessing();
            await _store.SaveAsync(session, ct);

            var results = await pipeline.ProcessAsync(session.Plan, session.IsDryRun, ct);
            session.MarkCompleted(results);
        }
        catch (Exception ex)
        {
            session.MarkFailed($"Processing failed: {ex.Message}");
        }

        await _store.SaveAsync(session, ct);
    }

    /// <summary>Session-level failure recovery. Re-enters confirmation with the same plan.</summary>
    public async Task RetryAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await GetSessionOrThrow(sessionId, ct);
        session.PrepareForRetry();
        await _store.SaveAsync(session, ct);
    }

    private static void ApplyPlanResult(UploadSession session, PlanResult result)
    {
        if (result.IsValidationFailure)
            session.FailValidation(result.Errors);
        else
            session.SetPlan(result.Plan);
    }

    private async Task<UploadSession> GetSessionOrThrow(Guid sessionId, CancellationToken ct) =>
        await _store.GetAsync(sessionId, ct)
        ?? throw new InvalidOperationException($"Upload session '{sessionId}' not found.");
}
