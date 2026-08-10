import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { UploadStartComponent } from './components/upload-start/upload-start.component';
import { UploadStatusComponent } from './components/upload-status/upload-status.component';

const routes: Routes = [
  { path: 'branches/:branchId/inventory/uploads', component: UploadStartComponent },
  { path: 'uploads/:sessionId', component: UploadStatusComponent }
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class UploadRoutingModule {}
