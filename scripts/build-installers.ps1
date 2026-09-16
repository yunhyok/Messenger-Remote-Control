param([switch]$SkipReleaseChecksums, [switch]$DependenciesOnly)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$masterProject = Join-Path $projectRoot 'src\RemoteMonitorMaster\RemoteMonitorMaster.csproj'
$slaveProject = Join-Path $projectRoot 'src\RemoteMonitorSlave\RemoteMonitorSlave.csproj'
$masterVersion = ([xml](Get-Content -LiteralPath $masterProject -Raw)).Project.PropertyGroup.Version
$version = ([xml](Get-Content -LiteralPath $slaveProject -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $masterVersion -cne $version) {
    throw "Master and Slave must have the same three-part version: $masterVersion / $version"
}

$cache = Join-Path $projectRoot '.cache\installer-dependencies'
$dist = Join-Path $projectRoot 'dist'
$iss = Join-Path $projectRoot 'installer\MessengerRemoteControl.iss'
$innoVersion = '7.1.0'
$innoInstallerName = "innosetup-$innoVersion-x64.exe"
$innoInstaller = Join-Path $cache $innoInstallerName
$innoUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe'
$innoSha256 = '0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F'
$dotNetName = 'NDP48-x86-x64-AllOS-ENU.exe'
$dotNetInstaller = Join-Path $cache $dotNetName
$dotNetUrl = 'https://download.microsoft.com/download/f/3/a/f3a6af84-da23-40a5-8d1c-49cc10c8e76f/NDP48-x86-x64-AllOS-ENU.exe'
$dotNetSha256 = '0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40'

function Test-AuthenticodeFile {
    param([string]$Path, [string]$Sha256, [string]$Publisher)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -cne $Sha256) { throw "SHA-256 mismatch for $Path. Expected $Sha256, got $actual." }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch [regex]::Escape($Publisher)) {
        throw "Invalid or unexpected Authenticode signature for $Path. Expected publisher: $Publisher."
    }
    return $true
}

function Get-VerifiedDownload {
    param([string]$Uri, [string]$Path, [string]$Sha256, [string]$Publisher)
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        try {
            if (Test-AuthenticodeFile -Path $Path -Sha256 $Sha256 -Publisher $Publisher) { return }
        } catch {
            Write-Warning $_.Exception.Message
            Remove-Item -LiteralPath $Path -Force
        }
    }
    $partial = $Path + '.download'
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $partial
        Test-AuthenticodeFile -Path $partial -Sha256 $Sha256 -Publisher $Publisher | Out-Null
        Move-Item -LiteralPath $partial -Destination $Path -Force
    } finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}

function Test-InnoCompiler {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $banner = (& $Path '/?' 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    return ($exitCode -eq 0 -and $banner -match '(?m)^Inno Setup 7 Command-Line Compiler\s*$')
}

function Find-InstalledInnoCompiler {
    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($entry in Get-ItemProperty $roots -ErrorAction SilentlyContinue) {
        if ($entry.PSObject.Properties['DisplayName'] -and $entry.PSObject.Properties['DisplayVersion'] -and
            $entry.PSObject.Properties['InstallLocation'] -and $entry.DisplayName -like 'Inno Setup 7*' -and
            $entry.DisplayVersion -ceq $innoVersion -and $entry.InstallLocation) {
            $candidate = Join-Path $entry.InstallLocation 'ISCC.exe'
            if (Test-InnoCompiler -Path $candidate) { return $candidate }
        }
    }
    return $null
}

$iscc = Find-InstalledInnoCompiler
if (-not $iscc) {
    Get-VerifiedDownload -Uri $innoUrl -Path $innoInstaller -Sha256 $innoSha256 -Publisher 'Pyrsys B.V.'
    $innoHome = Join-Path $cache "inno-setup-$innoVersion"
    $iscc = Join-Path $innoHome 'ISCC.exe'
    $marker = Join-Path $innoHome '.verified-installer-sha256'
    $ready = (Test-InnoCompiler -Path $iscc) -and (Test-Path -LiteralPath $marker) -and
        ((Get-Content -LiteralPath $marker -Raw).Trim() -ceq $innoSha256)
    if (-not $ready) {
        New-Item -ItemType Directory -Path $innoHome -Force | Out-Null
        $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /PORTABLE=1 /DIR=`"$innoHome`""
        $process = Start-Process -FilePath $innoInstaller -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
        if ($process.ExitCode -ne 0 -or -not (Test-InnoCompiler -Path $iscc)) {
            throw "Inno Setup $innoVersion portable installation failed with exit code $($process.ExitCode)."
        }
        Set-Content -LiteralPath $marker -Value $innoSha256 -Encoding ASCII
    }
}

Get-VerifiedDownload -Uri $dotNetUrl -Path $dotNetInstaller -Sha256 $dotNetSha256 -Publisher 'Microsoft Corporation'
if ($DependenciesOnly) {
    Write-Host "Verified Inno Setup $innoVersion compiler: $iscc"
    Write-Host "Verified .NET Framework 4.8 offline redistributable: $dotNetInstaller"
    return
}

foreach ($path in @(
    (Join-Path $projectRoot 'src\RemoteMonitorMaster\bin\Release\net48\RemoteMonitorMaster.exe'),
    (Join-Path $projectRoot 'src\RemoteMonitorMaster\bin\Release\net48\RemoteMonitorMaster.exe.config'),
    (Join-Path $projectRoot 'src\RemoteMonitorSlave\bin\Release\net48\RemoteMonitorSlave.exe'),
    (Join-Path $projectRoot 'src\RemoteMonitorSlave\bin\Release\net48\RemoteMonitorSlave.exe.config'),
    $iss
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing installer input: $path" }
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null

$common = @(
    "/DAppVersion=$version",
    "/DProjectRoot=$projectRoot",
    "/DOutputDir=$dist"
)
& $iscc @common "/DDotNetInstaller=$dotNetInstaller" '/DMasterBuild=1' $iss
if ($LASTEXITCODE -ne 0) { throw 'Master installer compilation failed.' }
& $iscc @common $iss
if ($LASTEXITCODE -ne 0) { throw 'Slave installer compilation failed.' }

$masterInstaller = Join-Path $dist "Messenger-Remote-Control-Master-Setup-$version.exe"
$slaveInstaller = Join-Path $dist "Messenger-Remote-Control-Slave-Setup-$version.exe"
foreach ($path in @($masterInstaller, $slaveInstaller)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing installer output: $path" }
}

if (-not $SkipReleaseChecksums) {
    $assets = @(
        $masterInstaller,
        $slaveInstaller,
        (Join-Path $dist "Messenger-Remote-Control-v$version-win7-win11-net48.zip"),
        (Join-Path $dist "Messenger-Remote-Control-Slave-v$version-win11-net48.zip")
    )
    foreach ($path in $assets) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release asset: $path" }
    }
    $sums = foreach ($path in $assets | Sort-Object { [IO.Path]::GetFileName($_) }) {
        "$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)  $([IO.Path]::GetFileName($path))"
    }
    Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Value $sums -Encoding ASCII
    & (Join-Path $PSScriptRoot 'check-release-assets.ps1') -AssetDirectory $dist
    if ($LASTEXITCODE -ne 0) { throw 'Release asset validation failed.' }
}

Write-Host "Inno Setup: $iscc"
Write-Host "Master installer: $masterInstaller"
Write-Host "Slave installer: $slaveInstaller"
