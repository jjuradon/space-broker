namespace FlowX.Upload.Infrastructure.Scheduling;

internal abstract record UploadWorkItem
{
    /// <summary>RENAMED from PreProcess — carries the file bytes for the combined Prepare+Parse+Plan pass.</summary>
    public sealed record Plan(Guid SessionId, byte[] FileContent) : UploadWorkItem;
    public sealed record Revalidate(Guid SessionId) : UploadWorkItem;
    public sealed record Process(Guid SessionId) : UploadWorkItem;

    private UploadWorkItem() { }
}
