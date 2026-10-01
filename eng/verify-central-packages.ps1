param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string[]]$BuildProperties = @(),
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

function Assert-PackageSource {
    param([xml]$Document, [string]$Path, [bool]$IsCentral)

    foreach ($reference in $Document.SelectNodes("//*[local-name()='PackageReference']")) {
        if ($reference.HasAttribute('Version') -or $reference.HasAttribute('VersionOverride') -or
            $reference.SelectSingleNode("./*[local-name()='Version' or local-name()='VersionOverride']")) {
            throw "${Path}: PackageReference versions must be managed centrally."
        }
    }

    if ($Document.SelectSingleNode("//*[local-name()='GlobalPackageReference']")) {
        throw "${Path}: GlobalPackageReference would broaden project dependencies."
    }

    if (-not $IsCentral) {
        if ([IO.Path]::GetFileName($Path) -eq 'Directory.Packages.props') {
            throw "${Path}: Nested central package files are not allowed."
        }
        if ($Document.SelectSingleNode("//*[local-name()='ItemGroup']/*[local-name()='PackageVersion']")) {
            throw "${Path}: PackageVersion items belong in the root Directory.Packages.props."
        }
        if ($Document.SelectSingleNode("//*[local-name()='ManagePackageVersionsCentrally' or local-name()='CentralPackageVersionOverrideEnabled' or local-name()='CentralPackageTransitivePinningEnabled' or local-name()='CentralPackageFloatingVersionsEnabled']")) {
            throw "${Path}: Central package policies belong in the root Directory.Packages.props."
        }
    }

    foreach ($version in $Document.SelectNodes("//*[local-name()='ItemGroup']/*[local-name()='PackageVersion']")) {
        if ($version.HasAttribute('Update') -or $version.SelectSingleNode('ancestor-or-self::*[@Condition]')) {
            throw "${Path}: Central package versions must be unconditional Include items."
        }
        $value = $version.GetAttribute('Version')
        if (-not $value) {
            $metadata = $version.SelectSingleNode("./*[local-name()='Version']")
            if ($metadata) {
                if ($metadata.HasAttribute('Condition')) {
                    throw "${Path}: Central package versions must be unconditional Include items."
                }
                $value = $metadata.InnerText
            }
        }
        if ($value.Contains('*')) {
            throw "${Path}: Floating package versions are not allowed."
        }
    }
}

function Assert-PackageEvaluation {
    param($Evaluation, [string]$Path)

    $properties = $Evaluation.Properties
    if ($properties.ManagePackageVersionsCentrally -ne 'true' -or
        $properties.CentralPackageVersionOverrideEnabled -ne 'false' -or
        $properties.CentralPackageTransitivePinningEnabled -ne 'false') {
        throw "${Path}: Evaluated central package policies do not match repository policy."
    }

    if ($properties.CentralPackageFloatingVersionsEnabled -ne 'false') {
        throw "${Path}: Floating package versions must be disabled."
    }

    $versions = @{}
    foreach ($version in $Evaluation.Items.PackageVersion) {
        if ($versions.ContainsKey($version.Identity) -or -not $version.Version) {
            throw "${Path}: Duplicate or missing central version for $($version.Identity)."
        }
        if ($version.Version.Contains('*')) {
            throw "${Path}: Unexpected evaluated floating version for $($version.Identity)."
        }
        $versions[$version.Identity] = $version.Version
    }

    foreach ($reference in $Evaluation.Items.PackageReference) {
        if ($reference.IsImplicitlyDefined -eq 'true') { continue }
        if ($reference.Version -or $reference.VersionOverride) {
            throw "${Path}: Evaluated inline version for $($reference.Identity)."
        }
        if (-not $versions.ContainsKey($reference.Identity)) {
            throw "${Path}: Missing central version for $($reference.Identity)."
        }
    }
}

function Assert-Rejected {
    param([scriptblock]$Check)

    $rejected = $false
    try { & $Check } catch { $rejected = $true }
    if (-not $rejected) { throw 'A negative central package policy test unexpectedly passed.' }
}

if ($SelfTest) {
    Assert-PackageSource ([xml]'<Project><ItemGroup><PackageReference Include="Example" PrivateAssets="all" /></ItemGroup><PropertyGroup><PackageVersion>1.0.0</PackageVersion></PropertyGroup></Project>') 'Example.csproj' $false
    Assert-PackageSource ([xml]'<Project><ItemGroup><PackageVersion Include="Example" Version="1.0.0" /></ItemGroup></Project>') 'Directory.Packages.props' $true
    foreach ($metadata in @('Version', 'VersionOverride')) {
        Assert-Rejected { Assert-PackageSource ([xml]"<Project><ItemGroup><PackageReference Include='Example' $metadata='1.0.0' /></ItemGroup></Project>") 'Example.csproj' $false }
        Assert-Rejected { Assert-PackageSource ([xml]"<Project xmlns='http://schemas.microsoft.com/developer/msbuild/2003'><ItemGroup><PackageReference Include='Example'><$metadata>1.0.0</$metadata></PackageReference></ItemGroup></Project>") 'Example.csproj' $false }
    }
    Assert-Rejected { Assert-PackageSource ([xml]'<Project />') 'nested/Directory.Packages.props' $false }
    Assert-Rejected { Assert-PackageSource ([xml]'<Project><ItemGroup><PackageVersion Include="Example" Version="1.0.0" /></ItemGroup></Project>') 'Example.targets' $false }
    Assert-Rejected { Assert-PackageSource ([xml]'<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>') 'Example.csproj' $false }
    Assert-Rejected { Assert-PackageSource ([xml]'<Project><ItemGroup><GlobalPackageReference Include="Example" Version="1.0.0" /></ItemGroup></Project>') 'Directory.Packages.props' $true }
    Assert-Rejected { Assert-PackageSource ([xml]'<Project><ItemGroup><PackageVersion Include="Example" Version="1.*" /></ItemGroup></Project>') 'Directory.Packages.props' $true }
    Assert-Rejected { Assert-PackageSource ([xml]'<Project><ItemGroup><PackageVersion Include="Microsoft.SemanticKernel" Version="1.*" /></ItemGroup></Project>') 'Directory.Packages.props' $true }
    Assert-Rejected { Assert-PackageSource ([xml]'<Project><ItemGroup><PackageVersion Update="Example" Version="1.0.0" /></ItemGroup></Project>') 'Directory.Packages.props' $true }
    Assert-Rejected { Assert-PackageSource ([xml]'<Project><ItemGroup Condition="true"><PackageVersion Include="Example" Version="1.0.0" /></ItemGroup></Project>') 'Directory.Packages.props' $true }
    Assert-Rejected { Assert-PackageSource ([xml]'<Project><ItemGroup><PackageVersion Include="Example" Version="1.0.0" Condition="true" /></ItemGroup></Project>') 'Directory.Packages.props' $true }
    Assert-Rejected { Assert-PackageSource ([xml]'<Project><ItemGroup><PackageVersion Include="Example"><Version Condition="true">1.0.0</Version></PackageVersion></ItemGroup></Project>') 'Directory.Packages.props' $true }

    $evaluation = [pscustomobject]@{
        Properties = [pscustomobject]@{
            MSBuildProjectName = 'Example'
            ManagePackageVersionsCentrally = 'true'
            CentralPackageVersionOverrideEnabled = 'false'
            CentralPackageTransitivePinningEnabled = 'false'
            CentralPackageFloatingVersionsEnabled = 'false'
        }
        Items = [pscustomobject]@{
            PackageVersion = @([pscustomobject]@{ Identity = 'Example'; Version = '1.0.0' })
            PackageReference = @([pscustomobject]@{ Identity = 'Example' }, [pscustomobject]@{ Identity = 'SdkImplicit'; IsImplicitlyDefined = 'true'; Version = '1.0.0' })
        }
    }
    Assert-PackageEvaluation $evaluation 'Example.csproj'
    $evaluation.Items.PackageVersion += $evaluation.Items.PackageVersion[0]
    Assert-Rejected { Assert-PackageEvaluation $evaluation 'Example.csproj' }
    $evaluation.Items.PackageVersion = @()
    Assert-Rejected { Assert-PackageEvaluation $evaluation 'Example.csproj' }
    $evaluation.Items.PackageVersion = @([pscustomobject]@{ Identity = 'Example'; Version = '1.*' })
    Assert-Rejected { Assert-PackageEvaluation $evaluation 'Example.csproj' }
    $evaluation.Properties.CentralPackageFloatingVersionsEnabled = 'true'
    Assert-Rejected { Assert-PackageEvaluation $evaluation 'Example.csproj' }
    $evaluation.Properties.MSBuildProjectName = 'OpenClaw.SemanticKernelAdapter'
    $evaluation.Items.PackageVersion = @([pscustomobject]@{ Identity = 'Microsoft.SemanticKernel'; Version = '1.*' })
    $evaluation.Items.PackageReference = @([pscustomobject]@{ Identity = 'Microsoft.SemanticKernel' })
    Assert-Rejected { Assert-PackageEvaluation $evaluation 'OpenClaw.SemanticKernelAdapter.csproj' }
    $evaluation.Properties.CentralPackageFloatingVersionsEnabled = 'false'
    Assert-Rejected { Assert-PackageEvaluation $evaluation 'OpenClaw.SemanticKernelAdapter.csproj' }
    $evaluation.Items.PackageVersion = @([pscustomobject]@{ Identity = 'Microsoft.SemanticKernel'; Version = '1.80.1' })
    Assert-PackageEvaluation $evaluation 'OpenClaw.SemanticKernelAdapter.csproj'
    Write-Output 'Central package policy self-tests passed.'
    return
}

$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $trackedFiles = git ls-files -z --cached --others --exclude-standard -- '*.csproj' '*.props' '*.targets'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot discover repository MSBuild files.' }
    $files = @(($trackedFiles -join "`n") -split '[\x00\r\n]+' |
        Where-Object { $_ -and $_ -notmatch '(^|/)(bin|obj|node_modules)/' } | Sort-Object -Unique)
    if ($files -notcontains 'Directory.Packages.props') { throw 'Root Directory.Packages.props is missing.' }

    foreach ($path in $files) {
        Assert-PackageSource ([xml](Get-Content -LiteralPath $path -Raw)) $path ($path -eq 'Directory.Packages.props')
    }

    $arguments = @("-p:Configuration=$Configuration")
    foreach ($property in $BuildProperties) {
        if ($property -notmatch '^[A-Za-z_][A-Za-z0-9_]*=.+$') { throw "Invalid MSBuild property: $property" }
        $arguments += "-p:$property"
    }
    $projects = @($files | Where-Object { $_.EndsWith('.csproj') })
    foreach ($path in $projects) {
        $output = dotnet msbuild $path -nologo @arguments -getProperty:MSBuildProjectName,ManagePackageVersionsCentrally,CentralPackageVersionOverrideEnabled,CentralPackageTransitivePinningEnabled,CentralPackageFloatingVersionsEnabled -getItem:PackageReference,PackageVersion
        if ($LASTEXITCODE -ne 0) { throw "${Path}: MSBuild evaluation failed." }
        Assert-PackageEvaluation (($output -join "`n") | ConvertFrom-Json) $path
    }
    Write-Output "Central package policy verified for $($projects.Count) projects ($Configuration)."
} finally {
    Pop-Location
}