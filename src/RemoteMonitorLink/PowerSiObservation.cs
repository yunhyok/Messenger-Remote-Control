using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace RemoteMonitorLink
{
    // ponytail: one semantic Output/status reader, not a general UI adapter or simulation-completion model.
    internal sealed class PowerSiObservation
    {
        internal const int MaxWireLength = 512;
        private const int TailLimit = 16384;
        private const string WorkerArgument = "--powersi-probe";
        private const int MaxProbeStageLines = 32;
        private const int MaxProbeStageLength = 32;
        private const int MaxProbeDetails = 3200; // Two records per visited node plus bounded candidate ancestry/results.
        private const int MaxProbeDetailLength = 96;
        private const string NoProbeStage = "P1|NONE";
        private static readonly string[] Codes = { "OK", "OUTPUT_READ", "NOT_OBSERVED", "IDENTITY_UNAVAILABLE", "WINDOW_UNAVAILABLE",
            "AMBIGUOUS_WINDOWS", "OUTPUT_UNAVAILABLE", "OUTPUT_AMBIGUOUS", "STATUS_UNAVAILABLE", "UI_UNAVAILABLE", "TIMEOUT", "WORKER_FAILED",
            "VISION_NOT_CONFIGURED", "VISION_BUSY", "VISION_SERVER_UNAVAILABLE", "VISION_MODEL_UNAVAILABLE", "VISION_MODEL_AMBIGUOUS",
            "VISION_AUTH_REQUIRED", "VISION_TIMEOUT", "VISION_INVALID_RESPONSE", "VISION_CAPTURE_FAILED", "VISION_FAILED",
            "OUTPUT_REGION_UNCONFIRMED" };
        internal string Code { get; private set; }
        internal string OutputEvent { get; private set; } = "UNKNOWN";
        internal string OutputFrequency { get; private set; } = "?";
        internal bool MemoryWarningSeen { get; private set; }
        internal string StatusState { get; private set; } = "UNKNOWN";
        internal string StatusFrequency { get; private set; } = "?";
        internal bool OutputExposed { get; private set; }
        internal bool StatusExposed { get; private set; }
        internal bool IsVision { get; private set; }
        internal DateTime? CapturedUtc { get; private set; }
        // Never serialized or sent to Master. SlaveLog explicitly allowlists model/hash/timing/geometry metadata;
        // images, raw text and model responses stay local and out of logs.
        internal byte[] LocalImage;
        internal byte[] LocalFullImage;
        internal string LocalCaptureInfo;
        internal PowerSiFrame LocalFrame;
        internal byte[] LocalPaneImage, LocalSuggestedImage;
        internal LocalVisionModel LocalModelInfo;
        internal string LocalSampleId, LocalOcrSampleId, LocalRegionInfo;
        // Metadata only, never serialized: captured frame/body geometry and the B2 body-search counters for this run.
        internal System.Drawing.Size LocalFrameSize;
        internal System.Drawing.Rectangle LocalOutputBody;
        internal bool LocalVisibleEmpty; // Screen evidence only; never a full-buffer or wire assertion.
        internal string LocalBodyDiagnostics;
        internal string LocalVisionMode;
        internal int LocalRequestTimeoutSeconds;
        internal long LocalElapsedMs, LocalLocateMs, LocalReadMs;
        internal string LocalEvidence;
        internal string LocalModel;
        internal string LocalFailure;
        // This is local helper diagnostics only; PS1 stays fixed and carries no probe trace.
        internal string ProbeStage { get; private set; } = NoProbeStage;
        internal string[] ProbeDetails { get; private set; } = new string[0];

        internal static PowerSiObservation NotObserved() { return Unavailable("NOT_OBSERVED"); }
        internal static PowerSiObservation Unavailable(string code)
        {
            var result = new PowerSiObservation { Code = code };
            result.Validate(); return result;
        }
        internal string Summary
        {
            get
            {
                Validate();
                if (Code == "OUTPUT_READ")
                    return "VISION MODEL_READ OUTPUT_READ | CAPTURE UTC " + CapturedUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                        " | LOG EXCERPT ON SLAVE | NOT INTERPRETED";
                return (IsVision ? "VISION MODEL_READ " : "UI ") + Code +
                    (IsVision ? " | CAPTURE UTC " + (CapturedUtc.HasValue ? CapturedUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "?") : "") +
                    " | OUTPUT " + (OutputExposed ? "READ" : "UNAVAILABLE") +
                    " LAST " + OutputEvent + " FREQ " + OutputFrequency.Replace('_', ' ') +
                    " MEMORY WARNING SEEN " + (OutputExposed ? (MemoryWarningSeen ? "YES" : "NO IN READ TAIL") : "?") +
                    " | STATUS " + (StatusExposed ? StatusState : "UNAVAILABLE") + " FREQ " + StatusFrequency.Replace('_', ' ');
            }
        }
        internal string Serialize()
        {
            Validate();
            var value = string.Join(":", IsVision ? "PS2" : "PS1", Code, OutputEvent, OutputFrequency, MemoryWarningSeen ? "1" : "0",
                StatusState, StatusFrequency, OutputExposed ? "1" : "0", StatusExposed ? "1" : "0");
            return IsVision ? value + ":VISION:" + (CapturedUtc?.Ticks ?? 0).ToString(CultureInfo.InvariantCulture) : value;
        }
        internal static PowerSiObservation Parse(string text)
        {
            if (text == null || text.Length > MaxWireLength) throw new InvalidDataException("Invalid PowerSI observation.");
            var parts = text.Split(':');
            var vision = parts.Length == 11 && parts[0] == "PS2";
            if ((!vision && (parts.Length != 9 || parts[0] != "PS1")) || new[] { parts[4], parts[7], parts[8] }.Any(p => p != "0" && p != "1"))
                throw new InvalidDataException("Invalid PowerSI observation.");
            var result = new PowerSiObservation { Code = parts[1], OutputEvent = parts[2], OutputFrequency = parts[3],
                MemoryWarningSeen = parts[4] == "1", StatusState = parts[5], StatusFrequency = parts[6],
                OutputExposed = parts[7] == "1", StatusExposed = parts[8] == "1" };
            if (vision)
            {
                long ticks;
                if (parts[9] != "VISION" || !long.TryParse(parts[10], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) ||
                    ticks < 0 || ticks > DateTime.MaxValue.Ticks || ticks.ToString(CultureInfo.InvariantCulture) != parts[10])
                    throw new InvalidDataException("Invalid vision timestamp.");
                result.IsVision = true;
                result.CapturedUtc = ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
            }
            result.Validate(); return result;
        }
        internal static PowerSiObservation VisionResult(string output, string status, DateTime capturedUtc)
        {
            var result = FromText(output, status);
            result.IsVision = true; result.CapturedUtc = capturedUtc; result.Validate(); return result;
        }
        internal static PowerSiObservation VisionUnavailable(string code)
        {
            var result = Unavailable(code); result.IsVision = true; result.Validate(); return result;
        }
        internal static PowerSiObservation VisionLogExcerpt(string text, DateTime capturedUtc)
        {
            bool readable = !string.IsNullOrWhiteSpace(text);
            var result = new PowerSiObservation { IsVision = true, CapturedUtc = capturedUtc,
                OutputExposed = readable, Code = readable ? "OUTPUT_READ" : "OUTPUT_UNAVAILABLE" };
            result.Validate(); return result; // No inferred percentage, completion, frequency or event state.
        }
        private void Validate()
        {
            if ((!IsVision && CapturedUtc.HasValue) || (CapturedUtc.HasValue &&
                (CapturedUtc.Value.Kind != DateTimeKind.Utc || CapturedUtc.Value.Year < 2000)) ||
                (IsVision && (OutputExposed || StatusExposed) && !CapturedUtc.HasValue))
                throw new InvalidDataException("Invalid observation source/time.");
            if (!Codes.Contains(Code) || !new[] { "UNKNOWN", "RESUMED", "SUSPENDED" }.Contains(OutputEvent) ||
                (StatusState != "UNKNOWN" && StatusState != "SIMULATION") || !ValidFrequency(OutputFrequency) || !ValidFrequency(StatusFrequency) ||
                (!OutputExposed && (OutputEvent != "UNKNOWN" || OutputFrequency != "?" || MemoryWarningSeen)) ||
                (!StatusExposed && (StatusState != "UNKNOWN" || StatusFrequency != "?")) ||
                (Code == "OK" && (!OutputExposed || !StatusExposed)) ||
                (Code == "OUTPUT_READ" && (!IsVision || !OutputExposed || StatusExposed || OutputEvent != "UNKNOWN" ||
                    OutputFrequency != "?" || MemoryWarningSeen))) throw new InvalidDataException("Invalid PowerSI observation.");
        }
        private static bool ValidFrequency(string value)
        {
            if (value == "?") return true;
            if (value == null || value.Length > 32 || !Regex.IsMatch(value, @"\A(?:0|[1-9][0-9]{0,11})\.[0-9]{3,6}_(?:MHZ|GHZ)\z")) return false;
            decimal number;
            return decimal.TryParse(value.Split('_')[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number) &&
                number.ToString("0.000###", CultureInfo.InvariantCulture) + "_" + value.Split('_')[1] == value;
        }
        private static string Frequency(string text)
        {
            var result = "?";
            foreach (Match match in Regex.Matches(text, @"AFS\s+Current\s+Frequency\s*\(\s*(MHz|GHz)\s*\)\s*=\s*([0-9]{1,12}(?:\.[0-9]{1,6})?)(?![0-9.Ee])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                decimal number;
                if (decimal.TryParse(match.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number))
                    result = number.ToString("0.000###", CultureInfo.InvariantCulture) + "_" + match.Groups[1].Value.ToUpperInvariant();
            }
            return result;
        }
        internal static PowerSiObservation FromText(string output, string status)
        {
            if ((output != null && output.Length > TailLimit) || (status != null && status.Length > TailLimit))
                throw new InvalidDataException("PowerSI text exceeds read bound.");
            var result = new PowerSiObservation { OutputExposed = !string.IsNullOrWhiteSpace(output), StatusExposed = !string.IsNullOrWhiteSpace(status) };
            result.Code = !result.OutputExposed ? "OUTPUT_UNAVAILABLE" : !result.StatusExposed ? "STATUS_UNAVAILABLE" : "OK";
            if (result.OutputExposed)
            {
                result.OutputFrequency = Frequency(output);
                foreach (Match match in Regex.Matches(output, @"Simulation\s+(resumed|is\s+suspended)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    result.OutputEvent = match.Groups[1].Value.Equals("resumed", StringComparison.OrdinalIgnoreCase) ? "RESUMED" : "SUSPENDED";
                result.MemoryWarningSeen = Regex.IsMatch(output, @"Insufficient memory|Warning:\s*Available memory", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            if (result.StatusExposed)
            {
                result.StatusFrequency = Frequency(status);
                if (Regex.IsMatch(status, @"\bSimulation\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) result.StatusState = "SIMULATION";
            }
            result.Validate(); return result;
        }

        internal static async Task<PowerSiObservation> CaptureAsync(ProcessInventory inventory, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); inventory.Validate();
            if (inventory.Items.Any(p => !ProcessInventory.IsPowerSiName(p.Name))) return Unavailable("IDENTITY_UNAVAILABLE");
            if (inventory.Items.Length == 0) return NotObserved();
            if (inventory.Omitted != 0 || inventory.Items.Any(p => !p.StartUtcTicks.HasValue)) return Unavailable("IDENTITY_UNAVAILABLE");
            var identities = string.Join(",", inventory.Items.Select(p => p.Pid.ToString(CultureInfo.InvariantCulture) + ":" + p.StartUtcTicks.Value.ToString(CultureInfo.InvariantCulture)));
            return await RunWorker(inventory.SessionId.ToString(CultureInfo.InvariantCulture) + " " + identities, cancellation, 3500).ConfigureAwait(false);
        }
        private PowerSiObservation WithProbeStage(string value)
        {
            ProbeStage = NormalizeProbeStage(value); return this;
        }
        private static string NormalizeProbeStage(string value)
        {
            if (value == NoProbeStage || value == "P1|START" || value == "P1|IDENTITY" || value == "P1|ROOT" || value == "P1|DONE" ||
                value == "P1|OUTPUT_READ" || value == "P1|STATUS_READ") return value;
            if (value == null || value.Length > MaxProbeStageLength) return NoProbeStage;
            var parts = value.Split('|'); int count;
            if (parts.Length == 5 && parts[0] == "P1" && parts[1] == "CHOICE" &&
                parts.Skip(2).All(p => p.Length == 1 && p[0] >= '0' && p[0] <= '4') && parts.Skip(2).Sum(p => p[0] - '0') <= 4) return value;
            if (parts.Length != 3 || parts[0] != "P1" || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
                count < 0 || count.ToString(CultureInfo.InvariantCulture) != parts[2]) return NoProbeStage;
            return ((parts[1] == "WINDOWS" && count <= 2) || (parts[1] == "CANDIDATE" && count <= 4) ||
                ((parts[1] == "WALK" || parts[1] == "DISCOVER" || parts[1] == "REGION" || parts[1] == "OUTPUT" || parts[1] == "STATUS") && count <= 1536)) ? value : NoProbeStage;
        }
        private sealed class StageReporter
        {
            private int lines;
            private int details;
            internal int Scope;
            internal void Report(string value)
            {
                var stage = NormalizeProbeStage(value);
                if (stage == NoProbeStage || lines >= MaxProbeStageLines) return;
                lines++;
                try { Console.Error.WriteLine(stage); Console.Error.Flush(); } catch { }
            }
            internal void ReportCount(string name, int count) { Report("P1|" + name + "|" + count.ToString(CultureInfo.InvariantCulture)); }
            internal void Detail(string value)
            {
                if (!ValidProbeDetail(value) || details >= MaxProbeDetails) return;
                details++;
                try { Console.Error.WriteLine(value); Console.Error.Flush(); } catch { }
            }
        }
        private static bool ValidProbeDetail(string value)
        {
            if (value == null || value.Length > MaxProbeDetailLength) return false;
            var p = value.Split('|');
            bool Number(int index, int maximum)
            {
                int n;
                return index < p.Length && int.TryParse(p[index], NumberStyles.None, CultureInfo.InvariantCulture, out n) &&
                    n >= 0 && n <= maximum && n.ToString(CultureInfo.InvariantCulture) == p[index];
            }
            if (p.Length < 3 || p[0] != "P2" || !Number(2, 5)) return false;
            if (p[1] == "NODE" || p[1] == "PATH")
                return p.Length == 9 && Number(3, 1535) && Number(4, 1536) && Number(5, 20) && Number(6, 40) &&
                    new[] { "EMPTY", "OUTPUT", "OTHER" }.Contains(p[7]) && Number(8, TailLimit + 1);
            if (p[1] == "READ")
                return p.Length == 8 && Number(3, 1535) && new[] { "TEXT", "VALUE", "NAME", "NONE", "COVERED" }.Contains(p[4]) &&
                    new[] { "EMPTY", "HEADING", "CONTENT" }.Contains(p[5]) && Number(6, TailLimit) && Number(7, 15);
            return p[1] == "RESULT" && p.Length == 8 && new[] { "CONTENT", "LABEL", "UNKNOWN" }.Contains(p[3]) &&
                Number(4, 1536) && Number(5, TailLimit) && Number(6, 15) && Number(7, 1537);
        }
        private static int EvidenceBits(string text)
        {
            var observation = FromText(text, null);
            return (observation.OutputFrequency != "?" ? 1 : 0) | (observation.OutputEvent == "RESUMED" ? 2 : 0) |
                (observation.OutputEvent == "SUSPENDED" ? 4 : 0) | (observation.MemoryWarningSeen ? 8 : 0);
        }
        private static string NodeDetail(string kind, int scope, int index, UiNode node)
        {
            var name = node.Name ?? "";
            var nameKind = string.IsNullOrWhiteSpace(name) ? "EMPTY" :
                string.Equals(name.Trim(), "Output", StringComparison.OrdinalIgnoreCase) ? "OUTPUT" : "OTHER";
            return "P2|" + kind + "|" + scope + "|" + index + "|" + (node.Parent + 1) + "|" + node.Depth + "|" +
                (node.Type.Id - 50000) + "|" + nameKind + "|" + Math.Min(name.Length, TailLimit + 1);
        }
        private sealed class WorkerTrace
        {
            private readonly object gate = new object();
            private string last = NoProbeStage;
            private readonly List<string> details = new List<string>();
            internal void Add(string value)
            {
                var stage = NormalizeProbeStage(value);
                lock (gate)
                {
                    if (stage != NoProbeStage) last = stage;
                    else if (details.Count < MaxProbeDetails && ValidProbeDetail(value)) details.Add(value);
                }
            }
            internal string Last { get { lock (gate) return last; } }
            internal PowerSiObservation Attach(PowerSiObservation result)
            {
                lock (gate) { result.ProbeStage = last; result.ProbeDetails = details.ToArray(); }
                return result;
            }
            internal async Task<PowerSiObservation> FinishAsync(PowerSiObservation result, Task draining)
            {
                // The helper may have exited while its final diagnostic bytes are still in the pipe.
                if (draining != null) await Task.WhenAny(draining, Task.Delay(100)).ConfigureAwait(false);
                return Attach(result); // If the drain itself stalls, retain only the records received so far.
            }
        }
        private static async Task<PowerSiObservation> RunWorker(string arguments, CancellationToken cancellation, int timeout, Action<int> started = null)
        {
            using (var worker = new Process { StartInfo = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                WorkerArgument + " " + arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true } })
            {
                var trace = new WorkerTrace(); Task tracing = null; PowerSiObservation result = null;
                try
                {
                    cancellation.ThrowIfCancellationRequested(); worker.Start(); started?.Invoke(worker.Id);
                    var reading = ReadWorkerOutput(worker.StandardOutput);
                    tracing = ReadWorkerTrace(worker.StandardError, trace);
                    var clock = Stopwatch.StartNew();
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (clock.ElapsedMilliseconds >= timeout) { result = Unavailable("TIMEOUT"); break; }
                        if (worker.HasExited && reading.IsCompleted && tracing.IsCompleted)
                        {
                            await tracing.ConfigureAwait(false);
                            result = worker.ExitCode == 0 ? Parse(await reading.ConfigureAwait(false)) : Unavailable("WORKER_FAILED");
                            break;
                        }
                        await Task.Delay(25, cancellation).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { result = Unavailable("WORKER_FAILED"); }
                finally
                {
                    try { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(200); } } catch { }
                }
                return await trace.FinishAsync(result ?? Unavailable("WORKER_FAILED"), tracing).ConfigureAwait(false);
            }
        }
        private static async Task<string> ReadWorkerOutput(StreamReader reader)
        {
            var buffer = new char[MaxWireLength + 1]; var length = 0;
            while (length < buffer.Length)
            {
                var count = await reader.ReadAsync(buffer, length, buffer.Length - length).ConfigureAwait(false);
                if (count == 0) return new string(buffer, 0, length);
                length += count;
            }
            throw new InvalidDataException("Worker output exceeded bound.");
        }
        private static async Task ReadWorkerTrace(StreamReader reader, WorkerTrace trace)
        {
            var buffer = new char[256]; var line = new StringBuilder(MaxProbeDetailLength); var overlong = false;
            var lines = 0; var stageLines = 0;
            void AddLine(string value)
            {
                if (ValidProbeDetail(value)) trace.Add(value);
                else if (stageLines < MaxProbeStageLines && NormalizeProbeStage(value) != NoProbeStage)
                { trace.Add(value); stageLines++; }
            }
            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read == 0)
                    {
                        if (lines < MaxProbeStageLines + MaxProbeDetails && !overlong && line.Length != 0) AddLine(line.ToString());
                        return;
                    }
                    for (var i = 0; i < read; i++)
                    {
                        var character = buffer[i];
                        if (character == '\n')
                        {
                            if (lines < MaxProbeStageLines + MaxProbeDetails)
                            {
                                var value = line.ToString();
                                if (value.EndsWith("\r", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 1);
                                if (!overlong) AddLine(value);
                                lines++;
                            }
                            line.Clear(); overlong = false; continue;
                        }
                        if (lines >= MaxProbeStageLines + MaxProbeDetails || overlong) continue;
                        if (line.Length < MaxProbeDetailLength) line.Append(character); else { line.Clear(); overlong = true; }
                    }
                }
            }
            catch { } // The worker can be killed while this bounded drain is pending.
        }
        internal static bool TryRunWorker(string[] args)
        {
            if (args.Length == 0 || args[0] != WorkerArgument) return false;
            PowerSiObservation result;
            try
            {
                // UIA calls stay off the application's STA thread, in a process the parent can terminate.
                result = Task.Run(() => ReadWorker(args)).GetAwaiter().GetResult();
            }
            catch { result = Unavailable("UI_UNAVAILABLE"); }
            Console.Out.Write(result.Serialize()); return true;
        }
        private static PowerSiObservation ReadWorker(string[] args)
        {
            var stages = new StageReporter(); stages.Report("P1|START"); stages.Report("P1|IDENTITY");
            int session;
            if (args.Length != 3 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out session) ||
                args[2].Length > 4096) return Unavailable("IDENTITY_UNAVAILABLE");
            using (var self = Process.GetCurrentProcess()) if (self.SessionId != session) return Unavailable("IDENTITY_UNAVAILABLE");
            var identities = new Dictionary<int, long>();
            foreach (var encoded in args[2].Split(','))
            {
                var pair = encoded.Split(':'); int pid; long start;
                if (pair.Length != 2 || !int.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid < 1 ||
                    !long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 1 || identities.ContainsKey(pid))
                    return Unavailable("IDENTITY_UNAVAILABLE");
                identities.Add(pid, start);
            }
            if (identities.Count > ProcessInventory.MaxItems || identities.Any(p => !Matches(p.Key, p.Value, session)))
                return Unavailable("IDENTITY_UNAVAILABLE");
            var windows = new List<IntPtr>();
            EnumWindows((window, parameter) =>
            {
                uint pid; GetWindowThreadProcessId(window, out pid);
                if (identities.ContainsKey((int)pid) && IsWindowVisible(window)) windows.Add(window);
                return windows.Count < 2;
            }, IntPtr.Zero);
            stages.ReportCount("WINDOWS", windows.Count);
            if (windows.Count != 1) return Unavailable(windows.Count == 0 ? "WINDOW_UNAVAILABLE" : "AMBIGUOUS_WINDOWS");
            uint selectedPid; GetWindowThreadProcessId(windows[0], out selectedPid);
            stages.Report("P1|ROOT");
            var root = AutomationElement.FromHandle(windows[0]);
            var nodes = new List<UiNode>();
            // ponytail: cache only per-node metadata; this reduces probe calls, not the PowerSI evidence threshold.
            var request = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Automation.RawViewCondition,
                AutomationElementMode = AutomationElementMode.Full };
            request.Add(AutomationElement.ProcessIdProperty); request.Add(AutomationElement.NameProperty); request.Add(AutomationElement.ControlTypeProperty);
            var visited = 0;
            Walk(root.GetUpdatedCache(request), -1, 0, (int)selectedPid, nodes, request, stages, true, ref visited);
            bool ambiguous;
            string outputText = ReadOutputCandidates(nodes, (int)selectedPid, request, stages, ref visited, out ambiguous);
            var bars = nodes.Where(n => n.Type == ControlType.StatusBar).ToArray();
            stages.ReportCount("STATUS", bars.Length);
            stages.Scope = 5;
            bool ignoredLabel;
            string statusText = bars.Length == 1 ? ReadSelectedRegion(bars[0], (int)selectedPid, request, stages, false, ref visited, out ignoredLabel) : null;
            if (!IsWindowVisible(windows[0]) || identities.Any(p => !Matches(p.Key, p.Value, session))) return Unavailable("IDENTITY_UNAVAILABLE");
            uint finalPid; GetWindowThreadProcessId(windows[0], out finalPid);
            if (finalPid != selectedPid) return Unavailable("IDENTITY_UNAVAILABLE");
            var result = FromText(outputText, statusText);
            if (ambiguous) result.Code = "OUTPUT_AMBIGUOUS"; // Independent status evidence remains usable.
            stages.Report("P1|DONE"); return result;
        }
        private static int[] OutputScopes(List<UiNode> nodes)
        {
            var scopes = new HashSet<int>();
            foreach (var output in nodes.Where(IsOutputMarker))
            {
                var index = nodes.IndexOf(output);
                if (output.Type == ControlType.Text || output.Type == ControlType.TitleBar) index = output.Parent;
                if (index > 0) scopes.Add(index); // Never treat the whole workbench as an Output pane.
            }
            return scopes.Where(i => !scopes.Any(other => other != i && Under(nodes, i, other))).ToArray();
        }
        private static string ReadOutputCandidates(List<UiNode> nodes, int pid, CacheRequest request, StageReporter stages, ref int visited, out bool ambiguous)
        {
            var scopes = OutputScopes(nodes);
            stages.ReportCount("OUTPUT", scopes.Length);
            ambiguous = scopes.Length > 4;
            if (ambiguous) return null;
            var texts = new string[scopes.Length]; var labels = new bool[scopes.Length];
            for (var i = 0; i < scopes.Length; i++)
            {
                stages.Scope = i + 1;
                stages.ReportCount("CANDIDATE", i + 1);
                for (var ancestor = scopes[i]; ancestor >= 0; ancestor = nodes[ancestor].Parent)
                    stages.Detail(NodeDetail("PATH", stages.Scope, ancestor, nodes[ancestor]));
                texts[i] = ReadSelectedRegion(nodes[scopes[i]], pid, request, stages, true, ref visited, out labels[i]);
            }
            var content = texts.Count(t => !string.IsNullOrWhiteSpace(t)); var labelCount = labels.Count(l => l);
            stages.Report("P1|CHOICE|" + content + "|" + labelCount + "|" + (scopes.Length - content - labelCount));
            return ChooseOutput(texts, labels, out ambiguous);
        }
        private static string ChooseOutput(string[] texts, bool[] labels, out bool ambiguous)
        {
            // ponytail: select one readable region only when every other candidate is proven label-only; no ranking or text deduplication.
            var content = texts.Count(t => !string.IsNullOrWhiteSpace(t));
            ambiguous = texts.Length > 4 || content > 1 ||
                (texts.Length > 1 && texts.Where((t, i) => string.IsNullOrWhiteSpace(t) && !labels[i]).Any());
            return ambiguous ? null : texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        }
        private static string ReadSelectedRegion(UiNode root, int pid, CacheRequest request, StageReporter stages, bool output, ref int visited, out bool labelOnly)
        {
            var region = new List<UiNode>();
            stages.ReportCount("REGION", visited);
            Walk(root.Element.GetUpdatedCache(request), -1, root.Depth, pid, region, request, stages, false, ref visited);
            if ((output && region[0].Name != root.Name) || region[0].Type != root.Type) throw new InvalidDataException("PowerSI region changed.");
            for (var i = 0; i < region.Count; i++) stages.Detail(NodeDetail("NODE", stages.Scope, i, region[i]));
            stages.Report(output ? "P1|OUTPUT_READ" : "P1|STATUS_READ");
            var text = ReadRegion(region, 0, stages);
            var rejected = LabelOnlyRejection(region);
            labelOnly = string.IsNullOrWhiteSpace(text) && rejected < 0;
            stages.Detail("P2|RESULT|" + stages.Scope + "|" + (!string.IsNullOrWhiteSpace(text) ? "CONTENT" : labelOnly ? "LABEL" : "UNKNOWN") +
                "|" + region.Count + "|" + (text?.Length ?? 0) + "|" + EvidenceBits(text) + "|" + (rejected + 1));
            return text;
        }
        private static bool IsLabelOnlyRegion(List<UiNode> nodes)
        { return LabelOnlyRejection(nodes) < 0; }
        private static int LabelOnlyRejection(List<UiNode> nodes)
        {
            var parents = new HashSet<int>(nodes.Select(n => n.Parent));
            var sawLabel = false;
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.Type == ControlType.Text || node.Type == ControlType.TitleBar || node.Type == ControlType.TabItem)
                {
                    if (!string.Equals(node.Name.Trim(), "Output", StringComparison.OrdinalIgnoreCase)) return i;
                    sawLabel = true;
                }
                else if (node.Type == ControlType.Button) continue;
                else if (!parents.Contains(i) || (node.Type != ControlType.Pane && node.Type != ControlType.Group))
                    return i; // Empty/opaque panes, images, documents, edits and lists are not proof of a harmless label.
            }
            return sawLabel ? -1 : nodes.Count;
        }
        private static bool Matches(int pid, long start, int session)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return !process.HasExited && process.SessionId == session && ProcessInventory.IsPowerSiName(ProcessInventory.NormalizeName(process.ProcessName)) &&
                        process.StartTime.ToUniversalTime().Ticks == start;
            }
            catch { return false; }
        }
        private sealed class UiNode
        {
            internal AutomationElement Element;
            internal int Parent;
            internal int Depth;
            internal string Name;
            internal ControlType Type;
        }
        private static bool IsOutputMarker(UiNode node)
        {
            return string.Equals(node.Name.Trim(), "Output", StringComparison.OrdinalIgnoreCase) &&
                node.Type != ControlType.Button && node.Type != ControlType.TabItem && node.Type != ControlType.MenuItem &&
                node.Type != ControlType.TreeItem && node.Type != ControlType.ListItem && node.Type != ControlType.DataItem;
        }
        private static bool SkipDiscoveryChildren(UiNode node)
        {
            // ponytail: discover dock containers, not net/model/log rows; expand only the selected Output/status region.
            // An Output nested inside an unnamed collection is unsupported, never guessed from its contents.
            return IsOutputMarker(node) || node.Type == ControlType.StatusBar || node.Type == ControlType.Tree ||
                node.Type == ControlType.List || node.Type == ControlType.DataGrid || node.Type == ControlType.Table ||
                node.Type == ControlType.ComboBox || node.Type == ControlType.Menu || node.Type == ControlType.MenuBar ||
                node.Type == ControlType.ToolBar || node.Type == ControlType.Document || node.Type == ControlType.Edit;
        }
        private static void Walk(AutomationElement element, int parent, int depth, int pid, List<UiNode> nodes, CacheRequest request,
            StageReporter stages, bool discover, ref int visited)
        {
            if (depth > 20 || visited >= 1536) throw new InvalidDataException("PowerSI UI limit reached.");
            if ((visited & 127) == 0) stages.ReportCount(discover ? "DISCOVER" : "REGION", visited);
            visited++;
            var processId = (int)element.GetCachedPropertyValue(AutomationElement.ProcessIdProperty);
            if (processId != pid) throw new InvalidDataException("PowerSI UI process changed.");
            var index = nodes.Count;
            var node = new UiNode { Element = element, Parent = parent, Depth = depth,
                Name = element.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "",
                Type = (ControlType)element.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) };
            nodes.Add(node);
            if (discover && SkipDiscoveryChildren(node)) return;
            var walker = TreeWalker.RawViewWalker;
            for (var child = walker.GetFirstChild(element, request); child != null; child = walker.GetNextSibling(child, request))
                Walk(child, index, depth + 1, pid, nodes, request, stages, discover, ref visited);
        }
        private static bool Under(List<UiNode> nodes, int index, int ancestor)
        {
            for (; index >= 0; index = nodes[index].Parent) if (index == ancestor) return true;
            return false;
        }
        private static string ReadRegion(List<UiNode> nodes, int root, StageReporter stages)
        {
            var pieces = new List<string>(); var covered = new List<int>();
            for (var i = root; i < nodes.Count; i++)
            {
                if (!Under(nodes, i, root)) continue;
                if (covered.Any(parent => Under(nodes, i, parent)))
                { stages.Detail("P2|READ|" + stages.Scope + "|" + i + "|COVERED|EMPTY|0|0"); continue; }
                var node = nodes[i]; object pattern; string text = null; var patternCovers = false;
                var source = "NONE";
                if (node.Element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    source = "TEXT";
                    var document = ((TextPattern)pattern).DocumentRange;
                    var tail = document.Clone();
                    tail.MoveEndpointByRange(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.End);
                    tail.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -TailLimit);
                    text = tail.GetText(TailLimit + 1);
                    if (text.Length > TailLimit) throw new InvalidDataException("Provider did not expose a bounded tail.");
                    patternCovers = true;
                }
                else if (node.Element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                {
                    source = "VALUE";
                    text = ((ValuePattern)pattern).Current.Value;
                    if (text.Length > TailLimit) text = text.Substring(text.Length - TailLimit);
                    patternCovers = true;
                }
                else if (node.Type == ControlType.Text || (node.Type == ControlType.StatusBar &&
                    Regex.IsMatch(node.Name, @"\bSimulation\b|AFS\s+Current\s+Frequency", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                { text = node.Name; source = "NAME"; }
                if (text != null && text.Length > TailLimit) throw new InvalidDataException("PowerSI region exceeds bound.");
                var state = string.IsNullOrWhiteSpace(text) ? "EMPTY" : string.Equals(text.Trim(), "Output", StringComparison.OrdinalIgnoreCase) ? "HEADING" : "CONTENT";
                stages.Detail("P2|READ|" + stages.Scope + "|" + i + "|" + source + "|" + state + "|" + (text?.Length ?? 0) + "|" + EvidenceBits(text));
                if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text.Trim(), "Output", StringComparison.OrdinalIgnoreCase))
                {
                    pieces.Add(text);
                    if (patternCovers) covered.Add(i); // A pattern returning only the heading must not hide descendant log text.
                }
            }
            return JoinRegion(pieces);
        }
        private static string JoinRegion(List<string> pieces)
        {
            if (pieces.Sum(p => (long)p.Length) + Math.Max(0, pieces.Count - 1) > TailLimit)
                throw new InvalidDataException("PowerSI region exceeds bound.");
            return pieces.Count == 0 ? null : string.Join("\n", pieces);
        }
        private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);

        internal static void SelfTest()
        {
            UiNode Node(ControlType type, string name = "", int parent = -1) { return new UiNode { Type = type, Name = name, Parent = parent }; }
            foreach (var type in new[] { ControlType.Tree, ControlType.List, ControlType.DataGrid, ControlType.Table,
                ControlType.ComboBox, ControlType.Menu, ControlType.MenuBar, ControlType.ToolBar, ControlType.Document, ControlType.Edit })
                if (!SkipDiscoveryChildren(Node(type))) throw new InvalidOperationException("PowerSI collection discovery was expanded.");
            if (SkipDiscoveryChildren(Node(ControlType.Pane)) || SkipDiscoveryChildren(Node(ControlType.Group)) ||
                !SkipDiscoveryChildren(Node(ControlType.Pane, "Output")) || !SkipDiscoveryChildren(Node(ControlType.StatusBar)))
                throw new InvalidOperationException("PowerSI dock discovery scope failed.");
            // Output label can follow the log/list sibling; selected-region traversal must later expand it without pruning.
            var layout = new List<UiNode> { Node(ControlType.Window), Node(ControlType.Tree, "Nets", 0),
                Node(ControlType.Pane, "", 0), Node(ControlType.List, "", 2), Node(ControlType.Text, "Output", 2),
                Node(ControlType.StatusBar, "Simulation", 0), Node(ControlType.Button, "Output", 0) };
            if (!OutputScopes(layout).SequenceEqual(new[] { 2 })) throw new InvalidOperationException("PowerSI label-to-dock selection failed.");
            layout.Add(Node(ControlType.Pane, "Output", 0));
            if (OutputScopes(layout).Length != 2) throw new InvalidOperationException("Multiple PowerSI Output docks accepted.");
            foreach (var type in new[] { ControlType.Button, ControlType.TabItem, ControlType.MenuItem, ControlType.TreeItem, ControlType.ListItem, ControlType.DataItem })
                if (IsOutputMarker(Node(type, "Output"))) throw new InvalidOperationException("PowerSI collection item became an Output dock.");
            if (OutputScopes(new List<UiNode> { Node(ControlType.Window), Node(ControlType.Text, "Output", 0) }).Length != 0)
                throw new InvalidOperationException("PowerSI whole workbench became Output.");
            var labelDock = new List<UiNode> { Node(ControlType.Pane), Node(ControlType.Text, "Output", 0) };
            if (IsLabelOnlyRegion(new List<UiNode> { Node(ControlType.Custom), Node(ControlType.Text, "Output", 0) }))
                throw new InvalidOperationException("Custom-painted PowerSI region was discarded as a label.");
            if (!IsLabelOnlyRegion(labelDock) || !IsLabelOnlyRegion(new List<UiNode> { Node(ControlType.Text, " OUTPUT ") }) ||
                !IsLabelOnlyRegion(new List<UiNode> { Node(ControlType.TabItem, "Output"), Node(ControlType.Text, "Output", 0) }))
                throw new InvalidOperationException("PowerSI label-only dock classification failed.");
            foreach (var opaque in new[] { ControlType.Edit, ControlType.Document, ControlType.List, ControlType.Image, ControlType.Pane, ControlType.Custom })
            {
                labelDock.Add(Node(opaque, "", 0));
                if (IsLabelOnlyRegion(labelDock)) throw new InvalidOperationException("Unreadable PowerSI content was discarded as a label.");
                labelDock.RemoveAt(labelDock.Count - 1);
            }
            bool ambiguous;
            if (ChooseOutput(new[] { "Simulation resumed.", null }, new[] { false, true }, out ambiguous) != "Simulation resumed." || ambiguous ||
                ChooseOutput(new[] { null, "Simulation resumed." }, new[] { true, false }, out ambiguous) != "Simulation resumed." || ambiguous ||
                ChooseOutput(new string[] { null }, new[] { false }, out ambiguous) != null || ambiguous ||
                ChooseOutput(new string[] { null, null }, new[] { true, true }, out ambiguous) != null || ambiguous)
                throw new InvalidOperationException("PowerSI unique content selection failed.");
            if (ChooseOutput(new[] { "same log", "same log" }, new[] { false, false }, out ambiguous) != null || !ambiguous ||
                ChooseOutput(new[] { "log", null }, new[] { false, false }, out ambiguous) != null || !ambiguous ||
                ChooseOutput(new string[5], Enumerable.Repeat(true, 5).ToArray(), out ambiguous) != null || !ambiguous)
                throw new InvalidOperationException("Ambiguous PowerSI candidates accepted.");
            if (NormalizeProbeStage("P1|CHOICE|1|1|0") != "P1|CHOICE|1|1|0" ||
                NormalizeProbeStage("P1|CHOICE|4|1|0") != NoProbeStage || NormalizeProbeStage("P1|CHOICE|01|1|0") != NoProbeStage ||
                NormalizeProbeStage("P1|CANDIDATE|5") != NoProbeStage)
                throw new InvalidOperationException("PowerSI candidate diagnostics exceeded bounds.");
            if (NormalizeProbeStage("P1|DISCOVER|128") != "P1|DISCOVER|128" || NormalizeProbeStage("P1|REGION|1536") != "P1|REGION|1536" ||
                NormalizeProbeStage("P1|OUTPUT_READ") != "P1|OUTPUT_READ" || NormalizeProbeStage("P1|STATUS_READ") != "P1|STATUS_READ" ||
                NormalizeProbeStage("P1|REGION|1537") != NoProbeStage)
                throw new InvalidOperationException("PowerSI selective probe stage failed.");
            var sample = FromText("AFS Current Frequency ( GHz ) = 1.930\nInsufficient memory to support the simulation of new frequency, Simulation is suspended.\nSimulation resumed.\nAFS Current Frequency ( MHz ) = 38.000",
                "Simulation AFS Current Frequency ( MHz ) = 38.000");
            if (sample.Serialize() != "PS1:OK:RESUMED:38.000_MHZ:1:SIMULATION:38.000_MHZ:1:1" || Parse(sample.Serialize()).Summary != sample.Summary ||
                sample.Summary.Length > 400 || sample.Summary.Any(c => c < 32 || c > 126) || Regex.IsMatch(sample.Summary, @"M[2-9]{6}"))
                throw new InvalidOperationException("PowerSI screenshot summary failed.");
            if (FromText("Simulation resumed.\nSimulation is suspended.", null).OutputEvent != "SUSPENDED" ||
                FromText(null, "Simulation").OutputExposed || FromText("unrecognized output", null).OutputEvent != "UNKNOWN" ||
                FromText("AFS Current Frequency ( GHz ) = 1.500", null).OutputFrequency != "1.500_GHZ")
                throw new InvalidOperationException("PowerSI last-event/source classification failed.");
            var longTail = new string(' ', TailLimit - "Simulation resumed.".Length) + "Simulation resumed.";
            if (FromText(JoinRegion(new List<string> { longTail }), null).OutputEvent != "RESUMED")
                throw new InvalidOperationException("PowerSI exact-bound tail failed.");
            try { JoinRegion(new List<string> { longTail, "X" }); throw new InvalidOperationException("Oversized PowerSI region accepted."); }
            catch (InvalidDataException) { }
            foreach (var invalid in new[] { null, sample.Serialize() + "\n", sample.Serialize() + ":extra", sample.Serialize().Replace(":OK:", ":RUN:"),
                sample.Serialize().Replace("38.000_MHZ", "038.000_MHZ"), sample.Serialize().Replace(":1:1", ":0:0"), new string('X', MaxWireLength + 1) })
            {
                try { Parse(invalid); throw new InvalidOperationException("Malformed PowerSI observation accepted."); }
                catch (InvalidDataException) { }
            }
            if (NormalizeProbeStage("P1|WALK|64") != "P1|WALK|64" || new[] { "P1|WALK|1537", "P1|WINDOWS|3", "P1|WALK|064",
                "P1|WALK|64\nP1|DONE", new string('X', MaxProbeStageLength + 1) }.Any(stage => NormalizeProbeStage(stage) != NoProbeStage))
                throw new InvalidOperationException("Malformed PowerSI helper stage accepted.");
            string Trace(string value)
            {
                var trace = new WorkerTrace();
                using (var stream = new MemoryStream(Encoding.ASCII.GetBytes(value)))
                using (var reader = new StreamReader(stream)) ReadWorkerTrace(reader, trace).GetAwaiter().GetResult();
                return trace.Last;
            }
            if (Trace("P1|START\r\nP1|WALK|64\r\n") != "P1|WALK|64" ||
                Trace("P1|START\nP1|RO\rOT\nP1|DONE\r\r\n") != "P1|START" ||
                Trace(new string('X', 1000) + "\nP1|ROOT\n") != "P1|ROOT" ||
                Trace(string.Concat(Enumerable.Repeat("P1|START\n", 32)) + "P1|DONE\n") != "P1|START")
                throw new InvalidOperationException("PowerSI bounded stage reader failed.");
            var traced = sample.WithProbeStage("P1|WALK|64");
            if (Parse(traced.Serialize()).ProbeStage != NoProbeStage)
                throw new InvalidOperationException("Local helper trace leaked into PS1 wire.");
            var nodeRecord = NodeDetail("NODE", 2, 0, Node(ControlType.Custom, "private-design.spd"));
            var records = new[] { nodeRecord, "P2|READ|2|0|NONE|EMPTY|0|0", "P2|RESULT|2|UNKNOWN|1|0|0|1" };
            if (records.Any(r => !ValidProbeDetail(r)) || nodeRecord.Contains("private") ||
                EvidenceBits("Simulation resumed.\nAFS Current Frequency ( MHz ) = 38.000\nInsufficient memory") != 11 ||
                LabelOnlyRejection(new List<UiNode> { Node(ControlType.Pane), Node(ControlType.Text, "Output", 0), Node(ControlType.Custom, "", 0) }) != 2 ||
                new[] { "P2|RESULT|2|UNKNOWN|1|0|0|1\n", "P2|READ|2|0|VALUE|CONTENT|16385|0",
                    "P2|NODE|2|0|0|0|25|private-design.spd|18", "P2|READ|2|0|VALUE|CONTENT|1|16", "P2|RESULT|6|LABEL|1|0|0|0" }.Any(ValidProbeDetail))
                throw new InvalidOperationException("PowerSI diagnostic metadata validation failed.");
            var detailsTrace = new WorkerTrace();
            var diagnosticInput = string.Concat(Enumerable.Repeat("P1|START\n", MaxProbeStageLines)) + string.Join("\r\n", records) + "\r\nP1|DONE\n";
            using (var stream = new MemoryStream(Encoding.ASCII.GetBytes(diagnosticInput)))
            using (var reader = new StreamReader(stream)) ReadWorkerTrace(reader, detailsTrace).GetAwaiter().GetResult();
            var diagnosticResult = detailsTrace.Attach(Unavailable("TIMEOUT"));
            if (!diagnosticResult.ProbeDetails.SequenceEqual(records) || diagnosticResult.ProbeStage != "P1|START" ||
                Parse(diagnosticResult.Serialize()).ProbeDetails.Length != 0)
                throw new InvalidOperationException("PowerSI completed candidate details were lost or leaked to PS1.");
            for (var i = 0; i < MaxProbeDetails + 1; i++) detailsTrace.Add(records[0]);
            if (detailsTrace.Attach(NotObserved()).ProbeDetails.Length != MaxProbeDetails)
                throw new InvalidOperationException("PowerSI diagnostic record bound failed.");
            var delayedTrace = new WorkerTrace();
            var delayedDrain = Task.Run(async () => { await Task.Delay(20).ConfigureAwait(false); delayedTrace.Add(records[2]); });
            if (!delayedTrace.FinishAsync(Unavailable("TIMEOUT"), delayedDrain).GetAwaiter().GetResult().ProbeDetails.SequenceEqual(new[] { records[2] }))
                throw new InvalidOperationException("PowerSI final diagnostic drain was not awaited.");
            var stalledDrain = new TaskCompletionSource<bool>();
            var finishing = delayedTrace.FinishAsync(Unavailable("TIMEOUT"), stalledDrain.Task);
            if (Task.WhenAny(finishing, Task.Delay(1500)).GetAwaiter().GetResult() != finishing || finishing.Result.ProbeDetails.Length != 1)
                throw new InvalidOperationException("PowerSI diagnostic drain exceeded its bound or lost received records.");
            var ambiguousStatus = FromText(null, "Simulation AFS Current Frequency ( MHz ) = 38.000");
            ambiguousStatus.Code = "OUTPUT_AMBIGUOUS";
            if (!Parse(ambiguousStatus.Serialize()).StatusExposed || Parse(ambiguousStatus.Serialize()).OutputExposed)
                throw new InvalidOperationException("Ambiguous Output discarded independent status evidence.");
            var seenAt = new DateTime(2026, 9, 11, 1, 2, 3, DateTimeKind.Utc);
            var excerpt = VisionLogExcerpt("Simulation completed. AFS Current Frequency (MHz) = 38.000", seenAt);
            if (excerpt.Code != "OUTPUT_READ" || !excerpt.OutputExposed || excerpt.StatusExposed ||
                excerpt.OutputEvent != "UNKNOWN" || excerpt.OutputFrequency != "?" || excerpt.Summary.Contains("WARNING") ||
                !excerpt.Summary.Contains("NOT INTERPRETED") || Parse(excerpt.Serialize()).CapturedUtc != seenAt ||
                VisionLogExcerpt(null, seenAt).Code != "OUTPUT_UNAVAILABLE")
                throw new InvalidOperationException("Log excerpt was converted into inferred simulation state.");
            var visionResult = VisionResult("Simulation resumed.\nAFS Current Frequency ( MHz ) = 38.000", "Simulation", seenAt);
            visionResult.LocalModel = "private-model"; visionResult.LocalEvidence = "private-design.spd";
            visionResult.LocalImage = new byte[] { 1, 2, 3 };
            visionResult.LocalFullImage = new byte[] { 4, 5, 6 };
            visionResult.LocalCaptureInfo = "private-crop-info";
            visionResult.LocalModelInfo = new LocalVisionModel { Id = "local-model-metadata" };
            visionResult.LocalSampleId = new string('A', 64);
            visionResult.LocalOcrSampleId = new string('B', 64);
            visionResult.LocalVisionMode = "OCR_ONLY";
            visionResult.LocalRequestTimeoutSeconds = 90;
            visionResult.LocalFrameSize = new System.Drawing.Size(1920, 1080);
            visionResult.LocalOutputBody = new System.Drawing.Rectangle(317, 393, 585, 560);
            visionResult.LocalVisibleEmpty = true;
            visionResult.LocalBodyDiagnostics = "B2|1920|1080|7|1|4|1|0|1|0";
            var visionWire = visionResult.Serialize(); var visionRoundTrip = Parse(visionWire);
            if (!visionWire.StartsWith("PS2:") || !visionRoundTrip.IsVision || visionRoundTrip.CapturedUtc != seenAt ||
                visionRoundTrip.Summary != visionResult.Summary || visionRoundTrip.LocalEvidence != null || visionRoundTrip.LocalImage != null ||
                visionRoundTrip.LocalModel != null || visionRoundTrip.LocalFullImage != null || visionRoundTrip.LocalCaptureInfo != null ||
                visionRoundTrip.LocalModelInfo != null || visionRoundTrip.LocalSampleId != null || visionRoundTrip.LocalOcrSampleId != null ||
                visionRoundTrip.LocalVisionMode != null || visionRoundTrip.LocalRequestTimeoutSeconds != 0 ||
                visionRoundTrip.LocalBodyDiagnostics != null || !visionRoundTrip.LocalFrameSize.IsEmpty ||
                !visionRoundTrip.LocalOutputBody.IsEmpty || visionRoundTrip.LocalVisibleEmpty ||
                visionWire.Contains("B2|") ||
                visionWire.Contains("private") || visionRoundTrip.OutputFrequency != "38.000_MHZ")
                throw new InvalidOperationException("Local vision source/time or data boundary failed.");
            foreach (var invalid in new[] { visionWire.Replace(":VISION:", ":CLOUD:"), visionWire.Substring(0, visionWire.LastIndexOf(':') + 1) + "0",
                visionWire + ":raw", visionWire.Replace(seenAt.Ticks.ToString(CultureInfo.InvariantCulture), "01") })
            {
                try { Parse(invalid); throw new InvalidOperationException("Malformed vision metadata accepted."); }
                catch (InvalidDataException) { }
            }
            var unconfirmed = VisionUnavailable("OUTPUT_REGION_UNCONFIRMED");
            unconfirmed.LocalBodyDiagnostics = "B2|1920|1080|7|0|4|1|0|2|0";
            if (Parse(unconfirmed.Serialize()).Code != "OUTPUT_REGION_UNCONFIRMED" || !Parse(unconfirmed.Serialize()).IsVision ||
                Parse(unconfirmed.Serialize()).OutputExposed || unconfirmed.Serialize().Contains("B2"))
                throw new InvalidOperationException("Unconfirmed Output body code did not round-trip as metadata.");
            if (PowerSiVision.CaptureAsync(new ProcessInventory(), new LocalVisionSettings(), CancellationToken.None).GetAwaiter().GetResult().Code != "VISION_NOT_CONFIGURED")
                throw new InvalidOperationException("Unconfigured vision did not stay disabled.");
            if (CaptureAsync(new ProcessInventory(), CancellationToken.None).GetAwaiter().GetResult().Code != "NOT_OBSERVED")
                throw new InvalidOperationException("PowerSI empty scope failed.");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { CaptureAsync(new ProcessInventory(), cancellation.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("PowerSI cancellation failed."); }
                catch (OperationCanceledException) { }
            }
            // Actual helper dispatch/stdout/identity rejection, without querying another application's UI.
            var worker = RunWorker("0 0:0", CancellationToken.None, 3500).GetAwaiter().GetResult();
            if (worker.Code != "IDENTITY_UNAVAILABLE" || worker.ProbeStage != "P1|IDENTITY")
                throw new InvalidOperationException("PowerSI helper dispatch/trace failed: " + worker.Code + " " + worker.ProbeStage);
            int childPid = 0;
            if (RunWorker("0 0:0", CancellationToken.None, 0, pid => childPid = pid).GetAwaiter().GetResult().Code != "TIMEOUT")
                throw new InvalidOperationException("PowerSI helper timeout failed.");
            RequireChildExited(childPid);
            using (var cancellation = new CancellationTokenSource())
            {
                childPid = 0;
                try
                {
                    RunWorker("0 0:0", cancellation.Token, 3500, pid => { childPid = pid; cancellation.Cancel(); }).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Active PowerSI helper cancellation failed.");
                }
                catch (OperationCanceledException) { }
                RequireChildExited(childPid);
            }
        }
        private static void RequireChildExited(int pid)
        {
            if (pid < 1) throw new InvalidOperationException("PowerSI helper was not started.");
            try
            {
                using (var process = Process.GetProcessById(pid))
                    if (!process.HasExited) throw new InvalidOperationException("PowerSI helper survived timeout/cancellation.");
            }
            catch (ArgumentException) { } // An exited child is no longer in the process table.
        }
    }
}
