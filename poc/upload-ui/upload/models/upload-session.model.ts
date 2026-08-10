import { RowValidationError } from './row-validation-error.model';
import { PlanItem } from './plan-item.model';
import { PlanItemResult } from './plan-item-result.model';

// Mirrors FlowX.Upload.Domain.UploadSessionStatus exactly — keep in sync with
// the backend enum. Requires JsonStringEnumConverter configured server-side
// so these arrive as strings, not numbers.
export enum UploadSessionStatus {
  PreProcessing = 'PreProcessing',
  ValidationFailed = 'ValidationFailed',
  PendingConfirmation = 'PendingConfirmation',
  Confirmed = 'Confirmed',
  Processing = 'Processing',
  Completed = 'Completed',
  Failed = 'Failed'
}

export interface UploadSessionDto {
  id: string;
  status: UploadSessionStatus;
  isDryRun: boolean;
  validationErrors: RowValidationError[];
  plan: PlanItem[] | null;
  results: PlanItemResult[];
  failureReason: string | null;
}
