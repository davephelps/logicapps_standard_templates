# Generate-Builtins.ps1
# Fetches Microsoft's published Logic Apps workflow definition schema and emits
# builtins-schema.json -- a slim catalog of every workflow-engine action type
# and trigger type that exists in the schema, with type names extracted verbatim.
#
# This file is re-generated, never hand-edited. If the schema is ever revised
# upstream, re-run this script and the catalog refreshes automatically.
#
# OUTPUT: builtins-schema.json
#   {
#     "schemaUrl": "...",
#     "generatedUtc": "...",
#     "triggers": { "<TypeName>": { "kind": [...], "inputs": [...], "$source": "schema oneOf #N" } },
#     "actions":  { "<TypeName>": { "kind": [...], "inputs": [...], "$source": "schema oneOf #N" } }
#   }

param(
  [string]$SchemaUrl = 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json',
  [string]$OutFile   = 'builtins-schema.json'
)

$ErrorActionPreference = 'Stop'

Write-Host "Fetching schema: $SchemaUrl"
$schema = Invoke-RestMethod $SchemaUrl -TimeoutSec 30

function Get-OneOfEntries($container) {
  # Container is properties.actions.additionalProperties or properties.triggers.additionalProperties
  # The shape is { allOf: [ {runAfter/...}, {type/kind/...}, { oneOf: [ {type:{enum:[...]}, ...}, ... ] } ] }
  $allOf = $container.allOf
  $oneOf = $null
  foreach ($a in $allOf) {
    if ($a.PSObject.Properties.Name -contains 'oneOf') { $oneOf = $a.oneOf; break }
  }
  return $oneOf
}

function Get-InputProperties($entry) {
  # Best-effort: pull the top-level property names from the entry's inputs object.
  # The schema uses heavy $refs so we don't resolve everything; we record what's directly visible.
  $inputs = $entry.properties.inputs
  if (-not $inputs) { return @() }
  $names = @()
  if ($inputs.PSObject.Properties.Name -contains 'properties' -and $inputs.properties) {
    foreach ($p in $inputs.properties.PSObject.Properties) { $names += $p.Name }
  }
  if ($inputs.PSObject.Properties.Name -contains 'allOf' -and $inputs.allOf) {
    foreach ($a in $inputs.allOf) {
      if ($a.PSObject.Properties.Name -contains 'properties' -and $a.properties) {
        foreach ($p in $a.properties.PSObject.Properties) { $names += $p.Name }
      }
    }
  }
  return ($names | Sort-Object -Unique)
}

function Build-Catalog($container, $kindLabel) {
  $oneOf = Get-OneOfEntries $container
  if (-not $oneOf) { throw "Could not locate oneOf for $kindLabel" }
  $catalog = [ordered]@{}
  $idx = 0
  foreach ($entry in $oneOf) {
    $idx++
    if (-not $entry.properties -or -not $entry.properties.type -or -not $entry.properties.type.enum) { continue }
    foreach ($typeName in $entry.properties.type.enum) {
      $kindEnum = @()
      if ($entry.properties.PSObject.Properties.Name -contains 'kind' -and $entry.properties.kind.enum) {
        $kindEnum = @($entry.properties.kind.enum)
      }
      $inputProps = Get-InputProperties $entry
      $key = if ($kindEnum.Count -gt 0 -and $catalog.Contains($typeName)) {
        "$typeName($($kindEnum -join ','))"
      } else {
        $typeName
      }
      $catalog[$key] = [ordered]@{
        type            = $typeName
        kind            = $kindEnum
        inputProperties = $inputProps
        '$source'       = "schema $kindLabel oneOf #$idx"
      }
    }
  }
  return $catalog
}

Write-Host "Extracting action types..."
$actions = Build-Catalog $schema.properties.actions.additionalProperties 'actions'

Write-Host "Extracting trigger types..."
$triggers = Build-Catalog $schema.properties.triggers.additionalProperties 'triggers'

# Pull the master action type enum (triggers don't have a single union enum).
$actionMasterEnum = @()
foreach ($a in $schema.properties.actions.additionalProperties.allOf) {
  if ($a.properties -and $a.properties.type -and $a.properties.type.enum) { $actionMasterEnum = @($a.properties.type.enum); break }
}

# Add stubs for action types that appear in the master enum but whose oneOf
# entries are $refs we did not resolve (e.g. Request/Recurrence/HttpWebhook/Batch/SlidingWindow,
# which are also triggers and reuse trigger definitions).
$catalogActionTypes = @($actions.Values | ForEach-Object { $_.type } | Sort-Object -Unique)
$stubAdded = @()
foreach ($t in $actionMasterEnum) {
  if ($t -notin $catalogActionTypes) {
    $actions[$t] = [ordered]@{
      type            = $t
      kind            = @()
      inputProperties = @()
      '$source'       = "schema actions master enum (oneOf entry is `$ref to trigger; see triggers for shape)"
    }
    $stubAdded += $t
  }
}

$catalogActionTypes  = @($actions.Values  | ForEach-Object { $_.type } | Sort-Object -Unique)
$catalogTriggerTypes = @($triggers.Values | ForEach-Object { $_.type } | Sort-Object -Unique)
$missingActions      = @($actionMasterEnum | Where-Object { $_ -notin $catalogActionTypes })

$out = [ordered]@{
  schemaUrl    = $SchemaUrl
  generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
  '$comment'   = "Auto-generated from Microsoft's published workflow definition schema. Do not hand-edit. Pair with builtins-runtime.json (bundle ops) and connectors.json (service providers). Trigger types are extracted from the schema's trigger oneOf union (no master enum exists)."
  coverage     = [ordered]@{
    actionTypesInMasterEnum = $actionMasterEnum.Count
    actionTypesCatalogued   = $catalogActionTypes.Count
    actionTypesMissing      = $missingActions
    actionTypesAddedAsStub  = $stubAdded
    triggerTypesCatalogued  = $catalogTriggerTypes.Count
  }
  triggers     = $triggers
  actions      = $actions
}

$out | ConvertTo-Json -Depth 12 | Out-File $OutFile -Encoding utf8
$size = (Get-Item $OutFile).Length
Write-Host ""
Write-Host "Wrote $OutFile ($size bytes)"
Write-Host "  Action types catalogued:  $($catalogActionTypes.Count) / $($actionMasterEnum.Count) in schema master enum (stubs added: $($stubAdded.Count))"
Write-Host "  Trigger types catalogued: $($catalogTriggerTypes.Count) (no master enum in schema)"
if ($missingActions.Count -gt 0) { Write-Warning "  Action types still missing: $($missingActions -join ', ')"; exit 1 }
