export interface RowValidationError {
  sheetIndex: number;
  rowNumber: number;
  field: string;
  message: string;
}
