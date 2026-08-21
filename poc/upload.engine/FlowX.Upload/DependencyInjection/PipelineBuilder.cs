using FlowX.Upload.Application.Abstractions.Pipelines;
using FlowX.Upload.Application.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace FlowX.Upload.DependencyInjection;

public sealed class PipelineBuilder<TContext, TParsed>
{
    private readonly IServiceCollection _services;
    private readonly string _key;
    private Type? _preparerType;
    private Type? _parserType;
    private Type? _plannerType;
    private Type? _processorType;
    private Type? _correctorType;

    internal PipelineBuilder(IServiceCollection services, string key)
    {
        _services = services;
        _key = key;
    }

    public PipelineBuilder<TContext, TParsed> UsePreparer<TPreparer>() where TPreparer : class, IUploadContextPreparer<TContext>
    {
        _services.AddScoped<TPreparer>();
        _preparerType = typeof(TPreparer);
        return this;
    }

    public PipelineBuilder<TContext, TParsed> UseParser<TParser>() where TParser : class, IUploadParser<TContext, TParsed>
    {
        _services.AddScoped<TParser>();
        _parserType = typeof(TParser);
        return this;
    }

    /// <summary>RENAMED from UsePreProcessor.</summary>
    public PipelineBuilder<TContext, TParsed> UsePlanner<TPlanner>() where TPlanner : class, IUploadPlanner<TParsed>
    {
        _services.AddScoped<TPlanner>();
        _plannerType = typeof(TPlanner);
        return this;
    }

    public PipelineBuilder<TContext, TParsed> UseProcessor<TProcessor>() where TProcessor : class, IUploadProcessor
    {
        _services.AddScoped<TProcessor>();
        _processorType = typeof(TProcessor);
        return this;
    }

    /// <summary>Optional. Omit if this pipeline requires re-upload on validation failure.</summary>
    public PipelineBuilder<TContext, TParsed> UseCorrector<TCorrector>() where TCorrector : class, IUploadCorrector<TContext, TParsed>
    {
        _services.AddScoped<TCorrector>();
        _correctorType = typeof(TCorrector);
        return this;
    }

    internal void Complete()
    {
        if (_preparerType is null || _parserType is null || _plannerType is null || _processorType is null)
            throw new InvalidOperationException(
                $"Pipeline '{_key}' is missing a preparer, parser, planner, or processor registration.");

        var (key, preparerType, parserType, plannerType, processorType, correctorType) =
            (_key, _preparerType, _parserType, _plannerType, _processorType, _correctorType);

        _services.AddScoped<IUploadPipeline>(sp => new UploadPipeline<TContext, TParsed>(
            key,
            (IUploadContextPreparer<TContext>)sp.GetRequiredService(preparerType),
            (IUploadParser<TContext, TParsed>)sp.GetRequiredService(parserType),
            (IUploadPlanner<TParsed>)sp.GetRequiredService(plannerType),
            (IUploadProcessor)sp.GetRequiredService(processorType),
            correctorType is null ? null : (IUploadCorrector<TContext, TParsed>)sp.GetRequiredService(correctorType)));
    }
}
