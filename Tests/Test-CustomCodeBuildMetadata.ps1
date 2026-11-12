<#
.SYNOPSIS
    Four-path build verification for CustomCode/CustomCode.csproj metadata and assemblies.

.DESCRIPTION
    Copies CustomCode/ and minimal LogicApp_Template/ + unit-test ProjectReference inputs into a
    disposable working directory and runs four consecutive dotnet operations:
      1. clean-build            -- dotnet build CustomCode.csproj
      2. incremental-build      -- second dotnet build CustomCode.csproj
      3. explicit-publish       -- dotnet publish CustomCode.csproj (reproduces MSB4006 without _IsPublishing guard)
      4. project-reference-build -- dotnet build on a test project referencing CustomCode.csproj

    After EVERY step, asserts:
      - exit code is 0
      - LogicApp_Template/lib/custom/CustomCode/function.json exists and is valid parseable JSON
        with ScriptFile = CustomCode.dll, Language = net8, and a workflowActionTrigger binding
      - LogicApp_Template/lib/custom/net8/ contains CustomCode.dll and required non-empty SDK assemblies

.PARAMETER WorkDirectory
    Disposable directory to create for this verification run. If omitted, creates a temporary directory
    and cleans it up on pass (retaining it on failure for inspection).

.PARAMETER TemplateRoot
    Root of logicapps_standard_templates. Defaults to parent of Tests/.
#>
[CmdletBinding()]
param(
    [string]$WorkDirectory,
    [string]$TemplateRoot = (Split-Path $PSScriptRoot -Parent),
    [switch]$KeepWorkDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$sourceRoot = (Resolve-Path -LiteralPath $TemplateRoot).Path

$autoTemp = $false
if ([string]::IsNullOrWhiteSpace($WorkDirectory)) {
    $WorkDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("customcode-verify-" + [guid]::NewGuid().ToString('N'))
    $autoTemp = $true
}
$work = [System.IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) {
    throw "WorkDirectory must not already exist: $work"
}

foreach ($req in @('CustomCode\CustomCode.csproj', 'CustomCode\CustomCode.cs', 'LogicApp_Template\host.json')) {
    $full = Join-Path $sourceRoot $req
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Missing required template input: $full"
    }
}

$appRoot    = Join-Path $work 'repo'
$logsDir    = Join-Path $work 'logs'
$scratchDir = Join-Path $work 'sdk-scratch'
foreach ($dir in @(
    (Join-Path $appRoot 'CustomCode'),
    (Join-Path $appRoot 'LogicApp_Template'),
    (Join-Path $appRoot 'Tests\UnitProjectRef'),
    $logsDir,
    $scratchDir
)) {
    [System.IO.Directory]::CreateDirectory($dir) | Out-Null
}

Copy-Item -LiteralPath (Join-Path $sourceRoot 'CustomCode\CustomCode.csproj'), (Join-Path $sourceRoot 'CustomCode\CustomCode.cs') `
    -Destination (Join-Path $appRoot 'CustomCode')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'LogicApp_Template\host.json') `
    -Destination (Join-Path $appRoot 'LogicApp_Template\host.json')

$unitRefCsproj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\CustomCode\CustomCode.csproj" />
  </ItemGroup>
</Project>
'@
[System.IO.File]::WriteAllText((Join-Path $appRoot 'Tests\UnitProjectRef\UnitProjectRef.csproj'), $unitRefCsproj, $utf8NoBom)
[System.IO.File]::WriteAllText((Join-Path $appRoot 'Tests\UnitProjectRef\RefCheck.cs'), "namespace UnitProjectRef; public sealed class RefCheck { public string TypeName => typeof(Contoso.Integration.CustomCode).FullName ?? string.Empty; }`n", $utf8NoBom)

$customProj  = Join-Path $appRoot 'CustomCode\CustomCode.csproj'
$unitRefProj = Join-Path $appRoot 'Tests\UnitProjectRef\UnitProjectRef.csproj'

$operations = @(
    [pscustomobject]@{ Name = 'clean-build';             Verb = 'build';   Project = $customProj  }
    [pscustomobject]@{ Name = 'incremental-build';       Verb = 'build';   Project = $customProj  }
    [pscustomobject]@{ Name = 'explicit-publish';        Verb = 'publish'; Project = $customProj  }
    [pscustomobject]@{ Name = 'project-reference-build'; Verb = 'build';   Project = $unitRefProj }
)

$requiredAssemblies = @(
    'CustomCode.dll',
    'Microsoft.Azure.Functions.Extensions.Workflows.Abstractions.dll',
    'Microsoft.Azure.Functions.Extensions.Workflows.Sdk.dll',
    'Microsoft.Azure.Functions.Worker.Extensions.Abstractions.dll'
)

$metadataPath = Join-Path $appRoot 'LogicApp_Template\lib\custom\CustomCode\function.json'
$net8Dir      = Join-Path $appRoot 'LogicApp_Template\lib\custom\net8'
$results      = @()

$oldTemp = $env:TEMP
$oldTmp  = $env:TMP
try {
    $env:TEMP = $scratchDir
    $env:TMP  = $scratchDir

    foreach ($op in $operations) {
        $logPath = Join-Path $logsDir "$($op.Name).log"
        $args = @($op.Verb, $op.Project, '--configuration', 'Debug', '-nologo')
        $outputLines = & dotnet @args 2>&1
        $exitCode = $LASTEXITCODE
        [System.IO.File]::WriteAllLines($logPath, @($outputLines | ForEach-Object { $_.ToString() }), $utf8NoBom)

        $metaValid = $false
        $metaError = $null
        if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
            try {
                $meta = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
                $bindings = @($meta.bindings)
                if ($bindings.Count -eq 0 -and $meta.PSObject.Properties.Name -contains 'Bindings') {
                    $bindings = @($meta.Bindings)
                }
                $hasWorkflowBinding = @($bindings | Where-Object {
                    $t = if ($_.PSObject.Properties.Name -contains 'type') { $_.type } elseif ($_.PSObject.Properties.Name -contains 'Type') { $_.Type } else { '' }
                    $t -eq 'workflowActionTrigger'
                }).Count -eq 1
                $scriptFile = if ($meta.PSObject.Properties.Name -contains 'scriptFile') { $meta.scriptFile } else { $meta.ScriptFile }
                $lang       = if ($meta.PSObject.Properties.Name -contains 'language') { $meta.language } else { $meta.Language }
                $metaValid  = ($scriptFile -eq 'CustomCode.dll' -and $lang -eq 'net8' -and $hasWorkflowBinding)
            }
            catch {
                $metaError = $_.Exception.Message
            }
        }

        $allDlls = @(if (Test-Path -LiteralPath $net8Dir) {
            Get-ChildItem -LiteralPath $net8Dir -Filter '*.dll' -File | Where-Object { $_.Length -gt 0 }
        })
        $missingRequired = @($requiredAssemblies | Where-Object {
            $p = Join-Path $net8Dir $_
            -not (Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -le 0
        })

        $passed = ($exitCode -eq 0) -and $metaValid -and ($allDlls.Count -ge $requiredAssemblies.Count) -and ($missingRequired.Count -eq 0)
        $row = [pscustomobject]@{
            Operation          = $op.Name
            ExitCode           = $exitCode
            MetadataValid      = $metaValid
            MetadataParseError = $metaError
            NonEmptyDllCount   = $allDlls.Count
            MissingAssemblies  = $missingRequired
            Pass               = $passed
            Log                = $logPath
        }
        $results += $row
        $tag = if ($passed) { '[OK]' } else { '[FAIL]' }
        Write-Host ("{0} {1}: exit={2} metadataValid={3} nonEmptyDlls={4}" -f $tag, $op.Name, $exitCode, $metaValid, $allDlls.Count)
    }

    [System.IO.File]::WriteAllText((Join-Path $work 'results.json'), ($results | ConvertTo-Json -Depth 8), $utf8NoBom)
}
finally {
    $env:TEMP = $oldTemp
    $env:TMP  = $oldTmp
}

$failed = @($results | Where-Object { -not $_.Pass })
if ($failed.Count -gt 0) {
    Write-Host "[FAIL] $($failed.Count) CustomCode build path(s) failed. Logs retained at: $work" -ForegroundColor Red
    exit 1
}

if ($autoTemp -and -not $KeepWorkDirectory) {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host '[OK] All 4 CustomCode build paths passed with valid function.json metadata and non-empty assemblies.' -ForegroundColor Green
exit 0
