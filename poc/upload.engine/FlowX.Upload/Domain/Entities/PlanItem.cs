using FlowX.Upload.Domain.Enums;

namespace FlowX.Upload.Domain.Entities;

/// <summary>
/// One proposed change within an UploadSession's plan. This is a domain
/// Entity, not a Value Object: Id is a genuine identity used for
/// reference — UploadSession.Confirm selects items by Id, and
/// PlanItemResult.PlanItemId correlates back to this Id after execution.
/// Payloads are opaque JSON — only the pipeline that produced them
/// (its Planner/Processor) knows their shape.
/// </summary>
public sealed record PlanItem(Guid Id, PlanItemKind Kind, string PayloadJson, string? PreActionPayloadJson, string Description);
