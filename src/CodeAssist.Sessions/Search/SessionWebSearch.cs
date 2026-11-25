using System.Collections.Concurrent;
using CodeAssist.Tools;

namespace CodeAssist.Sessions;

public sealed class SessionWebSearch : IWebSearch
{
    private readonly IReadOnlyDictionary<string, IWebSearch> _providers;
    private readonly IUserInteraction _interaction;
    private readonly string? _preferred;
    private readonly ConcurrentDictionary<Guid, string> _choices = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _selection = new();
    public string ProviderId => "session-selected";
    public IReadOnlyList<string> ProviderIds => _providers.Keys.Order(StringComparer.Ordinal).ToArray();

    public SessionWebSearch(IEnumerable<IWebSearch> providers, IUserInteraction interaction, string? preferred = null)
    {
        _providers = providers.ToDictionary(provider => provider.ProviderId, StringComparer.Ordinal);
        if (_providers.Count is < 1 or > 32 || _providers.ContainsKey("Disable for session")) throw new ArgumentException("Supply 1-32 uniquely named search providers.");
        if (preferred is not null && !_providers.ContainsKey(preferred)) throw new ArgumentException("Configured search provider is unavailable.");
        _interaction = interaction;
        _preferred = preferred;
    }

    public Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, CancellationToken cancellationToken) => throw new InvalidOperationException("Search selection requires a session context.");

    public async Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, ToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionId == Guid.Empty) throw new ArgumentException("Search requires a real session.");
        string? selected = _preferred;
        if (selected is null)
        {
            SemaphoreSlim selection;
            lock (_selection)
            {
                if (!_selection.TryGetValue(context.SessionId, out selection!))
                {
                    if (_selection.Count >= 4096) throw new InvalidOperationException("Search session capacity reached.");
                    _selection[context.SessionId] = selection = new(1, 1);
                }
            }
            await selection.WaitAsync(cancellationToken);
            try
            {
                if (!_choices.TryGetValue(context.SessionId, out selected))
                {
                    var options = ProviderIds.Select(id => new QuestionOption(id)).Append(new("Disable for session")).ToArray();
                    var answers = await _interaction.AskAsync([new("Search provider", "Choose where this session sends search queries.", options, false)], context, cancellationToken);
                    if (answers is not { Count: 1 } || answers[0].Choices.Count != 1) throw new InvalidOperationException("Search provider selection was dismissed; no query was sent.");
                    selected = answers[0].Choices[0];
                    if (selected != "Disable for session" && !_providers.ContainsKey(selected)) throw new UnauthorizedAccessException("Search provider selection is invalid.");
                    _choices[context.SessionId] = selected;
                }
            }
            finally { selection.Release(); }
        }
        if (selected == "Disable for session") throw new InvalidOperationException("Web search is disabled for this session.");
        return await _providers[selected].SearchAsync(query, context, cancellationToken);
    }
}