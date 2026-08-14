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

The solution multi-targets `net6.0`, `net8.0`, and `net10.0`. Run the full
test suite against **all three** before opening a PR:

```bash
dotnet test -f net6.0
dotnet test -f net8.0
dotnet test -f net10.0
```

A change that only compiles/passes on one TFM is not ready to merge — this
is the single most common way multi-targeted packages break silently for
some consumers.

## Project layout

```
src/
  FlowX.Upload/                     — core (Domain, Application, Infrastructure, DI)
  FlowX.Upload.EntityFrameworkCore/ — optional reference IUploadStore
  FlowX.Upload.Excel/               — optional ClosedXML parsing helpers (if/when this exists)
tests/
  FlowX.Upload.Tests/
  FlowX.Upload.EntityFrameworkCore.Tests/
```

## Coding standards

- File-scoped namespaces, nullable reference types enabled, explicit access
  modifiers, `IReadOnlyCollection<T>` for exposed collections.
- No `#if` conditional compilation in the core package unless you've
  confirmed no shared implementation is possible across all three TFMs —
  this has been achieved so far (e.g. the pipeline registry is hand-rolled
  specifically to avoid net8.0-only keyed DI). Don't reintroduce a
  compatibility gap without discussing it first.
- Before adding an interface, abstraction, or new package: ask "what
  concrete problem does this solve?" If the answer is speculative future
  reuse rather than a current, real use case, it doesn't belong yet.
- XML doc comments on all public types and members — they ship in the
  package and are the primary reference for consumers who don't read this repo.
- New optional dependencies (a parsing library, a persistence technology)
  belong in a separate package (`FlowX.Upload.*`), never added to core's
  dependency tree. This is a hard rule, not a preference — see
  [docs/design.md](docs/design.md) for why.

## Testing expectations

- **`UploadSession`**: every state transition, including every illegal one,
  is a unit test. This is the aggregate that guarantees correctness for
  every consumer — under-testing it here is worse than under-testing any
  one pipeline implementation.
- **`UploadOrchestrator`**: test against fakes for `IUploadStore`,
  `IUploadExecutor`, and `IUploadPipelineRegistry`. Cover both the success
  path and exception-during-stage paths for each method.
- **`StaleSessionReconciler`**: cover all branches (stale Processing, stale
  Confirmed, stale PreProcessing with/without `ParsedDataJson`) — these are
  easy to accidentally collapse into one path during a refactor.
- New public APIs need tests before merge, not after.

## Versioning & breaking changes

This package follows [Semantic Versioning](https://semver.org/).

- **PATCH**: bug fixes, no public API surface change.
- **MINOR**: new public APIs, new optional parameters with defaults, new
  packages. Must not break existing consumers.
- **MAJOR**: any breaking change — removed/renamed public members, changed
  method signatures, changed behavior a consumer could reasonably have
  depended on.

Before proposing a breaking change:

1. Check whether the same goal is reachable via an additive change instead
   (a new overload, a new optional member, obsoleting rather than removing).
2. If a breaking change is genuinely required, say so explicitly in the PR
   description, including what downstream consumers need to change.
3. Flag it in review — breaking changes need explicit sign-off, not a quiet
   merge.

## Pull request checklist

- [ ] Builds and passes tests on net6.0, net8.0, and net10.0
- [ ] New/changed public APIs have XML doc comments
- [ ] New public APIs have corresponding tests
- [ ] No new dependency was added to `FlowX.Upload` core (only to an
      optional extension package, if at all)
- [ ] If behavior changed: is it a breaking change? Labeled and justified if so
- [ ] Relevant doc updated if the change affects `docs/design.md`,
      `docs/usage-guide.md`, or either diagram doc (diagrams and prose drift
      out of sync silently — check both)

## Reporting issues

Open an issue with: the TFM(s) affected, a minimal repro (ideally a failing
test), and — if it's a behavioral question rather than a bug — which part of
[docs/design.md](docs/design.md) you expected to answer it, so we know where
the documentation gap is.
