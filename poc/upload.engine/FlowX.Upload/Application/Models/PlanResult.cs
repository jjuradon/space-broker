using FlowX.Upload.Domain.Entities;
using FlowX.Upload.Domain.ValueObjects;

namespace FlowX.Upload.Application.Models;

/// <summary>
/// RENAMED from PreProcessResult. Outcome of Stage 3 (Plan): either the
/// parsed data failed validation, or a plan was produced. The success
/// factory is named Success (not "Plan", to avoid the awkward
/// PlanResult.Plan(...) read against the Plan property below).
/// </summary>
public sealed class PlanResult
{
    public bool IsValidationFailure { get; }
    public IReadOnlyCollection<RowValidationError> Errors { get; }
    public IReadOnlyCollection<PlanItem> Plan { get; }

    private PlanResult(bool isValidationFailure, IReadOnlyCollection<RowValidationError> errors, IReadOnlyCollection<PlanItem> plan)
        => (IsValidationFailure, Errors, Plan) = (isValidationFailure, errors, plan);

    public static PlanResult ValidationFailed(IReadOnlyCollection<RowValidationError> errors) =>
        new(true, errors, Array.Empty<PlanItem>());

    public static PlanResult Success(IReadOnlyCollection<PlanItem> planItems) =>
        new(false, Array.Empty<RowValidationError>(), planItems);
}
