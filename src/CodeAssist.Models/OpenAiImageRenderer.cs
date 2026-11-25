using CodeAssist.Tools;
using OpenAI.Images;

namespace CodeAssist.Models;

public sealed class OpenAiImageRenderer(IReadOnlyDictionary<string, ImageClient> clients, IReadOnlySet<string>? editableModels = null) : IImageRenderer
{
    public IReadOnlyList<ImageModel> Models { get; } = clients.Keys.Select(reference => new ImageModel(reference, editableModels?.Contains(reference) == true)).ToArray();

    public async Task<RenderedImage> RenderAsync(ImageRenderRequest request, CancellationToken cancellationToken)
    {
        if (!clients.TryGetValue(request.Model, out var client)) throw new ArgumentException("Image model is not configured.");
        GeneratedImageFormat? binaryOptions = request.Model.Contains("gpt-image", StringComparison.OrdinalIgnoreCase) ? (GeneratedImageFormat?)null : GeneratedImageFormat.Bytes;
        GeneratedImage image;
        if (request.References.Count == 0)
            image = await client.GenerateImageAsync(request.Prompt, new ImageGenerationOptions { ResponseFormat = binaryOptions }, cancellationToken);
        else
        {
            if (request.References.Count != 1 || !Models.Single(model => model.Reference == request.Model).CanEdit)
                throw new ArgumentException("This image adapter supports one reference image for editable models.");
            using var stream = new MemoryStream(request.References[0].Data, writable: false);
            image = await client.GenerateImageEditAsync(stream, "reference.png", request.Prompt, new ImageEditOptions { ResponseFormat = binaryOptions }, cancellationToken);
        }
        if (image.ImageBytes is null) throw new InvalidOperationException("Image provider did not return binary data; remote URLs are not followed.");
        return new("image/png", image.ImageBytes.ToArray());
    }
}