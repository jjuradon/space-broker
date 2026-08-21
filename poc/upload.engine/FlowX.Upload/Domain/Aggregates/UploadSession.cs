using FlowX.Upload.Domain.Entities;
using FlowX.Upload.Domain.Enums;
using FlowX.Upload.Domain.ValueObjects;

namespace FlowX.Upload.Domain.Aggregates;

/// <summary>
/// Aggregate root for one upload's lifecycle:
/// Planning (Prepare -> Parse -> Plan, with an optional correction loop)
/// -> user confirmation of a subset of the plan -> Processing.
/// All state transitions are guarded here; nothing outside this type may
/// put a session into an invalid state.
/// </summary>
public sealed class UploadSession
{
    public Guid Id { get; private set; }
    public string PipelineKey { get; private set; } = default!;
    public string OwnerContext { get; private set; } = default!;
    public string InitiatedBy { get; private set; } = default!;
    public UploadSessionStatus Status { get; private set; }
    public bool IsDryRun { get; private set; }

    /// <summary>Durable snapshot of the pipeline-specific context produced by
    /// Prepare. Persisted so Parse/Correct never need the original file stream again.</summary>
    public string? ContextJson { get; private set; }

    /// <summary>Durable snapshot of the parsed (and possibly corrected) source data, as JSON.</summary>
    public string? ParsedDataJson { get; private set; }

    public IReadOnlyCollection<RowCorrection> PendingCorrections { get; private set; } = Array.Empty<RowCorrection>();
    public IReadOnlyCollection<RowValidationError> ValidationErrors { get; private set; } = Array.Empty<RowValidationError>();
    public IReadOnlyCollection<PlanItem> Plan { get; private set; } = Array.Empty<PlanItem>();
    public IReadOnlyCollection<PlanItemResult> Results { get; private set; } = Array.Empty<PlanItemResult>();
    public string? FailureReason { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }

    private UploadSession() { } // required for persistence rehydration

    public static UploadSession Start(string pipelineKey, string ownerContext, string initiatedBy, bool isDryRun = false)
    {
        if (string.IsNullOrWhiteSpace(pipelineKey))
            throw new ArgumentException("Pipeline key is required.", nameof(pipelineKey));

        return new UploadSession
        {
            Id = Guid.NewGuid(),
            PipelineKey = pipelineKey,
            OwnerContext = ownerContext,
            InitiatedBy = initiatedBy,
            Status = UploadSessionStatus.Planning,
            IsDryRun = isDryRun,
            LastActivityAt = DateTimeOffset.UtcNow
        };
    }

    public void SetContext(string json)
    {
        EnsureStatus(UploadSessionStatus.Planning);
        ContextJson = json;
        Touch();
    }

    public void SetParsedData(string json)
    {
        EnsureStatus(UploadSessionStatus.Planning);
        ParsedDataJson = json;
        Touch();
    }

    public void FailValidation(IReadOnlyCollection<RowValidationError> errors)
    {
        EnsureStatus(UploadSessionStatus.Planning);
        ValidationErrors = errors;
        Status = UploadSessionStatus.ValidationFailed;
        Touch();
    }

    /// <summary>
    /// Submits field-level fixes for a previously failed validation, without
    /// needing the original file. Corrections may be empty (re-plan after an
    /// external fix). Re-enters Planning.
    /// </summary>
    public void SubmitCorrections(string principalId, IReadOnlyCollection<RowCorrection> corrections)
    {
        EnsureStatus(UploadSessionStatus.ValidationFailed);
        if (principalId != InitiatedBy)
            throw new InvalidOperationException("Only the uploader who initiated the session may submit corrections.");
        if (ContextJson is null || ParsedDataJson is null)
            throw new InvalidOperationException("No parsed data/context available to correct; the file must be re-uploaded.");

        PendingCorrections = corrections;
        ValidationErrors = Array.Empty<RowValidationError>();
        Status = UploadSessionStatus.Planning;
        Touch();
    }

    public void SetPlan(IReadOnlyCollection<PlanItem> plan)
    {
        EnsureStatus(UploadSessionStatus.Planning);
        if (plan.Count == 0)
            throw new InvalidOperationException("Planning produced an empty plan; nothing to confirm.");

        Plan = plan;
        Status = UploadSessionStatus.PendingConfirmation;
        Touch();
    }

    /// <summary>Only the initiating user may confirm, and only a subset of the plan need be selected.</summary>
    public void Confirm(string principalId, IReadOnlyCollection<Guid> selectedItemIds)
    {
        EnsureStatus(UploadSessionStatus.PendingConfirmation);
        if (principalId != InitiatedBy)
            throw new InvalidOperationException("Only the uploader who initiated the session may confirm it.");
        if (selectedItemIds.Count == 0)
            throw new InvalidOperationException("At least one plan item must be selected.");

        Plan = Plan.Where(p => selectedItemIds.Contains(p.Id)).ToList();
        Status = UploadSessionStatus.Confirmed;
        Touch();
    }

    public void MarkProcessing()
    {
        EnsureStatus(UploadSessionStatus.Confirmed);
        Status = UploadSessionStatus.Processing;
        Touch();
    }

    /// <summary>Completion may carry partial item-level failures; that is not a session failure.</summary>
    public void MarkCompleted(IReadOnlyCollection<PlanItemResult> results)
    {
        EnsureStatus(UploadSessionStatus.Processing);
        Results = results;
        Status = UploadSessionStatus.Completed;
        Touch();
    }

    public void MarkFailed(string reason)
    {
        if (Status == UploadSessionStatus.Completed)
            throw new InvalidOperationException("Cannot fail a completed session.");

        Status = UploadSessionStatus.Failed;
        FailureReason = reason;
        Touch();
    }

    /// <summary>Session-level failure recovery: re-enter confirmation with the same plan.
    /// No partial resumability is offered by design — see docs/design.md.</summary>
    public void PrepareForRetry()
    {
        EnsureStatus(UploadSessionStatus.Failed);
        Status = UploadSessionStatus.PendingConfirmation;
        FailureReason = null;
        Touch();
    }

    /// <summary>Used only by the reconciler: recovers a Confirmed session whose enqueue
    /// was lost to a restart. The plan is durable, so re-entering Confirmed is safe.</summary>
    internal void ResetToConfirmed()
    {
        EnsureStatus(UploadSessionStatus.Processing);
        Status = UploadSessionStatus.Confirmed;
        Touch();
    }

    private void Touch() => LastActivityAt = DateTimeOffset.UtcNow;

    private void EnsureStatus(UploadSessionStatus expected)
    {
        if (Status != expected)
            throw new InvalidOperationException($"Expected status {expected} but session is {Status}.");
    }
}
