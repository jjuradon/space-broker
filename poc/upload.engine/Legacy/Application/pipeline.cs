namespace FlowX.Upload.Application;

public interface IUploadParser<TParsed>
{
    Task<TParsed> ParseAsync(Stream file, CancellationToken ct);
}

public interface IUploadPreProcessor<TParsed>
{
    Task<PreProcessResult> ProcessAsync(TParsed parsed, CancellationToken ct);
}

public interface IUploadProcessor
{
    Task<IReadOnlyCollection<PlanItemResult>> ExecuteAsync(IReadOnlyCollection<PlanItem> confirmedPlan, CancellationToken ct);
}

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

/// Non-generic facade so the orchestrator can drive any pipeline by key,
/// without knowing its TParsed type at the call site.
public interface IUploadPipeline
{
    string Key { get; }
    Task<object> ParseAsync(Stream file, CancellationToken ct);
    Task<PreProcessResult> PreProcessAsync(object parsed, CancellationToken ct);
    Task<IReadOnlyCollection<PlanItemResult>> ProcessAsync(IReadOnlyCollection<PlanItem> confirmedPlan, CancellationToken ct);
}

internal sealed class UploadPipeline<TParsed>(
    string key,
    IUploadParser<TParsed> parser,
    IUploadPreProcessor<TParsed> preProcessor,
    IUploadProcessor processor) : IUploadPipeline
{
    public string Key { get; } = key;

    public async Task<object> ParseAsync(Stream file, CancellationToken ct) =>
        (await parser.ParseAsync(file, ct))!;

    public Task<PreProcessResult> PreProcessAsync(object parsed, CancellationToken ct) =>
        preProcessor.ProcessAsync((TParsed)parsed, ct);

    public Task<IReadOnlyCollection<PlanItemResult>> ProcessAsync(IReadOnlyCollection<PlanItem> confirmedPlan, CancellationToken ct) =>
        processor.ExecuteAsync(confirmedPlan, ct);
}

public interface IUploadPipelineRegistry
{
    IUploadPipeline Resolve(string pipelineKey);
}

internal sealed class UploadPipelineRegistry(IEnumerable<IUploadPipeline> pipelines) : IUploadPipelineRegistry
{
    public IUploadPipeline Resolve(string pipelineKey) =>
        pipelines.FirstOrDefault(p => p.Key == pipelineKey)
        ?? throw new InvalidOperationException($"No upload pipeline registered for key '{pipelineKey}'.");
}