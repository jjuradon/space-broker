using FlowX.Upload.Application.Models;

namespace FlowX.Upload.Application.Abstractions.Pipelines;

/// <summary>
/// Stage 3 (Plan). RENAMED from IUploadPreProcessor — validates the parsed
/// data and, if valid, builds the proposed plan of changes. The rename
/// removes the naming collision this previously had with IUploadProcessor
/// (Stage 4): "PreProcessor" vs "Processor" read as near-duplicates: "Planner"
/// vs "Processor" do not.
/// </summary>
public interface IUploadPlanner<TParsed>
{
    Task<PlanResult> PlanAsync(TParsed parsed, CancellationToken ct);
}
