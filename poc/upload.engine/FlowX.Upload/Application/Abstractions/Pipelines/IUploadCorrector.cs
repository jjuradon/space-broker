namespace FlowX.Upload.Application.Abstractions.Pipelines;

/// <summary>Optional. Applies UI-submitted field corrections to parsed data without needing
/// the original file. A pipeline that always forces re-upload on validation failure does
/// not need to implement this. Receives TContext — never the file stream, since corrections
/// only ever run after Prepare+Parse have already completed and persisted both.</summary>
public interface IUploadCorrector<TContext, TParsed>
{
    /// <remarks>Implementations must be idempotent — a correction submitted twice must not double-apply.</remarks>
    TParsed ApplyCorrections(TParsed parsed, TContext context, IReadOnlyCollection<Domain.ValueObjects.RowCorrection> corrections);
}
