using System.Text.Json;
using System.Runtime.CompilerServices;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public enum ToolEffectKind { Read, Write, Delete, Exec, Net, Ask }

/// <summary>What a call would touch. The policy engine decides on effects, never on tool names.</summary>
public sealed record ToolEffect(ToolEffectKind Kind, string Target, bool Outside = false, bool Protected = false);

/// <summary>Static description of a tool. <see cref="Tags"/> are the capability tags roles allow or deny (read, write, exec, net, delegate).</summary>
public sealed record ToolSpec(string Name, string Description, string InputSchema, ToolRisk Risk, IReadOnlyList<string> Tags)
{
    public bool ReadOnly => Risk == ToolRisk.Read;
}

/// <summary>Effects plus an optional human-readable preview (a diff for mutating tools). Computing a plan never changes anything.</summary>
public sealed record ToolPlan(IReadOnlyList<ToolEffect> Effects, string? Preview = null);

public sealed record ToolAttachment(string MimeType, byte[] Data);

public sealed record ToolResult(string Text, bool IsError = false, IReadOnlyList<ToolAttachment>? Attachments = null, JsonElement? StructuredContent = null, Guid? OutputId = null)
{
    public static ToolResult Ok(string text) => new(text);
    public static ToolResult Error(string text) => new(text, IsError: true);
}

public sealed record ToolLimits
{
    public int MaxFileBytes { get; init; } = 1_048_576;
    public int ReadMaxLines { get; init; } = 2000;
    public int MaxLineChars { get; init; } = 2000;
    public int OutputMaxLines { get; init; } = 2000;
    public int OutputMaxBytes { get; init; } = 51_200;
    public int MaxAttachmentBytes { get; init; } = 20 * 1024 * 1024;
    public TimeSpan ProcessTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxProcessOutputChars { get; init; } = 24_000;
    public int MaxSearchFiles { get; init; } = 2000;
    public int MaxSearchResults { get; init; } = 200;
    public TimeSpan SearchTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int FetchDefaultSeconds { get; init; } = 30;
    public int FetchMaxSeconds { get; init; } = 120;
    public int FetchMaxBytes { get; init; } = 5 * 1024 * 1024;
    public bool AllowPrivateHosts { get; init; }
    public string? Shell { get; init; }
    public IReadOnlyDictionary<string, string> Shells { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> AllowedEnvironment { get; init; } = [];
}

/// <summary>Everything a call needs that is not in its arguments. Built per call by the engine from an immutable settings snapshot.</summary>
public sealed class ToolContext
{
    public required string Workspace { get; init; }
    public Guid SessionId { get; init; }
    public string CallId { get; init; } = "";
    public string? Role { get; init; }
    public IToolOutputStore? Outputs { get; init; }
    public ToolLimits Limits { get; init; } = new();
    public IReadOnlyList<string> ProtectedGlobs { get; init; } = [];
    public IReadOnlyList<string> IgnoreGlobs { get; init; } = [];
    public IProgress<string>? Progress { get; init; }

    public ToolContext WithWorkspace(string workspace, IToolOutputStore? outputs = null) => new()
    {
        Workspace = workspace, SessionId = SessionId, CallId = CallId, Role = Role, Limits = Limits,
        ProtectedGlobs = ProtectedGlobs, IgnoreGlobs = IgnoreGlobs, Progress = Progress, Outputs = outputs ?? Outputs,
    };
}

public interface ITool
{
    ToolSpec Spec { get; }

    /// <summary>Declares effects and a preview. Throws <see cref="ToolArgumentException"/> for invalid arguments.</summary>
    ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken);

    /// <summary>Runs the call. Operational failures come back as an error result; invalid arguments throw.</summary>
    Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken);
}

public interface IToolSource
{
    IReadOnlyList<ITool> Tools { get; }
}

public sealed class ToolSource(IEnumerable<ITool> tools) : IToolSource
{
    public IReadOnlyList<ITool> Tools { get; } = Array.AsReadOnly(tools.ToArray());
}

/// <summary>Registered tools by name. Names are unique; roles and settings narrow what a session sees.</summary>
public sealed class ToolCatalog
{
    private static readonly ConditionalWeakTable<ITool, ToolInputSchema> Schemas = new();
    private readonly Dictionary<string, ITool> _byName = new(StringComparer.Ordinal);
    private readonly List<ITool> _ordered = [];

    public ToolCatalog(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            Validate(tool.Spec);
            Schemas.GetValue(tool, value => new ToolInputSchema(value.Spec.InputSchema));
            if (!_byName.TryAdd(tool.Spec.Name, tool))
                throw new InvalidOperationException($"Tool '{tool.Spec.Name}' is registered twice.");
            _ordered.Add(tool);
        }
    }

    public IReadOnlyList<ITool> All => _ordered.AsReadOnly();

    public ITool? Find(string name) => _byName.GetValueOrDefault(name);

    public void ValidateArguments(ITool tool, ToolArguments arguments) => Schemas.GetValue(tool, value => new ToolInputSchema(value.Spec.InputSchema)).Validate(arguments.Json);

    /// <summary>Tools a role may use. Entries are tool names or capability tags; deny wins; an empty allow list means everything.</summary>
    public IReadOnlyList<ITool> Visible(IReadOnlyCollection<string>? allow = null, IReadOnlyCollection<string>? deny = null) =>
        _ordered.Where(tool => IsVisible(tool.Spec, allow, deny)).ToArray();

    public static bool IsVisible(ToolSpec tool, IReadOnlyCollection<string>? allow = null, IReadOnlyCollection<string>? deny = null) =>
        !Matches(tool, deny) && (allow is not { Count: > 0 } || Matches(tool, allow));

    private static bool Matches(ToolSpec tool, IReadOnlyCollection<string>? entries) =>
        entries is { Count: > 0 } &&
        entries.Any(entry => string.Equals(entry, tool.Name, StringComparison.Ordinal) ||
                             tool.Tags.Contains(entry, StringComparer.OrdinalIgnoreCase));

    private static void Validate(ToolSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec.Tags);
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Length > 256 ||
            spec.Name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new ArgumentException("Tool names must contain 1-256 ASCII letters, digits, dots, hyphens or underscores.");
        if (string.IsNullOrWhiteSpace(spec.Description) || !Enum.IsDefined(spec.Risk) ||
            spec.Tags.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Tool '{spec.Name}' has an invalid description, risk or capability tags.");
        try
        {
            using var schema = JsonDocument.Parse(spec.InputSchema);
            if (schema.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"Tool '{spec.Name}' input schema must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Tool '{spec.Name}' input schema is invalid JSON.", exception);
        }
    }
}
