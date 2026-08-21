# Usage Guide — Implementing a New Upload Pipeline

## 1. Define your context and parsed shapes

```csharp
public sealed record MyContext(string SchemaVersion); // or EmptyUploadContext if you need nothing extra
public sealed record MyRawRow(int SheetIndex, int RowNumber, string? SomeField, /* ... */);
```

## 2. Implement `IUploadContextPreparer<TContext>` (Stage 1: Prepare)

Runs first, resolves anything durable your later stages need — a schema
version, a resolved reference list, whatever doesn't fit in the file
itself. If you don't need this, use the package-provided
`EmptyContextPreparer` / `EmptyUploadContext` instead of writing your own.

```csharp
public sealed class MyContextPreparer : IUploadContextPreparer<MyContext>
{
    public Task<MyContext> PrepareAsync(Stream fileStream, string ownerContext, string initiatedBy, CancellationToken ct)
    {
        // e.g. sniff a header row for a schema version, resolve a reference list, etc.
        ...
    }
}
```

`TContext` must be a plain serializable type — no open resources, no
`Stream` references — since it's persisted as JSON immediately after
`Prepare` runs.

## 3. Implement `IUploadParser<TContext, TParsed>` (Stage 2: Parse)

Converts a file `Stream`, plus the context resolved in Stage 1, into your
parsed shape. Nothing else — no validation, no business rules.

```csharp
public sealed class MyParser : IUploadParser<MyContext, IReadOnlyCollection<MyRawRow>>
{
    public Task<IReadOnlyCollection<MyRawRow>> ParseAsync(Stream fileStream, MyContext context, CancellationToken ct)
    {
        ...
    }
}
```

## 4. Implement `IUploadPlanner<TParsed>` (Stage 3: Plan)

**Renamed from `IUploadPreProcessor<TParsed>`.** Two responsibilities:
validate, and — if valid — build the plan.

```csharp
public sealed class MyPlanner : IUploadPlanner<IReadOnlyCollection<MyRawRow>>
{
    public async Task<PlanResult> PlanAsync(IReadOnlyCollection<MyRawRow> rows, CancellationToken ct)
    {
        var errors = Validate(rows);
        if (errors.Count > 0)
            return PlanResult.ValidationFailed(errors);

        var planItems = BuildPlan(rows); // your Create/Update/Delete diffing logic
        return PlanResult.Success(planItems);
    }
}
```

**Validate before touching any external system.** If validation needs data
from a database or external API, do that after basic field-level checks
pass — don't spend an external call on a row already known to be invalid.

**`PlanItem` payloads are opaque JSON** — only your planner and processor
agree on their shape:

```csharp
new PlanItem(Guid.NewGuid(), PlanItemKind.Create, JsonSerializer.Serialize(myPayload), preActionJson, "human-readable description")
```

## 5. Implement `IUploadProcessor` (Stage 4: Process)

Executes the confirmed (possibly partially-selected) plan.

- **Commit per chunk, not per whole plan.** Return item-level results,
  don't throw for a single item's failure.
- **Support dry-run.** Run real writes inside a transaction, roll back when
  `isDryRun` is true.

```csharp
public sealed class MyProcessor : IUploadProcessor
{
    public async Task<IReadOnlyCollection<PlanItemResult>> ExecuteAsync(
        IReadOnlyCollection<PlanItem> confirmedPlan, bool isDryRun, CancellationToken ct)
    {
        var results = new List<PlanItemResult>();

        foreach (var chunk in confirmedPlan.Chunk(200))
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var succeeded = new List<PlanItem>();

            foreach (var item in chunk)
            {
                if (!await TryExecutePreAction(item, isDryRun, ct))
                {
                    results.Add(new PlanItemResult(item.Id, false, "Pre-action failed."));
                    continue;
                }
                ApplyToChangeTracker(item);
                succeeded.Add(item);
            }

            await _db.SaveChangesAsync(ct);
            if (isDryRun) await tx.RollbackAsync(ct); else await tx.CommitAsync(ct);
            results.AddRange(succeeded.Select(i => new PlanItemResult(i.Id, true, null)));
        }

        return results;
    }
}
```

## 6. (Optional) Implement `IUploadCorrector<TContext, TParsed>`

Only needed if you want users to fix invalid rows from the UI instead of
re-uploading. Now receives `TContext` alongside the parsed data, in case
correction logic needs it.

```csharp
public sealed class MyCorrector : IUploadCorrector<MyContext, IReadOnlyCollection<MyRawRow>>
{
    public IReadOnlyCollection<MyRawRow> ApplyCorrections(
        IReadOnlyCollection<MyRawRow> parsed, MyContext context, IReadOnlyCollection<RowCorrection> corrections)
    {
        // Match corrections to rows by (SheetIndex, RowNumber), apply by Field name.
        // MUST be idempotent.
    }
}
```

If you skip this, a `ValidationFailed` session can only be resolved by
re-uploading the file — `SubmitCorrectionsAsync` throws `NotSupportedException`.

## 7. Register the pipeline

```csharp
services
    .AddFlowXUpload()
    .AddPipeline<MyContext, IReadOnlyCollection<MyRawRow>>("my-domain.my-upload", pipeline => pipeline
        .UsePreparer<MyContextPreparer>()
        .UseParser<MyParser>()
        .UsePlanner<MyPlanner>()          // renamed from UsePreProcessor
        .UseProcessor<MyProcessor>()
        .UseCorrector<MyCorrector>());     // omit if you skipped step 6
```

No extra context needed? Swap in the defaults:

```csharp
.AddPipeline<EmptyUploadContext, IReadOnlyCollection<MyRawRow>>("my-domain.my-upload", pipeline => pipeline
    .UsePreparer<EmptyContextPreparer>()
    .UseParser<MyParser>()   // now IUploadParser<EmptyUploadContext, ...>
    .UsePlanner<MyPlanner>()
    .UseProcessor<MyProcessor>());
```

## 8. Register a store

```csharp
services.AddEfCoreUploadStore(services, opts => opts.UseSqlServer(connectionString));
```

If you're upgrading an existing deployment, see
[docs/design.md](design.md#the-preprocess--plan-rename) for the **required
one-time data migration** on the `Status` column before deploying.

## 9. Wire the API

Unchanged — five thin endpoints backed by `UploadOrchestrator` and
`IUploadStore`. See [docs/sequence-diagrams.md](sequence-diagrams.md) for
exactly when each is called.

## 10. Test

- **Planner**: exhaustive Create/Update/Delete classification cases.
- **Processor**: a case where one item's pre-action fails — rest of the
  chunk still commits, failure surfaces as a `PlanItemResult`.
- **Corrector** (if implemented): applying the same correction twice
  produces the same result as applying it once.

The orchestrator, aggregate, and reconciler are covered by the package's
own test suite.
