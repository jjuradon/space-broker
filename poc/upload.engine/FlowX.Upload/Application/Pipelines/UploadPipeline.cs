using System.Text.Json;
using FlowX.Upload.Application.Abstractions.Pipelines;
using FlowX.Upload.Application.Models;
using FlowX.Upload.Domain.Entities;
using FlowX.Upload.Domain.ValueObjects;

namespace FlowX.Upload.Application.Pipelines;

internal sealed class UploadPipeline<TContext, TParsed> : IUploadPipeline
{
    private readonly IUploadContextPreparer<TContext> _preparer;
    private readonly IUploadParser<TContext, TParsed> _parser;
    private readonly IUploadPlanner<TParsed> _planner;
    private readonly IUploadProcessor _processor;
    private readonly IUploadCorrector<TContext, TParsed>? _corrector;

    public string Key { get; }

    public UploadPipeline(
        string key,
        IUploadContextPreparer<TContext> preparer,
        IUploadParser<TContext, TParsed> parser,
        IUploadPlanner<TParsed> planner,
        IUploadProcessor processor,
        IUploadCorrector<TContext, TParsed>? corrector)
    {
        Key = key;
        _preparer = preparer;
        _parser = parser;
        _planner = planner;
        _processor = processor;
        _corrector = corrector;
    }

    public async Task<object> PrepareAsync(Stream fileStream, string ownerContext, string initiatedBy, CancellationToken ct) =>
        (await _preparer.PrepareAsync(fileStream, ownerContext, initiatedBy, ct))!;

    public async Task<object> ParseAsync(Stream fileStream, object context, CancellationToken ct) =>
        (await _parser.ParseAsync(fileStream, (TContext)context, ct))!;

    public Task<PlanResult> PlanAsync(object parsed, CancellationToken ct) =>
        _planner.PlanAsync((TParsed)parsed, ct);

    public Task<IReadOnlyCollection<PlanItemResult>> ProcessAsync(
        IReadOnlyCollection<PlanItem> confirmedPlan, bool isDryRun, CancellationToken ct) =>
        _processor.ExecuteAsync(confirmedPlan, isDryRun, ct);

    public string SerializeContext(object context) => JsonSerializer.Serialize((TContext)context);
    public object DeserializeContext(string json) => JsonSerializer.Deserialize<TContext>(json)!;

    public string SerializeParsedData(object parsed) => JsonSerializer.Serialize((TParsed)parsed);
    public object DeserializeParsedData(string json) => JsonSerializer.Deserialize<TParsed>(json)!;

    public object ApplyCorrections(object parsed, object context, IReadOnlyCollection<RowCorrection> corrections)
    {
        if (_corrector is null)
            throw new NotSupportedException($"Pipeline '{Key}' does not support in-place corrections; the file must be re-uploaded.");

        return _corrector.ApplyCorrections((TParsed)parsed, (TContext)context, corrections)!;
    }
}
