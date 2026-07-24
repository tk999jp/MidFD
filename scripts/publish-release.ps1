param (
    [Parameter(Mandatory=$true)]
    [string]$ReleaseTag,

    [switch]$AllowDirty = $false,

    [switch]$SkipTagCheck = $false,

    [switch]$SelfContained = $false,

    [string]$ArtifactsRoot = ""
)

# 1. Validation
if ($ReleaseTag -notmatch '^v\d{4}\.\d{2}\.\d{2}(\.\d+)?$') {
    Write-Error "ReleaseTag must follow the format 'vYYYY.MM.DD' or 'vYYYY.MM.DD.N' (e.g., v2026.05.20, v2026.05.24.1)."
    exit 1
}

if ($SelfContained) {
    Write-Error "-SelfContained is not supported: framework-dependent release packages only."
    exit 1
}

# 2. Git status verification
if (-not $AllowDirty) {
    $dirty = git status --porcelain
    if ($dirty) {
        Write-Error "Working tree is dirty. Commit all changes or use -AllowDirty to bypass."
        exit 1
    }
}

if (-not $SkipTagCheck) {
    # Check if tag exists
    $tagExists = git tag -l $ReleaseTag
    if (-not $tagExists) {
        Write-Error "Git tag '$ReleaseTag' does not exist. Create the tag first or use -SkipTagCheck."
        exit 1
    }
    # Check if tag matches HEAD
    $tagCommit = (git rev-list -n 1 $ReleaseTag)
    if ($null -ne $tagCommit) { $tagCommit = $tagCommit.Trim() }
    $headCommit = (git rev-parse HEAD).Trim()
    if ($tagCommit -ne $headCommit) {
        Write-Error "Git tag '$ReleaseTag' commit ($tagCommit) does not match HEAD ($headCommit). Use -SkipTagCheck to bypass."
        exit 1
    }
}

# 3. Calculate version strings
$parts = $ReleaseTag.Substring(1).Split('.')
$year = [int]$parts[0]
$month = [int]$parts[1]
$day = [int]$parts[2]
$revision = if ($parts.Length -ge 4) { [int]$parts[3] } else { 0 }

$Version = "$year.$month.$day.$revision"
$AssemblyVersion = "$year.$month.$day.$revision"
$FileVersion = "$year.$month.$day.$revision"
$InformationalVersion = $ReleaseTag

Write-Host "--- Version Configurations ---"
Write-Host "ReleaseTag:           $ReleaseTag"
Write-Host "Version:              $Version"
Write-Host "AssemblyVersion:      $AssemblyVersion"
Write-Host "FileVersion:          $FileVersion"
Write-Host "InformationalVersion: $InformationalVersion"
Write-Host "-----------------------------"

Write-Host "--- Publish Settings ---"
Write-Host "Runtime:              win-x64"
Write-Host "Deployment:           framework-dependent"
Write-Host "------------------------"

# 4. Resolve directories
$rootDir = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifactsDir = if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    Join-Path $rootDir "artifacts"
} elseif ([System.IO.Path]::IsPathRooted($ArtifactsRoot)) {
    [System.IO.Path]::GetFullPath($ArtifactsRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $rootDir $ArtifactsRoot))
}
$releaseDir = Join-Path $artifactsDir "release"
$releaseTestDir = Join-Path $artifactsDir "release-test"
$tempPublishDir = Join-Path $artifactsDir "temp-publish"

# Clean up stale directories
Write-Host "Cleaning up old/stale directories..."
if (Test-Path $releaseDir) { Remove-Item -Recurse -Force $releaseDir }
if (Test-Path $releaseTestDir) { Remove-Item -Recurse -Force $releaseTestDir }
if (Test-Path $tempPublishDir) { Remove-Item -Recurse -Force $tempPublishDir }

$null = New-Item -ItemType Directory -Path $releaseDir -Force
$null = New-Item -ItemType Directory -Path $tempPublishDir -Force

# 5. Run dotnet publish
Write-Host "Running dotnet publish..."
$csprojPath = Join-Path $rootDir "MidFD.csproj"
dotnet publish $csprojPath `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o $tempPublishDir `
  /p:Version=$Version `
  /p:AssemblyVersion=$AssemblyVersion `
  /p:FileVersion=$FileVersion `
  /p:InformationalVersion=$InformationalVersion

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed."
    exit 1
}

# Remove PDB files to keep release package clean
Write-Host "Removing PDB files from publish output..."
Get-ChildItem $tempPublishDir -Filter *.pdb -Recurse | Remove-Item -Force


# 5.5 Include public documentation and license in package
$docsToCopy = @("README.md", "CHANGELOG.md", "LICENSE")
foreach ($doc in $docsToCopy) {
    $source = Join-Path $rootDir $doc
    if (Test-Path $source) {
        Copy-Item $source -Destination (Join-Path $tempPublishDir $doc) -Force
    }
}

$userDocsSource = Join-Path $rootDir "UserDocs"
$userDocsDest = Join-Path $tempPublishDir "UserDocs"
if (Test-Path $userDocsSource) {
    Remove-Item $userDocsDest -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item $userDocsSource -Destination $userDocsDest -Recurse -Force
}

# 5.6 Copy runtime guidance files
$readmeFirstSource = Join-Path $rootDir "packaging\runtime-guidance\README_FIRST.txt"
if (Test-Path $readmeFirstSource) {
    Copy-Item $readmeFirstSource -Destination $tempPublishDir -Force
}

function Get-PackageRelativePath([string]$packageRoot, [string]$fullPath) {
    return [System.IO.Path]::GetRelativePath($packageRoot, $fullPath).Replace([System.IO.Path]::DirectorySeparatorChar, '/')
}

function Test-ForbiddenPackagePath([string]$relativePath) {
    $forbiddenSegments = @('.codex', 'tests', 'artifacts', 'scratch')
    $segments = $relativePath -split '[\\/]'
    foreach ($segment in $segments) {
        if ($forbiddenSegments -contains $segment) { return $true }
    }
    return $false
}

function Assert-PackageContents([string]$packageRoot) {
    $requiredRootFiles = @(
        "MidFD.exe",
        "MidFD.FileOperationHelper.exe",
        "MidFD.FileOperationHelper.dll",
        "MidFD.FileOperationHelper.deps.json",
        "MidFD.FileOperationHelper.runtimeconfig.json",
        "README_FIRST.txt",
        "README.md",
        "CHANGELOG.md",
        "LICENSE"
    )
    $requiredUserDocs = @("BUILD.md", "KEYBINDINGS.md", "PROFILES.md", "SUPPORT.md", "USER_GUIDE.md")
    $missing = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in $requiredRootFiles) {
        if (-not (Test-Path (Join-Path $packageRoot $relativePath) -PathType Leaf)) { $missing.Add($relativePath) }
    }
    if (-not (Test-Path (Join-Path $packageRoot "UserDocs") -PathType Container)) { $missing.Add("UserDocs/") }
    foreach ($relativePath in $requiredUserDocs) {
        if (-not (Test-Path (Join-Path $packageRoot "UserDocs\$relativePath") -PathType Leaf)) { $missing.Add("UserDocs\$relativePath") }
    }

    $forbidden = Get-ChildItem $packageRoot -Recurse -Force -File | ForEach-Object {
        $relativePath = Get-PackageRelativePath $packageRoot $_.FullName
        if ($_.Extension -ieq ".pdb" -or (Test-ForbiddenPackagePath $relativePath)) { $relativePath }
    }
    $forbiddenDirectories = Get-ChildItem $packageRoot -Recurse -Force -Directory | ForEach-Object {
        $relativePath = Get-PackageRelativePath $packageRoot $_.FullName
        if (Test-ForbiddenPackagePath $relativePath) { $relativePath }
    }
    if ($missing.Count -gt 0 -or @($forbidden).Count -gt 0 -or @($forbiddenDirectories).Count -gt 0) {
        $details = @()
        if ($missing.Count -gt 0) { $details += "missing: $($missing -join ', ')" }
        if (@($forbidden).Count -gt 0) { $details += "forbidden: $($forbidden -join ', ')" }
        if (@($forbiddenDirectories).Count -gt 0) { $details += "forbidden directories: $($forbiddenDirectories -join ', ')" }
        throw "Package gate failed: $($details -join '; ')"
    }
}


# 6. Create ZIP archive
$zipPath = Join-Path $releaseDir "MidFD-win-x64.zip"
Write-Host "Creating ZIP archive at: $zipPath"
Compress-Archive -Path "$tempPublishDir\*" -DestinationPath $zipPath -Force

if (-not (Test-Path $zipPath)) {
    Write-Error "Failed to create ZIP archive."
    exit 1
}

# 7. Extract archive for verification
$extractedDest = Join-Path $releaseTestDir "MidFD-win-x64"
Write-Host "Extracting ZIP for verification to: $extractedDest"
$null = New-Item -ItemType Directory -Path $releaseTestDir -Force
Expand-Archive -Path $zipPath -DestinationPath $extractedDest -Force

# 7.1 Verify complete package contents and helper runtime dependencies
Assert-PackageContents $extractedDest

# Verify exe exists
$exePath = Join-Path $extractedDest "MidFD.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "Verification failed: MidFD.exe is missing from the extracted ZIP."
    exit 1
}

# 8. Verify version metadata
Write-Host "Verifying ProductVersion in MidFD.exe..."
$fileVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
$productVersion = $fileVersionInfo.ProductVersion
Write-Host "Found ProductVersion: $productVersion"

# Check tag inclusion
if ($productVersion -notmatch [regex]::Escape($ReleaseTag)) {
    Write-Error "Verification failed: ProductVersion does not contain the ReleaseTag '$ReleaseTag'."
    exit 1
}

# Check commit hash inclusion
$shortHash = (git rev-parse --short HEAD).Trim()
$fullHash = (git rev-parse HEAD).Trim()

if ($productVersion -notmatch $shortHash -and $productVersion -notmatch $fullHash) {
    Write-Error "Verification failed: ProductVersion does not contain the git commit hash ($shortHash or $fullHash)."
    exit 1
}

Write-Host "ProductVersion verification passed successfully!"

# 8.5. Verify absence of self-contained runtime files (for framework-dependent deployment)
Write-Host "Verifying absence of self-contained runtime files..."
$forbiddenFiles = @("coreclr.dll", "hostfxr.dll", "System.Private.CoreLib.dll")
foreach ($file in $forbiddenFiles) {
    $filePath = Join-Path $extractedDest $file
    if (Test-Path $filePath) {
        Write-Error "Verification failed: '$file' was found in framework-dependent deployment."
        exit 1
    }
}
Write-Host "Runtime files absence verification passed!"

# 9. Generate SHA256 file
$sha256Path = Join-Path $releaseDir "MidFD-win-x64.zip.sha256"
Write-Host "Generating SHA256 checksum at: $sha256Path"
$fileHash = Get-FileHash -Path $zipPath -Algorithm SHA256
$fileHash.Hash.ToLower() | Out-File -FilePath $sha256Path -Encoding ascii -NoNewline

# 10. Clean up temporary directory
Write-Host "Cleaning up temporary publish directory..."
Remove-Item -Recurse -Force $tempPublishDir

Write-Host "Release packaging completed successfully!"
