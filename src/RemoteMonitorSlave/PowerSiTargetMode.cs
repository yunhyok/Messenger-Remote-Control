using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using RemoteMonitorLink;

namespace RemoteMonitorSlave
{
    // One PowerSI-named target's window mode. PowerSI and PowerDC ship as the same Sigrity executable, so the process name
    // (ProcessInventory.IsPowerSiName) cannot tell them apart; the window's own UIA Names can: the status bar version
    // string ends with "(PowerDC II)" or "(PowerSI II)" and the product dock caption is "PowerDC" or "PowerSI".
    internal sealed class TargetMode
    {
        internal string Mode = "UNKNOWN";   // POWERDC | POWERSI | UNKNOWN
        internal string Reason = "NOT_RUN"; // DC_MARKER | SI_MARKER | BOTH_MARKERS | NO_MARKER | SCAN_TIMEOUT | UIA_FAILED | worker code
        internal int Nodes;
        internal bool Complete;             // The bounded scan ended with nothing left to visit (not cut by the node limit).
        internal long ScanMilliseconds;
        internal bool Pending;              // The worker found the window not responding (SC_PENDING).
        internal bool IsPowerDc { get { return Mode == "POWERDC"; } }
    }

    // Positive identification only: POWERDC needs a PowerDC marker and no PowerSI marker among the UIA Names of one bounded
    // scan of the target's single top-level window (raw view, breadth first, at most 512 nodes, depth 8, 2 seconds).
    // Both, neither, a timeout or any failure is UNKNOWN, and an UNKNOWN or POWERSI target is collected exactly as before.
    // A POWERDC target gets no buffer read, capture, LLM, input or clipboard use, and never a stored Output position.
    // The scan runs in a killable worker (UIA providers may hang). Names are compared in that worker's memory only:
    // its result, the Slave log and the report carry the mode, counts, elapsed time and a fixed reason code, never a name.
    internal static class PowerSiTargetMode
    {
        internal const string WorkerArgument = "--powersi-target-mode";
        internal const string PowerDcCode = "TARGET_MODE_POWERDC";
        internal const int MaxNodes = PowerSiOutputBuffer.MaxUiaNodes;
        internal const int MaxDepth = 8;
        internal const int TimeBoxMilliseconds = 2000;
        private const int WorkerTimeoutMilliseconds = 5000; // Process start + UIA init + the 2 s scan + the final window checks.
        private const int MaxNameLength = 1024;             // Status bar and dock captions are short; longer names are content.
        private const string Method = "UIA_NAME_SCAN";
        private static readonly Regex PowerDcVersion = new Regex(@"\((PowerDC)\b[^)]*\)", RegexOptions.CultureInvariant);
        private static readonly Regex PowerSiVersion = new Regex(@"\((PowerSI)\b[^)]*\)", RegexOptions.CultureInvariant);
        private static readonly string[] UnknownReasons = { "BOTH_MARKERS", "NO_MARKER", "SCAN_TIMEOUT", "UIA_FAILED" };

        // Pure: the mode a list of UIA Names shows (ordinal, case-sensitive product names).
        internal static TargetMode Classify(IEnumerable<string> names)
        {
            bool dc = false, si = false;
            foreach (var name in names ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength) continue;
                var trimmed = name.Trim();
                if (trimmed == "PowerDC" || PowerDcVersion.IsMatch(name)) dc = true;
                if (trimmed == "PowerSI" || PowerSiVersion.IsMatch(name)) si = true;
            }
            if (dc && !si) return new TargetMode { Mode = "POWERDC", Reason = "DC_MARKER" };
            if (si && !dc) return new TargetMode { Mode = "POWERSI", Reason = "SI_MARKER" };
            return new TargetMode { Reason = dc ? "BOTH_MARKERS" : "NO_MARKER" };
        }

        // ---------------------------------------------------------------- UI side

        // Fail-open: every worker failure or timeout is UNKNOWN. Only SC_PENDING is surfaced (Pending), for the caller to stop.
        internal static async Task<TargetMode> DetectAsync(ProcessInventory target, CancellationToken cancellation)
        {
            target.Validate();
            if (target.Items.Length != 1 || target.Omitted != 0 || !ProcessInventory.IsPowerSiName(target.Items[0].Name) ||
                !target.Items[0].StartUtcTicks.HasValue) return new TargetMode { Reason = "SC_IDENTITY" };
            var item = target.Items[0];
            var arguments = WorkerArgument + " " + target.SessionId.ToString(CultureInfo.InvariantCulture) + " " +
                item.Pid.ToString(CultureInfo.InvariantCulture) + ":" + item.StartUtcTicks.Value.ToString(CultureInfo.InvariantCulture);
            return FromResult(await OutputBufferCapture.RunWorkerAsync(arguments, WorkerTimeoutMilliseconds, "TARGET_MODE_TIMEOUT",
                "TARGET_MODE_WORKER_FAILED", cancellation).ConfigureAwait(false));
        }

        // OB1 result of the worker: Code TARGET_MODE_<mode>, Method UIA_NAME_SCAN, Detail "M1|nodes|complete|scan_ms|reason".
        internal static OutputBufferResult ToResult(TargetMode mode)
        {
            return new OutputBufferResult { Code = "TARGET_MODE_" + mode.Mode, Method = Method,
                Detail = string.Format(CultureInfo.InvariantCulture, "M1|{0}|{1}|{2}|{3}", mode.Nodes, mode.Complete ? 1 : 0,
                    mode.ScanMilliseconds, mode.Reason) };
        }

        // Strict: a mode is taken only from a well-formed, self-consistent worker result; anything else is UNKNOWN with
        // the worker's own code (timeout, failure, window resolution) as its reason.
        internal static TargetMode FromResult(OutputBufferResult result)
        {
            if (result == null) return new TargetMode { Reason = "RESULT_INVALID" };
            if (result.Code == "SC_PENDING") return new TargetMode { Reason = "SC_PENDING", Pending = true };
            var p = (result.Detail ?? "").Split('|');
            bool Number(string text, out long value)
            {
                return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
                    value.ToString(CultureInfo.InvariantCulture) == text;
            }
            long nodes, milliseconds;
            string mode = result.Code == "TARGET_MODE_POWERDC" ? "POWERDC" : result.Code == "TARGET_MODE_POWERSI" ? "POWERSI" :
                result.Code == "TARGET_MODE_UNKNOWN" ? "UNKNOWN" : null;
            if (mode != null && result.Method == Method && result.Text == null && p.Length == 5 && p[0] == "M1" &&
                Number(p[1], out nodes) && nodes <= MaxNodes && (p[2] == "0" || p[2] == "1") && Number(p[3], out milliseconds) &&
                (mode == "POWERDC" ? p[4] == "DC_MARKER" : mode == "POWERSI" ? p[4] == "SI_MARKER" : UnknownReasons.Contains(p[4])))
                return new TargetMode { Mode = mode, Reason = p[4], Nodes = (int)nodes, Complete = p[2] == "1",
                    ScanMilliseconds = milliseconds };
            return new TargetMode { Reason = OutputBufferCapture.SafeCode(result.Code) ? result.Code : "RESULT_INVALID" };
        }

        // "OUTPUT_TARGET_MODE" log metadata: numbers and fixed codes only.
        internal static string LogDetail(int pid, TargetMode mode, long elapsedMilliseconds)
        {
            return string.Format(CultureInfo.InvariantCulture, "pid={0} mode={1} nodes={2} complete={3} scan_ms={4} elapsed_ms={5} reason={6}",
                pid, mode.Mode, mode.Nodes, mode.Complete ? 1 : 0, Math.Max(0, mode.ScanMilliseconds), Math.Max(0, elapsedMilliseconds),
                OutputBufferCapture.SafeCode(mode.Reason) ? mode.Reason : "INVALID");
        }

        // ---------------------------------------------------------------- worker (Windows)

        // ResolveWindow validates PID, start time, session and the single visible top-level window, exactly like the buffer
        // worker; Windows message responsiveness is required before and after the scan (SC_PENDING otherwise).
        internal static OutputBufferResult RunWorker(string[] args)
        {
            var root = PowerSiScreenCapture.ResolveWindow(args, PowerSiScreenCapture.RequireResponsive);
            uint pid;
            if (GetWindowThreadProcessId(root, out pid) == 0) throw new InvalidDataException("SC_WINDOW_CHANGED");
            var mode = Scan(root, pid);
            PowerSiScreenCapture.RequireResponsive(root);
            if (PowerSiScreenCapture.ResolveWindow(args) != root) throw new InvalidDataException("SC_WINDOW_CHANGED");
            return ToResult(mode);
        }

        // Breadth first under the root: the status bar and the dock captions sit right below the main window. The root's own
        // Name (the window title, which may hold a file name) is not used. Nodes of another process are neither read nor
        // entered; text content (Edit/Document) is never read and row containers are not entered, like the buffer scan.
        private static TargetMode Scan(IntPtr root, uint pid)
        {
            var clock = Stopwatch.StartNew();
            var names = new List<string>();
            int visited = 0;
            bool complete = true;
            TargetMode Finish(TargetMode mode) { mode.Nodes = visited; mode.ScanMilliseconds = clock.ElapsedMilliseconds; return mode; }
            try
            {
                var request = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Automation.RawViewCondition,
                    AutomationElementMode = AutomationElementMode.Full };
                request.Add(AutomationElement.NameProperty); request.Add(AutomationElement.ControlTypeProperty);
                request.Add(AutomationElement.ProcessIdProperty);
                var walker = TreeWalker.RawViewWalker;
                var queue = new Queue<KeyValuePair<AutomationElement, int>>();
                queue.Enqueue(new KeyValuePair<AutomationElement, int>(AutomationElement.FromHandle(root).GetUpdatedCache(request), 0));
                while (queue.Count > 0 && complete)
                {
                    var parent = queue.Dequeue();
                    for (var child = walker.GetFirstChild(parent.Key, request); child != null; child = walker.GetNextSibling(child, request))
                    {
                        if (clock.ElapsedMilliseconds > TimeBoxMilliseconds) return Finish(new TargetMode { Reason = "SCAN_TIMEOUT" });
                        if (visited >= MaxNodes) { complete = false; break; }
                        visited++;
                        if ((int)child.GetCachedPropertyValue(AutomationElement.ProcessIdProperty) != (int)pid) continue;
                        var type = child.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) as ControlType;
                        if (type != ControlType.Edit && type != ControlType.Document)
                            names.Add(child.GetCachedPropertyValue(AutomationElement.NameProperty) as string);
                        if (parent.Value + 1 < MaxDepth && !IsPruned(type))
                            queue.Enqueue(new KeyValuePair<AutomationElement, int>(child, parent.Value + 1));
                    }
                }
                if (clock.ElapsedMilliseconds > TimeBoxMilliseconds) return Finish(new TargetMode { Reason = "SCAN_TIMEOUT" });
            }
            catch { return Finish(new TargetMode { Reason = "UIA_FAILED" }); }
            var classified = Classify(names);
            classified.Complete = complete;
            return Finish(classified);
        }

        // Same pruning as the buffer worker's Output dock discovery: never scan individual rows or text content.
        private static bool IsPruned(ControlType type)
        {
            return type == ControlType.Tree || type == ControlType.List || type == ControlType.DataGrid || type == ControlType.Table ||
                type == ControlType.ComboBox || type == ControlType.Menu || type == ControlType.MenuBar || type == ControlType.ToolBar ||
                type == ControlType.Document || type == ControlType.Edit;
        }

        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

        // ---------------------------------------------------------------- self-test (pure; the UIA scan only fail-open on Windows)

        internal static void SelfTest()
        {
            void Need(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException("Target mode self-test: " + name);
            }
            TargetMode Of(params string[] names) { return Classify(names); }
            const string DcStatus = "Ver: 25.1.0.09191.616638 000 (PowerDC II)", SiStatus = "Ver: 25.1.0.09191.616638 000 (PowerSI II)";
            var dc = Of("Layout Workbench", DcStatus, "Output", "Results and Report", "Sink Voltage");
            Need(dc.Mode == "POWERDC" && dc.Reason == "DC_MARKER" && dc.IsPowerDc, "PowerDC status bar");
            Need(Of("Output", " PowerDC ", "Miscellaneous").Mode == "POWERDC" && Of("(PowerDC)").Mode == "POWERDC", "PowerDC dock caption");
            var si = Of("Layout Workbench", SiStatus, "PowerSI", "Miscellaneous", "Layout Errors", "Variables Check", "AFS Finished");
            Need(si.Mode == "POWERSI" && si.Reason == "SI_MARKER" && !si.IsPowerDc && Of("PowerSI").Mode == "POWERSI", "PowerSI status bar and caption");
            var both = Of(DcStatus, "PowerSI");
            Need(both.Mode == "UNKNOWN" && both.Reason == "BOTH_MARKERS" && Of("PowerDC", SiStatus).Reason == "BOTH_MARKERS" &&
                Of("(PowerDC II) (PowerSI II)").Reason == "BOTH_MARKERS", "both markers stay UNKNOWN");
            foreach (var none in new[] { new string[0], null, new[] { "Layout Workbench", "Output", null, "", "   " },
                new[] { "PowerDCX", "(PowerDCX)", "PowerDC II", "powerdc", "(powerdc II)", "POWERDC", "PowerDC.exe", "xPowerDC" },
                new[] { "PowerSIwave", "(PowerSIwave)", "PowerSI II", "(powersi)", "Network Display" },
                new[] { new string(' ', MaxNameLength) + "(PowerDC II)" } })
            {
                var unknown = Classify(none);
                Need(unknown.Mode == "UNKNOWN" && unknown.Reason == "NO_MARKER" && !unknown.IsPowerDc, "no marker stays UNKNOWN");
            }

            // Worker result: round trip, strict shape, self-consistency, pending and every failure fail-open.
            foreach (var mode in new[] { new TargetMode { Mode = "POWERDC", Reason = "DC_MARKER", Nodes = 137, Complete = true, ScanMilliseconds = 380 },
                new TargetMode { Mode = "POWERSI", Reason = "SI_MARKER", Nodes = 512, Complete = false, ScanMilliseconds = 1999 },
                new TargetMode { Reason = "BOTH_MARKERS", Nodes = 40, Complete = true }, new TargetMode { Reason = "SCAN_TIMEOUT", Nodes = 77 },
                new TargetMode { Reason = "UIA_FAILED" }, new TargetMode { Reason = "NO_MARKER", Nodes = 3, Complete = true } })
            {
                var wire = ToResult(mode);
                var back = FromResult(wire);
                Need(back.Mode == mode.Mode && back.Reason == mode.Reason && back.Nodes == mode.Nodes && back.Complete == mode.Complete &&
                    back.ScanMilliseconds == mode.ScanMilliseconds && !back.Pending && wire.Text == null &&
                    System.Text.RegularExpressions.Regex.IsMatch(wire.Detail, @"\A[A-Z0-9_:=,| -]{1,512}\z"), "worker result round trip " + mode.Reason);
            }
            Need(ToResult(new TargetMode { Mode = "POWERDC", Reason = "DC_MARKER", Nodes = 9, Complete = true, ScanMilliseconds = 5 }).Detail ==
                "M1|9|1|5|DC_MARKER", "worker detail shape");
            OutputBufferResult Wire(string code, string detail, string method = Method, string text = null)
            { return new OutputBufferResult { Code = code, Method = method, Detail = detail, Text = text }; }
            foreach (var bad in new[] { Wire("TARGET_MODE_POWERDC", "M1|9|1|5|SI_MARKER"), Wire("TARGET_MODE_POWERDC", "M1|9|1|5|NO_MARKER"),
                Wire("TARGET_MODE_POWERSI", "M1|9|1|5|DC_MARKER"), Wire("TARGET_MODE_UNKNOWN", "M1|9|1|5|DC_MARKER"),
                Wire("TARGET_MODE_POWERDC", "M1|9|1|5"), Wire("TARGET_MODE_POWERDC", "M1|9|1|5|DC_MARKER|X"), Wire("TARGET_MODE_POWERDC", "M2|9|1|5|DC_MARKER"),
                Wire("TARGET_MODE_POWERDC", "M1|09|1|5|DC_MARKER"), Wire("TARGET_MODE_POWERDC", "M1|-9|1|5|DC_MARKER"), Wire("TARGET_MODE_POWERDC", "M1|513|1|5|DC_MARKER"),
                Wire("TARGET_MODE_POWERDC", "M1|9|2|5|DC_MARKER"), Wire("TARGET_MODE_POWERDC", "M1|9|1|x|DC_MARKER"), Wire("TARGET_MODE_POWERDC", "NONE", "NONE"),
                Wire("TARGET_MODE_POWERDC", "M1|9|1|5|DC_MARKER", "NATIVE_WM_GETTEXT"), Wire("TARGET_MODE_POWERDC", "M1|9|1|5|DC_MARKER", Method, "PowerDC"),
                Wire("TARGET_MODE_POWERDCX", "M1|9|1|5|DC_MARKER"), Wire("BUFFER_READ", "M1|9|1|5|DC_MARKER"), Wire(null, null) })
            {
                var parsed = FromResult(bad);
                Need(parsed.Mode == "UNKNOWN" && !parsed.IsPowerDc && !parsed.Pending, "malformed worker result is UNKNOWN: " + (bad.Code ?? "null") + " " + (bad.Detail ?? "null"));
            }
            var timedOut = FromResult(Wire("TARGET_MODE_TIMEOUT", "NONE", "NONE"));
            var failed = FromResult(Wire("TARGET_MODE_WORKER_FAILED", "NONE", "NONE"));
            var window = FromResult(Wire("SC_AMBIGUOUS_WINDOW", "NONE", "NONE"));
            var pending = FromResult(Wire("SC_PENDING", "NONE", "NONE"));
            Need(timedOut.Mode == "UNKNOWN" && timedOut.Reason == "TARGET_MODE_TIMEOUT" && failed.Reason == "TARGET_MODE_WORKER_FAILED" &&
                window.Mode == "UNKNOWN" && window.Reason == "SC_AMBIGUOUS_WINDOW" && FromResult(null).Reason == "RESULT_INVALID" &&
                FromResult(Wire("private window title", "NONE", "NONE")).Reason == "RESULT_INVALID", "worker failures are UNKNOWN");
            Need(pending.Pending && pending.Mode == "UNKNOWN" && !pending.IsPowerDc, "SC_PENDING is surfaced as Pending");

            // Log record: fixed codes and numbers that pass the Slave log filter.
            var line = LogDetail(54108, FromResult(ToResult(new TargetMode { Mode = "POWERDC", Reason = "DC_MARKER", Nodes = 137, Complete = true,
                ScanMilliseconds = 380 })), 612);
            Need(line == "pid=54108 mode=POWERDC nodes=137 complete=1 scan_ms=380 elapsed_ms=612 reason=DC_MARKER" &&
                System.Text.RegularExpressions.Regex.IsMatch(line, @"\A[A-Za-z0-9_=| \-]{1,512}\z") &&
                LogDetail(1, new TargetMode { Reason = "bad reason" }, -5) == "pid=1 mode=UNKNOWN nodes=0 complete=0 scan_ms=0 elapsed_ms=0 reason=INVALID",
                "log detail");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) WindowsFailOpenSelfTest(Need);
            Console.WriteLine("PASS: PowerDC/PowerSI window mode (classifier, worker result, log record; positive identification only)");

            // Report projection of an excluded PowerDC window (SlaveForm owns the projection).
            SlaveForm.TargetModeProjectionSelfTest();
        }

        // A missing window can never be judged: the UIA scan fails open instead of throwing.
        private static void WindowsFailOpenSelfTest(Action<bool, string> need)
        {
            var mode = Scan(IntPtr.Zero, 0);
            need(mode.Mode == "UNKNOWN" && mode.Reason == "UIA_FAILED" && mode.Nodes == 0, "UIA scan fails open");
        }
    }
}
