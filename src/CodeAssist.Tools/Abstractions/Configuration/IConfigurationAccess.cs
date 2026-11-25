using System.Text.Json;

namespace CodeAssist.Tools;

public interface IConfigurationAccess
{
    Task<JsonElement> ReadAsync(string? path, CancellationToken cancellationToken);
    Task<JsonElement> ChangeAsync(string operation, string path, JsonElement? value, string revision, CancellationToken cancellationToken);
}