# Design

This document explains *why* FlowX.Upload is shaped the way it is. If
you're about to propose a change that seems obviously better, check here
first; there's a reasonable chance the alternative was already considered.

## The PreProcess → Plan rename

<a name="the-preprocess--plan-rename"></a>

The stage that validates parsed data and proposes a set of changes was
originally named `PreProcess` (`IUploadPreProcessor<TParsed>`,
`PreProcessResult`). It is now named **Plan** (`IUploadPlanner<TParsed>`,
`PlanResult`).

**Why:** `PreProcessor` and `Processor` (the unrelated execution stage,
`IUploadProcessor`) were one prefix apart in name while doing entirely
different jobs — one validates and proposes, the other executes. That
similarity was a standing source of confusion for anyone skimming the
codebase or the docs. Naming the stage after its actual output — a Plan —
removes the collision entirely and matches the vocabulary already used
everywhere else in the domain (`UploadSession.Plan`, `PlanItem`,
`PlanItemResult`, `SetPlan`). The four stages are now cleanly:

```
Prepare  →  Parse  →  Plan  →  [user confirms]  →  Process
```

**What else changed as a consequence, and why it's not arbitrary:**

- `UploadSessionStatus.PreProcessing` → `Planning`. The status names the
  session's current *goal* (producing a plan), not a stage name that no
  longer exists.
- `UploadOrchestrator.RunPreProcessingAsync` → `RunPlanningAsync`;
  `IUploadExecutor.EnqueuePreProcessingAsync` → `EnqueuePlanningAsync`;
  `UploadWorkItem.PreProcess` → `UploadWorkItem.Plan` — mechanical
  consequences of the same rename, kept consistent rather than left
  half-renamed.
- `PreProcessResult`'s success factory was `PreProcessResult.Plan(...)`.
  Under the new name that would read as `PlanResult.Plan(...)`, which is
  circular and confusing next to the existing `Plan` *property* on the
  same type. It was renamed to `PlanResult.Success(...)` instead — a
  small, deliberate improvement made *because* the mechanical rename would
  have produced something worse, not a scope-creep addition.

**This is a MAJOR (breaking) version change**, per `CONTRIBUTING.md`'s
versioning policy — every public type/member above changed name.

### Required data migration

`UploadDbContext` maps `UploadSession.Status` via `.HasConversion<string>()`,
which persists the **enum member name as literal text**. Any row currently
stored with `Status = "PreProcessing"` will fail to deserialize the moment
this version is deployed, since the enum member no longer exists under
that name.

**Before deploying this version against an existing database**, run a
migration renaming the stored value:

```sql
UPDATE flowx_upload."UploadSessions" SET "Status" = 'Planning' WHERE "Status" = 'PreProcessing';
```

(Adjust schema/table/column casing to your actual database and migration
tooling.) No other status value's stored text changed — `ValidationFailed`,
`PendingConfirmation`, `Confirmed`, `Processing`, `Completed`, and `Failed`
are all unchanged. This is a one-time, one-row-condition migration; it does
not need to be part of an ongoing compatibility shim.

## Core shape: four fixed stages

```
Prepare  →  Parse  →  Plan  →  [user confirms]  →  Process
```

This is fixed, not configurable — the package does not support arbitrary
pipeline topologies. A generic workflow engine that lets you wire steps in
any order would be a much bigger, much leakier abstraction than a
four-stage contract every use case in this domain actually follows. If a
future use case genuinely doesn't fit this shape, that's a signal to build
something else, not to generalize this package until it fits everything.

**Why Prepare is separate from Parse:** Prepare resolves durable,
pipeline-specific context (potentially needing data the parser itself
depends on) before Parse runs. Both execute in one pass, together, while
the file stream is available — Prepare's output (`TContext`) is persisted
immediately so neither Parse nor a later correction cycle ever needs the
raw file again.

## Layering

```
Domain          — UploadSession aggregate, entities, value objects, enums.
                   Zero framework dependencies.
Application     — Abstractions (ports), Pipelines (facade/registry/defaults),
                   Models (PlanResult), UseCases (UploadOrchestrator).
Infrastructure  — BackgroundServiceUploadExecutor, StaleSessionReconciler.
```

**The package never contains business logic.** `IUploadContextPreparer<T>`,
`IUploadParser<TContext,TParsed>`, `IUploadPlanner<TParsed>`,
`IUploadProcessor`, and `IUploadCorrector<TContext,TParsed>` are implemented
entirely in the consuming application's own layers.

### Why `IUploadStore` sits in `Application/Abstractions`, not `Domain`

A strict DDD reading might place the session's persistence contract as a
Domain-layer Repository interface, next to `UploadSession` itself.
`IUploadStore` doesn't fit that cleanly: its
`GetByStatusOlderThanAsync(status, olderThan, ct)` member exists solely to
serve `StaleSessionReconciler` — an Infrastructure-layer technical concern
(crash recovery), not a domain invariant `UploadSession` itself would ever
need satisfied. A pure domain repository interface should only expose
operations meaningful to loading/saving the aggregate. Mixing in an
operational query for one specific caller is a Clean Architecture
Application-layer port, not a DDD domain repository — so that's where it
lives.

### Why `PlanItem` is a Domain Entity, not a Value Object

`PlanItem.Id` isn't incidental — `UploadSession.Confirm` selects items by
`Id`, and `PlanItemResult.PlanItemId` correlates back to it by identity,
not by structural equality. An object with lifecycle-independent identity
that other objects reference is an Entity in DDD terms, even when declared
as an immutable C# `record`. `RowValidationError`, `RowCorrection`, and
`PlanItemResult` genuinely have no identity — pure structural facts about a
row or an outcome — and stay Value Objects.

## Why `UploadSession` is a real aggregate, not a status enum plus ad-hoc checks

Every guard (only the initiator can confirm; can't process before
confirming; can't fail a completed session) lives as a method on the
aggregate that throws on misuse. See [docs/state-diagram.md](state-diagram.md)
for the full transition map this produces.

## Why the plan supports partial confirmation

Users review a bulk plan and approve what they trust, not all-or-nothing.
`Confirm` takes a selection, not a boolean; item-level results (not just
session-level success/failure) follow directly — a batch where a few items
fail their pre-action is a normal outcome, not an error state.

## Why the executor and the store are separate ports

<a name="persistence"></a>

They vary independently: `IUploadStore` durability depends on your
database; `IUploadExecutor` durability depends on your scheduling
infrastructure (in-process today, a real queue once available).
Conflating them would mean one dictates the other, which isn't true.

## Known limitations

<a name="known-limitations"></a>

**The default executor is single-pod and loses in-flight work on restart —
except after Prepare+Parse succeed once.** `BackgroundServiceUploadExecutor`
queues work items in an in-memory `Channel`. A session whose `ContextJson`
and `ParsedDataJson` are both already set is recoverable
(`StaleSessionReconciler` re-enqueues revalidation). A session that hadn't
reached that point is not — the raw file bytes only ever existed in the
in-memory queue. This disappears entirely once `IUploadExecutor` gets a
durable-queue implementation; nothing else in the package needs to change,
by design (see [Ports](#why-the-executor-and-the-store-are-separate-ports)).

**Dry-run cannot verify external, non-transactional side effects.** Real
database constraints are exercised via a rolled-back transaction; calls to
external systems (e.g. a provider API pre-action) either don't run in
dry-run mode or run for real with no way to undo them.

**No partial resumability within a failed session.** A failed processing
run goes back to `PendingConfirmation` for re-confirmation of the same
plan — there's no "resume from item 47." Deliberate simplicity choice, not
an unintended gap.

## Alternatives considered and rejected

**Keeping `PreProcess` and adding a doc comment to disambiguate from
`Process`.** Considered and rejected — a naming collision that needs a
comment to explain is still a naming collision; renaming the type is a
smaller, more permanent fix than permanently annotating around the problem.

**Per-item resumability instead of whole-session retry.** Rejected for v1;
only pays off if plan sizes grow large enough that redoing a whole batch is
genuinely expensive.

**Keyed DI for the pipeline registry.** Rejected — unavailable on `net6.0`.

**Bundling ClosedXML (or any parser) into the core package.** Rejected —
every consumer would inherit the dependency whether or not they parse Excel.

## Multi-targeting

<a name="multi-targeting"></a>

`net6.0;net8.0;net10.0`, zero conditional compilation in the core package.
This rename touched no API compatibility constraint — it's a pure naming
change, verified against all three TFMs the same as any other change per
`CONTRIBUTING.md`.
