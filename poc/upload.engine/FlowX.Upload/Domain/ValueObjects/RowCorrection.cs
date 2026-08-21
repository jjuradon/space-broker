namespace FlowX.Upload.Domain.ValueObjects;

/// <summary>A field-level fix submitted by the user for a row that failed validation.</summary>
public sealed record RowCorrection(int SheetIndex, int RowNumber, string Field, string Value);
