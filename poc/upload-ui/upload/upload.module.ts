import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule } from '@angular/forms';
import { UploadRoutingModule } from './upload-routing.module';

import { UploadStartComponent } from './components/upload-start/upload-start.component';
import { UploadStatusComponent } from './components/upload-status/upload-status.component';
import { ValidationErrorsComponent } from './components/validation-errors/validation-errors.component';
import { PlanReviewComponent } from './components/plan-review/plan-review.component';
import { ProcessingIndicatorComponent } from './components/processing-indicator/processing-indicator.component';
import { UploadResultsComponent } from './components/upload-results/upload-results.component';

@NgModule({
  declarations: [
    UploadStartComponent,
    UploadStatusComponent,
    ValidationErrorsComponent,
    PlanReviewComponent,
    ProcessingIndicatorComponent,
    UploadResultsComponent
  ],
  imports: [CommonModule, ReactiveFormsModule, UploadRoutingModule]
})
export class UploadModule {}
