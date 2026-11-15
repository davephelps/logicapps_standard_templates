<#
.SYNOPSIS
    Verifies TestRunHooks.PatchForEmulators and StripWorkflowsAuthenticationMethod across Integration and E2E harnesses.

.DESCRIPTION
    Builds Tests/LogicApp.Tests.Integration and E2E/LogicApp.Tests.E2E, compiles a disposable runner
    against both assemblies, and verifies:
      1. ManagedServiceIdentity entries (SystemAssigned and UserAssigned) across serviceBus, AzureBlob,
         azureTables, azurequeues, AzureFile, eventHub, eventGridPublisher, and sql are swapped to valid
         emulator connectionString/accessKey shapes with populated local.settings.json Values.
      2. Retained non-Entra SQL connectionString entries are never mutated.
      3. WORKFLOWS_AUTHENTICATION_METHOD is stripped case-insensitively from Values.
#>
[CmdletBinding()]
param(
    [string]$TemplateRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$sourceRoot = (Resolve-Path -LiteralPath $TemplateRoot).Path
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("harness-patch-verify-" + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($work) | Out-Null

try {
    $intProj = Join-Path $sourceRoot 'Tests\LogicApp.Tests.Integration\LogicApp.Tests.Integration.csproj'
    $e2eProj = Join-Path $sourceRoot 'E2E\LogicApp.Tests.E2E\LogicApp.Tests.E2E.csproj'
    $connectionsJsonPath = Join-Path $sourceRoot 'LogicApp_Template\connections.json'

    $runnerCsproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$intProj" />
    <ProjectReference Include="$e2eProj" />
  </ItemGroup>
</Project>
"@
    [System.IO.File]::WriteAllText((Join-Path $work 'PatchVerifier.csproj'), $runnerCsproj, $utf8NoBom)

    $runnerCs = @'
using System;
using System.IO;
using Newtonsoft.Json.Linq;

var templateConnectionsPath = args[0];
var rawTemplate = File.ReadAllText(templateConnectionsPath);

void VerifyPatch(string label, Func<JObject, JObject, bool> invokePatch, Func<JObject, bool> invokeStrip)
{
    var connDoc = JObject.Parse(rawTemplate);
    var spc = (JObject)connDoc["serviceProviderConnections"]!;

    // Add a UserAssigned MSI connection and a retained non-Entra SQL connectionString entry
    spc["ordersSb_uami"] = JObject.Parse(@"{
      ""parameterValues"": {
        ""fullyQualifiedNamespace"": ""@appsetting('ordersSb_ns')"",
        ""authProvider"": { ""Type"": ""ManagedServiceIdentity"", ""Identity"": ""/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/uami-out"" }
      },
      ""parameterSetName"": ""ManagedServiceIdentity"",
      ""serviceProvider"": { ""id"": ""/serviceProviders/serviceBus"" },
      ""displayName"": ""Orders_SB_UAMI""
    }");
    spc["retainedOnPremSql"] = JObject.Parse(@"{
      ""parameterValues"": { ""connectionString"": ""@appsetting('retainedOnPremSql_conn')"" },
      ""parameterSetName"": ""connectionString"",
      ""serviceProvider"": { ""id"": ""/serviceProviders/sql"" },
      ""displayName"": ""Retained_SQL_Auth""
    }");

    var values = JObject.Parse(@"{
      ""WORKFLOWS_AUTHENTICATION_METHOD"": ""managedServiceIdentity"",
      ""retainedOnPremSql_conn"": ""Server=sql01.corp.internal;Database=Erp;User Id=app;Password=placeholder;""
    }");

    var mutated = invokePatch(connDoc, values);
    if (!mutated) throw new InvalidOperationException($"{label}: expected mutated=true");

    if (values.Property("WORKFLOWS_AUTHENTICATION_METHOD", StringComparison.OrdinalIgnoreCase) is not null)
        throw new InvalidOperationException($"{label}: WORKFLOWS_AUTHENTICATION_METHOD was not stripped");

    foreach (var name in new[] { "serviceBus", "ordersSb_uami", "AzureBlob", "azureTables", "azurequeues", "AzureFile", "eventHub", "sql" })
    {
        var c = (JObject)spc[name]!;
        if (c["parameterSetName"]?.ToString() != "connectionString")
            throw new InvalidOperationException($"{label}: {name} expected parameterSetName=connectionString, got {c["parameterSetName"]}");
        var settingRef = c["parameterValues"]?["connectionString"]?.ToString() ?? string.Empty;
        var expectedRef = $"@appsetting('{name}_connectionString')";
        if (settingRef != expectedRef)
            throw new InvalidOperationException($"{label}: {name} expected {expectedRef}, got {settingRef}");
        if (string.IsNullOrWhiteSpace(values[$"{name}_connectionString"]?.ToString()))
            throw new InvalidOperationException($"{label}: {name}_connectionString missing in Values");
    }

    var eg = (JObject)spc["eventGridPublisher"]!;
    if (eg["parameterSetName"]?.ToString() != "accessKey")
        throw new InvalidOperationException($"{label}: eventGridPublisher expected parameterSetName=accessKey");
    if (values["eventGridPublisher_topicEndpoint"]?.ToString() != "http://127.0.0.1:9091/eventgrid/api/events")
        throw new InvalidOperationException($"{label}: eventGridPublisher_topicEndpoint mismatch");

    // Verify retained SQL connectionString was NOT altered
    var retained = (JObject)spc["retainedOnPremSql"]!;
    if (retained["parameterSetName"]?.ToString() != "connectionString" ||
        retained["parameterValues"]?["connectionString"]?.ToString() != "@appsetting('retainedOnPremSql_conn')")
    {
        throw new InvalidOperationException($"{label}: retainedOnPremSql was unexpectedly modified");
    }

    var dtValues = JObject.Parse(@"{ ""workflows_authentication_method"": ""managedServiceIdentity"", ""APP_KIND"": ""workflowapp"" }");
    if (!invokeStrip(dtValues) || dtValues.Property("workflows_authentication_method", StringComparison.OrdinalIgnoreCase) is not null)
        throw new InvalidOperationException($"{label}: case-insensitive StripWorkflowsAuthenticationMethod failed");

    Console.WriteLine($"[OK] {label}: MSI->emulator swap, retained SQL preservation, and WORKFLOWS_AUTHENTICATION_METHOD strip verified.");
}

VerifyPatch(
    "Integration.TestRunHooks",
    (c, v) => LogicApp.Tests.Integration.Infrastructure.TestRunHooks.PatchForEmulators(c, v, "Endpoint=sb://localhost:5672/;UseDevelopmentEmulator=true", "UseDevelopmentStorage=true", "http://127.0.0.1:9091", "Server=127.0.0.1,1433;Database=TestDb;"),
    v => LogicApp.Tests.Integration.Infrastructure.TestRunHooks.StripWorkflowsAuthenticationMethod(v));

VerifyPatch(
    "E2E.TestRunHooks",
    (c, v) => LogicApp.Tests.E2E.Infrastructure.TestRunHooks.PatchForEmulators(c, v, "Endpoint=sb://localhost:5672/;UseDevelopmentEmulator=true", "UseDevelopmentStorage=true", "http://127.0.0.1:9091", "Server=127.0.0.1,1433;Database=TestDb;"),
    v => LogicApp.Tests.E2E.Infrastructure.TestRunHooks.StripWorkflowsAuthenticationMethod(v));
'@
    [System.IO.File]::WriteAllText((Join-Path $work 'Program.cs'), $runnerCs, $utf8NoBom)

    $out = & dotnet run --project (Join-Path $work 'PatchVerifier.csproj') --configuration Debug -- $connectionsJsonPath 2>&1
    $code = $LASTEXITCODE
    $out | ForEach-Object { Write-Host $_.ToString() }
    if ($code -ne 0) {
        exit $code
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
exit 0
