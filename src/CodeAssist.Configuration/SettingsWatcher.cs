using Microsoft.Extensions.Hosting;
using System.Threading.Channels;

namespace CodeAssist.Configuration;

/// <summary>Reloads settings shortly after a settings file changes (debounced).</summary>
public sealed class SettingsWatcher(SettingsService service) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var watchers = CreateWatchers(() => signals.Writer.TryWrite(true));
        try
        {
            while (await signals.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(Debounce, stoppingToken);
                while (signals.Reader.TryRead(out _)) { }
                service.Reload();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var watcher in watchers) watcher.Dispose();
        }
    }

    private List<FileSystemWatcher> CreateWatchers(Action onChange)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { service.Locations.Home, service.Locations.ManagedDirectory };
        var directory = new DirectoryInfo(service.Locations.WorkingDirectory);
        while (directory is not null)
        {
            directories.Add(Path.Combine(directory.FullName, ".code-assist"));
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) break;
            directory = directory.Parent;
        }

        var watchers = new List<FileSystemWatcher>();
        foreach (var path in directories.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(path) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                FileSystemEventHandler handler = (_, e) =>
                {
                    if (IsSettingsFile(e.Name)) onChange();
                };
                watcher.Changed += handler;
                watcher.Created += handler;
                watcher.Deleted += handler;
                watcher.Renamed += (_, e) =>
                {
                    if (IsSettingsFile(e.Name) || IsSettingsFile(e.OldName)) onChange();
                };
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        }
        return watchers;
    }

    private static bool IsSettingsFile(string? name) =>
        name is "settings.json" or "settings.jsonc" or "preferences.json";
}
