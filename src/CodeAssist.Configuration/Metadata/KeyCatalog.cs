using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace CodeAssist.Configuration;

public enum KeyKind { Scalar, Object, List, Dictionary }

/// <summary>Reflected description of one settings key (or the element type of a list or dictionary).</summary>
public sealed class KeyNode
{
    public required string Name { get; init; }
    public required Type ClrType { get; init; }
    public required KeyKind Kind { get; init; }
    public KeyNode? Parent { get; init; }

    public TrustClass Trust { get; set; } = TrustClass.Preference;
    public StrictDirection Strict { get; set; } = StrictDirection.None;
    public IReadOnlyList<string> StrictOrder { get; set; } = [];
    public MergeMode Merge { get; set; } = MergeMode.Replace;
    public ReloadKind Reload { get; set; } = ReloadKind.Live;
    public bool IsSecret { get; set; }
    public string? Description { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }

    /// <summary>Underlying type of a scalar (nullable removed).</summary>
    public Type? ScalarType { get; set; }

    public IReadOnlyList<string> EnumNames { get; set; } = [];
    public Dictionary<string, KeyNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public KeyNode? Element { get; set; }

    public bool IsNumeric => ScalarType is { } t && (t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(decimal) || t == typeof(float) || t == typeof(short));
    public bool IsEnum => ScalarType is { IsEnum: true };

    /// <summary>Returns the child for a path segment, or null when the segment is not valid here.</summary>
    public KeyNode? Step(string segment) => Kind switch
    {
        KeyKind.Object => Children.GetValueOrDefault(segment),
        KeyKind.Dictionary => Element,
        KeyKind.List => int.TryParse(segment, out var index) && index >= 0 ? Element : null,
        _ => null,
    };
}

/// <summary>The reflected key tree of <see cref="CodeAssistSettings"/>; the single source of truth for trust, limits, schema and validation.</summary>
public sealed class KeyCatalog
{
    public static KeyCatalog Default { get; } = Build(typeof(CodeAssistSettings));

    public KeyNode Root { get; }

    private KeyCatalog(KeyNode root) => Root = root;

    public static KeyCatalog Build(Type rootType) => new(BuildNode(rootType, "", null, null));

    /// <summary>Resolves path segments to a node, or null if the path does not exist in the model.</summary>
    public KeyNode? Resolve(IReadOnlyList<string> segments)
    {
        var node = Root;
        foreach (var segment in segments)
        {
            var next = node.Step(segment);
            if (next is null) return null;
            node = next;
        }
        return node;
    }

    /// <summary>Resolves a path and rewrites object-property segments to their canonical spelling.</summary>
    public KeyNode? Canonicalize(IReadOnlyList<string> segments, out string[] canonical)
    {
        var node = Root;
        var result = new List<string>(segments.Count);
        foreach (var segment in segments)
        {
            var next = node.Step(segment);
            if (next is null)
            {
                canonical = [];
                return null;
            }
            result.Add(node.Kind == KeyKind.Object ? next.Name : segment);
            node = next;
        }
        canonical = result.ToArray();
        return node;
    }

    private static KeyNode BuildNode(Type type, string name, KeyNode? parent, PropertyInfo? property)
    {
        var (kind, elementType, scalar) = Classify(type);
        var node = new KeyNode { Name = name, ClrType = type, Kind = kind, Parent = parent };

        node.Trust = property?.GetCustomAttribute<TrustAttribute>()?.Value ?? parent?.Trust ?? TrustClass.Preference;
        node.Reload = property?.GetCustomAttribute<ReloadAttribute>()?.Kind ?? parent?.Reload ?? ReloadKind.Live;
        node.Merge = property?.GetCustomAttribute<MergeAttribute>()?.Mode ?? MergeMode.Replace;
        node.IsSecret = property?.GetCustomAttribute<SecretAttribute>() is not null || (parent?.IsSecret ?? false);
        node.Description = property?.GetCustomAttribute<DescriptionAttribute>()?.Description;
        var strict = property?.GetCustomAttribute<StrictAttribute>();
        node.Strict = strict?.Direction ?? StrictDirection.None;
        node.StrictOrder = property?.GetCustomAttribute<StrictOrderAttribute>()?.Values ?? [];
        if (property?.GetCustomAttribute<RangeAttribute>() is { } range)
        {
            node.Min = Convert.ToDouble(range.Minimum);
            node.Max = Convert.ToDouble(range.Maximum);
        }

        switch (kind)
        {
            case KeyKind.Scalar:
                node.ScalarType = scalar;
                if (scalar!.IsEnum) node.EnumNames = SettingsJson.EnumNames(scalar);
                break;
            case KeyKind.Object:
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
                    var child = BuildNode(prop.PropertyType, SettingsJson.CamelCase(prop.Name), node, prop);
                    node.Children[child.Name] = child;
                }
                break;
            case KeyKind.List:
            case KeyKind.Dictionary:
                node.Element = BuildNode(elementType!, "*", node, null);
                // Element nodes inherit the container's metadata but not its own attribute-driven limits.
                node.Element.Trust = node.Trust;
                node.Element.Reload = node.Reload;
                node.Element.IsSecret = node.IsSecret;
                if (node.Element.Kind == KeyKind.Scalar && kind == KeyKind.List)
                {
                    node.Element.Min = node.Min;
                    node.Element.Max = node.Max;
                }
                break;
        }
        return node;
    }

    private static (KeyKind Kind, Type? Element, Type? Scalar) Classify(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string) || underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(decimal))
            return (KeyKind.Scalar, null, underlying);

        if (underlying.IsArray) return (KeyKind.List, underlying.GetElementType(), null);

        foreach (var contract in underlying.GetInterfaces().Prepend(underlying).Where(t => t.IsGenericType))
        {
            var definition = contract.GetGenericTypeDefinition();
            var arguments = contract.GetGenericArguments();
            if (definition == typeof(IDictionary<,>) && arguments[0] == typeof(string))
                return (KeyKind.Dictionary, arguments[1], null);
        }
        foreach (var contract in underlying.GetInterfaces().Prepend(underlying).Where(t => t.IsGenericType))
        {
            if (contract.GetGenericTypeDefinition() == typeof(IList<>))
                return (KeyKind.List, contract.GetGenericArguments()[0], null);
        }
        return (KeyKind.Object, null, null);
    }
}
