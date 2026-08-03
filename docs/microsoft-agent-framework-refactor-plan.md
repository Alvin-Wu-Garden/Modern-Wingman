# Microsoft Agent Framework 1.13 refactor plan

## Goal

Adopt the stable C# surfaces in Microsoft Agent Framework 1.13 where they replace
framework-like code in Modern Wingman, without weakening the product's local-only
security policy, SQLite audit trail, provider switching, or workspace rollback
guarantees.

The pre-change baseline is:

- `dotnet build -c Release`: 0 warnings, 0 errors.
- Unit/integration tests: 290 passed, 4 optional external acceptance tests skipped.
- GitHub Copilot remains an RC adapter (`Microsoft.Agents.AI.GitHub.Copilot`
  1.13.0-rc1), even though the core agent and workflow packages are stable 1.13.0.
- Source-generated workflow handlers require the separate stable
  `Microsoft.Agents.AI.Workflows.Generators` 1.13.0 analyzer package.

## Official API decisions

### Adopt now

1. Expose every registered Wingman tool as its own `AIFunction` with its real name,
   description, and JSON schema. The current `call_wingman_tool(toolName,
   argumentsJson)` router duplicates function discovery in prompt text and prevents
   the model/provider from using normal function-schema selection.
2. Implement Explore -> Plan -> Impact -> Code -> Verify as a typed
   `Microsoft.Agents.AI.Workflows` graph. Use `WorkflowBuilder`, source-generated
   `[MessageHandler]` executors, conditional edges, `WorkflowOutputEvent`, and a fresh
   workflow instance per run.
3. Consume framework workflow errors and terminal outputs from the workflow event
   stream. Keep `RunStreamEvent` only as the product-facing persistence/UI contract.
4. Add contract tests for tool schemas, tool invocation, workflow routing, retry
   limits, plan-only non-mutation, and framework package/API availability.

### Keep as Modern Wingman domain code

1. `IToolRegistry`, policy evaluation, approval coordination, audit records, and
   timeouts remain. Agent Framework supplies invocation plumbing; it does not replace
   Wingman's mode-aware workspace policy or SQLite audit model.
2. Filesystem/git change checkpoints remain. MAF workflow checkpoints serialize
   workflow state, but do not by themselves restore files changed by a coding agent.
3. SQLite conversations remain the provider-neutral source of truth. An
   `AgentSession` is tied to the agent/provider configuration and cannot safely be
   reused after the user switches provider or model.
4. Code graph construction, impact analysis, and the bounded evidence pack remain.
   The Neo4j GraphRAG integration is a generic context provider and does not replace
   Wingman's Roslyn/Java extraction, typed code graph, reverse impact closure, or
   index lifecycle.
5. Existing redaction and telemetry persistence remain. Framework OpenTelemetry is
   complementary transport-level tracing, not a replacement for cost, approval,
   retry, and audit records.

### Defer until the framework/provider boundary supports it safely

1. `AgentSkillsProvider` is stable and should eventually replace the custom skill
   prompt and read tool. It plugs into `ChatClientAgent.AIContextProviders`, while the
   current C# GitHub Copilot adapter does not expose context-provider composition.
   Adopting it only for BYOK would create different skill behavior by provider.
2. `RequestPort` and workflow checkpoints can replace the current plan approval
   rerun only after a durable checkpoint store and restart recovery are designed.
   An in-memory pending `StreamingRun` would regress desktop restarts.
3. Persistent `AgentSession` storage requires a provider/model/config fingerprint,
   migration rules, and a fallback to SQLite message history. It is not part of the
   first refactor batch.
4. C# progressive tool exposure is not available. The documented add/remove tools
   API is Python-only.
5. Declarative workflows, MCP skills, FIDES, Hyperlight CodeAct, and provider-specific
   hosted tools are preview/experimental or do not fit the local desktop boundary.

## Change batches and files

### Batch 1 - standard function tools

#### `apps/agent-service/src/Infrastructure/AgentFramework/WingmanToolAdapter.cs`

- Replace `BuildPrompt` and the single router function with `CreateTools`.
- Return one `AIFunction` per current `ToolDescriptor`.
- Preserve each descriptor's JSON input schema rather than wrapping it in a string.
- Convert `AIFunctionArguments` to the registry's argument dictionary without an
  extra JSON encode/decode round trip.
- Delegate execution to `IToolRegistry.ExecuteAsync`, preserving all policy,
  approval, hooks, telemetry, and plugin behavior.
- Snapshot descriptors when building an agent so concurrent plugin reconciliation
  cannot mutate the tool list mid-run.

#### `apps/agent-service/src/Infrastructure/AgentFramework/ByokAgentFactory.cs`

- Attach the standard Wingman function list directly to `ChatOptions.Tools`.
- Use the registry's existing `read_skill` function instead of registering a second
  BYOK-only copy.
- Remove the manually generated Wingman tool catalog from instructions.

#### `apps/agent-service/src/Infrastructure/AgentFramework/CopilotAgentFactory.cs`

- Attach the same standard function list to `SessionConfig.Tools`.
- Remove the manually generated Wingman tool catalog from the system message.
- Keep Copilot CLI permissions separate from Wingman function policy: CLI shell/file
  permissions and application function approvals are different trust boundaries.

#### `apps/UnitTests/WingmanToolAdapterTests.cs` (new)

- Assert one function per descriptor and no `call_wingman_tool` router.
- Assert name, description, and schema preservation.
- Invoke a generated function and verify the exact run/mode/workspace/project context
  reaches the registry.
- Assert invalid descriptor schemas fail while constructing the agent, not during a
  model request.

### Batch 2 - typed Agent Framework workflow

#### `apps/agent-service/src/Infrastructure/Workflow/WorkflowExecutionModels.cs` (new)

- Define immutable messages passed between executors: exploration, plan, impact,
  implementation, verification, and terminal result.
- Carry retry count and verification feedback in messages rather than mutable
  executor fields or global workflow state.

#### `apps/agent-service/src/Infrastructure/Workflow/ExplorePlanCodeVerifyExecutors.cs` (new)

- Add one source-generated, single-responsibility executor for each phase.
- Keep graph/RAG degradation behavior in Explore and Impact: optional evidence
  failures log a warning and do not fail unrelated chat/code tasks.
- Make the Verify executor the only place that decides success, retry, and the retry
  ceiling.
- Keep file checkpoints immediately before every code/fix attempt.

#### `apps/agent-service/src/Infrastructure/Workflow/ExplorePlanCodeVerifyWorkflow.cs`

- Reduce the class to workflow construction, event consumption, and terminal result
  extraction.
- Build a fresh graph per call to guarantee run isolation without
  `IResettableExecutor`.
- Route plan-only runs directly to the terminal executor.
- Route failed verification back to Code only while attempts remain.
- Use the default OffThread execution for production streaming.
- Throw on `WorkflowErrorEvent` and require exactly one terminal result.

#### `apps/agent-service/src/Host/RestEndpoints/WorkflowEndpoints.cs`

- Preserve all current REST routes and response shapes.
- Do not introduce an in-memory `StreamingRun` dependency.
- Only adjust exception/terminal handling if required by the typed workflow result.

#### `apps/UnitTests/WorkflowIntegrationTests.cs`

- Preserve the plan-only no-mutation acceptance test.
- Add verify-fail -> repair -> pass routing.
- Add verify-fail-at-limit terminal behavior.
- Assert a second run receives no state from the first.

#### `apps/UnitTests/AgentFrameworkPackageContractTests.cs`

- Assert stable workflow APIs used by production code are present: `WorkflowBuilder`,
  `MessageHandlerAttribute`, `InProcessExecution`, and `WorkflowOutputEvent`.

### Batch 3 - observability and cleanup

#### `apps/agent-service/src/Infrastructure/AgentFramework/AgentFrameworkTelemetry.cs` (new, only if exporter configuration is enabled)

- Centralize the source name and sensitive-data-disabled defaults.
- Instrument the agent or chat-client layer, not both, to avoid duplicate spans.
- Never enable prompt, response, tool argument, or tool result capture by default.

#### `apps/agent-service/src/Host/DependencyInjection/ServiceRegistration.cs`

- Register framework telemetry only when explicitly configured.
- Keep the existing SQLite telemetry and audit services.

#### Cleanup candidates after all regression tests pass

- Delete `SkillPromptBuilder` only after GitHub Copilot gains equivalent context
  provider support or a provider-neutral composition layer is implemented.
- Remove dead workflow helpers left by the graph migration.
- Format touched minified code only; do not mechanically rewrite unrelated files.

## Verification matrix

| Area | Required verification |
|---|---|
| Build/API | Locked restore; Release build with zero warnings and errors |
| Tool contract | Per-tool schema, name, invocation context, cancellation, plugin snapshot |
| Security | Ask/Plan/Auto/FullAuto policy tests; approval accept/reject; path escape tests |
| Workflow | Plan-only, success, repair loop, retry ceiling, cancellation, run isolation |
| Persistence | Run status/events, approval records, filesystem checkpoints, restart recovery |
| Providers | BYOK `ChatClientAgent`; bundled GitHub Copilot adapter; missing-key behavior |
| Streaming | Tokens, tool calls/results, usage, workflow phase and verify events |
| Regression | Full 290-test baseline plus new tests; optional external tests remain opt-in |
| Desktop | Typecheck/build and manual start-up smoke test after backend changes |

## Implemented and verified

- Batch 1 and Batch 2 are implemented. Batch 3 framework OpenTelemetry remains
  deferred because the application has no explicit exporter configuration; adding
  an unconsumed instrumentation layer would duplicate the existing SQLite telemetry
  without improving diagnostics.
- Locked restore succeeds for the service and test projects.
- Release service build succeeds with zero warnings and zero errors.
- The complete test suite passes: 297 passed; 4 existing environment-dependent
  Neo4j/large-project acceptance tests remain opt-in and skipped.
- New workflow acceptance coverage proves repair routing, retry-ceiling termination,
  and concurrent run isolation. New tool tests prove per-function schemas, invocation
  context, cancellation, invalid-schema rejection, and provider-safe plugin tool
  names.
- Workspace TypeScript typecheck and production Vite build pass. Vite still reports
  the existing large-chunk advisory.
- The Tauri project passes `cargo check --locked`. The obsolete `wingman-cli` target
  was removed because Modern Wingman is a desktop-UI-only product.
- An isolated AgentService process using a temporary SQLite database starts and
  returns HTTP 200 from the REST root endpoint.

## Stop conditions

- Do not replace a working domain control with a framework feature unless the latter
  covers persistence, cancellation, restart recovery, and the current UI contract.
- Do not adopt prerelease packages beyond the already-required GitHub Copilot adapter.
- Stop a batch if provider behavior diverges or a security/approval regression appears;
  keep the previous batch independently buildable and testable.
