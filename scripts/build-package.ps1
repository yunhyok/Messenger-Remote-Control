param([switch]$SlaveOnly)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $projectRoot 'src\RemoteMonitorMaster\RemoteMonitorMaster.csproj'
$output = Join-Path $projectRoot 'src\RemoteMonitorMaster\bin\Release\net48'
$slaveProject = Join-Path $projectRoot 'src\RemoteMonitorSlave\RemoteMonitorSlave.csproj'
$slaveOutput = Join-Path $projectRoot 'src\RemoteMonitorSlave\bin\Release\net48'
$masterVersion = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$version = ([xml](Get-Content -LiteralPath $slaveProject -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid package version.' }
if ($masterVersion -cne $version) { throw "Master and Slave versions differ: $masterVersion / $version" }
$dist = Join-Path $projectRoot 'dist'
$fullZip = Join-Path $dist "Messenger-Remote-Control-v$version-win7-win11-net48.zip"
$slaveZip = Join-Path $dist "Messenger-Remote-Control-Slave-v$version-win11-net48.zip"
$targetProjects = if ($SlaveOnly) { @($slaveProject) } else { @($project, $slaveProject) }
$targetExecutables = if ($SlaveOnly) { @((Join-Path $slaveOutput 'RemoteMonitorSlave.exe')) } else {
    @((Join-Path $output 'RemoteMonitorMaster.exe'), (Join-Path $slaveOutput 'RemoteMonitorSlave.exe'))
}

foreach ($targetProject in $targetProjects) {
    dotnet restore $targetProject
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    dotnet build $targetProject -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
}
foreach ($executable in $targetExecutables) {
    $verification = Join-Path $dist "verification-v$version"
    New-Item -ItemType Directory -Path $verification -Force | Out-Null
    $testName = [IO.Path]::GetFileNameWithoutExtension($executable)
    $selfTest = Start-Process -FilePath $executable -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $verification ($testName + '.stdout.log')) `
        -RedirectStandardError (Join-Path $verification ($testName + '.stderr.log'))
    if ($selfTest.ExitCode -ne 0) { throw "Self-test failed with exit code $($selfTest.ExitCode): $executable" }
    Write-Host "Self-test passed: $([IO.Path]::GetFileName($executable))"
}

$packageTargets = if ($SlaveOnly) { @($slaveZip) } else { @($fullZip, $slaveZip) }
$rootPrefix = $projectRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($target in @($packageTargets) + (Join-Path $dist 'SHA256SUMS.txt')) {
    $fullTarget = [IO.Path]::GetFullPath($target)
    if (-not $fullTarget.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace path outside project: $fullTarget"
    }
    if (Test-Path -LiteralPath $fullTarget) { Remove-Item -LiteralPath $fullTarget -Force }
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null
$slaveFiles = @(
    (Join-Path $slaveOutput 'RemoteMonitorSlave.exe'),
    (Join-Path $slaveOutput 'RemoteMonitorSlave.exe.config'),
    (Join-Path $projectRoot 'README.md'),
    (Join-Path $projectRoot 'INSTALL.md'),
    (Join-Path $projectRoot 'HANDOFF.md'),
    (Join-Path $projectRoot 'SLAVE-TEST.md')
)
foreach ($path in $slaveFiles) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing package file: $path" }
}
Compress-Archive -LiteralPath $slaveFiles -DestinationPath $slaveZip -CompressionLevel Optimal

if (-not $SlaveOnly) {
    $fullFiles = @(
        (Join-Path $output 'RemoteMonitorMaster.exe'),
        (Join-Path $output 'RemoteMonitorMaster.exe.config'),
        (Join-Path $projectRoot 'WIN7-TEST.md')
    ) + $slaveFiles
    foreach ($path in $fullFiles) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing package file: $path" }
    }
    Compress-Archive -LiteralPath $fullFiles -DestinationPath $fullZip -CompressionLevel Optimal
}

$sums = foreach ($path in $packageTargets | Sort-Object { [IO.Path]::GetFileName($_) }) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    Write-Host "Package: $path"
    Write-Host "SHA256: $hash"
    "$hash  $([IO.Path]::GetFileName($path))"
}
Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Value $sums -Encoding ASCII
