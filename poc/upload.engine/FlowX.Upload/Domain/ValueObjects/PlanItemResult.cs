namespace FlowX.Upload.Domain.ValueObjects;

/// <summary>Outcome of executing one PlanItem. Item-level failure does not imply session-level failure.</summary>
public sealed record PlanItemResult(Guid PlanItemId, bool Succeeded, string? FailureReason);
