# Sequence Diagrams

Same three flows as before; method/status names updated for the Plan rename.

## 1. Happy path — start to completion

```mermaid
sequenceDiagram
    actor User
    participant API as Inventory API
    participant Orch as UploadOrchestrator
    participant Store as IUploadStore
    participant Exec as BackgroundServiceUploadExecutor
    participant Pipe as IUploadPipeline
    participant DB as Domain DB

    User->>API: POST /uploads (file, dryRun)
    API->>Orch: StartAsync(...)
    Orch->>Store: SaveAsync(session: Planning)
    Orch->>Exec: EnqueuePlanningAsync
    Orch-->>API: sessionId
    API-->>User: 202 Accepted { sessionId }

    Exec->>Orch: RunPlanningAsync
    Orch->>Pipe: PrepareAsync(fileStream, ownerContext, initiatedBy)
    Pipe-->>Orch: context
    Orch->>Store: SaveAsync(ContextJson)
    Orch->>Pipe: ParseAsync(fileStream, context)
    Pipe-->>Orch: parsed data
    Orch->>Store: SaveAsync(ParsedDataJson)
    Orch->>Pipe: PlanAsync(parsed)
    Pipe-->>Orch: PlanResult.Success(items)
    Orch->>Store: SaveAsync(session: PendingConfirmation)

    loop Poll every ~2s
        User->>API: GET /uploads/{id}
        API-->>User: status + plan
    end

    User->>API: POST /uploads/{id}/confirm (selectedItemIds)
    API->>Orch: ConfirmAsync
    Orch->>Store: SaveAsync(session: Confirmed)
    Orch->>Exec: EnqueueProcessingAsync
    API-->>User: 202 Accepted

    Exec->>Orch: RunProcessingAsync
    Orch->>Store: SaveAsync(session: Processing)
    Orch->>Pipe: ProcessAsync(plan, isDryRun)
    Pipe->>DB: Per-chunk transaction — commit (or rollback if isDryRun)
    Pipe-->>Orch: PlanItemResult[]
    Orch->>Store: SaveAsync(session: Completed, results)

    User->>API: GET /uploads/{id}
    API-->>User: status=Completed, results
```

**Dry-run note:** identical flow; only the final DB step's commit/rollback
changes based on `session.IsDryRun`.

## 2. Correction loop — fix without re-upload

```mermaid
sequenceDiagram
    actor User
    participant API as Inventory API
    participant Orch as UploadOrchestrator
    participant Store as IUploadStore
    participant Exec as BackgroundServiceUploadExecutor
    participant Pipe as IUploadPipeline

    Note over Orch,Pipe: Continues from Planning — PlanAsync returns a validation failure this time

    Orch->>Pipe: PlanAsync(parsed)
    Pipe-->>Orch: PlanResult.ValidationFailed(errors)
    Orch->>Store: SaveAsync(session: ValidationFailed, errors)

    User->>API: GET /uploads/{id}
    API-->>User: status=ValidationFailed, errors (sheet/row/field/message)

    User->>API: POST /uploads/{id}/corrections
    API->>Orch: SubmitCorrectionsAsync(corrections)
    Orch->>Store: SaveAsync(session: Planning, PendingCorrections)
    Orch->>Exec: EnqueueRevalidationAsync
    API-->>User: 202 Accepted

    Exec->>Orch: RunRevalidationAsync
    Orch->>Store: GetAsync(session)
    Orch->>Pipe: DeserializeContext(ContextJson)
    Orch->>Pipe: DeserializeParsedData(ParsedDataJson)
    Orch->>Pipe: ApplyCorrections(parsed, context, corrections)
    Pipe-->>Orch: corrected parsed data
    Orch->>Store: SaveAsync(ParsedDataJson = corrected)
    Orch->>Pipe: PlanAsync(corrected)
    Pipe-->>Orch: PlanResult.Success(items)
    Orch->>Store: SaveAsync(session: PendingConfirmation)

    Note over User,Store: No file re-upload required — the original file's bytes<br/>are never needed again after Prepare+Parse complete once
```

## 3. Crash recovery — pod restart during an in-flight session

```mermaid
sequenceDiagram
    participant Host as .NET Host (pod)
    participant Reconciler as StaleSessionReconciler
    participant Store as IUploadStore
    participant Exec as IUploadExecutor

    Note over Host: Pod restarts (crash, redeploy)
    Host->>Reconciler: StartAsync (on host startup)

    Reconciler->>Store: GetByStatusOlderThanAsync(Processing, 10m)
    Store-->>Reconciler: [stale sessions]
    loop each stale Processing session
        Reconciler->>Store: session.MarkFailed(...); SaveAsync
    end

    Reconciler->>Store: GetByStatusOlderThanAsync(Confirmed, 10m)
    Store-->>Reconciler: [stale sessions]
    loop each stale Confirmed session
        Reconciler->>Exec: EnqueueProcessingAsync(sessionId)
    end

    Reconciler->>Store: GetByStatusOlderThanAsync(Planning, 10m)
    Store-->>Reconciler: [stale sessions]
    loop each stale Planning session
        alt ContextJson and ParsedDataJson are both set
            Reconciler->>Exec: EnqueueRevalidationAsync(sessionId)
            Note right of Reconciler: Prepare+Parse already succeeded —<br/>only the Plan step was interrupted
        else either is null
            Reconciler->>Store: session.MarkFailed("please re-upload"); SaveAsync
            Note right of Reconciler: Known gap — raw file bytes only ever<br/>lived in the in-memory queue, unrecoverable
        end
    end
```

See [docs/design.md](design.md#known-limitations) for why the `else`
branch exists and what would remove it.
