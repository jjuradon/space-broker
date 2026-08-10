import { ChangeDetectionStrategy, Component, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { BehaviorSubject, EMPTY, Observable } from 'rxjs';
import { catchError, switchMap, tap } from 'rxjs/operators';
import { UploadService } from '../../services/upload.service';
import { UploadPollingService } from '../../services/upload-polling.service';
import { UploadSessionDto, UploadSessionStatus } from '../../models/upload-session.model';
import { RowCorrection } from '../../models/row-correction.model';

/**
 * Container ("smart") component. Owns the session id, the polling/refresh
 * cycle, and every HTTP call in this feature. All sub-components below are
 * purely presentational and only emit events.
 */
@Component({
  selector: 'app-upload-status',
  templateUrl: './upload-status.component.html',
  styleUrls: ['./upload-status.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class UploadStatusComponent implements OnInit {
  readonly UploadSessionStatus = UploadSessionStatus; // exposed for the template's ngSwitch

  session$!: Observable<UploadSessionDto>;
  actionError: string | null = null;

  private readonly sessionId = this.route.snapshot.paramMap.get('sessionId')!;
  private readonly refresh$ = new BehaviorSubject<void>(undefined);

  constructor(
    private readonly route: ActivatedRoute,
    private readonly uploadService: UploadService,
    private readonly polling: UploadPollingService
  ) {}

  ngOnInit(): void {
    this.session$ = this.refresh$.pipe(switchMap(() => this.polling.pollUntilActionable(this.sessionId)));
  }

  onCorrectionsSubmitted(corrections: RowCorrection[]): void {
    this.runAction(this.uploadService.submitCorrections(this.sessionId, corrections));
  }

  onConfirmed(selectedItemIds: string[]): void {
    this.runAction(this.uploadService.confirm(this.sessionId, selectedItemIds));
  }

  onRetry(): void {
    this.runAction(this.uploadService.retry(this.sessionId));
  }

  private runAction(action$: Observable<void>): void {
    this.actionError = null;
    action$
      .pipe(
        tap(() => this.refresh$.next()),
        catchError(() => {
          this.actionError = 'That action could not be completed. Please try again.';
          return EMPTY;
        })
      )
      .subscribe();
  }
}
