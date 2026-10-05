namespace Shortener.Api.Orchestration;

public sealed record AgentStageContext(
    Guid RunId,
    int PlanVersion,
    string Scenario,
    string Requirement,
    string StageId,
    IReadOnlyDictionary<string, string> UpstreamOutputs);

public sealed record AgentStageResult(string Output);

public interface IAgentProvider
{
    Task<AgentStageResult> ExecuteAsync(AgentStageContext context, CancellationToken cancellationToken);
}

public sealed class DeterministicAgentProvider : IAgentProvider
{
    public Task<AgentStageResult> ExecuteAsync(AgentStageContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var architecture = context.UpstreamOutputs.GetValueOrDefault("architecture", "the approved architecture decision");
        var apiOutput = context.UpstreamOutputs.GetValueOrDefault("api-implementation", "the API implementation branch");
        var uiOutput = context.UpstreamOutputs.GetValueOrDefault("ui-implementation", "the operator-experience branch");

        var output = context.StageId switch
        {
            "requirements" => $"Normalized requirement: {context.Requirement}. Scenario={context.Scenario}; acceptance: redirects preserve target semantics, analytics are recorded, and validation is repeatable.",
            "architecture" => "Decision: ASP.NET Core API with SQLite persistence; isolated orchestration domain; staged DAG with validation and human release gate. Risks: abuse, redirect safety, data durability, and provider drift.",
            "api-implementation" => $"API workstream follows architecture: {architecture}. HTTP(S)-only target validation, unique short codes, expiry-aware redirects, click analytics, and request throttling.",
            "ui-implementation" => $"Operator workstream follows architecture: {architecture}. Scenario selection, run status, human controls, audit lineage, and reliability metrics.",
            "validation" => $"Validated API output and operator output: {apiOutput} / {uiOutput}. Tests, dependency gates, redirect scheme policy, and release policy passed.",
            "documentation" => $"Documentation covers API output and operator output: {apiOutput} / {uiOutput}, with setup, risks, assumptions, and decisions.",
            _ => "Release readiness assembled. A named human reviewer must approve; this prototype never deploys."
        };

        return Task.FromResult(new AgentStageResult(output));
    }
}