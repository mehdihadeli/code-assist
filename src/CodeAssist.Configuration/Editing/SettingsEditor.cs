using System.Text.Json.Nodes;

namespace CodeAssist.Configuration;

public enum EditScope { User, Project }

public sealed record EditResult(
    bool Success,
    string? Error,
    string? File,
    IReadOnlyList<SettingsDiagnostic> Diagnostics,
    string? Notice = null)
{
    public static EditResult Fail(string error, string? file = null, IReadOnlyList<SettingsDiagnostic>? diagnostics = null) =>
        new(false, error, file, diagnostics ?? []);
}

/// <summary>Validated, comment-preserving edits of settings files. A write that would make the settings invalid is rolled back.</summary>
public sealed class SettingsEditor(SettingsService service)
{
    public EditResult Set(string path, string rawValue, EditScope scope) => SetMany([(path, rawValue)], scope);

    /// <summary>Applies several keys in one write and one reload; all succeed or none are kept.</summary>
    public EditResult SetMany(IReadOnlyList<(string Path, string Value)> items, EditScope scope)
    {
        var edits = new List<(string[] Segments, JsonNode Value)>();
        Target? first = null;
        foreach (var (path, rawValue) in items)
        {
            var (target, error) = Resolve(path, scope);
            if (target is null) return EditResult.Fail(error!);
            first ??= target;

            var diags = new DiagnosticBag();
            var node = OverrideTree.ValueFor(target.Node, rawValue);
            var (ok, value) = new Canonicalizer(KeyCatalog.Default, diags).RunNode(node, target.Node, target.Segments, SettingsTier.User, "edit");
            if (!ok || value is null)
            {
                // The summary names the input; the diagnostics carry the reason. Keeping them distinct avoids printing the same text twice.
                var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
                return EditResult.Fail($"'{(target.Node.IsSecret ? "(secret)" : rawValue)}' is not valid for {path}.", diagnostics: errors);
            }
            edits.Add((target.Segments, value));
        }
        if (first is null) return EditResult.Fail("Nothing to set.");

        return Write(first, text => edits.Aggregate(text, (current, edit) => JsoncPatcher.Set(current, edit.Segments, edit.Value.DeepClone())),
            items.Count == 1 ? $"set {first.Text}" : $"set {items.Count} keys");
    }

    public EditResult Unset(string path, EditScope scope)
    {
        var (target, error) = Resolve(path, scope);
        if (target is null) return EditResult.Fail(error!);
        if (!File.Exists(target.File)) return EditResult.Fail($"{target.Text} is not set in {target.File}.", target.File);
        return Write(target, text => JsoncPatcher.Remove(text, target.Segments), $"unset {target.Text}");
    }

    private sealed record Target(string Text, string[] Segments, KeyNode Node, string File, EditScope Scope);

    private (Target? Target, string? Error) Resolve(string path, EditScope scope)
    {
        IReadOnlyList<string> parsed;
        try { parsed = SettingsPath.Parse(path); }
        catch (FormatException ex) { return (null, ex.Message); }

        var catalog = KeyCatalog.Default;
        var node = catalog.Root;
        foreach (var segment in parsed)
        {
            if (node.Kind == KeyKind.List) return (null, $"'{path}' addresses a list item. Set the whole list instead.");
            var next = node.Step(segment);
            if (next is null) return (null, $"'{path}' is not a setting.");
            node = next;
        }

        var resolved = catalog.Canonicalize(parsed, out var canonical)!;
        var text = SettingsPath.Format(canonical);

        if (resolved.IsSecret && !EnvironmentCredential.IsProviderKey(canonical))
            return (null, $"{text} is a secret and is never written to a file. Set the environment variable instead.");

        var trustError = (scope, resolved.Trust) switch
        {
            (_, TrustClass.ManagedOnly) => $"{text} can only be set in the managed settings file by an administrator.",
            (EditScope.Project, TrustClass.Sensitive) => $"{text} is sensitive and cannot be set from a project file.",
            (EditScope.Project, TrustClass.UserOnly) => $"{text} is a personal setting and cannot be set from a project file.",
            _ => null,
        };
        if (trustError is not null) return (null, trustError);

        if (service.Current.Locks.Touches(text)) return (null, $"{text} is locked by managed settings.");

        var file = scope == EditScope.User ? service.Locations.UserFile : service.Locations.ProjectFileForWrite();
        return (new Target(text, canonical, resolved, file, scope), null);
    }

    private EditResult Write(Target target, Func<string, string> patch, string action)
    {
        var existed = File.Exists(target.File);
        var original = existed ? File.ReadAllText(target.File) : "";

        string patched;
        try { patched = patch(original); }
        catch (FormatException ex) { return EditResult.Fail(ex.Message, target.File); }

        if (patched == original) return new EditResult(true, null, target.File, [], "No change.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target.File)!);
            AtomicWrite(target.File, patched);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EditResult.Fail($"Cannot write {target.File}: {ex.Message}", target.File);
        }

        var result = service.Reload();
        if (!result.Accepted)
        {
            if (existed) AtomicWrite(target.File, original);
            else File.Delete(target.File);
            service.Reload();
            return EditResult.Fail($"Rolled back: {action} would make the settings invalid.", target.File, service.Current.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray());
        }

        var snapshot = result.Snapshot;
        var origin = snapshot.OriginOf(target.Text).Effective;
        var notice = origin is not null && origin.Tier > (target.Scope == EditScope.User ? SettingsTier.User : SettingsTier.Project)
            ? $"Saved, but the effective value comes from {origin.Tier} ({origin.Source})."
            : null;
        return new EditResult(true, null, target.File, snapshot.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToArray(), notice);
    }

    private static void AtomicWrite(string file, string text)
    {
        var temp = file + ".tmp" + Environment.ProcessId;
        File.WriteAllText(temp, text);
        File.Move(temp, file, overwrite: true);
    }
}
