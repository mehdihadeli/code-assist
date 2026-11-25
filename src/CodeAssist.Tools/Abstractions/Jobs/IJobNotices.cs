using CodeAssist.Persistence;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public interface IJobNotices
{
    Task<IReadOnlyList<JobNoticeRecorded>> ReadAsync(Guid sessionId, CancellationToken cancellationToken);
    Task AcknowledgeAsync(Guid sessionId, Guid jobId, CancellationToken cancellationToken);
}