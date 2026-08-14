# FlowX.Upload

A small orchestration framework for building bulk file-upload workflows —
**parse → pre-process (validate + build a plan) → user confirmation → process**
— without re-implementing that lifecycle for every new upload use case in your
application.

Think of it as a `DbContext` for uploads: you define the business logic
(how to parse a file, how to validate it, how to turn it into a plan of
changes, how to execute that plan), and the package owns the session
lifecycle, persistence contract, background scheduling, and crash recovery
around it.

> Originally designed for a store-management application's bulk inventory
> upload (Excel → create/update/delete branch product offerings), but the
> pipeline contract is domain-agnostic.

## Why

Every new "upload a file to bulk create/update/delete records" feature tends
to reinvent the same shape: accept a file async, validate it, show the user
a plan of what will change, let them approve some or all of it, then execute
— with background processing, status polling, and error reporting bolted on
each time. FlowX.Upload extracts that shape into a small, well-tested core
so each new use case is three or four small classes plus one registration
call.

## Install

```bash
dotnet add package FlowX.Upload
# Optional reference persistence adapter:
dotnet add package FlowX.Upload.EntityFrameworkCore
```

Targets `net6.0`, `net8.0`, and `net10.0`. No `#if` branches in the core
package — see [docs/design.md](docs/design.md#multi-targeting) for why.

## Quick start

```csharp
// 1. Implement the three required stages (+ an optional corrector) for your use case.
public sealed class MyParser : IUploadParser<MyParsedShape> { /* ... */ }
public sealed class MyPreProcessor : IUploadPreProcessor<MyParsedShape> { /* ... */ }
public sealed class MyProcessor : IUploadProcessor { /* ... */ }

// 2. Register the package and your pipeline.
services
    .AddFlowXUpload()
    .AddPipeline<MyParsedShape>("my-domain.my-upload", pipeline => pipeline
        .UseParser<MyParser>()
        .UsePreProcessor<MyPreProcessor>()
        .UseProcessor<MyProcessor>());

// You also need an IUploadStore. Either use the EF Core reference
// implementation, or implement the port against your own persistence.
services.AddEfCoreUploadStore(services, opts => opts.UseSqlServer(connectionString));

// 3. Drive the lifecycle from your API through UploadOrchestrator.
public sealed class MyUploadsController : ControllerBase
{
    public MyUploadsController(UploadOrchestrator orchestrator) { /* ... */ }
    // POST start -> orchestrator.StartAsync(...)
    // GET status -> read from IUploadStore
    // POST confirm -> orchestrator.ConfirmAsync(...)
    // POST corrections -> orchestrator.SubmitCorrectionsAsync(...)
    // POST retry -> orchestrator.RetryAsync(...)
}
```

See [docs/usage-guide.md](docs/usage-guide.md) for the full walkthrough,
including the optional correction flow and dry-run support.

## Features

- **Fixed three-stage pipeline contract** (Parse / PreProcess / Process) —
  application code owns all business logic; the package owns orchestration.
- **Partial confirmation** — users select which proposed changes to actually
  execute, not all-or-nothing.
- **Item-level results** — a failed pre-action on one row doesn't block the
  rest of a batch; failures are reported per item.
- **In-place corrections** — fix invalid rows from the UI and re-validate
  without re-uploading the file.
- **Dry-run mode** — execute the real `Process` stage inside a transaction
  that's always rolled back, so real DB constraints are exercised without
  persisting anything.
- **Crash recovery** — a startup reconciler recovers or safely fails
  sessions interrupted by a pod restart, without needing a message queue.
- **Storage-agnostic** — `IUploadStore` is a port; an EF Core reference
  implementation ships separately so the core has zero EF Core dependency.

## Documentation

| Doc | Audience |
|---|---|
| [docs/usage-guide.md](docs/usage-guide.md) | Implementing a new pipeline |
| [docs/design.md](docs/design.md) | Why the package is shaped this way, and its known limitations |
| [docs/architecture.md](docs/architecture.md) | C4 diagrams (Context / Container / Component) |
| [docs/sequence-diagrams.md](docs/sequence-diagrams.md) | Runtime flows: happy path, correction loop, crash recovery |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Contributing to the package itself |

## Known limitations

The default `BackgroundServiceUploadExecutor` is single-pod and in-memory —
raw file bytes queued for the very first parse step do not survive a pod
restart (everything after the first successful parse does). See
[docs/design.md](docs/design.md#known-limitations) for the full explanation
and the upgrade path once a durable queue is available.

## License

[Specify your license here — this should be verified against your
organization's actual licensing decision before publishing.]
