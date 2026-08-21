using FlowX.Upload.Domain.Entities;
using FlowX.Upload.Domain.ValueObjects;
using FlowX.Upload.Application.Models;

namespace FlowX.Upload.Application.Pipelines;

/// <summary>
/// Non-generic facade over one registered pipeline's typed components, letting
/// the orchestrator and executor drive any pipeline by string key without
/// knowing its TContext/TParsed types at the call site.
/// PlanAsync/ApplyCorrections RENAMED from PreProcessAsync (matches the
/// IUploadPlanner rename).
/// </summary>
public interface IUploadPipeline
{
    string Key { get; }
    Task<object> PrepareAsync(Stream fileStream, string ownerContext, string initiatedBy, CancellationToken ct);
    Task<object> ParseAsync(Stream fileStream, object context, CancellationToken ct);
    Task<PlanResult> PlanAsync(object parsed, CancellationToken ct);
    Task<IReadOnlyCollection<PlanItemResult>> ProcessAsync(IReadOnlyCollection<PlanItem> confirmedPlan, bool isDryRun, CancellationToken ct);

    string SerializeContext(object context);
    object DeserializeContext(string json);
    string SerializeParsedData(object parsed);
    object DeserializeParsedData(string json);

    /// <exception cref="NotSupportedException">No IUploadCorrector registered for this pipeline.</exception>
    object ApplyCorrections(object parsed, object context, IReadOnlyCollection<RowCorrection> corrections);
}
