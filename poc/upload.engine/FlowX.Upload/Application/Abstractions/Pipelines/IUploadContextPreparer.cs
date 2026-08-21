namespace FlowX.Upload.Application.Abstractions.Pipelines;

/// <summary>Stage 1 (Prepare). Resolves pipeline-specific context from the raw file —
/// runs once, immediately before Parse, in the same pass while the file stream
/// is available. TContext must be a plain serializable type (no open resources,
/// no Stream references) since it is persisted as JSON.</summary>
public interface IUploadContextPreparer<TContext>
{
    Task<TContext> PrepareAsync(Stream fileStream, string ownerContext, string initiatedBy, CancellationToken ct);
}
