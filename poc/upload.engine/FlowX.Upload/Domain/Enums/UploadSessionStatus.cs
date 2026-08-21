namespace FlowX.Upload.Domain.Enums;

/// <summary>
/// Lifecycle status of an UploadSession. "Planning" covers the async
/// Prepare -> Parse -> Plan pass (RENAMED from "PreProcessing" — the status
/// now names its outcome, the Plan, rather than a stage that no longer
/// exists under that name).
/// </summary>
public enum UploadSessionStatus
{
    Planning,
    ValidationFailed,
    PendingConfirmation,
    Confirmed,
    Processing,
    Completed,
    Failed
}
