# Usage Guide — Implementing a New Upload Pipeline

This walks through adding a new bulk-upload use case end to end. It follows
the same steps used to build the reference inventory bulk-upload pipeline —
substitute your own domain types where noted.

## 1. Define your parsed shape

Pick whatever intermediate representation makes sense between "raw file"
and "validated business data." It doesn't need to be your domain model —
usually it's closer to the raw row shape, so validation errors can point
back to specific sheet/row/field locations.

```csharp
public sealed record MyRawRow(int SheetIndex, int RowNumber, string? SomeField, /* ... */);
```

## 2. Implement `IUploadParser<TParsed>`

Converts a file `Stream` into your parsed shape. Nothing else — no
validation, no business rules.

```csharp
public sealed class MyParser : IUploadParser<IReadOnlyCollection<MyRawRow>>
{
    public Task<IReadOnlyCollection<MyRawRow>> ParseAsync(Stream file, CancellationToken ct)
    {
        // e.g. ClosedXML for Excel — see FlowX.Upload.Excel if you need shared helpers
        ...
    }
}
```

## 3. Implement `IUploadPreProcessor<TParsed>`

Two responsibilities: validate, and — if valid — build the plan.

```csharp
public sealed class MyPreProcessor : IUploadPreProcessor<IReadOnlyCollection<MyRawRow>>
{
    public async Task<PreProcessResult> ProcessAsync(IReadOnlyCollection<MyRawRow> rows, CancellationToken ct)
    {
        var errors = Validate(rows);
        if (errors.Count > 0)
            return PreProcessResult.ValidationFailed(errors);

        var planItems = BuildPlan(rows); // your Create/Update/Delete diffing logic
        return PreProcessResult.Plan(planItems);
    }
}
```

**Validate before touching any external system.** If validation needs data
from a database or external API (as the inventory pipeline does, checking
the provider catalog), do that after the basic field-level checks pass —
don't spend an external call on a row that's already known to be invalid.

**PlanItem payloads are opaque JSON.** Only your pre-processor and processor
agree on their shape:

```csharp
new PlanItem(Guid.NewGuid(), PlanItemKind.Create, JsonSerializer.Serialize(myPayload), preActionJson, "human-readable description")
```

## 4. Implement `IUploadProcessor`

Executes the confirmed (possibly partially-selected) plan. Two rules that
are contractual, not optional:

- **Commit per chunk, not per whole plan.** Partial success is expected —
  return item-level results, don't throw for a single item's failure.
- **Support dry-run.** Run real writes inside a transaction, and roll it
  back when `isDryRun` is true, so real constraints are still exercised.

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
                    continue; // does not block the rest of the chunk
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

If any external, non-transactional side effect (calling another API) is
part of a pre-action, document explicitly that dry-run cannot verify it —
see [docs/design.md](design.md#known-limitations).

## 5. (Optional) Implement `IUploadCorrector<TParsed>`

Only needed if you want users to fix invalid rows from the UI instead of
re-uploading. Applies field-level patches to your parsed shape.

```csharp
public sealed class MyCorrector : IUploadCorrector<IReadOnlyCollection<MyRawRow>>
{
    public IReadOnlyCollection<MyRawRow> ApplyCorrections(
        IReadOnlyCollection<MyRawRow> parsed, IReadOnlyCollection<RowCorrection> corrections)
    {
        // Match corrections to rows by (SheetIndex, RowNumber), apply by Field name.
        // MUST be idempotent — a correction submitted twice must not double-apply.
    }
}
```

If you skip this, a `ValidationFailed` session can only be resolved by
re-uploading the file — `SubmitCorrectionsAsync` will throw
`NotSupportedException`.

## 6. Register the pipeline

```csharp
services
    .AddFlowXUpload()
    .AddPipeline<IReadOnlyCollection<MyRawRow>>("my-domain.my-upload", pipeline => pipeline
        .UseParser<MyParser>()
        .UsePreProcessor<MyPreProcessor>()
        .UseProcessor<MyProcessor>()
        .UseCorrector<MyCorrector>()); // omit if you skipped step 5
```

Pick a stable, descriptive key (`"<domain>.<use-case>"`). It's a plain
string with no compile-time check — consider exposing it as a `const string`
in your application to reduce the risk of a typo causing a confusing
"pipeline not found" error at runtime.

## 7. Register a store

```csharp
services.AddEfCoreUploadStore(services, opts => opts.UseSqlServer(connectionString));
// or implement IUploadStore yourself against your own persistence technology
```

## 8. Wire the API

Five endpoints, all thin — `UploadOrchestrator` and `IUploadStore` do the work.

| Method | Path | Calls |
|---|---|---|
| POST | `/my-uploads` | `orchestrator.StartAsync(...)` |
| GET | `/uploads/{id}` | `store.GetAsync(...)` |
| POST | `/uploads/{id}/corrections` | `orchestrator.SubmitCorrectionsAsync(...)` |
| POST | `/uploads/{id}/confirm` | `orchestrator.ConfirmAsync(...)` |
| POST | `/uploads/{id}/retry` | `orchestrator.RetryAsync(...)` |

See [docs/sequence-diagrams.md](sequence-diagrams.md) for exactly when each
is called relative to background work.

## 9. Test

At minimum:

- **Pre-processor**: exhaustive Create/Update/Delete classification cases —
  this is almost always the highest-risk logic in a new pipeline.
- **Processor**: a case where one item's pre-action fails — assert the rest
  of the chunk still commits, and the failure surfaces as a `PlanItemResult`,
  not an exception.
- **Corrector** (if implemented): applying the same correction twice
  produces the same result as applying it once.

The orchestrator, aggregate, and reconciler are already covered by the
package's own test suite — you don't need to re-test package behavior, only
your pipeline's implementation of the four ports above.
