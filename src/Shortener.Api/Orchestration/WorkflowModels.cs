using System.Collections.Concurrent;

namespace Shortener.Api.Orchestration;

public enum RunStatus
{
    Running,
    AwaitingClarification,
    AwaitingApproval,
    Succeeded,
    Failed,
    SafeStopped
}

public enum StageStatus
{
    Pending,
    Running,
    AwaitingClarification,
    AwaitingApproval,
    Succeeded,
    Failed,
    Rejected,
    Blocked
}

public sealed record StartRunRequest(
    string Scenario,
    string? Requirement = null,
    bool InjectTransientFailure = false,
    bool InjectDocumentationFailure = false,
    bool InjectValidationFailure = false);

public sealed record ApprovalRequest(bool Approve, string Decision, string Actor);
public sealed record ClarificationRequest(string Answer, string Actor);
public sealed record ReplanRequest(
    string Requirement,
    string Rationale,
    string Actor,
    bool InjectTransientFailure = false,
    bool InjectDocumentationFailure = false,
    bool InjectValidationFailure = false);
public sealed record SafeStopRequest(string Reason, string Actor);

public sealed record WorkflowStageDefinition(
    string Id,
    string Title,
    IReadOnlyList<string> DependsOn,
    string EntryGate,
    string ExitGate,
    bool RequiresHumanApproval = false);

public sealed class StageExecution
{
    public required WorkflowStageDefinition Definition { get; init; }
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public int Attempts { get; set; }
    public bool UsedFallback { get; set; }
    public string? Output { get; set; }
    public string? Failure { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed record DecisionRecord(
    int PlanVersion,
    DateTimeOffset AtUtc,
    string Actor,
    string Decision,
    string Rationale);

public sealed class WorkflowRun
{
    public Guid Id { get; init; }
    public required string Scenario { get; init; }
    public required string Requirement { get; set; }
    public RunStatus Status { get; set; }
    public int PlanVersion { get; set; } = 1;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int RetryCount { get; set; }
    public int RollbackCount { get; set; }
    public double? MttrMilliseconds { get; set; }
    public DateTimeOffset? RecoveryStartedAtUtc { get; set; }
    public string? SafeStopReason { get; set; }
    public List<StageExecution> Stages { get; init; } = [];
    public ConcurrentDictionary<string, string> Context { get; init; } = new();
    public List<DecisionRecord> Decisions { get; init; } = [];
}

public sealed record WorkflowMetrics(
    int TotalRuns,
    int SucceededRuns,
    int FailedRuns,
    double SuccessRate,
    int RetryCount,
    int RollbackCount,
    double RetryFrequency,
    double RollbackFrequency,
    double AverageMttrMilliseconds,
    double AverageEndToEndLatencyMilliseconds);

public sealed record ScenarioDescription(string Id, string Title, string Requirement, string Purpose);

public sealed record AuditEvent(
    long Sequence,
    DateTimeOffset AtUtc,
    Guid RunId,
    int PlanVersion,
    string Actor,
    string Action,
    string? StageId,
    string Detail,
    string PreviousHash,
    string Hash);

public sealed record AuditResponse(IReadOnlyList<AuditEvent> Events, bool HashChainValid);