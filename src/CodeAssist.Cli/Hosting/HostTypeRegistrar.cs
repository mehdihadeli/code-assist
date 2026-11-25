using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Hosting;

/// <summary>
/// Bridges Spectre.Console.Cli to the generic host's container. Spectre's own registrations (command and settings types,
/// interceptors, internal services) are captured in a registry; instances are created with a provider that sees both the
/// registry and the host, so command constructors receive host services.
/// Resolution order: Spectre registry (including IEnumerable of a registered service), then host services, then on-demand
/// construction of concrete types that Spectre did not register (settings classes).
/// </summary>
public sealed class HostTypeRegistrar(IServiceProvider host) : ITypeRegistrar
{
    private readonly Dictionary<Type, List<Registration>> _registrations = [];

    public void Register(Type service, Type implementation) =>
        Add(service, new Registration(provider => ActivatorUtilities.CreateInstance(provider, implementation)));

    public void RegisterInstance(Type service, object implementation) =>
        Add(service, new Registration(_ => implementation));

    public void RegisterLazy(Type service, Func<object> factory) =>
        Add(service, new Registration(_ => factory()));

    public ITypeResolver Build() => new HostTypeResolver(host, _registrations);

    private void Add(Type service, Registration registration)
    {
        if (!_registrations.TryGetValue(service, out var list)) _registrations[service] = list = [];
        list.Add(registration);
    }

    internal sealed class Registration(Func<IServiceProvider, object> factory)
    {
        private object? _instance;

        public object Get(IServiceProvider provider) => _instance ??= factory(provider);

        public IDisposable? Disposable => _instance as IDisposable;
    }
}

public sealed class HostTypeResolver : ITypeResolver, IDisposable
{
    private readonly IServiceProvider _host;
    private readonly Dictionary<Type, List<HostTypeRegistrar.Registration>> _registrations;
    private readonly KnownServices _known;
    private readonly Lock _gate = new();

    internal HostTypeResolver(IServiceProvider host, Dictionary<Type, List<HostTypeRegistrar.Registration>> registrations)
    {
        _host = host;
        _registrations = registrations;
        _known = new KnownServices(this);
    }

    public object? Resolve(Type? type)
    {
        if (type is null) return null;
        var known = ResolveKnown(type);
        if (known is not null) return known;

        // Settings classes and other concrete types Spectre did not register explicitly.
        return type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && type != typeof(string)
            ? ActivatorUtilities.CreateInstance(_known, type)
            : null;
    }

    private object? ResolveKnown(Type type)
    {
        lock (_gate)
        {
            if (_registrations.TryGetValue(type, out var list)) return list[^1].Get(_known);

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                _registrations.TryGetValue(type.GetGenericArguments()[0], out var many))
            {
                var items = Array.CreateInstance(type.GetGenericArguments()[0], many.Count);
                for (var i = 0; i < many.Count; i++) items.SetValue(many[i].Get(_known), i);
                return items;
            }
        }
        return _host.GetService(type);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var disposable in _registrations.Values.SelectMany(l => l).Select(r => r.Disposable).OfType<IDisposable>().Distinct())
                disposable.Dispose();
        }
    }

    private sealed class KnownServices(HostTypeResolver owner) : IServiceProvider
    {
        public object? GetService(Type serviceType) => owner.ResolveKnown(serviceType);
    }
}
