# Logic Apps Standard — Reference Workspace

This workspace is a **reference** for AI agents (and humans) building Logic Apps Standard workflows.
It is intentionally split into **lookup data** (machine-readable catalogs) and **shape patterns** (small workflow examples).

## How to use this workspace

When generating a `workflow.json`, follow this order:

1. **Look up service-provider connectors in [`connectors.json`](./connectors.json).**
   Every `serviceProviderId`, `operationId`, parameter name, type, enum value, and `required` flag is present.
   Generated from the live Logic Apps designtime runtime — do **not** invent or recall these strings from memory.

2. **Look up built-in actions/triggers in [`builtins.json`](./builtins.json).**
   Covers everything that is **not** a service provider: `Http`, `Foreach`, `Until`, `If`, `Switch`, `Scope`,
   variables, `Liquid`, `XsltTransform`, `Workflow`, `Function`, `Compose`, `ParseJson`, etc.
   Includes `runAfter`, `trackedProperties`, and `operationOptions` reference blocks.

3. **Copy a pattern from [`patterns/`](./patterns/) when you need *shape*** — a workflow structure
   that JSON Schema can't express. Each pattern is one minimal `workflow.json`.

## Lookup files

| File | Content | Source of truth |
|---|---|---|
| [`connectors.json`](./connectors.json) | Service-provider catalog (AzureBlob, serviceBus, sql, openai, …) with full input schema | Generated from `func host` running in [`workflow-designtime/`](./workflow-designtime/) |
| [`builtins.json`](./builtins.json) | Non-service-provider primitives (control flow, HTTP, variables, transforms, code, EDI helpers) | Hand-curated from Logic Apps Standard schema |

### Regenerating `connectors.json`

```powershell
# 1. Start the designtime host
cd workflow-designtime
func host start          # leave running

# 2. In a second terminal, run the generator
cd ..
.\Generate-ConnectorCatalog.ps1                                # all 50 providers
.\Generate-ConnectorCatalog.ps1 -IncludeProviders @('AzureBlob','serviceBus')  # subset
```

The "AzureWebJobsStorage Unhealthy" warning from `func host` is benign — the management API still serves operation manifests.

## Patterns (shape, not strings)

Each folder contains exactly one `workflow.json` that demonstrates a structure.

| Pattern | What it shows |
|---|---|
| [`patterns/parallel`](./patterns/parallel/) | Parallel branches = multiple actions sharing the same `runAfter`. **There is no `Parallel` action type.** |
| [`patterns/exception-handler`](./patterns/exception-handler/) | Try/catch via two sibling Scopes; catch uses `runAfter: { Try: [Failed, TimedOut, Skipped] }` |
| [`patterns/child-workflow-caller`](./patterns/child-workflow-caller/) + [`patterns/child-workflow-callee`](./patterns/child-workflow-callee/) | Invoking a sibling workflow with the `Workflow` action |
| [`patterns/http-oauth`](./patterns/http-oauth/) | `authentication.type: ActiveDirectoryOAuth` shape |
| [`patterns/http-basic`](./patterns/http-basic/) | `authentication.type: Basic` |
| [`patterns/http-bearer-token`](./patterns/http-bearer-token/) | `authentication.type: Raw` (`Bearer …`) |
| [`patterns/http-cookie-auth`](./patterns/http-cookie-auth/) | Two-step login + cookie reuse |
| [`patterns/http-retry-policy`](./patterns/http-retry-policy/) | `retryPolicy.type: exponential` with ISO8601 intervals |
| [`patterns/http-no-retry`](./patterns/http-no-retry/) | `retryPolicy.type: none` |
| [`patterns/xslt-static`](./patterns/xslt-static/) | `XsltTransform` with map name from `Artifacts/Maps` |
| [`patterns/xslt-dynamic`](./patterns/xslt-dynamic/) | `XsltTransform` with map name resolved at runtime |
| [`patterns/inline-csharp`](./patterns/inline-csharp/) | `CSharpCode` action calling the [`../CustomCode`](../CustomCode/) project |
| [`patterns/invoke-function`](./patterns/invoke-function/) | `Function` action invoking a custom Azure Function |
| [`patterns/tracked-properties`](./patterns/tracked-properties/) | Custom Application Insights properties on an action |

### Patterns to add (not yet present)

These would be high-value additions when next regenerating examples:

- `patterns/peek-lock-and-settle` — `peekLockQueueMessagesV2` trigger + `completeQueueMessageV2` / `abandonQueueMessageV2` / `deadLetterQueueMessageV2` (Service Bus settlement)
- `patterns/variables-and-until` — `InitializeVariable` + `Until` loop with `expression` and `limit`
- `patterns/foreach-serial` — `Foreach` with `runtimeConfiguration.concurrency.repetitions = 1`

## Workspace layout

```
LogicApp_Template/
├── connectors.json                 # service-provider catalog (lookup)
├── builtins.json                   # built-in actions/triggers (lookup)
├── Generate-ConnectorCatalog.ps1   # regenerator for connectors.json
├── patterns/                       # workflow.json shape examples
├── connections.json
├── host.json
├── parameters.json
├── local.settings.json
├── Artifacts/                      # Maps, Schemas, Rules
├── lib/                            # shared assemblies for inline C#
└── workflow-designtime/            # used to generate connectors.json
../CustomCode/                      # .NET 8 isolated worker for inline functions
```

## Rules of thumb for agents

1. **Never invent** a `serviceProviderId` or `operationId` — look up in `connectors.json`.
2. **Never invent** a built-in action `type` — look up in `builtins.json`. Common hallucinations: `Parallel`, `TryCatch`, `Sleep`, `Loop`, `Variable`. None exist.
3. **Casing matters**: `serviceProviderId` casing is inconsistent across providers (`AzureBlob`, `azurequeues`, `azureTables`, `serviceBus`, `keyVault`, `eventHub`, `openai`, …). Always copy verbatim.
4. **Service Bus settlement ops are V2-suffixed**: `completeQueueMessageV2`, `abandonQueueMessageV2`, `deadLetterQueueMessageV2`, `renewLockQueueMessageV2`, `deferQueueMessageV2`. Same pattern for topics. Non-V2 forms exist in old samples but should not be used in new workflows.
5. **`runAfter` statuses** are exactly: `Succeeded`, `Failed`, `Skipped`, `TimedOut`. Case-sensitive.
6. **Parallel = same `runAfter`**, not a wrapper action. See [`patterns/parallel`](./patterns/parallel/).
7. **Try/catch = two sibling Scopes**, second with `runAfter: { TryScope: [Failed, TimedOut, Skipped] }`. See [`patterns/exception-handler`](./patterns/exception-handler/).
