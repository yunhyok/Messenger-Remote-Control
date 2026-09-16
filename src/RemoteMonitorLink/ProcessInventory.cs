using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteMonitorLink
{
    internal sealed class ProcessState
    {
        internal int Pid;
        internal string Name;
        internal string FullName;
        internal bool HasWindow;
        internal long? AgeSeconds;
        internal long? WorkingSetMiB;
        internal int? CpuPermille;
        internal long? StartUtcTicks; // Exact process identity; P2 carries it when readable.
    }

    internal sealed class ProcessInventory
    {
        internal const int MaxItems = 128;
        internal const int MaxNameLength = 40;
        internal const int MaxFullNameLength = 255;
        internal const int MaxWireLength = 164000;

        private const string WireVersion = "P2";
        private const int MaxCount = 999999;
        private const long MaxAgeSeconds = 3155760000L;
        private const long MaxWorkingSetMiB = long.MaxValue / 1048576;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal ProcessState[] Items = new ProcessState[0];
        internal int Omitted;
        internal int Unreadable;
        internal int SessionId;

        private sealed class Candidate
        {
            internal Process Process;
            internal int Pid;
            internal string Name;
            internal string FullName;
            internal long? StartUtcTicks;
            internal long? CpuTicks;
            internal long CpuTimestamp;
            internal bool Unreadable;
        }

        internal static Task<ProcessInventory> CaptureAsync(CancellationToken cancellation)
        {
            return CaptureAsync(cancellation, false);
        }

        internal static async Task<ProcessInventory> CaptureAsync(CancellationToken cancellation, bool powerSiOnly = false)
        {
            cancellation.ThrowIfCancellationRequested();
            int sessionId;
            using (Process current = Process.GetCurrentProcess()) sessionId = current.SessionId;

            Process[] processes = Process.GetProcesses();
            var candidates = new List<Candidate>();
            var states = new List<ProcessState>();
            var unreadable = 0;
            try
            {
                foreach (Process process in processes)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int processSession;
                    try { processSession = process.SessionId; }
                    catch { IncrementCount(ref unreadable); continue; }
                    if (processSession != sessionId) continue;

                    Candidate candidate;
                    try
                    {
                        string fullName = process.ProcessName;
                        if (!ValidFullName(fullName)) throw new InvalidDataException("Invalid process name.");
                        candidate = new Candidate
                        {
                            Process = process,
                            Pid = process.Id,
                            Name = NormalizeName(fullName),
                            FullName = fullName
                        };
                    }
                    catch
                    {
                        IncrementCount(ref unreadable);
                        continue;
                    }
                    if (powerSiOnly && !IsPowerSiName(candidate.Name)) continue;

                    try { candidate.StartUtcTicks = process.StartTime.ToUniversalTime().Ticks; }
                    catch { candidate.Unreadable = true; }
                    if (candidate.StartUtcTicks.HasValue)
                    {
                        try { candidate.CpuTicks = process.TotalProcessorTime.Ticks; }
                        catch { candidate.Unreadable = true; }
                    }
                    candidate.CpuTimestamp = Stopwatch.GetTimestamp();
                    candidates.Add(candidate);
                }

                await Task.Delay(500, cancellation).ConfigureAwait(false);
                foreach (Candidate candidate in candidates)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var itemUnreadable = candidate.Unreadable;
                    var state = new ProcessState
                        { Pid = candidate.Pid, Name = candidate.Name, FullName = candidate.FullName };

                    try
                    {
                        // ponytail: net48 Refresh can rescan all processes; use two batched snapshots if capture nears the 8s request budget.
                        candidate.Process.Refresh();
                    }
                    catch
                    {
                        IncrementCount(ref unreadable);
                        states.Add(state);
                        continue;
                    }

                    bool exited;
                    try { exited = candidate.Process.HasExited; }
                    catch
                    {
                        IncrementCount(ref unreadable);
                        states.Add(state);
                        continue;
                    }
                    if (exited)
                    {
                        IncrementCount(ref unreadable);
                        continue;
                    }

                    try
                    {
                        string fullName = candidate.Process.ProcessName;
                        if (candidate.Process.Id != candidate.Pid || candidate.Process.SessionId != sessionId ||
                            !string.Equals(fullName, candidate.FullName, StringComparison.Ordinal) ||
                            NormalizeName(fullName) != candidate.Name)
                        {
                            IncrementCount(ref unreadable);
                            continue;
                        }
                    }
                    catch
                    {
                        IncrementCount(ref unreadable);
                        states.Add(state);
                        continue;
                    }

                    long? recheckedStartUtcTicks = null;
                    try { recheckedStartUtcTicks = candidate.Process.StartTime.ToUniversalTime().Ticks; }
                    catch { itemUnreadable = true; }
                    if (candidate.StartUtcTicks.HasValue && recheckedStartUtcTicks.HasValue &&
                        candidate.StartUtcTicks.Value != recheckedStartUtcTicks.Value)
                    {
                        IncrementCount(ref unreadable);
                        continue;
                    }
                    bool sameStartIdentity = SameStartIdentity(candidate.StartUtcTicks, recheckedStartUtcTicks);
                    if (sameStartIdentity) state.StartUtcTicks = candidate.StartUtcTicks;
                    if (!sameStartIdentity) itemUnreadable = true;

                    try { state.HasWindow = candidate.Process.MainWindowHandle != IntPtr.Zero; }
                    catch { itemUnreadable = true; }

                    try
                    {
                        long bytes = candidate.Process.WorkingSet64;
                        if (bytes < 0) itemUnreadable = true;
                        else state.WorkingSetMiB = bytes / 1048576;
                    }
                    catch { itemUnreadable = true; }

                    if (sameStartIdentity)
                    {
                        long nowTicks = DateTime.UtcNow.Ticks;
                        long ageSeconds = nowTicks < candidate.StartUtcTicks.Value ? -1 :
                            (nowTicks - candidate.StartUtcTicks.Value) / TimeSpan.TicksPerSecond;
                        if (ageSeconds < 0 || ageSeconds > MaxAgeSeconds) itemUnreadable = true;
                        else state.AgeSeconds = ageSeconds;
                    }

                    if (sameStartIdentity && candidate.CpuTicks.HasValue)
                    {
                        try
                        {
                            long cpuNow = candidate.Process.TotalProcessorTime.Ticks;
                            long sampleNow = Stopwatch.GetTimestamp();
                            state.CpuPermille = CalculateCpuPermille(candidate.CpuTicks.Value, cpuNow,
                                sampleNow - candidate.CpuTimestamp, Stopwatch.Frequency, Environment.ProcessorCount);
                            if (!state.CpuPermille.HasValue) itemUnreadable = true;
                        }
                        catch { itemUnreadable = true; }
                    }

                    if (itemUnreadable) IncrementCount(ref unreadable);
                    states.Add(state);
                }
            }
            finally
            {
                foreach (Process process in processes) process.Dispose();
            }

            cancellation.ThrowIfCancellationRequested();
            return FinishCapture(sessionId, unreadable, states, powerSiOnly);
        }

        internal void Validate()
        {
            if (Items == null || Items.Length > MaxItems || SessionId < 0 || Omitted < 0 || Unreadable < 0 ||
                Omitted > MaxCount || Unreadable > MaxCount || Items.Length + Omitted > MaxCount)
                throw new InvalidDataException("Invalid process inventory.");

            var pids = new HashSet<int>();
            for (var i = 0; i < Items.Length; i++)
            {
                ProcessState item = Items[i];
                string fullName = item == null ? null : FullNameOf(item);
                if (item == null || item.Pid <= 0 || !pids.Add(item.Pid) || string.IsNullOrEmpty(item.Name) ||
                    item.Name.Length > MaxNameLength || item.Name != NormalizeName(item.Name) ||
                    !ValidFullName(fullName) || item.Name != NormalizeName(fullName) ||
                    (item.StartUtcTicks.HasValue &&
                        (item.StartUtcTicks.Value < 1 || item.StartUtcTicks.Value > DateTime.MaxValue.Ticks)) ||
                    (item.AgeSeconds.HasValue && (item.AgeSeconds.Value < 0 || item.AgeSeconds.Value > MaxAgeSeconds)) ||
                    (item.WorkingSetMiB.HasValue && (item.WorkingSetMiB.Value < 0 || item.WorkingSetMiB.Value > MaxWorkingSetMiB)) ||
                    (item.CpuPermille.HasValue && (item.CpuPermille.Value < 0 || item.CpuPermille.Value > 1000)) ||
                    (i > 0 && Compare(Items[i - 1], item) > 0))
                    throw new InvalidDataException("Invalid process inventory.");
            }
        }

        internal string Serialize()
        {
            Validate();
            var text = new StringBuilder();
            text.Append(WireVersion).Append(':').Append(SessionId.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(Omitted.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(Unreadable.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(Items.Length.ToString(CultureInfo.InvariantCulture));
            foreach (ProcessState item in Items)
            {
                text.Append(';').Append(item.Pid.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Convert.ToBase64String(StrictUtf8.GetBytes(item.Name))).Append(',')
                    .Append(Convert.ToBase64String(StrictUtf8.GetBytes(FullNameOf(item)))).Append(',')
                    .Append(Nullable(item.StartUtcTicks)).Append(',')
                    .Append(item.HasWindow ? '1' : '0').Append(',')
                    .Append(Nullable(item.AgeSeconds)).Append(',')
                    .Append(Nullable(item.WorkingSetMiB)).Append(',')
                    .Append(item.CpuPermille.HasValue ? item.CpuPermille.Value.ToString(CultureInfo.InvariantCulture) : "-");
            }
            string wire = text.ToString();
            if (wire.Length > MaxWireLength || wire.IndexOf('|') >= 0)
                throw new InvalidDataException("Invalid process inventory.");
            return wire;
        }

        internal ProcessInventory IdentityOnly()
        {
            Validate();
            var items = Items.Select(item => new ProcessState
            {
                Pid = item.Pid,
                Name = item.Name,
                FullName = FullNameOf(item),
                StartUtcTicks = item.StartUtcTicks
            }).ToArray();
            Array.Sort(items, Compare);
            var result = new ProcessInventory
            {
                SessionId = SessionId,
                Omitted = Omitted,
                Unreadable = Unreadable,
                Items = items
            };
            result.Validate();
            return result;
        }

        internal static ProcessInventory Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxWireLength || text.IndexOf('|') >= 0 ||
                text.Any(character => character < 0x21 || character > 0x7e))
                throw new InvalidDataException("Invalid process inventory.");

            string[] records = text.Split(';');
            string[] header = records[0].Split(':');
            int sessionId, omitted, unreadable, count;
            if (header.Length != 5 || header[0] != WireVersion ||
                !TryInt(header[1], 0, int.MaxValue, out sessionId) ||
                !TryInt(header[2], 0, MaxCount, out omitted) ||
                !TryInt(header[3], 0, MaxCount, out unreadable) ||
                !TryInt(header[4], 0, MaxItems, out count) || records.Length != count + 1)
                throw new InvalidDataException("Invalid process inventory.");

            var items = new ProcessState[count];
            for (var i = 0; i < count; i++)
            {
                string[] fields = records[i + 1].Split(',');
                int pid, cpu;
                long startTicks, age, memory;
                string name, fullName;
                if (fields.Length != 8 || !TryInt(fields[0], 1, int.MaxValue, out pid) ||
                    !TryName(fields[1], out name) || !TryFullName(fields[2], out fullName) ||
                    !TryNullableLong(fields[3], DateTime.MaxValue.Ticks, out startTicks) ||
                    (fields[4] != "0" && fields[4] != "1") ||
                    !TryNullableLong(fields[5], MaxAgeSeconds, out age) ||
                    !TryNullableLong(fields[6], MaxWorkingSetMiB, out memory) ||
                    !TryNullableInt(fields[7], 1000, out cpu))
                    throw new InvalidDataException("Invalid process inventory.");
                items[i] = new ProcessState
                {
                    Pid = pid,
                    Name = name,
                    FullName = fullName,
                    StartUtcTicks = fields[3] == "-" ? (long?)null : startTicks,
                    HasWindow = fields[4] == "1",
                    AgeSeconds = fields[5] == "-" ? (long?)null : age,
                    WorkingSetMiB = fields[6] == "-" ? (long?)null : memory,
                    CpuPermille = fields[7] == "-" ? (int?)null : cpu
                };
            }

            var result = new ProcessInventory { SessionId = sessionId, Omitted = omitted, Unreadable = unreadable, Items = items };
            result.Validate();
            if (result.Serialize() != text) throw new InvalidDataException("Invalid process inventory.");
            return result;
        }

        internal static string NormalizeName(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            string lower = value.ToLowerInvariant();
            var safe = new StringBuilder(lower.Length);
            foreach (char character in lower)
                safe.Append(char.IsLetterOrDigit(character) || character == ' ' || character == '.' || character == '_' ||
                    character == '-' || character == '(' || character == ')' ? character : '_');
            if (safe.Length == 0) safe.Append('_');
            if (safe.Length > MaxNameLength)
            {
                safe.Length = MaxNameLength - 1;
                safe.Append('_');
            }
            return safe.ToString();
        }

        internal static bool IsPowerSiName(string normalizedName)
        {
            return string.Equals(normalizedName, "powersi", StringComparison.Ordinal) ||
                string.Equals(normalizedName, "pwrsi", StringComparison.Ordinal);
        }

        internal static int? CalculateCpuPermille(long beforeCpuTicks, long afterCpuTicks,
            long elapsedStopwatchTicks, long stopwatchFrequency, int logicalProcessors)
        {
            if (beforeCpuTicks < 0 || afterCpuTicks < beforeCpuTicks || elapsedStopwatchTicks <= 0 ||
                stopwatchFrequency <= 0 || logicalProcessors <= 0) return null;
            decimal usage = (decimal)(afterCpuTicks - beforeCpuTicks) / elapsedStopwatchTicks * stopwatchFrequency /
                TimeSpan.TicksPerSecond / logicalProcessors * 1000m;
            if (usage < 0) return null;
            if (usage > 1000m) return 1000;
            return (int)Math.Round(usage, MidpointRounding.AwayFromZero);
        }

        internal static void RunSelfTest()
        {
            Need(NormalizeName("MIX/한글\\Path\t") == "mix_한글_path_" &&
                NormalizeName(new string('A', 41)) == new string('a', 39) + "_" &&
                NormalizeName("") == "_" && NormalizeName("M234567").IndexOf('M') < 0,
                "PROCESS_NAME_NORMALIZATION");
            Need(IsPowerSiName("powersi") && IsPowerSiName("pwrsi") &&
                !IsPowerSiName("PowerSI") && !IsPowerSiName("powersi helper") && !IsPowerSiName(null),
                "PROCESS_PWRSI_EXACT_NAME");
            Need(CalculateCpuPermille(0, TimeSpan.TicksPerSecond, Stopwatch.Frequency,
                    Stopwatch.Frequency, 1) == 1000 &&
                CalculateCpuPermille(0, TimeSpan.TicksPerSecond, Stopwatch.Frequency,
                    Stopwatch.Frequency, 4) == 250 &&
                CalculateCpuPermille(10, 9, 1, Stopwatch.Frequency, 1) == null &&
                CalculateCpuPermille(0, 1, 0, Stopwatch.Frequency, 1) == null,
                "PROCESS_CPU_DENOMINATOR");
            Need(SameStartIdentity(123, 123) && !SameStartIdentity(null, 123) &&
                !SameStartIdentity(123, null) && !SameStartIdentity(123, 124),
                "PROCESS_START_IDENTITY");

            var inventory = new ProcessInventory
            {
                SessionId = 1,
                Omitted = 2,
                Unreadable = 3,
                Items = new[]
                {
                    new ProcessState { Pid = 10, Name = "한글 solver", HasWindow = true,
                        StartUtcTicks = new DateTime(2026, 9, 15, 1, 0, 0, DateTimeKind.Utc).Ticks },
                    new ProcessState { Pid = 20, Name = "worker_2", AgeSeconds = 12, WorkingSetMiB = 34, CpuPermille = 56 }
                }
            };
            Array.Sort(inventory.Items, Compare);
            string wire = inventory.Serialize();
            ProcessInventory parsed = Parse(wire);
            Need(wire.IndexOf('|') < 0 && parsed.Serialize() == wire && parsed.Items.Length == 2 &&
                parsed.Items[1].AgeSeconds == null && parsed.Items[1].WorkingSetMiB == null &&
                parsed.Items[1].CpuPermille == null && parsed.Items[0].CpuPermille == 56 && parsed.Items[0].Pid == 20 &&
                parsed.Items[0].FullName == "worker_2" && parsed.Items[1].StartUtcTicks == inventory.Items[1].StartUtcTicks,
                "PROCESS_CODEC_ROUNDTRIP");
            ProcessInventory identityOnly = inventory.IdentityOnly();
            ProcessState identityTen = identityOnly.Items.Single(item => item.Pid == 10);
            ProcessState identityTwenty = identityOnly.Items.Single(item => item.Pid == 20);
            Need(!ReferenceEquals(identityOnly, inventory) && identityOnly.Items[0].Pid == 10 &&
                identityOnly.SessionId == inventory.SessionId && identityOnly.Omitted == inventory.Omitted &&
                identityOnly.Unreadable == inventory.Unreadable && !identityTen.HasWindow &&
                !identityTen.AgeSeconds.HasValue && !identityTen.WorkingSetMiB.HasValue &&
                !identityTen.CpuPermille.HasValue && identityTen.StartUtcTicks == inventory.Items[1].StartUtcTicks &&
                identityTwenty.FullName == "worker_2" && !identityTwenty.AgeSeconds.HasValue &&
                !identityTwenty.WorkingSetMiB.HasValue && !identityTwenty.CpuPermille.HasValue &&
                inventory.Items[0].Pid == 20 && inventory.Items[0].CpuPermille == 56 && inventory.Items[1].HasWindow,
                "PROCESS_IDENTITY_ONLY_CLONE");
            Parse(identityOnly.Serialize());
            string longFullName = "PowerSI MixedCase 123 한글 " + new string('界', 220);
            var longNames = new ProcessInventory
            {
                SessionId = 1,
                Items = new[] { new ProcessState
                    { Pid = 30, Name = NormalizeName(longFullName), FullName = longFullName } }
            };
            Need(Parse(longNames.Serialize()).Items[0].FullName == longFullName,
                "PROCESS_FULL_NAME_UNICODE_ROUNDTRIP");
            Need(Parse(new ProcessInventory { SessionId = 0 }.Serialize()).Items.Length == 0,
                "PROCESS_EMPTY_INVENTORY");

            Reject(() => Parse(null));
            Reject(() => Parse(wire + " "));
            Reject(() => Parse(wire.Replace(";20,", ";10,")));
            Reject(() => Parse(wire.Replace(Convert.ToBase64String(StrictUtf8.GetBytes("worker_2")),
                Convert.ToBase64String(StrictUtf8.GetBytes("M234567")))));
            Reject(() => Parse("P2:0:0:0:1;1,@@,d29ya2Vy,-,0,-,-,-"));
            Reject(() => Parse("P2:0:999999:0:1;1,Xw==,Xw==,-,0,-,-,-"));
            Reject(() => Parse(new string('X', MaxWireLength + 1)));
            Reject(() => new ProcessInventory
            {
                SessionId = 1,
                Items = new[] { new ProcessState { Pid = 1, Name = "UPPER" } }
            }.Validate());

            var crowded = new List<ProcessState>();
            for (var i = 1; i <= MaxItems; i++)
                crowded.Add(new ProcessState { Pid = i, Name = "worker", HasWindow = true, CpuPermille = 1000 });
            for (var i = 0; i <= MaxItems; i++)
                crowded.Add(new ProcessState { Pid = MaxItems + i + 1, Name = i % 2 == 0 ? "powersi" : "pwrsi" });
            ProcessInventory filtered = FinishCapture(1, 0, crowded, true);
            Need(filtered.Items.Length == MaxItems && filtered.Omitted == 1 &&
                filtered.Items.All(item => IsPowerSiName(item.Name)), "PROCESS_PWRSI_FILTER_BEFORE_CAP");

            ProcessInventory live = CaptureAsync(CancellationToken.None).GetAwaiter().GetResult();
            live.Validate();
            Parse(live.Serialize());
            using (var cancellation = new CancellationTokenSource(50))
            {
                try
                {
                    CaptureAsync(cancellation.Token).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Process capture ignored cancellation.");
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private static int Compare(ProcessState left, ProcessState right)
        {
            // Busy headless solvers must not fall behind eight idle GUI processes. Unknown CPU remains unknown in output.
            int order = (right.CpuPermille ?? 0).CompareTo(left.CpuPermille ?? 0);
            if (order != 0) return order;
            order = right.HasWindow.CompareTo(left.HasWindow);
            if (order != 0) return order;
            order = CompareNullableDescending(left.WorkingSetMiB, right.WorkingSetMiB);
            return order != 0 ? order : left.Pid.CompareTo(right.Pid);
        }

        private static ProcessInventory FinishCapture(int sessionId, int unreadable,
            List<ProcessState> states, bool powerSiOnly)
        {
            List<ProcessState> selected = powerSiOnly ?
                states.Where(state => IsPowerSiName(state.Name)).ToList() : states;
            selected.Sort(Compare);
            var result = new ProcessInventory
            {
                SessionId = sessionId,
                Unreadable = unreadable,
                Omitted = Math.Max(0, selected.Count - MaxItems),
                Items = selected.Take(MaxItems).ToArray()
            };
            result.Validate();
            return result;
        }

        private static bool SameStartIdentity(long? beforeUtcTicks, long? afterUtcTicks)
        {
            return beforeUtcTicks.HasValue && afterUtcTicks.HasValue &&
                beforeUtcTicks.Value == afterUtcTicks.Value;
        }

        private static void IncrementCount(ref int value)
        {
            if (value >= MaxCount) throw new InvalidDataException("Invalid process inventory.");
            value++;
        }

        private static int CompareNullableDescending<T>(T? left, T? right) where T : struct, IComparable<T>
        {
            if (!left.HasValue) return right.HasValue ? 1 : 0;
            if (!right.HasValue) return -1;
            return right.Value.CompareTo(left.Value);
        }

        private static string Nullable(long? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";
        }

        private static bool TryName(string encoded, out string name)
        {
            name = null;
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 160) return false;
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
                if (Convert.ToBase64String(bytes) != encoded) return false;
                name = StrictUtf8.GetString(bytes);
                return !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && name == NormalizeName(name);
            }
            catch (FormatException) { return false; }
            catch (DecoderFallbackException) { return false; }
        }

        private static string FullNameOf(ProcessState item)
        {
            return item.FullName ?? item.Name;
        }

        private static bool ValidFullName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxFullNameLength || value.Any(char.IsControl)) return false;
            try { StrictUtf8.GetBytes(value); return true; }
            catch (EncoderFallbackException) { return false; }
        }

        private static bool TryFullName(string encoded, out string name)
        {
            name = null;
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 1020) return false;
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
                if (Convert.ToBase64String(bytes) != encoded) return false;
                name = StrictUtf8.GetString(bytes);
                return ValidFullName(name);
            }
            catch (FormatException) { return false; }
            catch (DecoderFallbackException) { return false; }
        }

        private static bool TryInt(string text, int minimum, int maximum, out int value)
        {
            value = 0;
            return CanonicalNumber(text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
                value >= minimum && value <= maximum;
        }

        private static bool TryNullableInt(string text, int maximum, out int value)
        {
            value = 0;
            return text == "-" || TryInt(text, 0, maximum, out value);
        }

        private static bool TryNullableLong(string text, long maximum, out long value)
        {
            value = 0;
            return text == "-" || (CanonicalNumber(text) &&
                long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= maximum);
        }

        private static bool CanonicalNumber(string text)
        {
            if (string.IsNullOrEmpty(text) || (text.Length > 1 && text[0] == '0')) return false;
            return text.All(character => character >= '0' && character <= '9');
        }

        private static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid process inventory was accepted.");
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Process inventory self-test failed: " + reason + ".");
        }
    }
}
