# Developer-only: powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test-powersi-discovery.ps1
# Uses an owned, offscreen WinForms window; never finds or operates another application's window.
param([string]$ExecutablePath = (Join-Path $PSScriptRoot '..\src\RemoteMonitorSlave\bin\Release\net48\RemoteMonitorSlave.exe'))
$ErrorActionPreference = 'Stop'
if ([Environment]::Version.Major -ne 4) { throw 'Run with Windows PowerShell (.NET Framework), not pwsh.' }
$uiaDirectory = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'WPF'
Add-Type -ReferencedAssemblies @('System.Windows.Forms', 'System.Drawing', 'Accessibility',
    (Join-Path $uiaDirectory 'UIAutomationClient.dll'), (Join-Path $uiaDirectory 'UIAutomationTypes.dll'), (Join-Path $uiaDirectory 'UIAutomationProvider.dll'),
    (Join-Path $uiaDirectory 'WindowsBase.dll')) -TypeDefinition @'
using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Forms;

public static class PowerSiDiscoveryCheck
{
    sealed class OpaqueCustom : Control, IRawElementProviderSimple
    {
        public ProviderOptions ProviderOptions { get { return ProviderOptions.ServerSideProvider; } }
        public IRawElementProviderSimple HostRawElementProvider { get { return AutomationInteropProvider.HostProviderFromHandle(Handle); } }
        public object GetPatternProvider(int patternId) { return null; }
        public object GetPropertyValue(int propertyId)
        {
            if (propertyId == AutomationElementIdentifiers.ControlTypeProperty.Id) return ControlType.Custom.Id;
            if (propertyId == AutomationElementIdentifiers.IsControlElementProperty.Id || propertyId == AutomationElementIdentifiers.IsContentElementProperty.Id) return true;
            return null;
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x003D && message.LParam.ToInt32() == AutomationInteropProvider.RootObjectId)
                message.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, message.WParam, message.LParam, this);
            else base.WndProc(ref message);
        }
    }

    sealed class HeadingValuePanel : Panel
    {
        protected override AccessibleObject CreateAccessibilityInstance() { return new HeadingValueObject(this); }
        sealed class HeadingValueObject : ControlAccessibleObject
        {
            internal HeadingValueObject(Control owner) : base(owner) { }
            public override AccessibleRole Role { get { return AccessibleRole.Text; } }
            public override string Value { get { return "Output"; } set { throw new NotSupportedException(); } }
        }
    }

    sealed class Fixture : Form
    {
        internal readonly TreeView Nets = new TreeView { Bounds = new Rectangle(0, 0, 180, 320), TabIndex = 0 };
        internal readonly Panel Output = ContentDock(new Panel());
        internal Panel Extra;
        internal readonly StatusBar Status = new StatusBar { Text = "Simulation AFS Current Frequency ( MHz ) = 38.000",
            AccessibleName = "Simulation AFS Current Frequency ( MHz ) = 38.000", TabIndex = 3 };
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; }
        }
        internal Fixture()
        {
            Text = "PowerSI discovery test fixture v2"; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000); Size = new Size(640, 420);
            for (int i = 0; i < 1000; i++) Nets.Nodes.Add("NET_" + i);
            Controls.AddRange(new Control[] { Nets, Output, Status });
        }
        static Panel ContentDock(Panel output)
        {
            output.Bounds = new Rectangle(190, 0, 400, 320); output.TabIndex = 1;
            var log = new Panel { Bounds = new Rectangle(0, 30, 390, 250), AccessibleRole = AccessibleRole.List, AccessibleName = "", TabIndex = 0 };
            log.Controls.Add(new Label { Text = "Simulation resumed.", Bounds = new Rectangle(0, 0, 380, 25), TabIndex = 0 });
            log.Controls.Add(new Label { Text = "AFS Current Frequency ( MHz ) = 38.000", Bounds = new Rectangle(0, 30, 380, 25), TabIndex = 1 });
            output.Controls.Add(log);
            output.Controls.Add(new Label { Text = "Output", Bounds = new Rectangle(0, 0, 380, 25), TabIndex = 1 });
            return output;
        }
        internal void SetExtra(string kind, bool first)
        {
            if (Extra != null) { Controls.Remove(Extra); Extra.Dispose(); }
            Extra = kind == "heading-value" ? ContentDock(new HeadingValuePanel()) :
                kind == "content" ? ContentDock(new Panel()) : new Panel();
            Extra.Bounds = new Rectangle(190, 0, 400, 320); Extra.AccessibleName = "Output";
            if (kind == "label") Extra.Controls.Add(new Label { Text = "Output" });
            else if (kind == "edit") Extra.Controls.Add(new TextBox { Text = "", AccessibleName = "" });
            else if (kind == "list") Extra.Controls.Add(new Panel { AccessibleName = "", AccessibleRole = AccessibleRole.List });
            else if (kind == "custom") Extra.Controls.Add(new OpaqueCustom { AccessibleName = "", Bounds = new Rectangle(0, 0, 100, 25) });
            Output.TabIndex = first ? 2 : 1; Extra.TabIndex = first ? 1 : 2;
            Controls.Add(Extra); Controls.SetChildIndex(Extra, first ? 1 : 2);
        }
    }

    const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Field(object node, string name) { return node.GetType().GetField(name, InstancePrivate).GetValue(node); }
    static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    static string Probe(string executable, Fixture fixture)
    {
        var type = Assembly.LoadFrom(executable).GetType("RemoteMonitorLink.PowerSiObservation", true);
        var nodeType = type.GetNestedType("UiNode", BindingFlags.NonPublic);
        var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(nodeType);
        var stages = Activator.CreateInstance(type.GetNestedType("StageReporter", BindingFlags.NonPublic), true);
        var walk = type.GetMethod("Walk", StaticPrivate);
        var select = type.GetMethod("ReadSelectedRegion", StaticPrivate);
        var request = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Automation.RawViewCondition,
            AutomationElementMode = AutomationElementMode.Full };
        request.Add(AutomationElement.ProcessIdProperty); request.Add(AutomationElement.NameProperty); request.Add(AutomationElement.ControlTypeProperty);
        var root = AutomationElement.FromHandle(fixture.Handle).GetUpdatedCache(request);
        int pid = Process.GetCurrentProcess().Id;
        var nodes = (IList)Activator.CreateInstance(listType);
        var args = new object[] { root, -1, 0, pid, nodes, request, stages, true, 0 };
        walk.Invoke(null, args);
        int visited = (int)args[8], discovery = visited;
        Need(fixture.Nets.Nodes.Count == 1000 && discovery < 100, "Large tree was not pruned.");
        Need(!nodes.Cast<object>().Any(n => ((string)Field(n, "Name")).StartsWith("NET_")), "Discovery read a net row.");
        int tree = -1, log = -1, label = -1;
        object status = null;
        for (int i = 0; i < nodes.Count; i++)
        {
            var kind = (ControlType)Field(nodes[i], "Type");
            if (kind == ControlType.Tree) tree = i;
            if (kind == ControlType.List) log = i;
            if ((string)Field(nodes[i], "Name") == "Output") label = i;
            if (kind == ControlType.StatusBar) { Need(status == null, "Fixture exposed multiple status bars."); status = nodes[i]; }
        }
        Need(tree >= 0 && tree < log && log < label && status != null, "Fixture did not expose tree, unnamed list, Output label, and status in expected order.");
        Need((string)Field(nodes[log], "Name") == "", "Fixture log collection was not unnamed.");
        Need(!nodes.Cast<object>().Any(n => (string)Field(n, "Name") == "Simulation resumed."), "Discovery expanded the log collection.");
        var scopes = (int[])type.GetMethod("OutputScopes", StaticPrivate).Invoke(null, new object[] { nodes });
        Need(scopes.Length == 1 && scopes[0] == (int)Field(nodes[label], "Parent"), "Output label did not select its dock.");
        args = new object[] { nodes[scopes[0]], pid, request, stages, true, visited, false };
        var output = (string)select.Invoke(null, args); visited = (int)args[5];
        Need(output != null && output.Contains("Simulation resumed.") && output.Contains("38.000") && visited > discovery + 3,
            "Selected Output region did not expand the preceding unnamed log list.");
        fixture.Invoke((Action)(() => { fixture.Status.Text = fixture.Status.AccessibleName = "Simulation AFS Current Frequency ( MHz ) = 40.000"; }));
        Need((string)Field(status, "Name") != fixture.Status.AccessibleName, "Status fixture did not change its name.");
        args = new object[] { status, pid, request, stages, false, visited, false };
        var statusText = (string)select.Invoke(null, args); visited = (int)args[5];
        Need(statusText != null && statusText.Contains("40.000"), "Changed status name was rejected or read stale.");
        var treeElement = ((AutomationElement)Field(nodes[tree], "Element")).GetUpdatedCache(request);
        // Exercise the real guards, including the final permitted visit at absolute depth 20.
        args = new object[] { treeElement, -1, 20, pid, Activator.CreateInstance(listType), request, stages, true, 1535 };
        walk.Invoke(null, args); Need((int)args[8] == 1536, "Last permitted global visit was rejected.");
        foreach (var limit in new[] { new[] { 0, 1536 }, new[] { 21, 0 } })
        {
            args = new object[] { treeElement, -1, limit[0], pid, Activator.CreateInstance(listType), request, stages, true, limit[1] };
            try { walk.Invoke(null, args); throw new InvalidOperationException("Traversal limit was bypassed."); }
            catch (TargetInvocationException ex) { if (!(ex.InnerException is InvalidDataException)) throw; }
        }
        var candidates = type.GetMethod("ReadOutputCandidates", StaticPrivate);
        foreach (var kind in new[] { "label", "content", "empty", "edit", "list", "custom", "heading-value" })
        {
            foreach (var first in new[] { false, true })
            {
                fixture.Invoke((Action)(() => fixture.SetExtra(kind, first)));
                nodes = (IList)Activator.CreateInstance(listType);
                root = AutomationElement.FromHandle(fixture.Handle).GetUpdatedCache(request);
                args = new object[] { root, -1, 0, pid, nodes, request, stages, true, 0 };
                walk.Invoke(null, args); int candidateVisits = (int)args[8];
                scopes = (int[])type.GetMethod("OutputScopes", StaticPrivate).Invoke(null, new object[] { nodes });
                Need(scopes.Length == 2, "Fixture did not expose two Output candidates: " + kind);
                int firstHandle = ((AutomationElement)Field(nodes[scopes[0]], "Element")).Current.NativeWindowHandle;
                Need(firstHandle == (first ? fixture.Extra.Handle : fixture.Output.Handle).ToInt32(), "Fixture did not reverse candidate order: " + kind);
                if (kind == "heading-value")
                {
                    object headingPattern;
                    var heading = AutomationElement.FromHandle(fixture.Extra.Handle);
                    Need(heading.TryGetCurrentPattern(ValuePattern.Pattern, out headingPattern) &&
                        ((ValuePattern)headingPattern).Current.Value == "Output", "Fixture did not expose the heading ValuePattern.");
                    args = new object[] { nodes[scopes[first ? 0 : 1]], pid, request, stages, true, candidateVisits, false };
                    var headingText = (string)select.Invoke(null, args);
                    Need(headingText != null && headingText.Contains("Simulation resumed.") && headingText.Contains("38.000") && !(bool)args[6],
                        "Heading ValuePattern hid descendant log text: first=" + first);
                }
                if (kind == "edit" || kind == "list" || kind == "custom")
                {
                    var children = AutomationElement.FromHandle(fixture.Extra.Handle).FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>().ToArray();
                    var expected = kind == "edit" ? ControlType.Edit : kind == "list" ? ControlType.List : ControlType.Custom;
                    Need(children.Any(child => child.Current.ControlType == expected), "Fixture did not expose opaque " + kind + " control: " + string.Join(",", children.Select(child => child.Current.ControlType.ProgrammaticName)));
                }
                args = new object[] { nodes, pid, request, stages, candidateVisits, false };
                string chosen, diagnostics;
                var previousError = Console.Error;
                using (var recording = new StringWriter())
                {
                    try { Console.SetError(recording); chosen = (string)candidates.Invoke(null, args); }
                    finally { Console.SetError(previousError); }
                    diagnostics = recording.ToString();
                }
                bool ambiguous = (bool)args[5];
                Need((int)args[4] > candidateVisits && (int)args[4] < 1536, "Candidate reads did not share the traversal budget.");
                if (kind == "label") Need(!ambiguous && chosen == output, "Label-only dock obscured the readable Output: first=" + first);
                else Need(ambiguous && chosen == null, "Ambiguous Output candidate was accepted: " + kind + " first=" + first);
                var trace = Activator.CreateInstance(type.GetNestedType("WorkerTrace", BindingFlags.NonPublic), true);
                using (var bytes = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(diagnostics)))
                using (var reader = new StreamReader(bytes))
                    ((Task)type.GetMethod("ReadWorkerTrace", StaticPrivate).Invoke(null, new object[] { reader, trace })).GetAwaiter().GetResult();
                var observation = type.GetMethod("Unavailable", StaticPrivate).Invoke(null, new object[] { ambiguous ? "OUTPUT_AMBIGUOUS" : "STATUS_UNAVAILABLE" });
                observation = trace.GetType().GetMethod("Attach", InstancePrivate).Invoke(trace, new[] { observation });
                var details = (string[])type.GetProperty("ProbeDetails", InstancePrivate).GetValue(observation);
                Need(details.Count(line => line.StartsWith("P2|RESULT|")) == 2 &&
                    details.Any(line => line.StartsWith("P2|PATH|")) && details.Any(line => line.StartsWith("P2|NODE|")) &&
                    details.Any(line => line.Contains("|NAME|CONTENT|") && line.EndsWith("|2")) &&
                    details.Any(line => line.Contains("|NAME|CONTENT|") && line.EndsWith("|1")) &&
                    !diagnostics.Contains("Simulation resumed") && !diagnostics.Contains("38.000") && !diagnostics.Contains("NET_"),
                    "Candidate diagnostic records lost scope/source/evidence or leaked raw text: " + kind);
                if (kind == "custom")
                {
                    var unknownScope = first ? "1" : "2";
                    Need(details.Any(line => line.StartsWith("P2|RESULT|" + unknownScope + "|UNKNOWN|")) &&
                        details.Any(line => line.StartsWith("P2|NODE|" + unknownScope + "|") && line.Contains("|25|")),
                        "Unknown competitor did not retain the Custom node and result.");
                    var directory = Path.Combine(Path.GetTempPath(), "remote-monitor-probe-check-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    try
                    {
                        var logType = type.Assembly.GetType("RemoteMonitorSlave.SlaveLog", true);
                        var saved = Activator.CreateInstance(logType, InstancePrivate, null, new object[] { directory }, null);
                        logType.GetMethod("WritePowerSi", InstancePrivate).Invoke(saved, new[] { observation });
                        var path = (string)logType.GetField("Path", InstancePrivate).GetValue(saved);
                        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                        using (var reader = new StreamReader(file))
                        {
                            var logged = reader.ReadToEnd();
                            Need(details.All(line => logged.Contains("code=PWRSI_PROBE data=" + line)), "Slave log lost diagnostic records or held the stream.");
                        }
                    }
                    finally { Directory.Delete(directory, true); }
                }
            }
        }
        return "PASS discovery=" + discovery + " total_visits=" + visited + " net_rows=1000 output_list=expanded status_name_change=accepted limits=1536/20 candidate_orders=both label=accepted duplicate/empty/edit/list/custom/heading-value=ambiguous diagnostics=scope/source/evidence/privacy/log-release";
    }

    public static string Run(string executable)
    {
        Fixture fixture = null; Exception startup = null;
        using (var ready = new ManualResetEvent(false))
        {
            var ui = new Thread(() => {
                try { using (fixture = new Fixture()) { fixture.Shown += (s, e) => ready.Set(); Application.Run(fixture); } }
                catch (Exception ex) { startup = ex; ready.Set(); }
            });
            ui.IsBackground = true; ui.SetApartmentState(ApartmentState.STA); ui.Start();
            try
            {
                Need(ready.WaitOne(5000), "Owned WinForms fixture did not start within 5 seconds.");
                if (startup != null) throw startup;
                var probe = Task.Run(() => Probe(executable, fixture));
                Need(Task.WhenAny(probe, Task.Delay(15000)).GetAwaiter().GetResult() == probe, "Owned UIA probe exceeded the 15 second test bound.");
                return probe.GetAwaiter().GetResult();
            }
            finally
            {
                if (fixture != null && fixture.IsHandleCreated && !fixture.IsDisposed) fixture.BeginInvoke((Action)(() => fixture.Close()));
                Need(ui.Join(3000), "Owned WinForms fixture did not close within 3 seconds.");
            }
        }
    }
}
'@
[PowerSiDiscoveryCheck]::Run([IO.Path]::GetFullPath($ExecutablePath))
