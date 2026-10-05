function Assert-RipgrepBundle {
    param([Parameter(Mandatory)][string]$PackageRoot)
    $trusted = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../third_party/ripgrep/manifest.json') -Raw | ConvertFrom-Json
    $manifestPath = Join-Path $PackageRoot 'licenses/ripgrep/manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'ripgrep manifest missing' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.version -ne $trusted.version -or $manifest.executableSha256 -ne $trusted.executableSha256 -or $manifest.architecture -ne 'win-x64') { throw 'ripgrep manifest mismatch' }
    $executable = Join-Path $PackageRoot 'rg.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'ripgrep executable missing' }
    if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $trusted.executableSha256) { throw 'ripgrep executable SHA-256 mismatch' }
    $version = & $executable --no-config --version
    if ($LASTEXITCODE -ne 0 -or $version[0] -notmatch ("^ripgrep " + [regex]::Escape($trusted.version) + "(?: |$)")) { throw 'ripgrep version mismatch' }
    foreach ($license in @('LICENSE-MIT','UNLICENSE','COPYING')) {
        $path = Join-Path $PackageRoot "licenses/ripgrep/$license"
        $source = Join-Path $PSScriptRoot "../third_party/ripgrep/$license"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path).Hash -ne (Get-FileHash -LiteralPath $source).Hash) { throw "ripgrep license missing or mismatched: $license" }
    }
    Write-Host "ripgrep bundle verified: $($trusted.version) $($trusted.executableSha256)"
}
