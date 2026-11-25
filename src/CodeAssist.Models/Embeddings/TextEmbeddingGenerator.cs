using CodeAssist.Tools;
using Microsoft.Extensions.AI;

namespace CodeAssist.Models;

public sealed class TextEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> generator, string name, string? destination) : ITextEmbeddings
{
    public string Name { get; } = name;
    public string? Destination { get; } = destination;

    public async Task<IReadOnlyList<float[]>> GenerateAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var embeddings = await generator.GenerateAsync(texts, cancellationToken: cancellationToken);
        return embeddings.Select(embedding => embedding.Vector.ToArray()).ToArray();
    }
}