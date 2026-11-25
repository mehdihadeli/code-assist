using Microsoft.Extensions.Options;

namespace CodeAssist.Configuration;

/// <summary>Exposes the live snapshot through the standard options abstraction so ordinary services can depend on <c>IOptionsMonitor&lt;CodeAssistSettings&gt;</c>.</summary>
public sealed class SettingsOptionsMonitor(ISettingsProvider provider) : IOptionsMonitor<CodeAssistSettings>
{
    public CodeAssistSettings CurrentValue => provider.Current.Settings;

    public CodeAssistSettings Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<CodeAssistSettings, string?> listener)
    {
        void Handler(SettingsSnapshot snapshot) => listener(snapshot.Settings, null);
        provider.Changed += Handler;
        return new Subscription(() => provider.Changed -= Handler);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
