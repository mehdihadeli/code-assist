using CodeAssist.Contracts;
using System.Text.Json.Serialization;

namespace CodeAssist.Persistence;

/// <summary>Append-only fact about a session. <see cref="Seq"/> is assigned by the journal store and is strictly increasing per session.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionStarted), "session.started")]
[JsonDerivedType(typeof(SessionRenamed), "session.renamed")]
[JsonDerivedType(typeof(SessionRelocationRequested), "session.relocation.requested")]
[JsonDerivedType(typeof(SessionRelocated), "session.relocated")]
[JsonDerivedType(typeof(TodoUpdated), "todo.updated")]
[JsonDerivedType(typeof(WorkItemChanged), "work.item.changed")]
[JsonDerivedType(typeof(TeamChanged), "team.changed")]
[JsonDerivedType(typeof(TeamLetterSent), "team.letter.sent")]
[JsonDerivedType(typeof(TeamLetterAcknowledged), "team.letter.acknowledged")]
[JsonDerivedType(typeof(StructuredResultSubmitted), "output.submitted")]
[JsonDerivedType(typeof(PlanningChanged), "planning.changed")]
[JsonDerivedType(typeof(WorktreeChanged), "worktree.changed")]
[JsonDerivedType(typeof(DelegationLinked), "delegation.linked")]
[JsonDerivedType(typeof(DelegationModelSelected), "delegation.model.selected")]
[JsonDerivedType(typeof(JobNoticeRecorded), "job.notice.recorded")]
[JsonDerivedType(typeof(JobNoticeAcknowledged), "job.notice.acknowledged")]
[JsonDerivedType(typeof(AgentMessageRecorded), "agent.message.recorded")]
[JsonDerivedType(typeof(DelegationUsage), "delegation.usage")]
[JsonDerivedType(typeof(PromptSubmitted), "prompt.submitted")]
[JsonDerivedType(typeof(AssistantMessage), "assistant.message")]
[JsonDerivedType(typeof(ToolRequested), "tool.requested")]
[JsonDerivedType(typeof(ApprovalRequested), "approval.requested")]
[JsonDerivedType(typeof(ApprovalResolved), "approval.resolved")]
[JsonDerivedType(typeof(ToolCompleted), "tool.completed")]
[JsonDerivedType(typeof(TurnCompleted), "turn.completed")]
[JsonDerivedType(typeof(ErrorRaised), "error")]
public abstract record JournalEntry(Guid SessionId, long Seq, DateTimeOffset At);

public sealed record SessionStarted(Guid SessionId, long Seq, DateTimeOffset At, string Workspace, string? Model) : JournalEntry(SessionId, Seq, At);
public sealed record SessionRenamed(Guid SessionId, long Seq, DateTimeOffset At, string Title) : JournalEntry(SessionId, Seq, At);
public sealed record SessionRelocationRequested(Guid SessionId, long Seq, DateTimeOffset At, Guid RequestId, string Directory) : JournalEntry(SessionId, Seq, At);
public sealed record SessionRelocated(Guid SessionId, long Seq, DateTimeOffset At, Guid RequestId, string Directory) : JournalEntry(SessionId, Seq, At);
public sealed record TodoRecord(string Content, string Status);
public sealed record TodoUpdated(Guid SessionId, long Seq, DateTimeOffset At, IReadOnlyList<TodoRecord> Items) : JournalEntry(SessionId, Seq, At);
public sealed record PromptSubmitted(Guid SessionId, long Seq, DateTimeOffset At, string Text) : JournalEntry(SessionId, Seq, At);
public sealed record AssistantMessage(Guid SessionId, long Seq, DateTimeOffset At, string Text) : JournalEntry(SessionId, Seq, At);
public sealed record ToolRequested(Guid SessionId, long Seq, DateTimeOffset At, string CallId, string Tool, string Arguments, ToolRisk Risk) : JournalEntry(SessionId, Seq, At);
public sealed record ApprovalRequested(Guid SessionId, long Seq, DateTimeOffset At, Guid ApprovalId, string CallId, string Summary) : JournalEntry(SessionId, Seq, At);
public sealed record ApprovalResolved(Guid SessionId, long Seq, DateTimeOffset At, Guid ApprovalId, ApprovalDecision Decision, ApprovalGrant Grant) : JournalEntry(SessionId, Seq, At);
public sealed record ToolCompleted(Guid SessionId, long Seq, DateTimeOffset At, string CallId, string Output, bool IsError) : JournalEntry(SessionId, Seq, At);
public sealed record TurnCompleted(Guid SessionId, long Seq, DateTimeOffset At, long InputTokens, long OutputTokens) : JournalEntry(SessionId, Seq, At);
public sealed record ErrorRaised(Guid SessionId, long Seq, DateTimeOffset At, string Code, string Message) : JournalEntry(SessionId, Seq, At);

/// <summary>Idempotent request from a surface; the host reacts only by appending journal entries.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SubmitPrompt), "submit.prompt")]
[JsonDerivedType(typeof(ResolveApproval), "resolve.approval")]
[JsonDerivedType(typeof(CancelTurn), "cancel.turn")]
public abstract record Intent(Guid SessionId, Guid IntentId);

public sealed record SubmitPrompt(Guid SessionId, Guid IntentId, string Text) : Intent(SessionId, IntentId);
public sealed record ResolveApproval(Guid SessionId, Guid IntentId, Guid ApprovalId, ApprovalDecision Decision, ApprovalGrant Grant) : Intent(SessionId, IntentId);
public sealed record CancelTurn(Guid SessionId, Guid IntentId) : Intent(SessionId, IntentId);
