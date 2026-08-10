import { ChangeDetectionStrategy, Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { FormArray, FormBuilder, FormControl, FormGroup } from '@angular/forms';
import { RowValidationError } from '../../models/row-validation-error.model';
import { RowCorrection } from '../../models/row-correction.model';

interface CorrectionRowForm {
  sheetIndex: FormControl<number>;
  rowNumber: FormControl<number>;
  field: FormControl<string>;
  message: FormControl<string>;
  value: FormControl<string>;
}

/**
 * Renders one editable row per validation error and emits only the rows the
 * user actually filled in (empty fixes are dropped — the user may only need
 * to correct a subset of the flagged rows in one pass).
 */
@Component({
  selector: 'app-validation-errors',
  templateUrl: './validation-errors.component.html',
  styleUrls: ['./validation-errors.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ValidationErrorsComponent implements OnChanges {
  @Input() errors: RowValidationError[] = [];
  @Output() corrected = new EventEmitter<RowCorrection[]>();

  form: FormArray<FormGroup<CorrectionRowForm>> = this.fb.array<FormGroup<CorrectionRowForm>>([]);

  constructor(private readonly fb: FormBuilder) {}

  ngOnChanges(): void {
    this.form = this.fb.array(
      this.errors.map((error) =>
        this.fb.group<CorrectionRowForm>({
          sheetIndex: this.fb.control(error.sheetIndex, { nonNullable: true }),
          rowNumber: this.fb.control(error.rowNumber, { nonNullable: true }),
          field: this.fb.control(error.field, { nonNullable: true }),
          message: this.fb.control(error.message, { nonNullable: true }),
          value: this.fb.control('', { nonNullable: true })
        })
      )
    );
  }

  onSubmit(): void {
    const corrections: RowCorrection[] = this.form.controls
      .map((group) => group.getRawValue())
      .filter((row) => row.value.trim().length > 0)
      .map(({ sheetIndex, rowNumber, field, value }) => ({ sheetIndex, rowNumber, field, value }));

    if (corrections.length > 0) {
      this.corrected.emit(corrections);
    }
  }
}
