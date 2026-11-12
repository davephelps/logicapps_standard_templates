<#
.SYNOPSIS
    Queries the local Logic Apps Standard designtime runtime and generates connectors.json and builtins-runtime.json.

.DESCRIPTION
    Start `func host start` in ../workflow-designtime/ (or pass -BaseUrl for an active host), then run this script.
    Emits UTF-8 (no BOM) ASCII-normalized JSON catalogs:
      - connectors.json       -- /serviceProviders/* actions, triggers, connectionParameters, and connectionParameterSets
      - builtins-runtime.json -- /connectionProviders/* bundle-added built-in operations
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:7071',
    [string]$ConnectorsOutFile = (Join-Path $PSScriptRoot 'connectors.json'),
    [string]$BuiltinsOutFile   = (Join-Path $PSScriptRoot 'builtins-runtime.json'),
    [string]$BundleVersion,
    [string[]]$IncludeProviders = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function ConvertTo-AsciiText {
    param([AllowNull()][string]$Text)
    if ($null -eq $Text) { return $null }
    $s = $Text
    $s = $s.Replace([string][char]0x2018, "'").Replace([string][char]0x2019, "'")
    $s = $s.Replace([string][char]0x201C, '"').Replace([string][char]0x201D, '"')
    $s = $s.Replace([string][char]0x2014, '--').Replace([string][char]0x2013, '-')
    $s = $s.Replace([string][char]0x2026, '...')
    $s = $s.Replace([string][char]0x00A0, ' ')
    $s = $s.Replace([string][char]0x2192, '->').Replace([string][char]0x2190, '<-')
    $s = $s.Replace([string][char]0x2022, '-')
    $s = $s.Replace([string][char]0x00D7, 'x')
    $s = $s.Replace([string][char]0x2264, '<=').Replace([string][char]0x2265, '>=')
    $sb = [System.Text.StringBuilder]::new($s.Length)
    foreach ($ch in $s.ToCharArray()) {
        $code = [int][char]$ch
        if ($code -le 127) {
            [void]$sb.Append($ch)
        }
    }
    return $sb.ToString()
}

function Resolve-BundleVersion {
    if (-not [string]::IsNullOrWhiteSpace($BundleVersion)) { return $BundleVersion }
    $bundleRoot = Join-Path $env:USERPROFILE '.azure-functions-core-tools\Functions\ExtensionBundles\Microsoft.Azure.Functions.ExtensionBundle.Workflows'
    if (Test-Path -LiteralPath $bundleRoot) {
        $latest = Get-ChildItem -LiteralPath $bundleRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            Select-Object -First 1
        if ($latest) { return $latest.Name }
    }
    return '1.194.31'
}

function Invoke-ManagementJson {
    param([Parameter(Mandatory)][string]$Path)
    $sep = if ($Path.Contains('?')) { '&' } else { '?' }
    $url = "$BaseUrl$Path${sep}api-version=2020-05-01-preview"
    return Invoke-RestMethod -Uri $url -TimeoutSec 30
}

function Convert-ConnectionParamMap {
    param($RawMap)
    $out = [ordered]@{}
    if (-not $RawMap) { return $out }
    foreach ($prop in $RawMap.PSObject.Properties) {
        $val = $prop.Value
        $item = [ordered]@{}
        if ($val.PSObject.Properties.Name -contains 'type' -and $val.type) {
            $item['type'] = [string]$val.type
        }
        $req = $false
        $propPath = @()
        $depParam = $null
        if ($val.PSObject.Properties.Name -contains 'uiDefinition' -and $val.uiDefinition -and
            $val.uiDefinition.PSObject.Properties.Name -contains 'constraints' -and $val.uiDefinition.constraints) {
            $c = $val.uiDefinition.constraints
            if ($c.PSObject.Properties.Name -contains 'required' -and [string]$c.required -eq 'true') {
                $req = $true
            }
            if ($c.PSObject.Properties.Name -contains 'propertyPath' -and $c.propertyPath) {
                $propPath = @($c.propertyPath | ForEach-Object { [string]$_ })
            }
            if ($c.PSObject.Properties.Name -contains 'dependentParameter' -and $c.dependentParameter) {
                $depParam = [ordered]@{
                    parameter = [string]$c.dependentParameter.parameter
                    value     = [string]$c.dependentParameter.value
                }
            }
        }
        $item['required'] = $req
        if ($propPath.Count -gt 0) { $item['propertyPath'] = $propPath }
        if ($null -ne $depParam)   { $item['dependentParameter'] = $depParam }
        $out[$prop.Name] = $item
    }
    return $out
}

function Get-OperationEntry {
    param([string]$GroupName, [string]$OpName)
    try {
        $detail = Invoke-ManagementJson "/runtime/webhooks/workflow/api/management/operationGroups/$GroupName/operations/$OpName`?`$expand=properties/manifest"
    }
    catch {
        Write-Warning "    skip $OpName : $($_.Exception.Message)"
        return $null
    }
    if (-not ($detail.properties.PSObject.Properties.Name -contains 'manifest') -or -not $detail.properties.manifest) {
        return $null
    }
    $m = $detail.properties.manifest

    $params = @()
    $required = @()
    if ($m.PSObject.Properties.Name -contains 'inputs' -and $m.inputs) {
        if ($m.inputs.PSObject.Properties.Name -contains 'properties' -and $m.inputs.properties) {
            foreach ($prop in $m.inputs.properties.PSObject.Properties) {
                $p = [ordered]@{ name = [string]$prop.Name }
                if ($prop.Value.PSObject.Properties.Name -contains 'type' -and $prop.Value.type) {
                    $p['type'] = [string]$prop.Value.type
                }
                if ($prop.Value.PSObject.Properties.Name -contains 'enum' -and $prop.Value.enum) {
                    $p['enum'] = @($prop.Value.enum | ForEach-Object { ConvertTo-AsciiText ([string]$_) })
                }
                if ($prop.Value.PSObject.Properties.Name -contains 'description' -and $prop.Value.description) {
                    $p['description'] = ConvertTo-AsciiText ([string]$prop.Value.description)
                }
                $params += $p
            }
        }
        if ($m.inputs.PSObject.Properties.Name -contains 'required' -and $m.inputs.required) {
            $required = @($m.inputs.required | ForEach-Object { [string]$_ })
        }
    }

    $summaryText = if ($detail.properties.PSObject.Properties.Name -contains 'summary') { ConvertTo-AsciiText ([string]$detail.properties.summary) } else { '' }
    $opType      = if ($detail.properties.PSObject.Properties.Name -contains 'operationType') { [string]$detail.properties.operationType } else { '' }

    $entry = [ordered]@{
        summary       = $summaryText
        operationType = $opType
        parameters    = $params
        required      = $required
    }
    return @{ entry = $entry; detail = $detail }
}

$resolvedVersion = Resolve-BundleVersion
Write-Host "Fetching operationGroups (bundleVersion=$resolvedVersion)..."
$groups = @((Invoke-ManagementJson '/runtime/webhooks/workflow/api/management/operationGroups').value)
Write-Host "  Found $($groups.Count) groups"

$connectors = [ordered]@{
    bundleVersion    = $resolvedVersion
    generatedUtc     = (Get-Date).ToUniversalTime().ToString('o')
    serviceProviders = [ordered]@{}
}

$builtins = [ordered]@{
    bundleVersion       = $resolvedVersion
    generatedUtc        = (Get-Date).ToUniversalTime().ToString('o')
    '$comment'          = "Bundle-added built-in operations exposed under /connectionProviders/*. Each operation's 'operationType' field is the canonical 'type' string for workflow.json. Workflow-engine primitives (Http, If, Switch, Foreach, Compose, ParseJson, Scope, etc.) are NOT here -- see builtins-schema.json."
    connectionProviders = [ordered]@{}
}

foreach ($g in ($groups | Sort-Object name)) {
    $gid   = [string]$g.id
    $gname = [string]$g.name
    if ($IncludeProviders.Count -gt 0 -and $gname -notin $IncludeProviders) { continue }

    $gidNorm = if ($gid.StartsWith('/')) { $gid } else { "/$gid" }
    $isServiceProvider    = $gidNorm -like '/serviceProviders/*'
    $isConnectionProvider = $gidNorm -like '/connectionProviders/*'
    if (-not ($isServiceProvider -or $isConnectionProvider)) { continue }

    Write-Host "  Group: $gname ($gidNorm)"
    $ops = @((Invoke-ManagementJson "/runtime/webhooks/workflow/api/management/operationGroups/$gname/operations").value)

    $actions     = [ordered]@{}
    $triggers    = [ordered]@{}
    $firstDetail = $null

    foreach ($op in $ops) {
        $r = Get-OperationEntry -GroupName $gname -OpName ([string]$op.name)
        if ($null -eq $r) { continue }
        if ($null -eq $firstDetail) { $firstDetail = $r.detail }

        $hasTriggerProp = ($r.detail.properties.PSObject.Properties.Name -contains 'trigger' -and [bool]$r.detail.properties.trigger)
        $isTrigger = ($r.detail.properties.operationType -eq 'ServiceProviderNotification' `
                      -or $hasTriggerProp `
                      -or ([string]$op.name) -match '^(when|on)[A-Z]')
        if ($isTrigger) { $triggers[[string]$op.name] = $r.entry } else { $actions[[string]$op.name] = $r.entry }
    }

    if ($isServiceProvider) {
        $connKey   = $null
        $connParams = [ordered]@{}
        $connSets   = [ordered]@{}
        if ($firstDetail -and
            $firstDetail.properties.manifest.PSObject.Properties.Name -contains 'connector' -and
            $firstDetail.properties.manifest.connector -and
            $firstDetail.properties.manifest.connector.PSObject.Properties.Name -contains 'properties' -and
            $firstDetail.properties.manifest.connector.properties) {
            $cp = $firstDetail.properties.manifest.connector.properties
            if ($cp.PSObject.Properties.Name -contains 'connectionParameters' -and $cp.connectionParameters) {
                $connParams = Convert-ConnectionParamMap -RawMap $cp.connectionParameters
            }
            if ($cp.PSObject.Properties.Name -contains 'connectionParameterSets' -and $cp.connectionParameterSets -and
                $cp.connectionParameterSets.PSObject.Properties.Name -contains 'values' -and $cp.connectionParameterSets.values) {
                foreach ($setEntry in @($cp.connectionParameterSets.values)) {
                    $setName = [string]$setEntry.name
                    $setParams = if ($setEntry.PSObject.Properties.Name -contains 'parameters') {
                        Convert-ConnectionParamMap -RawMap $setEntry.parameters
                    } else { [ordered]@{} }
                    $connSets[$setName] = [ordered]@{
                        parameters = $setParams
                    }
                    if ($setName -eq 'connectionString' -and $setParams.Contains('connectionString')) {
                        $connKey = "${gname}_connectionString"
                    }
                }
            }
        }
        $connectors.serviceProviders[$gname] = [ordered]@{
            serviceProviderId       = $gidNorm
            connectionStringSetting = $connKey
            connectionParameters    = $connParams
            connectionParameterSets = $connSets
            actions                 = $actions
            triggers                = $triggers
        }
    }
    else {
        $builtins.connectionProviders[$gname] = [ordered]@{
            connectionProviderId = $gidNorm
            actions              = $actions
            triggers             = $triggers
        }
    }
}

$connectorsJson = (ConvertTo-AsciiText ($connectors | ConvertTo-Json -Depth 14)) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($ConnectorsOutFile, $connectorsJson + "`n", $utf8NoBom)
Write-Host "Wrote $ConnectorsOutFile ($((Get-Item -LiteralPath $ConnectorsOutFile).Length) bytes, $($connectors.serviceProviders.Count) providers)"

$builtinsJson = (ConvertTo-AsciiText ($builtins | ConvertTo-Json -Depth 14)) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($BuiltinsOutFile, $builtinsJson + "`n", $utf8NoBom)
Write-Host "Wrote $BuiltinsOutFile ($((Get-Item -LiteralPath $BuiltinsOutFile).Length) bytes, $($builtins.connectionProviders.Count) providers)"
