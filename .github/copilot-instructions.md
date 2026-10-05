# Shortline Workspace Instructions

## Stack and Commands

- .NET 10 ASP.NET Core Web API, EF Core SQLite, plain HTML/CSS/JavaScript workbench, and xUnit tests.
- Restore: `dotnet restore ./Shortener.Orchestrator.slnx`
- Test: `dotnet test ./Shortener.Orchestrator.slnx`
- Run: `dotnet run --project ./src/Shortener.Api --urls http://localhost:5078`

## Engineering Boundaries

- Preserve HTTP(S)-only destination validation, alias constraints, expiry checks, click analytics, and rate limits.
- Keep stage execution behind `IAgentProvider`; provider code receives versioned upstream context and must honor cancellation.
- Never bypass dependency gates, secret scanning, the release approval checkpoint, or audit recording.
- The built-in provider is deterministic. Do not add credentials, autonomous source writes, shell execution, or deployment actions without explicit authorization and new safety review.
- Keep run/audit output free of secrets and unnecessary personal data. Referrer analytics store host only.
- Update README/docs and focused xUnit tests when behavior or API contracts change.

## Project Setup Checklist

- [x] Requirements clarified and .NET solution scaffolded.
- [x] Shortener, orchestration engine, provider boundary, and workbench implemented.
- [x] No additional VS Code extension was specified by the project setup guidance.
- [x] VS Code task created; solution builds and tests pass.
- [x] Local app launched and browser smoke-tested.
- [x] README, architecture, scenarios, setup, risks, and limitations documented.
