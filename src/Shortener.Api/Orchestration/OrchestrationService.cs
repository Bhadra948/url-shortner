using System.Collections.Concurrent;

namespace Shortener.Api.Orchestration;

public sealed class OrchestrationService(AuditTrail audit, TimeProvider clock, IAgentProvider agentProvider)
{
    private const int MaxAttempts = 3;
    private readonly ConcurrentDictionary<Guid, RunEnvelope> _runs = new();

    private static readonly WorkflowStageDefinition[] Plan =
    [
        new("requirements", "Normalize requirements", [], "Input is non-empty", "Intent and open questions are recorded"),
        new("architecture", "Architecture and risk design", ["requirements"], "Requirements are normalized", "Boundaries, risks, and gates are explicit"),
        new("api-implementation", "Implement service/API", ["architecture"], "Architecture is approved by policy", "API output and ownership are recorded"),
        new("ui-implementation", "Implement operator experience", ["architecture"], "Architecture is approved by policy", "Operator workflow output is recorded"),
        new("validation", "Test and security validation", ["api-implementation", "ui-implementation"], "Both implementation branches completed", "Tests and policy checks pass"),
        new("documentation", "Documentation and change record", ["api-implementation", "ui-implementation"], "Implementation outputs are frozen", "Setup, risks, and decisions are documented"),
        new("release-readiness", "Release readiness decision", ["validation", "documentation"], "Validation and documentation gates pass", "Human approval is recorded before release", true)
    ];

    private static readonly ScenarioDescription[] Scenarios =
    [
        new("greenfield", "Greenfield", "Build a URL shortener with expiring links, analytics, and safe redirects.", "Create a new service with testable persistence and release controls."),
        new("brownfield", "Brownfield", "Improve the existing shortener's redirect reliability without breaking current API behavior.", "Trace existing contracts, minimize blast radius, and add regression coverage."),
        new("ambiguous", "Ambiguous requirement", "Make redirects faster while preserving the right behavior.", "Expose missing scope and require human clarification before planning continues.")
    ];

    public IReadOnlyList<ScenarioDescription> GetScenarios() => Scenarios;

    public async Task<WorkflowRun> StartAsync(StartRunRequest request, CancellationToken cancellationToken)
    {
        var scenario = Scenarios.SingleOrDefault(item => item.Id.Equals(request.Scenario, StringComparison.OrdinalIgnoreCase));
        if (scenario is null)
        {
            throw new ArgumentException("Scenario must be greenfield, brownfield, or ambiguous.");
        }

        var requirement = string.IsNullOrWhiteSpace(request.Requirement) ? scenario.Requirement : request.Requirement.Trim();
        PolicyGuard.ValidateRequirement(requirement);
        var now = clock.GetUtcNow();
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            Scenario = scenario.Id,
            Requirement = requirement,
            Status = RunStatus.Running,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            Stages = Plan.Select(definition => new StageExecution { Definition = definition }).ToList()
        };
        var envelope = new RunEnvelope(run, request);
        if (!_runs.TryAdd(run.Id, envelope))
        {
            throw new InvalidOperationException("Could not allocate a workflow run.");
        }

        await audit.AppendAsync(run.Id, run.PlanVersion, "system", "run.created", null,
            $"scenario={run.Scenario}; requirement={run.Requirement}", cancellationToken);

        if (run.Scenario == "ambiguous")
        {
            run.Status = RunStatus.AwaitingClarification;
            run.Stages[0].Status = StageStatus.AwaitingClarification;
            const string clarificationPrompt = "Clarify target latency, expected traffic, and whether caching is allowed.";
            run.Stages[0].Failure = clarificationPrompt;
            run.UpdatedAtUtc = clock.GetUtcNow();
            await audit.AppendAsync(run.Id, run.PlanVersion, "system", "gate.clarification.required", "requirements",
                clarificationPrompt, cancellationToken);
        }
        else
        {
            await ExecuteReadyStagesAsync(envelope, cancellationToken);
        }

        return Snapshot(run);
    }

    public IReadOnlyList<WorkflowRun> GetRuns() => _runs.Values
        .Select(envelope => Snapshot(envelope.Run))
        .OrderByDescending(run => run.StartedAtUtc)
        .ToArray();

    public WorkflowRun? GetRun(Guid runId) =>
        _runs.TryGetValue(runId, out var envelope) ? Snapshot(envelope.Run) : null;

    public async Task<WorkflowRun?> ApproveAsync(
        Guid runId, ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(runId, out var envelope)) return null;
        var run = envelope.Run;
        var resume = false;
        await envelope.Mutex.WaitAsync(cancellationToken);
        try
        {
            var stage = run.Stages.Single(item => item.Definition.Id == "release-readiness");
            if (run.Status != RunStatus.AwaitingApproval || stage.Status != StageStatus.AwaitingApproval)
            {
                throw new InvalidOperationException("This run is not waiting for release approval.");
            }

            PolicyGuard.ValidateActor(request.Actor);
            PolicyGuard.ValidateSafeText(request.Decision, "Decision", 1000);
            AddDecision(run, request.Actor, request.Approve ? "release.approved" : "release.rejected", request.Decision);
            await audit.AppendAsync(run.Id, run.PlanVersion, request.Actor,
                request.Approve ? "release.approved" : "release.rejected", stage.Definition.Id,
                request.Decision, cancellationToken);
            if (!request.Approve)
            {
                stage.Status = StageStatus.Rejected;
                stage.Failure = request.Decision;
                run.Status = RunStatus.Failed;
                run.CompletedAtUtc = clock.GetUtcNow();
                run.UpdatedAtUtc = run.CompletedAtUtc.Value;
            }
            else
            {
                stage.Status = StageStatus.Succeeded;
                stage.Output = "Release readiness approved by a human reviewer. No deployment was performed.";
                stage.CompletedAtUtc = clock.GetUtcNow();
                run.Context[stage.Definition.Id] = stage.Output;
                run.Status = RunStatus.Running;
                resume = true;
            }
        }
        finally
        {
            envelope.Mutex.Release();
        }

        if (resume) await ExecuteReadyStagesAsync(envelope, cancellationToken);
        return Snapshot(run);
    }

    public async Task<WorkflowRun?> ClarifyAsync(
        Guid runId, ClarificationRequest request, CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(runId, out var envelope)) return null;
        var run = envelope.Run;
        await envelope.Mutex.WaitAsync(cancellationToken);
        try
        {
            if (run.Status != RunStatus.AwaitingClarification || string.IsNullOrWhiteSpace(request.Answer))
            {
                throw new InvalidOperationException("A non-empty clarification is required at the clarification gate.");
            }

            PolicyGuard.ValidateActor(request.Actor);
            PolicyGuard.ValidateRequirement(request.Answer);
            run.Requirement = $"{run.Requirement} Clarification: {request.Answer.Trim()}";
            var stage = run.Stages[0];
            stage.Status = StageStatus.Succeeded;
            stage.Failure = null;
            stage.Output = $"Requirement clarified by {request.Actor}: {request.Answer.Trim()}";
            stage.CompletedAtUtc = clock.GetUtcNow();
            run.Context[stage.Definition.Id] = stage.Output;
            AddDecision(run, request.Actor, "requirements.clarified", request.Answer.Trim());
            run.Status = RunStatus.Running;
            await audit.AppendAsync(run.Id, run.PlanVersion, request.Actor, "requirements.clarified",
                stage.Definition.Id, request.Answer.Trim(), cancellationToken);
        }
        finally
        {
            envelope.Mutex.Release();
        }

        await ExecuteReadyStagesAsync(envelope, cancellationToken);
        return Snapshot(run);
    }

    public async Task<WorkflowRun?> ReplanAsync(
        Guid runId, ReplanRequest request, CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(runId, out var envelope)) return null;
        var run = envelope.Run;
        await envelope.Mutex.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(request.Requirement) || string.IsNullOrWhiteSpace(request.Rationale))
            {
                throw new ArgumentException("A replacement requirement and replan rationale are required.");
            }

            var now = clock.GetUtcNow();
            PolicyGuard.ValidateRequirement(request.Requirement);
            PolicyGuard.ValidateActor(request.Actor);
            PolicyGuard.ValidateSafeText(request.Rationale, "Rationale", 1000);
            envelope.StopSource.Dispose();
            envelope.StopSource = new CancellationTokenSource();
            run.RecoveryStartedAtUtc = run.Status is RunStatus.Failed or RunStatus.SafeStopped
                ? run.CompletedAtUtc ?? now
                : null;
            run.RollbackCount++;
            await audit.AppendAsync(run.Id, run.PlanVersion, request.Actor, "plan.rollback", null,
                $"Superseded plan {run.PlanVersion}: {request.Rationale}", cancellationToken);
            run.PlanVersion++;
            run.Requirement = request.Requirement.Trim();
            envelope.Request = new StartRunRequest(
                run.Scenario,
                run.Requirement,
                request.InjectTransientFailure,
                request.InjectDocumentationFailure,
                request.InjectValidationFailure);
            run.StartedAtUtc = now;
            run.CompletedAtUtc = null;
            run.Status = RunStatus.Running;
            run.SafeStopReason = null;
            run.Context.Clear();
            foreach (var stage in run.Stages)
            {
                stage.Status = StageStatus.Pending;
                stage.Attempts = 0;
                stage.UsedFallback = false;
                stage.Output = null;
                stage.Failure = null;
                stage.StartedAtUtc = null;
                stage.CompletedAtUtc = null;
            }

            AddDecision(run, request.Actor, "plan.replanned", request.Rationale);
            run.UpdatedAtUtc = now;
            await audit.AppendAsync(run.Id, run.PlanVersion, request.Actor, "plan.replanned", null,
                request.Rationale, cancellationToken);
        }
        finally
        {
            envelope.Mutex.Release();
        }

        await ExecuteReadyStagesAsync(envelope, cancellationToken);
        return Snapshot(run);
    }

    public async Task<WorkflowRun?> SafeStopAsync(
        Guid runId, SafeStopRequest request, CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(runId, out var envelope)) return null;
        await envelope.Mutex.WaitAsync(cancellationToken);
        try
        {
            var run = envelope.Run;
            if (IsTerminal(run.Status)) return Snapshot(run);
            PolicyGuard.ValidateActor(request.Actor);
            PolicyGuard.ValidateSafeText(request.Reason, "Safe-stop reason", 1000);
            run.Status = RunStatus.SafeStopped;
            run.SafeStopReason = request.Reason;
            run.CompletedAtUtc = clock.GetUtcNow();
            run.UpdatedAtUtc = run.CompletedAtUtc.Value;
            envelope.StopSource.Cancel();
            foreach (var stage in run.Stages.Where(stage => stage.Status is StageStatus.Pending
                or StageStatus.Running or StageStatus.AwaitingApproval or StageStatus.AwaitingClarification))
            {
                stage.Status = StageStatus.Blocked;
            }

            AddDecision(run, request.Actor, "run.safe-stopped", request.Reason);
            await audit.AppendAsync(run.Id, run.PlanVersion, request.Actor, "run.safe-stopped", null,
                request.Reason, cancellationToken);
            await envelope.ExecutionMutex.WaitAsync(cancellationToken);
            envelope.ExecutionMutex.Release();
            return Snapshot(run);
        }
        finally
        {
            envelope.Mutex.Release();
        }
    }

    public WorkflowMetrics GetMetrics()
    {
        var runs = _runs.Values.Select(envelope => envelope.Run).ToArray();
        var completed = runs.Where(run => run.Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.SafeStopped).ToArray();
        var succeeded = completed.Count(run => run.Status == RunStatus.Succeeded);
        var durations = completed
            .Where(run => run.CompletedAtUtc.HasValue)
            .Select(run => (run.CompletedAtUtc!.Value - run.StartedAtUtc).TotalMilliseconds)
            .ToArray();

        return new WorkflowMetrics(
            runs.Length,
            succeeded,
            completed.Length - succeeded,
            completed.Length == 0 ? 0 : (double)succeeded / completed.Length,
            runs.Sum(run => run.RetryCount),
            runs.Sum(run => run.RollbackCount),
            runs.Length == 0 ? 0 : (double)runs.Count(run => run.RetryCount > 0) / runs.Length,
            runs.Length == 0 ? 0 : (double)runs.Count(run => run.RollbackCount > 0) / runs.Length,
            runs.Where(run => run.MttrMilliseconds.HasValue).Select(run => run.MttrMilliseconds!.Value).DefaultIfEmpty().Average(),
            durations.DefaultIfEmpty().Average());
    }

    public AuditResponse GetAudit(Guid runId) => new(audit.GetRunEvents(runId), audit.VerifyHashChain());

    private async Task ExecuteReadyStagesAsync(RunEnvelope envelope, CancellationToken cancellationToken)
    {
        await envelope.ExecutionMutex.WaitAsync(cancellationToken);
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, envelope.StopSource.Token);
            try
            {
                await ExecuteReadyStagesCoreAsync(envelope, linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (envelope.StopSource.IsCancellationRequested)
            {
            }
        }
        finally
        {
            envelope.ExecutionMutex.Release();
        }
    }

    private async Task ExecuteReadyStagesCoreAsync(RunEnvelope envelope, CancellationToken cancellationToken)
    {
        var run = envelope.Run;
        while (run.Status == RunStatus.Running)
        {
            var ready = run.Stages.Where(stage =>
                stage.Status == StageStatus.Pending
                && stage.Definition.DependsOn.All(dependency =>
                    run.Stages.Single(item => item.Definition.Id == dependency).Status == StageStatus.Succeeded)).ToArray();

            if (ready.Length == 0)
            {
                if (run.Stages.All(stage => stage.Status == StageStatus.Succeeded))
                {
                    run.Status = RunStatus.Succeeded;
                    run.CompletedAtUtc = clock.GetUtcNow();
                    run.UpdatedAtUtc = run.CompletedAtUtc.Value;
                    if (run.RecoveryStartedAtUtc is { } recoveryStartedAtUtc)
                    {
                        run.MttrMilliseconds = (run.CompletedAtUtc.Value - recoveryStartedAtUtc).TotalMilliseconds;
                        run.RecoveryStartedAtUtc = null;
                    }

                    await audit.AppendAsync(run.Id, run.PlanVersion, "system", "run.succeeded", null,
                        "All dependency and exit gates passed.", cancellationToken);
                }

                return;
            }

            var approvalStage = ready.FirstOrDefault(stage => stage.Definition.RequiresHumanApproval);
            if (approvalStage is not null)
            {
                approvalStage.Status = StageStatus.AwaitingApproval;
                approvalStage.StartedAtUtc = clock.GetUtcNow();
                run.Status = RunStatus.AwaitingApproval;
                run.UpdatedAtUtc = clock.GetUtcNow();
                await audit.AppendAsync(run.Id, run.PlanVersion, "system", "gate.approval.required",
                    approvalStage.Definition.Id, "Release approval is required; no external action will be taken.", cancellationToken);
                return;
            }

            foreach (var stage in ready)
            {
                stage.Status = StageStatus.Running;
                stage.StartedAtUtc = clock.GetUtcNow();
                await audit.AppendAsync(run.Id, run.PlanVersion, "system", "stage.started",
                    stage.Definition.Id, stage.Definition.EntryGate, cancellationToken);
            }

            await Task.WhenAll(ready.Select(stage => ExecuteStageAsync(envelope, stage, cancellationToken)));
            run.UpdatedAtUtc = clock.GetUtcNow();
            var failedStage = ready.FirstOrDefault(stage => stage.Status == StageStatus.Failed);
            if (failedStage is not null)
            {
                run.Status = RunStatus.Failed;
                run.CompletedAtUtc = clock.GetUtcNow();
                foreach (var pending in run.Stages.Where(stage => stage.Status == StageStatus.Pending))
                {
                    pending.Status = StageStatus.Blocked;
                }

                await audit.AppendAsync(run.Id, run.PlanVersion, "system", "run.failed", failedStage.Definition.Id,
                    failedStage.Failure ?? "Stage failed.", cancellationToken);
                return;
            }
        }
    }

    private async Task ExecuteStageAsync(
        RunEnvelope envelope, StageExecution stage, CancellationToken cancellationToken)
    {
        var run = envelope.Run;
        var stageId = stage.Definition.Id;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (envelope.StopSource.IsCancellationRequested)
            {
                stage.Status = StageStatus.Blocked;
                return;
            }

            stage.Attempts = attempt;
            var isTransientFailure = envelope.Request.InjectTransientFailure
                && stageId == "api-implementation" && attempt == 1;
            var isDocumentationFailure = envelope.Request.InjectDocumentationFailure
                && stageId == "documentation" && attempt == 1;
            var isValidationFailure = envelope.Request.InjectValidationFailure && stageId == "validation";

            if (isTransientFailure || isDocumentationFailure || isValidationFailure)
            {
                var reason = isTransientFailure ? "Injected transient provider timeout."
                    : isDocumentationFailure ? "Injected documentation generator failure."
                    : "Injected validation gate failure.";
                await audit.AppendAsync(run.Id, run.PlanVersion, "executor", "stage.attempt.failed",
                    stageId, $"attempt={attempt}; {reason}", cancellationToken);

                if (isDocumentationFailure)
                {
                    stage.UsedFallback = true;
                    stage.Output = "Fallback: deterministic documentation template generated from the execution record.";
                    break;
                }

                if (isValidationFailure || attempt == MaxAttempts)
                {
                    stage.Status = StageStatus.Failed;
                    stage.Failure = reason;
                    stage.CompletedAtUtc = clock.GetUtcNow();
                    return;
                }

                run.RetryCount++;
                await audit.AppendAsync(run.Id, run.PlanVersion, "executor", "stage.retry.scheduled",
                    stageId, $"nextAttempt={attempt + 1}; maxAttempts={MaxAttempts}", cancellationToken);
                continue;
            }

            try
            {
                var upstreamOutputs = new ConcurrentDictionary<string, string>(run.Context);
                var result = await agentProvider.ExecuteAsync(new AgentStageContext(
                    run.Id,
                    run.PlanVersion,
                    run.Scenario,
                    run.Requirement,
                    stageId,
                    upstreamOutputs), cancellationToken);
                if (envelope.StopSource.IsCancellationRequested)
                {
                    stage.Status = StageStatus.Blocked;
                    return;
                }

                if (string.IsNullOrWhiteSpace(result.Output))
                {
                    throw new InvalidOperationException("Agent provider returned an empty stage output.");
                }

                PolicyGuard.ValidateSafeText(result.Output, "Provider output", 12000);
                stage.Output = result.Output;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var reason = $"Agent provider failed with {exception.GetType().Name}.";
                await audit.AppendAsync(run.Id, run.PlanVersion, "executor", "stage.attempt.failed",
                    stageId, $"attempt={attempt}; {reason}", cancellationToken);

                if (stageId == "documentation")
                {
                    stage.UsedFallback = true;
                    stage.Output = "Fallback: deterministic documentation template generated from the execution record.";
                    break;
                }

                if (attempt == MaxAttempts)
                {
                    stage.Status = StageStatus.Failed;
                    stage.Failure = reason;
                    stage.CompletedAtUtc = clock.GetUtcNow();
                    return;
                }

                run.RetryCount++;
                await audit.AppendAsync(run.Id, run.PlanVersion, "executor", "stage.retry.scheduled",
                    stageId, $"nextAttempt={attempt + 1}; maxAttempts={MaxAttempts}", cancellationToken);
            }
        }

        if (envelope.StopSource.IsCancellationRequested)
        {
            stage.Status = StageStatus.Blocked;
            return;
        }

        stage.Status = StageStatus.Succeeded;
        stage.CompletedAtUtc = clock.GetUtcNow();
        stage.Failure = null;
        run.Context[stageId] = stage.Output
            ?? throw new InvalidOperationException("A successful stage must produce an output.");
        await audit.AppendAsync(run.Id, run.PlanVersion, "executor", stage.UsedFallback ? "stage.fallback.used" : "stage.succeeded",
            stageId, $"{stage.Definition.ExitGate}; output={stage.Output}", cancellationToken);
    }

    private static void AddDecision(WorkflowRun run, string actor, string decision, string rationale) =>
        run.Decisions.Add(new DecisionRecord(run.PlanVersion, DateTimeOffset.UtcNow, actor, decision, rationale));

    private static bool IsTerminal(RunStatus status) =>
        status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.SafeStopped;

    private static WorkflowRun Snapshot(WorkflowRun run) => new()
    {
        Id = run.Id,
        Scenario = run.Scenario,
        Requirement = run.Requirement,
        Status = run.Status,
        PlanVersion = run.PlanVersion,
        StartedAtUtc = run.StartedAtUtc,
        UpdatedAtUtc = run.UpdatedAtUtc,
        CompletedAtUtc = run.CompletedAtUtc,
        RetryCount = run.RetryCount,
        RollbackCount = run.RollbackCount,
        MttrMilliseconds = run.MttrMilliseconds,
        SafeStopReason = run.SafeStopReason,
        Stages = run.Stages.Select(stage => new StageExecution
        {
            Definition = stage.Definition,
            Status = stage.Status,
            Attempts = stage.Attempts,
            UsedFallback = stage.UsedFallback,
            Output = stage.Output,
            Failure = stage.Failure,
            StartedAtUtc = stage.StartedAtUtc,
            CompletedAtUtc = stage.CompletedAtUtc
        }).ToList(),
        Context = new ConcurrentDictionary<string, string>(run.Context),
        Decisions = run.Decisions.ToList()
    };

    private sealed class RunEnvelope(WorkflowRun run, StartRunRequest request)
    {
        public WorkflowRun Run { get; } = run;
        public StartRunRequest Request { get; set; } = request;
        public SemaphoreSlim Mutex { get; } = new(1, 1);
        public SemaphoreSlim ExecutionMutex { get; } = new(1, 1);
        public CancellationTokenSource StopSource { get; set; } = new();
    }
}