import { Injectable } from '@angular/core';
import { Observable, timer } from 'rxjs';
import { distinctUntilChanged, switchMap, takeWhile } from 'rxjs/operators';
import { UploadService } from './upload.service';
import { UploadSessionDto, UploadSessionStatus } from '../models/upload-session.model';

const BUSY_STATUSES: ReadonlySet<UploadSessionStatus> = new Set([
  UploadSessionStatus.PreProcessing,
  UploadSessionStatus.Confirmed,
  UploadSessionStatus.Processing
]);

/**
 * Polls session status while the backend is doing work (PreProcessing,
 * Confirmed — enqueued but not yet picked up, or Processing), and stops as
 * soon as the session reaches a status that needs user action or is terminal
 * (ValidationFailed, PendingConfirmation, Completed, Failed).
 *
 * Callers restart the poll (via a refresh trigger) after submitting
 * corrections, confirming, or retrying — see UploadStatusComponent.
 */
@Injectable({ providedIn: 'root' })
export class UploadPollingService {
  private readonly pollIntervalMs = 2000;

  constructor(private readonly uploadService: UploadService) {}

  pollUntilActionable(sessionId: string): Observable<UploadSessionDto> {
    return timer(0, this.pollIntervalMs).pipe(
      switchMap(() => this.uploadService.getStatus(sessionId)),
      distinctUntilChanged(
        (prev, curr) =>
          prev.status === curr.status &&
          prev.validationErrors.length === curr.validationErrors.length &&
          prev.results.length === curr.results.length
      ),
      takeWhile((session) => BUSY_STATUSES.has(session.status), true) // inclusive: emits the final actionable/terminal state too
    );
  }
}
