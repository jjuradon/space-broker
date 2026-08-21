namespace FlowX.Upload.Application.Pipelines;

public interface IUploadPipelineRegistry
{
    /// <exception cref="InvalidOperationException">No pipeline is registered under this key.</exception>
    IUploadPipeline Resolve(string pipelineKey);
}
