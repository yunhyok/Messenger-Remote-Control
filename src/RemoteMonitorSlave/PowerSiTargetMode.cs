using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
        // DC_MARKER | SI_MARKER | BOTH_MARKERS | NO_MARKER | DC_CAPTION_ONLY | SCAN_INCOMPLETE | SCAN_TIMEOUT | UIA_FAILED | worker code
        internal string Reason = "NOT_RUN";
        internal int Nodes;
        internal bool Complete;             // The bounded scan ended with nothing left to visit (not cut by the node limit).
        internal long ScanMilliseconds;
        internal bool Pending;              // The worker found the window not responding (SC_PENDING).
        // The resolved window's client size (the key of every stored Output position; Empty when unknown), its DPI and
        // whether it is shown unscaled (PowerSiOutputBuffer.UnscaledWindow). Metadata only, read before the scan.
        internal Size ClientSize = Size.Empty;
        internal int Dpi;
        internal bool Unscaled;
        internal bool IsPowerDc { get { return Mode == "POWERDC"; } }
    }

    // Where a UIA Name came from: Text names longer than 128 characters and every Edit/Document name are content (an Output
    // row, a log line) and never take part in marker matching.
    internal enum NameSource { Other, Text, Content }

    // Positive identification only: POWERDC needs the status-bar version marker "(PowerDC ...)" in a COMPLETE bounded scan
    // and no PowerSI marker among the UIA Names of the target's single top-level window (raw view, breadth first, at most
    // 512 nodes, depth 8, 2 seconds). A bare "PowerDC" caption alone, both, neither, an incomplete scan, a timeout or any
    // failure is UNKNOWN, and an UNKNOWN or POWERSI target is collected exactly as before.
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
        private const int MaxTextNameLength = 128;          // A Text element longer than this is a row or a log line.
        private const string Method = "UIA_NAME_SCAN";
        private static readonly Regex PowerDcVersion = new Regex(@"\((PowerDC)\b[^)]*\)", RegexOptions.CultureInvariant);
        private static readonly Regex PowerSiVersion = new Regex(@"\((PowerSI)\b[^)]*\)", RegexOptions.CultureInvariant);
        private static readonly string[] UnknownReasons = { "BOTH_MARKERS", "NO_MARKER", "DC_CAPTION_ONLY", "SCAN_INCOMPLETE",
            "SCAN_TIMEOUT", "UIA_FAILED" };

        // Pure: whether a Name may take part in marker matching at all.
        internal static bool UsableName(string name, NameSource source)
        {
            return !string.IsNullOrEmpty(name) && source != NameSource.Content &&
                name.Length <= (source == NameSource.Text ? MaxTextNameLength : MaxNameLength);
        }

        // Pure: the mode a list of usable UIA Names shows (ordinal, case-sensitive product names). Only the status-bar version
        // string identifies PowerDC, and only from a complete scan; any PowerSI marker means it is not PowerDC.
        internal static TargetMode Classify(IEnumerable<string> names, bool complete)
        {
            bool dcVersion = false, dcCaption = false, si = false;
            foreach (var name in names ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength) continue;
                var trimmed = name.Trim();
                if (PowerDcVersion.IsMatch(name)) dcVersion = true;
                else if (trimmed == "PowerDC") dcCaption = true;
                if (trimmed == "PowerSI" || PowerSiVersion.IsMatch(name)) si = true;
            }
            var mode = si ? (dcVersion || dcCaption ? new TargetMode { Reason = "BOTH_MARKERS" } : new TargetMode { Mode = "POWERSI", Reason = "SI_MARKER" })
                : dcVersion ? (complete ? new TargetMode { Mode = "POWERDC", Reason = "DC_MARKER" } : new TargetMode { Reason = "SCAN_INCOMPLETE" })
                : new TargetMode { Reason = dcCaption ? "DC_CAPTION_ONLY" : "NO_MARKER" };
            mode.Complete = complete;
            return mode;
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

        // OB1 result of the worker: Code TARGET_MODE_<mode>, Method UIA_NAME_SCAN,
        // Detail "M1|nodes|complete|scan_ms|client_w|client_h|dpi|unscaled|reason" (client 0|0 when unknown).
        internal static OutputBufferResult ToResult(TargetMode mode)
        {
            return new OutputBufferResult { Code = "TARGET_MODE_" + mode.Mode, Method = Method,
                Detail = string.Format(CultureInfo.InvariantCulture, "M1|{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}", mode.Nodes, mode.Complete ? 1 : 0,
                    mode.ScanMilliseconds, mode.ClientSize.Width, mode.ClientSize.Height, mode.Dpi, mode.Unscaled ? 1 : 0, mode.Reason) };
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
            long nodes, milliseconds, width, height, dpi;
            string mode = result.Code == "TARGET_MODE_POWERDC" ? "POWERDC" : result.Code == "TARGET_MODE_POWERSI" ? "POWERSI" :
                result.Code == "TARGET_MODE_UNKNOWN" ? "UNKNOWN" : null;
            if (mode != null && result.Method == Method && result.Text == null && p.Length == 9 && p[0] == "M1" &&
                Number(p[1], out nodes) && nodes <= MaxNodes && (p[2] == "0" || p[2] == "1") && Number(p[3], out milliseconds) &&
                Number(p[4], out width) && width <= 65535 && Number(p[5], out height) && height <= 65535 && (width == 0) == (height == 0) &&
                Number(p[6], out dpi) && dpi <= 4800 && (p[7] == "0" || p[7] == "1") &&
                (mode == "POWERDC" ? p[8] == "DC_MARKER" && p[2] == "1" : mode == "POWERSI" ? p[8] == "SI_MARKER" : UnknownReasons.Contains(p[8])))
                return new TargetMode { Mode = mode, Reason = p[8], Nodes = (int)nodes, Complete = p[2] == "1",
                    ScanMilliseconds = milliseconds, ClientSize = width == 0 ? Size.Empty : new Size((int)width, (int)height),
                    Dpi = (int)dpi, Unscaled = p[7] == "1" };
            return new TargetMode { Reason = OutputBufferCapture.SafeCode(result.Code) ? result.Code : "RESULT_INVALID" };
        }

        // "OUTPUT_TARGET_MODE" log metadata: numbers and fixed codes only.
        internal static string LogDetail(int pid, TargetMode mode, long elapsedMilliseconds)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "pid={0} mode={1} nodes={2} complete={3} scan_ms={4} elapsed_ms={5} client_w={6} client_h={7} dpi={8} unscaled={9} reason={10}",
                pid, mode.Mode, mode.Nodes, mode.Complete ? 1 : 0, Math.Max(0, mode.ScanMilliseconds), Math.Max(0, elapsedMilliseconds),
                mode.ClientSize.Width, mode.ClientSize.Height, mode.Dpi, mode.Unscaled ? 1 : 0,
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
            NativeRect client;
            var size = GetClientRect(root, out client) && client.Right > 0 && client.Bottom > 0 && client.Right <= 65535 &&
                client.Bottom <= 65535 ? new Size(client.Right, client.Bottom) : Size.Empty; // Same GetClientRect as the capture.
            int dpi;
            bool unscaled = PowerSiOutputBuffer.UnscaledWindow(root, out dpi);
            var mode = Scan(root, pid);
            mode.ClientSize = size; mode.Dpi = Math.Max(0, Math.Min(4800, dpi)); mode.Unscaled = unscaled;
            PowerSiScreenCapture.RequireResponsive(root);
            if (PowerSiScreenCapture.ResolveWindow(args) != root) throw new InvalidDataException("SC_WINDOW_CHANGED");
            return ToResult(mode);
        }

        // Breadth first under the root: the status bar and the dock captions sit right below the main window. The root's own
        // Name (the window title, which may hold a file name) is not used. Nodes of another process are neither read nor
        // entered; text content (Edit/Document, long Text) is never matched and row containers are not entered.
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
                        var name = child.GetCachedPropertyValue(AutomationElement.NameProperty) as string;
                        if (UsableName(name, type == ControlType.Edit || type == ControlType.Document ? NameSource.Content :
                            type == ControlType.Text ? NameSource.Text : NameSource.Other)) names.Add(name);
                        if (parent.Value + 1 < MaxDepth && !IsPruned(type))
                            queue.Enqueue(new KeyValuePair<AutomationElement, int>(child, parent.Value + 1));
                    }
                }
                if (clock.ElapsedMilliseconds > TimeBoxMilliseconds) return Finish(new TargetMode { Reason = "SCAN_TIMEOUT" });
            }
            catch { return Finish(new TargetMode { Reason = "UIA_FAILED" }); }
            return Finish(Classify(names, complete));
        }

        // Same pruning as the buffer worker's Output dock discovery: never scan individual rows or text content.
        private static bool IsPruned(ControlType type)
        {
            return type == ControlType.Tree || type == ControlType.List || type == ControlType.DataGrid || type == ControlType.Table ||
                type == ControlType.ComboBox || type == ControlType.Menu || type == ControlType.MenuBar || type == ControlType.ToolBar ||
                type == ControlType.Document || type == ControlType.Edit;
        }

        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

        // ---------------------------------------------------------------- self-test (pure; the UIA scan only fail-open on Windows)

        internal static void SelfTest()
        {
            void Need(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException("Target mode self-test: " + name);
            }
            TargetMode Of(params string[] names) { return Classify(names, true); }
            const string DcStatus = "Ver: 25.1.0.09191.616638 000 (PowerDC II)", SiStatus = "Ver: 25.1.0.09191.616638 000 (PowerSI II)";
            var dc = Of("Layout Workbench", DcStatus, "Output", "PowerDC", "Results and Report", "Sink Voltage");
            Need(dc.Mode == "POWERDC" && dc.Reason == "DC_MARKER" && dc.IsPowerDc && dc.Complete && Of("(PowerDC)").Mode == "POWERDC",
                "PowerDC status bar in a complete scan");
            // Only a complete scan identifies PowerDC: one cut at the node limit is UNKNOWN even with the version string.
            var cut = Classify(new[] { DcStatus, "Output" }, false);
            Need(cut.Mode == "UNKNOWN" && cut.Reason == "SCAN_INCOMPLETE" && !cut.IsPowerDc && !cut.Complete, "incomplete scan is UNKNOWN");
            // A bare "PowerDC" caption alone (in a complete or an incomplete scan) is not enough.
            foreach (bool complete in new[] { true, false })
            {
                var caption = Classify(new[] { "Output", " PowerDC ", "Miscellaneous" }, complete);
                Need(caption.Mode == "UNKNOWN" && caption.Reason == "DC_CAPTION_ONLY" && !caption.IsPowerDc, "DC caption only");
            }
            var si = Of("Layout Workbench", SiStatus, "PowerSI", "Miscellaneous", "Layout Errors", "Variables Check", "AFS Finished");
            Need(si.Mode == "POWERSI" && si.Reason == "SI_MARKER" && !si.IsPowerDc && Of("PowerSI").Mode == "POWERSI" &&
                Classify(new[] { SiStatus }, false).Mode == "POWERSI", "PowerSI status bar and caption");
            // Any PowerSI marker means it is not PowerDC.
            var both = Of(DcStatus, "PowerSI");
            Need(both.Mode == "UNKNOWN" && both.Reason == "BOTH_MARKERS" && Of("PowerDC", SiStatus).Reason == "BOTH_MARKERS" &&
                Of("(PowerDC II) (PowerSI II)").Reason == "BOTH_MARKERS" && Of(DcStatus, "Output", SiStatus).Reason == "BOTH_MARKERS",
                "both markers stay UNKNOWN");
            foreach (var none in new[] { new string[0], null, new[] { "Layout Workbench", "Output", null, "", "   " },
                new[] { "PowerDCX", "(PowerDCX)", "PowerDC II", "powerdc", "(powerdc II)", "POWERDC", "PowerDC.exe", "xPowerDC" },
                new[] { "PowerSIwave", "(PowerSIwave)", "PowerSI II", "(powersi)", "Network Display" },
                new[] { new string(' ', MaxNameLength) + "(PowerDC II)" } })
            {
                var unknown = Classify(none, true);
                Need(unknown.Mode == "UNKNOWN" && unknown.Reason == "NO_MARKER" && !unknown.IsPowerDc, "no marker stays UNKNOWN");
            }
            // Output rows and log lines never classify: long Text names and every Edit/Document name are dropped before matching.
            string row = "AFS Current Frequency ( MHz ) = 38.000 ... " + DcStatus + new string('.', 60);
            Need(row.Length > 128 && !UsableName(row, NameSource.Text) && UsableName(row, NameSource.Other) &&
                UsableName(DcStatus, NameSource.Text) && UsableName(new string('a', 128), NameSource.Text) &&
                !UsableName(new string('a', 129), NameSource.Text) && !UsableName(DcStatus, NameSource.Content) &&
                !UsableName("PowerDC", NameSource.Content) && !UsableName(null, NameSource.Other) && !UsableName("", NameSource.Text) &&
                UsableName(new string('a', 1024), NameSource.Other) && !UsableName(new string('a', 1025), NameSource.Other), "usable names");
            var scanned = new[] { Tuple.Create(row, NameSource.Text), Tuple.Create("(PowerDC II)", NameSource.Content),
                Tuple.Create("Output", NameSource.Other) }.Where(item => UsableName(item.Item1, item.Item2)).Select(item => item.Item1);
            Need(Classify(scanned, true).Reason == "NO_MARKER", "an Output row never identifies PowerDC");

            // Worker result: round trip, strict shape, self-consistency, pending and every failure fail-open.
            var client = new Size(1920, 1009);
            foreach (var mode in new[] { new TargetMode { Mode = "POWERDC", Reason = "DC_MARKER", Nodes = 137, Complete = true, ScanMilliseconds = 380,
                    ClientSize = client, Dpi = 96, Unscaled = true },
                new TargetMode { Mode = "POWERSI", Reason = "SI_MARKER", Nodes = 512, Complete = false, ScanMilliseconds = 1999, ClientSize = client, Dpi = 144 },
                new TargetMode { Reason = "BOTH_MARKERS", Nodes = 40, Complete = true }, new TargetMode { Reason = "SCAN_TIMEOUT", Nodes = 77, Dpi = 96 },
                new TargetMode { Reason = "UIA_FAILED" }, new TargetMode { Reason = "NO_MARKER", Nodes = 3, Complete = true },
                new TargetMode { Reason = "DC_CAPTION_ONLY", Nodes = 20, Complete = true }, new TargetMode { Reason = "SCAN_INCOMPLETE", Nodes = 512 } })
            {
                var wire = ToResult(mode);
                var back = FromResult(wire);
                Need(back.Mode == mode.Mode && back.Reason == mode.Reason && back.Nodes == mode.Nodes && back.Complete == mode.Complete &&
                    back.ScanMilliseconds == mode.ScanMilliseconds && back.ClientSize == mode.ClientSize && back.Dpi == mode.Dpi &&
                    back.Unscaled == mode.Unscaled && !back.Pending && wire.Text == null &&
                    System.Text.RegularExpressions.Regex.IsMatch(wire.Detail, @"\A[A-Z0-9_:=,| -]{1,512}\z"), "worker result round trip " + mode.Reason);
            }
            Need(ToResult(new TargetMode { Mode = "POWERDC", Reason = "DC_MARKER", Nodes = 9, Complete = true, ScanMilliseconds = 5,
                ClientSize = client, Dpi = 96, Unscaled = true }).Detail == "M1|9|1|5|1920|1009|96|1|DC_MARKER", "worker detail shape");
            OutputBufferResult Wire(string code, string detail, string method = Method, string text = null)
            { return new OutputBufferResult { Code = code, Method = method, Detail = detail, Text = text }; }
            foreach (var bad in new[] { Wire("TARGET_MODE_POWERDC", "M1|9|1|5|1920|1009|96|1|SI_MARKER"),
                Wire("TARGET_MODE_POWERDC", "M1|9|1|5|1920|1009|96|1|NO_MARKER"), Wire("TARGET_MODE_POWERDC", "M1|9|1|5|1920|1009|96|1|DC_CAPTION_ONLY"),
                Wire("TARGET_MODE_POWERDC", "M1|512|0|5|1920|1009|96|1|DC_MARKER"), // POWERDC from an incomplete scan
                Wire("TARGET_MODE_POWERSI", "M1|9|1|5|1920|1009|96|1|DC_MARKER"), Wire("TARGET_MODE_UNKNOWN", "M1|9|1|5|1920|1009|96|1|DC_MARKER"),
                Wire("TARGET_MODE_POWERDC", "M1|9|1|5|DC_MARKER"), Wire("TARGET_MODE_POWERDC", "M1|9|1|5|1920|1009|96|1|DC_MARKER|X"),
                Wire("TARGET_MODE_POWERDC", "M2|9|1|5|1920|1009|96|1|DC_MARKER"), Wire("TARGET_MODE_POWERDC", "M1|09|1|5|1920|1009|96|1|DC_MARKER"),
                Wire("TARGET_MODE_POWERDC", "M1|-9|1|5|1920|1009|96|1|DC_MARKER"), Wire("TARGET_MODE_POWERDC", "M1|513|1|5|1920|1009|96|1|DC_MARKER"),
                Wire("TARGET_MODE_POWERDC", "M1|9|2|5|1920|1009|96|1|DC_MARKER"), Wire("TARGET_MODE_POWERDC", "M1|9|1|x|1920|1009|96|1|DC_MARKER"),
                Wire("TARGET_MODE_POWERDC", "M1|9|1|5|1920|0|96|1|DC_MARKER"), Wire("TARGET_MODE_POWERDC", "M1|9|1|5|65536|1009|96|1|DC_MARKER"),
                Wire("TARGET_MODE_POWERDC", "M1|9|1|5|1920|1009|4801|1|DC_MARKER"), Wire("TARGET_MODE_POWERDC", "M1|9|1|5|1920|1009|96|2|DC_MARKER"),
                Wire("TARGET_MODE_POWERDC", "NONE", "NONE"), Wire("TARGET_MODE_POWERDC", "M1|9|1|5|1920|1009|96|1|DC_MARKER", "NATIVE_WM_GETTEXT"),
                Wire("TARGET_MODE_POWERDC", "M1|9|1|5|1920|1009|96|1|DC_MARKER", Method, "PowerDC"),
                Wire("TARGET_MODE_POWERDCX", "M1|9|1|5|1920|1009|96|1|DC_MARKER"), Wire("BUFFER_READ", "M1|9|1|5|1920|1009|96|1|DC_MARKER"), Wire(null, null) })
            {
                var parsed = FromResult(bad);
                Need(parsed.Mode == "UNKNOWN" && !parsed.IsPowerDc && !parsed.Pending && parsed.ClientSize.IsEmpty,
                    "malformed worker result is UNKNOWN: " + (bad.Code ?? "null") + " " + (bad.Detail ?? "null"));
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
                ScanMilliseconds = 380, ClientSize = client, Dpi = 96, Unscaled = true })), 612);
            Need(line == "pid=54108 mode=POWERDC nodes=137 complete=1 scan_ms=380 elapsed_ms=612 client_w=1920 client_h=1009 dpi=96 unscaled=1 reason=DC_MARKER" &&
                System.Text.RegularExpressions.Regex.IsMatch(line, @"\A[A-Za-z0-9_=| \-]{1,512}\z") &&
                LogDetail(1, new TargetMode { Reason = "bad reason" }, -5) ==
                    "pid=1 mode=UNKNOWN nodes=0 complete=0 scan_ms=0 elapsed_ms=0 client_w=0 client_h=0 dpi=0 unscaled=0 reason=INVALID",
                "log detail");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) WindowsFailOpenSelfTest(Need);
            Console.WriteLine("PASS: PowerDC/PowerSI window mode (classifier incl. complete-scan version marker and row exclusion, worker result, log record)");

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
