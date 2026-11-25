using CodeAssist.Models;
using CodeAssist.Extensions.Skills;
using CodeAssist.Mcp;
using CodeAssist.Tools;

namespace CodeAssist.Server.Composition;

public sealed record ToolPorts(
    IUserInteraction? Interaction = null,
    ITodoStore? Todos = null,
    ISkillCatalog? Skills = null,
    IDelegation? Delegation = null,
    IWebSearch? Search = null,
    IResourceSource? Resources = null,
    IToolJobs? Jobs = null,
    IModelCatalog? Models = null,
    IWorkspaceBranches? Branches = null,
    ISessionControl? Sessions = null,
    IToolDispatcher? Dispatcher = null,
    IImageRenderer? Images = null,
    IBrowserAutomation? Browser = null,
    IReadOnlyList<IToolSource>? Sources = null,
    Func<ToolContext, IReadOnlyList<ToolSpec>>? VisibleTools = null,
    IWorkLedger? WorkLedger = null,
    ITeamHub? Teams = null,
    IStructuredOutput? Output = null,
    IPlanningSessions? Planning = null,
    IConfigurationAccess? Configuration = null,
    IBriefPublisher? Briefs = null,
    ILanguageService? Language = null,
    IMcpConnections? McpConnections = null,
    ISessionWorktrees? Worktrees = null,
    Func<ToolContext, ToolSpec, bool>? DiscoveredToolVisible = null,
    IToolOutputStore? Outputs = null,
    IJobNotices? JobNotices = null,
    ITestFeedback? Tests = null,
    IWorkspaceChanges? Changes = null,
    IKnowledgeNotes? Knowledge = null,
    IWorkspaceRetrieval? Retrieval = null,
    IToolExposure? Exposure = null,
    IMcpPrompts? McpPrompts = null);