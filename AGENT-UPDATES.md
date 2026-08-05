# Agent updates required — test/mock harness template

Reference for follow-up work. This branch (`feat/test-mock-harness-template`) adds a reusable,
green-out-of-the-box test + mock harness to the template so that future Logic Apps Standard
projects inherit it deterministically instead of relying on agents to regenerate it each time.

## What this branch added

| Layer | Location | Purpose |
|---|---|---|
| Sample workflow | `LogicApp_Template/sample-order-servicebus/` | Request → external HTTP → Service Bus send → Response. Exercises the whole stack. |
| Unit (MSTest, static) | `Tests/LogicApp.Tests.Unit/` | Structural assertions over `workflow.json` — no engine, no Docker. |
| Integration (xUnit + Reqnroll) | `Tests/LogicApp.Tests.Integration/` | Real workflow run via `func host` against Azurite + Service Bus emulator + WireMock. |
| E2E (xUnit + Reqnroll + APIM emulator) | `E2E/LogicApp.Tests.E2E/` | Domain-level: APIM emulator front door composed with the Logic App + Service Bus. |
| Local-run/debug tooling | `LogicApp_Template/.vscode/tasks.json` | `generateDebugSymbols` + `func: host start`. |
| Wiring | `logicapps_standard_templates.sln`, `Workspace_Template.code-workspace`, `.gitignore` | Solution + workspace registration (Dev Kit discovery) and ignore rules. |

## Agents / skills to update

Change the intent from *"generate the harness from scratch"* to *"scaffold from this template,
then author only the product-specific scenarios."* The template owns the mechanical parts
(fixtures, package pins, `.vscode`, `.sln`/workspace wiring); the skills keep the judgement
(what to test, the push-down rule, scenario discipline).

| # | Agent / skill file | Required change |
|---|---|---|
| 1 | `agents/.github/agents/azure_integration_tests_logicapp.agent.md` | Scaffold the integration project from `Tests/LogicApp.Tests.Integration/` (copy `Infrastructure/` + configs verbatim); author only `Features/` + `Steps/` + `__mocks/` + `__testdata/`. Stop hand-writing the fixtures. |
| 2 | `agents/.github/agents/azure_integration_tests_logicapp_unit.agent.md` | Scaffold the unit project from `Tests/LogicApp.Tests.Unit/` (reuse `WorkflowFiles`/`WorkflowAssert`/`JsonNav`); author per-workflow structural tests + the `Workflows[]` list. |
| 3 | `agents/.github/agents/azure_integration_tests_e2e.agent.md` | Scaffold the E2E project from `E2E/LogicApp.Tests.E2E/` including `ApimEmulator`; author only the domain scenarios. |
| 4 | `agents/.github/skills/test-project-patterns/SKILL.md` | Point at the template as the canonical project structure + pinned package versions. Keep the scenario-discipline / push-down rules. Remove prose that duplicates now-templated fixtures. |
| 5 | `agents/.github/skills/logicapp-local-run/SKILL.md` | Reference the template `Infrastructure/` fixtures as the implementation of the emulator stack + trigger model + `local.settings.json` on-disk patch. Keep connection-string shapes and escape-hatch guidance. |
| 6 | `agents/testing_strategy.md` | Reference the template as the harness source of truth; keep the three-layer strategy and the static-unit-layer philosophy. |
| 7 | `agents/azure_dev_process.md` | Update the expected folder layout to match the template (`Tests/LogicApp.Tests.Unit`, `Tests/LogicApp.Tests.Integration`, sibling `E2E/`). |
| 8 | `agents/.github/agents/azure_integration_logicapps.agent.md` | Verify the `.vscode` / `local.settings.json` it emits matches the template; align if it drifts. |

## Notes for future work

1. **Unit layer is static-only.** It follows `testing_strategy.md` (no engine,
   no `Microsoft.Azure.Workflows.WebJobs.Tests.Extension`). The reference project had drifted to
   the SDK-based `UnitTestExecutor`; that drift was intentionally **not** carried into the
   template. We ruled the SDK engine-run approach out of the template default for two reasons:

   - **Layering.** The unit layer is deliberately static — it asserts the *structure* of
     `workflow.json` (bindings, `runAfter`, literal expressions). Runtime behaviour (expression
     evaluation, control flow, error routing) is proven one layer down in the integration suite,
     which runs the workflow against the **real** ServiceProvider connectors on emulators — higher
     fidelity than the SDK's *mocked* action outputs. The SDK sits awkwardly in the middle (real
     expression evaluation, faked connectors).
   - **Test Explorer / MTP + packaging fragility.** The SDK unit stack is fragile in C# Dev Kit's
     Test Explorer. The older pins associated with it (MSTest 3.2.0 / `Microsoft.NET.Test.Sdk`
     17.8.0) ship a broken MTP bridge that hangs Dev Kit's IPC handshake, so test discovery
     silently breaks. The extension also adds transitive-version friction (package **id ≠
     namespace** `…UnitTesting`; an `NU1605` downgrade unless both `Microsoft.Extensions.Logging`
     and `…Logging.Abstractions` are pinned to `9.0.0` together). It *can* be made green with the
     exact newer pins (MSTest 3.8.3, Test SDK 17.13.0) + the MTP opt-in flags, but that fragility
     makes it a poor default. Static-only sidesteps the whole surface.

   Decide whether the template should also ship an SDK engine-run unit sample (as an optional,
   clearly-pinned extra) for workflows whose in-engine logic warrants fast, Docker-free coverage.

2. **E2E happy-path forwarding.** The `ApimEmulator` returns `202 Accepted` for a valid request
   and the Logic App is then driven via its Request-trigger callback in the step (rather than a
   fragile WireMock proxy to the SAS callback URL). All contract scenarios (401/400/health) are
   pure-emulator and green with no stack.

3. **`local.settings.json` is patched on disk during integration/E2E runs** and restored in
   `[AfterTestRun]` (restore runs *first*, before fixture disposal, so a disposal failure cannot
   leave it dirty). A hard crash mid-run can still leave it patched — recover with
   `git checkout -- LogicApp_Template/local.settings.json`.
