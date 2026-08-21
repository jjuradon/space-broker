using FlowX.Upload.Application.Abstractions.Scheduling;
using FlowX.Upload.Application.Pipelines;
using FlowX.Upload.Application.UseCases;
using FlowX.Upload.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowX.Upload.DependencyInjection;

public static class UploadServiceCollectionExtensions
{
    /// <summary>
    /// Registers the orchestrator, pipeline registry, default (single-pod,
    /// in-memory) executor, and the stale-session reconciler. You still need
    /// to register an IUploadStore and at least one pipeline via AddPipeline.
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
