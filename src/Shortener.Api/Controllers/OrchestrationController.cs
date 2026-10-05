using Microsoft.AspNetCore.Mvc;
using Shortener.Api.Orchestration;

namespace Shortener.Api.Controllers;

/// <summary>
/// Controller for orchestrating workflow scenarios and runs.
/// </summary>  
[ApiController]
[Route("api/orchestration")]
public sealed class OrchestrationController(OrchestrationService orchestration) : ControllerBase
{
    [HttpGet("scenarios")]
    public IActionResult Scenarios() => Ok(orchestration.GetScenarios());

    [HttpGet("runs")]
    public IActionResult Runs() => Ok(orchestration.GetRuns());

    /// <summary>
    /// Starts a new workflow run based on the provided request.
    /// </summary>
    /// <param name="request">The request containing the workflow scenario and parameters.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A response indicating the result of the operation.</returns>   
    [HttpPost("runs")]
    [ProducesResponseType<WorkflowRun>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Start(StartRunRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var run = await orchestration.StartAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { runId = run.Id }, run);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid workflow request", Detail = exception.Message });
        }
    }

    [HttpGet("runs/{runId:guid}")]
    public IActionResult Get(Guid runId)
    {
        var run = orchestration.GetRun(runId);
        return run is null ? NotFound() : Ok(run);
    }

    [HttpPost("runs/{runId:guid}/approval")]
    public async Task<IActionResult> Approve(Guid runId, ApprovalRequest request, CancellationToken cancellationToken) =>
        await ExecuteDecision(() => orchestration.ApproveAsync(runId, request, cancellationToken));

    [HttpPost("runs/{runId:guid}/clarification")]
    public async Task<IActionResult> Clarify(Guid runId, ClarificationRequest request, CancellationToken cancellationToken) =>
        await ExecuteDecision(() => orchestration.ClarifyAsync(runId, request, cancellationToken));

    [HttpPost("runs/{runId:guid}/replan")]
    public async Task<IActionResult> Replan(Guid runId, ReplanRequest request, CancellationToken cancellationToken) =>
        await ExecuteDecision(() => orchestration.ReplanAsync(runId, request, cancellationToken));

    [HttpPost("runs/{runId:guid}/safe-stop")]
    public async Task<IActionResult> SafeStop(Guid runId, SafeStopRequest request, CancellationToken cancellationToken) =>
        await ExecuteDecision(() => orchestration.SafeStopAsync(runId, request, cancellationToken));

    [HttpGet("runs/{runId:guid}/audit")]
    public IActionResult Audit(Guid runId) =>
        orchestration.GetRun(runId) is null ? NotFound() : Ok(orchestration.GetAudit(runId));

    [HttpGet("metrics")]
    public IActionResult Metrics() => Ok(orchestration.GetMetrics());

    private async Task<IActionResult> ExecuteDecision(Func<Task<WorkflowRun?>> action)
    {
        try
        {
            var run = await action();
            return run is null ? NotFound() : Ok(run);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid decision", Detail = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new ProblemDetails { Title = "Decision not allowed", Detail = exception.Message });
        }
    }
}