using FlowX.Upload.Application.Abstractions.Pipelines;

namespace FlowX.Upload.Application.Pipelines.Defaults;

public sealed class EmptyContextPreparer : IUploadContextPreparer<EmptyUploadContext>
{
    public Task<EmptyUploadContext> PrepareAsync(Stream fileStream, string ownerContext, string initiatedBy, CancellationToken ct) =>
        Task.FromResult(new EmptyUploadContext());
}
