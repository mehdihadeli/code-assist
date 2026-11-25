using CodeAssist.Models;
using CodeAssist.Mcp;
using CodeAssist.Extensions.Skills;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;

namespace CodeAssist.Server.Composition;

/// <summary>Services the built-in tools run on. Optional ports decide which interaction tools exist.</summary>
public sealed record ToolServices(IFileSystem FileSystem, IProcessRunner ProcessRunner, HttpClient Http, ToolPorts Ports);

public static class BuiltInTools
{
    /// <summary>The built-in tool set. A tool whose port is not provided is left out, so every surface sees only what works.</summary>
    public static IReadOnlyList<ITool> Create(ToolServices services)
    {
        var tools = new List<ITool>
        {
            new FsReadTool(services.FileSystem),
            new FsFindTool(services.FileSystem),
            new FsDiffTool(services.FileSystem),
            new FsWriteTool(services.FileSystem),
            new FsEditTool(services.FileSystem),
            new FsPatchTool(services.FileSystem),
            new NotebookEditTool(services.FileSystem),
            new ProcRunTool(services.ProcessRunner, services.Ports.Jobs),
            new WebFetchTool(services.Http),
        };

        var ports = services.Ports;
        if (ports.Outputs is not null) tools.Add(new OutputReadTool(ports.Outputs));
        if (ports.JobNotices is not null) tools.Add(new JobNoticesTool(ports.JobNotices));
        if (ports.Tests is not null) tools.Add(new TestFeedbackTool(ports.Tests));
        if (ports.Changes is not null) tools.Add(new WorkspaceChangesTool(ports.Changes));
        if (ports.Knowledge is not null) tools.Add(new KnowledgeNotesTool(ports.Knowledge, ports.Interaction));
        if (ports.Retrieval is not null) tools.Add(new WorkspaceSearchTool(ports.Retrieval, ports.Interaction));
        if (ports.Models is not null) tools.Add(new ModelFindTool(ports.Models));
        if (ports.Branches is not null) tools.Add(new WorkspaceBranchTool(ports.Branches));
        if (ports.Worktrees is not null && ports.Branches is not null) tools.Add(new SessionWorktreeTool(ports.Worktrees, ports.Branches));
        if (ports.Sessions is not null) tools.Add(new SessionManageTool(ports.Sessions));
        if (ports.Dispatcher is not null) tools.Add(new FlowRunTool(ports.Dispatcher));
        if (ports.Images is { Models.Count: > 0 } && ports.Jobs is not null) tools.Add(new MediaRenderTool(ports.Images, ports.Jobs, services.FileSystem));
        if (ports.Browser is not null)
            tools.AddRange(ports.Browser.Operations.Select(operation => operation.Family).Distinct(StringComparer.Ordinal).Select(family => new BrowserTool(ports.Browser, family)));
        if (ports.Search is not null) tools.Add(new WebSearchTool(ports.Search));
        if (ports.Interaction is not null) tools.Add(new AskUserTool(ports.Interaction));
        if (ports.Todos is not null) tools.Add(new PlanTodoTool(ports.Todos));
        if (ports.WorkLedger is not null) tools.Add(new WorkItemsTool(ports.WorkLedger));
        if (ports.Output is not null) tools.Add(new StructuredOutputTool(ports.Output));
        if (ports.Planning is not null) tools.Add(new SessionPlanTool(ports.Planning));
        if (ports.Configuration is not null) tools.Add(new ConfigManageTool(ports.Configuration));
        if (ports.Briefs is not null && ports.Interaction is not null) tools.Add(new BriefPublishTool(ports.Briefs, ports.Interaction, services.FileSystem));
        if (ports.Language is not null) tools.Add(new LanguageQueryTool(ports.Language, services.FileSystem));
        if (ports.Language is not null) tools.Add(new CodeDiagnosticsTool(ports.Language, services.FileSystem));
        if (ports.McpConnections is not null)
        {
            tools.Add(new McpRemoteTool(ports.McpConnections, ports.DiscoveredToolVisible));
            if (ports.McpPrompts is not null) tools.Add(new McpPromptTool(ports.McpPrompts, ports.McpConnections));
            if (ports.Interaction is not null) tools.Add(new McpAuthTool(ports.McpConnections, ports.Interaction));
        }
        if (ports.Teams is not null)
        {
            tools.Add(new TeamManageTool(ports.Teams));
            tools.Add(new TeamMailTool(ports.Teams));
        }
        if (ports.Skills is not null) tools.Add(new SkillLoadTool(ports.Skills));
        if (ports.Skills is not null && ports.Dispatcher is not null) tools.Add(new SkillRunTool(ports.Skills, ports.Dispatcher));
        if (ports.Delegation is not null) tools.Add(new AgentDelegateTool(ports.Delegation, ports.Jobs));
        if (ports.Resources is not null)
        {
            tools.Add(new ResourceListTool(ports.Resources));
            tools.Add(new ResourceReadTool(ports.Resources));
        }
        if (ports.Jobs is not null)
        {
            tools.Add(new JobTool(ports.Jobs, JobOperation.List));
            tools.Add(new JobTool(ports.Jobs, JobOperation.Read));
            tools.Add(new JobTool(ports.Jobs, JobOperation.Stop));
            tools.Add(new JobTool(ports.Jobs, JobOperation.Wait));
        }
        if (ports.Sources is not null) tools.AddRange(ports.Sources.SelectMany(source => source.Tools));
        if (ports.VisibleTools is not null) tools.Add(new ToolFindTool(ports.VisibleTools, ports.McpConnections, ports.DiscoveredToolVisible, ports.Exposure));
        return tools;
    }
}
