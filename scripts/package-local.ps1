param (
    [ValidateSet("Portable", "Installed", "All")]
    [string]$Mode = "All",

    [string]$Configuration = "Release",

    [string]$Version = "dev-local",

    [switch]$SelfContained = $false,
    [string]$OutputDirectory = "artifacts/local-package"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'ripgrep-bundle.ps1')
. (Join-Path $PSScriptRoot 'release-package-contract.ps1')

$rootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactsDir = Join-Path $rootDir "artifacts"
if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $packageRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    $packageRoot = [System.IO.Path]::GetFullPath((Join-Path $rootDir $OutputDirectory))
}
$artifactsPrefix = [System.IO.Path]::GetFullPath($artifactsDir).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $packageRoot.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Package output must stay under the repository artifacts directory: $packageRoot"
}
$tempRoot = Join-Path $packageRoot "temp"
$outputRoot = Join-Path $packageRoot "out"
$midPlayerBuildRoot = Join-Path $tempRoot "midplayer-build-output"
$midfdBuildRoot = Join-Path $tempRoot "midfd-build-output"
$csprojPath = Join-Path $rootDir "MidFD.csproj"

$modes = if ($Mode -eq "All") { @("Portable", "Installed") } else { @($Mode) }

if (Test-Path $packageRoot) { throw "Package output already exists; preserving it and stopping: $packageRoot" }
$null = New-Item -ItemType Directory -Path $tempRoot
$null = New-Item -ItemType Directory -Path $outputRoot

function Copy-PackageDocs {
    param([string]$PublishDir)

    foreach ($doc in @("README.md", "CHANGELOG.md", "LICENSE")) {
        $source = Join-Path $rootDir $doc
        if (Test-Path $source) {
            Copy-Item $source -Destination (Join-Path $PublishDir $doc) -Force
        }
    }

    $userDocsSource = Join-Path $rootDir "UserDocs"
    if (Test-Path $userDocsSource) {
        Copy-Item $userDocsSource -Destination (Join-Path $PublishDir "UserDocs") -Recurse -Force
    }

    $readmeFirstSource = Join-Path $rootDir "packaging\runtime-guidance\README_FIRST.txt"
    if (Test-Path $readmeFirstSource) {
        Copy-Item $readmeFirstSource -Destination $PublishDir -Force
    }
}

foreach ($currentMode in $modes) {
    $modeLower = $currentMode.ToLowerInvariant()
    $publishDir = Join-Path $tempRoot $modeLower
    $zipPath = Join-Path $outputRoot "MidFD-win-x64-$modeLower.zip"

    $selfContainedValue = if ($SelfContained) { "true" } else { "false" }

    dotnet msbuild $csprojPath `
        -t:Restore `
        -m:1 `
        -p:RestoreProjectReferences=false `
        -p:PlatformTarget=x64 `
        -p:RuntimeIdentifier=win-x64 `
        "-p:MidPlayerBuildRoot=$midPlayerBuildRoot" `
        "-p:MidFDBuildRoot=$midfdBuildRoot" `
        -p:NuGetAudit=false

    if ($LASTEXITCODE -ne 0) {
        throw "Restore failed for $currentMode package."
    }

    dotnet msbuild (Join-Path $rootDir "src\MidPlayer\MidPlayer.Lite.csproj") `
        -t:Restore `
        -m:1 `
        -p:RestoreProjectReferences=false `
        -p:PlatformTarget=x64 `
        -p:RuntimeIdentifier=win-x64 `
        "-p:MidPlayerBuildRoot=$midPlayerBuildRoot" `
        -p:NuGetAudit=false

    if ($LASTEXITCODE -ne 0) {
        throw "Lite viewer restore failed for $currentMode package."
    }


    dotnet publish $csprojPath `
        -m:1 `
        -c $Configuration `
        -r win-x64 `
        --self-contained $selfContainedValue `
        --no-restore `
        -o $publishDir `
        -p:PlatformTarget=x64 `
        "-p:MidPlayerBuildRoot=$midPlayerBuildRoot" `
        "-p:MidFDBuildRoot=$midfdBuildRoot" `
        /p:InformationalVersion=$Version

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $currentMode package."
    }


    Get-ChildItem $publishDir -Filter *.pdb -Recurse | Remove-Item -Force
    Copy-PackageDocs -PublishDir $publishDir
    Assert-PackageContents -PackageRoot $publishDir

    $profileBootstrapPath = Join-Path $publishDir "storage-profile.json"
    if ($currentMode -eq "Installed") {
        Set-Content -Path $profileBootstrapPath -Value '{ "profile": "installed" }' -Encoding UTF8
        Set-Content -Path (Join-Path $publishDir "README_INSTALLED_PROFILE.txt") `
            -Value "This local package opts in to the Installed storage profile. Runtime settings are stored under %LOCALAPPDATA%\MidFD. To inspect diagnostics, run MidFD.exe --storage-profile-diagnostics-file .\storage-profile-diagnostics.txt." `
            -Encoding UTF8
    } elseif (Test-Path $profileBootstrapPath) {
        Remove-Item $profileBootstrapPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

    $verifyRoot = Join-Path $tempRoot "verify-$modeLower"
    Expand-Archive -Path $zipPath -DestinationPath $verifyRoot
    Assert-RipgrepBundle -PackageRoot $verifyRoot
    Assert-PackageContents -PackageRoot $verifyRoot

    if (-not (Test-Path (Join-Path $verifyRoot "MidFD.exe"))) {
        throw "Package verification failed for ${currentMode}: MidFD.exe is missing."
    }
    foreach ($hostRelativePath in @("SusieHost32\SusieHost32.exe", "SusieHost64\SusieHost64.exe")) {
        if (-not (Test-Path (Join-Path $verifyRoot $hostRelativePath))) {
            throw "Package verification failed for ${currentMode}: $hostRelativePath is missing."
        }
    }

    $hasBootstrap = Test-Path (Join-Path $verifyRoot "storage-profile.json")
    if ($currentMode -eq "Portable" -and $hasBootstrap) {
        throw "Portable package must not include storage-profile.json."
    }

    if ($currentMode -eq "Installed" -and -not $hasBootstrap) {
        throw "Installed package must include storage-profile.json."
    }

    Write-Host "Created local $currentMode package: $zipPath"
}

Write-Host "Local package generation completed. Output: $outputRoot"
