# Repository Guidelines

This repo provides an Orleans-backed SignalR backplane. Use the conventions below to keep contributions consistent and easy to review.

## Project Structure & Module Organization
- `ManagedCode.Orleans.SignalR.Core` — core types, options, and helpers shared by all modules.
- `ManagedCode.Orleans.SignalR.Client` — client integration extensions.
- `ManagedCode.Orleans.SignalR.Server` — Orleans grains and server-side plumbing.
- `ManagedCode.Orleans.SignalR.Tests` — xUnit tests and a minimal test host under `TestApp/`.
- `ManagedCode.Orleans.SignalR.slnx` and `Directory.Build.props` — solution and central build settings (net9.0, C# 13, analyzers, nullable).

## Build, Test, and Development Commands
- Restore/build: `dotnet restore` • `dotnet build -c Debug`
- Run tests: `dotnet test -c Debug` (xUnit; Coverlet collector is enabled for coverage)
- Filter tests: `dotnet test --filter "FullyQualifiedName~PartitioningTests"`
- Pack NuGet: `dotnet pack -c Release`
- Format: `dotnet format` (run before committing)

## Coding Style & Naming Conventions
- C#: 4‑space indent; file‑scoped namespaces; `Nullable` enabled; `EnableNETAnalyzers=true`.
- Naming: PascalCase for types/members; camelCase for locals/parameters; interfaces prefixed `I`.
- Domain naming: Orleans grain classes end with `Grain` (e.g., `SignalRGroupGrain`); namespaces start with `ManagedCode.Orleans.SignalR.*`.
- Prefer explicit access modifiers, `readonly` where applicable, and expression‑bodied members when clearer.

## Testing Guidelines
- Framework: xUnit with `[Fact]`/`[Theory]`. Tests live in `ManagedCode.Orleans.SignalR.Tests` and end with `*Tests.cs`.
- Cluster tests: use Orleans TestingHost utilities; keep tests deterministic and isolated.
- Coverage: keep or increase coverage on core logic. Example: `dotnet test -c Debug --collect:"XPlat Code Coverage"`.

## Commit & Pull Request Guidelines
- Commits: short, imperative subject lines. History shows concise tags like “fix”, “tests”, “refactoring” — keep using them.
  - Examples: `fix: avoid deadlock in invocation`, `tests: improve group partitioning suite`.
- PRs: include a clear description, linked issues, rationale, and how you tested. Add/adjust tests with behavior changes and update README if public APIs change.
- CI hygiene: run `dotnet build` and `dotnet test` locally and ensure `dotnet format` yields no diffs before opening a PR.

## Security & Configuration Tips
- Do not commit secrets or connection strings. Tests should use the provided in‑memory/TestHost setup.
- Configuration lives in code and `Directory.Build.props`; discuss before introducing new external dependencies.

