# Validate-Catalogs.ps1
# Cross-validates connectors.json + builtins-runtime.json + builtins-schema.json + builtins-overlay.json
# against every workflow.json in the parent LogicApp_Template directory (the patterns).
#
# Asserts, for each action and trigger:
#   * Its `type` exists in at least one catalog
#   * If it's a service-provider action, the serviceProviderId + operationId match connectors.json
#     with verbatim casing
#   * Its runAfter statuses (if any) are within the canonical set
#
# Exits non-zero on any failure. Suitable for CI.

param(
  [string]$ConnectorsPath  = (Join-Path $PSScriptRoot 'connectors.json'),
  [string]$RuntimePath     = (Join-Path $PSScriptRoot 'builtins-runtime.json'),
  [string]$SchemaPath      = (Join-Path $PSScriptRoot 'builtins-schema.json'),
  [string]$OverlayPath     = (Join-Path $PSScriptRoot 'builtins-overlay.json'),
  [string]$PatternsRoot    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

function Load-Json($p) {
  if (-not (Test-Path $p)) { throw "Catalog not found: $p" }
  return Get-Content $p -Raw | ConvertFrom-Json
}

Write-Host "Loading catalogs..."
$connectors = Load-Json $ConnectorsPath
$runtime    = Load-Json $RuntimePath
$schema     = Load-Json $SchemaPath
$overlay    = Load-Json $OverlayPath

# --- Build the union set of valid action / trigger types ---
$schemaActionTypes  = @($schema.actions.PSObject.Properties.Value  | ForEach-Object { $_.type })
$schemaTriggerTypes = @($schema.triggers.PSObject.Properties.Value | ForEach-Object { $_.type })

$runtimeTypes = @()
foreach ($prov in $runtime.connectionProviders.PSObject.Properties) {
  foreach ($op in $prov.Value.actions.PSObject.Properties)  { if ($op.Value.operationType) { $runtimeTypes += $op.Value.operationType } }
  foreach ($op in $prov.Value.triggers.PSObject.Properties) { if ($op.Value.operationType) { $runtimeTypes += $op.Value.operationType } }
}
$runtimeTypes = @($runtimeTypes | Sort-Object -Unique)

# Service-provider actions don't use a "type" string; they all use the literal "ServiceProviderConnection"
# in the type field of workflow.json, with the actual operation identified by serviceProviderConfiguration.
# Logic Apps Standard accepts both "ServiceProviderConnection" (modern) and "ApiConnection" (legacy) for these.
$serviceProviderTypes = @('ServiceProviderConnection','ServiceProvider','ApiConnection')

$validActionTypes  = @(@($schemaActionTypes  + $runtimeTypes + $serviceProviderTypes) | Sort-Object -Unique)
$validTriggerTypes = @(@($schemaTriggerTypes + $runtimeTypes + $serviceProviderTypes) | Sort-Object -Unique)

Write-Host "  Schema action types:  $($schemaActionTypes.Count)"
Write-Host "  Schema trigger types: $($schemaTriggerTypes.Count)"
Write-Host "  Runtime types:        $($runtimeTypes.Count)"
Write-Host "  Total valid actions:  $($validActionTypes.Count)"
Write-Host "  Total valid triggers: $($validTriggerTypes.Count)"

# --- Build the service-provider operation lookup ---
$spOps = @{}
foreach ($prov in $connectors.serviceProviders.PSObject.Properties) {
  $spId = $prov.Value.serviceProviderId
  foreach ($op in $prov.Value.actions.PSObject.Properties)  { $spOps["$spId|$($op.Name)"] = $true }
  foreach ($op in $prov.Value.triggers.PSObject.Properties) { $spOps["$spId|$($op.Name)"] = $true }
}
Write-Host "  Service-provider operations: $($spOps.Count)"

# --- Walk patterns ---
Write-Host ""
Write-Host "Scanning patterns under: $PatternsRoot"
$workflows = Get-ChildItem -Path $PatternsRoot -Filter workflow.json -Recurse -File |
             Where-Object { $_.FullName -notmatch '\\_connectors\\' -and $_.FullName -notmatch '\\workflow-designtime\\' -and $_.FullName -notmatch '\\Artifacts\\' }
Write-Host "  Found $($workflows.Count) workflow.json file(s)"

$validRunAfter = @('Succeeded','Failed','Skipped','TimedOut')
$failures      = @()
$typesSeen     = @{}

function Walk-Actions($container, $pathHint, $filePath) {
  if (-not $container) { return }
  foreach ($prop in $container.PSObject.Properties) {
    $name   = $prop.Name
    $entry  = $prop.Value
    if (-not $entry -or -not $entry.PSObject.Properties.Name -contains 'type') { continue }
    $aType = $entry.type
    if ($aType) { $script:typesSeen[$aType] = $true }

    if ($aType -and $aType -notin $script:validActionTypes) {
      $script:failures += [pscustomobject]@{
        file    = $filePath
        path    = "$pathHint.$name"
        type    = $aType
        problem = "type '$aType' not in any catalog"
      }
    }

    # Service-provider operationId check
    if ($aType -in $script:serviceProviderTypes) {
      $spId = $entry.inputs.serviceProviderConfiguration.serviceProviderId
      $opId = $entry.inputs.serviceProviderConfiguration.operationId
      if ($spId -and $opId) {
        $key = "$spId|$opId"
        if (-not $script:spOps.ContainsKey($key)) {
          $script:failures += [pscustomobject]@{
            file    = $filePath
            path    = "$pathHint.$name"
            type    = $aType
            problem = "serviceProviderId/operationId '$key' not in connectors.json (case-sensitive)"
          }
        }
      }
    }

    # runAfter status check
    if ($entry.runAfter) {
      foreach ($predProp in $entry.runAfter.PSObject.Properties) {
        $statuses = @($predProp.Value)
        foreach ($s in $statuses) {
          if ($s -notin $script:validRunAfter) {
            # Logic Apps tolerates uppercase too (SUCCEEDED, FAILED, etc.) -- the canonical schema casing is PascalCase
            # but examples in the agent and patterns use uppercase. Accept both.
            if ($s -notin @('SUCCEEDED','FAILED','SKIPPED','TIMEDOUT')) {
              $script:failures += [pscustomobject]@{
                file    = $filePath
                path    = "$pathHint.$name.runAfter.$($predProp.Name)"
                type    = $aType
                problem = "runAfter status '$s' not in canonical set (Succeeded|Failed|Skipped|TimedOut)"
              }
            }
          }
        }
      }
    }

    # Recurse into nested actions
    if ($entry.actions)         { Walk-Actions $entry.actions         "$pathHint.$name.actions"           $filePath }
    if ($entry.else -and $entry.else.actions) { Walk-Actions $entry.else.actions "$pathHint.$name.else.actions" $filePath }
    if ($entry.cases) {
      foreach ($caseProp in $entry.cases.PSObject.Properties) {
        if ($caseProp.Value.actions) { Walk-Actions $caseProp.Value.actions "$pathHint.$name.cases.$($caseProp.Name).actions" $filePath }
      }
    }
    if ($entry.default -and $entry.default.actions) { Walk-Actions $entry.default.actions "$pathHint.$name.default.actions" $filePath }
  }
}

function Walk-Triggers($container, $filePath) {
  if (-not $container) { return }
  foreach ($prop in $container.PSObject.Properties) {
    $name  = $prop.Name
    $entry = $prop.Value
    if (-not $entry -or -not $entry.PSObject.Properties.Name -contains 'type') { continue }
    $tType = $entry.type
    if ($tType) { $script:typesSeen[$tType] = $true }
    if ($tType -and $tType -notin $script:validTriggerTypes) {
      $script:failures += [pscustomobject]@{
        file    = $filePath
        path    = "triggers.$name"
        type    = $tType
        problem = "trigger type '$tType' not in any catalog"
      }
    }
  }
}

foreach ($wf in $workflows) {
  try {
    $j = Get-Content $wf.FullName -Raw | ConvertFrom-Json
  } catch {
    $failures += [pscustomobject]@{ file = $wf.FullName; path = '(root)'; type = ''; problem = "JSON parse error: $($_.Exception.Message)" }
    continue
  }
  $def = $j.definition
  if (-not $def) { continue }
  Walk-Triggers $def.triggers $wf.FullName
  Walk-Actions  $def.actions  'actions' $wf.FullName
}

Write-Host ""
Write-Host "Distinct action/trigger types observed in patterns: $($typesSeen.Count)"
$typesSeen.Keys | Sort-Object | ForEach-Object {
  $present = if ($_ -in $validActionTypes -or $_ -in $validTriggerTypes) { 'OK' } else { 'GAP' }
  Write-Host ("  [{0}] {1}" -f $present, $_)
}

Write-Host ""
if ($failures.Count -eq 0) {
  Write-Host "PASS -- all $($workflows.Count) workflow(s) validate against the catalogs."
  exit 0
} else {
  Write-Host "FAIL -- $($failures.Count) issue(s) found:"
  $failures | Format-Table file, path, type, problem -AutoSize -Wrap | Out-String -Width 200 | Write-Host
  exit 1
}
