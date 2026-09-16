using System;
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
using RemoteMonitorLink;

namespace RemoteMonitorSlave
{
    // A local-only result pipe. Never reuse this encoding for the Master/messenger protocol.
    internal static class OutputBufferCapture
    {
        internal const int MaxCharacters = PowerSiOutputBuffer.MaxCharacters;
        private const int MaxWire = (MaxCharacters * 2 + 2) / 3 * 4 + (OutputAutoCopy.MaxFramePng + 2) / 3 * 4 + 4096;
        private const string Argument = "--powersi-output-buffer";
        private const string CopyArgument = "--powersi-output-copy";
        private static readonly Encoding Encoding = new UnicodeEncoding(false, false, true);

        internal static bool TryRunWorker(string[] args)
        {
            if (args.Length == 0 || (args[0] != Argument && args[0] != CopyArgument && args[0] != OutputAutoCopy.WorkerArgument)) return false;
            OutputBufferResult result;
            try
            {
                // The auto-copy verb owns its own window resolution, live re-checks and failure codes.
                if (args[0] == OutputAutoCopy.WorkerArgument) result = OutputAutoCopy.RunWorker(args);
                else
                {
                    var targetArgs = args[0] == CopyArgument ? args.Take(3).ToArray() : args;
                    var root = PowerSiScreenCapture.ResolveWindow(targetArgs, PowerSiScreenCapture.RequireResponsive);
                    if (args[0] == CopyArgument)
                    {
                        uint baseline;
                        if (args.Length != 4 || !uint.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out baseline))
                            throw new InvalidDataException("SC_IDENTITY");
                        var inventory = ParseInventory(args[1], args[2]);
                        if (!TryReadUserCopy(inventory, baseline, out result)) result = Failed("COPY_PENDING");
                        // Read-only sampling of where the user clicked before copying; a hint for the auto-copy verb only.
                        else if (result != null && result.Code == "USER_COPY_READ" && result.Detail == "SOURCE_PID_MATCH")
                            result.Detail = "SOURCE_PID_MATCH|" + OutputAutoCopy.LearnAnchor(root, inventory);
                    }
                    else result = PowerSiOutputBuffer.Read(root);
                    PowerSiScreenCapture.RequireResponsive(root);
                    if (PowerSiScreenCapture.ResolveWindow(targetArgs) != root) throw new InvalidDataException("SC_WINDOW_CHANGED");
                }
            }
            catch (InvalidDataException ex) { result = Failed(SafeCode(ex.Message) ? ex.Message : "BUFFER_FAILED"); }
            catch { result = Failed("BUFFER_FAILED"); }
            try { Console.Out.Write(Serialize(result)); }
            catch { Console.Out.Write(Serialize(Failed("BUFFER_TEXT_INVALID"))); }
            return true;
        }

        internal static async Task<OutputBufferResult> ReadAsync(ProcessInventory inventory, CancellationToken cancellation, uint? copyBaseline = null)
        {
            inventory.Validate();
            if (inventory.Items.Length == 0) return Failed("SC_NOT_RUNNING");
            if (inventory.Omitted != 0 || inventory.Items.Any(p => !ProcessInventory.IsPowerSiName(p.Name) || !p.StartUtcTicks.HasValue))
                return Failed("SC_IDENTITY");
            var identities = string.Join(",", inventory.Items.Select(p => p.Pid.ToString(CultureInfo.InvariantCulture) + ":" +
                p.StartUtcTicks.Value.ToString(CultureInfo.InvariantCulture)));
            var arguments = (copyBaseline.HasValue ? CopyArgument : Argument) + " " +
                inventory.SessionId.ToString(CultureInfo.InvariantCulture) + " " + identities +
                (copyBaseline.HasValue ? " " + copyBaseline.Value.ToString(CultureInfo.InvariantCulture) : "");
            return await RunWorkerAsync(arguments, copyBaseline.HasValue ? 3000 : 10000, "BUFFER_TIMEOUT",
                "BUFFER_WORKER_FAILED", cancellation).ConfigureAwait(false);
        }

        // Read-only workers may be terminated immediately; an input worker first gets EOF and cleanup time.
        internal static async Task<OutputBufferResult> RunWorkerAsync(string arguments, int timeoutMilliseconds,
            string timeoutCode, string failureCode, CancellationToken cancellation)
        {
            bool inputWorker = arguments.StartsWith(OutputAutoCopy.WorkerArgument + " ", StringComparison.Ordinal);
            using (var worker = new Process { StartInfo = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, arguments)
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardInput = inputWorker } })
            {
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    worker.Start();
                    var reading = ReadBounded(worker.StandardOutput);
                    var clock = Stopwatch.StartNew();
                    while (!worker.HasExited || !reading.IsCompleted)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (reading.IsFaulted) await reading.ConfigureAwait(false);
                        if (clock.ElapsedMilliseconds >= timeoutMilliseconds) return Failed(timeoutCode);
                        await Task.Delay(25, cancellation).ConfigureAwait(false);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    return worker.ExitCode == 0 ? Parse(await reading.ConfigureAwait(false)) : Failed(failureCode);
                }
                catch (OperationCanceledException) { throw; }
                catch { return Failed(failureCode); }
                finally
                {
                    try { await StopWorkerAsync(worker, inputWorker).ConfigureAwait(false); } catch { }
                }
            }
        }

        private static async Task<bool> StopWorkerAsync(Process worker, bool inputWorker)
        {
            if (inputWorker && !worker.HasExited)
            {
                try { worker.StandardInput.Close(); } catch (IOException) { } // Still enforce the exit bound if the pipe broke.
                var cleanup = Stopwatch.StartNew();
                while (!worker.HasExited && cleanup.ElapsedMilliseconds < 1000)
                    await Task.Delay(25).ConfigureAwait(false);
            }
            if (worker.HasExited) return true;
            // A hung provider still has a bounded lifetime. Input releases are paired in one SendInput call.
            worker.Kill(); worker.WaitForExit(200);
            return false;
        }

        private static void CancellationSelfTest()
        {
            using (var worker = new Process { StartInfo = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                OutputAutoCopy.WorkerArgument + " --self-test-cancel") { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true } })
            {
                try
                {
                    worker.Start();
                    var ready = worker.StandardOutput.ReadLineAsync();
                    if (!ready.Wait(5000) || ready.Result != "READY") throw new InvalidOperationException("Input worker did not become ready.");
                    var reading = worker.StandardOutput.ReadToEndAsync();
                    if (!StopWorkerAsync(worker, true).GetAwaiter().GetResult() || !reading.Wait(1000) ||
                        Parse(reading.Result).Code != "AUTO_COPY_TEST_CLEANUP")
                        throw new InvalidOperationException("Input worker was killed before cooperative cleanup.");
                }
                finally { try { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(200); } } catch { } }
            }
            Console.WriteLine("PASS: input worker cancellation completes cleanup before exit (no injected input)");
        }

        // Worker-side identity arguments. ResolveWindow has already validated and matched every PID/start time.
        internal static ProcessInventory ParseInventory(string session, string items)
        {
            return new ProcessInventory { SessionId = int.Parse(session, CultureInfo.InvariantCulture),
                Items = items.Split(',').Select(p => p.Split(':')).Select(p => new ProcessState {
                    Pid = int.Parse(p[0], CultureInfo.InvariantCulture), StartUtcTicks = long.Parse(p[1], CultureInfo.InvariantCulture) }).ToArray() };
        }

        private static async Task<string> ReadBounded(StreamReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[8192];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                if (text.Length + count > MaxWire) throw new InvalidDataException("BUFFER_SIZE");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }

        private static OutputBufferResult Failed(string code) { return new OutputBufferResult { Code = code, Method = "NONE", Detail = "NONE" }; }
        internal static bool SafeCode(string text) { return text != null && Regex.IsMatch(text, @"\A[A-Z0-9_]{1,64}\z"); }
        private static void Validate(OutputBufferResult result)
        {
            if (result == null || !SafeCode(result.Code) || !SafeCode(result.Method) || result.Detail == null ||
                !Regex.IsMatch(result.Detail, @"\A[A-Z0-9_:=,| -]{1,512}\z") || (result.Text?.Length ?? 0) > MaxCharacters)
                throw new InvalidDataException("BUFFER_RESULT_INVALID");
        }
        private static string Serialize(OutputBufferResult result)
        {
            Validate(result);
            var wire = string.Join("\t", "OB1", result.Code, result.Method, result.Detail,
                result.Text == null ? "-" : Convert.ToBase64String(Encoding.GetBytes(result.Text)));
            // Optional 6th field: auto-copy clean pre-Ctrl+A frame on success, latest diagnostic frame on failure.
            var frame = OutputAutoCopy.FrameOf(result);
            if (frame == null) return wire;
            if (frame.Length > OutputAutoCopy.MaxFramePng) throw new InvalidDataException("BUFFER_RESULT_INVALID");
            return wire + "\t" + Convert.ToBase64String(frame);
        }
        private static OutputBufferResult Parse(string wire)
        {
            if (wire == null || wire.Length > MaxWire) throw new InvalidDataException("BUFFER_SIZE");
            var parts = wire.Split('\t');
            if ((parts.Length != 5 && parts.Length != 6) || parts[0] != "OB1") throw new InvalidDataException("BUFFER_RESULT_INVALID");
            var bytes = parts[4] == "-" ? new byte[0] : Convert.FromBase64String(parts[4]);
            if (bytes.Length > MaxCharacters * 2 || (parts[4] != "-" && Convert.ToBase64String(bytes) != parts[4])) throw new InvalidDataException("BUFFER_SIZE");
            var result = new OutputBufferResult { Code = parts[1], Method = parts[2], Detail = parts[3], Text = parts[4] == "-" ? null : Encoding.GetString(bytes) };
            Validate(result);
            result.CharacterCount = result.Text?.Length ?? 0;
            result.LineCount = CountLines(result.Text);
            if (parts.Length == 6) OutputAutoCopy.AttachFrame(result, ParseFrame(parts[5]));
            return result;
        }
        // The worker frame is an image, never text: only its size and PNG signature are accepted here.
        private static byte[] ParseFrame(string field)
        {
            if (field == null || field.Length < 4 || field.Length > (OutputAutoCopy.MaxFramePng + 2) / 3 * 4 + 4)
                throw new InvalidDataException("BUFFER_SIZE");
            byte[] png;
            try { png = Convert.FromBase64String(field); }
            catch (FormatException) { throw new InvalidDataException("BUFFER_RESULT_INVALID"); }
            if (png.Length < 8 || png.Length > OutputAutoCopy.MaxFramePng ||
                png[0] != 137 || png[1] != 80 || png[2] != 78 || png[3] != 71 ||
                png[4] != 13 || png[5] != 10 || png[6] != 26 || png[7] != 10)
                throw new InvalidDataException("BUFFER_RESULT_INVALID");
            return png;
        }
        internal static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'))) count++;
            return count;
        }

        internal static uint ClipboardSequence { get { return GetClipboardSequenceNumber(); } }
        // Optional same-run fallback: user copies Output. We only read a NEW clipboard value owned by the identified PowerSI.
        // No EmptyClipboard/SetClipboardData, clipboard replacement, keyboard input or mouse automation here.
        internal static bool TryReadUserCopy(ProcessInventory inventory, uint baseline, out OutputBufferResult result)
        {
            result = null;
            uint sequence = GetClipboardSequenceNumber();
            if (sequence == 0 || sequence == baseline || !OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                if (GetClipboardSequenceNumber() != sequence) return false;
                var owner = GetClipboardOwner(); uint pid;
                if (owner == IntPtr.Zero || GetWindowThreadProcessId(owner, out pid) == 0) return false;
                var identity = inventory.Items.SingleOrDefault(p => p.Pid == (int)pid);
                if (identity == null || !identity.StartUtcTicks.HasValue) return false;
                using (var process = Process.GetProcessById(identity.Pid))
                    if (process.HasExited || process.SessionId != inventory.SessionId ||
                        process.StartTime.ToUniversalTime().Ticks != identity.StartUtcTicks.Value ||
                        !ProcessInventory.IsPowerSiName(ProcessInventory.NormalizeName(process.ProcessName))) return false;
                var memory = GetClipboardData(13); // CF_UNICODETEXT; Windows supplies standard ANSI conversion when supported.
                if (memory == IntPtr.Zero) return false;
                ulong bytes = GlobalSize(memory).ToUInt64();
                if (bytes < 2 || bytes > (ulong)(MaxCharacters + 1) * 2) { result = Failed("BUFFER_SIZE"); return true; }
                var pointer = GlobalLock(memory);
                if (pointer == IntPtr.Zero) return false;
                try
                {
                    int count = 0, bound = (int)(bytes / 2);
                    while (count < bound && Marshal.ReadInt16(pointer, count * 2) != 0) count++;
                    if (count == bound || count > MaxCharacters) { result = Failed("BUFFER_SIZE"); return true; }
                    string text = Marshal.PtrToStringUni(pointer, count);
                    if (string.IsNullOrWhiteSpace(text)) return false;
                    result = new OutputBufferResult { Text = text, Code = "USER_COPY_READ", Method = "USER_CLIPBOARD",
                        Detail = "SOURCE_PID_MATCH", CharacterCount = text.Length, LineCount = CountLines(text) };
                    return true;
                }
                finally { GlobalUnlock(memory); }
            }
            catch { return false; }
            finally { CloseClipboard(); }
        }

        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr window);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
        internal static string LogMetadata(OutputBufferResult result)
        {
            Validate(result);
            return " result=" + result.Code + " method=" + result.Method + " chars=" + (result.Text?.Length ?? 0).ToString(CultureInfo.InvariantCulture) +
                " lines=" + CountLines(result.Text).ToString(CultureInfo.InvariantCulture) + " detail=" + result.Detail;
        }
        internal static void SelfTest()
        {
            OutputBufferResult stale;
            if (TryReadUserCopy(new ProcessInventory(), ClipboardSequence, out stale))
                throw new InvalidOperationException("Unidentified/stale clipboard accepted as PowerSI Output.");
            var result = new OutputBufferResult { Code = "BUFFER_READ", Method = "NATIVE_WM_GETTEXT", Detail = "NATIVE=1,UIA=0",
                Text = "private-buffer-sentinel\r\nAFS Current Frequency (MHz) = 860.000\n완료\r끝" };
            var copy = Parse(Serialize(result));
            if (Parse(Serialize(Failed("BUFFER_STANDARD_TEXT_NOT_FOUND"))).Text != null)
                throw new InvalidOperationException("Unavailable buffer became empty successful text.");
            if (copy.Text != result.Text || copy.LineCount != 4 || copy.CharacterCount != result.Text.Length ||
                LogMetadata(copy).Contains("private") || LogMetadata(copy).Contains("860.000"))
                throw new InvalidOperationException("Buffer pipe preservation/privacy failed.");
            var anchor = new OutputAnchor { Pid = 4321, StartUtcTicks = 638000000000000000L, SessionId = 1,
                ClientPoint = new System.Drawing.Point(320, 385), ClientSize = new System.Drawing.Size(1920, 1040),
                LearnedUtc = new DateTime(2026, 9, 11, 1, 2, 3, DateTimeKind.Utc) };
            var learned = Parse(Serialize(new OutputBufferResult { Code = "USER_COPY_READ", Method = "USER_CLIPBOARD",
                Detail = "SOURCE_PID_MATCH|" + anchor.Serialize(), Text = "private-buffer-sentinel" }));
            if (learned.Detail.Split('|')[0] != "SOURCE_PID_MATCH" || !anchor.Matches(OutputAutoCopy.AnchorOf(learned)) ||
                LogMetadata(learned).Contains("private"))
                throw new InvalidOperationException("Learned anchor lost or unsafe on the OB1 wire.");
            var none = Parse(Serialize(new OutputBufferResult { Code = "USER_COPY_READ", Method = "USER_CLIPBOARD",
                Detail = "SOURCE_PID_MATCH|ANCHOR_NONE|FOREGROUND" }));
            if (none.Detail.Split('|')[0] != "SOURCE_PID_MATCH" || OutputAutoCopy.AnchorOf(none) != null)
                throw new InvalidOperationException("Missing anchor became an anchor.");
            var automatic = Parse(Serialize(new OutputBufferResult { Code = "AUTO_COPY_READ", Method = "AUTO_CLIPBOARD",
                Detail = "A2|320|385|20|40|360|320|1000|900|B2|1920|1040|12|1|3|4|1|2|1", Text = "sentinel" }));
            if (automatic.Code != "AUTO_COPY_READ" || automatic.Method != "AUTO_CLIPBOARD" || automatic.Text != "sentinel" ||
                Serialize(automatic).Split('\t').Length != 5 || OutputAutoCopy.FrameOf(automatic) != null)
                throw new InvalidOperationException("Auto copy result did not survive the OB1 wire.");
            // A failed auto copy carries its own capture back in the optional 6th field.
            var framePng = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aLe8AAAAASUVORK5CYII=");
            var aborted = new OutputBufferResult { Code = "AUTO_COPY_BODY_MOVED", Method = "NONE",
                Detail = "B2|1920|1009|12|1|3|4|1|2|1" };
            OutputAutoCopy.AttachFrame(aborted, framePng);
            if (Serialize(aborted).Split('\t').Length != 6)
                throw new InvalidOperationException("Auto copy frame did not reach the OB1 wire.");
            var returned = Parse(Serialize(aborted));
            var carried = OutputAutoCopy.FrameOf(returned);
            if (returned.Code != "AUTO_COPY_BODY_MOVED" || returned.Text != null || carried == null ||
                !carried.SequenceEqual(framePng) || LogMetadata(returned).Contains("iVBOR"))
                throw new InvalidOperationException("Auto copy frame lost or leaked on the OB1 wire.");
            // Successful automatic OCR uses this exact pre-selection frame and its actual capture timestamp.
            using (var bitmap = new System.Drawing.Bitmap(600, 420))
            using (var png = new MemoryStream())
            {
                bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                var capturedUtc = new DateTime(2026, 9, 14, 2, 3, 4, DateTimeKind.Utc);
                var clean = new OutputBufferResult { Code = "AUTO_COPY_READ", Method = "AUTO_CLIPBOARD",
                    Detail = automatic.Detail + "|CLEAN_FRAME|" + capturedUtc.Ticks.ToString(CultureInfo.InvariantCulture), Text = "sentinel" };
                OutputAutoCopy.AttachFrame(clean, png.ToArray());
                var decoded = Parse(Serialize(clean));
                var cleanFrame = OutputAutoCopy.CleanFrameOf(decoded);
                if (decoded.Text != clean.Text || cleanFrame.CapturedUtc != capturedUtc || cleanFrame.PixelSize != bitmap.Size ||
                    !cleanFrame.Png.SequenceEqual(png.ToArray()) || LogMetadata(decoded).Contains("iVBOR"))
                    throw new InvalidOperationException("Clean automatic frame or timestamp changed during transport.");
                foreach (var invalid in new[] { automatic, returned, new OutputBufferResult { Code = "AUTO_COPY_READ",
                    Method = "AUTO_CLIPBOARD", Text = "sentinel", Detail = "CLEAN_FRAME|0" } })
                {
                    OutputAutoCopy.AttachFrame(invalid, png.ToArray());
                    try { OutputAutoCopy.CleanFrameOf(invalid); throw new InvalidOperationException("Invalid clean capture accepted."); }
                    catch (InvalidDataException) { }
                }
            }
            foreach (var broken in new[] { "not base64", "AAAA", Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) })
            {
                var rejected = false;
                try { Parse(Serialize(automatic) + "\t" + broken); }
                catch (InvalidDataException) { rejected = true; }
                catch (FormatException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("A non-PNG 6th OB1 field was accepted.");
            }
            // Maximum text plus a real PNG larger than the old 4KiB spare envelope must survive both readers.
            using (var bitmap = new System.Drawing.Bitmap(100, 100))
            using (var png = new MemoryStream())
            {
                var random = new Random(53);
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++)
                        bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(unchecked((int)0xff000000) | random.Next(0x1000000)));
                bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                var large = new OutputBufferResult { Code = "AUTO_COPY_READ", Method = "AUTO_CLIPBOARD",
                    Detail = "CLEAN_FRAME|1", Text = new string('x', MaxCharacters) };
                OutputAutoCopy.AttachFrame(large, png.ToArray());
                var wire = Serialize(large);
                if (wire.Length <= (MaxCharacters * 2 + 2) / 3 * 4 + 4096)
                    throw new InvalidOperationException("Combined result did not exercise the old envelope boundary.");
                using (var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(wire)))
                using (var reader = new StreamReader(stream, System.Text.Encoding.ASCII))
                {
                    var combined = Parse(ReadBounded(reader).GetAwaiter().GetResult());
                    if (combined.Text != large.Text || !OutputAutoCopy.FrameOf(combined).SequenceEqual(png.ToArray()))
                        throw new InvalidOperationException("Maximum text plus clean PNG was lost at the worker envelope boundary.");
                }
            }
            result.Text = new string('x', MaxCharacters + 1);
            var oversize = false;
            try { Serialize(result); } catch (InvalidDataException) { oversize = true; }
            if (!oversize) throw new InvalidOperationException("Oversize buffer silently accepted.");
            CancellationSelfTest();
            Console.WriteLine("PASS: clean auto-copy frame, UTC and text round-trip; failed/missing frame rejected");
            OutputAutoCopy.SelfTest();
        }
    }
}
