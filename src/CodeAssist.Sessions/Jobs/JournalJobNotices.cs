using CodeAssist.Persistence;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Sessions;

public sealed class JournalJobNotices(IJournalStore journal) : IJobNotices
{
    public async Task RecordAsync(ToolJob job, CancellationToken cancellationToken)
    {
        if (job.Status == ToolJobStatus.Running || job.SessionId == Guid.Empty) throw new ArgumentException("Completion requires a terminal job and real session.");
        await AppendAsync(job.SessionId, entries => entries.OfType<JobNoticeRecorded>().Any(entry => entry.JobId == job.Id) ? null :
            new JobNoticeRecorded(job.SessionId, 0, default, job.Id, job.Description, job.Status.ToString(), job.Result?.Text ?? "", job.Result?.OutputId), cancellationToken);
    }

    public async Task<IReadOnlyList<JobNoticeRecorded>> ReadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var entries = await journal.ReadAsync(sessionId, cancellationToken);
        var acknowledged = entries.OfType<JobNoticeAcknowledged>().Select(entry => entry.JobId).ToHashSet();
        return entries.OfType<JobNoticeRecorded>().Where(entry => !acknowledged.Contains(entry.JobId)).Take(100).ToArray();
    }

    public Task AcknowledgeAsync(Guid sessionId, Guid jobId, CancellationToken cancellationToken) => AppendAsync(sessionId, entries =>
    {
        if (!entries.OfType<JobNoticeRecorded>().Any(entry => entry.JobId == jobId)) throw new UnauthorizedAccessException("Completion notice does not belong to this session.");
        return entries.OfType<JobNoticeAcknowledged>().Any(entry => entry.JobId == jobId) ? null : new JobNoticeAcknowledged(sessionId, 0, default, jobId);
    }, cancellationToken);

    private async Task AppendAsync(Guid sessionId, Func<IReadOnlyList<JournalEntry>, JournalEntry?> create, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var entries = await journal.ReadAsync(sessionId, cancellationToken);
            if (entries.FirstOrDefault() is not SessionStarted) throw new UnauthorizedAccessException("Session does not exist.");
            var entry = create(entries);
            if (entry is null) return;
            try { await journal.AppendAsync(entry, entries.Last().Seq, cancellationToken); return; }
            catch (JournalConflictException) when (attempt < 3) { }
        }
        throw new JournalConflictException();
    }
}