using FlowX.Upload.Domain.Aggregates;
using FlowX.Upload.Domain.Enums;

namespace FlowX.Upload.Application.Abstractions.Persistence;

/// <summary>Persistence port for UploadSession state. Implementations decide storage
/// technology and schema ownership entirely; the package has no opinion beyond this contract.
/// Note: GetByStatusOlderThanAsync exists to serve StaleSessionReconciler's operational
/// recovery sweep, not a domain invariant of UploadSession itself — that's why this
/// port lives in Application, not as a Domain-layer repository interface.</summary>
public interface IUploadStore
{
    Task SaveAsync(UploadSession session, CancellationToken ct);
    Task<UploadSession?> GetAsync(Guid sessionId, CancellationToken ct);
    Task<IReadOnlyCollection<UploadSession>> GetByStatusOlderThanAsync(
        UploadSessionStatus status, TimeSpan olderThan, CancellationToken ct);
}
