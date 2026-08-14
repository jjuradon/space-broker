# Design

This document explains *why* FlowX.Upload is shaped the way it is — the
decisions, the trade-offs, and what was deliberately rejected. If you're
about to propose a change that seems obviously better, check here first;
there's a reasonable chance the alternative was already considered.

## Core shape: three fixed stages

```
Parse  →  PreProcess (validate + build plan)  →  [user confirms]  →  Process
```

This is fixed, not configurable — the package does not support arbitrary
pipeline topologies. That's a deliberate scope limit: a generic workflow
engine that lets you wire steps in any order is a much bigger, much leakier
abstraction than a three-stage contract every use case in this domain
actually follows. If a future use case genuinely doesn't fit this shape,
that's a signal to build something else, not to generalize this package
until it fits everything.

## Layering

<a name="layering"></a>

```
Domain          — UploadSession aggregate. Zero framework dependencies.
Application     — ports (IUploadParser<T>, IUploadStore, IUploadExecutor, ...),
                   UploadOrchestrator, pipeline registry/facade.
Infrastructure  — BackgroundServiceUploadExecutor, StaleSessionReconciler.
```

**The package never contains business logic.** `IUploadParser<T>`,
`IUploadPreProcessor<T>`, `IUploadProcessor`, and `IUploadCorrector<T>` are
implemented entirely in the consuming application's own
application/infrastructure layers. This is the one rule the package cannot
compromise on without becoming a different kind of product — the moment it
starts making domain assumptions, it stops being reusable across use cases.

## Why `UploadSession` is a real aggregate, not a status enum

Early designs considered tracking upload state as a status column plus
ad-hoc infrastructure code checking/setting it. That was rejected: every
guard (only the initiator can confirm; can't process before confirming;
can't fail a completed session) would then live wherever the infrastructure
code happened to touch it, enforced inconsistently. `UploadSession` makes
every transition a method that throws on misuse — the state machine cannot
be driven into an invalid state by any caller, including future
maintainers who haven't read this document.

## Why the plan supports partial confirmation

The obvious v1 design is all-or-nothing confirmation. It doesn't match how
users actually review a bulk plan (some rows might be wrong; users approve
what they trust and leave the rest for the next upload). `Confirm` takes a
selection, not a boolean, and item-level results (not just session-level
success/failure) follow directly from that — a batch where 3 of 400 items
fail their pre-action is a normal outcome, not an error state.

## Why the executor and the store are separate ports

<a name="persistence"></a>

It's tempting to fold "how work gets scheduled" and "where session state
lives" into one abstraction — they're both about durability, sort of. They
were kept separate because they vary independently:

- `IUploadStore` durability depends on your database.
- `IUploadExecutor` durability depends on your scheduling infrastructure
  (in-process background service today; a real queue once available).

Conflating them would mean the store implementation dictates executor
choice or vice versa, which isn't true — you can have a durable store with
a non-durable executor (this package's current default) or the reverse.

## Known limitations

<a name="known-limitations"></a>

**The default executor is single-pod and loses in-flight work on restart —
except after the first successful parse.** `BackgroundServiceUploadExecutor`
queues work items in an in-memory `Channel`. If the pod restarts:

- A session whose file was already parsed (`ParsedDataJson` is set) is
  recoverable — `StaleSessionReconciler` re-enqueues it.
- A session whose file was *not yet* parsed is not recoverable — the raw
  file bytes only ever existed in the in-memory queue. The reconciler fails
  these sessions with a message asking the user to re-upload.

This is an accepted trade-off under a real constraint (no durable queue
infrastructure currently available to the consuming team — see the
project's upload proposal history for context), not an oversight. It
disappears entirely once `IUploadExecutor` gets a durable-queue
implementation; nothing else in the package needs to change, by design
(see [Ports](#why-the-executor-and-the-store-are-separate-ports) above).

**Dry-run cannot verify external, non-transactional side effects.** The
`isDryRun` flag lets `IUploadProcessor` run inside a transaction that's
always rolled back, which genuinely exercises database constraints. It
cannot do the same for calls to external systems (e.g. a provider API
pre-action) — those either don't run in dry-run mode, or run for real with
no way to undo them. Pipeline authors must document this gap per pipeline;
the package cannot close it generically.

**No partial resumability within a failed session.** If processing fails
partway through, the whole confirmed plan goes back to `PendingConfirmation`
for re-confirmation — there's no "resume from item 47." This was a
deliberate simplicity choice (see alternatives below), not a limitation the
package is trying and failing to work around.

## Alternatives considered and rejected

**Per-item resumability instead of whole-session retry.** Would require
tracking which individual items succeeded before a crash and replaying only
the remainder — meaningfully more complex, and only pays off if plans are
large enough that redoing the whole batch is expensive. Rejected for v1;
revisit if plan sizes grow to where this becomes a real cost, not a
theoretical one.

**Keyed DI for the pipeline registry.** .NET 8 introduced keyed service
registration, which would be a more idiomatic way to resolve "one pipeline
per string key." Rejected because it's unavailable on `net6.0`, one of the
three supported target frameworks — the hand-rolled
`IUploadPipelineRegistry` (a linear scan over `IEnumerable<IUploadPipeline>`)
works identically across all three TFMs with no conditional compilation.
Revisit if `net6.0` support is ever dropped.

**Bundling ClosedXML (or any parser) into the core package.** Rejected —
every consumer of the core package would inherit that dependency whether or
not they parse Excel. Parsing-technology-specific helpers belong in a
separate optional package (e.g. `FlowX.Upload.Excel`), following the same
reasoning that put EF Core in its own `FlowX.Upload.EntityFrameworkCore`
package rather than core.

## Multi-targeting

<a name="multi-targeting"></a>

`net6.0;net8.0;net10.0`, with zero conditional compilation in the core
package. Every design decision above that touches API compatibility (the
pipeline registry being the clearest example) was made specifically to
preserve this. If a future change seems to require an `#if` branch, treat
that as a signal to look for a shared implementation first — see
[CONTRIBUTING.md](../CONTRIBUTING.md) before adding one.
