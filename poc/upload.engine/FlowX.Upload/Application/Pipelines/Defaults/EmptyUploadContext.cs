namespace FlowX.Upload.Application.Pipelines.Defaults;

/// <summary>Package-provided default for pipelines that don't need extra context
/// beyond the file itself — keeps Prepare a uniform, required stage without
/// forcing meaningless boilerplate on every pipeline.</summary>
public sealed record EmptyUploadContext;
