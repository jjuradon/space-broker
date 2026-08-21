namespace FlowX.Upload.Application.Abstractions.Pipelines;

/// <summary>Stage 2 (Parse). Converts a raw file stream, plus the context resolved by
/// Prepare, into an application-defined intermediate shape.</summary>
public interface IUploadParser<TContext, TParsed>
{
    Task<TParsed> ParseAsync(Stream fileStream, TContext context, CancellationToken ct);
}
