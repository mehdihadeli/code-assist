using CodeAssist.Extensions.Skills;
using System.Text.Json;
using CodeAssist.Server.Composition;
using CodeAssist.Configuration;
using CodeAssist.Contracts;
using CodeAssist.Tools;
using CodeAssist.Extensions.Plugins;
using CodeAssist.Mcp;

namespace CodeAssist.Server.Management;

public sealed class SkillManagementService(CodeAssistSettings settings, SettingsLocations locations)
{
    public async Task<ManagementReply> ExecuteAsync(string operation, SkillManagementArguments options, CancellationToken cancellation)
    {
        ManagementReply? reply = null;

        var catalog = SkillComposition.CreateCatalog(settings, locations);
        var callContext = new ToolContext { Workspace = locations.WorkingDirectory };
        var caller = options.Agent ? SkillCaller.Agent : SkillCaller.Human;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (operation == "diagnose")
            {
                var report = SkillComposition.CreateLocalCatalog(settings, locations).Discover(callContext);
                Write(report, report.Issues.Count == 0 ? "Local skill discovery checks passed." : string.Join("\n", report.Issues.Select(issue => $"{issue.Origin}/{issue.Path}: {issue.Code}: {issue.Message}")));
                return reply! with { ExitCode = report.Issues.Any(issue => issue.Code is "invalid-skill" or "source-unreadable" or "duplicate-id") ? 2 : 0 };
            }
            if (operation == "list")
            {
                var entries = catalog.Describe(callContext, caller);
                Write(entries, entries.Count == 0 ? "No admitted skills." : string.Join("\n", entries.Select(item => $"{item.Id}  [{item.Origin}]  {item.Description}")));
            }
            else
            {
                var id = options.Id ?? throw new ArgumentException("Skill expansion requires an id.");
                var content = await catalog.InvokeAsync(id, new(caller, options.Input, options.Arguments, Files: options.Files), callContext, cancellation)
                    ?? throw new ArgumentException("Skill is missing, hidden or not admitted for this caller.");
                Write(content, content.Body + (content.Route is null ? "" : $"\n\nIsolated role route: {content.Route}. Expansion starts no child."));
            }
            return reply ?? new(JsonSerializer.SerializeToElement(new { }), "");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            throw new ArgumentException(exception.Message);
        }

        void Write(object value, string text) => reply = new(JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web), text);
    }
}
