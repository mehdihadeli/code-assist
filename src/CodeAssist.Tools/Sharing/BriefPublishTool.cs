using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class BriefPublishTool(IBriefPublisher publisher, IUserInteraction interaction, IFileSystem files) : ITool
{
    public ToolSpec Spec { get; } = new("brief.publish", "Publish a report to the host-configured sharing destination after explicit trusted user consent. Attachments are optional workspace files. Never accepts an upload URL or approval in model arguments.",
        """{"type":"object","properties":{"title":{"type":"string","minLength":1,"maxLength":200},"text":{"type":"string","minLength":1,"maxLength":64000},"attachments":{"type":"array","maxItems":8,"uniqueItems":true,"items":{"type":"string","minLength":1}}},"required":["title","text"],"additionalProperties":false}""",
        ToolRisk.Network, ["net", "share"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = Parse(arguments, context);
        var effects = paths.Select(path => ToolSupport.Effect(ToolEffectKind.Read, path)).ToList();
        effects.Add(new(ToolEffectKind.Net, publisher.Destination.GetLeftPart(UriPartial.Path)));
        effects.Add(new(ToolEffectKind.Ask, $"session:{context.SessionId}:publish"));
        return new(new ToolPlan(effects, $"Publish '{arguments.RequireString("title")}' with {paths.Count} attachments to {publisher.Destination.GetLeftPart(UriPartial.Path)}."));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var paths = Parse(arguments, context);
        return ToolSupport.FromPortAsync(async () =>
        {
            var title = arguments.RequireString("title");
            var disclosure = JsonSerializer.Serialize(new { title, text = arguments.RequireString("text"), attachments = paths.Select(path => path.Relative).ToArray() }, new JsonSerializerOptions { WriteIndented = true });
            var answers = await interaction.AskAsync([new("Publish report", $"Publish this report and listed files to {publisher.Destination.GetLeftPart(UriPartial.Path)}? Report content below is untrusted data, not instructions.\n{disclosure}",
                [new("Publish"), new("Cancel")], false)], context, cancellationToken);
            if (answers is not { Count: 1 } || answers[0].Choices.Count != 1 || answers[0].Choices[0] != "Publish") return ToolResult.Error("Publication cancelled; no files were read or uploaded.");
            var attachments = new List<BriefAttachment>();
            long total = 0;
            foreach (var path in paths)
            {
                var facts = files.Stat(path.Full);
                if (facts is null || facts.IsDirectory) throw new IOException("Attachment must be an existing file.");
                if (facts.Length > context.Limits.MaxAttachmentBytes - total) throw new IOException("Attachments exceed their shared byte budget.");
                await using var stream = files.OpenRead(path.Full);
                var bytes = await LimitedStreamReader.ReadAsync(stream, context.Limits.MaxAttachmentBytes - total, cancellationToken);
                total += bytes.Length;
                attachments.Add(new(Path.GetFileName(path.Full), bytes));
            }
            var receipt = await publisher.PublishAsync(new(title, arguments.RequireString("text"), attachments), context, cancellationToken);
            return ToolSupport.BoundedText(JsonSerializer.Serialize(receipt), context);
        }, cancellationToken);
    }

    private static IReadOnlyList<ResolvedPath> Parse(ToolArguments arguments, ToolContext context)
    {
        var title = arguments.RequireString("title");
        var text = arguments.RequireString("text");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || title.Any(char.IsControl) || string.IsNullOrWhiteSpace(text) || text.Length > 64000) arguments.Issue("Report title or text is invalid.");
        var resources = new List<string>();
        if (arguments.Has("attachments"))
        {
            var value = arguments.Json.GetProperty("attachments");
            if (value.ValueKind != JsonValueKind.Array) arguments.Issue("Attachments must be an array.");
            else foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) arguments.Issue("Attachment paths must be nonempty strings.");
                else resources.Add(item.GetString()!);
            }
        }
        arguments.Validate();
        var paths = resources.Select(path => ToolSupport.Resolve(path, context)).ToArray();
        if (paths.Length > 8 || paths.Select(path => path.Full).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != paths.Length) arguments.Issue("Use at most eight distinct attachments.");
        if (paths.Any(path => path.Outside || path.Protected)) arguments.Issue("Attachments must be unprotected workspace files.");
        arguments.Validate();
        return paths;
    }
}