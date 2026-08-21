using FlowX.Upload.Application.Abstractions.Persistence;
using FlowX.Upload.Domain.Aggregates;
using FlowX.Upload.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace FlowX.Upload.EntityFrameworkCore;

public sealed class EfUploadStore : IUploadStore
{
    private readonly UploadDbContext _db;

    public EfUploadStore(UploadDbContext db) => _db = db;

    public async Task SaveAsync(UploadSession session, CancellationToken ct)
    {
        var tracked = await _db.UploadSessions.FindAsync(new object[] { session.Id }, ct);
        if (tracked is null)
            _db.UploadSessions.Add(session);
        else
            _db.Entry(tracked).CurrentValues.SetValues(session);

        await _db.SaveChangesAsync(ct);
    }

    public Task<UploadSession?> GetAsync(Guid sessionId, CancellationToken ct) =>
        _db.UploadSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    public Task<IReadOnlyCollection<UploadSession>> GetByStatusOlderThanAsync(
        UploadSessionStatus status, TimeSpan olderThan, CancellationToken ct)
    {
        var threshold = DateTimeOffset.UtcNow - olderThan;
        return _db.UploadSessions
            .Where(s => s.Status == status && s.LastActivityAt < threshold)
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyCollection<UploadSession>)t.Result, ct);
    }
}
