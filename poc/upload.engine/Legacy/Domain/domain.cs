namespace FlowX.Upload.Domain;

public enum UploadSessionStatus
{
    PreProcessing,
    ValidationFailed,
    PendingConfirmation,
    Confirmed,
    Processing,
    Completed,
    Failed
}

public sealed record RowValidationError(int SheetIndex, int RowNumber, string Field, string Message);
public sealed record PlanItem(Guid Id, PlanItemKind Kind, string PayloadJson, string? PreActionPayloadJson, string Description);
public enum PlanItemKind { Create, Update, Delete }
public sealed record PlanItemResult(Guid PlanItemId, bool Succeeded, string? FailureReason);

public sealed class UploadSession
{
    public Guid Id { get; private set; }
    public string PipelineKey { get; private set; } = default!;
    public string OwnerContext { get; private set; } = default!;
    public string InitiatedBy { get; private set; } = default!;
    public UploadSessionStatus Status { get; private set; }
    public IReadOnlyCollection<PlanItem> Plan { get; private set; } = Array.Empty<PlanItem>();
    public IReadOnlyCollection<RowValidationError> ValidationErrors { get; private set; } = Array.Empty<RowValidationError>();
    public IReadOnlyCollection<PlanItemResult> Results { get; private set; } = Array.Empty<PlanItemResult>();
    public string? FailureReason { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }

    private UploadSession() { } // rehydration

    public static UploadSession Start(string pipelineKey, string ownerContext, string initiatedBy) => new()
    {
        Id = Guid.NewGuid(),
        PipelineKey = pipelineKey,
        OwnerContext = ownerContext,
        InitiatedBy = initiatedBy,
        Status = UploadSessionStatus.PreProcessing,
        LastActivityAt = DateTimeOffset.UtcNow
    };

    public void FailValidation(IReadOnlyCollection<RowValidationError> errors)
    {
        EnsureStatus(UploadSessionStatus.PreProcessing);
        ValidationErrors = errors;
        Status = UploadSessionStatus.ValidationFailed;
        Touch();
    }

    /// Supports "fix rows on screen, re-validate without re-uploading" without a new file.
    public void RetryValidation()
    {
        EnsureStatus(UploadSessionStatus.ValidationFailed);
        ValidationErrors = Array.Empty<RowValidationError>();
        Status = UploadSessionStatus.PreProcessing;
        Touch();
    }

    public void SetPlan(IReadOnlyCollection<PlanItem> plan)
    {
        EnsureStatus(UploadSessionStatus.PreProcessing);
        if (plan.Count == 0)
            throw new InvalidOperationException("Pre-processing produced an empty plan.");
        Plan = plan;
        Status = UploadSessionStatus.PendingConfirmation;
        Touch();
    }

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

    public void PrepareForRetry()
    {
        EnsureStatus(UploadSessionStatus.Failed);
        Status = UploadSessionStatus.PendingConfirmation;
        FailureReason = null;
        Touch();
    }

    /// Used only by the reconciler to recover a Confirmed session whose enqueue was
    /// lost to a restart. The plan is durable; re-entering Confirmed is safe to retry.
    internal void ResetToConfirmed()
    {
        EnsureStatus(UploadSessionStatus.Processing);
        Status = UploadSessionStatus.Confirmed;
        Touch();
    }

    private void Touch() => LastActivityAt = DateTimeOffset.UtcNow;
    private void EnsureStatus(UploadSessionStatus expected)
    {
        if (Status != expected) throw new InvalidOperationException($"Expected {expected} but session is {Status}.");
    }
}