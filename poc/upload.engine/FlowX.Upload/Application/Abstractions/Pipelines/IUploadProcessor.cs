using FlowX.Upload.Domain.Entities;
using FlowX.Upload.Domain.ValueObjects;

namespace FlowX.Upload.Application.Abstractions.Pipelines;

/// <summary>
/// Stage 4 (Process). Executes the confirmed plan.
/// Implementers MUST commit atomically per chunk (not per whole plan) — partial
/// success is expected and reported via the returned PlanItemResult collection,
/// not treated as a thrown exception / session-level failure.
/// When isDryRun is true, implementers should execute real writes inside a
/// transaction and always roll it back, so genuine DB constraints are still
/// exercised. External, non-transactional side effects generally cannot be
/// verified this way — document that gap per pipeline.
/// </summary>
public interface IUploadProcessor
{
    Task<IReadOnlyCollection<PlanItemResult>> ExecuteAsync(
        IReadOnlyCollection<PlanItem> confirmedPlan, bool isDryRun, CancellationToken ct);
}
