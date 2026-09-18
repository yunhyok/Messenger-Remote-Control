param(
    [string]$AssetDirectory = (Join-Path $PSScriptRoot '..\dist'),
    [ValidateSet('Master', 'Slave', 'Both')]
    [string]$Role = 'Both'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ('work\installer-smoke-' + [Guid]::NewGuid().ToString('N'))))
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\RemoteMonitorSlave\RemoteMonitorSlave.csproj') -Raw)).Project.PropertyGroup.Version
$assetDirectory = [IO.Path]::GetFullPath($AssetDirectory)
$originalLocalAppData = $env:LOCALAPPDATA
$realLocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$fakeLocalAppData = Join-Path $workRoot 'LocalAppData'

function Get-UninstallEntries {
    param([string]$RegistryKey)
    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    return @(Get-ItemProperty $roots -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -ceq $RegistryKey })
}

function Invoke-Setup {
    param([string]$Path, [string]$InstallDirectory)
    $log = Join-Path $workRoot (([IO.Path]::GetFileNameWithoutExtension($Path)) + '-' + [Guid]::NewGuid().ToString('N') + '.log')
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /LOG=`"$log`""
    if ($InstallDirectory) { $arguments += " /DIR=`"$InstallDirectory`"" }
    $process = Start-Process -FilePath $Path -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Installer exited $($process.ExitCode): $Path (log: $log)" }
}

function Find-Shortcut {
    param([string]$ProductName)
    $paths = @(
        (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) "Messenger Remote Control\$ProductName.lnk"),
        (Join-Path ([Environment]::GetFolderPath('Programs')) "Messenger Remote Control\$ProductName.lnk")
    )
    return @($paths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
}

function Assert-PreservedFiles {
    param([hashtable]$Expected)
    foreach ($path in $Expected.Keys) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Content -LiteralPath $path -Raw) -cne $Expected[$path]) {
            throw "Installer changed or removed preserved local data: $path"
        }
    }
}

function Get-RealConfigSnapshot {
    param([string]$DataDirectoryName)
    $directory = Join-Path $realLocalAppData $DataDirectoryName
    $candidates = @(
        (Join-Path $directory 'identity.dat'),
        (Join-Path $directory 'local-vision.json')
    )
    $stateDirectory = Join-Path $directory 'state'
    if (Test-Path -LiteralPath $stateDirectory -PathType Container) {
        $candidates += @(Get-ChildItem -LiteralPath $stateDirectory -Filter '*.txt' -File |
            ForEach-Object { $_.FullName })
    }
    $snapshot = @{}
    foreach ($path in $candidates | Sort-Object -Unique) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $snapshot[[IO.Path]::GetFullPath($path)] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        }
    }
    return $snapshot
}

function Assert-RealConfigUnchanged {
    param([string]$DataDirectoryName, [hashtable]$Expected)
    $actual = Get-RealConfigSnapshot -DataDirectoryName $DataDirectoryName
    if ($actual.Count -ne $Expected.Count) {
        throw "Installer changed the protected real $DataDirectoryName config file set."
    }
    foreach ($path in $Expected.Keys) {
        if (-not $actual.ContainsKey($path) -or $actual[$path] -cne $Expected[$path]) {
            throw "Installer changed protected real $DataDirectoryName configuration."
        }
    }
}

New-Item -ItemType Directory -Path $fakeLocalAppData -Force | Out-Null
$env:LOCALAPPDATA = $fakeLocalAppData
$roles = @(
    [pscustomobject]@{
        Name = 'Master'; Product = 'Messenger Remote Control Master'; Exe = 'RemoteMonitorMaster.exe'; Data = 'RemoteMonitorMaster';
        RegistryKey = '{3F6487C1-1D80-4C38-8CAB-92C22E3DF4A0}_is1';
        Installer = Join-Path $assetDirectory "Messenger-Remote-Control-Master-Setup-$version.exe"
    },
    [pscustomobject]@{
        Name = 'Slave'; Product = 'Messenger Remote Control Slave'; Exe = 'RemoteMonitorSlave.exe'; Data = 'RemoteMonitorSlave';
        RegistryKey = '{8D85E8BA-6F8E-4B51-BA88-4B8B6D515627}_is1';
        Installer = Join-Path $assetDirectory "Messenger-Remote-Control-Slave-Setup-$version.exe"
    }
)
if ($Role -cne 'Both') { $roles = @($roles | Where-Object Name -CEQ $Role) }

try {
    foreach ($roleSpec in $roles) {
        if (-not (Test-Path -LiteralPath $roleSpec.Installer -PathType Leaf)) { throw "Missing installer: $($roleSpec.Installer)" }
        if (@(Get-UninstallEntries -RegistryKey $roleSpec.RegistryKey).Count -ne 0) {
            throw "$($roleSpec.Product) is already registered at another version; refusing to touch an existing installation."
        }
        if (@(Find-Shortcut -ProductName $roleSpec.Product).Count -ne 0) {
            throw "$($roleSpec.Product) already has a Start Menu shortcut; refusing to overwrite it."
        }

        $realConfig = Get-RealConfigSnapshot -DataDirectoryName $roleSpec.Data
        Write-Host "$($roleSpec.Product): protected real config present = $($realConfig.Count -gt 0)."

        $installDirectory = Join-Path $workRoot $roleSpec.Name
        $dataDirectory = Join-Path $fakeLocalAppData $roleSpec.Data
        New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
        $marker = [Guid]::NewGuid().ToString('N')
        $preserved = @{
            (Join-Path $dataDirectory 'identity.dat') = $marker
            (Join-Path $dataDirectory 'local-vision.json') = "{`"installer_smoke`":`"$marker`"}"
        }
        if ($roleSpec.Name -ceq 'Master') {
            $historyDirectory = Join-Path $dataDirectory 'state'
            New-Item -ItemType Directory -Path $historyDirectory -Force | Out-Null
            $preserved[(Join-Path $historyDirectory 'powersi-output-history-v1.txt')] = "installer-preservation-$marker"
        }
        foreach ($path in $preserved.Keys) { Set-Content -LiteralPath $path -Value $preserved[$path] -NoNewline -Encoding UTF8 }

        Invoke-Setup -Path $roleSpec.Installer -InstallDirectory $installDirectory
        Assert-PreservedFiles -Expected $preserved
        Assert-RealConfigUnchanged -DataDirectoryName $roleSpec.Data -Expected $realConfig
        $installedExe = Join-Path $installDirectory $roleSpec.Exe
        if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) { throw "Missing installed executable: $installedExe" }
        foreach ($document in @('README.md', 'INSTALL.md')) {
            if (-not (Test-Path -LiteralPath (Join-Path $installDirectory $document) -PathType Leaf)) { throw "Missing installed document: $document" }
        }
        $info = (Get-Item -LiteralPath $installedExe).VersionInfo
        if ($info.FileVersion -cne "$version.0" -or $info.ProductName -cne $roleSpec.Product) {
            throw "Unexpected installed metadata for $($roleSpec.Exe): $($info.ProductName) / $($info.FileVersion)"
        }
        $entries = @(Get-UninstallEntries -RegistryKey $roleSpec.RegistryKey)
        if ($entries.Count -ne 1 -or $entries[0].DisplayName -cne "$($roleSpec.Product) v$version" -or $entries[0].DisplayVersion -cne $version) {
            throw "Unexpected uninstall registration for $($roleSpec.Product)."
        }
        $shortcuts = @(Find-Shortcut -ProductName $roleSpec.Product)
        if ($shortcuts.Count -ne 1) { throw "Expected one Start Menu shortcut for $($roleSpec.Product), found $($shortcuts.Count)." }
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $null
        try {
            $shortcut = $shell.CreateShortcut($shortcuts[0])
            if ([IO.Path]::GetFullPath($shortcut.TargetPath) -cne [IO.Path]::GetFullPath($installedExe)) {
                throw "Shortcut target mismatch for $($roleSpec.Product)."
            }
        } finally {
            if ($shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }

        Invoke-Setup -Path $roleSpec.Installer -InstallDirectory $null
        Assert-PreservedFiles -Expected $preserved
        Assert-RealConfigUnchanged -DataDirectoryName $roleSpec.Data -Expected $realConfig
        $entries = @(Get-UninstallEntries -RegistryKey $roleSpec.RegistryKey)
        if ($entries.Count -ne 1) { throw "Upgrade created a duplicate uninstall entry for $($roleSpec.Product)." }
        if (-not $entries[0].PSObject.Properties['InstallLocation'] -or
            [IO.Path]::GetFullPath($entries[0].InstallLocation.TrimEnd('\')) -cne [IO.Path]::GetFullPath($installDirectory.TrimEnd('\'))) {
            throw "Upgrade did not reuse the isolated install directory for $($roleSpec.Product)."
        }

        $selfTestOut = Join-Path $workRoot ("self-test-$($roleSpec.Name).stdout.log")
        $selfTestErr = Join-Path $workRoot ("self-test-$($roleSpec.Name).stderr.log")
        $selfTest = Start-Process -FilePath $installedExe -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru `
            -RedirectStandardOutput $selfTestOut -RedirectStandardError $selfTestErr
        if ($selfTest.ExitCode -ne 0) { throw "Installed self-test exited $($selfTest.ExitCode): $installedExe" }
        Assert-PreservedFiles -Expected $preserved
        Assert-RealConfigUnchanged -DataDirectoryName $roleSpec.Data -Expected $realConfig

        $uninstaller = Join-Path $installDirectory 'unins000.exe'
        if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw "Missing uninstaller: $uninstaller" }
        $uninstallLog = Join-Path $workRoot ("uninstall-$($roleSpec.Name).log")
        $uninstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$uninstallLog`""
        $process = Start-Process -FilePath $uninstaller -ArgumentList $uninstallArguments -Wait -PassThru -WindowStyle Hidden
        if ($process.ExitCode -ne 0) { throw "Uninstaller exited $($process.ExitCode): $uninstaller" }
        Assert-PreservedFiles -Expected $preserved
        Assert-RealConfigUnchanged -DataDirectoryName $roleSpec.Data -Expected $realConfig
        if (Test-Path -LiteralPath $installedExe) { throw "Uninstaller left the executable in place: $installedExe" }
        if (@(Find-Shortcut -ProductName $roleSpec.Product).Count -ne 0) { throw "Uninstaller left the Start Menu shortcut for $($roleSpec.Product)." }
        if (@(Get-UninstallEntries -RegistryKey $roleSpec.RegistryKey).Count -ne 0) { throw "Uninstaller left registration for $($roleSpec.Product)." }
        Write-Host "$($roleSpec.Product): install, upgrade, uninstall, metadata, shortcut, and local-data checks passed."
    }
    Set-Content -LiteralPath (Join-Path $workRoot 'result.txt') -Value "Installer smoke checks passed for v$version." -Encoding ASCII
} finally {
    $env:LOCALAPPDATA = $originalLocalAppData
    foreach ($roleSpec in $roles) {
        $uninstaller = Join-Path (Join-Path $workRoot $roleSpec.Name) 'unins000.exe'
        if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
            Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -WindowStyle Hidden
        }
    }
}

Write-Host "Smoke evidence: $workRoot"
