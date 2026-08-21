using System.Text.Json;
using FlowX.Upload.Domain.Aggregates;
using FlowX.Upload.Domain.Entities;
using FlowX.Upload.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FlowX.Upload.EntityFrameworkCore;

/// <summary>
/// Reference EF Core persistence for UploadSession.
///
/// BREAKING CHANGE / MIGRATION REQUIRED for existing deployments: Status is
/// mapped via HasConversion&lt;string&gt;(), which persists the C# enum
/// member NAME as text. The Domain rename of UploadSessionStatus.PreProcessing
/// to Planning means any row currently stored with Status = "PreProcessing"
/// will fail to deserialize once this version is deployed.
///
/// Before deploying this version against an existing database, run:
///   UPDATE flowx_upload."UploadSessions" SET "Status" = 'Planning' WHERE "Status" = 'PreProcessing';
/// (adjust schema/table/column casing to your actual migration history).
/// No other stored values are affected — every other status name is unchanged.
/// </summary>
public sealed class UploadDbContext : DbContext
{
    public UploadDbContext(DbContextOptions<UploadDbContext> options) : base(options) { }

    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UploadSession>(entity =>
        {
            entity.ToTable("UploadSessions", schema: "flowx_upload");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Status).HasConversion<string>();

            entity.Property(s => s.PendingCorrections).HasConversion(JsonConverter<IReadOnlyCollection<RowCorrection>>());
            entity.Property(s => s.ValidationErrors).HasConversion(JsonConverter<IReadOnlyCollection<RowValidationError>>());
            entity.Property(s => s.Plan).HasConversion(JsonConverter<IReadOnlyCollection<PlanItem>>());
            entity.Property(s => s.Results).HasConversion(JsonConverter<IReadOnlyCollection<PlanItemResult>>());

            entity.HasIndex(s => new { s.Status, s.LastActivityAt }); // supports GetByStatusOlderThanAsync
        });
    }

    private static ValueConverter<T, string> JsonConverter<T>() where T : class => new(
        value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
        json => JsonSerializer.Deserialize<T>(json, (JsonSerializerOptions?)null)!);
}
