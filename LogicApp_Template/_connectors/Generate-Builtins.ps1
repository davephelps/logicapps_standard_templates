<#
.SYNOPSIS
    Fetches Microsoft's published Logic Apps workflow definition schema and emits builtins-schema.json.

.DESCRIPTION
    Writes UTF-8 (no BOM) ASCII-normalized JSON to LogicApp_Template/_connectors/builtins-schema.json.
#>
[CmdletBinding()]
param(
    [string]$SchemaUrl = 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json',
    [string]$OutFile   = (Join-Path $PSScriptRoot 'builtins-schema.json')
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
    $sb = [System.Text.StringBuilder]::new($s.Length)
    foreach ($ch in $s.ToCharArray()) {
        if ([int][char]$ch -le 127) { [void]$sb.Append($ch) }
    }
    return $sb.ToString()
}

Write-Host "Fetching schema: $SchemaUrl"
$schema = Invoke-RestMethod -Uri $SchemaUrl -TimeoutSec 30

function Get-OneOfEntries {
    param($Container)
    foreach ($a in @($Container.allOf)) {
        if ($a.PSObject.Properties.Name -contains 'oneOf') { return $a.oneOf }
    }
    return $null
}

function Get-InputProperties {
    param($Entry)
    if (-not ($Entry.properties.PSObject.Properties.Name -contains 'inputs') -or -not $Entry.properties.inputs) {
        return @()
    }
    $inputs = $Entry.properties.inputs
    $names = @()
    if ($inputs.PSObject.Properties.Name -contains 'properties' -and $inputs.properties) {
        foreach ($p in $inputs.properties.PSObject.Properties) { $names += [string]$p.Name }
    }
    if ($inputs.PSObject.Properties.Name -contains 'allOf' -and $inputs.allOf) {
        foreach ($a in @($inputs.allOf)) {
            if ($a.PSObject.Properties.Name -contains 'properties' -and $a.properties) {
                foreach ($p in $a.properties.PSObject.Properties) { $names += [string]$p.Name }
            }
        }
    }
    return @($names | Sort-Object -Unique)
}

function Build-Catalog {
    param($Container, [string]$KindLabel)
    $oneOf = Get-OneOfEntries -Container $Container
    if (-not $oneOf) { throw "Could not locate oneOf for $KindLabel" }
    $catalog = [ordered]@{}
    $idx = 0
    foreach ($entry in @($oneOf)) {
        $idx++
        if (-not ($entry.PSObject.Properties.Name -contains 'properties') -or -not $entry.properties -or
            -not ($entry.properties.PSObject.Properties.Name -contains 'type') -or -not $entry.properties.type -or
            -not ($entry.properties.type.PSObject.Properties.Name -contains 'enum') -or -not $entry.properties.type.enum) {
            continue
        }
        foreach ($typeName in @($entry.properties.type.enum)) {
            $kindEnum = @()
            if ($entry.properties.PSObject.Properties.Name -contains 'kind' -and $entry.properties.kind -and
                $entry.properties.kind.PSObject.Properties.Name -contains 'enum' -and $entry.properties.kind.enum) {
                $kindEnum = @($entry.properties.kind.enum | ForEach-Object { [string]$_ })
            }
            $inputProps = Get-InputProperties -Entry $entry
            $key = if ($kindEnum.Count -gt 0 -and $catalog.Contains([string]$typeName)) {
                "$typeName($($kindEnum -join ','))"
            } else {
                [string]$typeName
            }
            $catalog[$key] = [ordered]@{
                type            = [string]$typeName
                kind            = $kindEnum
                inputProperties = $inputProps
                '$source'       = "schema $KindLabel oneOf #$idx"
            }
        }
    }
    return $catalog
}

Write-Host 'Extracting action types...'
$actions = Build-Catalog -Container $schema.properties.actions.additionalProperties -KindLabel 'actions'

Write-Host 'Extracting trigger types...'
$triggers = Build-Catalog -Container $schema.properties.triggers.additionalProperties -KindLabel 'triggers'

$actionMasterEnum = @()
foreach ($a in @($schema.properties.actions.additionalProperties.allOf)) {
    if ($a.PSObject.Properties.Name -contains 'properties' -and $a.properties -and
        $a.properties.PSObject.Properties.Name -contains 'type' -and $a.properties.type -and
        $a.properties.type.PSObject.Properties.Name -contains 'enum' -and $a.properties.type.enum) {
        $actionMasterEnum = @($a.properties.type.enum | ForEach-Object { [string]$_ })
        break
    }
}

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

$jsonText = (ConvertTo-AsciiText ($out | ConvertTo-Json -Depth 12)) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($OutFile, $jsonText + "`n", $utf8NoBom)
$size = (Get-Item -LiteralPath $OutFile).Length
Write-Host "Wrote $OutFile ($size bytes)"
if ($missingActions.Count -gt 0) {
    Write-Warning "Action types still missing: $($missingActions -join ', ')"
    exit 1
}
