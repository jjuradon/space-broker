import { ChangeDetectionStrategy, Component } from '@angular/core';
import { FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { finalize } from 'rxjs/operators';
import { UploadService } from '../../services/upload.service';

interface UploadStartForm {
  file: FormControl<File | null>;
  dryRun: FormControl<boolean>;
}

@Component({
  selector: 'app-upload-start',
  templateUrl: './upload-start.component.html',
  styleUrls: ['./upload-start.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class UploadStartComponent {
  readonly form: FormGroup<UploadStartForm> = this.fb.group({
    file: this.fb.control<File | null>(null, Validators.required),
    dryRun: this.fb.control(false, { nonNullable: true })
  });

  isSubmitting = false;
  errorMessage: string | null = null;

  private readonly branchId = this.route.snapshot.paramMap.get('branchId')!;

  constructor(
    private readonly fb: FormBuilder,
    private readonly uploadService: UploadService,
    private readonly route: ActivatedRoute,
    private readonly router: Router
  ) {}

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.item(0) ?? null;
    this.form.controls.file.setValue(file);
  }

  onSubmit(): void {
    if (this.form.invalid || this.isSubmitting) {
      return;
    }

    const { file, dryRun } = this.form.getRawValue();
    this.isSubmitting = true;
    this.errorMessage = null;

    this.uploadService
      .startUpload(this.branchId, file!, dryRun)
      .pipe(finalize(() => (this.isSubmitting = false)))
      .subscribe({
        next: (response) => this.router.navigate(['/uploads', response.sessionId]),
        error: () => (this.errorMessage = 'Could not start the upload. Please check the file and try again.')
      });
  }
}
