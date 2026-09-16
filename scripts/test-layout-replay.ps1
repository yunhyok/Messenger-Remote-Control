# Development-only: run with Windows PowerShell (powershell.exe), not pwsh.
# Reads supplied logs in memory; never prints or copies their private contents.
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$IntegratedLog,
    [Parameter(Mandatory = $true)][string]$StandaloneLog
)

$ErrorActionPreference = 'Stop'
$checkpoint = 'Windows PowerShell (.NET Framework) required'
try {
    if ([Environment]::Version.Major -ne 4) { throw 'Unsupported host.' }
    $checkpoint = 'loading replay API'
    $assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
    $nodeType = $assembly.GetType('RemoteMonitorMaster.ProbeNode', $true)
    $identityType = $assembly.GetType('RemoteMonitorMaster.ElementIdentity', $true)
    $identityCtor = $identityType.GetConstructors('Instance,NonPublic')[0]
    $listType = [Collections.Generic.List``1].MakeGenericType([Type[]]@($nodeType))
    $layout = $assembly.GetType('RemoteMonitorMaster.ReadOnlyPair', $true).GetMethod(
        'LayoutRejection', [Reflection.BindingFlags]'Static,Public,NonPublic')
    if ($null -eq $layout) { throw 'Missing replay API.' }

    function Read-Attempts([string]$path) {
        $attempts = New-Object 'Collections.Generic.List[object]'
        $current = $null
        foreach ($line in [IO.File]::ReadLines([IO.Path]::GetFullPath($path))) {
            $fields = @{}
            foreach ($part in $line.Split([char]9)) {
                if ($part -match '^(code|stage|node|parent_node|document_node|process_id|control_type|runtime_id|enabled|offscreen|value_pattern|value_read_only|complete|nodes)="([^"]*)"$') {
                    $fields[$matches[1]] = $matches[2]
                }
            }
            if ($fields.code -eq 'READ_ONLY_PROBE_BEGIN') {
                if ($null -ne $current) { throw 'Unclosed snapshot.' }
                if ($fields.stage -notin @('PAIR', 'PAIR_RECHECK')) { continue }
                $current = [pscustomobject]@{ Stage = $fields.stage; Nodes = [Activator]::CreateInstance($listType)
                    Rejection = $null; FirstReject = 0; Complete = $false }
            } elseif ($fields.code -eq 'PROBE_NODE' -and $null -ne $current) {
                if ($fields.stage -ne $current.Stage) { throw 'Mismatched node stage.' }
                $node = [Activator]::CreateInstance($nodeType, $true)
                $node.Node = [int]$fields.node
                $node.Parent = [int]$fields.parent_node
                $node.Document = [int]$fields.document_node
                if ($node.Node -ne $current.Nodes.Count + 1) { throw 'Unexpected node order.' }
                if ($fields.offscreen -notin @('True', 'False')) { throw 'Unknown visibility.' }
                $node.Visible = $fields.offscreen -eq 'False'
                $node.Enabled = $fields.enabled -eq 'True'
                $node.ValueWritable = $fields.value_pattern -eq 'True' -and $fields.value_read_only -eq 'False'
                $node.Identity = $identityCtor.Invoke([object[]]@(
                    $fields.runtime_id, [int]$fields.process_id, '', $fields.control_type, '', '', '', 0, '', ''))
                $current.Nodes.Add($node)
                if ($null -eq $current.Rejection) {
                    $arguments = New-Object object[] 1
                    $arguments[0] = $current.Nodes
                    $current.Rejection = $layout.Invoke($null, $arguments)
                    if ($null -ne $current.Rejection) { $current.FirstReject = $node.Node }
                }
            } elseif ($fields.code -eq 'READ_ONLY_PROBE_RESULT' -and $null -ne $current) {
                if ($fields.stage -ne $current.Stage -or [int]$fields.nodes -ne $current.Nodes.Count) {
                    throw 'Mismatched snapshot result.'
                }
                $current.Complete = $fields.complete -eq 'True'
                $attempts.Add($current)
                $current = $null
            }
        }
        if ($null -ne $current) { throw 'Missing snapshot result.' }
        return ,$attempts
    }

    $checkpoint = 'integrated evidence assertions'
    $integrated = Read-Attempts $IntegratedLog
    if ($integrated.Count -ne 2) { throw 'Expected two integrated attempts.' }
    $output = New-Object 'Collections.Generic.List[string]'
    for ($index = 0; $index -lt $integrated.Count; $index++) {
        $attempt = $integrated[$index]
        if ($attempt.Stage -ne 'PAIR' -or $attempt.Rejection -ne 'LAYOUT_VISIBLE_LIST' -or
            $attempt.FirstReject -le 0 -or $attempt.FirstReject -ge 400) { throw 'Integrated rejection regression.' }
        $output.Add(('integrated attempt={0} first_reject_node={1} reason=LAYOUT_VISIBLE_LIST' -f ($index + 1), $attempt.FirstReject))
    }
    $checkpoint = 'standalone evidence assertions'
    $standalone = Read-Attempts $StandaloneLog
    if ($standalone.Count -ne 2 -or $standalone[0].Stage -ne 'PAIR' -or
        $standalone[1].Stage -ne 'PAIR_RECHECK') { throw 'Expected standalone pair and recheck.' }
    foreach ($attempt in $standalone) {
        if ($null -ne $attempt.Rejection -or -not $attempt.Complete -or $attempt.Nodes.Count -eq 0) {
            throw 'Standalone layout or completeness regression.'
        }
        $output.Add(('standalone stage={0} nodes={1} layout_rejection=NONE complete=True' -f $attempt.Stage, $attempt.Nodes.Count))
    }
    $checkpoint = 'output privacy assertion'
    foreach ($message in $output) {
        if ($message -notmatch '^(integrated attempt=[12] first_reject_node=[0-9]+ reason=LAYOUT_VISIBLE_LIST|standalone stage=PAIR(_RECHECK)? nodes=[0-9]+ layout_rejection=NONE complete=True)$') {
            throw 'Unexpected output.'
        }
    }
    $output | Write-Output
    Write-Output 'PASS: layout replay; original snapshot completeness remains required.'
    exit 0
} catch {
    # Never render exception messages: filesystem errors can contain private paths.
    Write-Output "FAIL: $checkpoint. No input contents or paths printed."
    exit 1
}
