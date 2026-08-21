using Microsoft.Extensions.DependencyInjection;

namespace FlowX.Upload.DependencyInjection;

public sealed class UploadBuilder
{
    private readonly IServiceCollection _services;

    internal UploadBuilder(IServiceCollection services) => _services = services;

    /// <summary>Registers one pipeline under a stable string key. Consider exposing
    /// the key as a const string field in your application to reduce the risk of typos.</summary>
    public UploadBuilder AddPipeline<TContext, TParsed>(string key, Action<PipelineBuilder<TContext, TParsed>> configure)
    {
        var builder = new PipelineBuilder<TContext, TParsed>(_services, key);
        configure(builder);
        builder.Complete();
        return this;
    }
}
