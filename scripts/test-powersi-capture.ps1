param([Parameter(Mandatory=$true)][string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
# Developer-only fixture: renders only its own off-screen/no-activate form; no screenshots are saved.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
public sealed class CaptureFixture : Form
{
    public CaptureFixture()
    {
        Text = "Owned capture test";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-20000, -20000);
        ClientSize = new Size(320, 180);
        ShowInTaskbar = false;
        BackColor = Color.White;
        Controls.Add(new Label { Text = "Output\r\nSimulation resumed.\r\nAFS Current Frequency (MHz) = 38.000",
            Location = new Point(15, 15), Size = new Size(290, 130), ForeColor = Color.Black });
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000; return p; }
    }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $ExecutablePath).Path)
$type = $assembly.GetType('RemoteMonitorLink.PowerSiScreenCapture', $true)
$flags = [Reflection.BindingFlags]'Static, NonPublic'
$type.GetMethod('SelfTest', $flags).Invoke($null, @()) | Out-Null
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = (Resolve-Path -LiteralPath $ExecutablePath).Path
$start.Arguments = '--powersi-screen invalid invalid'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true
$worker = New-Object Diagnostics.Process
$worker.StartInfo = $start
try {
    $worker.Start() | Out-Null
    if (-not $worker.WaitForExit(4000)) { throw 'Capture worker dispatch did not exit.' }
    if ($worker.ExitCode -ne 0 -or $worker.StandardOutput.ReadToEnd() -ne 'SC_IDENTITY') { throw 'Invalid worker identity was not rejected.' }
} finally {
    if (-not $worker.HasExited) { $worker.Kill(); $worker.WaitForExit(200) | Out-Null }
    $worker.Dispose()
}
$form = New-Object CaptureFixture
$foreground = [CaptureFixture]::GetForegroundWindow()
try {
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $frame = $type.GetMethod('CaptureWindow', $flags).Invoke($null, @($form.Handle))
    $frameType = $frame.GetType()
    $png = $frameType.GetField('Png', [Reflection.BindingFlags]'Instance, NonPublic').GetValue($frame)
    $stream = New-Object IO.MemoryStream(,$png)
    try {
        $image = [Drawing.Image]::FromStream($stream)
        try {
            if ($image.Width -ne 320 -or $image.Height -ne 180) { throw 'Wrong client dimensions.' }
            $bitmap = [Drawing.Bitmap]$image
            if ($bitmap.GetPixel(300,160).ToArgb() -ne [Drawing.Color]::White.ToArgb()) { throw 'Missing white fixture background.' }
            $dark = 0
            for ($y = 15; $y -lt 80; $y++) {
                for ($x = 15; $x -lt 305; $x++) {
                    if ($bitmap.GetPixel($x,$y).R -lt 80) { $dark++ }
                }
            }
            if ($dark -lt 100) { throw 'Fixture text did not render.' }
        } finally { $image.Dispose() }
    } finally { $stream.Dispose() }
    if ([CaptureFixture]::GetForegroundWindow() -ne $foreground) { throw 'Fixture changed foreground window.' }
    Write-Output ('PASS capture=self-owned client=320x180 text_pixels={0} png_bytes={1} elapsed_ms={2} worker_identity=rejected no_disk_image=true no_activation=true' -f $dark,$png.Length,$watch.ElapsedMilliseconds)
} finally { $form.Close(); $form.Dispose() }
