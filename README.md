# Logic Apps Standard Templates

Reference workspace, connector catalogs, custom-code project template, and Reqnroll integration and E2E test harnesses for Azure Logic Apps Standard.

## Repository layout

- `LogicApp_Template/` -- reference Logic App Standard project, workflow shape patterns, `connections.json`, `connections.azure.example.json`, and `_connectors/` lookup catalogs.
- `CustomCode/` -- .NET 8 isolated-worker custom code library (`Microsoft.Azure.Workflows.Webjobs.Sdk`) publishing assemblies and `function.json` metadata into `LogicApp_Template/lib/custom/`.
- `Tests/LogicApp.Tests.Unit/` -- MSTest static workflow and cross-reference checks.
- `Tests/LogicApp.Tests.Integration/` -- Reqnroll + xUnit per-Logic-App integration harness (`func host start` + Azurite + Service Bus emulator + WireMock).
- `E2E/LogicApp.Tests.E2E/` -- Reqnroll + xUnit domain-level E2E harness adding `ApimEmulator` and `APIMClient`.
- `OptionalFixtures/` -- opt-in emulator fixtures (`SqlServerFixture.cs` for `azure-sql-edge` + `Microsoft.Data.SqlClient`, and `FtpEmulatorFixture.cs` for in-process passive FTP). Kept outside the default test `.csproj` files so projects without SQL or FTP connectors do not pull extra dependencies.
- `Tests/Test-CustomCodeBuildMetadata.ps1` -- four-path build verification (`clean-build`, `incremental-build`, `explicit-publish`, and `project-reference-build`) asserting `function.json` metadata and non-empty assemblies.

## Machine prerequisites

### .NET runtimes and SDKs

- **Required host runtimes:** `Microsoft.NETCore.App 8.0.x` and `Microsoft.AspNetCore.App 8.0.x` must be installed so the Logic Apps Standard local host (`func host start`) and `Microsoft.Azure.Workflows.WebJobs.Tests.Extension` test host can boot.
- **SDK setup:** `net8.0` / `net8` projects build cleanly when a `.NET 8` or `.NET 10` SDK is installed alongside the `.NET 8` shared runtimes (side-by-side `.NET 8` + `.NET 10` SDK installation is supported).
- **Support lifecycle note:** General Microsoft support for `.NET 8` ended on 10 November 2026. These local setup instructions document the current Logic Apps Standard runtime requirement and do not authorize production deployment on an out-of-support framework without an explicit customer framework decision.

### Azure Functions Core Tools v4

Install Azure Functions Core Tools v4 (`func --version` starting with `4.`), or use the bundled Core Tools installed by the VS Code Azure Logic Apps (Standard) extension under `%USERPROFILE%\.azurelogicapps\dependencies\FuncCoreTools`.

### Docker Desktop (Windows WSL2 backend)

Integration and E2E suites run Azurite (`mcr.microsoft.com/azure-storage/azurite:3.33.0`) and the Azure Service Bus emulator (`mcr.microsoft.com/azure-messaging/servicebus-emulator:1.1.2` + `mcr.microsoft.com/azure-sql-edge:1.0.7`) via Testcontainers.

1. Enable Windows features `Microsoft-Windows-Subsystem-Linux` and `VirtualMachinePlatform`, and complete any pending Windows reboot before starting Docker Desktop.
2. Start Docker Desktop in Linux-containers mode (`docker info --format "{{.OSType}}"` returns `linux`).
3. Optional image pre-pulls to avoid first-run timeouts:
   ```powershell
   docker pull mcr.microsoft.com/azure-storage/azurite:3.33.0
   docker pull mcr.microsoft.com/azure-messaging/servicebus-emulator:1.1.2
   docker pull mcr.microsoft.com/azure-sql-edge:1.0.7
   ```
4. **Troubleshooting note (Docker Resource Saver):** Docker Desktop's Resource Saver pauses the VM only when no containers are running. If idle host wake-up delays cause initial `docker info` timeouts on a developer workstation, disabling Resource Saver is an optional troubleshooting step -- change workstation settings only with operator approval.

## Local test execution rules

1. **Run SDK unit tests sequentially (port 7071 contention):** Projects using `Microsoft.Azure.Workflows.WebJobs.Tests.Extension` (`UnitTestExecutor`) start a local Functions host on fixed TCP port `7071`. Never run two `UnitTestExecutor` test suites in parallel on the same machine, and verify no orphaned `func` or `testhost` process is listening on port `7071` before running unit tests.
2. **Configuration file restoration (`local.settings.json`, `connections.json`, `parameters.json`):** `TestRunHooks` snapshots all three files before starting `func host` and restores them (UTF-8 without BOM) in `[AfterTestRun]` and on startup failure. If a test runner process is forcefully terminated (`kill -9` / Task Manager) before `[AfterTestRun]` runs, restore `LogicApp/local.settings.json`, `LogicApp/connections.json`, and `LogicApp/parameters.json` from Git or your local backup before opening the VS Code workflow designer.
3. **Opt-in SQL and FTP fixtures:** Copy `OptionalFixtures/SqlServerFixture.cs` into `Infrastructure/` (and add `<PackageReference Include="Microsoft.Data.SqlClient" Version="5.2.2" />` to the test `.csproj`) only when testing workflows that use `/serviceProviders/sql`. Copy `OptionalFixtures/FtpEmulatorFixture.cs` into `Infrastructure/` when testing `/serviceProviders/Ftp` workflows; assert workflow action retry counts via `LogicAppTrigger.GetLatestActionRepetitionCountAsync` rather than counting raw FTP protocol commands.
