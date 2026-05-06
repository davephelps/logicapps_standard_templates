# Generate-ConnectorCatalog.ps1
# Queries the local Logic Apps designtime runtime (func host start in workflow-designtime/)
# and produces:
#   * connectors.json        — the service-provider catalog (/serviceProviders/*)
#   * builtins-runtime.json  — bundle-added built-in operations (/connectionProviders/*)
#                              including the canonical action 'type' string per operation
#                              (InvokeFunction, CSharpScriptCode, Xslt, Liquid, Workflow, Function, ApiManagement,
#                               Batch, SendToBatch, FlatFileEncoding/Decoding, XmlValidation, IntegrationAccountArtifactLookup, etc.)
#
# Run AFTER starting the designtime host on http://localhost:7071.
#
# NOTE: Workflow-engine primitives (Http, If, Switch, Foreach, Until, Scope, Compose, ParseJson, Select, Query,
#       Join, Table, Terminate, Wait, InitializeVariable/SetVariable/etc., Request, Response, Recurrence) are
#       hardwired in the engine and NOT exposed via operationGroups. They remain in the hand-curated builtins.json.

param(
  [string]$BaseUrl = "http://localhost:7071",
  [string]$ConnectorsOutFile = "connectors.json",
  [string]$BuiltinsOutFile   = "builtins-runtime.json",
  [string[]]$IncludeProviders = @() # empty = all
)

$ErrorActionPreference = 'Stop'

function Get-Json($path) {
  $sep = if ($path.Contains('?')) { '&' } else { '?' }
  $url = "$BaseUrl$path${sep}api-version=2020-05-01-preview"
  return Invoke-RestMethod $url -TimeoutSec 30
}

function Get-OperationEntry($gname, $opName) {
  try {
    $detail = Get-Json "/runtime/webhooks/workflow/api/management/operationGroups/$gname/operations/$opName`?`$expand=properties/manifest"
  } catch {
    Write-Warning "    skip $opName : $($_.Exception.Message)"
    return $null
  }
  $m = $detail.properties.manifest
  if (-not $m) { return $null }

  $params = @()
  $required = @()
  if ($m.inputs.properties) {
    foreach ($prop in $m.inputs.properties.PSObject.Properties) {
      $p = [ordered]@{ name = $prop.Name }
      if ($prop.Value.type) { $p.type = $prop.Value.type }
      if ($prop.Value.enum) { $p.enum = $prop.Value.enum }
      if ($prop.Value.description) { $p.description = $prop.Value.description }
      $params += $p
    }
  }
  if ($m.inputs.required) { $required = @($m.inputs.required) }

  $entry = [ordered]@{
    summary       = $detail.properties.summary
    operationType = $detail.properties.operationType   # canonical action 'type' for workflow.json
    parameters    = $params
    required      = $required
  }
  return @{ entry = $entry; detail = $detail }
}

Write-Host "Fetching operationGroups..."
$groups = (Get-Json "/runtime/webhooks/workflow/api/management/operationGroups").value
Write-Host "  Found $($groups.Count) groups"

$connectors = [ordered]@{
  bundleVersion    = "1.161.25"
  generatedUtc     = (Get-Date).ToUniversalTime().ToString('o')
  serviceProviders = [ordered]@{}
}

$builtins = [ordered]@{
  bundleVersion       = "1.161.25"
  generatedUtc        = (Get-Date).ToUniversalTime().ToString('o')
  '$comment'          = "Bundle-added built-in operations exposed under /connectionProviders/*. Each operation's 'operationType' field is the canonical 'type' string for workflow.json. Workflow-engine primitives (Http, If, Switch, Foreach, Compose, ParseJson, Scope, etc.) are NOT here — see builtins.json."
  connectionProviders = [ordered]@{}
}

foreach ($g in $groups) {
  $gid   = $g.id
  $gname = $g.name
  if ($IncludeProviders.Count -gt 0 -and $gname -notin $IncludeProviders) { continue }

  # Note: $gid may or may not have a leading '/'. Normalise before matching.
  $gidNorm = if ($gid.StartsWith('/')) { $gid } else { "/$gid" }
  $isServiceProvider    = $gidNorm -like '/serviceProviders/*'
  $isConnectionProvider = $gidNorm -like '/connectionProviders/*'
  if (-not ($isServiceProvider -or $isConnectionProvider)) { continue }

  Write-Host "  Group: $gname ($gid)"
  $ops = (Get-Json "/runtime/webhooks/workflow/api/management/operationGroups/$gname/operations").value

  $actions  = [ordered]@{}
  $triggers = [ordered]@{}
  $firstDetail = $null

  foreach ($op in $ops) {
    $r = Get-OperationEntry $gname $op.name
    if ($null -eq $r) { continue }
    if ($null -eq $firstDetail) { $firstDetail = $r.detail }

    $isTrigger = ($r.detail.properties.operationType -eq 'ServiceProviderNotification' `
                  -or $r.detail.properties.trigger `
                  -or $op.name -match '^(when|on)[A-Z]')
    if ($isTrigger) { $triggers[$op.name] = $r.entry } else { $actions[$op.name] = $r.entry }
  }

  if ($isServiceProvider) {
    # Pull connection-string parameter name from first ops connector definition
    $connKey = $null
    if ($firstDetail) {
      try {
        $sets = $firstDetail.properties.manifest.connector.properties.connectionParameterSets.values
        if ($sets) {
          $cs = $sets | Where-Object { $_.name -eq 'connectionString' } | Select-Object -First 1
          if ($cs -and $cs.parameters.connectionString) {
            $connKey = "${gname}_connectionString"
          }
        }
      } catch {}
    }
    $connectors.serviceProviders[$gname] = [ordered]@{
      serviceProviderId       = $gid
      connectionStringSetting = $connKey
      actions                 = $actions
      triggers                = $triggers
    }
  }
  else {
    $builtins.connectionProviders[$gname] = [ordered]@{
      connectionProviderId = $gid
      actions              = $actions
      triggers             = $triggers
    }
  }
}

$connectors | ConvertTo-Json -Depth 12 | Out-File $ConnectorsOutFile -Encoding utf8
Write-Host "Wrote $ConnectorsOutFile ($((Get-Item $ConnectorsOutFile).Length) bytes, $($connectors.serviceProviders.Count) providers)"

$builtins | ConvertTo-Json -Depth 12 | Out-File $BuiltinsOutFile -Encoding utf8
Write-Host "Wrote $BuiltinsOutFile ($((Get-Item $BuiltinsOutFile).Length) bytes, $($builtins.connectionProviders.Count) providers)"
