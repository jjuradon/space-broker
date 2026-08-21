// ============================================================================
// FlowX.Upload — Package Implementation
// ============================================================================
// Package boundaries (as separate projects/NuGet packages in your solution):
//
//   FlowX.Upload                       (core — this is most of the file below)
//     TargetFrameworks: net6.0;net8.0;net10.0
//     No dependency on EF Core, ClosedXML, or any specific persistence/parsing
//     technology. Depends only on Microsoft.Extensions.* (DI, Hosting, Logging).
//
//   FlowX.Upload.EntityFrameworkCore   (optional reference persistence adapter)
//     References FlowX.Upload + Microsoft.EntityFrameworkCore.
//     Ships EfUploadStore as ONE valid IUploadStore implementation — consumers
//     may ignore this package entirely and implement IUploadStore against
//     Dapper, Mongo, whatever their app already uses.
//
// Layer folders within FlowX.Upload:
//   Domain/          — UploadSession aggregate, value objects. Zero framework deps.
//   Application/      — ports (IUploadParser<T>, IUploadStore, IUploadExecutor,
//                        IUploadCorrector<T>, IUploadProcessor), UploadOrchestrator,
//                        pipeline registry/facade.
//   Infrastructure/   — BackgroundServiceUploadExecutor (default IUploadExecutor),
//                        StaleSessionReconciler.
//   DependencyInjection/ — AddFlowXUpload(), UploadBuilder, PipelineBuilder<TParsed>.
//
// .csproj sketch for the core package:
//
//   <Project Sdk="Microsoft.NET.Sdk">
//     <PropertyGroup>
//       <TargetFrameworks>net6.0;net8.0;net10.0</TargetFrameworks>
//       <Nullable>enable</Nullable>
//       <ImplicitUsings>enable</ImplicitUsings>
//       <PackageId>FlowX.Upload</PackageId>
//       <GenerateDocumentationFile>true</GenerateDocumentationFile>
//       <PackageReadmeFile>README.md</PackageReadmeFile>
//       <IncludeSymbols>true</IncludeSymbols>
//       <SymbolPackageFormat>snupkg</SymbolPackageFormat>
//       <PublishRepositoryUrl>true</PublishRepositoryUrl>
//       <EmbedUntrackedSources>true</EmbedUntrackedSources>
//     </PropertyGroup>
//     <ItemGroup>
//       <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="[verify against docs]" />
//       <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="[verify against docs]" />
//       <PackageReference Include="Microsoft.SourceLink.GitHub" Version="[verify against docs]" PrivateAssets="All" />
//     </ItemGroup>
//   </Project>
//
// No conditional compilation (#if NETx_0) appears anywhere below — every type
// here is expressible identically across net6.0/net8.0/net10.0. The one
// deliberate compatibility-driven choice is the manually-built pipeline
// registry (rather than keyed DI, which is net8.0+ only).
// ============================================================================

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowX.Upload.Domain;

// ----------------------------------------------------------------------------
// Domain — UploadSession aggregate and value objects
// ----------------------------------------------------------------------------

public enum UploadSessionStatus
{
    PreProcessing,
    ValidationFailed,
    PendingConfirmation,
    Confirmed,
    Processing,
    Completed,
    Failed
}

/// <summary>A field-level validation problem on a specific source row.</summary>
public sealed record RowValidationError(int SheetIndex, int RowNumber, string Field, string Message);

/// <summary>A field-level fix submitted by the user for a row that failed validation.</summary>
public sealed record RowCorrection(int SheetIndex, int RowNumber, string Field, string Value);

public enum PlanItemKind { Create, Update, Delete }

/// <summary>
/// One proposed change. Payloads are opaque JSON — only the pipeline that
/// produced them (via its PreProcessor/Processor) knows their shape.
/// </summary>
public sealed record PlanItem(Guid Id, PlanItemKind Kind, string PayloadJson, string? PreActionPayloadJson, string Description);

/// <summary>Outcome of executing one PlanItem. Item-level failure does not imply session-level failure.</summary>
public sealed record PlanItemResult(Guid PlanItemId, bool Succeeded, string? FailureReason);

/// <summary>
/// Aggregate root for one upload's lifecycle: Parse/PreProcess (with optional
/// correction loop) -> user confirmation of a subset of the plan -> Process.
/// All state transitions are guarded here; nothing outside this type may put
/// a session into an invalid state.
/// </summary>
public sealed class UploadSession
{
    public Guid Id { get; private set; }
    public string PipelineKey { get; private set; } = default!;
    public string OwnerContext { get; private set; } = default!;
    public string InitiatedBy { get; private set; } = default!;
    public UploadSessionStatus Status { get; private set; }
    public bool IsDryRun { get; private set; }

    /// <summary>Durable snapshot of the parsed (and possibly corrected) source data, as JSON.
    /// Enables both the correction flow and crash recovery of the PreProcessing stage
    /// without needing the original file bytes again.</summary>
    public string? ParsedDataJson { get; private set; }

    public IReadOnlyCollection<RowCorrection> PendingCorrections { get; private set; } = Array.Empty<RowCorrection>();
    public IReadOnlyCollection<RowValidationError> ValidationErrors { get; private set; } = Array.Empty<RowValidationError>();
    public IReadOnlyCollection<PlanItem> Plan { get; private set; } = Array.Empty<PlanItem>();
    public IReadOnlyCollection<PlanItemResult> Results { get; private set; } = Array.Empty<PlanItemResult>();
    public string? FailureReason { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }

    private UploadSession() { } // required for persistence rehydration

    public static UploadSession Start(string pipelineKey, string ownerContext, string initiatedBy, bool isDryRun = false)
    {
        if (string.IsNullOrWhiteSpace(pipelineKey))
            throw new ArgumentException("Pipeline key is required.", nameof(pipelineKey));

        return new UploadSession
        {
            Id = Guid.NewGuid(),
            PipelineKey = pipelineKey,
            OwnerContext = ownerContext,
            InitiatedBy = initiatedBy,
            Status = UploadSessionStatus.PreProcessing,
            IsDryRun = isDryRun,
            LastActivityAt = DateTimeOffset.UtcNow
        };
    }

    public void SetParsedData(string json)
    {
        EnsureStatus(UploadSessionStatus.PreProcessing);
        ParsedDataJson = json;
        Touch();
    }

    public void FailValidation(IReadOnlyCollection<RowValidationError> errors)
    {
        EnsureStatus(UploadSessionStatus.PreProcessing);
        ValidationErrors = errors;
        Status = UploadSessionStatus.ValidationFailed;
        Touch();
    }

    /// <summary>
    /// Submits field-level fixes for a previously failed validation, without
    /// needing the original file. Corrections may be empty (e.g. re-validate
    /// after an external fix). Re-enters PreProcessing.
    /// </summary>
    public void SubmitCorrections(string principalId, IReadOnlyCollection<RowCorrection> corrections)
    {
        EnsureStatus(UploadSessionStatus.ValidationFailed);
        if (principalId != InitiatedBy)
            throw new InvalidOperationException("Only the uploader who initiated the session may submit corrections.");
        if (ParsedDataJson is null)
            throw new InvalidOperationException("No parsed data available to correct; the file must be re-uploaded.");

        PendingCorrections = corrections;
        ValidationErrors = Array.Empty<RowValidationError>();
        Status = UploadSessionStatus.PreProcessing;
        Touch();
    }

    public void SetPlan(IReadOnlyCollection<PlanItem> plan)
    {
        EnsureStatus(UploadSessionStatus.PreProcessing);
        if (plan.Count == 0)
            throw new InvalidOperationException("Pre-processing produced an empty plan; nothing to confirm.");

        Plan = plan;
        Status = UploadSessionStatus.PendingConfirmation;
        Touch();
    }

    /// <summary>Only the initiating user may confirm, and only a subset of the plan need be selected.</summary>
    public void Confirm(string principalId, IReadOnlyCollection<Guid> selectedItemIds)
    {
        EnsureStatus(UploadSessionStatus.PendingConfirmation);
        if (principalId != InitiatedBy)
            throw new InvalidOperationException("Only the uploader who initiated the session may confirm it.");
        if (selectedItemIds.Count == 0)
            throw new InvalidOperationException("At least one plan item must be selected.");

        Plan = Plan.Where(p => selectedItemIds.Contains(p.Id)).ToList();
        Status = UploadSessionStatus.Confirmed;
        Touch();
    }

    public void MarkProcessing()
    {
        EnsureStatus(UploadSessionStatus.Confirmed);
        Status = UploadSessionStatus.Processing;
        Touch();
    }

    /// <summary>Completion may carry partial item-level failures; that is not a session failure.</summary>
    public void MarkCompleted(IReadOnlyCollection<PlanItemResult> results)
    {
        EnsureStatus(UploadSessionStatus.Processing);
        Results = results;
        Status = UploadSessionStatus.Completed;
        Touch();
    }

    public void MarkFailed(string reason)
    {
        if (Status == UploadSessionStatus.Completed)
            throw new InvalidOperationException("Cannot fail a completed session.");

        Status = UploadSessionStatus.Failed;
        FailureReason = reason;
        Touch();
    }

    /// <summary>Session-level failure recovery: re-enter confirmation with the same (already-selected) plan.
    /// No partial resumability is offered by design — see package docs.</summary>
    public void PrepareForRetry()
    {
        EnsureStatus(UploadSessionStatus.Failed);
        Status = UploadSessionStatus.PendingConfirmation;
        FailureReason = null;
        Touch();
    }

    /// <summary>Used only by the reconciler: recovers a Confirmed session whose enqueue
    /// was lost to a restart. The plan is durable, so re-entering Confirmed is safe.</summary>
    internal void ResetToConfirmed()
    {
        EnsureStatus(UploadSessionStatus.Processing);
        Status = UploadSessionStatus.Confirmed;
        Touch();
    }

    private void Touch() => LastActivityAt = DateTimeOffset.UtcNow;

    private void EnsureStatus(UploadSessionStatus expected)
    {
        if (Status != expected)
            throw new InvalidOperationException($"Expected status {expected} but session is {Status}.");
    }
}

namespace FlowX.Upload.Application;

using FlowX.Upload.Domain;

// ----------------------------------------------------------------------------
// Application — ports every consuming pipeline implements
// ----------------------------------------------------------------------------

/// <summary>Stage 1. Converts a raw file stream into an application-defined intermediate shape.</summary>
public interface IUploadParser<TParsed>
{
    Task<TParsed> ParseAsync(Stream file, CancellationToken ct);
}

/// <summary>Stage 2. Validates the parsed data and, if valid, builds the proposed plan of changes.</summary>
public interface IUploadPreProcessor<TParsed>
{
    Task<PreProcessResult> ProcessAsync(TParsed parsed, CancellationToken ct);
}

/// <summary>Optional. Applies UI-submitted field corrections to parsed data without needing the original file.
/// A pipeline that always forces re-upload on validation failure does not need to implement this.</summary>
public interface IUploadCorrector<TParsed>
{
    /// <remarks>Implementations must be idempotent — a correction submitted twice must not double-apply.</remarks>
    TParsed ApplyCorrections(TParsed parsed, IReadOnlyCollection<RowCorrection> corrections);
}

/// <summary>
/// Stage 3. Executes the confirmed plan.
/// Implementers MUST commit atomically per chunk (not per whole plan) — partial
/// success is expected and reported via the returned PlanItemResult collection,
/// not treated as a thrown exception / session-level failure.
/// When isDryRun is true, implementers should execute real writes inside a
/// transaction and always roll it back, so genuine DB constraints are still
/// exercised. External, non-transactional side effects (e.g. calling a third
/// party API) generally cannot be verified this way — document that gap per pipeline.
/// </summary>
public interface IUploadProcessor
{
    Task<IReadOnlyCollection<PlanItemResult>> ExecuteAsync(
        IReadOnlyCollection<PlanItem> confirmedPlan, bool isDryRun, CancellationToken ct);
}

/// <summary>Outcome of Stage 2: either the parsed data failed validation, or a plan was produced.</summary>
public sealed class PreProcessResult
{
    public bool IsValidationFailure { get; }
    public IReadOnlyCollection<RowValidationError> Errors { get; }
    public IReadOnlyCollection<PlanItem> Plan { get; }

    private PreProcessResult(bool isValidationFailure, IReadOnlyCollection<RowValidationError> errors, IReadOnlyCollection<PlanItem> plan)
        => (IsValidationFailure, Errors, Plan) = (isValidationFailure, errors, plan);

    public static PreProcessResult ValidationFailed(IReadOnlyCollection<RowValidationError> errors) =>
        new(true, errors, Array.Empty<PlanItem>());

    public static PreProcessResult Plan(IReadOnlyCollection<PlanItem> planItems) =>
        new(false, Array.Empty<RowValidationError>(), planItems);
}

/// <summary>Persistence port for UploadSession state. Implementations decide storage
/// technology and schema ownership entirely; the package has no opinion beyond this contract.</summary>
public interface IUploadStore
{
    Task SaveAsync(UploadSession session, CancellationToken ct);
    Task<UploadSession?> GetAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Used by StaleSessionReconciler to find sessions stuck in a given status past a threshold.</summary>
    Task<IReadOnlyCollection<UploadSession>> GetByStatusOlderThanAsync(
        UploadSessionStatus status, TimeSpan olderThan, CancellationToken ct);
}

/// <summary>Orchestration/scheduling port. BackgroundServiceUploadExecutor is the default,
/// single-pod, in-memory implementation; swap for a durable-queue implementation later
/// without changing anything above this port.</summary>
public interface IUploadExecutor
{
    Task EnqueuePreProcessingAsync(Guid sessionId, byte[] fileContent, CancellationToken ct);
    Task EnqueueRevalidationAsync(Guid sessionId, CancellationToken ct);
    Task EnqueueProcessingAsync(Guid sessionId, CancellationToken ct);
}

/// <summary>
/// Non-generic facade over one registered pipeline's typed components, letting
/// the orchestrator and executor drive any pipeline by string key without
/// knowing its TParsed type at the call site.
/// </summary>
public interface IUploadPipeline
{
    string Key { get; }
    Task<object> ParseAsync(Stream file, CancellationToken ct);
    Task<PreProcessResult> PreProcessAsync(object parsed, CancellationToken ct);
    Task<IReadOnlyCollection<PlanItemResult>> ProcessAsync(IReadOnlyCollection<PlanItem> confirmedPlan, bool isDryRun, CancellationToken ct);
    string SerializeParsedData(object parsed);
    object DeserializeParsedData(string json);

    /// <exception cref="NotSupportedException">Thrown when the pipeline has no registered IUploadCorrector.</exception>
    object ApplyCorrections(object parsed, IReadOnlyCollection<RowCorrection> corrections);
}

internal sealed class UploadPipeline<TParsed> : IUploadPipeline
{
    private readonly IUploadParser<TParsed> _parser;
    private readonly IUploadPreProcessor<TParsed> _preProcessor;
    private readonly IUploadProcessor _processor;
    private readonly IUploadCorrector<TParsed>? _corrector;

    public string Key { get; }

    public UploadPipeline(
        string key,
        IUploadParser<TParsed> parser,
        IUploadPreProcessor<TParsed> preProcessor,
        IUploadProcessor processor,
        IUploadCorrector<TParsed>? corrector)
    {
        Key = key;
        _parser = parser;
        _preProcessor = preProcessor;
        _processor = processor;
        _corrector = corrector;
    }

    public async Task<object> ParseAsync(Stream file, CancellationToken ct) => (await _parser.ParseAsync(file, ct))!;

    public Task<PreProcessResult> PreProcessAsync(object parsed, CancellationToken ct) =>
        _preProcessor.ProcessAsync((TParsed)parsed, ct);

    public Task<IReadOnlyCollection<PlanItemResult>> ProcessAsync(
        IReadOnlyCollection<PlanItem> confirmedPlan, bool isDryRun, CancellationToken ct) =>
        _processor.ExecuteAsync(confirmedPlan, isDryRun, ct);

    public string SerializeParsedData(object parsed) => JsonSerializer.Serialize((TParsed)parsed);

    public object DeserializeParsedData(string json) => JsonSerializer.Deserialize<TParsed>(json)!;

    public object ApplyCorrections(object parsed, IReadOnlyCollection<RowCorrection> corrections)
    {
        if (_corrector is null)
            throw new NotSupportedException($"Pipeline '{Key}' does not support in-place corrections; the file must be re-uploaded.");

        return _corrector.ApplyCorrections((TParsed)parsed, corrections)!;
    }
}

public interface IUploadPipelineRegistry
{
    /// <exception cref="InvalidOperationException">No pipeline is registered under this key.</exception>
    IUploadPipeline Resolve(string pipelineKey);
}

internal sealed class UploadPipelineRegistry : IUploadPipelineRegistry
{
    private readonly IEnumerable<IUploadPipeline> _pipelines;

    public UploadPipelineRegistry(IEnumerable<IUploadPipeline> pipelines) => _pipelines = pipelines;

    public IUploadPipeline Resolve(string pipelineKey) =>
        _pipelines.FirstOrDefault(p => p.Key == pipelineKey)
        ?? throw new InvalidOperationException(
            $"No upload pipeline registered for key '{pipelineKey}'. Check for a typo, or that AddPipeline was called for this key.");
}

// ----------------------------------------------------------------------------
// Application — UploadOrchestrator: the package's central coordination logic
// ----------------------------------------------------------------------------

/// <summary>
/// Coordinates the full session lifecycle for any registered pipeline:
/// Start -> PreProcess -> [ValidationFailed -> SubmitCorrections -> Revalidate]* -> Confirm -> Process.
/// This is the single place pipeline-agnostic orchestration logic lives; individual
/// pipelines never re-implement it.
/// </summary>
public sealed class UploadOrchestrator
{
    private readonly IUploadPipelineRegistry _registry;
    private readonly IUploadStore _store;
    private readonly IUploadExecutor _executor;

    public UploadOrchestrator(IUploadPipelineRegistry registry, IUploadStore store, IUploadExecutor executor)
    {
        _registry = registry;
        _store = store;
        _executor = executor;
    }

    /// <summary>Creates the session and enqueues parsing/pre-processing. Returns immediately (async 202 flow).</summary>
    public async Task<Guid> StartAsync(
        string pipelineKey, string ownerContext, string initiatedBy, byte[] fileContent, bool isDryRun, CancellationToken ct)
    {
        var session = UploadSession.Start(pipelineKey, ownerContext, initiatedBy, isDryRun);
        await _store.SaveAsync(session, ct);
        await _executor.EnqueuePreProcessingAsync(session.Id, fileContent, ct);
        return session.Id;
    }

    /// <summary>Invoked by an IUploadExecutor worker. Parses the file, persists the parsed
    /// snapshot, then runs pre-processing (validation + plan-building).</summary>
    public async Task RunPreProcessingAsync(Guid sessionId, byte[] fileContent, CancellationToken ct)
    {
        var session = await GetSessionOrThrow(sessionId, ct);
        var pipeline = _registry.Resolve(session.PipelineKey);

        try
        {
            using var stream = new MemoryStream(fileContent);
            var parsed = await pipeline.ParseAsync(stream, ct);
            session.SetParsedData(pipeline.SerializeParsedData(parsed));

            var result = await pipeline.PreProcessAsync(parsed, ct);
            ApplyPreProcessResult(session, result);
        }
        catch (Exception ex)
        {
            session.MarkFailed($"Pre-processing failed: {ex.Message}");
        }

        await _store.SaveAsync(session, ct);
    }

    /// <summary>Enhancement: submit field-level corrections for a validation failure, without
    /// needing the original file. Enqueues re-validation from the durably-stored parsed data.</summary>
    public async Task SubmitCorrectionsAsync(
        Guid sessionId, string principalId, IReadOnlyCollection<RowCorrection> corrections, CancellationToken ct)
    {
        var session = await GetSessionOrThrow(sessionId, ct);
        session.SubmitCorrections(principalId, corrections); // validates status/ownership, -> PreProcessing
        await _store.SaveAsync(session, ct);
        await _executor.EnqueueRevalidationAsync(sessionId, ct);
    }

    /// <summary>Invoked by an IUploadExecutor worker. Re-runs pre-processing from the stored
    /// parsed data (with pending corrections applied, if any) — no file re-upload required.</summary>
    public async Task RunRevalidationAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await GetSessionOrThrow(sessionId, ct);
        var pipeline = _registry.Resolve(session.PipelineKey);

        try
        {
            var parsed = pipeline.DeserializeParsedData(session.ParsedDataJson!);
            var corrected = session.PendingCorrections.Count > 0
                ? pipeline.ApplyCorrections(parsed, session.PendingCorrections)
                : parsed;

            session.SetParsedData(pipeline.SerializeParsedData(corrected));
            var result = await pipeline.PreProcessAsync(corrected, ct);
            ApplyPreProcessResult(session, result);
        }
        catch (Exception ex)
        {
            session.MarkFailed($"Re-validation failed: {ex.Message}");
        }

        await _store.SaveAsync(session, ct);
    }

    /// <summary>User confirms a (possibly partial) selection of the plan. Enqueues processing.</summary>
    public async Task ConfirmAsync(
        Guid sessionId, string principalId, IReadOnlyCollection<Guid> selectedItemIds, CancellationToken ct)
    {
        var session = await GetSessionOrThrow(sessionId, ct);
        session.Confirm(principalId, selectedItemIds);
        await _store.SaveAsync(session, ct);
        await _executor.EnqueueProcessingAsync(sessionId, ct);
    }

    /// <summary>Invoked by an IUploadExecutor worker. Executes the confirmed plan via the pipeline's processor.</summary>
    public async Task RunProcessingAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await GetSessionOrThrow(sessionId, ct);
        var pipeline = _registry.Resolve(session.PipelineKey);

        try
        {
            session.MarkProcessing();
            await _store.SaveAsync(session, ct);

            var results = await pipeline.ProcessAsync(session.Plan, session.IsDryRun, ct);
            session.MarkCompleted(results);
        }
        catch (Exception ex)
        {
            session.MarkFailed($"Processing failed: {ex.Message}");
        }

        await _store.SaveAsync(session, ct);
    }

    /// <summary>Session-level failure recovery. Re-enters confirmation with the same plan.</summary>
    public async Task RetryAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await GetSessionOrThrow(sessionId, ct);
        session.PrepareForRetry();
        await _store.SaveAsync(session, ct);
    }

    private static void ApplyPreProcessResult(UploadSession session, PreProcessResult result)
    {
        if (result.IsValidationFailure)
            session.FailValidation(result.Errors);
        else
            session.SetPlan(result.Plan);
    }

    private async Task<UploadSession> GetSessionOrThrow(Guid sessionId, CancellationToken ct) =>
        await _store.GetAsync(sessionId, ct)
        ?? throw new InvalidOperationException($"Upload session '{sessionId}' not found.");
}

namespace FlowX.Upload.Infrastructure;

using FlowX.Upload.Application;
using FlowX.Upload.Domain;

// ----------------------------------------------------------------------------
// Infrastructure — default (single-pod, in-memory) executor and reconciler
// ----------------------------------------------------------------------------

internal abstract record UploadWorkItem
{
    public sealed record PreProcess(Guid SessionId, byte[] FileContent) : UploadWorkItem;
    public sealed record Revalidate(Guid SessionId) : UploadWorkItem;
    public sealed record Process(Guid SessionId) : UploadWorkItem;

    private UploadWorkItem() { }
}

/// <summary>
/// Default IUploadExecutor: an in-process, single-pod, in-memory queue driven
/// by a BackgroundService. KNOWN LIMITATION: work items queued here (in
/// particular, raw file bytes for a not-yet-parsed upload) do not survive a
/// pod restart. StaleSessionReconciler mitigates this for every stage EXCEPT
/// the very first parse — see its remarks. Replace this implementation (only)
/// with a durable-queue-backed IUploadExecutor when horizontal scaling or a
/// stronger durability guarantee becomes available; nothing else in the
/// package needs to change.
/// </summary>
public sealed class BackgroundServiceUploadExecutor : BackgroundService, IUploadExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackgroundServiceUploadExecutor> _logger;
    private readonly System.Threading.Channels.Channel<UploadWorkItem> _queue =
        System.Threading.Channels.Channel.CreateUnbounded<UploadWorkItem>();

    public BackgroundServiceUploadExecutor(IServiceScopeFactory scopeFactory, ILogger<BackgroundServiceUploadExecutor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task EnqueuePreProcessingAsync(Guid sessionId, byte[] fileContent, CancellationToken ct) =>
        _queue.Writer.WriteAsync(new UploadWorkItem.PreProcess(sessionId, fileContent), ct).AsTask();

    public Task EnqueueRevalidationAsync(Guid sessionId, CancellationToken ct) =>
        _queue.Writer.WriteAsync(new UploadWorkItem.Revalidate(sessionId), ct).AsTask();

    public Task EnqueueProcessingAsync(Guid sessionId, CancellationToken ct) =>
        _queue.Writer.WriteAsync(new UploadWorkItem.Process(sessionId), ct).AsTask();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var scope = _scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<UploadOrchestrator>();

            try
            {
                switch (item)
                {
                    case UploadWorkItem.PreProcess p:
                        await orchestrator.RunPreProcessingAsync(p.SessionId, p.FileContent, stoppingToken);
                        break;
                    case UploadWorkItem.Revalidate r:
                        await orchestrator.RunRevalidationAsync(r.SessionId, stoppingToken);
                        break;
                    case UploadWorkItem.Process pr:
                        await orchestrator.RunProcessingAsync(pr.SessionId, stoppingToken);
                        break;
                }
            }
            catch (Exception ex)
            {
                // UploadOrchestrator already persists Failed for domain-level failures inside
                // its own try/catch; this is the last line of defense for truly unexpected exceptions
                // (e.g. the store itself being unreachable), which cannot be attributed to a session.
                _logger.LogError(ex, "Unhandled error processing upload work item {WorkItem}.", item);
            }
        }
    }
}

/// <summary>
/// Startup sweep compensating for the in-memory executor's loss of state
/// across restarts. Three cases:
///   - Processing (stale): no partial-resumability is offered by design ->
///     fail the session; the user re-confirms and retries from the same plan.
///   - Confirmed (stale): the plan is durable in the store, only the in-memory
///     enqueue was lost -> safely re-enqueue processing, no data was at risk.
///   - PreProcessing (stale) WITH ParsedDataJson set: parsing already
///     succeeded (initial parse, or a prior correction) and only the
///     pre-process/validate step was interrupted -> re-enqueue revalidation,
///     no file needed.
///   - PreProcessing (stale) WITHOUT ParsedDataJson: the raw file bytes only
///     ever lived in the in-memory queue and are unrecoverable -> fail with a
///     message asking the user to re-upload. This is the one gap not solved
///     by this package under the single-pod/no-durable-queue constraint.
/// </summary>
public sealed class StaleSessionReconciler : IHostedService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly IUploadStore _store;
    private readonly IUploadExecutor _executor;
    private readonly ILogger<StaleSessionReconciler> _logger;

    public StaleSessionReconciler(IUploadStore store, IUploadExecutor executor, ILogger<StaleSessionReconciler> logger)
    {
        _store = store;
        _executor = executor;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await ReconcileStaleProcessingAsync(ct);
        await ReconcileStaleConfirmedAsync(ct);
        await ReconcileStalePreProcessingAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task ReconcileStaleProcessingAsync(CancellationToken ct)
    {
        var sessions = await _store.GetByStatusOlderThanAsync(UploadSessionStatus.Processing, StaleAfter, ct);
        foreach (var session in sessions)
        {
            _logger.LogWarning("Failing orphaned session {SessionId}: interrupted mid-processing by restart.", session.Id);
            session.MarkFailed("Interrupted by process restart during execution.");
            await _store.SaveAsync(session, ct);
        }
    }

    private async Task ReconcileStaleConfirmedAsync(CancellationToken ct)
    {
        var sessions = await _store.GetByStatusOlderThanAsync(UploadSessionStatus.Confirmed, StaleAfter, ct);
        foreach (var session in sessions)
        {
            _logger.LogWarning("Re-enqueueing session {SessionId}: enqueue was lost by restart before processing started.", session.Id);
            await _executor.EnqueueProcessingAsync(session.Id, ct);
        }
    }

    private async Task ReconcileStalePreProcessingAsync(CancellationToken ct)
    {
        var sessions = await _store.GetByStatusOlderThanAsync(UploadSessionStatus.PreProcessing, StaleAfter, ct);
        foreach (var session in sessions)
        {
            if (session.ParsedDataJson is not null)
            {
                _logger.LogWarning("Re-enqueueing revalidation for {SessionId}: parsed data is durable.", session.Id);
                await _executor.EnqueueRevalidationAsync(session.Id, ct);
            }
            else
            {
                _logger.LogWarning("Failing orphaned session {SessionId}: never received file bytes.", session.Id);
                session.MarkFailed("Interrupted before the file could be processed; please re-upload.");
                await _store.SaveAsync(session, ct);
            }
        }
    }
}

namespace FlowX.Upload.DependencyInjection;

using FlowX.Upload.Application;
using FlowX.Upload.Infrastructure;

// ----------------------------------------------------------------------------
// DependencyInjection — registration entry points
// ----------------------------------------------------------------------------

public static class UploadServiceCollectionExtensions
{
    /// <summary>
    /// Registers the orchestrator, pipeline registry, default (single-pod,
    /// in-memory) executor, and the stale-session reconciler. You still need
    /// to register an IUploadStore (e.g. via FlowX.Upload.EntityFrameworkCore's
    /// AddEfCoreUploadStore, or your own implementation) and at least one
    /// pipeline via AddPipeline.
    /// </summary>
    public static UploadBuilder AddFlowXUpload(this IServiceCollection services)
    {
        services.TryAddScoped<IUploadPipelineRegistry, UploadPipelineRegistry>();
        services.TryAddScoped<UploadOrchestrator>();

        services.AddSingleton<BackgroundServiceUploadExecutor>();
        services.TryAddSingleton<IUploadExecutor>(sp => sp.GetRequiredService<BackgroundServiceUploadExecutor>());
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundServiceUploadExecutor>());
        services.AddHostedService<StaleSessionReconciler>();

        return new UploadBuilder(services);
    }
}

public sealed class UploadBuilder
{
    private readonly IServiceCollection _services;

    internal UploadBuilder(IServiceCollection services) => _services = services;

    /// <summary>Registers one pipeline under a stable string key. Consider exposing
    /// the key as a const string field in your application to reduce the risk of typos.</summary>
    public UploadBuilder AddPipeline<TParsed>(string key, Action<PipelineBuilder<TParsed>> configure)
    {
        var builder = new PipelineBuilder<TParsed>(_services, key);
        configure(builder);
        builder.Complete();
        return this;
    }
}

public sealed class PipelineBuilder<TParsed>
{
    private readonly IServiceCollection _services;
    private readonly string _key;
    private Type? _parserType;
    private Type? _preProcessorType;
    private Type? _processorType;
    private Type? _correctorType;

    internal PipelineBuilder(IServiceCollection services, string key)
    {
        _services = services;
        _key = key;
    }

    public PipelineBuilder<TParsed> UseParser<TParser>() where TParser : class, IUploadParser<TParsed>
    {
        _services.AddScoped<TParser>();
        _parserType = typeof(TParser);
        return this;
    }

    public PipelineBuilder<TParsed> UsePreProcessor<TPreProcessor>() where TPreProcessor : class, IUploadPreProcessor<TParsed>
    {
        _services.AddScoped<TPreProcessor>();
        _preProcessorType = typeof(TPreProcessor);
        return this;
    }

    public PipelineBuilder<TParsed> UseProcessor<TProcessor>() where TProcessor : class, IUploadProcessor
    {
        _services.AddScoped<TProcessor>();
        _processorType = typeof(TProcessor);
        return this;
    }

    /// <summary>Optional. Omit if this pipeline requires re-upload on validation failure.</summary>
    public PipelineBuilder<TParsed> UseCorrector<TCorrector>() where TCorrector : class, IUploadCorrector<TParsed>
    {
        _services.AddScoped<TCorrector>();
        _correctorType = typeof(TCorrector);
        return this;
    }

    internal void Complete()
    {
        if (_parserType is null || _preProcessorType is null || _processorType is null)
            throw new InvalidOperationException(
                $"Pipeline '{_key}' is missing a parser, pre-processor, or processor registration.");

        var key = _key;
        var parserType = _parserType;
        var preProcessorType = _preProcessorType;
        var processorType = _processorType;
        var correctorType = _correctorType;

        _services.AddScoped<IUploadPipeline>(sp => new UploadPipeline<TParsed>(
            key,
            (IUploadParser<TParsed>)sp.GetRequiredService(parserType),
            (IUploadPreProcessor<TParsed>)sp.GetRequiredService(preProcessorType),
            (IUploadProcessor)sp.GetRequiredService(processorType),
            correctorType is null ? null : (IUploadCorrector<TParsed>)sp.GetRequiredService(correctorType)));
    }
}

// ============================================================================
// FlowX.Upload.EntityFrameworkCore — optional reference IUploadStore
// ============================================================================
// Separate package: references FlowX.Upload + Microsoft.EntityFrameworkCore.
// Consumers are free to ignore this entirely and implement IUploadStore
// against their own persistence technology instead.

namespace FlowX.Upload.EntityFrameworkCore;

using System.Linq.Expressions;
using FlowX.Upload.Application;
using FlowX.Upload.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// Reference EF Core persistence for UploadSession. Maps the aggregate
/// directly (private setters bound by EF's default field/property access),
/// storing its collection-shaped members as JSON columns. Uses a dedicated
/// schema so the package's lifecycle is independent of any single service's
/// domain model migrations, even when colocated in the same physical database.
/// </summary>
public sealed class UploadDbContext : DbContext
{
    public UploadDbContext(DbContextOptions<UploadDbContext> options) : base(options) { }

    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UploadSession>(entity =>
        {
            entity.ToTable("UploadSessions", schema: "flowx_upload");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Status).HasConversion<string>();

            entity.Property(s => s.PendingCorrections).HasConversion(JsonConverter<IReadOnlyCollection<RowCorrection>>());
            entity.Property(s => s.ValidationErrors).HasConversion(JsonConverter<IReadOnlyCollection<RowValidationError>>());
            entity.Property(s => s.Plan).HasConversion(JsonConverter<IReadOnlyCollection<PlanItem>>());
            entity.Property(s => s.Results).HasConversion(JsonConverter<IReadOnlyCollection<PlanItemResult>>());

            entity.HasIndex(s => new { s.Status, s.LastActivityAt }); // supports GetByStatusOlderThanAsync
        });
    }

    private static ValueConverter<T, string> JsonConverter<T>() where T : class => new(
        value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
        json => JsonSerializer.Deserialize<T>(json, (JsonSerializerOptions?)null)!);
}

public sealed class EfUploadStore : IUploadStore
{
    private readonly UploadDbContext _db;

    public EfUploadStore(UploadDbContext db) => _db = db;

    public async Task SaveAsync(UploadSession session, CancellationToken ct)
    {
        var tracked = await _db.UploadSessions.FindAsync(new object[] { session.Id }, ct);
        if (tracked is null)
            _db.UploadSessions.Add(session);
        else
            _db.Entry(tracked).CurrentValues.SetValues(session);

        await _db.SaveChangesAsync(ct);
    }

    public Task<UploadSession?> GetAsync(Guid sessionId, CancellationToken ct) =>
        _db.UploadSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    public Task<IReadOnlyCollection<UploadSession>> GetByStatusOlderThanAsync(
        UploadSessionStatus status, TimeSpan olderThan, CancellationToken ct)
    {
        var threshold = DateTimeOffset.UtcNow - olderThan;
        return _db.UploadSessions
            .Where(s => s.Status == status && s.LastActivityAt < threshold)
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyCollection<UploadSession>)t.Result, ct);
    }
}

public static class EfCoreUploadServiceCollectionExtensions
{
    /// <summary>Registers UploadDbContext and EfUploadStore as the IUploadStore implementation.</summary>
    public static FlowX.Upload.DependencyInjection.UploadBuilder AddEfCoreUploadStore(
        this FlowX.Upload.DependencyInjection.UploadBuilder builder,
        IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDb)
    {
        services.AddDbContext<UploadDbContext>(configureDb);
        services.AddScoped<IUploadStore, EfUploadStore>();
        return builder;
    }
}