# Inventory Upload Feature Module

Angular v14 feature module implementing the bulk-upload UI described in the
use case: start upload (with dry-run) -> poll status -> fix validation errors
without re-uploading -> review/select the plan -> confirm -> view results
(including per-row failures and a retry path for session-level failures).

## Wiring into the app shell

1. Lazy-load the module from your root routing config:

   ```typescript
   // app-routing.module.ts
   const routes: Routes = [
     { path: '', loadChildren: () => import('./upload/upload.module').then(m => m.UploadModule) }
     // ...your other routes
   ];
   ```

2. Ensure `HttpClientModule` is imported once in `AppModule` (not shown here —
   assumed already present, since other features in your app also call APIs).

3. Confirm the backend's JSON options include a `JsonStringEnumConverter`
   (or equivalent) so `UploadSessionStatus` and `PlanItemKind` serialize as
   strings — `UploadStatusComponent`'s `[ngSwitch]` and `PlanReviewComponent`'s
   badge mapping both assume this. If the backend instead sends numeric
   enums, either add the converter server-side or change these TypeScript
   `enum`s to numeric and adjust the switch/mapping accordingly.

4. Bootstrap 5 SCSS is assumed to already be imported globally in
   `styles.scss` (e.g. `@import "~bootstrap/scss/bootstrap";`). No Bootstrap
   JS bundle is required — nothing here uses dropdowns/modals/etc. that need it.

## Structure

```
upload/
  upload.module.ts
  upload-routing.module.ts
  models/                   — DTOs matching the API contract exactly
  services/
    upload.service.ts        — HTTP calls only
    upload-polling.service.ts — the one place polling behavior lives
  components/
    upload-start/            — file picker + dry-run toggle, POSTs and navigates
    upload-status/           — container: owns session id, refresh cycle, all actions
    validation-errors/       — presentational: editable correction rows
    plan-review/             — presentational: selectable plan rows
    processing-indicator/    — presentational: spinner + label
    upload-results/          — presentational: results table + retry
```

## Known gaps / next steps

- No toast/notification service wired up — `actionError` in
  `UploadStatusComponent` is a plain inline alert. Swap for your app's
  existing notification pattern if one exists.
- No file-type/size client-side validation beyond the `accept=".xlsx"` hint —
  add if your API doesn't already reject oversized/invalid files gracefully.
- `UploadStartComponent` reads `branchId` once via `route.snapshot` — fine
  since this route is never navigated to itself with a changing param, but
  switch to `route.paramMap` (observable) if that assumption ever changes.
