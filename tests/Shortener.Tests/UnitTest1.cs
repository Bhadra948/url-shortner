using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shortener.Api.Contracts;
using Shortener.Api.Orchestration;
using Shortener.Api.Persistence;
using Shortener.Api.Services;

namespace Shortener.Tests;

public sealed class UnitTest1
{
    [Fact]
    public async Task Create_and_resolve_records_click_analytics()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = new ShortLinkService(db, TimeProvider.System);

        var created = await service.CreateAsync(
            new CreateShortLinkRequest("https://example.com/path", "unit-test"), CancellationToken.None);
        var resolved = await service.ResolveAsync(
            created.Code, "https://referrer.example/page", null, CancellationToken.None);
        var analytics = await service.GetAnalyticsAsync(created.Code, CancellationToken.None);
        var recent = await service.GetRecentAsync(CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("https://example.com/path", resolved.TargetUrl);
        Assert.NotNull(analytics);
        Assert.Equal(1, analytics.TotalClicks);
        Assert.Equal(1, analytics.TopReferrers["referrer.example"]);
        Assert.Equal(1, recent.Single().TotalClicks);
    }

    [Fact]
    public async Task Rejects_non_http_targets_and_invalid_aliases()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = new ShortLinkService(db, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateShortLinkRequest("file:///etc/passwd"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateShortLinkRequest("https://example.com", "bad alias"), CancellationToken.None));
        Assert.Empty(await db.Links.ToListAsync());
    }

    [Fact]
    public async Task Greenfield_run_executes_parallel_branches_and_waits_for_human_approval()
    {
        var service = CreateOrchestrator(out var audit);
        var run = await service.StartAsync(new StartRunRequest("greenfield"), CancellationToken.None);

        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        Assert.Equal(StageStatus.Succeeded, Stage(run, "api-implementation").Status);
        Assert.Equal(StageStatus.Succeeded, Stage(run, "ui-implementation").Status);
        Assert.Equal(StageStatus.AwaitingApproval, Stage(run, "release-readiness").Status);
        Assert.Contains("follows architecture", Stage(run, "api-implementation").Output);
        Assert.Contains(audit.GetRunEvents(run.Id), item => item.Action == "gate.approval.required");

        var approved = await service.ApproveAsync(run.Id,
            new ApprovalRequest(true, "Validation reviewed; approve readiness.", "reviewer@example.test"),
            CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, approved!.Status);
        Assert.True(audit.VerifyHashChain());
        Assert.Equal(1, service.GetMetrics().SucceededRuns);
    }

    [Fact]
    public async Task Ambiguous_run_waits_for_clarification_before_planning()
    {
        var service = CreateOrchestrator(out _);
        var run = await service.StartAsync(new StartRunRequest("ambiguous"), CancellationToken.None);

        Assert.Equal(RunStatus.AwaitingClarification, run.Status);
        Assert.Equal(StageStatus.Pending, Stage(run, "architecture").Status);

        var clarified = await service.ClarifyAsync(run.Id,
            new ClarificationRequest("p95 under 150 ms at 500 requests per second; caching is permitted.", "product-owner"),
            CancellationToken.None);

        Assert.Equal(RunStatus.AwaitingApproval, clarified!.Status);
        Assert.Contains("Clarification:", clarified.Requirement);
        Assert.Contains(clarified.Decisions, decision => decision.Decision == "requirements.clarified");
    }

    [Fact]
    public async Task Retries_transient_work_and_uses_documentation_fallback()
    {
        var service = CreateOrchestrator(out var audit);
        var run = await service.StartAsync(
            new StartRunRequest("greenfield", InjectTransientFailure: true, InjectDocumentationFailure: true),
            CancellationToken.None);

        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        Assert.Equal(1, run.RetryCount);
        Assert.Equal(2, Stage(run, "api-implementation").Attempts);
        Assert.True(Stage(run, "documentation").UsedFallback);
        Assert.Equal(1, service.GetMetrics().RetryFrequency);
        Assert.Contains(audit.GetRunEvents(run.Id), item => item.Action == "stage.retry.scheduled");
        Assert.Contains(audit.GetRunEvents(run.Id), item => item.Action == "stage.fallback.used");
    }

    [Fact]
    public async Task Provider_exceptions_use_the_bounded_retry_policy()
    {
        var service = CreateOrchestrator(out _, new FailOnceAgentProvider("api-implementation"));
        var run = await service.StartAsync(new StartRunRequest("greenfield"), CancellationToken.None);

        Assert.Equal(1, run.RetryCount);
        Assert.Equal(2, Stage(run, "api-implementation").Attempts);
        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
    }

    [Fact]
    public async Task Replan_rolls_back_old_plan_and_recovers_after_validation_failure()
    {
        var service = CreateOrchestrator(out _);
        var failed = await service.StartAsync(
            new StartRunRequest("brownfield", InjectValidationFailure: true), CancellationToken.None);
        Assert.Equal(RunStatus.Failed, failed.Status);

        var replanned = await service.ReplanAsync(failed.Id,
            new ReplanRequest("Preserve redirects; add a bounded response cache with invalidation tests.",
                "The initial cache scope was too broad; constrain it and rerun validation.", "tech-lead"),
            CancellationToken.None);

        Assert.Equal(2, replanned!.PlanVersion);
        Assert.Equal(1, replanned.RollbackCount);
        Assert.Equal(RunStatus.AwaitingApproval, replanned.Status);
        var completed = await service.ApproveAsync(replanned.Id,
            new ApprovalRequest(true, "Replanned validation reviewed.", "reviewer"), CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, completed!.Status);
        Assert.NotNull(completed.MttrMilliseconds);
        Assert.Equal(1, service.GetMetrics().RollbackCount);
        Assert.Equal(1, service.GetMetrics().RollbackFrequency);
    }

    [Fact]
    public async Task Safe_stop_blocks_pending_high_impact_work()
    {
        var service = CreateOrchestrator(out _);
        var waiting = await service.StartAsync(new StartRunRequest("greenfield"), CancellationToken.None);
        var stopped = await service.SafeStopAsync(waiting.Id,
            new SafeStopRequest("Risk review requested a pause.", "operator"), CancellationToken.None);

        Assert.Equal(RunStatus.SafeStopped, stopped!.Status);
        Assert.Equal(StageStatus.Blocked, Stage(stopped, "release-readiness").Status);
        Assert.Equal("Risk review requested a pause.", stopped.SafeStopReason);
    }

    [Fact]
    public async Task Safe_stop_cancels_an_inflight_provider_stage()
    {
        var provider = new BlockingAgentProvider("architecture");
        var service = CreateOrchestrator(out _, provider);
        var startTask = service.StartAsync(new StartRunRequest("greenfield"), CancellationToken.None);
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(5));
        var runId = Assert.Single(service.GetRuns()).Id;

        var stopped = await service.SafeStopAsync(runId,
            new SafeStopRequest("Cancel active analysis.", "operator"), CancellationToken.None);
        var returnedRun = await startTask;

        Assert.Equal(RunStatus.SafeStopped, stopped!.Status);
        Assert.Equal(RunStatus.SafeStopped, returnedRun.Status);
        Assert.Equal(StageStatus.Blocked, Stage(returnedRun, "architecture").Status);
    }

    [Fact]
    public async Task Rejects_requirements_that_contain_secret_values()
    {
        var service = CreateOrchestrator(out var audit);

        await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync(
            new StartRunRequest("greenfield", "Ship this with api_key=supersecretvalue"), CancellationToken.None));

        Assert.Empty(service.GetRuns());
        Assert.True(audit.VerifyHashChain());
    }

    [Fact]
    public async Task Rejects_secrets_in_human_decisions_before_audit_or_state_change()
    {
        var service = CreateOrchestrator(out var audit);
        var run = await service.StartAsync(new StartRunRequest("greenfield"), CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ApproveAsync(run.Id,
            new ApprovalRequest(true, "api_key=supersecretvalue", "reviewer"), CancellationToken.None));

        Assert.Equal(RunStatus.AwaitingApproval, service.GetRun(run.Id)!.Status);
        Assert.DoesNotContain(audit.GetRunEvents(run.Id), item => item.Detail.Contains("supersecretvalue", StringComparison.Ordinal));
    }

    private static OrchestrationService CreateOrchestrator(out AuditTrail audit, IAgentProvider? agentProvider = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"shortener-tests-{Guid.NewGuid():N}");
        audit = new AuditTrail(root);
        return new OrchestrationService(audit, TimeProvider.System, agentProvider ?? new DeterministicAgentProvider());
    }

    private static StageExecution Stage(WorkflowRun run, string id) =>
        run.Stages.Single(stage => stage.Definition.Id == id);

    private sealed class FailOnceAgentProvider(string failedStage) : IAgentProvider
    {
        private readonly DeterministicAgentProvider _inner = new();
        private int _failureInjected;

        public Task<AgentStageResult> ExecuteAsync(AgentStageContext context, CancellationToken cancellationToken)
        {
            if (context.StageId == failedStage && Interlocked.Exchange(ref _failureInjected, 1) == 0)
            {
                throw new TimeoutException("Simulated provider timeout.");
            }

            return _inner.ExecuteAsync(context, cancellationToken);
        }
    }

    private sealed class BlockingAgentProvider(string blockedStage) : IAgentProvider
    {
        private readonly DeterministicAgentProvider _inner = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;

        public async Task<AgentStageResult> ExecuteAsync(AgentStageContext context, CancellationToken cancellationToken)
        {
            if (context.StageId == blockedStage)
            {
                _started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return await _inner.ExecuteAsync(context, cancellationToken);
        }
    }
}
