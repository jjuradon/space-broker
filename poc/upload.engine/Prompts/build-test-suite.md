# Prompt: Build the Test Suite for FlowX.Upload

You are acting as a senior .NET test engineer. Your task is to design and
implement a full unit and integration test suite for the `FlowX.Upload`
package (core library + `FlowX.Upload.EntityFrameworkCore` reference
adapter). Prioritize the test cases below over exhaustive coverage of
trivial code — this package's risk is concentrated in a small number of
places, and the suite should reflect that concentration, not spread evenly
across every file.

## Context you need

`FlowX.Upload` orchestrates a fixed four-stage upload lifecycle:
**Prepare → Parse → PreProcess (validate + build plan) → [user confirms a
subset] → Process**, plus an optional correction loop that revalidates from
durably-persisted state without ever re-touching the original file. It
targets `net6.0;net8.0;net10.0` with zero conditional compilation. Source
follows Clean Architecture + DDD folder conventions:

```
FlowX.Upload/
  Domain/
    Aggregates/UploadSession.cs
    Entities/PlanItem.cs
    ValueObjects/{RowValidationError, RowCorrection, PlanItemResult}.cs
    Enums/{UploadSessionStatus, PlanItemKind}.cs
  Application/
    Abstractions/
      Persistence/IUploadStore.cs
      Scheduling/IUploadExecutor.cs
      Pipelines/{IUploadContextPreparer, IUploadParser, IUploadPreProcessor, IUploadCorrector, IUploadProcessor}.cs
    Pipelines/
      Defaults/{EmptyUploadContext, EmptyContextPreparer}.cs
      {IUploadPipeline, UploadPipeline, IUploadPipelineRegistry, UploadPipelineRegistry}.cs
    Models/PreProcessResult.cs
    UseCases/UploadOrchestrator.cs
  Infrastructure/Scheduling/{BackgroundServiceUploadExecutor, StaleSessionReconciler}.cs
  DependencyInjection/{UploadServiceCollectionExtensions, UploadBuilder, PipelineBuilder}.cs

FlowX.Upload.EntityFrameworkCore/
  {UploadDbContext, EfUploadStore}.cs
```

Mirror this structure in the test project(s) — a reviewer should be able to
find the test for any source file by following the same path.

## Test projects to create

```
tests/
  FlowX.Upload.Tests/                          — unit tests, no I/O, no real DB
  FlowX.Upload.EntityFrameworkCore.Tests/       — integration tests against a real database
  FlowX.Upload.IntegrationTests/                — end-to-end tests wiring real orchestrator +
                                                    real EF Core store + an in-process fake pipeline
```

**Stack:** xUnit, FluentAssertions, and hand-written fakes for `IUploadStore`
/ `IUploadExecutor` in orchestrator tests rather than a mocking framework —
these interfaces are small and stateful enough that a fake is clearer and
less brittle than a mock with dozens of `.Setup()` calls. NSubstitute or Moq
is acceptable for simpler, stateless collaborators (e.g. verifying a call
happened once). Use whichever the existing repo already has a convention
for if one exists — check before introducing a second mocking library.

**Naming convention:** `MethodName_Scenario_ExpectedOutcome`, e.g.
`Confirm_WhenPrincipalDidNotInitiateSession_Throws`. Apply consistently.

**Multi-targeting:** the unit test project must build and pass against all
three TFMs (`net6.0`, `net8.0`, `net10.0`), per the package's own
`CONTRIBUTING.md`. Integration test projects may target a single modern TFM
if the real dependency (EF Core version, test host) doesn't support all
three — note explicitly wherever you make that call and why.

## Priority 1 — `UploadSession` (Domain aggregate)

This is the single highest-value test target: every consumer's correctness
depends on this state machine rejecting invalid transitions. Cover **every**
transition and **every** guard, not just happy paths:

- `Start` sets initial status `PreProcessing`, and rejects a null/whitespace
  `pipelineKey`.
- `SetContext` / `SetParsedData` only succeed from `PreProcessing`; throw
  otherwise.
- `FailValidation` only from `PreProcessing`; sets `ValidationErrors` and
  status `ValidationFailed`.
- `SubmitCorrections`: throws if principal ≠ `InitiatedBy`; throws if either
  `ContextJson` or `ParsedDataJson` is null; succeeds with an **empty**
  corrections collection (re-validate without changes is valid); resets
  `ValidationErrors` and returns to `PreProcessing`.
- `SetPlan`: throws on an empty plan; only succeeds from `PreProcessing`;
  moves to `PendingConfirmation`.
- `Confirm`: throws if principal ≠ `InitiatedBy`; throws on empty selection;
  **correctly narrows `Plan` to only the selected `Id`s** — this is a prime
  off-by-one/wrong-filter risk, test with a plan of 5 items selecting a
  non-contiguous subset (e.g. items 1, 3, 5) and assert exactly those remain,
  in no particular re-ordering assumption unless the implementation
  guarantees one.
- `MarkProcessing`: only from `Confirmed`.
- `MarkCompleted`: only from `Processing`; carries partial results (assert a
  results collection containing both `Succeeded=true` and `Succeeded=false`
  entries is accepted without complaint — partial success is a valid
  `Completed` outcome, not something the aggregate should reject).
- `MarkFailed`: throws if called on an already-`Completed` session; succeeds
  from every other status (parametrize this as a theory over all
  non-`Completed` statuses).
- `PrepareForRetry`: only from `Failed`; clears `FailureReason`; returns to
  `PendingConfirmation` with the plan intact (assert `Plan` is unchanged
  from before the failure, not reset).
- `ResetToConfirmed` (internal, reconciler-only): only from `Processing`.
- `Touch()`/`LastActivityAt`: assert it advances on every mutating call —
  this underpins the reconciler's staleness queries, so a regression here
  is silent until a production incident.

## Priority 2 — `UploadOrchestrator` (Application use case)

Test against **hand-written fakes** for `IUploadStore`, `IUploadExecutor`,
and `IUploadPipelineRegistry`/`IUploadPipeline` — this isolates the
orchestrator's coordination logic from any real pipeline or persistence
behavior, which is the point.

- `StartAsync`: creates and saves a new session, enqueues
  `EnqueuePreProcessingAsync`, returns the new session's `Id`.
- `RunPreProcessingAsync` happy path: calls `Prepare` then `Parse` in order
  (assert ordering, e.g. via a fake pipeline that records call sequence),
  persists `ContextJson` before `ParsedDataJson`, ends in
  `PendingConfirmation` with the produced plan.
- `RunPreProcessingAsync` when the fake pipeline's `PreProcessAsync` returns
  a validation failure: session ends `ValidationFailed` with the errors set,
  no plan.
- `RunPreProcessingAsync` when `Prepare`, `Parse`, or `PreProcess` throws:
  session ends `Failed` with a message referencing the failure — test all
  three throw points separately, since they're different code paths.
- `SubmitCorrectionsAsync`: delegates to `session.SubmitCorrections` (so the
  aggregate's own guard tests above cover the validation — don't duplicate
  every aggregate guard here, just confirm the orchestrator wires it and
  enqueues `EnqueueRevalidationAsync` on success).
- `RunRevalidationAsync` with pending corrections: deserializes context +
  parsed data, calls `ApplyCorrections`, re-serializes the corrected data,
  then re-runs `PreProcessAsync` on the corrected data (not the original) —
  assert the fake pipeline receives the *corrected* object, not the
  pre-correction one.
- `RunRevalidationAsync` with **no** pending corrections (empty collection):
  skips `ApplyCorrections` entirely and re-validates the existing parsed
  data as-is.
- `ConfirmAsync`: delegates to `session.Confirm`, enqueues
  `EnqueueProcessingAsync` only on success.
- `RunProcessingAsync` happy path: marks `Processing` and saves *before*
  calling `ProcessAsync` (assert save-then-process ordering via the fake
  store's call log — this ordering matters for the reconciler's stale-scan
  to find genuinely-in-progress sessions), passes `session.IsDryRun`
  through to the pipeline call, ends `Completed` with results.
- `RunProcessingAsync` when the pipeline throws: session ends `Failed`.
- `RetryAsync`: delegates to `session.PrepareForRetry` (aggregate guard
  tests cover the "only from Failed" behavior).
- Every method: throws a clear exception when `store.GetAsync` returns
  `null` (session not found) — test this once per public orchestrator
  method, since it's a copy-pasted guard across all of them and a likely
  place for one method to silently drift from the others during a future edit.

## Priority 3 — Pipeline machinery

- **`UploadPipeline<TContext, TParsed>`**: each facade method
  (`PrepareAsync`, `ParseAsync`, `PreProcessAsync`, `ProcessAsync`)
  delegates to the correct injected component with correct argument
  passing (especially `ParseAsync` receiving both the stream *and* the
  context produced by `Prepare` — this is the exact behavior the last
  design change introduced, and the boxing/unboxing through `object` in the
  facade is the highest-risk spot for a silent cast failure). `Serialize`/
  `Deserialize` round-trip for both context and parsed data. `ApplyCorrections`
  throws `NotSupportedException` with a clear message when no corrector was
  registered, and delegates correctly when one was.
- **`UploadPipelineRegistry`**: resolves the correct pipeline by key when
  multiple are registered (test with ≥2 registered pipelines to catch a
  "always returns the first" bug); throws with a message that includes the
  requested key when not found.
- **`PipelineBuilder<TContext, TParsed>.Complete()`**: throws when any of
  preparer/parser/pre-processor/processor is missing (parametrize over
  each missing piece individually); succeeds when the optional corrector is
  omitted; succeeds when everything is present.

## Priority 4 — Infrastructure

- **`BackgroundServiceUploadExecutor`**: enqueueing each work item type
  (`PreProcess`, `Revalidate`, `Process`) results in the orchestrator's
  corresponding `Run*Async` method being invoked with the right arguments
  (use a fake/spy `UploadOrchestrator` substitute or a real orchestrator
  wired to fakes). **An unhandled exception processing one item does not
  stop the executor from processing the next enqueued item** — this is a
  specific, easy-to-regress behavior (a naive rewrite of the processing loop
  could let one bad item kill the whole background loop) and deserves an
  explicit test: enqueue an item that throws, then a normal item, assert
  the second still completes.
- **`StaleSessionReconciler`**: the four branches, each as its own test,
  using a fake store seeded with sessions at specific `Status`/
  `LastActivityAt` combinations (no real time delay needed — seed the
  timestamp directly):
  1. Stale `Processing` → `MarkFailed` called, saved.
  2. Stale `Confirmed` → `EnqueueProcessingAsync` called, session otherwise
     untouched (assert `MarkFailed` was *not* called on it).
  3. Stale `PreProcessing` **with** `ContextJson`/`ParsedDataJson` set →
     `EnqueueRevalidationAsync` called.
  4. Stale `PreProcessing` **without** them → `MarkFailed` with a
     re-upload-oriented message.
  5. A **non-stale** session in any of the above statuses is left
     completely untouched — assert no store/executor calls reference it.
     This is the case most likely to be skipped and is exactly what
     guards against a threshold-comparison bug (`<` vs `<=`, wrong sign).

## Priority 5 — `EfUploadStore` (integration, real database)

Use a real database (Testcontainers with the actual target RDBMS is
preferred over EF Core's InMemory provider, since InMemory doesn't enforce
unique constraints or real transaction semantics — and this store's whole
value proposition is durability under a real database). If Testcontainers
isn't feasible in this environment, use a local SQL Server/PostgreSQL
instance and document the requirement clearly in the test project's README.

- `SaveAsync` on a new session inserts a row; `SaveAsync` on an
  already-persisted session's `Id` **updates** the existing row (upsert
  behavior) rather than throwing a duplicate-key error or inserting a
  second row — test both paths explicitly.
- `GetAsync` returns `null` for an unknown `Id` (not an exception).
- **JSON column round-trip**: save a session with non-trivial `Plan`,
  `ValidationErrors`, `Results`, and `PendingCorrections` collections
  (multiple items, not empty/single-item — catches serialization bugs that
  only appear with real collection shapes), reload it via `GetAsync`, and
  assert deep equality against the original collections.
- `GetByStatusOlderThanAsync`: seed sessions with a mix of statuses and
  `LastActivityAt` timestamps straddling the threshold; assert only
  matching sessions (right status AND older than the threshold) are
  returned — this is the query the reconciler depends on entirely, so an
  off-by-one on the boundary condition here has the same production impact
  as a bug in the reconciler itself.

## Priority 6 — End-to-end integration

One or two tests that wire a **real** `UploadOrchestrator` + real
`EfUploadStore` (against the real database) + a minimal in-memory test
pipeline (simple string-based `TContext`/`TParsed` types, no real Excel
parsing needed) + a synchronous fake `IUploadExecutor` that invokes the
orchestrator inline instead of via a background loop (removes timing
flakiness from the test while still exercising the real orchestration and
persistence code together):

1. **Full happy path**: Start → Prepare → Parse → PreProcess → Confirm
   (partial selection) → Process → assert final `Completed` state read back
   from the real store matches expectations, including that unselected plan
   items are absent from the final `Plan`.
2. **Correction loop without file access**: drive a session to
   `ValidationFailed`, then call `SubmitCorrectionsAsync` and
   `RunRevalidationAsync` **without providing any file bytes to this second
   part of the test** — this is the assertion that actually proves the
   "no re-upload needed" design guarantee holds in a real, persisted round-trip,
   not just in the unit-tested orchestrator logic.
3. *(Optional, if a processor with a genuine unique-constraint dependency is
   available)*: dry-run mode still surfaces a real constraint violation from
   the database (transaction runs, constraint fires, error is visible in the
   result) while leaving zero rows persisted afterward — query the DB
   directly post-test to confirm.

## Explicit non-goals

- Do not write tests for `BackgroundServiceUploadExecutor`'s underlying
  `Channel<T>`/`BackgroundService` framework behavior itself — that's
  .NET's own tested code, not this package's.
- Do not test the inventory bulk-upload example pipeline's business logic
  (parser/pre-processor/processor for that specific use case) as part of
  this suite — that belongs to the consuming application's own test suite,
  not the package's.
- Do not chase 100% line coverage on `DependencyInjection/` registration
  glue beyond `PipelineBuilder.Complete()`'s guard clauses — straightforward
  `services.AddScoped<T>()` calls don't need dedicated tests.

## Deliverables

1. The three test projects above, each building and passing independently.
2. For the unit test project, confirmation (e.g. a short note or CI matrix
   snippet) that it was run against all three target frameworks.
3. A short summary at the end: test count per project, and — if any
   priority-1/2 case above turned out to be **untestable as currently
   written** (e.g. a missing seam, a hardcoded dependency) — flag it
   explicitly rather than silently skipping it or weakening the assertion.
   A gap in testability is itself a finding worth reporting, not something
   to route around quietly.