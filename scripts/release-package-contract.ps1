function Get-PackageRelativePath([string]$packageRoot, [string]$fullPath) {
    return [System.IO.Path]::GetRelativePath($packageRoot, $fullPath).Replace([System.IO.Path]::DirectorySeparatorChar, '/')
}

function Test-ForbiddenPackagePath([string]$relativePath) {
    $forbiddenSegments = @('.codex', '.git', '.vs', '.vscode', 'artifacts', 'Poc', 'scratch', 'scripts', 'src', 'tests')
    $segments = $relativePath -split '[\\/]'
    foreach ($segment in $segments) {
        if ($forbiddenSegments -contains $segment) { return $true }
    }
    return $false
}

function Test-ForbiddenPackageFile([string]$relativePath) {
    $forbiddenExtensions = @('.pdb', '.cs', '.c', '.h', '.def', '.csproj', '.sln', '.props', '.targets', '.ps1')
    if ($forbiddenExtensions -contains [System.IO.Path]::GetExtension($relativePath)) { return $true }
    $forbiddenRuntimeFiles = @('coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll')
    return $forbiddenRuntimeFiles -contains [System.IO.Path]::GetFileName($relativePath)
}

function Get-PackagePeMachine([string]$filePath) {
    $stream = [System.IO.File]::OpenRead($filePath)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64) { throw 'file is shorter than a DOS header' }
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or ([long]$peOffset + 6) -gt $stream.Length) {
            throw 'PE header offset is outside the file'
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'PE signature is invalid' }
        return [int]$reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
    }
}

function Assert-PackageContents([string]$packageRoot) {
    if (-not (Test-Path $packageRoot -PathType Container)) {
        throw "Package gate failed: package root is missing: $packageRoot"
    }

    $requiredFiles = @(
        'MidFD.exe',
        'MidFD.dll',
        'MidFD.deps.json',
        'MidFD.runtimeconfig.json',
        'MidPlayer.Lite.dll',
        'MidFD.FileOperationHelper.exe',
        'MidFD.FileOperationHelper.dll',
        'MidFD.FileOperationHelper.deps.json',
        'MidFD.FileOperationHelper.runtimeconfig.json',
        'SusieHost32\SusieHost32.exe',
        'SusieHost32\SusieHost32.dll',
        'SusieHost32\SusieHost32.deps.json',
        'SusieHost32\SusieHost32.runtimeconfig.json',
        'SusieHost64\SusieHost64.exe',
        'SusieHost64\SusieHost64.dll',
        'SusieHost64\SusieHost64.deps.json',
        'SusieHost64\SusieHost64.runtimeconfig.json',
        'README_FIRST.txt',
        'README.md',
        'CHANGELOG.md',
        'LICENSE'
    )
    $requiredDirectories = @('UserDocs')
    $requiredUserDocs = @('BUILD.md', 'KEYBINDINGS.md', 'PROFILES.md', 'SUPPORT.md', 'USER_GUIDE.md')
    $missing = [System.Collections.Generic.List[string]]::new()

    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path (Join-Path $packageRoot $relativePath) -PathType Leaf)) {
            $missing.Add($relativePath)
        }
    }
    foreach ($relativePath in $requiredDirectories) {
        if (-not (Test-Path (Join-Path $packageRoot $relativePath) -PathType Container)) {
            $missing.Add($relativePath + '/')
        }
    }
    foreach ($relativePath in $requiredUserDocs) {
        $userDocPath = Join-Path $packageRoot (Join-Path 'UserDocs' $relativePath)
        if (-not (Test-Path $userDocPath -PathType Leaf)) {
            $missing.Add((Join-Path 'UserDocs' $relativePath).Replace([System.IO.Path]::DirectorySeparatorChar, '/'))
        }
    }

    $dependencyFiles = @(Get-ChildItem $packageRoot -Recurse -Force -Filter '*.deps.json' -File)
    $dependencyText = ($dependencyFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    $standardForbiddenFiles = @(Get-ChildItem $packageRoot -Recurse -Force -File | Where-Object {
        $_.Name -match '^(MidPlayer\.Full(\.|$)|MidPlayer\.(exe|dll|deps\.json|runtimeconfig\.json)$|LibVLCSharp(\.|$)|VideoLAN\.LibVLC(\.|$)|SkiaSharp(\.|$)|libSkiaSharp(\.|$))'
    })
    $standardForbiddenDirectories = @(Get-ChildItem $packageRoot -Recurse -Force -Directory | Where-Object {
        $_.Name -ieq 'libvlc'
    })
    if ($standardForbiddenFiles.Count -gt 0 -or $standardForbiddenDirectories.Count -gt 0 -or $dependencyText -match '(?i)LibVLCSharp|VideoLAN\.LibVLC|SkiaSharp') {
        $missing.Add('standard package must not contain LibVLC or SkiaSharp artifacts/references')
    }
    $expectedMachines = [ordered]@{
        'MidFD.exe' = 0x8664
        'MidFD.FileOperationHelper.exe' = 0x8664
        'SusieHost32/SusieHost32.exe' = 0x014c
        'SusieHost64/SusieHost64.exe' = 0x8664
    }
    $architectureFailures = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in $expectedMachines.Keys) {
        $normalizedPath = $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $executablePath = Join-Path $packageRoot $normalizedPath
        if (-not (Test-Path $executablePath -PathType Leaf)) { continue }
        try {
            $actualMachine = Get-PackagePeMachine $executablePath
            if ($actualMachine -ne $expectedMachines[$relativePath]) {
                $architectureFailures.Add("$relativePath has PE machine 0x$('{0:X4}' -f $actualMachine); expected 0x$('{0:X4}' -f $expectedMachines[$relativePath])")
            }
        }
        catch {
            $architectureFailures.Add("$relativePath is not a valid PE executable")
        }
    }

    $forbidden = Get-ChildItem $packageRoot -Recurse -Force -File | ForEach-Object {
        $relativePath = Get-PackageRelativePath $packageRoot $_.FullName
        if (Test-ForbiddenPackageFile $relativePath -or (Test-ForbiddenPackagePath $relativePath)) { $relativePath }
    }
    $forbiddenDirectories = Get-ChildItem $packageRoot -Recurse -Force -Directory | ForEach-Object {
        $relativePath = Get-PackageRelativePath $packageRoot $_.FullName
        if (Test-ForbiddenPackagePath $relativePath) { $relativePath }
    }
    if ($missing.Count -gt 0 -or $architectureFailures.Count -gt 0 -or @($forbidden).Count -gt 0 -or @($forbiddenDirectories).Count -gt 0) {
        $details = @()
        if ($missing.Count -gt 0) { $details += "missing: $($missing -join ', ')" }
        if ($architectureFailures.Count -gt 0) { $details += "invalid executable architecture: $($architectureFailures -join ', ')" }
        if (@($forbidden).Count -gt 0) { $details += "forbidden: $($forbidden -join ', ')" }
        if (@($forbiddenDirectories).Count -gt 0) { $details += "forbidden directories: $($forbiddenDirectories -join ', ')" }
        throw "Package gate failed: $($details -join '; ')"
    }
}
