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

3. **Copy a sibling pattern folder from `LogicApp_Template/`** when you need *shape* — a workflow structure
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

## Validating generated workflows

[`Validate-Workflows.ps1`](./Validate-Workflows.ps1) is the mechanical gate that enforces the rules in this README against a user-generated Logic App folder.

```powershell
# From the LogicApp folder you generated
.\_connectors\Validate-Workflows.ps1 -Path .

# With reference resolution against local.settings.json and parameters.json
.\_connectors\Validate-Workflows.ps1 -Path . -CheckAppSettings -CheckParameters -Strict
```

Sibling of [`Validate-Catalogs.ps1`](./Validate-Catalogs.ps1): that script validates the **shipped patterns** against the catalogs; this script validates **user-generated workflows** against the catalogs.

### Tiers of checks

All tiers run by default; warnings only fail the run under `-Strict`.

| Tier | What it catches |
|---|---|
| **T1 Type-in-catalog** | Every action/trigger `type` exists in `builtins-runtime` ∪ `builtins-schema` ∪ `{ServiceProviderConnection, ServiceProvider, ApiConnection}`. Banned hallucinations (`Parallel`, `TryCatch`, `Sleep`, `Loop`, `Variable`, `CSharpCode`, `XsltTransform`) are flagged with a suggested replacement. |
| **T2 ServiceProvider deep** | `(serviceProviderId, operationId)` exists in `connectors.json` (case-sensitive); all `required` params present; param keys are a subset of the catalog; `connectionName` resolves to a `connections.json` entry whose `serviceProvider.id` matches. The well-known `(Sftp, createFile)` mistake gets a specific hint recommending `uploadFileContent`. |
| **T2b ApiConnection** | Structural shape only (no managed-API op-level catalog ships here): `inputs.host.connection.referenceName`, `inputs.method`, `inputs.path`; cross-references `referenceName` against `connections.json` → `managedApiConnections`. |
| **T3 Engine-primitive shape** | Per-type required-key checks for `Http`, `If`, `Switch`, `Foreach`, `Until`, `Scope`, `InitializeVariable` (type whitelist), `SetVariable`/`Increment`/`Decrement`/`AppendToArrayVariable`/`AppendToStringVariable`, `Compose`, `ParseJson` (content + schema), `Response`, `Wait`, `Terminate` (runStatus whitelist), `Workflow` (child), `Function`, `Liquid`, `Xslt`. |
| **T4 Cross-action** | `runAfter` keys reference real sibling actions in the same container; statuses ∈ `{Succeeded, Failed, Skipped, TimedOut}` (any case). |
| **T5 Expression discipline** | Three rules: (1) `json(concat(...string(...)))` anti-pattern, (2) action types called as inline functions inside `@expressions` (`select`, `query`, `compose`, `parseJson`, `terminate`, `xslt`), (3) `If`/`Until` expression with a bare comparator not wrapped in `and`/`or`/`not`. |
| **T6 Reference resolution** (opt-in) | Every `@appsetting('X')` has a key in `local.settings.json`; every `@parameters('Y')` has an entry in `parameters.json`. Enable via `-CheckAppSettings -CheckParameters`. |

### Action-type coverage matrix

| Action | T1 catalog | Deep shape |
|---|---|---|
| `Http`, `If`, `Switch`, `Foreach`, `Until`, `Scope`, `InitializeVariable`, `SetVariable`, `IncrementVariable`, `DecrementVariable`, `AppendToArrayVariable`, `AppendToStringVariable`, `Compose`, `ParseJson`, `Response`, `Wait`, `Terminate`, `Workflow`, `Function`, `Liquid`, `Xslt` | ✅ | ✅ T3 |
| `ServiceProvider`, `ServiceProviderConnection` | ✅ | ✅ T2 (op-level catalog) |
| `ApiConnection` | ✅ | 🟡 T2b (structural only — no managed-API catalog) |
| `Select`, `Query`, `Table`, `JavaScriptCode`, `CSharpScriptCode`, `FlatFileEncoding`, `FlatFileDecoding`, `XmlValidation`, `ApiConnectionWebhook`, `HttpWebhook` | ✅ | T1 + T4 + T5 only |

All actions, regardless of category, still get T1 (catalog + banned-hallucination gate), T4 (runAfter), T5 (expression discipline), and T6 (opt-in reference resolution).

### Exit codes

- `0` — pass (warnings allowed unless `-Strict`)
- `1` — at least one failure, or any warning under `-Strict`

## Patterns (shape, not strings)

Each folder contains exactly one `workflow.json` that demonstrates a structure.

Pattern folders live as siblings of this `_connectors/` folder, directly under `LogicApp_Template/`. Each is a deployable workflow folder (matching the Logic Apps Standard convention of one folder per workflow), so links from this README are relative paths up one level.

| Pattern | What it shows |
|---|---|
| [`parallel`](../parallel/) | Parallel branches = multiple actions sharing the same `runAfter`. **There is no `Parallel` action type.** |
| [`exception-handler`](../exception-handler/) | Try/catch via two sibling Scopes; catch uses `runAfter: { Try: [Failed, TimedOut, Skipped] }` |
| [`condition-if-else`](../condition-if-else/) | `If` action with `expression.and[...]` (or `or[...]`) wrapper - the designer renders BLANK if a bare comparison is at the top level, even though the runtime accepts both forms |
| [`switch-case`](../switch-case/) | `Switch` action with a SCALAR `expression` (not an and/or wrapper like `If`), one entry per matchable value under `cases`, plus a `default` branch |
| [`validate-and-short-circuit`](../validate-and-short-circuit/) | Precondition check that aborts with a typed non-2xx `Response` by putting the happy path inside `else.actions` - NO synthetic exceptions, NO `Terminate`, NO `Force_Scope_Failure` hacks |
| [`child-workflow-caller`](../child-workflow-caller/) + [`child-workflow-callee`](../child-workflow-callee/) | Invoking a sibling workflow with the `Workflow` action |
| [`http-oauth`](../http-oauth/) | `authentication.type: ActiveDirectoryOAuth` shape |
| [`http-basic`](../http-basic/) | `authentication.type: Basic` |
| [`http-cookie-auth`](../http-cookie-auth/) | Two-step login + cookie reuse |
| [`http-retry-policy`](../http-retry-policy/) | `retryPolicy.type: exponential` with ISO8601 intervals |
| [`http-no-retry`](../http-no-retry/) | `retryPolicy.type: none` |
| [`loop-collect`](../loop-collect/) | `Foreach` collecting per-iteration results for downstream aggregation |
| [`xslt-static`](../xslt-static/) | `XsltTransform` with map name from `Artifacts/Maps` |
| [`xslt-dynamic`](../xslt-dynamic/) | `XsltTransform` with map name resolved at runtime |
| [`inline-csharp`](../inline-csharp/) | `CSharpCode` action calling the [`../../CustomCode`](../../CustomCode/) project |
| [`invoke-function`](../invoke-function/) | `Function` action invoking a custom Azure Function |
| [`tracked-properties`](../tracked-properties/) | Custom Application Insights properties on an action |

### Patterns to add (not yet present)

These would be high-value additions when next regenerating examples:

- `http-bearer-token` — `authentication.type: Raw` (`Bearer ...`) shape for vendors that issue static or pre-fetched bearer tokens
- `peek-lock-and-settle` — `peekLockQueueMessagesV2` trigger + `completeQueueMessageV2` / `abandonQueueMessageV2` / `deadLetterQueueMessageV2` (Service Bus settlement)
- `variables-and-until` — `InitializeVariable` + `Until` loop with `expression` and `limit`
- `foreach-serial` — `Foreach` with `runtimeConfiguration.concurrency.repetitions = 1`

## Workspace layout

```
LogicApp_Template/
├── connectors.json                 # service-provider catalog (lookup)
├── builtins.json                   # built-in actions/triggers (lookup)
├── Generate-ConnectorCatalog.ps1   # regenerator for connectors.json
├── parallel/                       # one folder per pattern (workflow.json shape examples)
├── exception-handler/
├── condition-if-else/
├── switch-case/
├── validate-and-short-circuit/
├── child-workflow-caller/ + child-workflow-callee/
├── http-{oauth,basic,cookie-auth,retry-policy,no-retry}/
├── loop-collect/
├── xslt-{static,dynamic}/
├── inline-csharp/  invoke-function/  tracked-properties/
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
6. **Parallel = same `runAfter`**, not a wrapper action. See [`parallel`](../parallel/).
7. **Try/catch = two sibling Scopes**, second with `runAfter: { TryScope: [Failed, TimedOut, Skipped] }`. See [`exception-handler`](../exception-handler/).
