# Contributing to FlowX.Upload

Thanks for helping improve this package. It's used across multiple services
as a shared dependency, so changes here have a wider blast radius than a
typical application change — please read this before opening a PR.

## Development setup

```bash
git clone <repo-url>
cd flowx-upload
dotnet restore
dotnet build
dotnet test
```

Multi-targets `net6.0`, `net8.0`, and `net10.0`. Run the full test suite
against **all three** before opening a PR:

```bash
dotnet test -f net6.0
dotnet test -f net8.0
dotnet test -f net10.0
```

## Project layout (Clean Architecture + DDD)

```
src/
  FlowX.Upload/
    Domain/
      Aggregates/       — UploadSession
      Entities/         — PlanItem (has identity — see docs/design.md)
      ValueObjects/      — RowValidationError, RowCorrection, PlanItemResult
      Enums/             — UploadSessionStatus, PlanItemKind
    Application/
      Abstractions/
        Persistence/     — IUploadStore
        Scheduling/       — IUploadExecutor
        Pipelines/        — IUploadContextPreparer, IUploadParser, IUploadPlanner,
                             IUploadCorrector, IUploadProcessor
      Pipelines/          — IUploadPipeline, UploadPipeline, registry, Defaults/
      Models/             — PlanResult
      UseCases/            — UploadOrchestrator
    Infrastructure/
      Scheduling/          — BackgroundServiceUploadExecutor, StaleSessionReconciler
    DependencyInjection/
  FlowX.Upload.EntityFrameworkCore/
tests/
  FlowX.Upload.Tests/
  FlowX.Upload.EntityFrameworkCore.Tests/
```

## Coding standards

- File-scoped namespaces, nullable reference types enabled, explicit access
  modifiers, `IReadOnlyCollection<T>` for exposed collections.
- No `#if` conditional compilation in the core package unless no shared
  implementation is possible across all three TFMs.
- Before adding an interface, abstraction, or new package: ask "what
  concrete problem does this solve?"
- New folders should track a meaningful boundary (a DDD tactical pattern in
  Domain, a Clean Architecture layer/port boundary in Application) — not
  type-kind for its own sake. See `docs/design.md` for the reasoning behind
  the current folder structure before proposing a different one.
- New optional dependencies belong in a separate package (`FlowX.Upload.*`),
  never added to core's dependency tree.
- **Renaming a public type or member is a breaking change**, even when no
  signature changes — it forces every consumer's `using`/references to
  update, and if the member is persisted (e.g. an enum via
  `HasConversion<string>()`), it may require a data migration. See
  `docs/design.md#the-preprocess--plan-rename` for a worked example of what
  a rename's full impact assessment should look like — trace it all the way
  to persisted data, not just source references.

## Testing expectations

- **`UploadSession`**: every state transition, including every illegal one.
- **`UploadOrchestrator`**: test against fakes for `IUploadStore`,
  `IUploadExecutor`, and `IUploadPipelineRegistry`.
- **`StaleSessionReconciler`**: cover all branches (stale Processing, stale
  Confirmed, stale Planning with/without `ContextJson`/`ParsedDataJson`).
- New public APIs need tests before merge.

## Versioning & breaking changes

Follows [Semantic Versioning](https://semver.org/). A rename — of a type,
member, or a persisted enum value — is MAJOR. Before proposing one, check
whether an additive/obsoleting approach reaches the same goal without
breaking existing consumers; if a rename genuinely is required, say so
explicitly in the PR and include the full impact (source + any persisted
data format) in the description, per the example above.

## Pull request checklist

- [ ] Builds and passes tests on net6.0, net8.0, and net10.0
- [ ] New/changed public APIs have XML doc comments
- [ ] New public APIs have corresponding tests
- [ ] No new dependency was added to `FlowX.Upload` core
- [ ] If a rename or other breaking change: labeled, justified, and its
      impact on any persisted data (not just source code) is documented
- [ ] Relevant doc updated — `docs/design.md`, `docs/usage-guide.md`,
      `docs/architecture.md`, `docs/state-diagram.md`, and
      `docs/sequence-diagrams.md` all reference specific method/type names
      and drift out of sync silently if only the code changes

## Reporting issues

Open an issue with: the TFM(s) affected, a minimal repro (ideally a failing
test), and — if it's a behavioral question — which doc you expected to
answer it, so we know where the documentation gap is.
