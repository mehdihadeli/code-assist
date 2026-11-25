using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Configuration;

namespace CodeAssist.Sessions;

public sealed class SettingsToolAccess(SettingsService settings) : IConfigurationAccess
{
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private static readonly HashSet<string> Editable = new(StringComparer.OrdinalIgnoreCase) { "appearance.animations" };

    public Task<JsonElement> ReadAsync(string? path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = settings.Current;
        var leaves = snapshot.Leaves().Where(leaf => path is null || leaf.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            leaf.Path.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase)).Select(leaf => new
        {
            leaf.Path, Value = leaf.Node.IsSecret || leaf.Node.Trust == TrustClass.Sensitive ? System.Text.Json.Nodes.JsonValue.Create("(restricted)") : leaf.Value,
            editable = Editable.Contains(leaf.Path) && !snapshot.Locks.Touches(leaf.Path),
            origin = leaf.Origin.Effective?.Tier.ToString(), locked = snapshot.Locks.Touches(leaf.Path),
        }).ToArray();
        if (path is not null && leaves.Length == 0) throw new ArgumentException("Unknown configuration path.");
        return Task.FromResult(JsonSerializer.SerializeToElement(new { revision = snapshot.Id, settings = leaves }));
    }

    public async Task<JsonElement> ChangeAsync(string operation, string path, JsonElement? value, string revision, CancellationToken cancellationToken)
    {
        if (operation is not ("set" or "unset") || !Editable.Contains(path)) throw new UnauthorizedAccessException("Model-callable edits are restricted to presentation preferences.");
        if (operation == "set" && value is null) throw new ArgumentException("Set requires a value.");
        await _mutation.WaitAsync(cancellationToken);
        try
        {
            if (settings.Current.Id != revision) throw new InvalidOperationException("Configuration changed; read it again before editing.");
            var editor = new SettingsEditor(settings);
            var result = operation == "set" ? editor.Set(path, value!.Value.GetRawText(), EditScope.User) : editor.Unset(path, EditScope.User);
            if (!result.Success) throw new InvalidOperationException(result.Error);
            return JsonSerializer.SerializeToElement(new { revision = settings.Current.Id, result.Success, result.Notice });
        }
        finally { _mutation.Release(); }
    }
}