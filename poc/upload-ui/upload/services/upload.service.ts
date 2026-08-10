import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { UploadSessionDto } from '../models/upload-session.model';
import { RowCorrection } from '../models/row-correction.model';

export interface StartUploadResponse {
  sessionId: string;
  dryRun: boolean;
}

/**
 * Thin HTTP client for the FlowX.Upload-backed API. No orchestration logic
 * lives here — that belongs to UploadPollingService (status polling) and to
 * the components (deciding when to call which action).
 */
@Injectable({ providedIn: 'root' })
export class UploadService {
  private readonly baseUrl = '/api';

  constructor(private readonly http: HttpClient) {}

  startUpload(branchId: string, file: File, dryRun: boolean): Observable<StartUploadResponse> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    const params = new HttpParams().set('dryRun', String(dryRun));

    return this.http.post<StartUploadResponse>(
      `${this.baseUrl}/branches/${branchId}/inventory/uploads`,
      formData,
      { params }
    );
  }

  getStatus(sessionId: string): Observable<UploadSessionDto> {
    return this.http.get<UploadSessionDto>(`${this.baseUrl}/uploads/${sessionId}`);
  }

  submitCorrections(sessionId: string, corrections: RowCorrection[]): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/uploads/${sessionId}/corrections`, { corrections });
  }

  confirm(sessionId: string, selectedItemIds: string[]): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/uploads/${sessionId}/confirm`, { selectedItemIds });
  }

  retry(sessionId: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/uploads/${sessionId}/retry`, {});
  }
}
