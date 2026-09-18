using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RemoteMonitorLink;

namespace RemoteMonitorMaster
{
    internal sealed class PowerSiOutputDelta
    {
        internal const string First = "FIRST";
        internal const string Appended = "APPENDED";
        internal const string Unchanged = "UNCHANGED";
        internal const string Replaced = "REPLACED";

        internal readonly string Kind;
        internal readonly string Text;

        internal PowerSiOutputDelta(string kind, string text)
        {
            Kind = kind;
            Text = text;
        }
    }

    internal sealed class PreparedPowerSiOutput
    {
        internal readonly PowerSiOutputDelta[] Primary;
        internal readonly PowerSiOutputDelta[] Secondary;

        private readonly Func<string> commit;
        private readonly object sync = new object();
        private bool committing;
        private bool committed;
        internal string FailureReason { get; private set; }

        internal PreparedPowerSiOutput(PowerSiOutputDelta[] primary, PowerSiOutputDelta[] secondary,
            Func<string> commit)
        {
            Primary = primary;
            Secondary = secondary;
            this.commit = commit;
        }

        internal bool Commit()
        {
            lock (sync)
            {
                if (committed || committing) return false;
                committing = true;
            }

            string failure;
            try { failure = commit(); }
            catch (Exception ex) { failure = "UNEXPECTED_" + ex.GetType().Name; }
            bool result = failure == null;
            lock (sync)
            {
                committing = false;
                if (result) committed = true;
                FailureReason = failure;
            }
            return result;
        }
    }

    internal sealed class PowerSiOutputHistory
    {
        private const string FileVersion = "PWRH1";
        private const int MaxEntries = 1024;
        private const int MaxStateBytes = 256 * 1024;
        private const string PrimaryLane = "P";
        private const string SecondaryLane = "S";
        private const string DirectFamily = "DIRECT";
        private const string OcrFamily = "OCR";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Mutex StateMutex =
            new Mutex(false, @"Local\RemoteMonitorMaster.PowerSiOutputHistory.v1");

        internal static readonly PowerSiOutputHistory Shared = new PowerSiOutputHistory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteMonitorMaster", "state", "powersi-output-history-v1.txt"));

        private readonly string statePath;

        internal PowerSiOutputHistory(string statePath)
        {
            if (string.IsNullOrWhiteSpace(statePath) || string.IsNullOrEmpty(Path.GetDirectoryName(statePath)))
                throw new ArgumentException("PowerSI output history path must include a directory.", "statePath");
            this.statePath = Path.GetFullPath(statePath);
        }

        internal PreparedPowerSiOutput Prepare(string slavePin, PowerSiReport report, Action checkPreparation = null)
        {
            if (string.IsNullOrEmpty(slavePin)) throw new ArgumentException("Slave pin is required.", "slavePin");
            if (report == null || report.Targets == null || report.SessionId < 0)
                throw new ArgumentException("A valid PowerSI report is required.", "report");

            checkPreparation?.Invoke();
            Snapshot snapshot = ReadSnapshotWithLock();
            checkPreparation?.Invoke();
            string pinHash = Hash(slavePin);
            var primary = new PowerSiOutputDelta[report.Targets.Length];
            var secondary = new PowerSiOutputDelta[report.Targets.Length];
            var mutations = new List<Mutation>();

            for (var index = 0; index < report.Targets.Length; index++)
            {
                checkPreparation?.Invoke();
                PowerSiTargetReport target = report.Targets[index];
                if (!CapturedTarget(target)) continue;

                string family = Family(target.Source);
                if (family != null && target.OutputText != null)
                    primary[index] = Stage(snapshot, pinHash, report.SessionId, target, PrimaryLane,
                        family, target.OutputText, mutations);

                if (family == DirectFamily && target.OcrText != null && target.OcrCapturedUtc.HasValue)
                    secondary[index] = Stage(snapshot, pinHash, report.SessionId, target, SecondaryLane,
                        OcrFamily, target.OcrText, mutations);
            }

            checkPreparation?.Invoke();

            return new PreparedPowerSiOutput(primary, secondary,
                () => CommitState(snapshot.Revision, mutations));
        }

        private static bool CapturedTarget(PowerSiTargetReport target)
        {
            return target != null && target.Pid > 0 && target.StartUtcTicks.HasValue &&
                target.StartUtcTicks.Value > 0 && target.CapturedUtc.HasValue &&
                (target.State == "READ" || target.State == "VISIBLE_EMPTY");
        }

        private static string Family(string source)
        {
            if (source == "BUFFER" || source == "AUTO_COPY") return DirectFamily;
            return source == "OCR" ? OcrFamily : null;
        }

        private static PowerSiOutputDelta Stage(Snapshot snapshot, string pinHash, int sessionId,
            PowerSiTargetReport target, string lane, string family, string rawText, List<Mutation> mutations)
        {
            string text = Normalize(rawText);
            string baseKey = string.Join("|", pinHash,
                sessionId.ToString(CultureInfo.InvariantCulture),
                target.Pid.ToString(CultureInfo.InvariantCulture),
                target.StartUtcTicks.Value.ToString(CultureInfo.InvariantCulture), lane);
            string key = baseKey + "|" + family;
            Entry prior;
            string kind;
            string deltaText;

            if (snapshot.Entries.TryGetValue(key, out prior))
            {
                string digest = Hash(text);
                if (prior.Length == text.Length && prior.Digest == digest)
                {
                    kind = PowerSiOutputDelta.Unchanged;
                    deltaText = string.Empty;
                }
                else if (text.Length >= prior.Length && WholeCharacterBoundary(text, prior.Length) &&
                    Hash(text.Substring(0, prior.Length)) == prior.Digest)
                {
                    kind = PowerSiOutputDelta.Appended;
                    deltaText = text.Substring(prior.Length);
                }
                else
                {
                    kind = PowerSiOutputDelta.Replaced;
                    deltaText = text;
                }
            }
            else
            {
                bool changedFamily = snapshot.Entries.Values.Any(item => item.BaseKey == baseKey);
                kind = changedFamily ? PowerSiOutputDelta.Replaced : PowerSiOutputDelta.First;
                deltaText = text;
            }

            mutations.Add(new Mutation(baseKey, key, text.Length, Hash(text), pinHash, sessionId,
                target.Pid, target.StartUtcTicks.Value, lane, family));
            return new PowerSiOutputDelta(kind, deltaText);
        }

        private string CommitState(string expectedRevision, IList<Mutation> mutations)
        {
            if (mutations.Count == 0) return null;
            bool ownsMutex = false;
            try
            {
                ownsMutex = WaitForMutex();
                if (!ownsMutex) return "LOCK_TIMEOUT";
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        Snapshot current = ReadSnapshot();
                        if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
                            return "STALE_REVISION";

                        long sequence = current.Entries.Count == 0 ? 0 :
                            current.Entries.Values.Max(item => item.Sequence);
                        foreach (Mutation mutation in mutations)
                        {
                            foreach (string oldKey in current.Entries.Values
                                .Where(item => item.BaseKey == mutation.BaseKey && item.Key != mutation.Key)
                                .Select(item => item.Key).ToArray())
                                current.Entries.Remove(oldKey);
                            current.Entries[mutation.Key] = mutation.ToEntry(++sequence);
                        }

                        foreach (string oldKey in current.Entries.Values.OrderByDescending(item => item.Sequence)
                            .Skip(MaxEntries).Select(item => item.Key).ToArray())
                            current.Entries.Remove(oldKey);
                        return Save(current.Entries.Values) ? null : "STATE_TOO_LARGE";
                    }
                    catch (IOException ex)
                    {
                        if (attempt == 2)
                            return "IO_" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture);
                        Thread.Sleep((attempt + 1) * 10);
                    }
                }
                return "IO_RETRY_EXHAUSTED";
            }
            catch (UnauthorizedAccessException ex)
            { return "ACCESS_" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture); }
            finally
            {
                if (ownsMutex) StateMutex.ReleaseMutex();
            }
        }

        private Snapshot ReadSnapshotWithLock()
        {
            bool ownsMutex = false;
            try
            {
                ownsMutex = WaitForMutex();
                return ownsMutex ? ReadSnapshot() : Snapshot.Failed("LOCK_TIMEOUT");
            }
            catch (IOException) { return Snapshot.Failed("READ_ERROR"); }
            catch (UnauthorizedAccessException) { return Snapshot.Failed("READ_ERROR"); }
            finally
            {
                if (ownsMutex) StateMutex.ReleaseMutex();
            }
        }

        private static bool WaitForMutex()
        {
            try { return StateMutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { return true; }
        }

        private Snapshot ReadSnapshot()
        {
            if (!File.Exists(statePath)) return new Snapshot("MISSING", new Dictionary<string, Entry>(StringComparer.Ordinal));
            var info = new FileInfo(statePath);
            if (info.Length < 0 || info.Length > MaxStateBytes)
                return Snapshot.Failed("OVERSIZE:" + info.Length.ToString(CultureInfo.InvariantCulture) + ":" +
                    info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));

            byte[] bytes = File.ReadAllBytes(statePath);
            string digest = Hash(bytes);
            try
            {
                if (bytes.Any(value => value > 0x7f)) throw new InvalidDataException();
                string[] lines = Encoding.ASCII.GetString(bytes).Replace("\r\n", "\n").Split('\n');
                if (lines.Length == 0 || lines[0] != FileVersion) throw new InvalidDataException();
                var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                for (var index = 1; index < lines.Length; index++)
                {
                    if (lines[index].Length == 0)
                    {
                        if (index != lines.Length - 1) throw new InvalidDataException();
                        continue;
                    }
                    Entry entry = ParseEntry(lines[index]);
                    if (entries.Count >= MaxEntries || entries.ContainsKey(entry.Key)) throw new InvalidDataException();
                    entries.Add(entry.Key, entry);
                }
                return new Snapshot("VALID:" + digest, entries);
            }
            catch (InvalidDataException)
            {
                return Snapshot.Failed("CORRUPT:" + digest);
            }
            catch (OverflowException)
            {
                return Snapshot.Failed("CORRUPT:" + digest);
            }
        }

        private static Entry ParseEntry(string line)
        {
            string[] fields = line.Split('\t');
            long sequence, startTicks;
            int sessionId, pid, length;
            if (fields.Length != 9 || !PositiveLong(fields[0], out sequence) || !Hex(fields[1]) ||
                !NonNegativeInt(fields[2], out sessionId) || !PositiveInt(fields[3], out pid) ||
                !PositiveLong(fields[4], out startTicks) ||
                (fields[5] != PrimaryLane && fields[5] != SecondaryLane) ||
                (fields[6] != DirectFamily && fields[6] != OcrFamily) ||
                !NonNegativeInt(fields[7], out length) || !Hex(fields[8]))
                throw new InvalidDataException();
            if (fields[5] == SecondaryLane && fields[6] != OcrFamily) throw new InvalidDataException();
            return new Entry(sequence, fields[1], sessionId, pid, startTicks, fields[5], fields[6],
                length, fields[8]);
        }

        private bool Save(IEnumerable<Entry> entries)
        {
            string directory = Path.GetDirectoryName(statePath);
            Directory.CreateDirectory(directory);
            string temporaryPath = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var text = new StringBuilder(FileVersion).Append('\n');
                foreach (Entry entry in entries.OrderBy(item => item.Sequence).ThenBy(item => item.Key,
                    StringComparer.Ordinal))
                {
                    text.Append(entry.Sequence.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(entry.PinHash).Append('\t')
                        .Append(entry.SessionId.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(entry.Pid.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(entry.StartTicks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(entry.Lane).Append('\t').Append(entry.Family).Append('\t')
                        .Append(entry.Length.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(entry.Digest).Append('\n');
                }
                byte[] bytes = Encoding.ASCII.GetBytes(text.ToString());
                if (bytes.Length > MaxStateBytes) return false;
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(statePath)) File.Replace(temporaryPath, statePath, null);
                else File.Move(temporaryPath, statePath);
                return true;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string Normalize(string value)
        {
            string normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
            try { StrictUtf8.GetByteCount(normalized); }
            catch (EncoderFallbackException) { throw new InvalidDataException("PowerSI output is not valid Unicode."); }
            return normalized;
        }

        private static bool WholeCharacterBoundary(string value, int length)
        {
            return length == 0 || length == value.Length ||
                !(char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]));
        }

        private static string Hash(string value) { return Hash(StrictUtf8.GetBytes(value)); }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var result = new StringBuilder(64);
                foreach (byte value in sha.ComputeHash(bytes))
                    result.Append(value.ToString("X2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private static bool Hex(string value)
        {
            return value != null && value.Length == 64 && value.All(character =>
                (character >= '0' && character <= '9') || (character >= 'A' && character <= 'F'));
        }

        private static bool PositiveLong(string value, out long result)
        {
            result = 0;
            return Canonical(value) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                out result) && result > 0;
        }

        private static bool PositiveInt(string value, out int result)
        {
            result = 0;
            return Canonical(value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                out result) && result > 0;
        }

        private static bool NonNegativeInt(string value, out int result)
        {
            result = 0;
            return Canonical(value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                out result) && result >= 0;
        }

        private static bool Canonical(string value)
        {
            return !string.IsNullOrEmpty(value) && (value.Length == 1 || value[0] != '0') &&
                value.All(character => character >= '0' && character <= '9');
        }

        private sealed class Snapshot
        {
            internal readonly string Revision;
            internal readonly Dictionary<string, Entry> Entries;

            internal Snapshot(string revision, Dictionary<string, Entry> entries)
            {
                Revision = revision;
                Entries = entries;
            }

            internal static Snapshot Failed(string revision)
            {
                return new Snapshot(revision, new Dictionary<string, Entry>(StringComparer.Ordinal));
            }
        }

        private sealed class Entry
        {
            internal readonly long Sequence;
            internal readonly string PinHash;
            internal readonly int SessionId;
            internal readonly int Pid;
            internal readonly long StartTicks;
            internal readonly string Lane;
            internal readonly string Family;
            internal readonly int Length;
            internal readonly string Digest;
            internal string BaseKey { get { return string.Join("|", PinHash,
                SessionId.ToString(CultureInfo.InvariantCulture), Pid.ToString(CultureInfo.InvariantCulture),
                StartTicks.ToString(CultureInfo.InvariantCulture), Lane); } }
            internal string Key { get { return BaseKey + "|" + Family; } }

            internal Entry(long sequence, string pinHash, int sessionId, int pid, long startTicks,
                string lane, string family, int length, string digest)
            {
                Sequence = sequence;
                PinHash = pinHash;
                SessionId = sessionId;
                Pid = pid;
                StartTicks = startTicks;
                Lane = lane;
                Family = family;
                Length = length;
                Digest = digest;
            }
        }

        private sealed class Mutation
        {
            internal readonly string BaseKey;
            internal readonly string Key;
            private readonly int length;
            private readonly string digest;
            private readonly string pinHash;
            private readonly int sessionId;
            private readonly int pid;
            private readonly long startTicks;
            private readonly string lane;
            private readonly string family;

            internal Mutation(string baseKey, string key, int length, string digest, string pinHash,
                int sessionId, int pid, long startTicks, string lane, string family)
            {
                BaseKey = baseKey;
                Key = key;
                this.length = length;
                this.digest = digest;
                this.pinHash = pinHash;
                this.sessionId = sessionId;
                this.pid = pid;
                this.startTicks = startTicks;
                this.lane = lane;
                this.family = family;
            }

            internal Entry ToEntry(long sequence)
            {
                return new Entry(sequence, pinHash, sessionId, pid, startTicks, lane, family, length, digest);
            }
        }

        internal static void RunSelfTest()
        {
            string folder = Path.Combine(Path.GetTempPath(), "RemoteMonitorMaster-output-history-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, "state.txt");
            Directory.CreateDirectory(folder);
            try
            {
                var history = new PowerSiOutputHistory(path);
                long start = new DateTime(2026, 9, 18, 1, 2, 3, DateTimeKind.Utc).Ticks;
                PowerSiTargetReport target = Target(101, start, "BUFFER", "one\r\n\uD83D\uDE00 par");

                PreparedPowerSiOutput prepared = history.Prepare("slave-a", Report(7, target));
                NeedDelta(prepared.Primary[0], PowerSiOutputDelta.First, "one\n\uD83D\uDE00 par", "FIRST");
                NeedCommit(prepared, "FIRST_COMMIT");
                Need(!File.ReadAllText(path).Contains("one"), "RAW_TEXT_PERSISTED");

                target.OutputText = "one\n\uD83D\uDE00 partial\rnext";
                prepared = history.Prepare("slave-a", Report(7, target));
                NeedDelta(prepared.Primary[0], PowerSiOutputDelta.Appended, "tial\nnext", "APPENDED_UNICODE_PARTIAL_LINE");
                NeedCommit(prepared, "APPENDED_COMMIT");

                prepared = history.Prepare("slave-a", Report(7, target));
                NeedDelta(prepared.Primary[0], PowerSiOutputDelta.Unchanged, string.Empty, "UNCHANGED");
                NeedCommit(prepared, "UNCHANGED_COMMIT");

                target.OutputText = "replacement";
                prepared = history.Prepare("slave-a", Report(7, target));
                NeedDelta(prepared.Primary[0], PowerSiOutputDelta.Replaced, "replacement", "CHANGED_PREFIX");
                NeedCommit(prepared, "REPLACED_COMMIT");

                target.Source = "OCR";
                prepared = history.Prepare("slave-a", Report(7, target));
                NeedDelta(prepared.Primary[0], PowerSiOutputDelta.Replaced, "replacement", "SOURCE_CHANGE");
                NeedCommit(prepared, "SOURCE_CHANGE_COMMIT");

                NeedDelta(history.Prepare("slave-a", Report(7, Target(101, start + 1, "OCR", "replacement"))).Primary[0],
                    PowerSiOutputDelta.First, "replacement", "PID_REUSE");
                NeedDelta(history.Prepare("slave-a", Report(8, target)).Primary[0],
                    PowerSiOutputDelta.First, "replacement", "SESSION_CHANGE");
                NeedDelta(history.Prepare("slave-b", Report(7, target)).Primary[0],
                    PowerSiOutputDelta.First, "replacement", "SLAVE_CHANGE");

                prepared = new PowerSiOutputHistory(path).Prepare("slave-a", Report(7, target));
                NeedDelta(prepared.Primary[0], PowerSiOutputDelta.Unchanged, string.Empty, "RESTART");

                target.Source = "BUFFER";
                target.OutputText = "replacement plus";
                prepared = history.Prepare("slave-a", Report(7, target));
                NeedDelta(prepared.Primary[0], PowerSiOutputDelta.Replaced, "replacement plus", "SOURCE_RETURN");
                PreparedPowerSiOutput canceled = history.Prepare("slave-a", Report(7,
                    Target(101, start, "BUFFER", "replacement plus later")));
                NeedDelta(canceled.Primary[0], PowerSiOutputDelta.Replaced, "replacement plus later", "UNCOMMITTED_STAGE");
                NeedCommit(prepared, "SOURCE_RETURN_COMMIT");
                Need(!canceled.Commit() && canceled.FailureReason == "STALE_REVISION", "STALE_COMMIT_ACCEPTED");
                NeedDelta(history.Prepare("slave-a", Report(7,
                    Target(101, start, "BUFFER", "replacement plus later"))).Primary[0],
                    PowerSiOutputDelta.Appended, " later", "CANCELED_DID_NOT_ADVANCE");

                var surrogateBoundary = Target(111, start + 11, "BUFFER", "a");
                prepared = history.Prepare("slave-a", Report(7, surrogateBoundary));
                NeedCommit(prepared, "SURROGATE_BOUNDARY_BASELINE_COMMIT");
                surrogateBoundary.OutputText = "\uD83D\uDE00x";
                NeedDelta(history.Prepare("slave-a", Report(7, surrogateBoundary)).Primary[0],
                    PowerSiOutputDelta.Replaced, "\uD83D\uDE00x", "SURROGATE_PREFIX_BOUNDARY");

                var pending = Target(101, start, "NONE", null);
                pending.State = "PENDING";
                pending.CapturedUtc = null;
                prepared = history.Prepare("slave-a", Report(7, pending));
                Need(prepared.Primary[0] == null && prepared.Secondary[0] == null,
                    "PENDING_PREPARED_OUTPUT");
                NeedCommit(prepared, "PENDING_CHANGED_BASELINE");
                NeedDelta(history.Prepare("slave-a", Report(7,
                    Target(101, start, "BUFFER", "replacement plus"))).Primary[0],
                    PowerSiOutputDelta.Unchanged, string.Empty, "PENDING_ERASED_BASELINE");

                var empty = Target(202, start + 2, "BUFFER", string.Empty);
                empty.State = "VISIBLE_EMPTY";
                prepared = history.Prepare("slave-a", Report(7, empty));
                NeedDelta(prepared.Primary[0], PowerSiOutputDelta.First, string.Empty, "EMPTY_FIRST");
                NeedCommit(prepared, "EMPTY_FIRST_COMMIT");
                NeedDelta(history.Prepare("slave-a", Report(7, empty)).Primary[0],
                    PowerSiOutputDelta.Unchanged, string.Empty, "EMPTY_UNCHANGED");
                empty.State = "READ";
                empty.OutputText = "new";
                prepared = history.Prepare("slave-a", Report(7, empty));
                NeedDelta(prepared.Primary[0], PowerSiOutputDelta.Appended, "new", "EMPTY_TO_TEXT");
                NeedCommit(prepared, "EMPTY_TO_TEXT_COMMIT");
                empty.State = "VISIBLE_EMPTY";
                empty.OutputText = string.Empty;
                prepared = history.Prepare("slave-a", Report(7, empty));
                NeedDelta(prepared.Primary[0], PowerSiOutputDelta.Replaced, string.Empty, "CLEARED");
                NeedCommit(prepared, "CLEARED_COMMIT");
                NeedDelta(history.Prepare("slave-a", Report(7, empty)).Primary[0],
                    PowerSiOutputDelta.Unchanged, string.Empty, "CLEARED_UNCHANGED");

                var dual = Target(303, start + 3, "AUTO_COPY", "direct");
                dual.OcrText = "ocr";
                dual.OcrCapturedUtc = dual.CapturedUtc;
                prepared = history.Prepare("slave-a", Report(7, dual));
                NeedDelta(prepared.Primary[0], PowerSiOutputDelta.First, "direct", "DUAL_PRIMARY");
                NeedDelta(prepared.Secondary[0], PowerSiOutputDelta.First, "ocr", "DUAL_SECONDARY");
                NeedCommit(prepared, "DUAL_COMMIT");

                File.WriteAllText(path, "corrupt private-looking text", Encoding.UTF8);
                prepared = new PowerSiOutputHistory(path).Prepare("slave-a", Report(7, target));
                NeedDelta(prepared.Primary[0], PowerSiOutputDelta.First, "replacement plus", "CORRUPT_FULL_RESEND");
                NeedCommit(prepared, "CORRUPT_REPAIR_COMMIT");
                NeedDelta(new PowerSiOutputHistory(path).Prepare("slave-a", Report(7, target)).Primary[0],
                    PowerSiOutputDelta.Unchanged, string.Empty, "CORRUPT_RESTART");
            }
            finally
            {
                try { Directory.Delete(folder, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static PowerSiReport Report(int sessionId, PowerSiTargetReport target)
        {
            return new PowerSiReport
            {
                CapturedUtc = new DateTime(2026, 9, 18, 2, 0, 0, DateTimeKind.Utc),
                SessionId = sessionId,
                Code = "OK",
                Targets = new[] { target }
            };
        }

        private static PowerSiTargetReport Target(int pid, long startTicks, string source, string output)
        {
            return new PowerSiTargetReport
            {
                Pid = pid,
                StartUtcTicks = startTicks,
                ProcessName = "PowerSI",
                CapturedUtc = new DateTime(2026, 9, 18, 1, 30, 0, DateTimeKind.Utc),
                State = output == null ? "UNAVAILABLE" : "READ",
                Source = source,
                Code = output == null ? "READ_FAILED" : "READ_OK",
                BufferCode = source == "BUFFER" || source == "AUTO_COPY" ? "READ_OK" : "NOT_USED",
                VisionCode = source == "OCR" ? "READ_OK" : "NOT_USED",
                OutputText = output
            };
        }

        private static void NeedDelta(PowerSiOutputDelta actual, string kind, string text, string reason)
        {
            Need(actual != null && actual.Kind == kind && actual.Text == text, reason);
        }

        private static void NeedCommit(PreparedPowerSiOutput prepared, string reason)
        {
            Need(prepared.Commit(), reason + "_" + (prepared.FailureReason ?? "UNKNOWN"));
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("PowerSI output history self-test failed: " + reason + ".");
        }
    }
}
