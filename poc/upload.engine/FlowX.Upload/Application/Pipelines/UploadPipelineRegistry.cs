namespace FlowX.Upload.Application.Pipelines;

internal sealed class UploadPipelineRegistry : IUploadPipelineRegistry
{
    private readonly IEnumerable<IUploadPipeline> _pipelines;

    public UploadPipelineRegistry(IEnumerable<IUploadPipeline> pipelines) => _pipelines = pipelines;

    public IUploadPipeline Resolve(string pipelineKey) =>
        _pipelines.FirstOrDefault(p => p.Key == pipelineKey)
        ?? throw new InvalidOperationException(
            $"No upload pipeline registered for key '{pipelineKey}'. Check for a typo, or that AddPipeline was called for this key.");
}
