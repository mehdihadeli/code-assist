namespace CodeAssist.Tools;

public sealed record ImageModel(string Reference, bool CanEdit, int MaxReferences = 1);
public sealed record ImageRenderRequest(string Model, string Prompt, IReadOnlyList<ToolAttachment> References);
public sealed record RenderedImage(string MimeType, byte[] Data);

public interface IImageRenderer
{
    IReadOnlyList<ImageModel> Models { get; }
    Task<RenderedImage> RenderAsync(ImageRenderRequest request, CancellationToken cancellationToken);
}