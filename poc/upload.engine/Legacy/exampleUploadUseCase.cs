// ============================================================================
// Inventory Bulk-Upload Use Case — Full implementation on top of FlowX.Upload
// ============================================================================
// This file is organized by logical project/folder (as comments) to mirror how
// it would actually be split across your solution:
//
//   MyApp.Inventory.Infrastructure.Upload/
//     RawInventoryRow.cs
//     InventoryExcelParser.cs
//     InventoryPlanPayload.cs
//     InventoryPreProcessor.cs
//     InventoryDataCorrector.cs
//     Commands/ (CreateOfferingCommand, UpdateOfferingCommand, DeleteOfferingCommand)
//     InventoryUploadProcessor.cs
//     UploadServiceRegistration.cs   (DI wiring — only place FlowX.Upload types are referenced)
//
//   MyApp.Inventory.Api/
//     InventoryUploadsController.cs
//
// None of the FlowX.Upload package types (UploadOrchestrator, IUploadParser<T>,
// IUploadPreProcessor<T>, IUploadProcessor, IUploadCorrector<T>, PlanItem,
// PreProcessResult, RowValidationError, RowCorrection, etc.) are redefined here
// — they come from the package as designed in the architecture review. Existing
// app-owned types (IProviderCatalogClient, IBranchInventoryReadStore,
// InventoryDbContext, Product, BranchOffering, ProductDetail) are stubbed
// minimally below, marked accordingly, so this file is self-contained to read.

using FlowX.Upload.Application;
using FlowX.Upload.DependencyInjection;
using FlowX.Upload.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ClosedXML.Excel;
using System.Text.Json;

namespace MyApp.Inventory.Infrastructure.Upload;

// ----------------------------------------------------------------------------
// Existing app-owned types (stubs — already exist in your domain/infra layers)
// ----------------------------------------------------------------------------

public sealed class Product
{
    public Guid Id { get; set; }
    public string GroupRef { get; set; } = default!;
    public string ProviderRef { get; set; } = default!;
}

public sealed class BranchOffering
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public string ProviderRef { get; set; } = default!;
    public string GroupRef { get; set; } = default!;
    public Guid LocalProductId { get; set; }
}

public sealed class ProductDetail
{
    public Guid Id { get; set; }
    public string ProviderRef { get; set; } = default!;
    public decimal Price { get; set; }
    public decimal? Discount { get; set; }
    public DateOnly EffectiveDate { get; set; }
    // Unique index configured in OnModelCreating: (ProviderRef, EffectiveDate)
}

public sealed class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<BranchOffering> BranchOfferings => Set<BranchOffering>();
    public DbSet<ProductDetail> ProductDetails => Set<ProductDetail>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProductDetail>()
            .HasIndex(d => new { d.ProviderRef, d.EffectiveDate })
            .IsUnique();
    }
}

public sealed record ProviderProduct(string ProviderRef);

public interface IProviderCatalogClient
{
    Task<IReadOnlyCollection<ProviderProduct>> GetProductsAsync(
        string groupRef, IReadOnlyCollection<string> providerRefs, CancellationToken ct);

    Task CreateProductAsync(string providerRef, string groupRef, CancellationToken ct);
}

public sealed class ProviderApiException : Exception
{
    public ProviderApiException(string message) : base(message) { }
}

public sealed record BranchOfferingSnapshot(string GroupRef, string ProviderRef, bool LocalProductExists, Guid? LocalProductId);

public interface IBranchInventoryReadStore
{
    Task<IReadOnlyCollection<BranchOfferingSnapshot>> GetOfferingsAsync(
        Guid branchId, IReadOnlyCollection<string> groupRefs, CancellationToken ct);
}

// ----------------------------------------------------------------------------
// Stage 1: Parser — Excel (multi-sheet) -> IReadOnlyCollection<RawInventoryRow>
// ----------------------------------------------------------------------------

public sealed record RawInventoryRow(
    int SheetIndex,
    int RowNumber,
    string? GroupRef,
    string? ProviderRef,
    string? PriceRaw,
    string? DiscountRaw,
    string? EffectiveDateRaw);

public sealed class InventoryExcelParser : IUploadParser<IReadOnlyCollection<RawInventoryRow>>
{
    public Task<IReadOnlyCollection<RawInventoryRow>> ParseAsync(Stream file, CancellationToken ct)
    {
        using var workbook = new XLWorkbook(file);
        var rows = new List<RawInventoryRow>();

        // Sheet 0: group/provider ref + price. Sheet 1: discounts + effective date.
        // Joined by (GroupRef, ProviderRef) — the merge logic below is intentionally
        // owned entirely by this parser, not the package.
        var mainSheet = workbook.Worksheet(1);
        var detailsSheet = workbook.Worksheet(2);

        var detailsByKey = detailsSheet.RowsUsed().Skip(1).ToDictionary(
            r => (r.Cell(1).GetString(), r.Cell(2).GetString()),
            r => (Discount: r.Cell(3).GetString(), EffectiveDate: r.Cell(4).GetString()));

        foreach (var row in mainSheet.RowsUsed().Skip(1))
        {
            var groupRef = row.Cell(1).GetString();
            var providerRef = row.Cell(2).GetString();
            detailsByKey.TryGetValue((groupRef, providerRef), out var details);

            rows.Add(new RawInventoryRow(
                SheetIndex: 0,
                RowNumber: row.RowNumber(),
                GroupRef: groupRef,
                ProviderRef: providerRef,
                PriceRaw: row.Cell(3).GetString(),
                DiscountRaw: details.Discount,
                EffectiveDateRaw: details.EffectiveDate));
        }

        return Task.FromResult<IReadOnlyCollection<RawInventoryRow>>(rows);
    }
}

// ----------------------------------------------------------------------------
// Plan item payloads (opaque JSON carried inside PlanItem.PayloadJson)
// ----------------------------------------------------------------------------

public sealed record InventoryPlanPayload(
    string GroupRef,
    string ProviderRef,
    Guid BranchId,
    decimal Price,
    decimal? Discount,
    DateOnly EffectiveDate,
    bool LocalProductExists,
    Guid? LocalProductId);

public sealed record CreateProviderProductPayload(string ProviderRef, string GroupRef);

// ----------------------------------------------------------------------------
// Stage 2: PreProcessor — validate, diff against current offerings, build plan
// ----------------------------------------------------------------------------

public sealed class InventoryPreProcessor(
    IProviderCatalogClient providerClient,
    IBranchInventoryReadStore branchInventory,
    Guid branchId) : IUploadPreProcessor<IReadOnlyCollection<RawInventoryRow>>
{
    public async Task<PreProcessResult> ProcessAsync(
        IReadOnlyCollection<RawInventoryRow> rows, CancellationToken ct)
    {
        // 1. Row-level validation — minimal required fields present and parseable.
        var errors = new List<RowValidationError>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.GroupRef))
                errors.Add(new(row.SheetIndex, row.RowNumber, nameof(row.GroupRef), "Group reference is required."));
            if (string.IsNullOrWhiteSpace(row.ProviderRef))
                errors.Add(new(row.SheetIndex, row.RowNumber, nameof(row.ProviderRef), "Provider reference is required."));
            if (!decimal.TryParse(row.PriceRaw, out _))
                errors.Add(new(row.SheetIndex, row.RowNumber, nameof(row.PriceRaw), "Price is missing or invalid."));
            if (!DateOnly.TryParse(row.EffectiveDateRaw, out _))
                errors.Add(new(row.SheetIndex, row.RowNumber, nameof(row.EffectiveDateRaw), "Effective date is missing or invalid."));
        }

        if (errors.Count > 0)
            return PreProcessResult.ValidationFailed(errors);

        // 2. Duplicate-date pre-check — surfaces as a validation error here instead
        //    of an opaque DB unique-constraint failure at execution time.
        var duplicateDates = rows
            .GroupBy(r => (r.ProviderRef, r.EffectiveDateRaw))
            .Where(g => g.Count() > 1);

        foreach (var group in duplicateDates)
        {
            foreach (var row in group)
            {
                errors.Add(new(row.SheetIndex, row.RowNumber, nameof(row.EffectiveDateRaw),
                    $"Duplicate effective date for provider ref '{row.ProviderRef}' in this file."));
            }
        }

        if (errors.Count > 0)
            return PreProcessResult.ValidationFailed(errors);

        var validRows = rows.Select(MapRow).ToList();

        // 3. Groups present in the file.
        var groupRefs = validRows.Select(r => r.GroupRef).Distinct().ToList();

        // 4. Provider catalog check per group — flags missing provider products for pre-action.
        var providerRefsByGroup = validRows
            .GroupBy(r => r.GroupRef)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(r => r.ProviderRef).Distinct().ToList());

        var existingProviderProducts = new HashSet<string>();
        foreach (var groupRef in groupRefs)
        {
            var found = await providerClient.GetProductsAsync(groupRef, providerRefsByGroup[groupRef], ct);
            foreach (var p in found) existingProviderProducts.Add(p.ProviderRef);
        }

        // 5. Current branch offerings for these groups (local DB).
        var currentOfferings = await branchInventory.GetOfferingsAsync(branchId, groupRefs, ct);
        var fileProviderRefs = validRows.Select(r => r.ProviderRef).ToHashSet();

        var planItems = new List<PlanItem>();

        // 6. Rows in the file -> Create or Update.
        foreach (var row in validRows)
        {
            var existingOffering = currentOfferings.FirstOrDefault(o => o.ProviderRef == row.ProviderRef);
            var kind = existingOffering is null ? PlanItemKind.Create : PlanItemKind.Update;

            var payload = new InventoryPlanPayload(
                row.GroupRef, row.ProviderRef, branchId, row.Price, row.Discount, row.EffectiveDate,
                LocalProductExists: existingOffering?.LocalProductExists ?? false,
                LocalProductId: existingOffering?.LocalProductId);

            string? preActionJson = null;
            if (!existingProviderProducts.Contains(row.ProviderRef))
                preActionJson = JsonSerializer.Serialize(new CreateProviderProductPayload(row.ProviderRef, row.GroupRef));

            planItems.Add(new PlanItem(
                Guid.NewGuid(), kind, JsonSerializer.Serialize(payload), preActionJson,
                $"{kind} {row.ProviderRef} in {row.GroupRef}"));
        }

        // 7. Branch offerings not present in the file -> Delete.
        foreach (var offering in currentOfferings.Where(o => !fileProviderRefs.Contains(o.ProviderRef)))
        {
            var payload = new InventoryPlanPayload(
                offering.GroupRef, offering.ProviderRef, branchId, 0, null, default,
                LocalProductExists: true, LocalProductId: offering.LocalProductId);

            planItems.Add(new PlanItem(
                Guid.NewGuid(), PlanItemKind.Delete, JsonSerializer.Serialize(payload), null,
                $"Delete {offering.ProviderRef} from {offering.GroupRef}"));
        }

        return PreProcessResult.Plan(planItems);
    }

    private static (string GroupRef, string ProviderRef, decimal Price, decimal? Discount, DateOnly EffectiveDate) MapRow(RawInventoryRow r)
        => (r.GroupRef!, r.ProviderRef!, decimal.Parse(r.PriceRaw!),
            decimal.TryParse(r.DiscountRaw, out var d) ? d : null, DateOnly.Parse(r.EffectiveDateRaw!));
}

// ----------------------------------------------------------------------------
// Correction support — lets the UI patch specific fields without re-uploading
// ----------------------------------------------------------------------------

public sealed class InventoryDataCorrector : IUploadCorrector<IReadOnlyCollection<RawInventoryRow>>
{
    public IReadOnlyCollection<RawInventoryRow> ApplyCorrections(
        IReadOnlyCollection<RawInventoryRow> parsed, IReadOnlyCollection<RowCorrection> corrections)
    {
        var byRow = corrections.ToLookup(c => (c.SheetIndex, c.RowNumber));

        return parsed.Select(row =>
        {
            var patches = byRow[(row.SheetIndex, row.RowNumber)];
            return patches.Aggregate(row, (current, c) => c.Field switch
            {
                nameof(RawInventoryRow.GroupRef) => current with { GroupRef = c.Value },
                nameof(RawInventoryRow.ProviderRef) => current with { ProviderRef = c.Value },
                nameof(RawInventoryRow.PriceRaw) => current with { PriceRaw = c.Value },
                nameof(RawInventoryRow.DiscountRaw) => current with { DiscountRaw = c.Value },
                nameof(RawInventoryRow.EffectiveDateRaw) => current with { EffectiveDateRaw = c.Value },
                _ => current
            });
        }).ToList();
    }
}

// ----------------------------------------------------------------------------
// Stage 3: Processor — Command pattern, chunked + transactional, dry-run aware
// ----------------------------------------------------------------------------

public interface IPlanItemCommand
{
    /// External, non-transactional side effect. Returns false = pre-action
    /// failed; the item is reported as failed but does NOT block the rest
    /// of the batch. Not meaningfully verifiable in dry-run mode (see below).
    Task<bool> TryExecutePreActionAsync(PlanItem item, bool isDryRun, CancellationToken ct);

    /// Local, transactional DB mutation only. No I/O.
    void ApplyToChangeTracker(PlanItem item, InventoryDbContext db);
}

public sealed class CreateOfferingCommand(IProviderCatalogClient providerClient) : IPlanItemCommand
{
    public async Task<bool> TryExecutePreActionAsync(PlanItem item, bool isDryRun, CancellationToken ct)
    {
        if (item.PreActionPayloadJson is null)
            return true; // provider product already exists, no pre-action needed

        if (isDryRun)
            return true; // cannot safely simulate an external, non-transactional call — documented limitation

        var preAction = JsonSerializer.Deserialize<CreateProviderProductPayload>(item.PreActionPayloadJson)!;
        try
        {
            await providerClient.CreateProductAsync(preAction.ProviderRef, preAction.GroupRef, ct);
            return true;
        }
        catch (ProviderApiException)
        {
            return false;
        }
    }

    public void ApplyToChangeTracker(PlanItem item, InventoryDbContext db)
    {
        var payload = JsonSerializer.Deserialize<InventoryPlanPayload>(item.PayloadJson)!;

        if (!payload.LocalProductExists)
            db.Products.Add(new Product { Id = Guid.NewGuid(), GroupRef = payload.GroupRef, ProviderRef = payload.ProviderRef });

        db.BranchOfferings.Add(new BranchOffering
        {
            Id = Guid.NewGuid(),
            BranchId = payload.BranchId,
            ProviderRef = payload.ProviderRef,
            GroupRef = payload.GroupRef
        });

        db.ProductDetails.Add(new ProductDetail
        {
            Id = Guid.NewGuid(),
            ProviderRef = payload.ProviderRef,
            Price = payload.Price,
            Discount = payload.Discount,
            EffectiveDate = payload.EffectiveDate
        }); // unique index on (ProviderRef, EffectiveDate) enforces "no duplicate date" at the DB level
    }
}

public sealed class UpdateOfferingCommand : IPlanItemCommand
{
    public Task<bool> TryExecutePreActionAsync(PlanItem item, bool isDryRun, CancellationToken ct) => Task.FromResult(true);

    public void ApplyToChangeTracker(PlanItem item, InventoryDbContext db)
    {
        var payload = JsonSerializer.Deserialize<InventoryPlanPayload>(item.PayloadJson)!;
        db.ProductDetails.Add(new ProductDetail
        {
            Id = Guid.NewGuid(),
            ProviderRef = payload.ProviderRef,
            Price = payload.Price,
            Discount = payload.Discount,
            EffectiveDate = payload.EffectiveDate
        }); // new dated detail row — preserves history, never mutates a prior date's record
    }
}

public sealed class DeleteOfferingCommand : IPlanItemCommand
{
    public Task<bool> TryExecutePreActionAsync(PlanItem item, bool isDryRun, CancellationToken ct) => Task.FromResult(true);

    public void ApplyToChangeTracker(PlanItem item, InventoryDbContext db)
    {
        var payload = JsonSerializer.Deserialize<InventoryPlanPayload>(item.PayloadJson)!;
        var offering = db.BranchOfferings.Local
            .FirstOrDefault(o => o.BranchId == payload.BranchId && o.ProviderRef == payload.ProviderRef)
            ?? db.BranchOfferings.First(o => o.BranchId == payload.BranchId && o.ProviderRef == payload.ProviderRef);
        db.BranchOfferings.Remove(offering);
    }
}

public sealed class InventoryUploadProcessor(
    InventoryDbContext db,
    IReadOnlyDictionary<PlanItemKind, IPlanItemCommand> commands) : IUploadProcessor
{
    private const int ChunkSize = 200;

    public async Task<IReadOnlyCollection<PlanItemResult>> ExecuteAsync(
        IReadOnlyCollection<PlanItem> confirmedPlan, bool isDryRun, CancellationToken ct)
    {
        var results = new List<PlanItemResult>();

        foreach (var chunk in confirmedPlan.Chunk(ChunkSize))
        {
            var chunkSucceeded = new List<PlanItem>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            foreach (var item in chunk)
            {
                var command = commands[item.Kind];
                var preActionOk = await command.TryExecutePreActionAsync(item, isDryRun, ct);

                if (!preActionOk)
                {
                    results.Add(new PlanItemResult(item.Id, false, "Pre-action (provider product creation) failed."));
                    continue; // does not block the rest of the batch
                }

                command.ApplyToChangeTracker(item, db);
                chunkSucceeded.Add(item);
            }

            // Runs for real even in dry-run — this is what actually catches the
            // unique (ProviderRef, EffectiveDate) constraint before anything commits.
            await db.SaveChangesAsync(ct);

            if (isDryRun)
                await transaction.RollbackAsync(ct);
            else
                await transaction.CommitAsync(ct);

            results.AddRange(chunkSucceeded.Select(i => new PlanItemResult(i.Id, true, null)));
        }

        return results;
    }
}

// ----------------------------------------------------------------------------
// DI wiring — the only place FlowX.Upload package types are referenced besides
// the pipeline component signatures above.
// ----------------------------------------------------------------------------

public static class UploadServiceRegistration
{
    public static IServiceCollection AddInventoryUploadPipeline(this IServiceCollection services)
    {
        services.AddScoped<IProviderCatalogClient, ProviderCatalogClient>();     // your existing HTTP client impl
        services.AddScoped<IBranchInventoryReadStore, BranchInventoryReadStore>(); // your existing EF-backed read store

        services.AddScoped<IReadOnlyDictionary<PlanItemKind, IPlanItemCommand>>(sp => new Dictionary<PlanItemKind, IPlanItemCommand>
        {
            [PlanItemKind.Create] = ActivatorUtilities.CreateInstance<CreateOfferingCommand>(sp),
            [PlanItemKind.Update] = ActivatorUtilities.CreateInstance<UpdateOfferingCommand>(sp),
            [PlanItemKind.Delete] = ActivatorUtilities.CreateInstance<DeleteOfferingCommand>(sp),
        });

        services
            .AddFlowXUpload()
            .AddPipeline<IReadOnlyCollection<RawInventoryRow>>("inventory.branch-upload", pipeline => pipeline
                .UseParser<InventoryExcelParser>()
                .UsePreProcessor<InventoryPreProcessor>()
                .UseProcessor<InventoryUploadProcessor>()
                .UseCorrector<InventoryDataCorrector>());

        return services;
    }
}

// Minimal stand-ins referenced above — your actual implementations already exist.
public sealed class ProviderCatalogClient : IProviderCatalogClient
{
    public Task<IReadOnlyCollection<ProviderProduct>> GetProductsAsync(
        string groupRef, IReadOnlyCollection<string> providerRefs, CancellationToken ct)
        => throw new NotImplementedException("Existing Provider API HTTP client.");

    public Task CreateProductAsync(string providerRef, string groupRef, CancellationToken ct)
        => throw new NotImplementedException("Existing Provider API HTTP client.");
}

public sealed class BranchInventoryReadStore : IBranchInventoryReadStore
{
    public Task<IReadOnlyCollection<BranchOfferingSnapshot>> GetOfferingsAsync(
        Guid branchId, IReadOnlyCollection<string> groupRefs, CancellationToken ct)
        => throw new NotImplementedException("Existing EF-backed read store.");
}

// ============================================================================
// MyApp.Inventory.Api — Controller
// ============================================================================

namespace MyApp.Inventory.Api;

using MyApp.Inventory.Infrastructure.Upload;

[ApiController]
[Route("api")]
public sealed class InventoryUploadsController(
    UploadOrchestrator orchestrator,
    IUploadStore store) : ControllerBase
{
    private const string PipelineKey = "inventory.branch-upload";

    /// Starts the upload. ?dryRun=true runs the full pipeline (including a real
    /// transactional Process pass that is always rolled back) without persisting
    /// any change — useful for testing a file against current data/constraints.
    [HttpPost("branches/{branchId:guid}/inventory/uploads")]
    public async Task<IActionResult> Upload(
        Guid branchId, IFormFile file, [FromQuery] bool dryRun, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);

        var sessionId = await orchestrator.StartAsync(
            PipelineKey, ownerContext: branchId.ToString(), initiatedBy: User.GetUserId(),
            fileContent: ms.ToArray(), isDryRun: dryRun, ct);

        return Accepted(new { sessionId, dryRun });
    }

    /// Polled by the UI while the job is Parsing/PreProcessing/Processing.
    /// Shape varies by status: ValidationErrors (ValidationFailed), Plan
    /// (PendingConfirmation), Results (Completed/Failed).
    [HttpGet("uploads/{sessionId:guid}")]
    public async Task<IActionResult> GetStatus(Guid sessionId, CancellationToken ct)
    {
        var session = await store.GetAsync(sessionId, ct);
        if (session is null) return NotFound();

        return Ok(new
        {
            session.Id,
            Status = session.Status.ToString(),
            session.IsDryRun,
            session.ValidationErrors,
            Plan = session.Status == UploadSessionStatus.PendingConfirmation ? session.Plan : null,
            session.Results,
            session.FailureReason
        });
    }

    /// Enhancement 1: fix invalid rows in the UI and resubmit without re-uploading
    /// the file. Corrections apply to the durably-persisted parsed data, and
    /// pre-processing (validation + plan-building) re-runs from there.
    [HttpPost("uploads/{sessionId:guid}/corrections")]
    public async Task<IActionResult> SubmitCorrections(
        Guid sessionId, [FromBody] SubmitCorrectionsRequest request, CancellationToken ct)
    {
        await orchestrator.SubmitCorrectionsAsync(sessionId, User.GetUserId(), request.Corrections, ct);
        return Accepted();
    }

    /// User selects which plan rows to execute, then confirms.
    [HttpPost("uploads/{sessionId:guid}/confirm")]
    public async Task<IActionResult> Confirm(
        Guid sessionId, [FromBody] ConfirmRequest request, CancellationToken ct)
    {
        await orchestrator.ConfirmAsync(sessionId, User.GetUserId(), request.SelectedItemIds, ct);
        return Accepted();
    }

    /// Session-level failure (crash, unhandled exception) — re-enters
    /// PendingConfirmation with the same plan for the user to confirm again.
    [HttpPost("uploads/{sessionId:guid}/retry")]
    public async Task<IActionResult> Retry(Guid sessionId, CancellationToken ct)
    {
        await orchestrator.RetryAsync(sessionId, ct);
        return Accepted();
    }
}

public sealed record SubmitCorrectionsRequest(IReadOnlyCollection<RowCorrection> Corrections);
public sealed record ConfirmRequest(IReadOnlyCollection<Guid> SelectedItemIds);

// Stand-in for your existing auth extension.
file static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this System.Security.Claims.ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value ?? throw new InvalidOperationException("No user id claim found.");
}