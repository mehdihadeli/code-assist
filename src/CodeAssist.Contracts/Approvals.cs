namespace CodeAssist.Contracts;

public enum ToolRisk { Read, Write, Execute, Network }
public enum ApprovalDecision { Allow, Deny }
public enum ApprovalGrant { Once, Session, Project }