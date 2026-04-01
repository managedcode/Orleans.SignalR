# AGENTS.md

Project: Orleans.SignalR (ManagedCode.Orleans.SignalR)
Stack: C# (LangVersion 14) on .NET 10 (net10.0), Microsoft Orleans 9.2.1, ASP.NET Core SignalR 10.0.0, xUnit 2.9.3, Shouldly 4.3.0, Coverlet 6.0.4

Follows [MCAF](https://mcaf.managed-code.com/)

---

## Conversations (Self-Learning)

Learn the user's habits, preferences, and working style. Extract rules from conversations, save to "## Rules to follow", and generate code according to the user's personal rules.

**Update requirement (core mechanism):**

Before doing ANY task, evaluate the latest user message.
If you detect a new rule, correction, preference, or change -> update `AGENTS.md` first.
Only after updating the file you may produce the task output.
If no new rule is detected -> do not update the file.

**When to extract rules:**

- prohibition words (never, don't, stop, avoid) or similar -> add NEVER rule
- requirement words (always, must, make sure, should) or similar -> add ALWAYS rule
- memory words (remember, keep in mind, note that) or similar -> add rule
- process words (the process is, the workflow is, we do it like) or similar -> add to workflow
- future words (from now on, going forward) or similar -> add permanent rule

**Preferences -> add to Preferences section:**

- positive (I like, I prefer, this is better) or similar -> Likes
- negative (I don't like, I hate, this is bad) or similar -> Dislikes
- comparison (prefer X over Y, use X instead of Y) or similar -> preference rule

**Corrections -> update or add rule:**

- error indication (this is wrong, incorrect, broken) or similar -> fix and add rule
- repetition frustration (don't do this again, you ignored, you missed) or similar -> emphatic rule
- manual fixes by user -> extract what changed and why

**Strong signal (add IMMEDIATELY):**

- swearing, frustration, anger, sarcasm -> critical rule
- ALL CAPS, excessive punctuation (!!!, ???) -> high priority
- same mistake twice -> permanent emphatic rule
- user undoes your changes -> understand why, prevent

**Ignore (do NOT add):**

- temporary scope (only for now, just this time, for this task) or similar
- one-off exceptions
- context-specific instructions for current task only

**Rule format:**

- One instruction per bullet
- Tie to category (Testing, Code, Docs, etc.)
- Capture WHY, not just what
- Remove obsolete rules when superseded

---

## Rules to follow (Mandatory, no exceptions)

### Commands

- build: `dotnet restore` then `dotnet build -c Debug`
- test: `dotnet test -c Debug`
- format: `dotnet format`
- coverage: `dotnet test -c Debug --collect:"XPlat Code Coverage"`

### Task Delivery (ALL TASKS)

- Always start from the architecture map in `docs/Architecture/Overview.md`:
  - confirm the Mermaid diagrams exist and are up to date (if missing/outdated -> update them first):
    - system/module map (blocks/modules + dependency direction)
    - interfaces/contracts map (APIs/events/interfaces between modules)
    - key classes/types map (high-signal types only; not an inventory)
  - `docs/Architecture/Overview.md` is the primary "start here" card for humans and AI agents:
    - diagram elements must use real names (no placeholders like "Module A")
    - every diagram element must be anchored to reality via links in the same file (feature docs, ADRs, code paths, or entry-point files)
    - keep diagrams readable; if a diagram becomes "spaghetti", split by boundary and link out
    - keep the file short: prefer diagrams + a tiny link index over large tables or inventories (optimize token footprint)
  - identify the impacted boundary/module(s) and entry points
  - follow the links to the relevant ADR(s) and Feature doc(s) (do not read everything)
- Scope first (prevent context overload):
  - use `docs/Architecture/Overview.md` -> "Scoping (read first)"
  - write **in scope / out of scope** (what will change and what must not change)
  - if you cannot identify scope from the architecture map -> stop and fix the map (or ask one clarifying question)
- Context engineering (hard requirement):
  - keep only the context needed for the task (avoid repo-wide scanning and "read everything")
  - if you need a doc/module that isn't reachable from `docs/Architecture/Overview.md`, update the overview to link it
- Skills (workflow packages):
  - if the task matches an existing skill's description, follow that skill's workflow instead of improvising
  - if a skill is missing or drifting from reality, update the skill so the fix is permanent
- Analyze first (no coding yet):
  - what exists today (facts only)
  - what must change / must not change
  - unknowns and risks (what must be clarified)
- Create a written plan before implementation:
  - for architecture/decision work: keep the plan as `## Implementation plan (step-by-step)` in the ADR
  - for behaviour work: keep the plan as `## Implementation plan (step-by-step)` in the Feature doc
  - update the plan while executing (tick items / update statuses as work is completed)
- Implement code and tests together
- If `build` is separate from `test`, run `build` before `test`
- Verification timing (optimize time + tokens):
  - do not run `test`/`coverage` "just because" before you wrote/changed code or tests (exception: reproducing a bug / confirming baseline)
  - while iterating, run the smallest meaningful scope (new/changed tests first)
  - run broader suites only when you have something real to verify (avoid re-running the same command without changes)
  - run coverage once per change (it is heavier than tests)
- Run tests in layers: new/changed -> related suite -> broader regressions
- Final verification for code changes requires the full `dotnet test -c Debug` suite to pass; targeted test runs are only for iteration, not for closing the task
- After tests pass: run format
- After format: run build (final check)
- Summarize changes and test results before marking complete
- Always run required builds and tests yourself; do not ask the user to execute them (explicit user directive)
- Commit messages are short and imperative; common prefixes in history include `fix:`, `tests:`, and `code style`

### Reviews

- For code reviews, be extra thorough and explicitly call out low-value/AI-sounding changes and whether changes actually improve behavior, performance, or safety
- Never run SignalR work on Orleans scheduler/context; offload to dedicated tasks/threads to avoid blocking

### Documentation (ALL TASKS)

- All docs live in `docs/` (or `.wiki/`)
- Global architecture entry point: `docs/Architecture/Overview.md` (read first)
- Single source of truth (no duplication):
  - each important fact/rule/diagram lives in exactly one canonical place; other docs should link, not copy
  - `docs/Architecture/Overview.md` is coarse and navigational (diagrams + links), not the place for detailed behaviour
  - detailed behaviour belongs in `docs/Features/*`; detailed decisions/invariants belong in `docs/ADR/*`
  - if you need detail, follow the link; only add new text when there is no canonical place yet
- When creating docs from templates:
  - copy the template into its real docs location
  - replace all placeholders and remove all template notes (real docs must be clean: no `TEMPLATE ONLY`)
- Update feature docs when behaviour changes
- Update ADRs when architecture changes
- Diagrams are mandatory in docs:
  - `docs/Architecture/Overview.md`: Mermaid diagrams for system/modules + interfaces/contracts + key classes/types (must render)
    - every diagram element has a real reference link in the same file (docs/code), so an agent can navigate without repo-wide scanning
  - `docs/Features/*`: at least one Mermaid diagram for the main flow
  - `docs/ADR/*`: at least one Mermaid diagram for the decision
- Mermaid hygiene (Mermaid often breaks if you freestyle it):
  - diagrams must render in repo Markdown preview (broken Mermaid is treated as broken documentation)
  - prefer simple Mermaid syntax (`flowchart` / `sequenceDiagram`); use short ASCII-only IDs
  - if `classDiagram` is flaky in your renderer, replace it with a `flowchart` that shows the same relationships
  - if a diagram doesn't render, simplify it until it does (no "close enough")

### Testing (ALL TASKS)

- Framework: xUnit + Shouldly; tests live in `ManagedCode.Orleans.SignalR.Tests` and end with `*Tests.cs`
- Avoid introducing new `[GenerateSerializer]` state types in tests; reuse production models to keep Orleans serialization types in product code.
- Integration tests use Orleans TestingHost with fixtures in `ManagedCode.Orleans.SignalR.Tests/Cluster` and the minimal host in `ManagedCode.Orleans.SignalR.Tests/TestApp`
- Prefer TDD for new behaviour and bugfixes: write a failing test first, then implement the smallest change to make it pass, then refactor safely
- Every behaviour change needs sufficient automated tests to cover its cases; one is the minimum, not the target
- Each public API endpoint has at least one test; complex endpoints have tests for different inputs and errors
- Integration tests must exercise real flows end-to-end, not just call endpoints in isolation
- Prefer integration/API/UI tests over unit tests
- Prefer the minimum set of high-value tests that proves behaviour; invest in diagrams for understanding and safe change
- No mocks for internal systems (DB, queues, caches) - use containers
- Mocks only for external third-party systems
- Never delete or weaken a test to make it pass
- Each test verifies a real flow or scenario, not just calls a function - tests without meaningful assertions are forbidden
- Check code coverage to see which functionality is actually tested; coverage is for finding gaps, not a number to chase
- Flaky tests are failures: fix the test or the underlying behaviour, don't "retry until green"

### Autonomy

- Start work immediately - no permission seeking
- Questions only for architecture blockers not covered by ADR
- Report only when task is complete

### Advisor stance (ALL TASKS)

- Stop being agreeable: be direct and honest; no flattery, no validation, no sugar-coating
- Challenge weak reasoning; point out missing assumptions and trade-offs
- If something is underspecified/contradictory/risky - say so and list what must be clarified
- Never guess or invent. If unsure, say "I don't know" and propose how to verify
- Quality and security first: tests + static analysis are gates; treat security regressions as blockers

### Code Style

- Style rules: `.editorconfig` (Allman braces, file-scoped namespaces, var usage, underscore prefix for private/internal fields, primary constructors preferred)
- Language/runtime: `net10.0`, C# 14, `Nullable` enabled, `EnableNETAnalyzers=true`, `TreatWarningsAsErrors=true`
- Naming: PascalCase for types/constants, camelCase for locals/parameters, async methods end with Async (except Hubs/Controllers)
- Avoid `this.` qualification; prefer predefined types (`int` over `Int32`)
- Never use `ConfigureAwait(false)`
- No magic literals - extract to constants, enums, config
- In concurrency-sensitive paths like `OrleansHubLifetimeManager`, prefer the smallest behavior-preserving fix; avoid widening the async/concurrency shape unless tests prove it is necessary, because this code is easy to make unsafe

### Diagnostics

- Use simple metrics counters; avoid concurrent collections or per-hub observable gauges unless explicitly requested
- Do not use string literals for metrics names, targets, or reasons; define and reuse constants

### Comments

- When offloading SignalR observer sends with Task.Run, add a critical comment explaining it must not run on the Orleans scheduler to avoid blocking

### Critical (NEVER violate)

- Never commit secrets, keys, connection strings
- Never mock internal systems in integration tests
- Never skip tests to make PR green
- Never force push to main
- Never approve or merge (human decision)

### Boundaries

**Protected areas:**
- Public API surface: `ManagedCode.Orleans.SignalR.Core/Interfaces`, `ManagedCode.Orleans.SignalR.Core/Config/OrleansSignalROptions.cs`, `ManagedCode.Orleans.SignalR.Core/HubContext`, `ManagedCode.Orleans.SignalR.Client/Extensions`
- Lifetime manager and routing: `ManagedCode.Orleans.SignalR.Core/SignalR/OrleansHubLifetimeManager.cs`, `ManagedCode.Orleans.SignalR.Core/Helpers/PartitionHelper.cs`
- Server grains and persistence: `ManagedCode.Orleans.SignalR.Server/*Grain.cs`, `ManagedCode.Orleans.SignalR.Server/Storage`
- Test infrastructure: `ManagedCode.Orleans.SignalR.Tests/Cluster`, `ManagedCode.Orleans.SignalR.Tests/TestApp`

**Always:**

- Read AGENTS.md and docs before editing code
- Run tests before commit

**Ask first:**

- Changing public API contracts
- Adding new dependencies
- Modifying database schema
- Deleting code files

---

## Preferences

### Likes

### Dislikes

- Leaving placeholder text in docs or AGENTS; replace with real values
