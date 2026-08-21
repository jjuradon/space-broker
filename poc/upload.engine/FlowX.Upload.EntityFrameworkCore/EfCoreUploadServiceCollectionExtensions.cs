using FlowX.Upload.Application.Abstractions.Persistence;
using FlowX.Upload.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlowX.Upload.EntityFrameworkCore;

public static class EfCoreUploadServiceCollectionExtensions
{
    /// <summary>Registers UploadDbContext and EfUploadStore as the IUploadStore implementation.</summary>
    public static UploadBuilder AddEfCoreUploadStore(
        this UploadBuilder builder,
        IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDb)
    {
        services.AddDbContext<UploadDbContext>(configureDb);
        services.AddScoped<IUploadStore, EfUploadStore>();
        return builder;
    }
}
