import { ChangeDetectionStrategy, Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { FormArray, FormBuilder, FormControl, FormGroup } from '@angular/forms';
import { PlanItem, PlanItemKind } from '../../models/plan-item.model';

interface PlanRowForm {
  id: FormControl<string>;
  selected: FormControl<boolean>;
}

/**
 * User reviews the proposed plan and selects which rows to actually execute.
 * Everything is selected by default; the user narrows it down, not builds it up.
 */
@Component({
  selector: 'app-plan-review',
  templateUrl: './plan-review.component.html',
  styleUrls: ['./plan-review.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PlanReviewComponent implements OnChanges {
  @Input() plan: PlanItem[] = [];
  @Output() confirmed = new EventEmitter<string[]>();

  readonly PlanItemKind = PlanItemKind;

  form: FormArray<FormGroup<PlanRowForm>> = this.fb.array<FormGroup<PlanRowForm>>([]);

  constructor(private readonly fb: FormBuilder) {}

  ngOnChanges(): void {
    this.form = this.fb.array(
      this.plan.map((item) =>
        this.fb.group<PlanRowForm>({
          id: this.fb.control(item.id, { nonNullable: true }),
          selected: this.fb.control(true, { nonNullable: true })
        })
      )
    );
  }

  get selectedCount(): number {
    return this.form.controls.filter((g) => g.controls.selected.value).length;
  }

  toggleAll(selected: boolean): void {
    this.form.controls.forEach((g) => g.controls.selected.setValue(selected));
  }

  kindBadgeClass(kind: PlanItemKind): string {
    switch (kind) {
      case PlanItemKind.Create:
        return 'bg-success';
      case PlanItemKind.Update:
        return 'bg-primary';
      case PlanItemKind.Delete:
        return 'bg-danger';
    }
  }

  onConfirm(): void {
    const selectedIds = this.form.controls
      .filter((g) => g.controls.selected.value)
      .map((g) => g.controls.id.value);

    if (selectedIds.length > 0) {
      this.confirmed.emit(selectedIds);
    }
  }
}
