using CodeAssist.Persistence;
using System.Text;
using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Sessions;

public sealed class JournalStructuredOutput : IStructuredOutput
{
    private readonly IJournalStore _journal;
    private readonly ToolInputSchema _schema;
    public string Contract { get; }
    public string InputSchema { get; }

    public JournalStructuredOutput(IJournalStore journal, string contract, string inputSchema)
    {
        if (string.IsNullOrWhiteSpace(contract) || contract.Length > 120 || contract.Any(char.IsControl)) throw new ArgumentException("Output contract needs a bounded identifier.");
        _journal = journal;
        Contract = contract;
        InputSchema = inputSchema;
        _schema = new ToolInputSchema(inputSchema);
    }

    public async Task<OutputReceipt> SubmitAsync(JsonElement value, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(context.CallId) || context.CallId.Length > 200 || context.CallId.Any(char.IsControl))
            throw new ArgumentException("Structured output requires a real session and bounded call ID.");
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > context.Limits.OutputMaxBytes) throw new ArgumentException("Structured result exceeds output byte budget.");
        _schema.Validate(value);
        var snapshot = value.Clone();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var entries = await _journal.ReadAsync(context.SessionId, cancellationToken);
            if (entries.FirstOrDefault() is not SessionStarted) throw new IOException("Session does not exist.");
            if (entries.OfType<StructuredResultSubmitted>().FirstOrDefault(entry => entry.Contract == Contract) is { } existing)
            {
                if (existing.CallId == context.CallId && JsonElement.DeepEquals(existing.Value, snapshot)) return new(Contract, existing.Seq, true);
                throw new InvalidOperationException("This output contract already has a committed result in the current session.");
            }
            try
            {
                var entry = await _journal.AppendAsync(new StructuredResultSubmitted(context.SessionId, 0, default, Contract, context.CallId, snapshot), entries[^1].Seq, cancellationToken);
                return new(Contract, entry.Seq, false);
            }
            catch (JournalConflictException) when (attempt < 3) { }
        }
        throw new JournalConflictException();
    }
}