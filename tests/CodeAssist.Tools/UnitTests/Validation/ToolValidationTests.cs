using CodeAssist.Mcp;
using CodeAssist.Extensions.Skills;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.UnitTests;

public sealed class ToolValidationTests
{
    [Theory]
    [InlineData("config", "{\"operation\":\"unknown\"}")]
    [InlineData("config", "{\"operation\":\"set\",\"path\":\"appearance.animations\"}")]
    [InlineData("config", "{\"operation\":\"get\",\"value\":true}")]
    [InlineData("brief", "{\"title\":\"\",\"text\":\"hello\"}")]
    [InlineData("brief", "{\"title\":\"report\",\"text\":\"hello\",\"attachments\":[1]}")]
    [InlineData("brief", "{\"title\":\"report\",\"text\":\"hello\",\"attachments\":\"file\"}")]
    [InlineData("brief", "{\"title\":\"report\",\"text\":\"hello\",\"attachments\":[\"same\",\"same\"]}")]
    [InlineData("skill", "{\"id\":\"../escape\"}")]
    [InlineData("skill", "{\"id\":\"review\",\"path\":\"file\",\"input\":\"data\"}")]
    [InlineData("skill", "{\"id\":\"review\",\"path\":\" \"}")]
    [InlineData("language", "{\"operation\":\"executeCommand\",\"path\":\"code.cs\",\"language\":\"csharp\"}")]
    [InlineData("language", "{\"operation\":\"hover\",\"path\":\"code.cs\",\"language\":\"csharp\",\"line\":0}")]
    [InlineData("auth", "{\"operation\":\"connect\",\"server\":\"unknown\"}")]
    [InlineData("auth", "{\"operation\":\"token\",\"server\":\"docs\"}")]
    [InlineData("remote", "{\"operation\":\"call\",\"server\":\"docs\",\"tool\":\"read\",\"input\":[]}")]
    [InlineData("remote", "{\"operation\":\"call\",\"server\":\"docs\",\"tool\":\"../escape\",\"input\":{}}")]
    [InlineData("worktree", "{\"operation\":\"enter\",\"name\":\"../escape\"}")]
    [InlineData("worktree", "{\"operation\":\"get\",\"remove\":true}")]
    [InlineData("worktree", "{\"operation\":\"exit\",\"name\":\"unused\"}")]
    [InlineData("agent", "{\"role\":\"reviewer\",\"description\":\"review\",\"prompt\":\"task\",\"resume\":\"bad\"}")]
    [InlineData("agent", "{\"role\":\"reviewer\",\"description\":\"review\",\"prompt\":\" \"}")]
    [InlineData("find", "{\"limit\":0}")]
    [InlineData("find", "{\"server\":\" \"}")]
    [InlineData("process", "{\"command\":\"echo test\",\"shell\":\"custom.exe\"}")]
    [InlineData("process", "{\"command\":\"echo test\",\"timeoutSeconds\":86400}")]
    [InlineData("process", "{\"command\":\" \"}")]
    [InlineData("read", "{\"path\":\"file\",\"offset\":0}")]
    [InlineData("team", "{\"operation\":\"close\",\"team\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("mail", "{\"operation\":\"unknown\",\"team\":\"bad\"}")]
    public async Task Should_RejectInvalidArgumentsBeforeEffects_When_PlanningTools(string family, string json)
    {
        var tool = Create(family);
        await Should.ThrowAsync<ToolArgumentException>(async () => await tool.PlanAsync(ToolArguments.Parse(json), new ToolContext { Workspace = Path.GetTempPath() }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("config")]
    [InlineData("brief")]
    [InlineData("skill")]
    [InlineData("language")]
    [InlineData("auth")]
    [InlineData("remote")]
    [InlineData("agent")]
    [InlineData("find")]
    [InlineData("process")]
    [InlineData("read")]
    [InlineData("team")]
    [InlineData("mail")]
    public async Task Should_ObserveCancellationBeforeParsing_When_PlanningTools(string family)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await Create(family).PlanAsync(ToolArguments.Parse("{}"), new ToolContext { Workspace = Path.GetTempPath() }, cancelled.Token));
    }

    private static ITool Create(string family)
    {
        var connections = Substitute.For<IMcpConnections>();
        connections.Servers.Returns(new[] { "docs" });
        return family switch
        {
            "config" => new ConfigManageTool(Substitute.For<IConfigurationAccess>()),
            "brief" => new BriefPublishTool(Substitute.For<IBriefPublisher>(), Substitute.For<IUserInteraction>(), Substitute.For<IFileSystem>()),
            "skill" => new SkillLoadTool(Substitute.For<ISkillCatalog>()),
            "language" => new LanguageQueryTool(Substitute.For<ILanguageService>(), Substitute.For<IFileSystem>()),
            "auth" => new McpAuthTool(connections, Substitute.For<IUserInteraction>()),
            "remote" => new McpRemoteTool(connections),
            "worktree" => new SessionWorktreeTool(Substitute.For<ISessionWorktrees>(), Substitute.For<IWorkspaceBranches>()),
            "agent" => new AgentDelegateTool(Substitute.For<IDelegation>()),
            "find" => new ToolFindTool(_ => []),
            "process" => new ProcRunTool(Substitute.For<IProcessRunner>()),
            "read" => new FsReadTool(Substitute.For<IFileSystem>()),
            "team" => new TeamManageTool(Substitute.For<ITeamHub>()),
            "mail" => new TeamMailTool(Substitute.For<ITeamHub>()),
            _ => throw new ArgumentException("Unknown tool fixture."),
        };
    }
}