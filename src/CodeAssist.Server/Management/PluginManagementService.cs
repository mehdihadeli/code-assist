using System.Text.Json;
using CodeAssist.Server.Composition;
using CodeAssist.Configuration;
using CodeAssist.Contracts;
using CodeAssist.Tools;
using CodeAssist.Extensions.Plugins;
using CodeAssist.Mcp;

namespace CodeAssist.Server.Management;

public sealed class PluginManagementService(CodeAssistSettings settings, SettingsLocations locations)
{
    public async Task<ManagementReply> ExecuteAsync(string operation, PluginManagementArguments options, CancellationToken cancellation)
    {
        ManagementReply? reply = null;

        var store = PluginComposition.CreateStore(settings, locations.StateDirectory);
        var workspace = locations.WorkingDirectory;
        var scope = options.ForWorkspace ? workspace : null;
        string Target() => options.Target ?? throw new ArgumentException("This plugin command requires a target.");
        void Write(object value, string text) => reply = new(JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web), text);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            switch (operation)
            {
                case "list":
                {
                    var entries = store.List(scope);
                    Write(entries, entries.Count == 0 ? "No plugins installed." : string.Join("\n", entries.Select(item =>
                        $"{item.Id}@{item.Release}  {(item.Enabled ? "enabled" : "disabled")}  sha256:{item.Digest}")));
                    break;
                }
                case "init":
                {
                    var package = PluginComposition.Scaffold(Path.GetFullPath(Target(), workspace), options.Id ?? throw new ArgumentException("Creation requires --id team.name."));
                    Write(new { package.Manifest, package.Digest }, $"Created {package.Manifest.Id}@{package.Manifest.Release}. Not installed or enabled.");
                    break;
                }
                case "validate":
                case "show":
                {
                    var target = Path.GetFullPath(Target(), workspace);
                    if (operation == "validate" && (Directory.Exists(target) && File.Exists(Path.Combine(target, PluginRegistry.FileName)) || Path.GetFileName(target) == PluginRegistry.FileName))
                    {
                        var registry = await PluginComposition.OpenRegistryAsync(settings, Target(), workspace, cancellation);
                        Write(registry.Document, $"Valid registry metadata: {registry.Document.Name}. Archive checks occur during installation.");
                        break;
                    }
                    var package = operation == "show" && store.List().Any(item => item.Id == Target()) ? store.Inspect(Target()) :
                        PluginComposition.ReadPackage(Path.GetFullPath(Target(), workspace));
                    var components = package.Manifest.Contributions.Select(item => new
                    {
                        Name = package.Manifest.Id + "/" + item.Key, item.Kind, item.Description, item.Tools,
                        ContentBytes = package.FileBytes(item.Entry),
                    }).ToArray();
                    Write(new { package.Manifest, package.Digest, package.Bytes, Components = components },
                        $"Valid {package.Manifest.Id}@{package.Manifest.Release}\nSHA-256: {package.Digest}\nDeclared bytes: {package.Bytes}\n" +
                        string.Join("\n", components.Select(item => $"{item.Kind} {item.Name} ({item.ContentBytes} content bytes)")));
                    break;
                }
                case "pack":
                {
                    var package = PluginComposition.ReadPackage(Path.GetFullPath(Target(), workspace));
                    var output = Path.GetFullPath(options.Output ?? throw new ArgumentException("Packaging requires --out release.zip."), workspace);
                    package.Pack(output);
                    Write(new { Path = output, package.Digest }, $"Packaged {package.Manifest.Id}@{package.Manifest.Release}: {output}");
                    break;
                }
                case "install":
                {
                    if (options.Id is null)
                    {
                        if (!settings.Policy.Plugins.LocalPackages) throw new ArgumentException("Direct plugin installation is restricted; use an admitted registry.");
                        if (options.Release is not null) throw new ArgumentException("--release requires a registry install with --id.");
                        store.Install([PluginComposition.ReadPackage(Path.GetFullPath(Target(), workspace))]);
                    }
                    else
                    {
                        var registry = await PluginComposition.OpenRegistryAsync(settings, Target(), workspace, cancellation);
                        store.Install(await registry.ResolveAsync(options.Id, options.Release, cancellation, options.Lane), registry.Source);
                    }
                    Write(store.List(scope), "Installed verified release(s). New user approvals remain disabled; managed required content takes effect immediately. Review plugins list and approve non-required dependencies with --approve SHA256.");
                    break;
                }
                case "enable":
                case "disable":
                {
                    var enabled = operation == "enable";
                    if (enabled && !PluginComposition.Permitted(settings, Target())) throw new ArgumentException("Plugin use is restricted by settings or policy.");
                    store.SetEnabled(Target(), enabled, options.Digest, scope);
                    Write(store.List(scope), $"{(enabled ? "Enabled" : "Disabled")} {Target()} for {(scope is null ? "this user" : "this workspace")}.");
                    break;
                }
                case "remove":
                    if (scope is not null) throw new ArgumentException("Removal is user-wide. Use disable --workspace for a local override.");
                    store.Remove(Target());
                    Write(store.List(), "Installation removed. Unreferenced content cache is retained; no dependency was removed automatically.");
                    break;
                case "expand":
                {
                    var catalog = PluginComposition.CreateCatalog(settings, locations.StateDirectory, workspace);
                    var text = catalog.Prompt(Target(), options.Input);
                    Write(new { Text = text }, text);
                    break;
                }
                case "publish":
                {
                    var registry = Path.GetFullPath(options.Registry ?? throw new ArgumentException("Publishing requires --registry DIRECTORY."), workspace);
                    PluginComposition.Publish(Path.GetFullPath(Target(), workspace), registry, options.RegistryName, options.Lane);
                    Write(new { Registry = registry }, "Published into local registry. No remote upload or Git operation performed.");
                    break;
                }
                case "registry":
                {
                    var registry = await PluginComposition.OpenRegistryAsync(settings, Target(), workspace, cancellation);
                    Write(registry.Document, registry.Document.Name + "\n" + string.Join("\n", registry.Document.Releases.Select(item => item.Id + "@" + item.Release)));
                    break;
                }
                case "stats":
                {
                    var usage = store.Usage();
                    Write(usage, usage.Count == 0 ? "No recorded plugin invocations." : string.Join("\n", usage.Select(pair => $"{pair.Key}  {pair.Value.Invocations}  {pair.Value.LastUsed:O}")));
                    break;
                }
                case "libraries":
                    Write(settings.PluginLibraries, settings.PluginLibraries.Count == 0 ? "No plugin libraries configured." :
                        string.Join("\n", settings.PluginLibraries.Select(pair => $"{pair.Key}  {pair.Value.Source}")));
                    break;
                case "search":
                case "suggest":
                {
                    var registry = await PluginComposition.OpenRegistryAsync(settings, Target(), workspace, cancellation);
                    if (operation == "search")
                    {
                        var matches = registry.Search(options.Query, options.Lane).Where(item => PluginComposition.Permitted(settings, item.Id)).ToArray();
                        Write(matches, matches.Length == 0 ? "No matching releases." : string.Join("\n", matches.Select(item => $"{item.Id}@{item.Release}  [{item.Lane}]  {item.Blurb}")));
                    }
                    else
                    {
                        var activity = new PluginActivity(options.SeenFiles, options.SeenFolders, options.SeenCommands, options.SeenDependencies);
                        var matches = PluginRecommendations.Rank(registry, activity, store.Diagnose(scope).Entries.Select(item => item.Id), options.Lane)
                            .Where(item => PluginComposition.Permitted(settings, item.Id)).ToArray();
                        Write(matches, matches.Length == 0 ? "No matching uninstalled plugins." : string.Join("\n", matches.Select(item => $"{item.Id}@{item.Release}  [{item.Lane}]  {string.Join(", ", item.Reasons)}")));
                    }
                    break;
                }
                case "diagnose":
                {
                    var report = store.Diagnose(scope);
                    Write(report, string.Join("\n", new[] { report.Ready ? "Plugin load checks passed." : "Plugin loading is blocked." }
                        .Concat(report.Entries.Select(item => $"{item.Id}@{item.Release}  {(item.Selected ? "selected" : "disabled")}{(item.Required ? "  required" : "")}"))
                        .Concat(report.Issues.Select(issue => $"{issue.Id ?? "store"}: {issue.Code}: {issue.Message}\n{issue.Action}"))));
                    if (!report.Ready) return reply! with { ExitCode = 2 };
                    break;
                }
                default: throw new ArgumentException("Unknown plugin command.");
            }
            return reply ?? new(JsonSerializer.SerializeToElement(new { }), "");
        }
        catch (HttpRequestException) { throw new ArgumentException("Plugin registry download failed. Check source, network access and TLS; no unverified plugin was installed."); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException or ArgumentException)
        {
            throw new ArgumentException(exception.Message);
        }
    }
}
