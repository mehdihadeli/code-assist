using System.ComponentModel;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Plugins;

public sealed class PluginCommandSettings : OutputSettings
{
    [CommandArgument(0, "[TARGET]")]
    [Description("Directory, ZIP, registry source, installed id or namespaced contribution, depending on the command.")]
    public string? Target { get; init; }

    [CommandOption("--id <ID>"), Description("Dotted plugin id for creation or registry installation.")]
    public string? Id { get; init; }

    [CommandOption("--release <VERSION>"), Description("Exact registry release; omitted selects the latest listed stable release.")]
    public string? Release { get; init; }

    [CommandOption("--out <FILE>"), Description("New output ZIP path; never overwrites an existing file.")]
    public string? Output { get; init; }

    [CommandOption("--approve <SHA256>"), Description("Installed content digest you explicitly approve for activation.")]
    public string? Digest { get; init; }

    [CommandOption("--workspace"), Description("List or change activation only for this workspace, without writing repository settings.")]
    public bool ForWorkspace { get; init; }

    [CommandOption("--input <TEXT>"), Description("Untrusted task input appended to expanded guidance; never shell-expanded.")]
    public string Input { get; init; } = "";

    [CommandOption("--registry <DIRECTORY>"), Description("Local registry output directory for publishing.")]
    public string? Registry { get; init; }

    [CommandOption("--name <NAME>"), Description("Dotted name for a new local registry.")]
    public string RegistryName { get; init; } = "local.registry";

    [CommandOption("--lane <NAME>"), Description("Release lane used for registry publication, lookup and dependency resolution.")]
    public string Lane { get; init; } = "general";

    [CommandOption("--query <TEXT>"), Description("Search registry ids, blurbs and labels without downloading packages.")]
    public string Query { get; init; } = "";

    [CommandOption("--seen-file <PATH>"), Description("Explicit observed file path for recommendations; repeatable. File contents are never read.")]
    public string[] SeenFiles { get; init; } = [];

    [CommandOption("--seen-folder <PATH>"), Description("Explicit workspace-relative activity folder for recommendations; repeatable.")]
    public string[] SeenFolders { get; init; } = [];

    [CommandOption("--seen-command <NAME>"), Description("Explicit executable name for recommendations; repeatable. Nothing is executed.")]
    public string[] SeenCommands { get; init; } = [];

    [CommandOption("--seen-dependency <ID>"), Description("Explicit dependency id for recommendations; repeatable. Manifests are never scanned.")]
    public string[] SeenDependencies { get; init; } = [];
}