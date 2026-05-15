# Validate-Workflows.ps1
# Validates user-generated workflow.json files against the connector + built-in catalogs and
# a battery of common Logic Apps Standard authoring bug classes. Designed to be the mechanical
# gate that complements the human "self-review" pass in the Logic Apps generator agent.
#
# Sibling of Validate-Catalogs.ps1 -- that script validates the SHIPPED PATTERNS (catalog vs
# patterns); this script validates USER-GENERATED workflows (catalog vs deployed code).
#
# Tiers of checks (all enabled by default):
#   T1 Type-in-catalog       Every action/trigger `type` exists in builtins-runtime ∪ builtins-schema
#                            ∪ {ServiceProviderConnection, ServiceProvider, ApiConnection}.
#                            Banned hallucinations (Parallel, TryCatch, Sleep, Loop, Variable,
#                            CSharpCode, XsltTransform) are flagged with a suggested replacement.
#   T2 ServiceProvider deep  (spId, opId) exists in connectors.json (case-sensitive); all required
#                            params present; param keys are a subset of the catalog; connectionName
#                            resolves to a connections.json entry whose serviceProvider.id matches.
#                            ApiConnection (managed connector) actions get a lighter shape +
#                            cross-reference check (referenceName, inputs.method, inputs.path)
#                            because no managed-API operation catalog is shipped in _connectors/.
#   T3 Engine-primitive      Http requires uri+method and a known authentication.type; If/Switch/
#       shape                Foreach/Until/Scope require their structural keys; InitializeVariable
#                            variables[].type ∈ {Array, Boolean, Float, Integer, Object, String};
#                            SetVariable/Increment/Decrement/Append*Variable require name (+ value
#                            where applicable); Compose/ParseJson/Response/Wait/Terminate/Workflow/
#                            Function/Liquid/Xslt have the required-key checks documented in code.
#   T4 Cross-action          runAfter statuses ∈ canonical set (any case); runAfter keys reference
#                            sibling actions that actually exist in the same container.
#   T5 Expression discipline Three patterns from logic-apps-expression-rules.instructions.md:
#                            (1) json(concat(...string(...))), (2) action types called as inline
#                            functions inside @expressions, (3) If/Until expression with a bare
#                            comparator not wrapped in and/or/not.
#   T6 Reference resolution  (opt-in) every @appsetting('X') has a key in local.settings.json;
#                            every @parameters('Y') has an entry in parameters.json.
#
# Exits 0 on success, 1 on any failure. Warnings do not fail the run unless -Strict is passed.
#
# Usage:
#   .\Validate-Workflows.ps1 -Path .\LogicApp
#   .\Validate-Workflows.ps1 -Path .\LogicApp -CatalogsRoot ..\Workspace_Template\LogicApp_Template\_connectors
#   .\Validate-Workflows.ps1 -Path .\LogicApp -CheckAppSettings -CheckParameters -Strict

[CmdletBinding()]
param(
  [string]$Path,
  [string]$CatalogsRoot,
  [switch]$CheckAppSettings,
  [switch]$CheckParameters,
  [switch]$Strict
)

$ErrorActionPreference = 'Stop'

# ----- Locate workflow root --------------------------------------------------
if (-not $Path) {
  if (Test-Path '.\LogicApp\host.json') { $Path = '.\LogicApp' }
  elseif (Test-Path '.\host.json')      { $Path = '.' }
  else { throw "No -Path supplied and no LogicApp/host.json or ./host.json found. Pass -Path explicitly." }
}
$Path = (Resolve-Path $Path).Path

# ----- Locate catalogs -------------------------------------------------------
if (-not $CatalogsRoot) {
  $candidates = @(
    (Join-Path $PSScriptRoot ''),
    (Join-Path $Path '_connectors'),
    (Join-Path (Split-Path $Path -Parent) 'LogicApp_Template\_connectors'),
    (Join-Path $PSScriptRoot '..\..\_connectors')
  )
  foreach ($c in $candidates) {
    if ($c -and (Test-Path (Join-Path $c 'connectors.json'))) { $CatalogsRoot = (Resolve-Path $c).Path; break }
  }
  if (-not $CatalogsRoot) { throw "Cannot locate connectors.json. Pass -CatalogsRoot explicitly." }
}

function Load-Json($p) {
  if (-not (Test-Path $p)) { throw "Catalog not found: $p" }
  return Get-Content $p -Raw | ConvertFrom-Json
}

Write-Host "Loading catalogs from: $CatalogsRoot"
$connectors = Load-Json (Join-Path $CatalogsRoot 'connectors.json')
$runtime    = Load-Json (Join-Path $CatalogsRoot 'builtins-runtime.json')
$schema     = Load-Json (Join-Path $CatalogsRoot 'builtins-schema.json')
$overlay    = $null
$overlayPath = Join-Path $CatalogsRoot 'builtins-overlay.json'
if (Test-Path $overlayPath) { $overlay = Load-Json $overlayPath }

# ----- Build the union set of valid action / trigger types ------------------
$schemaActionTypes  = @($schema.actions.PSObject.Properties.Value  | ForEach-Object { $_.type })
$schemaTriggerTypes = @($schema.triggers.PSObject.Properties.Value | ForEach-Object { $_.type })

$runtimeTypes = @()
foreach ($prov in $runtime.connectionProviders.PSObject.Properties) {
  foreach ($op in $prov.Value.actions.PSObject.Properties)  { if ($op.Value.operationType) { $runtimeTypes += $op.Value.operationType } }
  foreach ($op in $prov.Value.triggers.PSObject.Properties) { if ($op.Value.operationType) { $runtimeTypes += $op.Value.operationType } }
}
$runtimeTypes = @($runtimeTypes | Sort-Object -Unique)

$serviceProviderTypes = @('ServiceProviderConnection','ServiceProvider','ApiConnection')
$validActionTypes  = @(@($schemaActionTypes  + $runtimeTypes + $serviceProviderTypes) | Sort-Object -Unique)
$validTriggerTypes = @(@($schemaTriggerTypes + $runtimeTypes + $serviceProviderTypes) | Sort-Object -Unique)

# Suggested replacements for banned hallucinations
$bannedReplacements = @{
  'Parallel'       = 'Use multiple actions sharing the same runAfter -- see the parallel shape pattern.'
  'TryCatch'       = 'Use two sibling Scopes -- see the exception-handler shape pattern.'
  'Sleep'          = 'Use the schema primitive Wait.'
  'Loop'           = 'Use Foreach (collection) or Until (condition).'
  'Variable'       = 'Use InitializeVariable, SetVariable, AppendToArrayVariable, etc.'
  'CSharpCode'     = 'Use CSharpScriptCode (bundle runtime).'
  'XsltTransform'  = 'Use Xslt (bundle runtime).'
}

# ----- Build service-provider operation lookup with param metadata ----------
# Key: "<spId>|<operationId>" -> @{ required = @(...); params = @(...) }
$spOps = @{}
foreach ($prov in $connectors.serviceProviders.PSObject.Properties) {
  $spId = $prov.Value.serviceProviderId
  foreach ($collection in 'actions','triggers') {
    if (-not $prov.Value.$collection) { continue }
    foreach ($op in $prov.Value.$collection.PSObject.Properties) {
      $entry = $op.Value
      $required = @(); if ($entry.required) { $required = @($entry.required) }
      $paramNames = @()
      if ($entry.parameters) { $paramNames = @($entry.parameters | ForEach-Object { $_.name }) }
      $spOps["$spId|$($op.Name)"] = @{ required = $required; params = $paramNames }
    }
  }
}

# ----- Lookup tables for HTTP auth, variable types -------------------------
$schemaHttpAuthTypes = @()
$httpAuth = $schema.actions.Http.inputs.authentication
if ($httpAuth -and $httpAuth.type -and $httpAuth.type.enum) {
  $schemaHttpAuthTypes = @($httpAuth.type.enum)
}
# Overlay extends Http.authentication with ManagedServiceIdentity (the only Standard-only auth)
if ($overlay -and $overlay.Http -and $overlay.Http.authentication -and $overlay.Http.authentication.type) {
  $schemaHttpAuthTypes += @($overlay.Http.authentication.type)
}
$validHttpAuthTypes = @($schemaHttpAuthTypes | Sort-Object -Unique)
if ($validHttpAuthTypes.Count -eq 0) {
  # Sensible defaults if the schema doesn't enumerate
  $validHttpAuthTypes = @('Basic','ClientCertificate','ActiveDirectoryOAuth','Raw','ManagedServiceIdentity')
}

$validVariableTypes = @('Array','Boolean','Float','Integer','Object','String')
$validRunAfter      = @('Succeeded','Failed','Skipped','TimedOut')

# ----- Load LogicApp-level config for cross-reference ----------------------
function Load-OptionalJson($p) { if (Test-Path $p) { return Get-Content $p -Raw | ConvertFrom-Json } else { return $null } }
$connsJson      = Load-OptionalJson (Join-Path $Path 'connections.json')
$paramsJson     = Load-OptionalJson (Join-Path $Path 'parameters.json')
$localSettings  = Load-OptionalJson (Join-Path $Path 'local.settings.json')

# Connection name -> serviceProviderId (for ServiceProvider actions)
$connNameToSpId = @{}
if ($connsJson -and $connsJson.serviceProviderConnections) {
  foreach ($p in $connsJson.serviceProviderConnections.PSObject.Properties) {
    $sp = $p.Value.serviceProvider.id
    if ($sp) { $connNameToSpId[$p.Name] = $sp }
  }
}

# Managed-API connection name -> api.id (for ApiConnection actions)
$managedApiConnNames = @{}
if ($connsJson -and $connsJson.managedApiConnections) {
  foreach ($p in $connsJson.managedApiConnections.PSObject.Properties) {
    $apiId = $null
    if ($p.Value.api -and $p.Value.api.id) { $apiId = $p.Value.api.id }
    $managedApiConnNames[$p.Name] = $apiId
  }
}

# ----- Walk workflows -------------------------------------------------------
Write-Host "Scanning workflows under: $Path"
$workflows = Get-ChildItem -Path $Path -Filter workflow.json -Recurse -File |
             Where-Object { $_.FullName -notmatch '\\_connectors\\' -and $_.FullName -notmatch '\\workflow-designtime\\' -and $_.FullName -notmatch '\\Artifacts\\' }
Write-Host "  Found $($workflows.Count) workflow.json file(s)"

$failures = New-Object System.Collections.ArrayList
$warnings = New-Object System.Collections.ArrayList
$typesSeen = @{}

function Add-Issue($list, $file, $path, $type, $problem, $hint) {
  [void]$list.Add([pscustomobject]@{ file=$file; path=$path; type=$type; problem=$problem; hint=$hint })
}

function Has-Property($obj, $name) {
  return ($obj -and $obj.PSObject.Properties.Name -contains $name)
}

function Walk-Actions($container, $pathHint, $filePath, $siblings) {
  if (-not $container) { return }
  $siblingNames = @($container.PSObject.Properties.Name)
  foreach ($prop in $container.PSObject.Properties) {
    $name  = $prop.Name
    $entry = $prop.Value
    if (-not $entry -or -not (Has-Property $entry 'type')) { continue }
    $aType = $entry.type
    if ($aType) { $script:typesSeen[$aType] = $true }
    $pathKey = "$pathHint.$name"

    # --- T1 Type-in-catalog ---
    if ($aType -and $aType -notin $script:validActionTypes) {
      $hint = $script:bannedReplacements[$aType]
      Add-Issue $script:failures $filePath $pathKey $aType "Action type '$aType' is not in any catalog." $hint
    }

    # --- T2 ServiceProvider deep checks ---
    if ($aType -in @('ServiceProviderConnection','ServiceProvider')) {
      $cfg = $entry.inputs.serviceProviderConfiguration
      $spId = $cfg.serviceProviderId
      $opId = $cfg.operationId
      $cnam = $cfg.connectionName
      if (-not $spId -or -not $opId) {
        Add-Issue $script:failures $filePath $pathKey $aType "ServiceProvider action missing serviceProviderId or operationId." $null
      } else {
        $key = "$spId|$opId"
        if (-not $script:spOps.ContainsKey($key)) {
          # Specific guidance for the well-known (Sftp, createFile) mistake
          $hint = $null
          if ($spId -eq '/serviceProviders/Sftp' -and $opId -eq 'createFile') {
            $hint = "Use operationId 'uploadFileContent' under /serviceProviders/Sftp (required params: filePath, overWriteFileIfExists). 'createFile' exists only on /serviceProviders/FileSystem and on the managed SFTP-SSH connector."
          }
          Add-Issue $script:failures $filePath $pathKey $aType "($spId, $opId) not in connectors.json (case-sensitive)." $hint
        } else {
          $meta = $script:spOps[$key]
          $supplied = @()
          if ($entry.inputs.parameters) { $supplied = @($entry.inputs.parameters.PSObject.Properties.Name) }
          # Required params present?
          foreach ($r in $meta.required) {
            if ($r -notin $supplied) {
              Add-Issue $script:failures $filePath "$pathKey.inputs.parameters" $aType "Missing required param '$r' for ($spId, $opId)." $null
            }
          }
          # Unknown params (warn unless -Strict)
          foreach ($s in $supplied) {
            if ($s -notin $meta.params) {
              $list = if ($script:Strict) { $script:failures } else { $script:warnings }
              $hint = if ($meta.params.Count -gt 0) { "Known params: $($meta.params -join ', ')" } else { $null }
              Add-Issue $list $filePath "$pathKey.inputs.parameters" $aType "Unknown param '$s' for ($spId, $opId)." $hint
            }
          }
        }
      }
      if ($cnam) {
        if (-not $script:connNameToSpId.ContainsKey($cnam)) {
          Add-Issue $script:warnings $filePath $pathKey $aType "connectionName '$cnam' not declared in connections.json." $null
        } elseif ($spId -and $script:connNameToSpId[$cnam] -ne $spId) {
          Add-Issue $script:failures $filePath $pathKey $aType "connectionName '$cnam' points to '$($script:connNameToSpId[$cnam])' but action uses '$spId'." $null
        }
      }
    }

    # --- T2b ApiConnection (managed connector) shallow check ---
    # No managed-API operation catalog is shipped, so we validate structural shape and
    # cross-reference the connection name against connections.json managedApiConnections.
    if ($aType -eq 'ApiConnection') {
      $refName = $null
      if ($entry.inputs -and $entry.inputs.host -and $entry.inputs.host.connection) {
        $refName = $entry.inputs.host.connection.referenceName
      }
      if (-not $refName) {
        Add-Issue $script:failures $filePath $pathKey $aType "ApiConnection action missing inputs.host.connection.referenceName." $null
      } elseif (-not $script:managedApiConnNames.ContainsKey($refName)) {
        Add-Issue $script:warnings $filePath $pathKey $aType "ApiConnection referenceName '$refName' not declared in connections.json (managedApiConnections)." $null
      }
      if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'method')) {
        Add-Issue $script:failures $filePath $pathKey $aType "ApiConnection action missing inputs.method." $null
      }
      if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'path')) {
        Add-Issue $script:failures $filePath $pathKey $aType "ApiConnection action missing inputs.path." $null
      }
    }

    # --- T3 Engine-primitive shape checks ---
    switch ($aType) {
      'Http' {
        if (-not $entry.inputs -or -not $entry.inputs.uri)    { Add-Issue $script:failures $filePath $pathKey $aType "Http action missing inputs.uri."    $null }
        if (-not $entry.inputs -or -not $entry.inputs.method) { Add-Issue $script:failures $filePath $pathKey $aType "Http action missing inputs.method." $null }
        if ($entry.inputs -and $entry.inputs.authentication -and $entry.inputs.authentication.type) {
          $at = $entry.inputs.authentication.type
          if ($at -notin $script:validHttpAuthTypes) {
            Add-Issue $script:failures $filePath $pathKey $aType "Unknown Http authentication.type '$at'." "Allowed: $($script:validHttpAuthTypes -join ', ')"
          }
        }
      }
      'If'       { if (-not $entry.expression) { Add-Issue $script:failures $filePath $pathKey $aType "If action missing 'expression'." $null }
                   if (-not $entry.actions -and -not ($entry.else -and $entry.else.actions)) {
                     Add-Issue $script:warnings $filePath $pathKey $aType "If action has no actions and no else.actions." $null } }
      'Switch'   { if (-not $entry.expression) { Add-Issue $script:failures $filePath $pathKey $aType "Switch action missing 'expression'." $null }
                   if (-not $entry.cases -and -not ($entry.default -and $entry.default.actions)) {
                     Add-Issue $script:warnings $filePath $pathKey $aType "Switch action has no cases and no default.actions." $null } }
      'Foreach'  { if (-not $entry.foreach)  { Add-Issue $script:failures $filePath $pathKey $aType "Foreach action missing 'foreach' iteration source." $null }
                   if (-not $entry.actions)  { Add-Issue $script:failures $filePath $pathKey $aType "Foreach action missing 'actions'." $null } }
      'Until'    { if (-not $entry.expression) { Add-Issue $script:failures $filePath $pathKey $aType "Until action missing 'expression'." $null }
                   if (-not $entry.limit)      { Add-Issue $script:failures $filePath $pathKey $aType "Until action missing 'limit'." $null }
                   if (-not $entry.actions)    { Add-Issue $script:failures $filePath $pathKey $aType "Until action missing 'actions'." $null } }
      'Scope'    { if (-not $entry.actions) { Add-Issue $script:warnings $filePath $pathKey $aType "Scope action has no inner actions." $null } }
      'InitializeVariable' {
        if ($entry.inputs -and $entry.inputs.variables) {
          foreach ($v in $entry.inputs.variables) {
            if ($v.type -and $v.type -notin $script:validVariableTypes) {
              Add-Issue $script:failures $filePath $pathKey $aType "Variable '$($v.name)' has invalid type '$($v.type)'." "Allowed: $($script:validVariableTypes -join ', ')"
            }
          }
        }
      }
      'SetVariable'             { if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'name'))  { Add-Issue $script:failures $filePath $pathKey $aType "SetVariable missing inputs.name."  $null }
                                  if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'value')) { Add-Issue $script:failures $filePath $pathKey $aType "SetVariable missing inputs.value." $null } }
      'IncrementVariable'       { if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'name'))  { Add-Issue $script:failures $filePath $pathKey $aType "IncrementVariable missing inputs.name." $null } }
      'DecrementVariable'       { if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'name'))  { Add-Issue $script:failures $filePath $pathKey $aType "DecrementVariable missing inputs.name." $null } }
      'AppendToArrayVariable'   { if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'name'))  { Add-Issue $script:failures $filePath $pathKey $aType "AppendToArrayVariable missing inputs.name."  $null }
                                  if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'value')) { Add-Issue $script:failures $filePath $pathKey $aType "AppendToArrayVariable missing inputs.value." $null } }
      'AppendToStringVariable'  { if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'name'))  { Add-Issue $script:failures $filePath $pathKey $aType "AppendToStringVariable missing inputs.name."  $null }
                                  if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'value')) { Add-Issue $script:failures $filePath $pathKey $aType "AppendToStringVariable missing inputs.value." $null } }
      'Compose'                 { if (-not (Has-Property $entry 'inputs')) { Add-Issue $script:failures $filePath $pathKey $aType "Compose action missing 'inputs'." $null } }
      'ParseJson'               { if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'content')) { Add-Issue $script:failures $filePath $pathKey $aType "ParseJson missing inputs.content." $null }
                                  if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'schema'))  { Add-Issue $script:failures $filePath $pathKey $aType "ParseJson missing inputs.schema."  "ParseJson requires a JSON Schema describing the expected content." } }
      'Response'                { if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'statusCode')) { Add-Issue $script:failures $filePath $pathKey $aType "Response action missing inputs.statusCode." $null } }
      'Wait'                    { if (-not $entry.inputs -or (-not (Has-Property $entry.inputs 'interval') -and -not (Has-Property $entry.inputs 'until'))) {
                                    Add-Issue $script:failures $filePath $pathKey $aType "Wait action requires inputs.interval or inputs.until." $null } }
      'Terminate'               { if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'runStatus')) { Add-Issue $script:failures $filePath $pathKey $aType "Terminate missing inputs.runStatus." "Allowed: Succeeded|Failed|Cancelled" }
                                  elseif ($entry.inputs.runStatus -notin @('Succeeded','Failed','Cancelled')) {
                                    Add-Issue $script:failures $filePath $pathKey $aType "Terminate inputs.runStatus '$($entry.inputs.runStatus)' is invalid." "Allowed: Succeeded|Failed|Cancelled" } }
      'Workflow'                { if (-not $entry.inputs -or -not $entry.inputs.host -or -not $entry.inputs.host.workflow -or -not $entry.inputs.host.workflow.id) {
                                    Add-Issue $script:failures $filePath $pathKey $aType "Workflow (child) action missing inputs.host.workflow.id." $null } }
      'Function'                { if (-not $entry.inputs -or -not $entry.inputs.function -or -not $entry.inputs.function.id) {
                                    Add-Issue $script:failures $filePath $pathKey $aType "Function action missing inputs.function.id." $null } }
      'Liquid'                  { if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'content')) { Add-Issue $script:failures $filePath $pathKey $aType "Liquid action missing inputs.content." $null }
                                  if (-not $entry.inputs -or -not $entry.inputs.map -or -not $entry.inputs.map.name) {
                                    Add-Issue $script:failures $filePath $pathKey $aType "Liquid action missing inputs.map.name." "In Standard, set inputs.map.source to 'LogicApp' (Artifacts) or 'IntegrationAccount'." } }
      'Xslt'                    { if (-not $entry.inputs -or -not (Has-Property $entry.inputs 'content')) { Add-Issue $script:failures $filePath $pathKey $aType "Xslt action missing inputs.content." $null }
                                  if (-not $entry.inputs -or -not $entry.inputs.map -or -not $entry.inputs.map.name) {
                                    Add-Issue $script:failures $filePath $pathKey $aType "Xslt action missing inputs.map.name." "In Standard, set inputs.map.source to 'LogicApp' (Artifacts) or 'IntegrationAccount'." } }
    }

    # --- T4 runAfter checks ---
    if ($entry.runAfter) {
      foreach ($predProp in $entry.runAfter.PSObject.Properties) {
        # Target exists as a sibling?
        if ($predProp.Name -notin $siblingNames) {
          Add-Issue $script:failures $filePath "$pathKey.runAfter" $aType "runAfter target '$($predProp.Name)' is not a sibling action in '$pathHint'." $null
        }
        # Status valid?
        foreach ($s in @($predProp.Value)) {
          if ($s -notin $script:validRunAfter -and $s -notin @('SUCCEEDED','FAILED','SKIPPED','TIMEDOUT')) {
            Add-Issue $script:failures $filePath "$pathKey.runAfter.$($predProp.Name)" $aType "runAfter status '$s' not in canonical set." "Allowed: Succeeded|Failed|Skipped|TimedOut (any case)"
          }
        }
      }
    }

    # --- Recurse ---
    if ($entry.actions)                       { Walk-Actions $entry.actions "$pathKey.actions" $filePath $entry.actions }
    if ($entry.else -and $entry.else.actions) { Walk-Actions $entry.else.actions "$pathKey.else.actions" $filePath $entry.else.actions }
    if ($entry.cases) {
      foreach ($caseProp in $entry.cases.PSObject.Properties) {
        if ($caseProp.Value.actions) { Walk-Actions $caseProp.Value.actions "$pathKey.cases.$($caseProp.Name).actions" $filePath $caseProp.Value.actions }
      }
    }
    if ($entry.default -and $entry.default.actions) { Walk-Actions $entry.default.actions "$pathKey.default.actions" $filePath $entry.default.actions }
  }
}

function Walk-Triggers($container, $filePath) {
  if (-not $container) { return }
  foreach ($prop in $container.PSObject.Properties) {
    $name  = $prop.Name
    $entry = $prop.Value
    if (-not $entry -or -not (Has-Property $entry 'type')) { continue }
    $tType = $entry.type
    if ($tType) { $script:typesSeen[$tType] = $true }
    $pathKey = "triggers.$name"
    if ($tType -and $tType -notin $script:validTriggerTypes) {
      Add-Issue $script:failures $filePath $pathKey $tType "Trigger type '$tType' is not in any catalog." $null
    }
    # ServiceProvider trigger deep check
    if ($tType -in @('ServiceProviderConnection','ServiceProvider')) {
      $cfg = $entry.inputs.serviceProviderConfiguration
      if ($cfg -and $cfg.serviceProviderId -and $cfg.operationId) {
        $key = "$($cfg.serviceProviderId)|$($cfg.operationId)"
        if (-not $script:spOps.ContainsKey($key)) {
          Add-Issue $script:failures $filePath $pathKey $tType "($($cfg.serviceProviderId), $($cfg.operationId)) not in connectors.json." $null
        }
      }
    }
  }
}

# ----- T5 + T6 helpers ------------------------------------------------------
function Check-Expressions($rawText, $filePath) {
  # Strip description strings -- they often quote the very pattern they warn against.
  $t = $rawText -replace '"description"\s*:\s*"(?:[^"\\]|\\.)*"', '"description":""'
  # Rule 1
  if ($t -match 'json\(\s*concat\([^)]*string\(') {
    Add-Issue $script:failures $filePath '(expression)' '' "json(concat(...string(...))) detected." "Build dynamic JSON via a literal object expression (@{...}) or addProperty() chain."
  }
  # Rule 2 -- action types called as inline functions inside @expressions
  foreach ($fn in 'select','query','compose','parse_json','parseJson','terminate','xslt') {
    # Only match function-call form when preceded by @, comma, ( or whitespace
    if ($t -match ("[@,(\s]" + [regex]::Escape($fn) + '\(')) {
      Add-Issue $script:failures $filePath '(expression)' '' "'$fn(' used inline but it is an action type." "Restrict @expressions to documented WDL functions; for projection use a Select action."
    }
  }
  # Rule 3 -- If/Until with bare comparator
  try {
    $j = $rawText | ConvertFrom-Json
    $stack = New-Object System.Collections.Stack
    if ($j.definition -and $j.definition.actions) { $stack.Push($j.definition.actions) }
    while ($stack.Count -gt 0) {
      $node = $stack.Pop()
      if ($null -eq $node) { continue }
      foreach ($prop in $node.PSObject.Properties) {
        $action = $prop.Value
        if (($action.type -eq 'If' -or $action.type -eq 'Until') -and $action.expression) {
          $keys = @($action.expression.PSObject.Properties.Name)
          if ($keys.Count -eq 1 -and $keys[0] -notin @('and','or','not')) {
            Add-Issue $script:failures $filePath "actions.$($prop.Name).expression" $action.type "Bare '$($keys[0])' expression at top level." "Wrap in and/or/not, even for a single clause."
          }
        }
        if ($action.actions) { $stack.Push($action.actions) }
        if ($action.else -and $action.else.actions) { $stack.Push($action.else.actions) }
        if ($action.cases) { foreach ($c in $action.cases.PSObject.Properties) { if ($c.Value.actions) { $stack.Push($c.Value.actions) } } }
        if ($action.default -and $action.default.actions) { $stack.Push($action.default.actions) }
      }
    }
  } catch { } # JSON parse failure already reported elsewhere
}

function Check-References($rawText, $filePath) {
  if ($script:CheckAppSettings -and $script:localSettings -and $script:localSettings.Values) {
    $keys = @($script:localSettings.Values.PSObject.Properties.Name)
    $hits = [regex]::Matches($rawText, "appsetting\('([^']+)'\)")
    foreach ($m in $hits) {
      $k = $m.Groups[1].Value
      if ($k -notin $keys) {
        Add-Issue $script:failures $filePath '(@appsetting)' '' "@appsetting('$k') not declared in local.settings.json." $null
      }
    }
  }
  if ($script:CheckParameters -and $script:paramsJson) {
    $keys = @($script:paramsJson.PSObject.Properties.Name)
    $hits = [regex]::Matches($rawText, "parameters\('([^']+)'\)")
    foreach ($m in $hits) {
      $k = $m.Groups[1].Value
      if ($k -notin $keys) {
        Add-Issue $script:failures $filePath '(@parameters)' '' "@parameters('$k') not declared in parameters.json." $null
      }
    }
  }
}

# ----- Main loop ------------------------------------------------------------
foreach ($wf in $workflows) {
  $rawText = $null
  try { $rawText = Get-Content $wf.FullName -Raw } catch {
    Add-Issue $failures $wf.FullName '(root)' '' "Cannot read file: $($_.Exception.Message)" $null
    continue
  }
  $j = $null
  try { $j = $rawText | ConvertFrom-Json } catch {
    Add-Issue $failures $wf.FullName '(root)' '' "JSON parse error: $($_.Exception.Message)" $null
    continue
  }
  $def = $j.definition
  if (-not $def) { continue }
  Walk-Triggers $def.triggers $wf.FullName
  Walk-Actions  $def.actions  'actions' $wf.FullName $def.actions
  Check-Expressions $rawText $wf.FullName
  Check-References  $rawText $wf.FullName
}

# ----- Report ---------------------------------------------------------------
Write-Host ""
Write-Host "Distinct action/trigger types observed: $($typesSeen.Count)"
$typesSeen.Keys | Sort-Object | ForEach-Object {
  $present = if ($_ -in $validActionTypes -or $_ -in $validTriggerTypes) { 'OK' } else { 'GAP' }
  Write-Host ("  [{0}] {1}" -f $present, $_)
}

if ($warnings.Count -gt 0) {
  Write-Host ""
  Write-Host "WARNINGS ($($warnings.Count)):" -ForegroundColor Yellow
  $warnings | Format-Table file, path, type, problem, hint -AutoSize -Wrap | Out-String -Width 220 | Write-Host
}

Write-Host ""
if ($failures.Count -eq 0) {
  Write-Host "PASS -- $($workflows.Count) workflow(s) validate against the catalogs." -ForegroundColor Green
  exit 0
} else {
  Write-Host "FAIL -- $($failures.Count) issue(s) found:" -ForegroundColor Red
  $failures | Format-Table file, path, type, problem, hint -AutoSize -Wrap | Out-String -Width 220 | Write-Host
  exit 1
}
