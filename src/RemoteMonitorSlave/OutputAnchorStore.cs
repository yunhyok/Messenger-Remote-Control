using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RemoteMonitorLink;

namespace RemoteMonitorSlave
{
    // API: the LLM-free aim of the vision-failure Output copy fallback (SlaveForm.ReadOutputBuffer).
    //
    // OutputAnchorEntry
    //   One vision-confirmed A2 OutputAnchor (PID, start time, session, exact client size, click point, body) plus the
    //   normalized PowerSI process name and a continuity fingerprint of the text that copy produced: L = min(length,
    //   4096) UTF-16 code units and the uppercase hex SHA-256 of those L code units as raw UTF-16LE bytes (each char
    //   as low byte, high byte; no encoder, so a surrogate pair split at L or a lone surrogate hashes exactly instead
    //   of becoming U+FFFD). Output text itself is never stored.
    //   Line: "OA1|<A2 anchor, 13 fields>|<L>|<HEX64>|<savedUtcTicks>|<powersi|pwrsi>" (the name is the layout key).
    //
    // OutputAnchorStore(path)       Loads path (null = memory only). Never throws: corrupt lines are ignored and
    //                               counted (InvalidLines); an unreadable file leaves the store empty (LastFileError).
    //   TryGetProcessAnchor(pid, start, session, clientSize, out entry)    route 1 (ANCHOR): exact process + size.
    //   TryGetLayoutAnchor(name, clientSize, pid, out entry)               route 3 (LAYOUT): newest entry of ANY other
    //                                                                      PID with the same name and client size.
    //   HasProcessEntry(pid, start, session)      Any stored position of this process, whatever its client size.
    //   Store(entry)        Only after a vision-confirmed clean copy. One entry per process; at most 128 (oldest
    //                       dropped); atomic temp + replace rewrite. Returns false when the file write failed.
    //   Drop(pid, start, reason)        Removes that process's entry after a fallback proved it wrong.
    //   PruneMissing(session, living, complete)   Removes this session's entries whose process is gone, only when
    //                                             the inventory is complete.
    //   static CheckContinuity(entry, text)       The new text starts with the stored prefix (hash of its first L).
    //   static DecideFallbackRoute(...)           "ANCHOR" | "SCOPE" | "LAYOUT" | null, in that priority. With vision
    //                                             off the cause is VISION_NOT_CONFIGURED (no model call at all).
    //   static DecideBlindFallback(...)           Vision off: whether a target is worth a capture for those routes.
    //   static OffersLayout(scope)                LAYOUT only beside this window's own Output scope rectangle.
    //   static LocateFailureCode(observation)     The model failure that permits a fallback, null on today's paths.
    //   static DropsEntry / FallsThrough / IsTransient(code)   Failure handling table of one fallback copy.
    //   static Rekey(stored, target, clientSize)  A LAYOUT anchor re-keyed to the current process.
    //   static FallbackDetail(...)                "FB|VISION=..|ANCHOR=..|SCOPE=..|LAYOUT=.." for OutputBufferResult.Detail.
    internal sealed class OutputAnchorEntry
    {
        internal OutputAnchor Anchor;
        internal string ProcessName;
        internal int PrefixLength;
        internal string PrefixHash;
        internal DateTime SavedUtc;

        internal string Serialize()
        {
            return string.Join("|", "OA1", Anchor.Serialize(), PrefixLength.ToString(CultureInfo.InvariantCulture), PrefixHash,
                SavedUtc.Ticks.ToString(CultureInfo.InvariantCulture), ProcessName);
        }

        internal static bool TryParse(string line, out OutputAnchorEntry entry)
        {
            entry = null;
            try
            {
                if (line == null || line.Length > 400) return false;
                var parts = line.Split('|');
                if (parts.Length != 18 || parts[0] != "OA1") return false;
                var anchorText = string.Join("|", parts, 1, 13);
                OutputAnchor anchor;
                int length;
                long saved;
                if (!OutputAnchor.TryParse(anchorText, out anchor) || !anchor.HasBody || anchor.Serialize() != anchorText ||
                    !int.TryParse(parts[14], NumberStyles.None, CultureInfo.InvariantCulture, out length) ||
                    length.ToString(CultureInfo.InvariantCulture) != parts[14] ||
                    length < 1 || length > OutputAnchorStore.ContinuityCharacters ||
                    !Regex.IsMatch(parts[15], @"\A[0-9A-F]{64}\z") ||
                    !long.TryParse(parts[16], NumberStyles.None, CultureInfo.InvariantCulture, out saved) ||
                    saved.ToString(CultureInfo.InvariantCulture) != parts[16] || saved < 1 || saved > DateTime.MaxValue.Ticks ||
                    !ProcessInventory.IsPowerSiName(parts[17])) return false;
                entry = new OutputAnchorEntry { Anchor = anchor, ProcessName = parts[17], PrefixLength = length,
                    PrefixHash = parts[15], SavedUtc = new DateTime(saved, DateTimeKind.Utc) };
                return true;
            }
            catch { entry = null; return false; }
        }

        // Null unless the anchor is a valid A2, the name is a PowerSI name and the copied text is not empty.
        internal static OutputAnchorEntry Create(OutputAnchor anchor, string processName, string copiedText, DateTime savedUtc)
        {
            try
            {
                if (anchor == null || !anchor.HasBody || string.IsNullOrEmpty(copiedText) ||
                    !ProcessInventory.IsPowerSiName(processName) || savedUtc.Kind != DateTimeKind.Utc || savedUtc.Ticks < 1) return null;
                int length = Math.Min(copiedText.Length, OutputAnchorStore.ContinuityCharacters);
                var entry = new OutputAnchorEntry { Anchor = anchor, ProcessName = processName, PrefixLength = length,
                    PrefixHash = OutputAnchorStore.PrefixHash(copiedText, length), SavedUtc = savedUtc };
                OutputAnchorEntry parsed;
                return TryParse(entry.Serialize(), out parsed) ? parsed : null;
            }
            catch { return null; }
        }
    }

    internal sealed class OutputAnchorStore
    {
        internal const string FileName = "output-anchors-v1.txt";
        internal const int MaxEntries = 128;
        internal const int ContinuityCharacters = 4096;
        private const long MaxFileBytes = 256 * 1024; // 128 lines of at most 400 characters, with room to spare.

        // Every model failure that may be followed by an LLM-free copy. VISION_CAPTURE_FAILED is excluded on purpose:
        // it carries a window/capture SC_* failure, not a model failure, and must not be followed by input.
        private static readonly string[] VisionFailures = { "VISION_MODEL_UNAVAILABLE", "VISION_SERVER_UNAVAILABLE",
            "VISION_TIMEOUT", "VISION_INVALID_RESPONSE", "OUTPUT_UNAVAILABLE", "OUTPUT_REGION_UNCONFIRMED", "VISION_BUSY",
            "VISION_MODEL_AMBIGUOUS", "VISION_AUTH_REQUIRED", "VISION_NOT_CONFIGURED", "VISION_FAILED" };
        // Failures that prove the aimed position wrong: the stored process entry is dropped (ANCHOR route only).
        private static readonly string[] DropCodes = { "AUTO_COPY_BODY_MOVED", "AUTO_COPY_BODY_UNCONFIRMED",
            "AUTO_COPY_WINDOW_CHANGED", "AUTO_COPY_ANCHOR_CONTINUITY", "AUTO_COPY_OUTSIDE_SCOPE" };
        // Refusals before any worker input; the next route may be tried as well.
        private static readonly string[] PreInputCodes = { "AUTO_COPY_SCOPE_UNCONFIRMED", "AUTO_COPY_REQUEST_INVALID",
            "AUTO_COPY_ANCHOR_STALE" };

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly object gate = new object();
        private readonly string path;
        private readonly List<OutputAnchorEntry> entries = new List<OutputAnchorEntry>();
        private int invalidLines;
        private string lastFileError;

        internal OutputAnchorStore(string path)
        {
            this.path = path;
            lock (gate) Load();
        }

        internal int Count { get { lock (gate) return entries.Count; } }
        internal int InvalidLines { get { lock (gate) return invalidLines; } }
        // A fixed code (STORE_<EXCEPTION> or STORE_TOO_LARGE) after the last failed read/write, null after a success.
        internal string LastFileError { get { lock (gate) return lastFileError; } }

        internal bool TryGetProcessAnchor(int pid, long startUtcTicks, int sessionId, Size clientSize, out OutputAnchorEntry entry)
        {
            lock (gate)
                entry = entries.Where(item => item.Anchor.Pid == pid && item.Anchor.StartUtcTicks == startUtcTicks &&
                    item.Anchor.SessionId == sessionId && item.Anchor.ClientSize == clientSize)
                    .OrderByDescending(item => item.SavedUtc).FirstOrDefault();
            return entry != null;
        }

        // Whatever the client size: decides only whether a vision-off target may have an ANCHOR aim before its capture.
        internal bool HasProcessEntry(int pid, long startUtcTicks, int sessionId)
        {
            lock (gate)
                return entries.Any(item => item.Anchor.Pid == pid && item.Anchor.StartUtcTicks == startUtcTicks &&
                    item.Anchor.SessionId == sessionId);
        }

        // Never this PID's own entry: route 3 borrows another PowerSI's confirmed geometry, route 1 owns this one.
        internal bool TryGetLayoutAnchor(string processName, Size clientSize, int pid, out OutputAnchorEntry entry)
        {
            lock (gate)
                entry = !ProcessInventory.IsPowerSiName(processName) ? null : entries.Where(item => item.ProcessName == processName &&
                    item.Anchor.ClientSize == clientSize && item.Anchor.Pid != pid)
                    .OrderByDescending(item => item.SavedUtc).FirstOrDefault();
            return entry != null;
        }

        internal bool Store(OutputAnchorEntry entry)
        {
            OutputAnchorEntry parsed;
            if (entry == null || entry.Anchor == null || !OutputAnchorEntry.TryParse(entry.Serialize(), out parsed)) return false;
            lock (gate)
            {
                entries.RemoveAll(item => SameProcess(item, parsed));
                entries.Add(parsed);
                Trim();
                return Persist();
            }
        }

        internal int Drop(int pid, long startUtcTicks, string reason)
        {
            lock (gate)
            {
                int removed = entries.RemoveAll(item => item.Anchor.Pid == pid && item.Anchor.StartUtcTicks == startUtcTicks);
                if (removed > 0)
                {
                    LastDropReason = OutputBufferCapture.SafeCode(reason) ? reason : "NONE";
                    Persist();
                }
                return removed;
            }
        }

        internal string LastDropReason { get; private set; }

        // Only this session's entries are judged: the inventory never lists another session's processes.
        internal int PruneMissing(int sessionId, IEnumerable<KeyValuePair<int, long>> living, bool inventoryComplete)
        {
            if (!inventoryComplete || living == null) return 0;
            var alive = new HashSet<KeyValuePair<int, long>>(living);
            lock (gate)
            {
                int removed = entries.RemoveAll(item => item.Anchor.SessionId == sessionId &&
                    !alive.Contains(new KeyValuePair<int, long>(item.Anchor.Pid, item.Anchor.StartUtcTicks)));
                if (removed > 0) Persist();
                return removed;
            }
        }

        internal static bool CheckContinuity(OutputAnchorEntry entry, string newText)
        {
            if (entry == null || string.IsNullOrEmpty(newText) || entry.PrefixLength < 1 ||
                entry.PrefixLength > ContinuityCharacters || newText.Length < entry.PrefixLength) return false;
            return string.Equals(PrefixHash(newText, entry.PrefixLength), entry.PrefixHash, StringComparison.Ordinal);
        }

        internal static string PrefixHash(string text, int length)
        {
            var bytes = new byte[length * 2];
            for (int index = 0; index < length; index++)
            {
                bytes[index * 2] = (byte)text[index];
                bytes[index * 2 + 1] = (byte)(text[index] >> 8);
            }
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        // Today's success paths (a located body, a visibly empty pane) and non-model failures yield null.
        internal static string LocateFailureCode(PowerSiObservation located)
        {
            if (located == null || located.LocalVisibleEmpty) return null;
            if (located.Code == "OUTPUT_UNAVAILABLE" && located.LocalFailure == null) return null;
            return located.Code;
        }

        // The cause once the model answered but no copy position came of it: the model's own failure code (a region whose
        // Output boundary PowerSiVision could not confirm is already OUTPUT_REGION_UNCONFIRMED, its LocalFailure
        // CROP_OUTPUT_REGION_BOUNDARY_UNCONFIRMED), or OUTPUT_REGION_UNCONFIRMED as well for a success-shaped locate whose
        // region AnchorFromVision refused. Null only for no observation or a visibly empty pane (no input then).
        internal static string RejectedLocateCode(PowerSiObservation located)
        {
            if (located == null || located.LocalVisibleEmpty) return null;
            return LocateFailureCode(located) ?? "OUTPUT_REGION_UNCONFIRMED";
        }

        internal static bool IsVisionFailure(string code)
        {
            return code != null && VisionFailures.Contains(code, StringComparer.Ordinal);
        }

        // Vision off is no longer a refusal: with auto copy allowed the same routes run, their cause being
        // VISION_NOT_CONFIGURED (the only code a target without a model call can have). Auto copy off still refuses.
        internal static string DecideFallbackRoute(bool visionEnabled, bool autoCopyEnabled, bool copyAllowed, string locateCode,
            bool processAnchor, bool scopeRect, bool layoutAnchor)
        {
            if (!autoCopyEnabled || !copyAllowed || !IsVisionFailure(locateCode)) return null;
            if (!visionEnabled && locateCode != "VISION_NOT_CONFIGURED") return null;
            return processAnchor ? "ANCHOR" : scopeRect ? "SCOPE" : layoutAnchor ? "LAYOUT" : null;
        }

        // Vision off, before any capture (the client size that keys every aim is not known yet): only a read failure that
        // permits input, with this process's stored position or the buffer read's Output scope rectangle, is worth the
        // activation and capture. LAYOUT needs that scope rectangle as well (OffersLayout), so it adds nothing here.
        internal static bool DecideBlindFallback(bool visionEnabled, bool autoCopyEnabled, bool copyAllowed, bool processEntry,
            bool scopeRect)
        {
            return !visionEnabled && autoCopyEnabled && copyAllowed && (processEntry || scopeRect);
        }

        // LAYOUT borrows another window's stored point: offered only when this window's own Output scope rectangle is
        // known, so the borrowed point must also fall inside it (InsideScope). A same-size window of another product
        // (a PowerDC window shares the executable name) can then never aim this one's copy at another pane.
        internal static bool OffersLayout(Rectangle scope) { return !scope.IsEmpty; }

        internal static bool DropsEntry(string code) { return code != null && DropCodes.Contains(code, StringComparer.Ordinal); }

        // Only a failure that proved the aim wrong, or a refusal before any input, lets the next route run.
        // Everything else (interference, timeouts, the 8 Mi clipboard cap, worker failures) ends the fallback.
        internal static bool FallsThrough(string code)
        {
            return DropsEntry(code) || (code != null && PreInputCodes.Contains(code, StringComparer.Ordinal));
        }

        // Interference by the operator or another window: the entry stays and the chain stops.
        internal static bool IsTransient(string code)
        {
            return code == "AUTO_COPY_CURSOR_MOVED" || code == "AUTO_COPY_INPUT_BUSY" || code == "AUTO_COPY_OCCLUDED" ||
                code == "AUTO_COPY_NO_CLIPBOARD" || (code != null && code.StartsWith("AUTO_COPY_FOREGROUND_", StringComparison.Ordinal));
        }

        // The stored point/body of another PowerSI, keyed to the current process. Null unless the client size matches
        // exactly and the result is a valid A2 anchor. The worker still re-finds the body before any input.
        internal static OutputAnchor Rekey(OutputAnchor stored, ProcessInventory target, Size clientSize)
        {
            try
            {
                if (stored == null || !stored.HasBody || target == null || stored.ClientSize != clientSize) return null;
                target.Validate();
                if (target.Items.Length != 1 || target.Omitted != 0) return null;
                var identity = target.Items[0];
                if (!ProcessInventory.IsPowerSiName(identity.Name) || !identity.StartUtcTicks.HasValue || identity.StartUtcTicks.Value < 1)
                    return null;
                var anchor = new OutputAnchor { Pid = identity.Pid, StartUtcTicks = identity.StartUtcTicks.Value,
                    SessionId = target.SessionId, ClientPoint = stored.ClientPoint, ClientSize = stored.ClientSize,
                    LearnedUtc = stored.LearnedUtc, Body = stored.Body };
                OutputAnchor parsed;
                return OutputAnchor.TryParse(anchor.Serialize(), out parsed) && parsed.Matches(anchor) && parsed.Body == anchor.Body
                    ? parsed : null;
            }
            catch { return null; }
        }

        // When the Output scope HWND rectangle is known, a stored click point outside it aims at another pane.
        internal static bool InsideScope(OutputAnchor anchor, Rectangle scope)
        {
            if (anchor == null || !anchor.HasBody) return false;
            if (scope.IsEmpty) return true;
            return scope.Contains(new Point(anchor.Body.X + anchor.Body.Width / 2, anchor.Body.Y + anchor.Body.Height / 2));
        }

        internal static string FallbackDetail(string visionCode, string anchorCode, string scopeCode, string layoutCode)
        {
            string Code(string value) => value == null ? "NONE" : OutputBufferCapture.SafeCode(value) ? value : "INVALID";
            return "FB|VISION=" + Code(visionCode) + "|ANCHOR=" + Code(anchorCode) + "|SCOPE=" + Code(scopeCode) +
                "|LAYOUT=" + Code(layoutCode);
        }

        internal static string RouteCaption(string route)
        {
            switch (route)
            {
                case "ANCHOR": return "저장 위치";
                case "SCOPE": return "Output 창 영역";
                case "LAYOUT": return "같은 크기 창의 저장 위치";
                default: return "대체 위치";
            }
        }

        private static bool SameProcess(OutputAnchorEntry left, OutputAnchorEntry right)
        {
            return left.Anchor.Pid == right.Anchor.Pid && left.Anchor.StartUtcTicks == right.Anchor.StartUtcTicks &&
                left.Anchor.SessionId == right.Anchor.SessionId;
        }

        private void Trim()
        {
            while (entries.Count > MaxEntries)
            {
                var oldest = entries.OrderBy(item => item.SavedUtc).First();
                entries.Remove(oldest);
            }
        }

        private void Load()
        {
            if (path == null) return;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return;
                if (info.Length > MaxFileBytes) { lastFileError = "STORE_TOO_LARGE"; return; }
                foreach (var line in File.ReadAllLines(path, StrictUtf8))
                {
                    OutputAnchorEntry entry;
                    if (!OutputAnchorEntry.TryParse(line, out entry)) { invalidLines++; continue; }
                    var existing = entries.FirstOrDefault(item => SameProcess(item, entry));
                    if (existing != null)
                    {
                        invalidLines++; // A second line for one process is not a valid file; keep the newer one.
                        if (existing.SavedUtc >= entry.SavedUtc) continue;
                        entries.Remove(existing);
                    }
                    entries.Add(entry);
                }
                Trim();
            }
            catch (Exception error) { entries.Clear(); lastFileError = ErrorCode(error); }
        }

        private bool Persist()
        {
            if (path == null) return true;
            var temp = path + ".tmp";
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var text = new StringBuilder();
                foreach (var entry in entries.OrderBy(item => item.SavedUtc)) text.Append(entry.Serialize()).Append("\r\n");
                File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
                // A reader sees either the previous complete file or the new one, never a partial rewrite.
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                lastFileError = null;
                return true;
            }
            catch (Exception error)
            {
                lastFileError = ErrorCode(error);
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                return false;
            }
        }

        private static string ErrorCode(Exception error)
        {
            var name = new string((error?.GetType().Name ?? "ERROR").ToUpperInvariant().Where(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')).Take(40).ToArray());
            return "STORE_" + (name.Length == 0 ? "ERROR" : name);
        }

        // ---------------------------------------------------------------- self-test (pure + temp-file I/O; no Win32)

        internal static void SelfTest()
        {
            void Need(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException("Output anchor store self-test: " + name);
            }
            var learned = new DateTime(2026, 10, 3, 1, 2, 3, DateTimeKind.Utc);
            var size = new Size(1920, 1009);
            var body = new Rectangle(317, 393, 585, 560);
            OutputAnchor Anchor(int pid, long start, int session, Size client)
            {
                return new OutputAnchor { Pid = pid, StartUtcTicks = start, SessionId = session,
                    ClientPoint = new Point(body.X + body.Width / 2, body.Y + body.Height / 2), ClientSize = client,
                    LearnedUtc = learned, Body = body };
            }
            const string Text = "AFS Current Frequency ( MHz ) = 38.000\r\nTotal Sampling Points = 12\r\n";
            var first = OutputAnchorEntry.Create(Anchor(4321, 638000000000000000L, 2, size), "powersi", Text, learned.AddMinutes(1));
            Need(first != null && first.PrefixLength == Text.Length && first.PrefixHash.Length == 64, "entry created from a copy");
            var line = first.Serialize();
            Need(line.StartsWith("OA1|A2|4321|638000000000000000|2|609|673|1920|1009|", StringComparison.Ordinal) &&
                line.EndsWith("|" + learned.AddMinutes(1).Ticks.ToString(CultureInfo.InvariantCulture) + "|powersi", StringComparison.Ordinal) &&
                !line.Contains("38.000") && !line.Contains("AFS"), "line format carries no Output text");
            OutputAnchorEntry parsed;
            Need(OutputAnchorEntry.TryParse(line, out parsed) && parsed.Serialize() == line && parsed.Anchor.Matches(first.Anchor) &&
                parsed.Anchor.Body == body && parsed.SavedUtc.Kind == DateTimeKind.Utc, "entry round trip");
            var anchorText = first.Anchor.Serialize();
            var a1 = new OutputAnchor { Pid = 4321, StartUtcTicks = 638000000000000000L, SessionId = 2, ClientPoint = new Point(5, 5),
                ClientSize = size, LearnedUtc = learned }.Serialize();
            foreach (var invalid in new[] { null, "", line + "|", line.Replace("OA1|", "OA2|"), line.Replace("|powersi", "|PowerSI"),
                line.Replace("|powersi", "|other"), "OA1|" + a1 + "|1|" + first.PrefixHash + "|1|powersi",
                "OA1|" + anchorText + "|0|" + first.PrefixHash + "|1|powersi", "OA1|" + anchorText + "|4097|" + first.PrefixHash + "|1|powersi",
                "OA1|" + anchorText + "|01|" + first.PrefixHash + "|1|powersi", "OA1|" + anchorText + "|1|" + first.PrefixHash.ToLowerInvariant() + "|1|powersi",
                "OA1|" + anchorText + "|1|" + first.PrefixHash.Substring(1) + "|1|powersi", "OA1|" + anchorText + "|1|" + first.PrefixHash + "|0|powersi",
                "OA1|" + anchorText + "|1|" + first.PrefixHash + "|+1|powersi", " " + line, line + " ", line.Replace("|2|609|", "|2|609 |") })
            {
                OutputAnchorEntry rejected;
                Need(!OutputAnchorEntry.TryParse(invalid, out rejected) && rejected == null, "strict parse rejects: " + (invalid ?? "null"));
            }
            Need(OutputAnchorEntry.Create(null, "powersi", Text, learned) == null &&
                OutputAnchorEntry.Create(Anchor(1, 1, 1, size), "notepad", Text, learned) == null &&
                OutputAnchorEntry.Create(Anchor(1, 1, 1, size), "powersi", "", learned) == null &&
                OutputAnchorEntry.Create(new OutputAnchor { Pid = 1, StartUtcTicks = 1, SessionId = 1, ClientPoint = new Point(1, 1),
                    ClientSize = size, LearnedUtc = learned }, "powersi", Text, learned) == null, "only a valid A2 copy becomes an entry");

            // Continuity: the hash of the first L UTF-16 code units, exact even for a surrogate pair split at L.
            Need(CheckContinuity(first, Text) && CheckContinuity(first, Text + "Simulation finished.\r\n") &&
                !CheckContinuity(first, Text.Substring(0, Text.Length - 1)) && !CheckContinuity(first, "") &&
                !CheckContinuity(first, null) && !CheckContinuity(first, "X" + Text.Substring(1)) && !CheckContinuity(null, Text),
                "continuity: prefix, shorter, empty, changed");
            var longText = new string('a', ContinuityCharacters - 1) + "\U0001F600" + "tail";
            var split = OutputAnchorEntry.Create(Anchor(1, 1, 1, size), "pwrsi", longText, learned);
            Need(split.PrefixLength == ContinuityCharacters && char.IsHighSurrogate(longText[ContinuityCharacters - 1]) &&
                CheckContinuity(split, longText) && CheckContinuity(split, longText.Substring(0, ContinuityCharacters) + "\uDE01 other") &&
                // U+20000 starts with another high surrogate (D840, not D83D); UTF-8 would turn both halves into U+FFFD.
                !CheckContinuity(split, new string('a', ContinuityCharacters - 1) + "\U00020000" + "tail") &&
                !CheckContinuity(split, longText.Substring(0, ContinuityCharacters - 1)), "continuity at a split surrogate pair");
            Need(PrefixHash("\uD800", 1) != PrefixHash("\uD801", 1) && PrefixHash("\uD800", 1) != PrefixHash("�", 1),
                "lone surrogates hash as raw code units, never as U+FFFD");

            // Lookup, layout, prune, drop and the 128-entry cap, all in memory.
            var memory = new OutputAnchorStore(null);
            var second = OutputAnchorEntry.Create(Anchor(5555, 638000000000000001L, 2, size), "powersi", "other", learned.AddMinutes(2));
            var third = OutputAnchorEntry.Create(Anchor(6666, 638000000000000002L, 2, new Size(1280, 1009)), "powersi", "x", learned.AddMinutes(3));
            var fourth = OutputAnchorEntry.Create(Anchor(7777, 638000000000000003L, 2, size), "pwrsi", "y", learned.AddMinutes(4));
            Need(memory.Store(first) && memory.Store(second) && memory.Store(third) && memory.Store(fourth) && memory.Count == 4, "store");
            OutputAnchorEntry found;
            Need(memory.TryGetProcessAnchor(4321, 638000000000000000L, 2, size, out found) && found.Anchor.Matches(first.Anchor), "process key match");
            Need(memory.HasProcessEntry(4321, 638000000000000000L, 2) && memory.HasProcessEntry(6666, 638000000000000002L, 2) &&
                !memory.HasProcessEntry(4321, 638000000000000009L, 2) && !memory.HasProcessEntry(4321, 638000000000000000L, 3) &&
                !memory.HasProcessEntry(4322, 638000000000000000L, 2), "any-size process entry needs PID, start and session");
            Need(!memory.TryGetProcessAnchor(4321, 638000000000000009L, 2, size, out found) && found == null &&
                !memory.TryGetProcessAnchor(4321, 638000000000000000L, 3, size, out found) &&
                !memory.TryGetProcessAnchor(4321, 638000000000000000L, 2, new Size(1920, 1008), out found) &&
                !memory.TryGetProcessAnchor(4322, 638000000000000000L, 2, size, out found), "process key needs PID, start, session and exact size");
            Need(memory.TryGetLayoutAnchor("powersi", size, 9999, out found) && found.Anchor.Pid == 5555, "layout: newest other PID, same name and size");
            Need(memory.TryGetLayoutAnchor("powersi", size, 5555, out found) && found.Anchor.Pid == 4321, "layout never returns the same PID");
            Need(!memory.TryGetLayoutAnchor("powersi", new Size(1000, 1000), 9999, out found) &&
                !memory.TryGetLayoutAnchor("other", size, 9999, out found) &&
                memory.TryGetLayoutAnchor("pwrsi", size, 9999, out found) && found.Anchor.Pid == 7777 &&
                !memory.TryGetLayoutAnchor("pwrsi", size, 7777, out found), "layout: size and name must match exactly");
            var replaced = OutputAnchorEntry.Create(Anchor(4321, 638000000000000000L, 2, size), "powersi", "new", learned.AddMinutes(9));
            Need(memory.Store(replaced) && memory.Count == 4 && memory.TryGetProcessAnchor(4321, 638000000000000000L, 2, size, out found) &&
                found.PrefixLength == 3, "one entry per process; a newer vision copy replaces it");
            var living = new[] { new KeyValuePair<int, long>(4321, 638000000000000000L), new KeyValuePair<int, long>(6666, 638000000000000002L) };
            Need(memory.PruneMissing(2, living, false) == 0 && memory.Count == 4, "partial inventory never prunes");
            Need(memory.PruneMissing(3, living, true) == 0 && memory.Count == 4, "another session's inventory never prunes");
            Need(memory.PruneMissing(2, living, true) == 2 && memory.Count == 2 &&
                !memory.TryGetLayoutAnchor("pwrsi", size, 1, out found), "complete inventory prunes exited processes");
            Need(memory.Drop(4321, 638000000000000000L, "AUTO_COPY_BODY_MOVED") == 1 && memory.Count == 1 &&
                memory.LastDropReason == "AUTO_COPY_BODY_MOVED" && memory.Drop(4321, 638000000000000000L, "AUTO_COPY_BODY_MOVED") == 0, "drop");
            for (int index = 0; index < MaxEntries + 5; index++)
                memory.Store(OutputAnchorEntry.Create(Anchor(10000 + index, 638000000000000000L + index, 2, size), "powersi", "z",
                    learned.AddHours(1).AddSeconds(index)));
            Need(memory.Count == MaxEntries && !memory.TryGetProcessAnchor(10000, 638000000000000000L, 2, size, out found) &&
                memory.TryGetProcessAnchor(10000 + MaxEntries + 4, 638000000000000000L + MaxEntries + 4, 2, size, out found),
                "128-entry cap drops the oldest");

            // Failure handling table of one fallback copy.
            foreach (var code in new[] { "AUTO_COPY_BODY_MOVED", "AUTO_COPY_BODY_UNCONFIRMED", "AUTO_COPY_WINDOW_CHANGED",
                "AUTO_COPY_ANCHOR_CONTINUITY", "AUTO_COPY_OUTSIDE_SCOPE" })
                Need(DropsEntry(code) && FallsThrough(code) && !IsTransient(code), "drop and fall through: " + code);
            foreach (var code in new[] { "AUTO_COPY_CURSOR_MOVED", "AUTO_COPY_INPUT_BUSY", "AUTO_COPY_OCCLUDED",
                "AUTO_COPY_FOREGROUND_FAILED", "AUTO_COPY_FOREGROUND_LOST", "AUTO_COPY_NO_CLIPBOARD" })
                Need(!DropsEntry(code) && !FallsThrough(code) && IsTransient(code), "transient keeps and stops: " + code);
            foreach (var code in new[] { "AUTO_COPY_TIMEOUT", "AUTO_COPY_WORKER_FAILED", "AUTO_COPY_CLIPBOARD_SIZE",
                "AUTO_COPY_CLIPBOARD_FOREIGN", "AUTO_COPY_CLICK_FAILED", "AUTO_COPY_CANCELLED", "SC_WINDOW_CHANGED", null })
                Need(!DropsEntry(code) && !FallsThrough(code) && !IsTransient(code), "other failure keeps and stops: " + (code ?? "null"));
            foreach (var code in new[] { "AUTO_COPY_SCOPE_UNCONFIRMED", "AUTO_COPY_REQUEST_INVALID", "AUTO_COPY_ANCHOR_STALE" })
                Need(!DropsEntry(code) && FallsThrough(code), "pre-input refusal falls through without a drop: " + code);

            // Route decision table.
            foreach (var code in VisionFailures)
            {
                Need(DecideFallbackRoute(true, true, true, code, true, true, true) == "ANCHOR" &&
                    DecideFallbackRoute(true, true, true, code, false, true, true) == "SCOPE" &&
                    DecideFallbackRoute(true, true, true, code, false, false, true) == "LAYOUT" &&
                    DecideFallbackRoute(true, true, true, code, false, false, false) == null, "route priority: " + code);
                // Vision off runs the routes only for its own cause, VISION_NOT_CONFIGURED; a model code needs a model call.
                Need(DecideFallbackRoute(false, true, true, code, true, true, true) == (code == "VISION_NOT_CONFIGURED" ? "ANCHOR" : null) &&
                    DecideFallbackRoute(true, false, true, code, true, true, true) == null &&
                    DecideFallbackRoute(true, true, false, code, true, true, true) == null, "disabled or refused copy: " + code);
            }
            foreach (var code in new[] { null, "", "OUTPUT_READ", "VISION_CAPTURE_FAILED", "SC_PENDING", "NOT_OBSERVED", "vision_timeout" })
                Need(DecideFallbackRoute(true, true, true, code, true, true, true) == null &&
                    DecideFallbackRoute(false, true, true, code, true, true, true) == null, "no fallback after: " + (code ?? "null"));
            // Vision off or not configured (no model call): the same priority while auto copy is allowed; auto copy off,
            // or a read failure that forbids input, still refuses every route.
            const string NotConfigured = "VISION_NOT_CONFIGURED";
            Need(DecideFallbackRoute(false, true, true, NotConfigured, true, true, true) == "ANCHOR" &&
                DecideFallbackRoute(false, true, true, NotConfigured, false, true, true) == "SCOPE" &&
                DecideFallbackRoute(false, true, true, NotConfigured, false, false, true) == "LAYOUT" &&
                DecideFallbackRoute(false, true, true, NotConfigured, false, false, false) == null &&
                DecideFallbackRoute(true, true, true, NotConfigured, false, true, false) == "SCOPE", "vision off: route priority");
            Need(DecideFallbackRoute(false, false, true, NotConfigured, true, true, true) == null &&
                DecideFallbackRoute(false, false, true, NotConfigured, false, true, false) == null &&
                DecideFallbackRoute(false, true, false, NotConfigured, true, true, true) == null &&
                DecideFallbackRoute(false, false, false, NotConfigured, true, true, true) == null, "vision off: auto copy off or input refused");
            // Vision off, before the capture: worth it only with an own stored position or the Output scope rectangle.
            Need(DecideBlindFallback(false, true, true, true, false) && DecideBlindFallback(false, true, true, false, true) &&
                DecideBlindFallback(false, true, true, true, true) && !DecideBlindFallback(false, true, true, false, false) &&
                !DecideBlindFallback(false, false, true, true, true) && !DecideBlindFallback(false, true, false, true, true) &&
                !DecideBlindFallback(true, true, true, true, true), "vision off: capture only with a possible aim");
            var success = PowerSiObservation.VisionLogExcerpt(null, learned);
            var empty = PowerSiObservation.VisionLogExcerpt(null, learned); empty.LocalVisibleEmpty = true;
            var unreadable = PowerSiObservation.VisionUnavailable("OUTPUT_UNAVAILABLE"); unreadable.LocalFailure = "LOCATE_OUTPUT_OUTPUT_UNREADABLE";
            var busy = PowerSiObservation.VisionUnavailable("VISION_BUSY");
            Need(LocateFailureCode(success) == null && LocateFailureCode(empty) == null && LocateFailureCode(null) == null &&
                LocateFailureCode(unreadable) == "OUTPUT_UNAVAILABLE" && LocateFailureCode(busy) == "VISION_BUSY", "locate failure code");
            // A model region refused by the crop boundary check, or a success-shaped locate refused by AnchorFromVision, is
            // one model failure for the routes: OUTPUT_REGION_UNCONFIRMED (never AUTO_COPY_REGION_UNCONFIRMED as VisionCode).
            var cropRefused = PowerSiObservation.VisionUnavailable("OUTPUT_REGION_UNCONFIRMED");
            cropRefused.LocalFailure = "CROP_OUTPUT_REGION_BOUNDARY_UNCONFIRMED";
            Need(RejectedLocateCode(cropRefused) == "OUTPUT_REGION_UNCONFIRMED" && RejectedLocateCode(success) == "OUTPUT_REGION_UNCONFIRMED" &&
                RejectedLocateCode(busy) == "VISION_BUSY" && RejectedLocateCode(unreadable) == "OUTPUT_UNAVAILABLE" &&
                RejectedLocateCode(empty) == null && RejectedLocateCode(null) == null && IsVisionFailure("OUTPUT_REGION_UNCONFIRMED") &&
                !IsVisionFailure("AUTO_COPY_REGION_UNCONFIRMED"), "rejected locate code");
            Need(DecideFallbackRoute(true, true, true, RejectedLocateCode(cropRefused), false, true, true) == "SCOPE" &&
                DecideFallbackRoute(true, true, true, RejectedLocateCode(success), true, true, true) == "ANCHOR" &&
                DecideFallbackRoute(true, true, true, RejectedLocateCode(cropRefused), false, false, false) == null &&
                DecideFallbackRoute(true, true, true, RejectedLocateCode(empty), true, true, true) == null, "rejected region runs the routes");

            // LAYOUT re-keying and the scope guard.
            var target = new ProcessInventory { SessionId = 3, Items = new[] { new ProcessState { Pid = 8888, Name = "powersi",
                FullName = "PowerSI", StartUtcTicks = 638000000000000077L } } };
            var rekeyed = Rekey(second.Anchor, target, size);
            Need(rekeyed != null && rekeyed.Pid == 8888 && rekeyed.StartUtcTicks == 638000000000000077L && rekeyed.SessionId == 3 &&
                rekeyed.Body == body && rekeyed.ClientPoint == second.Anchor.ClientPoint && rekeyed.ClientSize == size, "layout re-key");
            Need(Rekey(second.Anchor, target, new Size(1920, 1010)) == null && Rekey(null, target, size) == null &&
                Rekey(second.Anchor, new ProcessInventory { SessionId = 3, Items = new[] { new ProcessState { Pid = 8888, Name = "powersi",
                    FullName = "PowerSI" } } }, size) == null, "re-key needs an exact size and a full identity");
            Need(InsideScope(rekeyed, Rectangle.Empty) && InsideScope(rekeyed, new Rectangle(300, 380, 700, 600)) &&
                !InsideScope(rekeyed, new Rectangle(0, 0, 300, 300)) && !InsideScope(null, Rectangle.Empty), "scope guard");
            // LAYOUT is offered only beside this window's own known scope (cross-product contamination guard); then the
            // borrowed point must also fall inside that scope.
            Need(!OffersLayout(Rectangle.Empty) && OffersLayout(new Rectangle(300, 380, 700, 600)) &&
                OffersLayout(new Rectangle(0, 0, 300, 300)) && !InsideScope(rekeyed, new Rectangle(0, 0, 300, 300)), "layout scope guard");
            var detail = FallbackDetail("VISION_TIMEOUT", "AUTO_COPY_BODY_MOVED", null, "AUTO_COPY_READ");
            Need(detail == "FB|VISION=VISION_TIMEOUT|ANCHOR=AUTO_COPY_BODY_MOVED|SCOPE=NONE|LAYOUT=AUTO_COPY_READ" &&
                Regex.IsMatch(detail, @"\A[A-Z0-9_:=,| -]{1,512}\z") &&
                FallbackDetail("bad code", null, null, null) == "FB|VISION=INVALID|ANCHOR=NONE|SCOPE=NONE|LAYOUT=NONE", "chain detail");

            // File: atomic rewrite, strict reload, corrupt lines ignored and counted, no Output text on disk.
            var directory = Path.Combine(Path.GetTempPath(), "RemoteMonitorSlave-anchors-" + Guid.NewGuid().ToString("N"));
            try
            {
                var file = Path.Combine(directory, FileName);
                var disk = new OutputAnchorStore(file);
                Need(disk.Count == 0 && disk.LastFileError == null && disk.InvalidLines == 0, "missing file is an empty store");
                Need(disk.Store(first) && disk.Store(second) && File.Exists(file) && !File.Exists(file + ".tmp") &&
                    disk.LastFileError == null, "persisted with temp + replace");
                var content = File.ReadAllText(file);
                Need(!content.Contains("38.000") && !content.Contains("other") && content.Split(new[] { "\r\n" }, StringSplitOptions.None).Length == 3,
                    "file holds one line per entry and no text");
                var reloaded = new OutputAnchorStore(file);
                Need(reloaded.Count == 2 && reloaded.InvalidLines == 0 && reloaded.TryGetProcessAnchor(4321, 638000000000000000L, 2, size, out found) &&
                    found.Serialize() == first.Serialize(), "reload");
                File.AppendAllText(file, "garbage\r\nOA1|A2|1\r\n" + first.Serialize().Replace("|powersi", "|other") + "\r\n\r\n");
                var corrupt = new OutputAnchorStore(file);
                Need(corrupt.Count == 2 && corrupt.InvalidLines == 4 && corrupt.LastFileError == null, "corrupt lines ignored and counted");
                File.WriteAllBytes(file, new byte[] { 0x4F, 0x41, 0x31, 0xFF, 0xFE });
                var invalidUtf8 = new OutputAnchorStore(file);
                Need(invalidUtf8.Count == 0 && invalidUtf8.LastFileError != null && invalidUtf8.LastFileError.StartsWith("STORE_", StringComparison.Ordinal),
                    "invalid UTF-8 file leaves an empty store with an error code");
                Need(invalidUtf8.Store(first) && invalidUtf8.LastFileError == null && new OutputAnchorStore(file).Count == 1, "rewrite recovers the file");
            }
            finally { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { } }
            Console.WriteLine("PASS: output anchor store (format, continuity, keys, layout, prune, drop table, route table incl. vision off, layout scope guard, file)");

            // Report projection of each route and of an all-failed chain (SlaveForm owns the projection).
            SlaveForm.FallbackProjectionSelfTest();
        }
    }
}
