namespace CodeAssist.Tools;

public interface ITextEmbeddings
{
    string Name { get; }
    string? Destination { get; }
    Task<IReadOnlyList<float[]>> GenerateAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}