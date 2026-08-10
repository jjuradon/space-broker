namespace FlowX.Upload.DependencyInjection;

public static class UploadServiceCollectionExtensions
{
    public static UploadBuilder AddFlowXUpload(this IServiceCollection services)
    {
        services.TryAddScoped<IUploadPipelineRegistry, UploadPipelineRegistry>();
        services.TryAddScoped<UploadOrchestrator>();
        services.AddSingleton<BackgroundServiceUploadExecutor>();
        services.AddSingleton<IUploadExecutor>(sp => sp.GetRequiredService<BackgroundServiceUploadExecutor>());
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundServiceUploadExecutor>());
        services.AddHostedService<StaleSessionReconciler>();
        return new UploadBuilder(services);
    }
}

public sealed class UploadBuilder(IServiceCollection services)
{
    public UploadBuilder AddPipeline<TParsed>(string key, Action<PipelineBuilder<TParsed>> configure)
    {
        var builder = new PipelineBuilder<TParsed>(services, key);
        configure(builder);
        builder.Complete();
        return this;
    }
}

public sealed class PipelineBuilder<TParsed>(IServiceCollection services, string key)
{
    private Type? _parserType, _preProcessorType, _processorType;

    public PipelineBuilder<TParsed> UseParser<TParser>() where TParser : class, IUploadParser<TParsed>
    {
        services.AddScoped<TParser>();
        _parserType = typeof(TParser);
        return this;
    }

    public PipelineBuilder<TParsed> UsePreProcessor<TPreProcessor>() where TPreProcessor : class, IUploadPreProcessor<TParsed>
    {
        services.AddScoped<TPreProcessor>();
        _preProcessorType = typeof(TPreProcessor);
        return this;
    }

    public PipelineBuilder<TParsed> UseProcessor<TProcessor>() where TProcessor : class, IUploadProcessor
    {
        services.AddScoped<TProcessor>();
        _processorType = typeof(TProcessor);
        return this;
    }

    internal void Complete()
    {
        if (_parserType is null || _preProcessorType is null || _processorType is null)
            throw new InvalidOperationException(
                $"Pipeline '{key}' is missing a parser, pre-processor, or processor registration.");

        var (parserType, preProcessorType, processorType) = (_parserType, _preProcessorType, _processorType);

        services.AddScoped<IUploadPipeline>(sp => new UploadPipeline<TParsed>(
            key,
            (IUploadParser<TParsed>)sp.GetRequiredService(parserType),
            (IUploadPreProcessor<TParsed>)sp.GetRequiredService(preProcessorType),
            (IUploadProcessor)sp.GetRequiredService(processorType)));
    }
}