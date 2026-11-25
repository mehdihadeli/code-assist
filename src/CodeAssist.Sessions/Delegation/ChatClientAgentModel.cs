using CodeAssist.Persistence;
using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;
using Microsoft.Extensions.AI;

namespace CodeAssist.Sessions;

public sealed class ChatClientAgentModel(Func<string, IChatClient> clients, IToolExposure? exposure = null) : IAgentModel
{
    public bool ReportsCost => false;

    public async Task<AgentModelReply> RespondAsync(AgentModelRequest request, ToolContext context, CancellationToken cancellationToken)
    {
        using var client = clients(request.Model);
        var messages = new List<ChatMessage> { new(ChatRole.System, request.Instructions) };
        foreach (var message in request.Messages)
        {
            if (message.Role == "tool") messages.Add(new(ChatRole.Tool, [new FunctionResultContent(message.CallId!, message.Text)]));
            else
            {
                var contents = new List<AIContent>();
                if (message.Text.Length > 0) contents.Add(new TextContent(message.Text));
                contents.AddRange(message.Calls.Select(call => new FunctionCallContent(call.Id, call.Name, JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments))));
                messages.Add(new(message.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, contents));
            }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(context.Limits.ProcessTimeout);
        var response = await client.GetResponseAsync(messages, new ChatOptions
        {
            MaxOutputTokens = (int)Math.Clamp(request.RemainingTokens, 1, 8192),
            Tools = (exposure?.Select(request.Tools, context) ?? request.Tools).Select(tool => (AITool)new AgentFunctionDeclaration(tool)).ToList(),
        }, deadline.Token);
        if (response.Usage?.InputTokenCount is not { } input || response.Usage.OutputTokenCount is not { } output)
            throw new InvalidOperationException("Delegation requires provider usage accounting.");
        var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .Select(call => new AgentCall(call.CallId, call.Name, JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>()))).ToArray();
        return new(response.Text, calls, input, output);
    }
}