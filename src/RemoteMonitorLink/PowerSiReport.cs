using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RemoteMonitorLink
{
    internal sealed class PowerSiTargetReport
    {
        internal int Pid;
        internal long? StartUtcTicks;
        internal string ProcessName;
        internal DateTime? CapturedUtc;
        internal string State;
        internal string Source;
        internal string Code;
        internal string Summary;
        internal string Excerpt;
        internal bool Truncated;

        internal void Validate()
        {
            string summary = Summary ?? string.Empty;
            string excerpt = Excerpt ?? string.Empty;
            if (Pid <= 0 || !PowerSiReport.ValidStartTicks(StartUtcTicks) ||
                !PowerSiReport.ValidText(ProcessName, 1, ProcessInventory.MaxFullNameLength, false) ||
                !PowerSiReport.ValidUtc(CapturedUtc) || !PowerSiReport.ValidState(State) ||
                !PowerSiReport.ValidSource(Source) || !PowerSiReport.ValidCode(Code) ||
                !PowerSiReport.ValidText(summary, 0, PowerSiReport.MaxSummaryLength, true) ||
                !PowerSiReport.ValidExcerpt(excerpt))
                throw new InvalidDataException("Invalid PowerSI target report.");

            bool capturedState = State == "READ" || State == "PENDING" || State == "VISIBLE_EMPTY";
            if ((capturedState && !StartUtcTicks.HasValue) ||
                (State == "READ" && (!CapturedUtc.HasValue || Source == "NONE" ||
                    string.IsNullOrWhiteSpace(excerpt))) ||
                (State == "PENDING" && (CapturedUtc.HasValue || Source != "NONE" ||
                    summary.Length != 0 || excerpt.Length != 0 || Truncated)) ||
                (State == "VISIBLE_EMPTY" && (!CapturedUtc.HasValue || Source == "NONE" ||
                    excerpt.Length != 0 || Truncated)) ||
                ((State == "UNAVAILABLE" || State == "TIMEOUT" || State == "NOT_ATTEMPTED") &&
                    (Source != "NONE" || excerpt.Length != 0 || Truncated)) ||
                (Truncated && State != "READ"))
                throw new InvalidDataException("Inconsistent PowerSI target report.");
        }
    }

    internal sealed class PowerSiReport
    {
        internal const int CollectionBudgetMilliseconds = 100000;
        internal const int RequestDeadlineMilliseconds = 110000;
        internal const int MaxTargets = 128;
        internal const int MaxExcerptLength = 600;
        internal const int MaxExcerptLines = 5;
        internal const int MaxSummaryLength = 240;
        internal const int MaxCodeLength = 48;
        internal const int MaxWireLength = 600000;

        private const int MaxCount = 999999;
        private const string WireVersion = "PS3";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly string[] States =
            { "READ", "PENDING", "VISIBLE_EMPTY", "UNAVAILABLE", "TIMEOUT", "NOT_ATTEMPTED" };
        private static readonly string[] Sources = { "BUFFER", "AUTO_COPY", "OCR", "NONE" };

        internal DateTime CapturedUtc;
        internal int SessionId;
        internal int Omitted;
        internal int Unreadable;
        internal bool Partial;
        internal string Code = "OK";
        internal PowerSiTargetReport[] Targets = new PowerSiTargetReport[0];

        internal void Validate()
        {
            if (!ValidUtc(CapturedUtc) || SessionId < 0 || Omitted < 0 || Unreadable < 0 ||
                Omitted > MaxCount || Unreadable > MaxCount || !ValidCode(Code) ||
                Targets == null || Targets.Length > MaxTargets || Targets.Length + Omitted > MaxCount ||
                (Code != "OK" && !Partial))
                throw new InvalidDataException("Invalid PowerSI report.");

            var pids = new HashSet<int>();
            foreach (PowerSiTargetReport target in Targets)
            {
                if (target == null || !pids.Add(target.Pid))
                    throw new InvalidDataException("Invalid PowerSI target identity.");
                target.Validate();
            }
        }

        internal string Serialize()
        {
            Validate();
            var text = new StringBuilder();
            text.Append(WireVersion).Append(':')
                .Append(CapturedUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(SessionId.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(Omitted.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(Unreadable.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(Partial ? '1' : '0').Append(':').Append(Code).Append(':')
                .Append(Targets.Length.ToString(CultureInfo.InvariantCulture));
            foreach (PowerSiTargetReport target in Targets)
            {
                text.Append(';').Append(target.Pid.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Nullable(target.StartUtcTicks)).Append(',').Append(Encode(target.ProcessName)).Append(',')
                    .Append(target.CapturedUtc.HasValue ?
                        target.CapturedUtc.Value.Ticks.ToString(CultureInfo.InvariantCulture) : "-").Append(',')
                    .Append(target.State).Append(',').Append(target.Source).Append(',').Append(target.Code).Append(',')
                    .Append(EncodeOptional(target.Summary)).Append(',').Append(EncodeOptional(target.Excerpt)).Append(',')
                    .Append(target.Truncated ? '1' : '0');
            }
            string wire = text.ToString();
            if (wire.Length > MaxWireLength || wire.IndexOf('|') >= 0 || !PrintableAscii(wire))
                throw new InvalidDataException("Invalid PowerSI report.");
            return wire;
        }

        internal static PowerSiReport Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxWireLength || text.IndexOf('|') >= 0 ||
                !PrintableAscii(text))
                throw new InvalidDataException("Invalid PowerSI report.");

            string[] records = text.Split(';');
            string[] header = records[0].Split(':');
            long capturedTicks;
            int sessionId, omitted, unreadable, count;
            if (header.Length != 8 || header[0] != WireVersion ||
                !TryLong(header[1], DateTime.MaxValue.Ticks, out capturedTicks) ||
                !TryInt(header[2], 0, int.MaxValue, out sessionId) ||
                !TryInt(header[3], 0, MaxCount, out omitted) ||
                !TryInt(header[4], 0, MaxCount, out unreadable) ||
                (header[5] != "0" && header[5] != "1") || !ValidCode(header[6]) ||
                !TryInt(header[7], 0, MaxTargets, out count) || records.Length != count + 1)
                throw new InvalidDataException("Invalid PowerSI report.");

            var targets = new PowerSiTargetReport[count];
            for (var i = 0; i < count; i++)
            {
                string[] fields = records[i + 1].Split(',');
                int pid;
                long startTicks, targetCapturedTicks;
                string processName, summary, excerpt;
                if (fields.Length != 10 || !TryInt(fields[0], 1, int.MaxValue, out pid) ||
                    !TryNullableLong(fields[1], DateTime.MaxValue.Ticks, out startTicks) ||
                    !TryText(fields[2], 1, ProcessInventory.MaxFullNameLength, false, out processName) ||
                    !TryNullableLong(fields[3], DateTime.MaxValue.Ticks, out targetCapturedTicks) ||
                    !ValidState(fields[4]) || !ValidSource(fields[5]) || !ValidCode(fields[6]) ||
                    !TryText(fields[7], 0, MaxSummaryLength, true, out summary) ||
                    !TryText(fields[8], 0, MaxExcerptLength, false, out excerpt) ||
                    (fields[9] != "0" && fields[9] != "1"))
                    throw new InvalidDataException("Invalid PowerSI target report.");

                targets[i] = new PowerSiTargetReport
                {
                    Pid = pid,
                    StartUtcTicks = fields[1] == "-" ? (long?)null : startTicks,
                    ProcessName = processName,
                    CapturedUtc = fields[3] == "-" ? (DateTime?)null :
                        new DateTime(targetCapturedTicks, DateTimeKind.Utc),
                    State = fields[4],
                    Source = fields[5],
                    Code = fields[6],
                    Summary = summary,
                    Excerpt = excerpt,
                    Truncated = fields[9] == "1"
                };
            }

            var result = new PowerSiReport
            {
                CapturedUtc = new DateTime(capturedTicks, DateTimeKind.Utc),
                SessionId = sessionId,
                Omitted = omitted,
                Unreadable = unreadable,
                Partial = header[5] == "1",
                Code = header[6],
                Targets = targets
            };
            result.Validate();
            if (result.Serialize() != text) throw new InvalidDataException("Invalid PowerSI report.");
            return result;
        }

        internal static string LatestExcerpt(string text, out bool truncated)
        {
            truncated = false;
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            string normalized = NormalizeSourceText(text);
            string[] lines = normalized.Split(new[] { '\n' }, StringSplitOptions.None);
            int last = lines.Length - 1;
            while (last >= 0 && string.IsNullOrWhiteSpace(lines[last])) last--;
            if (last < 0) return string.Empty;

            int first = Math.Max(0, last + 1 - MaxExcerptLines);
            truncated = first != 0 || last != lines.Length - 1;
            string excerpt = string.Join("\n", lines.Skip(first).Take(last - first + 1));
            if (excerpt.Length > MaxExcerptLength)
            {
                int end = excerpt.Length;
                int start = end - MaxExcerptLength;
                if (!excerpt.Skip(start).Any(character => !char.IsWhiteSpace(character)))
                {
                    while (end > 0 && char.IsWhiteSpace(excerpt[end - 1])) end--;
                    start = Math.Max(0, end - MaxExcerptLength);
                }
                if (start > 0 && char.IsLowSurrogate(excerpt[start]) && char.IsHighSurrogate(excerpt[start - 1]))
                    start++;
                excerpt = excerpt.Substring(start, end - start);
                truncated = true;
            }
            return excerpt;
        }

        internal static void RunSelfTest()
        {
            bool truncated;
            string excerpt = LatestExcerpt("old1\r\nold2\r\nKeep3 003\r\nKeep4 Aa\r\nKeep5 한글\r\nKeep6 006\r\nKeep7 Z", out truncated);
            Need(truncated && excerpt == "Keep3 003\nKeep4 Aa\nKeep5 한글\nKeep6 006\nKeep7 Z",
                "REPORT_LATEST_FIVE_LINES");
            string longExcerpt = LatestExcerpt("😀" + new string('x', MaxExcerptLength + 10), out truncated);
            Need(truncated && longExcerpt.Length == MaxExcerptLength && !char.IsLowSurrogate(longExcerpt[0]),
                "REPORT_LATEST_600_CHARACTERS");
            Need(LatestExcerpt(" \r\n\t ", out truncated) == string.Empty && !truncated,
                "REPORT_EMPTY_EXCERPT");
            string paddedExcerpt = LatestExcerpt("log line\r\n\r\n \t\r\n\r\n\r\n\r\n", out truncated);
            Need(truncated && paddedExcerpt == "log line", "REPORT_TRAILING_BLANK_PADDING");
            string paddedTail = LatestExcerpt("relevant" + new string(' ', MaxExcerptLength + 10), out truncated);
            Need(truncated && paddedTail == "relevant", "REPORT_TRAILING_WHITESPACE_LIMIT");
            string internalBlanks = LatestExcerpt("one\n\nthree\n\nfive\n\n", out truncated);
            Need(truncated && internalBlanks == "one\n\nthree\n\nfive", "REPORT_INTERNAL_BLANK_LINES");

            string fullName = "PowerSI Case 123 한글 " + new string('界', 220);
            var sample = new PowerSiReport
            {
                CapturedUtc = new DateTime(2026, 9, 16, 1, 2, 3, DateTimeKind.Utc),
                SessionId = 7,
                Omitted = 1,
                Unreadable = 2,
                Partial = true,
                Targets = new[]
                {
                    new PowerSiTargetReport
                    {
                        Pid = 101,
                        StartUtcTicks = new DateTime(2026, 9, 15, 1, 0, 0, DateTimeKind.Utc).Ticks,
                        ProcessName = fullName,
                        CapturedUtc = new DateTime(2026, 9, 16, 1, 1, 1, DateTimeKind.Utc),
                        State = "READ",
                        Source = "AUTO_COPY",
                        Code = "AUTO_COPY_READ",
                        Summary = "Exact local output excerpt.",
                        Excerpt = excerpt,
                        Truncated = true
                    },
                    new PowerSiTargetReport
                    {
                        Pid = 202,
                        StartUtcTicks = new DateTime(2026, 9, 15, 2, 0, 0, DateTimeKind.Utc).Ticks,
                        ProcessName = "PowerSI Pending",
                        State = "PENDING",
                        Source = "NONE",
                        Code = "SC_PENDING"
                    },
                    new PowerSiTargetReport
                    {
                        Pid = 303,
                        StartUtcTicks = new DateTime(2026, 9, 15, 3, 0, 0, DateTimeKind.Utc).Ticks,
                        ProcessName = "PowerSI Direct Empty",
                        CapturedUtc = new DateTime(2026, 9, 16, 1, 1, 2, DateTimeKind.Utc),
                        State = "VISIBLE_EMPTY",
                        Source = "BUFFER",
                        Code = "OUTPUT_EMPTY",
                        Summary = "Direct Output buffer was empty."
                    }
                }
            };
            string wire = sample.Serialize();
            PowerSiReport parsed = Parse(wire);
            Need(wire.IndexOf('|') < 0 && parsed.Serialize() == wire && parsed.Targets[0].ProcessName == fullName &&
                parsed.Targets[0].Excerpt == excerpt && parsed.Targets[1].Summary == string.Empty &&
                parsed.Targets[2].State == "VISIBLE_EMPTY" && parsed.Targets[2].Source == "BUFFER" &&
                parsed.Targets[2].Excerpt == string.Empty,
                "REPORT_CODEC_ROUNDTRIP");

            var failed = new PowerSiReport
            {
                CapturedUtc = sample.CapturedUtc,
                SessionId = 7,
                Partial = true,
                Code = "CAPTURE_FAILED"
            };
            Need(Parse(failed.Serialize()).Code == "CAPTURE_FAILED" && Parse(failed.Serialize()).Targets.Length == 0,
                "REPORT_GLOBAL_FAILURE");

            Reject(() => Parse(wire + " "));
            Reject(() => Parse(wire.Replace("AUTO_COPY_READ", "bad-code")));
            Reject(() => Parse(wire.Replace(",READ,AUTO_COPY,", ",EXECUTE,AUTO_COPY,")));
            Reject(() => Parse(wire + wire.Substring(wire.IndexOf(';'))));
            Reject(() => Parse(new string('X', MaxWireLength + 1)));
            Reject(() => new PowerSiReport
            {
                CapturedUtc = sample.CapturedUtc,
                SessionId = 7,
                Targets = new[]
                {
                    new PowerSiTargetReport
                    {
                        Pid = 1,
                        StartUtcTicks = sample.Targets[1].StartUtcTicks,
                        ProcessName = "powersi",
                        State = "PENDING",
                        Source = "NONE",
                        Code = "SC_PENDING",
                        Summary = "must not leak evidence"
                    }
                }
            }.Serialize());
        }

        internal static bool ValidUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Utc && value.Year >= 2000;
        }

        internal static bool ValidUtc(DateTime? value)
        {
            return !value.HasValue || ValidUtc(value.Value);
        }

        internal static bool ValidStartTicks(long? value)
        {
            if (!value.HasValue) return true;
            try { return ValidUtc(new DateTime(value.Value, DateTimeKind.Utc)); }
            catch (ArgumentOutOfRangeException) { return false; }
        }

        internal static bool ValidCode(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxCodeLength || value[0] < 'A' || value[0] > 'Z')
                return false;
            for (var i = 1; i < value.Length; i++)
                if (!((value[i] >= 'A' && value[i] <= 'Z') || (value[i] >= '0' && value[i] <= '9') || value[i] == '_'))
                    return false;
            return true;
        }

        internal static bool ValidState(string value) { return States.Contains(value); }

        internal static bool ValidSource(string value) { return Sources.Contains(value); }

        internal static bool ValidText(string value, int minimum, int maximum, bool singleLine)
        {
            if (value == null || value.Length < minimum || value.Length > maximum) return false;
            try { StrictUtf8.GetBytes(value); }
            catch (EncoderFallbackException) { return false; }
            foreach (char character in value)
                if ((singleLine && (character == '\r' || character == '\n')) || char.IsControl(character)) return false;
            return true;
        }

        internal static bool ValidExcerpt(string value)
        {
            if (value == null || value.Length > MaxExcerptLength || value.Count(character => character == '\n') >= MaxExcerptLines)
                return false;
            try { StrictUtf8.GetBytes(value); }
            catch (EncoderFallbackException) { return false; }
            foreach (char character in value)
                if (character == '\r' || (char.IsControl(character) && character != '\n' && character != '\t')) return false;
            return true;
        }

        private static string NormalizeSourceText(string value)
        {
            value = value.Replace("\r\n", "\n").Replace('\r', '\n');
            var safe = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (char.IsHighSurrogate(character))
                {
                    if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        safe.Append(character).Append(value[++i]);
                        continue;
                    }
                    safe.Append('\ufffd');
                }
                else if (char.IsLowSurrogate(character)) safe.Append('\ufffd');
                else if (char.IsControl(character) && character != '\n' && character != '\t') safe.Append('\ufffd');
                else safe.Append(character);
            }
            return safe.ToString();
        }

        private static string Nullable(long? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(StrictUtf8.GetBytes(value));
        }

        private static string EncodeOptional(string value)
        {
            return string.IsNullOrEmpty(value) ? "-" : Encode(value);
        }

        private static bool TryText(string encoded, int minimum, int maximum, bool singleLine, out string value)
        {
            value = null;
            if (encoded == "-")
            {
                value = string.Empty;
                return minimum == 0;
            }
            int maxEncoded = ((maximum * 3 + 2) / 3) * 4;
            if (string.IsNullOrEmpty(encoded) || encoded.Length > maxEncoded) return false;
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
                if (Convert.ToBase64String(bytes) != encoded) return false;
                value = StrictUtf8.GetString(bytes);
                return singleLine ? ValidText(value, minimum, maximum, true) :
                    (minimum == 0 ? ValidExcerpt(value) : ValidText(value, minimum, maximum, false));
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

        private static bool TryNullableLong(string text, long maximum, out long value)
        {
            value = 0;
            return text == "-" || TryLong(text, maximum, out value);
        }

        private static bool TryLong(string text, long maximum, out long value)
        {
            value = 0;
            return CanonicalNumber(text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
                value <= maximum;
        }

        private static bool CanonicalNumber(string value)
        {
            return !string.IsNullOrEmpty(value) && (value.Length == 1 || value[0] != '0') &&
                value.All(character => character >= '0' && character <= '9');
        }

        private static bool PrintableAscii(string value)
        {
            return value.All(character => character >= 0x21 && character <= 0x7e);
        }

        private static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid PowerSI report was accepted.");
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("PowerSI report self-test failed: " + reason + ".");
        }
    }
}
