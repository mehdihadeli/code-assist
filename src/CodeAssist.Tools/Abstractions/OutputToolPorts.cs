using System.Text.Json;

namespace CodeAssist.Tools;

public sealed record OutputReceipt(string Contract, long Sequence, bool Replayed);

public interface IStructuredOutput
{
    string Contract { get; }
    string InputSchema { get; }
    Task<OutputReceipt> SubmitAsync(JsonElement value, ToolContext context, CancellationToken cancellationToken);
}