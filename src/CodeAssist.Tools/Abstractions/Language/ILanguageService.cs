using System.Text.Json;

namespace CodeAssist.Tools;

public interface ILanguageService
{
    string Server { get; }
    Task<JsonElement> QueryAsync(LanguageQuery query, ToolContext context, CancellationToken cancellationToken);
}