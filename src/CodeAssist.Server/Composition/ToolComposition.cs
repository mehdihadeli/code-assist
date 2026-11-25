using CodeAssist.Models;
using CodeAssist.Persistence;
using CodeAssist.Extensions.Skills;
using CodeAssist.Tools;
using CodeAssist.Tools.Execution;
using CodeAssist.Security;
using CodeAssist.Tools.Extensions;
using CodeAssist.Extensions.Plugins;
using CodeAssist.Sessions;
using CodeAssist.Mcp;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Models.Chat;
using CodeAssist.Configuration;
using CodeAssist.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeAssist.Server.Composition;

public static class ToolComposition
{
    public static IServiceCollection AddCodeAssistTool<TTool>(this IServiceCollection services) where TTool : class, ITool
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITool, TTool>());
        return services;
    }

    /// <summary>Maps tool settings to the limits every call runs under.</summary>
    public static ToolLimits ToLimits(ToolSettings tools) => new()
    {
        MaxFileBytes = tools.MaxFileBytes,
        ReadMaxLines = tools.OutputMaxLines,
        OutputMaxLines = tools.OutputMaxLines,
        OutputMaxBytes = tools.OutputMaxBytes,
        ProcessTimeout = TimeSpan.FromSeconds(tools.ProcessTimeoutSeconds),
        MaxProcessOutputChars = tools.MaxProcessOutputCharacters,
        MaxSearchFiles = tools.MaxSearchFiles,
        MaxSearchResults = tools.MaxSearchResults,
        FetchDefaultSeconds = tools.Web.FetchTimeoutSeconds,
        FetchMaxBytes = tools.Web.FetchMaxBytes,
        AllowPrivateHosts = tools.Web.AllowPrivateHosts,
        Shell = string.IsNullOrWhiteSpace(tools.Shell) ? null : tools.Shell,
        Shells = new Dictionary<string, string>(tools.Shells, StringComparer.OrdinalIgnoreCase),
        AllowedEnvironment = tools.Env.Allow,
    };

    /// <summary>Context for one call, built from a settings snapshot. The engine creates one per tool call.</summary>
    public static ToolContext CreateContext(CodeAssistSettings settings, string workspace, Guid sessionId, string callId) => new()
    {
        Workspace = workspace,
        SessionId = sessionId,
        CallId = callId,
        Role = settings.Agent.Role,
        Limits = ToLimits(settings.Tools),
        ProtectedGlobs = settings.Workspace.Protected,
        IgnoreGlobs = settings.Workspace.Ignore,
    };

    /// <summary>Builds the catalog: built-in tools minus <c>tools.disabled</c>. Tools whose port is missing are not registered.</summary>
    public static ToolCatalog CreateCatalog(ToolSettings tools, ToolPorts? ports = null, HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient(NetworkGuard.CreateHandler(tools.Web.AllowPrivateHosts)) : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;

        var services = new ToolServices(new LocalFileSystem(), new ProcessRunner(), http, ports ?? new ToolPorts());
        var disabled = new HashSet<string>(tools.Disabled, StringComparer.Ordinal);
        return new ToolCatalog(BuiltInTools.Create(services).Where(tool => !disabled.Contains(tool.Spec.Name)));
    }

    public static ToolCatalog CreateCatalog(CodeAssistSettings settings, ToolPorts? ports = null, HttpMessageHandler? handler = null, Func<string, string, RoleSettings?>? roles = null)
    {
        ToolCatalog? catalog = null;
        var scoped = (ports ?? new ToolPorts()) with
        {
            DiscoveredToolVisible = (context, spec) => (roles?.Invoke(context.Role ?? settings.Agent.Role, context.Workspace) ?? settings.Roles.GetValueOrDefault(context.Role ?? settings.Agent.Role)) is { } role &&
                !settings.Tools.Disabled.Contains(spec.Name, StringComparer.Ordinal) && ToolCatalog.IsVisible(spec, role.Tools.Allow, role.Tools.Deny),
            VisibleTools = context =>
            {
                var role = roles?.Invoke(context.Role ?? settings.Agent.Role, context.Workspace) ?? settings.Roles.GetValueOrDefault(context.Role ?? settings.Agent.Role);
                if (role is null) return [];
                return catalog!.Visible(role.Tools.Allow, role.Tools.Deny).Select(tool => tool.Spec).ToArray();
            },
        };
        catalog = CreateCatalog(settings.Tools, scoped, handler);
        return catalog;
    }

    public static ToolsApi CreateApi(CodeAssistSettings settings, ToolPorts? ports = null) =>
        new(CreateCatalog(settings, ports));

    /// <summary>Registers the tool catalog and API. Optional ports are taken from the container when present.</summary>
    public static IServiceCollection AddCodeAssistTools(this IServiceCollection services)
    {
        services.TryAddSingleton<McpConnectionManager>(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>();
            var host = provider.GetService<McpHostServices>();
            var bridge = new McpElicitationBridge(provider.GetRequiredService<IUserInteraction>(), host?.OpenBrowser);
            return ConnectorComposition.Create(settings.Current.Settings, settings.Locations.WorkingDirectory, provider.GetRequiredService<IEnvironmentVariables>().GetAll(),
                clientOptions: bridge.CreateOptions, host: host);
        });
        services.TryAddSingleton<IMcpConnections>(provider => provider.GetRequiredService<McpConnectionManager>());
        services.TryAddSingleton<IMcpPrompts>(provider => provider.GetRequiredService<McpConnectionManager>());
        services.TryAddSingleton<IToolExposure, SessionToolExposure>();
        services.TryAddSingleton<JournalJobNotices>();
        services.TryAddSingleton<IJobNotices>(provider => provider.GetRequiredService<JournalJobNotices>());
        services.TryAddSingleton<IToolJobs>(provider => new InMemoryToolJobs(persistCompletion: provider.GetRequiredService<JournalJobNotices>().RecordAsync));
        services.TryAddSingleton<IToolOutputStore>(provider => new FileToolOutputStore(Path.Combine(provider.GetRequiredService<SettingsService>().Locations.StateDirectory, "outputs")));
        services.TryAddSingleton<IKnowledgeNotes>(provider => new FileKnowledgeNotes(Path.Combine(provider.GetRequiredService<SettingsService>().Locations.StateDirectory, "knowledge")));
        services.TryAddSingleton<IJournalStore>(provider => new JsonlJournalStore(Path.Combine(provider.GetRequiredService<SettingsService>().Locations.StateDirectory, "sessions")));
        services.TryAddSingleton<ISessionControl>(provider => new JournalSessionControl(provider.GetRequiredService<IJournalStore>(), Directory.Exists));
        services.TryAddSingleton<ITodoStore, JournalTodoStore>();
        services.TryAddSingleton<IWorkLedger, JournalWorkLedger>();
        services.TryAddSingleton<ITeamHub, JournalTeamHub>();
        services.TryAddSingleton<IPlanningSessions, JournalPlanningSessions>();
        services.TryAddSingleton<IConfigurationAccess, SettingsToolAccess>();
        services.TryAddSingleton<IAgentModel>(provider => new ChatClientAgentModel(reference =>
        {
            var settings = provider.GetRequiredService<ISettingsProvider>().Current.Settings;
            if (!ModelRef.TryParse(reference, out var model) || !settings.Providers.TryGetValue(model.Provider, out var configured))
                throw new InvalidOperationException("Delegation requires a configured model.");
            return ProviderComposition.CreateChatClient(model.Provider, model.Model, configured,
                provider.GetService<ProviderRegistry>() ?? ProviderRegistry.CreateDefault(), provider.GetRequiredService<IEnvironmentVariables>().GetAll(), variant: model.Variant);
        }, provider.GetRequiredService<IToolExposure>()));
        services.TryAddSingleton<IDelegation>(provider => new JournalDelegation(provider.GetRequiredService<IJournalStore>(), provider.GetRequiredService<ISettingsProvider>(),
            provider.GetRequiredService<IAgentModel>(), () => provider.GetRequiredService<ToolCatalog>(), () => provider.GetRequiredService<IToolDispatcher>(),
            workspace => PluginComposition.Roles(provider.GetRequiredService<ISettingsProvider>().Current.Settings, provider.GetRequiredService<PluginCatalog>(), workspace)));
        services.TryAddSingleton<QuestionBroker>();
        services.TryAddSingleton<IUserInteraction>(provider => provider.GetRequiredService<QuestionBroker>());
        services.TryAddSingleton<SearxngWebSearch>(provider =>
        {
            var web = provider.GetRequiredService<ISettingsProvider>().Current.Settings.Tools.Web;
            return new SearxngWebSearch(new Uri(web.SearchEndpoint ?? throw new InvalidOperationException("SearXNG requires tools.web.searchEndpoint.")), web.AllowPrivateHosts);
        });
        services.TryAddSingleton<PluginCatalog>(provider =>
        {
            var service = provider.GetRequiredService<SettingsService>();
            var local = SkillComposition.CreateLocalCatalog(service.Current.Settings, service.Locations);
            return PluginComposition.CreateCatalog(service.Current.Settings, service.Locations.StateDirectory, service.Locations.WorkingDirectory, local);
        });
        services.TryAddSingleton<ISkillCatalog>(provider => SkillComposition.Configure(provider.GetRequiredService<ISettingsProvider>().Current.Settings, provider.GetRequiredService<PluginCatalog>()));
        services.TryAddSingleton<IWorkspaceBranches>(provider => new GitWorkspaceBranches(new ProcessRunner(), () => provider.GetRequiredService<ISessionWorktrees>().ActiveRoots));
        services.TryAddSingleton<IWorkspaceChanges>(_ => new GitWorkspaceChanges(new ProcessRunner()));
        services.TryAddSingleton<IWorkspaceRetrieval>(provider => new WorkspaceRetrieval(provider.GetService<ITextEmbeddings>()));
        services.TryAddSingleton<ISessionWorktrees, JournalSessionWorktrees>();
        services.TryAddSingleton<IToolDispatcher>(provider =>
        {
            var authorization = provider.GetService<IToolAuthorization>();
            if (authorization is null) return null!;
            var settings = provider.GetRequiredService<ISettingsProvider>().Current.Settings;
            var planning = provider.GetService<IPlanningSessions>();
            if (planning is not null) authorization = new PlanningAuthorization(authorization, planning);
            IToolDispatcher dispatcher = new ToolDispatcher(context =>
            {
                var role = PluginComposition.ResolveRole(settings, provider.GetRequiredService<PluginCatalog>(), context.Role ?? settings.Agent.Role, context.Workspace);
                if (role is null) return new ToolCatalog([]);
                return new ToolCatalog(provider.GetRequiredService<ToolCatalog>().Visible(role.Tools.Allow, role.Tools.Deny));
            }, authorization);
            dispatcher = new WorkspaceToolDispatcher(dispatcher, provider.GetRequiredService<ISessionWorktrees>(), provider.GetRequiredService<IToolOutputStore>());
            return new PluginToolDispatcher(dispatcher, provider.GetRequiredService<PluginCatalog>(), provider.GetRequiredService<IJournalStore>());
        });
        return services
            .AddSingleton(provider =>
            {
                var settings = provider.GetRequiredService<ISettingsProvider>().Current.Settings;
                var dispatcher = provider.GetService<IToolDispatcher>();
                var resources = provider.GetServices<IResourceSource>().Append(provider.GetRequiredService<McpConnectionManager>()).ToArray();
                var searches = provider.GetServices<IWebSearch>().ToList();
                var preferredSearch = settings.Tools.Web.SearchProvider;
                if (searches.All(search => search.ProviderId != "searxng") && preferredSearch is "searxng" or "ask" && settings.Tools.Web.SearchEndpoint is not null) searches.Add(provider.GetRequiredService<SearxngWebSearch>());
                IWebSearch? search = searches.Count == 0 ? null : new SessionWebSearch(searches, provider.GetRequiredService<IUserInteraction>(), preferredSearch == "ask" ? null : preferredSearch ?? (searches.Count == 1 ? searches[0].ProviderId : null));
                var ports = new ToolPorts(
                    provider.GetService<IUserInteraction>(),
                    provider.GetService<ITodoStore>(),
                    provider.GetService<ISkillCatalog>(),
                    provider.GetService<IDelegation>(),
                    search,
                    resources.Length == 0 ? null : new ResourceSources(resources),
                    provider.GetService<IToolJobs>(),
                    provider.GetService<IModelCatalog>(),
                    provider.GetService<IWorkspaceBranches>(),
                    provider.GetService<ISessionControl>(),
                    dispatcher,
                    provider.GetService<IImageRenderer>(),
                    provider.GetService<IBrowserAutomation>(),
                    provider.GetServices<IToolSource>().Append(new ToolSource(provider.GetServices<ITool>())).ToArray(),
                    WorkLedger: provider.GetService<IWorkLedger>(),
                    Teams: provider.GetService<ITeamHub>(),
                    Output: provider.GetService<IStructuredOutput>(),
                    Planning: provider.GetService<IPlanningSessions>(),
                    Configuration: provider.GetService<IConfigurationAccess>(),
                    Briefs: provider.GetService<IBriefPublisher>(),
                    Language: provider.GetService<ILanguageService>(),
                    McpConnections: provider.GetService<IMcpConnections>(),
                    Worktrees: provider.GetService<ISessionWorktrees>(),
                    Outputs: provider.GetService<IToolOutputStore>(),
                    JobNotices: provider.GetService<IJobNotices>(),
                    Tests: provider.GetService<ITestFeedback>(),
                    Changes: provider.GetService<IWorkspaceChanges>(),
                    Knowledge: provider.GetService<IKnowledgeNotes>(),
                    Retrieval: provider.GetService<IWorkspaceRetrieval>(),
                    Exposure: provider.GetService<IToolExposure>(),
                    McpPrompts: provider.GetService<IMcpPrompts>());
                return CreateCatalog(settings, ports, roles: (name, root) => PluginComposition.ResolveRole(settings, provider.GetRequiredService<PluginCatalog>(), name, root));
            })
            .AddSingleton(provider => new ToolsApi(provider.GetRequiredService<ToolCatalog>()));
    }
}
