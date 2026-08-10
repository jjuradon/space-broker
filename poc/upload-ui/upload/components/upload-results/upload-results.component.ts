import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { PlanItemResult } from '../../models/plan-item-result.model';

@Component({
  selector: 'app-upload-results',
  templateUrl: './upload-results.component.html',
  styleUrls: ['./upload-results.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class UploadResultsComponent {
  @Input() results: PlanItemResult[] = [];
  @Input() isDryRun = false;
  @Input() failureReason: string | null = null;
  @Output() retry = new EventEmitter<void>();

  get failedCount(): number {
    return this.results.filter((r) => !r.succeeded).length;
  }
}
