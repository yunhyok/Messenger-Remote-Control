param([string]$AssetDirectory = (Join-Path $PSScriptRoot '..\dist'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetDirectory = [IO.Path]::GetFullPath($AssetDirectory)
$masterProject = Join-Path $projectRoot 'src\RemoteMonitorMaster\RemoteMonitorMaster.csproj'
$slaveProject = Join-Path $projectRoot 'src\RemoteMonitorSlave\RemoteMonitorSlave.csproj'
$masterVersion = ([xml](Get-Content -LiteralPath $masterProject -Raw)).Project.PropertyGroup.Version
$version = ([xml](Get-Content -LiteralPath $slaveProject -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $masterVersion -cne $version) {
    throw "Master and Slave must have the same three-part version: $masterVersion / $version"
}

$expectedNames = @(
    "Messenger-Remote-Control-Master-Setup-$version.exe",
    "Messenger-Remote-Control-Slave-Setup-$version.exe",
    "Messenger-Remote-Control-v$version-win7-win11-net48.zip",
    "Messenger-Remote-Control-Slave-v$version-win11-net48.zip"
) | Sort-Object
$files = @{}
foreach ($name in $expectedNames) {
    $path = Join-Path $assetDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release asset: $path" }
    $files[$name] = Get-Item -LiteralPath $path
}

$sumsPath = Join-Path $assetDirectory 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) { throw "Missing checksum file: $sumsPath" }
$lines = @(Get-Content -LiteralPath $sumsPath | Where-Object { $_.Trim().Length -gt 0 })
if ($lines.Count -ne $expectedNames.Count) { throw 'SHA256SUMS.txt must contain exactly the four release assets.' }
$seen = @{}
foreach ($line in $lines) {
    if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64})  (?<name>[^\\/]+)$') { throw "Invalid checksum line: $line" }
    $name = $Matches.name
    $hash = $Matches.hash.ToUpperInvariant()
    if ($name -notin $expectedNames -or $seen.ContainsKey($name)) { throw "Unexpected or duplicate checksum entry: $name" }
    $actual = (Get-FileHash -LiteralPath $files[$name].FullName -Algorithm SHA256).Hash
    if ($hash -cne $actual) { throw "Checksum mismatch for $name. Expected $hash, got $actual." }
    $seen[$name] = $true
}

$masterInstaller = $files["Messenger-Remote-Control-Master-Setup-$version.exe"]
$slaveInstaller = $files["Messenger-Remote-Control-Slave-Setup-$version.exe"]
if ($masterInstaller.Length -le ($slaveInstaller.Length + 50MB)) {
    throw 'Master installer is too small to contain the offline .NET Framework 4.8 redistributable.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
function Test-ZipEntries {
    param([string]$Path, [string[]]$Expected)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $actual = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') } |
            ForEach-Object { $_.FullName.Replace('\', '/') } | Sort-Object)
    } finally {
        $archive.Dispose()
    }
    $expectedSorted = @($Expected | Sort-Object)
    if (($actual -join "`n") -cne ($expectedSorted -join "`n")) {
        throw "Unexpected ZIP contents in $Path.`nExpected:`n$($expectedSorted -join "`n")`nActual:`n$($actual -join "`n")"
    }
}

Test-ZipEntries -Path $files["Messenger-Remote-Control-Slave-v$version-win11-net48.zip"].FullName -Expected @(
    'RemoteMonitorSlave.exe',
    'RemoteMonitorSlave.exe.config',
    'README.md',
    'INSTALL.md',
    'HANDOFF.md',
    'SLAVE-TEST.md'
)
Test-ZipEntries -Path $files["Messenger-Remote-Control-v$version-win7-win11-net48.zip"].FullName -Expected @(
    'RemoteMonitorMaster.exe',
    'RemoteMonitorMaster.exe.config',
    'RemoteMonitorSlave.exe',
    'RemoteMonitorSlave.exe.config',
    'README.md',
    'INSTALL.md',
    'HANDOFF.md',
    'SLAVE-TEST.md',
    'WIN7-TEST.md'
)

Write-Host "Verified four release assets and SHA256SUMS.txt for v$version."
