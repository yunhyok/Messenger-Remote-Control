using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        internal string BufferCode;
        internal string VisionCode;
        internal string OutputText;
        internal DateTime? OcrCapturedUtc;
        internal string OcrText;

        internal void Validate()
        {
            string output = OutputText ?? string.Empty;
            string ocr = OcrText ?? string.Empty;
            if (Pid <= 0 || !PowerSiReport.ValidStartTicks(StartUtcTicks) ||
                !PowerSiReport.ValidText(ProcessName, 1, ProcessInventory.MaxFullNameLength, false) ||
                !PowerSiReport.ValidUtc(CapturedUtc) || !PowerSiReport.ValidUtc(OcrCapturedUtc) ||
                !PowerSiReport.ValidState(State) || !PowerSiReport.ValidSource(Source) ||
                !PowerSiReport.ValidCode(Code) || !PowerSiReport.ValidOptionalCode(BufferCode) ||
                !PowerSiReport.ValidOptionalCode(VisionCode) || !PowerSiReport.ValidOutputText(output) ||
                !PowerSiReport.ValidOutputText(ocr))
                throw new InvalidDataException("Invalid PowerSI target report.");

            bool capturedState = State == "READ" || State == "PENDING" || State == "VISIBLE_EMPTY";
            bool hasOutput = !string.IsNullOrWhiteSpace(output);
            bool hasOcr = !string.IsNullOrWhiteSpace(ocr);
            if ((capturedState && !StartUtcTicks.HasValue) ||
                (State == "READ" && (!CapturedUtc.HasValue || Source == "NONE" || !hasOutput)) ||
                (State == "PENDING" && (CapturedUtc.HasValue || OcrCapturedUtc.HasValue || Source != "NONE" ||
                    output.Length != 0 || ocr.Length != 0)) ||
                (State == "VISIBLE_EMPTY" && (!CapturedUtc.HasValue || Source == "NONE" ||
                    output.Length != 0 || ocr.Length != 0 || OcrCapturedUtc.HasValue)) ||
                ((State == "UNAVAILABLE" || State == "TIMEOUT" || State == "NOT_ATTEMPTED") &&
                    (Source != "NONE" || output.Length != 0 || ocr.Length != 0 || OcrCapturedUtc.HasValue)) ||
                (Source == "OCR" && (ocr.Length != 0 || OcrCapturedUtc.HasValue)) ||
                ((Source == "BUFFER" || Source == "AUTO_COPY") && (hasOcr != OcrCapturedUtc.HasValue)) ||
                (Source == "NONE" && OcrCapturedUtc.HasValue))
                throw new InvalidDataException("Inconsistent PowerSI target report.");
        }
    }

    internal sealed class PowerSiReport
    {
        internal const int CollectionBudgetMilliseconds = 100000;
        internal const int RequestDeadlineMilliseconds = 110000;
        internal const int MaxTargets = 128;
        internal const int MaxOutputLength = 8 * 1024 * 1024;
        internal const int MaxAggregateOutputLength = 32 * 1024 * 1024;
        internal const int MaxCodeLength = 48;
        internal const int MaxFrameLineLength = 8300;
        internal const string WireVersion = "PS4";

        private const int MaxCount = 999999;
        private const int ChunkCharacters = 1536;
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
            ValidateCore(true);
        }

        internal PowerSiReport ForTransportCapacity()
        {
            ValidateCore(false);
            if (AggregateOutputLength() <= MaxAggregateOutputLength) return this;
            return new PowerSiReport
            {
                CapturedUtc = CapturedUtc, SessionId = SessionId, Omitted = Omitted, Unreadable = Unreadable,
                Partial = true, Code = "REPORT_TOO_LARGE",
                Targets = Targets.Select(target => new PowerSiTargetReport
                {
                    Pid = target.Pid, StartUtcTicks = target.StartUtcTicks, ProcessName = target.ProcessName,
                    State = target.State == "PENDING" ? "PENDING" : "UNAVAILABLE", Source = "NONE",
                    Code = target.State == "PENDING" ? "SC_PENDING" : "REPORT_TOO_LARGE",
                    BufferCode = target.State == "PENDING" ? "SC_PENDING" : "REPORT_TOO_LARGE",
                    OutputText = string.Empty, OcrText = string.Empty
                }).ToArray()
            };
        }

        private void ValidateCore(bool enforceCapacity)
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
            if (enforceCapacity && AggregateOutputLength() > MaxAggregateOutputLength)
                throw new InvalidDataException("PowerSI report exceeds the aggregate text limit.");
        }

        private long AggregateOutputLength()
        {
            long total = 0;
            foreach (PowerSiTargetReport target in Targets)
                total += (target.OutputText ?? string.Empty).Length + (target.OcrText ?? string.Empty).Length;
            return total;
        }

        internal async Task WriteFramedAsync(Stream stream, CancellationToken cancellation)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            Validate();
            await LinkProtocol.WriteLineAsync(stream, string.Join("|", new[]
            {
                WireVersion, Number(CapturedUtc.Ticks), Number(SessionId), Number(Omitted), Number(Unreadable),
                Partial ? "1" : "0", Code, Number(Targets.Length)
            }), MaxFrameLineLength, cancellation).ConfigureAwait(false);
            for (int index = 0; index < Targets.Length; index++)
            {
                PowerSiTargetReport target = Targets[index];
                string output = target.OutputText ?? string.Empty;
                string ocr = target.OcrText ?? string.Empty;
                string header = string.Join("|", new[]
                {
                    "T", Number(index), Number(target.Pid), Nullable(target.StartUtcTicks), Encode(target.ProcessName),
                    NullableUtc(target.CapturedUtc), target.State, target.Source, target.Code,
                    OptionalCode(target.BufferCode), OptionalCode(target.VisionCode), NullableUtc(target.OcrCapturedUtc),
                    Number(output.Length), Number(StrictUtf8.GetByteCount(output)), Number(PartCount(output)),
                    Number(ocr.Length), Number(StrictUtf8.GetByteCount(ocr)), Number(PartCount(ocr))
                });
                await LinkProtocol.WriteLineAsync(stream, header, MaxFrameLineLength, cancellation).ConfigureAwait(false);
                await WriteTextAsync(stream, index, "O", output, cancellation).ConfigureAwait(false);
                await WriteTextAsync(stream, index, "R", ocr, cancellation).ConfigureAwait(false);
            }
            await LinkProtocol.WriteLineAsync(stream, "E|" + Number(Targets.Length), MaxFrameLineLength, cancellation)
                .ConfigureAwait(false);
        }

        internal static async Task<PowerSiReport> ReadFramedAsync(LinkProtocol.LineReader reader, CancellationToken cancellation)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            string[] header = Split(await reader.ReadLineAsync(MaxFrameLineLength, cancellation).ConfigureAwait(false));
            long capturedTicks;
            int sessionId, omitted, unreadable, count;
            if (header.Length != 8 || header[0] != WireVersion ||
                !TryLong(header[1], DateTime.MaxValue.Ticks, out capturedTicks) ||
                !TryInt(header[2], 0, int.MaxValue, out sessionId) ||
                !TryInt(header[3], 0, MaxCount, out omitted) || !TryInt(header[4], 0, MaxCount, out unreadable) ||
                (header[5] != "0" && header[5] != "1") || !ValidCode(header[6]) ||
                !TryInt(header[7], 0, MaxTargets, out count))
                throw new InvalidDataException("Invalid PowerSI report frame.");
            var targets = new PowerSiTargetReport[count];
            long aggregateCharacters = 0;
            for (int index = 0; index < count; index++)
            {
                string[] fields = Split(await reader.ReadLineAsync(MaxFrameLineLength, cancellation).ConfigureAwait(false));
                int parsedIndex, pid, outputChars, outputBytes, outputParts, ocrChars, ocrBytes, ocrParts;
                long startTicks, targetTicks, ocrTicks;
                string name;
                if (fields.Length != 18 || fields[0] != "T" || !TryInt(fields[1], index, index, out parsedIndex) ||
                    !TryInt(fields[2], 1, int.MaxValue, out pid) || !TryNullableLong(fields[3], DateTime.MaxValue.Ticks, out startTicks) ||
                    !TryDecodedText(fields[4], 1, ProcessInventory.MaxFullNameLength, false, out name) ||
                    !TryNullableLong(fields[5], DateTime.MaxValue.Ticks, out targetTicks) ||
                    !ValidState(fields[6]) || !ValidSource(fields[7]) || !ValidCode(fields[8]) ||
                    !TryOptionalCode(fields[9]) || !TryOptionalCode(fields[10]) ||
                    !TryNullableLong(fields[11], DateTime.MaxValue.Ticks, out ocrTicks) ||
                    !TryInt(fields[12], 0, MaxOutputLength, out outputChars) ||
                    !TryInt(fields[13], 0, MaxOutputLength * 4, out outputBytes) ||
                    !TryInt(fields[14], MinimumPartCount(outputChars), MaximumPartCount(outputChars), out outputParts) ||
                    !TryInt(fields[15], 0, MaxOutputLength, out ocrChars) ||
                    !TryInt(fields[16], 0, MaxOutputLength * 4, out ocrBytes) ||
                    !TryInt(fields[17], MinimumPartCount(ocrChars), MaximumPartCount(ocrChars), out ocrParts))
                    throw new InvalidDataException("Invalid PowerSI target frame.");
                aggregateCharacters += (long)outputChars + ocrChars;
                if (aggregateCharacters > MaxAggregateOutputLength)
                    throw new InvalidDataException("PowerSI report exceeds the aggregate text limit.");
                string output = await ReadTextAsync(reader, index, "O", outputChars, outputBytes, outputParts, cancellation).ConfigureAwait(false);
                string ocr = await ReadTextAsync(reader, index, "R", ocrChars, ocrBytes, ocrParts, cancellation).ConfigureAwait(false);
                targets[index] = new PowerSiTargetReport
                {
                    Pid = pid, StartUtcTicks = fields[3] == "-" ? (long?)null : startTicks, ProcessName = name,
                    CapturedUtc = fields[5] == "-" ? (DateTime?)null : new DateTime(targetTicks, DateTimeKind.Utc),
                    State = fields[6], Source = fields[7], Code = fields[8],
                    BufferCode = DecodeOptionalCode(fields[9]), VisionCode = DecodeOptionalCode(fields[10]),
                    OcrCapturedUtc = fields[11] == "-" ? (DateTime?)null : new DateTime(ocrTicks, DateTimeKind.Utc),
                    OutputText = output, OcrText = ocr
                };
            }
            string[] end = Split(await reader.ReadLineAsync(MaxFrameLineLength, cancellation).ConfigureAwait(false));
            int endCount;
            if (end.Length != 2 || end[0] != "E" || !TryInt(end[1], count, count, out endCount))
                throw new InvalidDataException("Invalid PowerSI report terminator.");
            var report = new PowerSiReport
            {
                CapturedUtc = new DateTime(capturedTicks, DateTimeKind.Utc), SessionId = sessionId,
                Omitted = omitted, Unreadable = unreadable, Partial = header[5] == "1", Code = header[6], Targets = targets
            };
            report.Validate();
            return report;
        }

        internal static void RunSelfTest()
        {
            DateTime captured = new DateTime(2026, 9, 18, 1, 2, 3, DateTimeKind.Utc);
            string unicode = "first\r\n한글 😀\tlast";
            string large = string.Concat(Enumerable.Repeat("0123456789한글😀\r\n", 12000));
            string boundary = new string('a', ChunkCharacters - 1) + "😀" + new string('b', ChunkCharacters - 1);
            var sample = new PowerSiReport
            {
                CapturedUtc = captured, SessionId = 7, Omitted = 1, Unreadable = 2, Partial = true,
                Targets = new[]
                {
                    new PowerSiTargetReport { Pid = 101, StartUtcTicks = captured.AddDays(-1).Ticks, ProcessName = "PowerSI 한글",
                        CapturedUtc = captured, State = "READ", Source = "AUTO_COPY", Code = "AUTO_COPY_READ",
                        BufferCode = "AUTO_COPY_READ", VisionCode = "OUTPUT_READ", OutputText = large,
                        OcrCapturedUtc = captured.AddSeconds(1), OcrText = boundary + unicode },
                    new PowerSiTargetReport { Pid = 202, StartUtcTicks = captured.AddHours(-1).Ticks, ProcessName = "PowerSI Pending",
                        State = "PENDING", Source = "NONE", Code = "SC_PENDING", BufferCode = "SC_PENDING",
                        VisionCode = "VISION_CAPTURE_FAILED", OutputText = string.Empty, OcrText = string.Empty },
                    new PowerSiTargetReport { Pid = 303, StartUtcTicks = captured.AddHours(-2).Ticks, ProcessName = "PowerSI Empty",
                        CapturedUtc = captured, State = "VISIBLE_EMPTY", Source = "BUFFER", Code = "OUTPUT_EMPTY",
                        BufferCode = "BUFFER_READ", VisionCode = "VISION_NOT_CONFIGURED", OutputText = string.Empty, OcrText = string.Empty }
                }
            };
            PowerSiReport parsed = RoundTrip(sample);
            Need(parsed.Targets[0].OutputText == large && parsed.Targets[0].OcrText == boundary + unicode &&
                parsed.Targets[0].ProcessName == "PowerSI 한글" && parsed.Targets[1].OutputText == string.Empty &&
                parsed.Targets[2].State == "VISIBLE_EMPTY", "PS4_FRAMED_ROUNDTRIP");
            Reject(() => RoundTrip(new PowerSiReport { CapturedUtc = captured, SessionId = 7, Targets = new[] {
                new PowerSiTargetReport { Pid = 1, StartUtcTicks = captured.Ticks, ProcessName = "PowerSI", State = "PENDING",
                    Source = "NONE", Code = "SC_PENDING", OutputText = "must not leak" } } }));
            Reject(() => RoundTrip(new PowerSiReport { CapturedUtc = captured, SessionId = 7, Targets = new[] {
                new PowerSiTargetReport { Pid = 1, StartUtcTicks = captured.Ticks, ProcessName = "PowerSI", CapturedUtc = captured,
                    State = "READ", Source = "BUFFER", Code = "BUFFER_READ", OutputText = new string('x', MaxOutputLength + 1) } } }));
            using (var stream = new MemoryStream())
            {
                sample.WriteFramedAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
                byte[] wire = stream.ToArray();
                for (int i = 0, line = 0; i < wire.Length; i++)
                    if (wire[i] == (byte)'\n') { Need(i - line <= MaxFrameLineLength, "PS4_BOUNDED_LINE"); line = i + 1; }
                string reordered = Encoding.ASCII.GetString(wire).Replace("D|0|O|0|", "D|0|O|1|");
                Reject(() => ReadWire(Encoding.ASCII.GetBytes(reordered)));
                Reject(() => ReadWire(wire.Take(wire.Length - 2).ToArray()));
                string original = Encoding.ASCII.GetString(wire);
                int dataStart = original.IndexOf("D|0|O|0|", StringComparison.Ordinal);
                int dataEnd = original.IndexOf('\n', dataStart);
                string oversizedChunk = original.Substring(0, dataStart) + "D|0|O|0|" +
                    Convert.ToBase64String(Enumerable.Repeat((byte)'a', ChunkCharacters + 1).ToArray()) +
                    original.Substring(dataEnd);
                Reject(() => ReadWire(Encoding.ASCII.GetBytes(oversizedChunk)));
            }

            string maximum = new string('x', MaxOutputLength);
            var oversized = new PowerSiReport
            {
                CapturedUtc = captured, SessionId = 9,
                Targets = Enumerable.Range(1, 5).Select(pid => new PowerSiTargetReport
                {
                    Pid = pid, StartUtcTicks = captured.AddMinutes(-pid).Ticks, ProcessName = "PowerSI " + pid,
                    CapturedUtc = captured, State = "READ", Source = "BUFFER", Code = "BUFFER_READ",
                    BufferCode = "BUFFER_READ", OutputText = maximum, OcrText = string.Empty
                }).Concat(new[] { new PowerSiTargetReport
                {
                    Pid = 99, StartUtcTicks = captured.AddHours(-1).Ticks, ProcessName = "PowerSI Pending",
                    State = "PENDING", Source = "NONE", Code = "SC_PENDING", OutputText = string.Empty, OcrText = string.Empty
                } }).ToArray()
            };
            PowerSiReport capacity = oversized.ForTransportCapacity();
            capacity.Validate();
            Need(capacity.Code == "REPORT_TOO_LARGE" && capacity.Partial &&
                capacity.Targets.Take(5).All(target => target.State == "UNAVAILABLE" && target.Code == "REPORT_TOO_LARGE" &&
                    target.OutputText.Length == 0) && capacity.Targets[5].State == "PENDING",
                "PS4_AGGREGATE_CAPACITY_REPORT");
        }

        private static PowerSiReport RoundTrip(PowerSiReport report)
        {
            using (var stream = new MemoryStream())
            {
                report.WriteFramedAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
                stream.Position = 0;
                return ReadFramedAsync(new LinkProtocol.LineReader(stream), CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        private static PowerSiReport ReadWire(byte[] wire)
        {
            using (var stream = new MemoryStream(wire, false))
                return ReadFramedAsync(new LinkProtocol.LineReader(stream), CancellationToken.None).GetAwaiter().GetResult();
        }

        private static async Task WriteTextAsync(Stream stream, int targetIndex, string field, string value, CancellationToken cancellation)
        {
            int part = 0;
            for (int offset = 0; offset < value.Length; part++)
            {
                int count = Math.Min(ChunkCharacters, value.Length - offset);
                if (offset + count < value.Length && char.IsHighSurrogate(value[offset + count - 1]) && char.IsLowSurrogate(value[offset + count])) count--;
                byte[] bytes = StrictUtf8.GetBytes(value.Substring(offset, count));
                string line = "D|" + Number(targetIndex) + "|" + field + "|" + Number(part) + "|" + Convert.ToBase64String(bytes);
                await LinkProtocol.WriteLineAsync(stream, line, MaxFrameLineLength, cancellation).ConfigureAwait(false);
                offset += count;
            }
        }

        private static async Task<string> ReadTextAsync(LinkProtocol.LineReader reader, int targetIndex, string field,
            int expectedChars, int expectedBytes, int expectedParts, CancellationToken cancellation)
        {
            var text = new StringBuilder();
            Decoder decoder = StrictUtf8.GetDecoder();
            int totalBytes = 0;
            var chars = new char[ChunkCharacters * 4];
            for (int part = 0; part < expectedParts; part++)
            {
                string[] fields = Split(await reader.ReadLineAsync(MaxFrameLineLength, cancellation).ConfigureAwait(false));
                int parsedTarget, parsedPart;
                byte[] bytes;
                if (fields.Length != 5 || fields[0] != "D" || fields[2] != field ||
                    !TryInt(fields[1], targetIndex, targetIndex, out parsedTarget) || !TryInt(fields[3], part, part, out parsedPart) ||
                    !TryBase64(fields[4], out bytes) || bytes.Length < 1 || bytes.Length > ChunkCharacters * 4 ||
                    totalBytes + bytes.Length > expectedBytes)
                    throw new InvalidDataException("Invalid PowerSI text frame.");
                totalBytes += bytes.Length;
                try
                {
                    int count = decoder.GetChars(bytes, 0, bytes.Length, chars, 0, part == expectedParts - 1);
                    if (count > ChunkCharacters || text.Length + count > expectedChars)
                        throw new InvalidDataException("Invalid PowerSI text length.");
                    text.Append(chars, 0, count);
                }
                catch (DecoderFallbackException) { throw new InvalidDataException("Invalid PowerSI UTF-8 text."); }
                catch (ArgumentException) { throw new InvalidDataException("Invalid PowerSI text frame."); }
            }
            if (text.Length != expectedChars || totalBytes != expectedBytes) throw new InvalidDataException("Invalid PowerSI text length.");
            string value = text.ToString();
            if (!ValidOutputText(value)) throw new InvalidDataException("Invalid PowerSI text.");
            return value;
        }

        internal static bool ValidUtc(DateTime value) { return value.Kind == DateTimeKind.Utc && value.Year >= 2000; }
        internal static bool ValidUtc(DateTime? value) { return !value.HasValue || ValidUtc(value.Value); }
        internal static bool ValidStartTicks(long? value)
        {
            if (!value.HasValue) return true;
            try { return ValidUtc(new DateTime(value.Value, DateTimeKind.Utc)); }
            catch (ArgumentOutOfRangeException) { return false; }
        }
        internal static bool ValidCode(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxCodeLength || value[0] < 'A' || value[0] > 'Z') return false;
            for (int i = 1; i < value.Length; i++)
                if (!((value[i] >= 'A' && value[i] <= 'Z') || (value[i] >= '0' && value[i] <= '9') || value[i] == '_')) return false;
            return true;
        }
        internal static bool ValidOptionalCode(string value) { return string.IsNullOrEmpty(value) || ValidCode(value); }
        internal static bool ValidState(string value) { return States.Contains(value); }
        internal static bool ValidSource(string value) { return Sources.Contains(value); }
        internal static bool ValidText(string value, int minimum, int maximum, bool singleLine)
        {
            if (value == null || value.Length < minimum || value.Length > maximum) return false;
            try { StrictUtf8.GetByteCount(value); } catch (EncoderFallbackException) { return false; }
            foreach (char character in value)
                if ((singleLine && (character == '\r' || character == '\n')) || char.IsControl(character)) return false;
            return true;
        }
        internal static bool ValidOutputText(string value)
        {
            if (value == null || value.Length > MaxOutputLength) return false;
            try { StrictUtf8.GetByteCount(value); } catch (EncoderFallbackException) { return false; }
            return true;
        }
        private static int PartCount(string value)
        {
            int parts = 0;
            for (int offset = 0; offset < value.Length; parts++)
            {
                int count = Math.Min(ChunkCharacters, value.Length - offset);
                if (offset + count < value.Length && char.IsHighSurrogate(value[offset + count - 1]) &&
                    char.IsLowSurrogate(value[offset + count])) count--;
                offset += count;
            }
            return parts;
        }
        private static int MinimumPartCount(int length) { return length == 0 ? 0 : (length + ChunkCharacters - 1) / ChunkCharacters; }
        private static int MaximumPartCount(int length) { return length == 0 ? 0 : (length + ChunkCharacters - 2) / (ChunkCharacters - 1); }
        private static string Nullable(long? value) { return value.HasValue ? Number(value.Value) : "-"; }
        private static string NullableUtc(DateTime? value) { return value.HasValue ? Number(value.Value.Ticks) : "-"; }
        private static string OptionalCode(string value) { return string.IsNullOrEmpty(value) ? "-" : value; }
        private static string DecodeOptionalCode(string value) { return value == "-" ? string.Empty : value; }
        private static bool TryOptionalCode(string value) { return value == "-" || ValidCode(value); }
        private static string Number(long value) { return value.ToString(CultureInfo.InvariantCulture); }
        private static string Encode(string value) { return Convert.ToBase64String(StrictUtf8.GetBytes(value)); }
        private static string[] Split(string value) { return value.Split('|'); }
        private static bool TryBase64(string encoded, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 8192) return false;
            try { bytes = Convert.FromBase64String(encoded); return Convert.ToBase64String(bytes) == encoded; }
            catch (FormatException) { return false; }
        }
        private static bool TryDecodedText(string encoded, int minimum, int maximum, bool singleLine, out string value)
        {
            value = null;
            byte[] bytes;
            if (!TryBase64(encoded, out bytes)) return false;
            try { value = StrictUtf8.GetString(bytes); } catch (DecoderFallbackException) { return false; }
            return ValidText(value, minimum, maximum, singleLine) && Encode(value) == encoded;
        }
        private static bool TryInt(string text, int minimum, int maximum, out int value)
        {
            value = 0;
            return CanonicalNumber(text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum;
        }
        private static bool TryNullableLong(string text, long maximum, out long value)
        {
            value = 0;
            return text == "-" || TryLong(text, maximum, out value);
        }
        private static bool TryLong(string text, long maximum, out long value)
        {
            value = 0;
            return CanonicalNumber(text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= maximum;
        }
        private static bool CanonicalNumber(string value)
        {
            return !string.IsNullOrEmpty(value) && (value.Length == 1 || value[0] != '0') && value.All(character => character >= '0' && character <= '9');
        }
        private static void Reject(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; } catch (EndOfStreamException) { return; }
            throw new InvalidOperationException("Invalid PowerSI report was accepted.");
        }
        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("PowerSI report self-test failed: " + reason + ".");
        }
    }
}
