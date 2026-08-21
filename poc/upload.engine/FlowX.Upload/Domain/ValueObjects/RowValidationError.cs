namespace FlowX.Upload.Domain.ValueObjects;

/// <summary>A field-level validation problem on a specific source row. No identity — pure structural fact.</summary>
public sealed record RowValidationError(int SheetIndex, int RowNumber, string Field, string Message);
