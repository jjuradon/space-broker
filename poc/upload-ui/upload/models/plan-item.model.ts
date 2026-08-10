export enum PlanItemKind {
  Create = 'Create',
  Update = 'Update',
  Delete = 'Delete'
}

export interface PlanItem {
  id: string;
  kind: PlanItemKind;
  description: string;
  payloadJson: string;
  preActionPayloadJson: string | null;
}
