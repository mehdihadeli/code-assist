namespace CodeAssist.Tools;

public interface IToolOutputStore
{
    Task<Guid> SaveAsync(Guid session, string text, CancellationToken cancellationToken);
    Task<string> ReadAsync(Guid session, Guid output, int offset, int limit, CancellationToken cancellationToken);
    Task<string> ReadCharactersAsync(Guid session, Guid output, int offset, int limit, CancellationToken cancellationToken) => throw new NotSupportedException("Character paging is unavailable in this output store.");
}