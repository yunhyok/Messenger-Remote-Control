using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemoteMonitorMaster
{
    internal static class AppInfo
    {
        public const string Name = "Messenger Remote Control Master";
        public const string Version = RemoteMonitorLink.LinkVersion.AppValue;
        public const string Title = Name + " v" + Version;
        public const string InstanceMutexName = @"Local\RemoteMonitorMaster";

        public static void RejectAutomationInDiagnosticBuild()
        {
            throw new MonitorException("READ_ONLY_DIAGNOSTIC_BUILD", "Legacy automatic input and sending remain disabled; use only the explicit supervised one-shot test.");
        }
    }

    internal static class SystemInfo
    {
        public static readonly int FrameworkRelease = ReadFrameworkRelease();

        private static int ReadFrameworkRelease()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    return key == null ? 0 : Convert.ToInt32(key.GetValue("Release", 0), CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                return 0;
            }
        }
    }

    internal sealed class MonitorException : Exception
    {
        public string ReasonCode { get; private set; }

        public MonitorException(string reasonCode, string message)
            : base(message)
        {
            ReasonCode = reasonCode;
        }

        public MonitorException(string reasonCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ReasonCode = reasonCode;
        }
    }

    internal static class Protocol
    {
        public static string CreateDiagnosticDigits()
        {
            var bytes = new byte[6];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var digits = new char[6];
            for (var i = 0; i < digits.Length; i++) digits[i] = (char)('2' + (bytes[i] & 7));
            // A short per-run test label, not a credential or a globally unique message identifier.
            return new string(digits);
        }

        internal static bool IsDiagnosticMarker(string stage, string marker)
        {
            var prefix = stage == "MESSAGE" ? "M" : stage == "DRAFT" ? "D" : null;
            return prefix != null && marker != null &&
                Regex.IsMatch(marker, @"\A" + prefix + @"[2-9]{6}\z", RegexOptions.CultureInvariant);
        }

        private static readonly Regex Ping = new Regex(
            @"\A!RM PING ([A-Za-z0-9][A-Za-z0-9._-]{0,63})\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool TryParsePing(string message, out string token)
        {
            token = null;
            if (message == null)
            {
                return false;
            }

            var match = Ping.Match(message);
            if (!match.Success)
            {
                return false;
            }

            token = match.Groups[1].Value;
            return true;
        }

        public static string Pong(string token)
        {
            return "[RM-OUT] PONG " + token;
        }
    }

    internal sealed class TokenStore
    {
        private static readonly Regex HashLine = new Regex(@"\A[A-F0-9]{64}\z", RegexOptions.CultureInvariant);
        private static readonly Mutex ReservationMutex = new Mutex(false, @"Local\RemoteMonitorMaster.TokenStore.v1");
        private readonly object sync = new object();
        private readonly string path;
        private readonly HashSet<string> hashes = new HashSet<string>(StringComparer.Ordinal);

        public TokenStore(string path)
        {
            this.path = path;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidDataException("Token state path has no directory.");
            }

            Directory.CreateDirectory(directory);
            if (!File.Exists(path))
            {
                return;
            }

            foreach (var rawLine in File.ReadAllLines(path, Encoding.ASCII))
            {
                var line = rawLine.Trim();
                if (!HashLine.IsMatch(line))
                {
                    throw new InvalidDataException("Token state contains an invalid record.");
                }

                hashes.Add(line);
            }
        }

        // Allocation hint from this loaded snapshot; only TryReserve authorizes an action atomically.
        internal bool Contains(string token)
        {
            lock (sync) return hashes.Contains(Hash(token));
        }

        public bool TryReserve(string token)
        {
            var hash = Hash(token);
            lock (sync)
            {
                if (hashes.Contains(hash))
                {
                    return false;
                }

                var ownsMutex = false;
                try
                {
                    try
                    {
                        ownsMutex = ReservationMutex.WaitOne(TimeSpan.FromSeconds(5));
                    }
                    catch (AbandonedMutexException)
                    {
                        ownsMutex = true;
                    }

                    if (!ownsMutex)
                    {
                        throw new IOException("Timed out waiting for the token reservation lock.");
                    }

                    using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                    {
                        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                line = line.Trim();
                                if (!HashLine.IsMatch(line))
                                {
                                    throw new InvalidDataException("Token state contains an invalid record.");
                                }

                                hashes.Add(line);
                            }
                        }

                        if (hashes.Contains(hash))
                        {
                            return false;
                        }

                        var bytes = Encoding.ASCII.GetBytes(hash + Environment.NewLine);
                        stream.Seek(0, SeekOrigin.End);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                }
                finally
                {
                    if (ownsMutex)
                    {
                        ReservationMutex.ReleaseMutex();
                    }
                }

                hashes.Add(hash);
                return true;
            }
        }

        public static string Hash(string token)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var value in bytes)
                {
                    builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }
    }

    internal sealed class AuditLog : IDisposable
    {
        internal struct LogField
        {
            public readonly string Key;
            public readonly object Value;

            public LogField(string key, object value)
            {
                Key = key;
                Value = value;
            }
        }

        private readonly object sync = new object();
        private StreamWriter writer;
        private bool disposed;
        private readonly byte[] fingerprintKey;

        public string FolderPath { get; private set; }
        public string FilePath { get; private set; }

        public AuditLog(string folderPath = null)
        {
            fingerprintKey = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(fingerprintKey);
            }

            FolderPath = folderPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RemoteMonitorMaster",
                "logs");
            Directory.CreateDirectory(FolderPath);
            FilePath = Path.Combine(
                FolderPath,
                "remote-monitor-master-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                "-pid" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".log");
            writer = OpenWriter(FileMode.CreateNew);
        }

        private StreamWriter OpenWriter(FileMode mode)
        {
            return new StreamWriter(new FileStream(FilePath, mode, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)) { AutoFlush = true };
        }

        public static LogField Field(string key, object value)
        {
            return new LogField(key, value);
        }

        public string Fingerprint(string value)
        {
            using (var hmac = new HMACSHA256(fingerprintKey))
            {
                var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var item in bytes)
                {
                    builder.Append(item.ToString("X2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        public void Write(string level, string code, params LogField[] fields)
        {
            var utc = DateTime.UtcNow;
            var local = DateTime.Now;
            var line = new StringBuilder(256)
                .Append("utc=\"").Append(Escape(utc.ToString("o", CultureInfo.InvariantCulture))).Append("\"\t")
                .Append("local=\"").Append(Escape(local.ToString("o", CultureInfo.InvariantCulture))).Append("\"\t")
                .Append("app=\"").Append(AppInfo.Name).Append("\"\t")
                .Append("version=\"").Append(AppInfo.Version).Append("\"\t")
                .Append("level=\"").Append(Escape(level)).Append("\"\t")
                .Append("code=\"").Append(Escape(code)).Append('"');

            foreach (var field in fields)
            {
                line.Append('\t')
                    .Append(field.Key)
                    .Append("=\"")
                    .Append(Escape(Convert.ToString(field.Value, CultureInfo.InvariantCulture)))
                    .Append('"');
            }

            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(AuditLog));
                if (writer != null) writer.WriteLine(line.ToString());
                else
                {
                    // After the test, rare lifecycle entries must not keep the attachment file open.
                    using (var append = OpenWriter(FileMode.Append)) append.WriteLine(line.ToString());
                }
            }
        }

        public void WriteException(string code, Exception exception, params LogField[] fields)
        {
            var combined = new LogField[fields.Length + 2];
            Array.Copy(fields, combined, fields.Length);
            combined[fields.Length] = Field("exception_type", exception.GetType().FullName);
            combined[fields.Length + 1] = Field("hresult", "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture));
            Write("ERROR", code, combined);
        }

        public void ReleaseFile()
        {
            lock (sync)
            {
                var active = writer;
                writer = null;
                active?.Dispose();
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                ReleaseFile();
            }
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

    }

    internal static class NativeMethods
    {
        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint Size;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        internal struct ScreenPoint { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowRectangle { public int Left, Top, Right, Bottom; }

        // Only compare successive values on the same thread; these may be DPI-virtualized, unlike UIA bounds.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr window, out WindowRectangle rectangle);

        // UIA bounds use physical pixels; Cursor.Position can be DPI-virtualized on Win7.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetPhysicalCursorPos(out ScreenPoint point);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsChild(IntPtr parent, IntPtr child);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint WinVerifyTrust(
            IntPtr window,
            [In] ref Guid actionId,
            [In, Out] ref WinTrustData trustData);

        public static string WindowClass(IntPtr window)
        {
            var builder = new StringBuilder(256);
            var length = GetClassName(window, builder, builder.Capacity);
            if (length <= 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            return builder.ToString();
        }

        public static uint VerifyAuthenticode(string path)
        {
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                FilePath = path
            };
            var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            var marshaled = false;
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                marshaled = true;
                var trustData = new WinTrustData
                {
                    Size = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    UiChoice = 2,
                    RevocationChecks = 0,
                    UnionChoice = 1,
                    FileInfo = fileInfoPointer,
                    StateAction = 1,
                    ProviderFlags = 0x10 | 0x1000,
                    UiContext = 0
                };
                var action = WinTrustActionGenericVerifyV2;
                var result = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
                trustData.StateAction = 2;
                WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
                return result;
            }
            finally
            {
                if (marshaled)
                {
                    Marshal.DestroyStructure(fileInfoPointer, typeof(WinTrustFileInfo));
                }

                Marshal.FreeCoTaskMem(fileInfoPointer);
            }
        }
    }

    internal static class CoreSelfTest
    {
        public static int Run()
        {
            var directory = Path.Combine(Path.GetTempPath(), "RemoteMonitorMaster-selftest-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "seen-tokens.txt");
            try
            {
                string token;
                Require(Protocol.TryParsePing("!RM PING abc-123", out token), "valid PING was rejected");
                Require(token == "abc-123", "token changed");
                Require(!Protocol.TryParsePing(" !RM PING abc-123", out token), "leading space was accepted");
                Require(!Protocol.TryParsePing("!RM  PING abc-123", out token), "non-canonical spacing was accepted");
                Require(!Protocol.TryParsePing("!RM PING abc-123\r\n", out token), "newline-terminated PING was accepted");
                Require(!Protocol.TryParsePing("[RM-OUT] PONG abc-123", out token), "own output was parsed");
                Require(!Protocol.TryParsePing("2026-09-04 12:00", out token), "timestamp was parsed");
                Require(!Protocol.TryParsePing("!RM PING report.pdf extra", out token), "file-like extra text was parsed");
                Require(Protocol.TryParsePing("!RM PING profile-1", out token), "valid token containing 'file' was rejected");
                Require(Protocol.Pong("abc-123") == "[RM-OUT] PONG abc-123", "PONG format changed");
                Require(!AutomationTarget.MatchesInputMetadata("messageSearch", "Edit"), "message search decoy matched composer input");
                Require(AutomationTarget.MatchesInputMetadata("messageInput", "Edit"), "explicit composer input metadata was rejected");
                Require(AutomationTarget.MatchesExcludedMessageMetadata("fileName", "Text"), "file metadata was accepted");
                Require(AutomationTarget.MatchesExcludedMessageMetadata("caption", "Attachment"), "attachment class was accepted");
                Require(AutomationTarget.MatchesExcludedMessageMetadata("message", "FileAttachment"), "file attachment class was accepted");
                Require(AutomationTarget.MatchesExcludedMessageMetadata("downloadButton", "Button"), "download metadata was accepted");
                Require(AutomationTarget.MatchesExcludedMessageMetadata("textMessageFile", "Text"), "camel-case file suffix was accepted");
                Require(!AutomationTarget.MatchesExcludedMessageMetadata("messageBody", "Text"), "plain body metadata was excluded");
                Require(!AutomationTarget.MatchesExcludedMessageMetadata("profileName", "profileText"), "profile metadata was treated as a file");
                Require(AutomationTarget.IsMessageEligible(1, true, false), "visible body was skipped");
                Require(!AutomationTarget.IsMessageEligible(1, false, false), "poll accepted an offscreen body");
                Require(AutomationTarget.IsMessageEligible(1, false, true), "baseline skipped an exposed offscreen body");
                Require(!AutomationTarget.IsMessageEligible(2, true, true), "baseline accepted an ambiguous envelope");
                Require(!AutomationTarget.IsMessageEligible(2, true, false), "poll accepted a hidden second body");
                TestElementIdentity();
                ProbeElementCache.RunSelfTest();
                TestReadOnlyGuards();
                TestPointerAndShapeHints();
                ReadOnlyPair.RunSelfTest();
                SendMetadataProbe.RunSelfTest();
                UiaPointProbe.RunSelfTest();
                SupervisedSendTest.RunSelfTest();
                MouseClickInput.RunSelfTest();
                ReceiveMetadata.RunSelfTest();
                OperationalTarget.RunSelfTest();
                ReceiveProbe.RunSelfTest();
                RoundTripTest.RunSelfTest(directory);
                RoundTripSession.RunSelfTest(directory);
                StatusSession.RunSelfTest(directory);
                PcStatusReport.RunSelfTest();
                ReadOnlyCommands.RunSelfTest(directory);
                PowerSiOutputHistory.RunSelfTest();
                Require(AutomationTarget.RootRejectionReason(false, null) == "UNSUPPORTED_UIA_ROOT_MISSING",
                    "missing root did not have a distinct failure");
                Require(AutomationTarget.RootRejectionReason(true, System.Windows.Automation.ControlType.Window) == null,
                    "Window root was rejected by type guard");
                foreach (var type in new[] { System.Windows.Automation.ControlType.Pane,
                    System.Windows.Automation.ControlType.Document, System.Windows.Automation.ControlType.Custom, null })
                {
                    Require(AutomationTarget.RootRejectionReason(true, type) == "UNSUPPORTED_UIA_ROOT_TYPE",
                        "non-Window root bypassed the safety guard");
                }
                Require(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3) == AppInfo.Version,
                    "title and assembly versions differ");
                Require(AppInfo.InstanceMutexName.IndexOf(AppInfo.Version, StringComparison.Ordinal) < 0,
                    "single-instance mutex must remain stable across versions");

                var store = new TokenStore(path);
                var concurrentStore = new TokenStore(path);
                Require(!store.Contains("abc-123"), "unused token reported as reserved");
                Require(store.TryReserve("abc-123"), "first token was rejected");
                Require(store.Contains("abc-123") && new TokenStore(path).Contains("abc-123"), "reserved token missing from allocator snapshot");
                Require(!concurrentStore.TryReserve("abc-123"), "concurrently opened duplicate store accepted a token");
                Require(!store.TryReserve("abc-123"), "duplicate token was accepted");
                Require(!new TokenStore(path).TryReserve("abc-123"), "persisted duplicate was accepted");
                Require(NativeMethods.VerifyAuthenticode(path) != 0, "an unsigned state file was reported as trusted");
                TestMainForm(directory);
                TestAuditLogRelease(directory);
                TestReceiveForm(directory);
                TestRoundTripForm(directory);
                TestPcStatusForm(directory);
                TestOperatingForm(directory);
                MasterHubForm.RunSelfTest(directory);
                TestEnvironmentInterruption(directory);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory);
                }
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Self-test failed: " + message);
            }
        }

        private static void RequireLogFileReleased(AuditLog audit)
        {
            using (var reader = new FileStream(audit.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Require(reader.Length > 0, "released log is empty");
        }

        private static void TestAuditLogRelease(string directory)
        {
            var audit = new AuditLog(directory);
            try
            {
                audit.Write("INFO", "TEST_BEFORE_RELEASE", AuditLog.Field("text", "한글"));
                bool blocked = false;
                try { using (var reader = new FileStream(audit.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) { } }
                catch (IOException ex) { blocked = (ex.HResult & 0xffff) == 32; }
                Require(blocked, "active writer sharing conflict was not reproduced");
                audit.ReleaseFile();
                audit.ReleaseFile();
                RequireLogFileReleased(audit); // AuditLog and application need not be disposed.
                var before = File.ReadAllText(audit.FilePath);
                Require(before.Contains("TEST_BEFORE_RELEASE") && before.Contains("한글"), "release lost buffered UTF-8 data");
                using (var upload = new FileStream(audit.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    blocked = false;
                    try { audit.Write("INFO", "TEST_BLOCKED_APPEND"); }
                    catch (IOException ex) { blocked = (ex.HResult & 0xffff) == 32; }
                    Require(blocked, "upload conflict was hidden from the logging caller");
                }
                audit.Write("INFO", "TEST_AFTER_RELEASE");
                RequireLogFileReleased(audit);
                var after = File.ReadAllText(audit.FilePath);
                Require(after.StartsWith(before, StringComparison.Ordinal) && after.Contains("TEST_AFTER_RELEASE") &&
                    !after.Contains("TEST_BLOCKED_APPEND") && File.ReadAllLines(audit.FilePath).Length == 2,
                    "late append replaced or corrupted the completed log");
                audit.Dispose();
                audit.Dispose();
                blocked = false;
                try { audit.Write("INFO", "TEST_AFTER_DISPOSE"); }
                catch (ObjectDisposedException) { blocked = true; }
                Require(blocked, "disposed audit reopened its file");
            }
            finally { audit.Dispose(); File.Delete(audit.FilePath); }
        }

        private static void TestElementIdentity()
        {
            ElementIdentity Identity(string name, string runtime = "1,2", int pid = 10,
                string id = "messageInput", string type = "ControlType.Edit", string className = "Edit",
                string framework = "WinForm", string patterns = "ValuePatternIdentifiers.Pattern")
            {
                return new ElementIdentity(runtime, pid, id, type, className, framework, patterns,
                    name.Length, TokenStore.Hash(name), "<redacted>");
            }

            var original = Identity("empty");
            Require(original.Equals(Identity("empty")), "unchanged identity was rejected");
            Require(original.Matches(Identity("new message"), false), "content Name churn changed structural identity");
            Require(!original.Equals(Identity("other chat")), "header/window Name guard was bypassed");
            Require(!original.Matches(null, false), "null identity was accepted");
            foreach (var changed in new[] { Identity("empty", runtime: "2,3"), Identity("empty", pid: 11),
                Identity("empty", id: "messageSearch"), Identity("empty", type: "ControlType.Text"),
                Identity("empty", className: "Other"), Identity("empty", framework: "Other"),
                Identity("empty", patterns: "") })
            {
                Require(!original.Matches(changed, false), "structural identity change was accepted");
            }
        }

        private static void TestReceiveForm(string directory)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object Field(ReceiveForm form, string name) { return typeof(ReceiveForm).GetField(name, flags).GetValue(form); }
            void Set(ReceiveForm form, string name, object value) { typeof(ReceiveForm).GetField(name, flags).SetValue(form, value); }
            void Call(ReceiveForm form, string name, params object[] args) { typeof(ReceiveForm).GetMethod(name, flags).Invoke(form, args); }
            var audit = new AuditLog(directory);
            string secretCode = null;
            try
            {
                using (var form = new ReceiveForm(audit))
                {
                    Require(form.Text == AppInfo.Title + " - RECEIVE / READ ONLY", "receive title/version missing");
                    var code = (System.Windows.Forms.TextBox)Field(form, "code");
                    var confirm = (System.Windows.Forms.CheckBox)Field(form, "confirmation");
                    var start = (System.Windows.Forms.Button)Field(form, "start");
                    var stop = (System.Windows.Forms.Button)Field(form, "stop");
                    var timer = (System.Windows.Forms.Timer)Field(form, "countdown");
                    secretCode = (string)Field(form, "marker");
                    Require(Protocol.IsDiagnosticMarker("MESSAGE", secretCode) && code.Text == "WAIT" && code.ReadOnly &&
                        code.Font.FontFamily.Name == "Consolas" && code.Font.Size >= 20 && !start.Enabled && !stop.Enabled,
                        "receive code leaked before baseline or initial controls invalid");
                    foreach (System.Windows.Forms.Control control in form.Controls)
                    {
                        Require(!control.Text.Contains(secretCode), "phone code visible before ready");
                        if (control is System.Windows.Forms.Button || control is System.Windows.Forms.TextBox || control is System.Windows.Forms.CheckBox)
                            Require(form.ClientRectangle.Contains(control.Bounds), "receive minimum size clips a control");
                    }
                    Call(form, "Begin");
                    Call(form, "Tick", null, EventArgs.Empty); // Disabled timer returns before any native/UIA call.
                    Require(!timer.Enabled && !(bool)Field(form, "started"), "unconfirmed receive run started");
                    confirm.Checked = true;
                    Call(form, "Begin");
                    Require(timer.Enabled && !start.Enabled && !confirm.Enabled && stop.Enabled, "receive run was not single-use");
                    timer.Stop();
                    Set(form, "busy", true); // Synthetic UI-only progress. Never invoke the backend or native input.
                    var run = (int)Field(form, "generation");
                    Call(form, "ApplyProgress", "READY_TO_RECEIVE", run - 1);
                    Require(code.Text == "WAIT", "stale callback revealed phone code");
                    Call(form, "ApplyProgress", "BASELINE_METADATA", run);
                    Require(code.Text == "WAIT", "metadata phase revealed phone code early");
                    Call(form, "ApplyProgress", "READY_TO_RECEIVE", run);
                    Require(code.Text == secretCode && ((System.Windows.Forms.Label)Field(form, "status")).ForeColor == System.Drawing.Color.DarkGreen,
                        "ready code or green signal missing");
                    Call(form, "Stop", "TEST_STOP");
                    Call(form, "ApplyProgress", "READY_TO_RECEIVE", run);
                    Require(((System.Windows.Forms.Label)Field(form, "status")).Text.Contains("TEST_STOP") && !start.Enabled &&
                        !(bool)Field(form, "closing"), "late ready overrode Stop or reopened the test");
                    Set(form, "busy", false);
                }
                audit.Dispose();
                Require(!File.ReadAllText(audit.FilePath).Contains(secretCode), "receive UI logged raw phone code");
            }
            finally { audit.Dispose(); File.Delete(audit.FilePath); } // Only this test's exact generated log, as in TestMainForm.
        }

        private static void TestRoundTripForm(string directory)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object Field(ReceiveForm form, string name) { return typeof(ReceiveForm).GetField(name, flags).GetValue(form); }
            void Set(ReceiveForm form, string name, object value) { typeof(ReceiveForm).GetField(name, flags).SetValue(form, value); }
            void Call(ReceiveForm form, string name, params object[] args) { typeof(ReceiveForm).GetMethod(name, flags).Invoke(form, args); }
            var audit = new AuditLog(directory);
            string incoming = null, reply = null, second = null, secondReply = null;
            try
            {
                using (var form = new ReceiveForm(audit, true))
                {
                    Require(form.Text == AppInfo.Title + " - SESSION / TWO SENDS MAX", "session title/version missing");
                    incoming = (string)Field(form, "marker");
                    reply = (string)Field(form, "reply");
                    second = (string)Field(form, "secondMarker");
                    secondReply = (string)Field(form, "secondReply");
                    var code = (System.Windows.Forms.TextBox)Field(form, "code");
                    var output = (System.Windows.Forms.Label)Field(form, "replyCode");
                    var confirmation = (System.Windows.Forms.CheckBox)Field(form, "confirmation");
                    var start = (System.Windows.Forms.Button)Field(form, "start");
                    var timer = (System.Windows.Forms.Timer)Field(form, "countdown");
                    Require(code.Text == "WAIT" && output.Text.Contains(reply) && output.Text.Contains(secondReply) &&
                        incoming != second && Protocol.IsDiagnosticMarker("MESSAGE", second) &&
                        reply == "D" + incoming.Substring(1) && secondReply == "D" + second.Substring(1) &&
                        output.Text.Contains("직접 입력하지") && output.Font.Size < code.Font.Size &&
                        !start.Enabled && Field(form, "session") == null && confirmation.Text.Contains("첨부") &&
                        confirmation.Text.Contains("최대 2회") && confirmation.Text.Contains("각각 한 번"),
                        "session consent or displayed fixed outputs missing");
                    foreach (System.Windows.Forms.Control control in form.Controls)
                    {
                        Require(!control.Text.Contains(incoming) && !control.Text.Contains(second), "session code revealed early");
                        Require(form.ClientRectangle.Contains(control.Bounds),
                            "roundtrip control clipped: " + control.GetType().Name + " " + control.Bounds);
                    }
                    Call(form, "Begin");
                    Require(Field(form, "session") == null && !timer.Enabled, "session started without local approval");
                    confirmation.Checked = true;
                    Call(form, "Begin");
                    var session = (RoundTripSession)Field(form, "session");
                    Require(timer.Enabled && session.FirstReply == reply && session.SecondReply == secondReply && !session.Cancelled && !start.Enabled,
                        "session did not bind two fixed approvals to displayed replies");
                    timer.Stop();
                    Set(form, "busy", true); // UI-only synthetic progress; never run the backend or native input.
                    var generation = (int)Field(form, "generation");
                    Call(form, "ApplySessionProgress", 0, "READY_TO_RECEIVE", generation - 1);
                    Require(code.Text == "WAIT", "stale roundtrip READY revealed code");
                    Call(form, "ApplySessionProgress", 0, "READY_TO_RECEIVE", generation);
                    Require(code.Text == incoming &&
                        ((System.Windows.Forms.Label)Field(form, "status")).Text.Contains("휴대폰에서 M코드만") &&
                        ((System.Windows.Forms.Label)Field(form, "status")).Text.Contains("D는 보내지 마세요"),
                        "roundtrip READY did not explain phone-first M code");
                    Call(form, "ApplySessionProgress", 0, "ROUND_COMPLETE", generation);
                    Require(code.Text == "WAIT", "old M code remained active between rounds");
                    Call(form, "ApplySessionProgress", 1, "BASELINE", generation);
                    Require(code.Text == "WAIT", "second code revealed before its baseline");
                    Call(form, "ApplySessionProgress", 1, "READY_TO_RECEIVE", generation);
                    Call(form, "ApplySessionProgress", 0, "READY_TO_RECEIVE", generation);
                    Require(code.Text == second && ((System.Windows.Forms.Label)Field(form, "status")).Text.Contains("READY 2/2"),
                        "second READY missing or old round callback overrode it");
                    Call(form, "Stop", "TEST_STOP");
                    Call(form, "ApplySessionProgress", 1, "ROUNDTRIP_SENDING", generation);
                    bool activeWriter = false;
                    try { using (var reader = new FileStream(audit.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) { } }
                    catch (IOException ex) { activeWriter = (ex.HResult & 0xffff) == 32; }
                    Require(activeWriter, "busy Stop released the log before the worker result");
                    Require(session.Cancelled && session.GetConsent(0).Cancelled && session.GetConsent(1).Cancelled &&
                        code.Text == "WAIT" && !start.Enabled && ((System.Windows.Forms.Label)Field(form, "status")).Text.Contains("TEST_STOP"),
                        "Stop failed to revoke both consents or late progress overrode it");
                    var partial = new RoundTripSession(incoming, second, true);
                    var firstSend = partial.GetConsent(0);
                    Require(firstSend.TryConsume(reply) && firstSend.TryCommitMove() && firstSend.TryCommitWrite() && firstSend.TryCommit(),
                        "synthetic first-send state invalid");
                    var writing = partial.GetConsent(1);
                    Require(writing.TryConsume(secondReply) && writing.TryCommitMove() && writing.TryCommitWrite(), "synthetic write state invalid");
                    Set(form, "session", partial);
                    Set(form, "closing", true);
                    Call(form, "ShowResult", "TEST_WRITE_FAILURE", generation);
                    RequireLogFileReleased(audit); // Completed worker, form still alive (also on failure).
                    Require(((System.Windows.Forms.TextBox)Field(form, "details")).Text.Contains("LOG READY"), "file release notice missing");
                    Require(!(bool)Field(form, "closing") && ((System.Windows.Forms.TextBox)Field(form, "details")).Text.Contains("초안을 그대로") &&
                        ((System.Windows.Forms.TextBox)Field(form, "details")).Text.Contains("입력 후 클릭하지 못한 회차"),
                        "pending draft warning was hidden during close");
                    Require(writing.TryCommit(), "synthetic click state invalid");
                    Set(form, "closing", true);
                    Call(form, "ShowResult", SupervisedSendTest.MouseReleaseWarning, generation);
                    Require(!(bool)Field(form, "closing") && ((System.Windows.Forms.TextBox)Field(form, "details")).Text.Contains(SupervisedSendTest.MouseReleaseWarning),
                        "mouse-release warning was hidden during close");
                    Set(form, "busy", false);
                }
                audit.Dispose();
                var contents = File.ReadAllText(audit.FilePath);
                Require(!contents.Contains(incoming) && !contents.Contains(reply) && !contents.Contains(second) && !contents.Contains(secondReply),
                    "session UI logged raw codes");
            }
            finally { audit.Dispose(); File.Delete(audit.FilePath); }
        }

        private static void TestPcStatusForm(string directory)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object Field(ReceiveForm form, string name) { return typeof(ReceiveForm).GetField(name, flags).GetValue(form); }
            void Set(ReceiveForm form, string name, object value) { typeof(ReceiveForm).GetField(name, flags).SetValue(form, value); }
            void Call(ReceiveForm form, string name, params object[] args) { typeof(ReceiveForm).GetMethod(name, flags).Invoke(form, args); }
            var audit = new AuditLog(directory);
            try
            {
                using (var form = new ReceiveForm(audit, true, true))
                {
                    Require(form.Text == AppInfo.Title + " - PC STATUS / ONE REPLY", "PC status title/version missing");
                    var code = (System.Windows.Forms.TextBox)Field(form, "code");
                    var confirm = (System.Windows.Forms.CheckBox)Field(form, "confirmation");
                    var output = (System.Windows.Forms.Label)Field(form, "replyCode");
                    Require(code.Text == "WAIT" && !output.Text.Contains((string)Field(form, "secondReply")) &&
                        confirm.Text.Contains("상태") && confirm.Text.Contains("1회"), "PC status scope/hidden future reply invalid");
                    Call(form, "Begin");
                    Require(Field(form, "session") == null, "status query started without approval");
                    confirm.Checked = true;
                    Call(form, "Begin");
                    ((System.Windows.Forms.Timer)Field(form, "countdown")).Stop();
                    var session = (RoundTripSession)Field(form, "session");
                    Require(session.MaximumReplies == 1 && session.GetConsent(0).IsPcStatus, "status UI selected legacy backend");
                    Set(form, "busy", true); // No backend run or real input; synthetic received phases only.
                    var generation = (int)Field(form, "generation");
                    Call(form, "ApplySessionProgress", 0, "READY_TO_RECEIVE", generation);
                    var ready = ((System.Windows.Forms.Label)Field(form, "status")).Text;
                    Require(code.Text == (string)Field(form, "marker") && ready.Contains("READY"), "status request not shown at READY");
                    Call(form, "ApplySessionProgress", 1, "READY_TO_RECEIVE", generation);
                    Require(((System.Windows.Forms.Label)Field(form, "status")).Text == ready, "status UI opened second round");
                    foreach (var phase in new[] { "SLAVE_QUERYING", "ROUNDTRIP_SENDING", "ROUND_COMPLETE" })
                    {
                        Call(form, "ApplySessionProgress", 0, phase, generation);
                        Require(code.Text == "WAIT" && output.BackColor == System.Drawing.Color.LightYellow &&
                            !output.Text.StartsWith("종료"), "status UI implied completion before worker/log finish");
                    }
                    Call(form, "Stop", "TEST_STOP");
                    Require(session.Cancelled && session.GetConsent(0).Cancelled, "status Stop did not revoke approval");
                    Require(output.Text.Contains("중단 요청 중"), "busy Stop implied worker completion");
                    Set(form, "busy", false);
                    Call(form, "ShowResult", "TEST_STATUS_RESULT", generation);
                    RequireLogFileReleased(audit);
                    Require(((System.Windows.Forms.TextBox)Field(form, "details")).Text.Contains("LOG READY"), "status result omitted live-app log attachment");
                    Require(output.Text.StartsWith("종료 / LOG READY") && output.BackColor == System.Drawing.SystemColors.Control,
                        "log release omitted visible final state");
                    Call(form, "ApplySessionProgress", 0, "ROUNDTRIP_SENDING", generation);
                    Require(output.Text.StartsWith("종료 / LOG READY"), "late progress replaced final state");
                    foreach (System.Windows.Forms.Control control in form.Controls)
                        Require(form.ClientRectangle.Contains(control.Bounds), "PC status control is clipped");
                }
            }
            finally { audit.Dispose(); File.Delete(audit.FilePath); }
        }

        private static void TestOperatingForm(string directory)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object Field(ReceiveForm form, string name) { return typeof(ReceiveForm).GetField(name, flags).GetValue(form); }
            void Set(ReceiveForm form, string name, object value) { typeof(ReceiveForm).GetField(name, flags).SetValue(form, value); }
            void Call(ReceiveForm form, string name, params object[] args) { typeof(ReceiveForm).GetMethod(name, flags).Invoke(form, args); }
            var audit = new AuditLog(directory);
            try
            {
                using (var form = new ReceiveForm(audit, true, true, null, true))
                {
                    var code = (System.Windows.Forms.TextBox)Field(form, "code");
                    var confirm = (System.Windows.Forms.CheckBox)Field(form, "confirmation");
                    var notice = (System.Windows.Forms.Label)Field(form, "replyCode");
                    Require(form.Text.Contains(AppInfo.Version) && form.Text.Contains("REPEAT UNTIL STOP") &&
                        confirm.Text.Contains("Stop") && code.Text == "WAIT", "operating mode/scope not visible");
                    Call(form, "Begin");
                    Require(Field(form, "statusSession") == null, "continuous mode began without approval");
                    confirm.Checked = true;
                    Call(form, "Begin");
                    ((System.Windows.Forms.Timer)Field(form, "countdown")).Stop();
                    var operation = (StatusSession)Field(form, "statusSession");
                    Require(operation != null && Field(form, "session") == null, "operating UI selected one-shot backend");
                    Set(form, "busy", true); // UI-only notifications; never execute KI or native input.
                    var generation = (int)Field(form, "generation");
                    Call(form, "ApplyOperatingProgress", 0, "READY_TO_RECEIVE", "M234567", generation);
                    Require(code.Text == "M234567", "READY ignored allocated marker");
                    Call(form, "ApplyOperatingProgress", 0, "ROUNDTRIP_SENDING", "M234567", generation);
                    Require(code.Text == "WAIT" && !notice.Text.Contains("LOG READY"), "in-flight send implied final log readiness");
                    Call(form, "ApplyOperatingProgress", 1, "READY_TO_RECEIVE", "M345678", generation);
                    Require(code.Text == "M345678" && !notice.Text.StartsWith("종료"), "next READY ended operating mode");
                    Call(form, "Stop", "TEST_STOP");
                    Require(operation.Cancelled && notice.Text.Contains("중단 요청 중"), "busy operating Stop did not cancel/preserve pending state");
                    Set(form, "busy", false);
                    Call(form, "ShowResult", "TEST_OPERATING_STOP", generation);
                    RequireLogFileReleased(audit);
                    Require(notice.Text.StartsWith("종료 / LOG READY"), "operating stop did not release log");
                    Call(form, "ApplyOperatingProgress", 2, "READY_TO_RECEIVE", "M456789", generation);
                    Require(code.Text == "WAIT" && notice.Text.StartsWith("종료 / LOG READY"), "late operating progress resumed stopped UI");
                }
                using (var form = new ReceiveForm(audit, true, true, null, true))
                {
                    ((System.Windows.Forms.CheckBox)Field(form, "confirmation")).Checked = true;
                    Call(form, "Begin");
                    ((System.Windows.Forms.Timer)Field(form, "countdown")).Stop();
                    var operation = (StatusSession)Field(form, "statusSession");
                    System.Threading.Tasks.Task.Run(() => Call(form, "OnSessionSwitch", null,
                        new Microsoft.Win32.SessionSwitchEventArgs(Microsoft.Win32.SessionSwitchReason.SessionLock))).GetAwaiter().GetResult();
                    Require(operation.Cancelled && (bool)Field(form, "stopped"), "operating lock notification did not revoke before UI dispatch");
                    Call(form, "Tick", null, EventArgs.Empty);
                    Require(!((System.Windows.Forms.Timer)Field(form, "countdown")).Enabled, "operating notification resumed countdown");
                }
            }
            finally { audit.Dispose(); File.Delete(audit.FilePath); }
        }

        private static void TestEnvironmentInterruption(string directory)
        {
            // Synthetic received notifications only: no real lock, suspend, target read or native input.
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object Field(ReceiveForm form, string name) { return typeof(ReceiveForm).GetField(name, flags).GetValue(form); }
            void Set(ReceiveForm form, string name, object value) { typeof(ReceiveForm).GetField(name, flags).SetValue(form, value); }
            object Call(ReceiveForm form, string name, params object[] args) { return typeof(ReceiveForm).GetMethod(name, flags).Invoke(form, args); }
            void Start(ReceiveForm form)
            {
                ((System.Windows.Forms.CheckBox)Field(form, "confirmation")).Checked = true;
                Call(form, "Begin");
            }
            var audit = new AuditLog(directory);
            try
            {
                var context = System.Threading.SynchronizationContext.Current;
                using (var form = new ReceiveForm(audit, true))
                {
                    Require(ReferenceEquals(context, System.Threading.SynchronizationContext.Current), "OS subscription changed UI context");
                    Start(form);
                    var timer = (System.Windows.Forms.Timer)Field(form, "countdown");
                    var session = (RoundTripSession)Field(form, "session");
                    var approved = (int)Field(form, "approvedEnvironmentRevision");
                    Require(timer.Enabled && !session.Cancelled, "environment test session did not start");
                    Call(form, "OnPowerModeChanged", null, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.StatusChange));
                    Require(!session.Cancelled && !(bool)Call(form, "EnvironmentChanged", approved), "battery status cancelled consent");
                    // Do not pump UI messages: revocation must happen in the received callback itself.
                    System.Threading.Tasks.Task.Run(() => Call(form, "OnSessionSwitch", null,
                        new Microsoft.Win32.SessionSwitchEventArgs(Microsoft.Win32.SessionSwitchReason.SessionLock))).GetAwaiter().GetResult();
                    Require(session.Cancelled && session.GetConsent(0).Cancelled && session.GetConsent(1).Cancelled &&
                        (bool)Field(form, "stopped"), "lock failed to revoke both replies before UI dispatch");
                    Call(form, "Tick", null, EventArgs.Empty); // Epoch mismatch must return before native/UIA work.
                    Call(form, "OnSessionSwitch", null, new Microsoft.Win32.SessionSwitchEventArgs(Microsoft.Win32.SessionSwitchReason.SessionUnlock));
                    Call(form, "OnPowerModeChanged", null, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Resume));
                    Start(form);
                    Require(!timer.Enabled && session.Cancelled && !((System.Windows.Forms.Button)Field(form, "start")).Enabled,
                        "unlock/resume reopened a consumed session");
                }
                foreach (var method in new[] { "OnPowerModeChanged", "OnSessionEnding" })
                {
                    using (var form = new ReceiveForm(audit, true))
                    {
                        Start(form);
                        ((System.Windows.Forms.Timer)Field(form, "countdown")).Stop();
                        object notification = method == "OnPowerModeChanged"
                            ? (object)new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Suspend)
                            : new Microsoft.Win32.SessionEndingEventArgs(Microsoft.Win32.SessionEndReasons.Logoff);
                        Call(form, method, null, notification);
                        Require(((RoundTripSession)Field(form, "session")).Cancelled, "power/logoff failed to cancel");
                        Require(!(notification is Microsoft.Win32.SessionEndingEventArgs ending) || !ending.Cancel, "program vetoed OS logoff");
                    }
                }
                using (var form = new ReceiveForm(audit, true))
                {
                    Start(form);
                    // Force the event-before-publication race: stopped is reset and a new consent is published late.
                    var approved = (int)Field(form, "approvedEnvironmentRevision");
                    Call(form, "InterruptEnvironment", "SESSION_TEST_RACE");
                    Set(form, "stopped", false);
                    var late = new RoundTripSession((string)Field(form, "marker"), (string)Field(form, "secondMarker"), true);
                    Set(form, "session", late);
                    Require((bool)Call(form, "EnvironmentChanged", approved), "epoch lost an event before publication");
                    Call(form, "Tick", null, EventArgs.Empty);
                    Require(late.Cancelled && !((System.Windows.Forms.Timer)Field(form, "countdown")).Enabled,
                        "epoch check failed to revoke late-published approval");
                    RequireLogFileReleased(audit); // Countdown Stop has no worker left to finish.
                    Set(form, "busy", false);
                    Call(form, "ShowResult", SupervisedSendTest.MouseReleaseWarning, (int)Field(form, "generation"));
                    Call(form, "ShowEnvironmentInterruption", (int)Field(form, "generation"), (int)Field(form, "environmentRevision"));
                    Require(((System.Windows.Forms.TextBox)Field(form, "details")).Text.Contains(SupervisedSendTest.MouseReleaseWarning),
                        "late OS notification hid final mouse-release warning");
                    form.Dispose();
                    var revision = (int)Field(form, "environmentRevision");
                    Call(form, "InterruptEnvironment", "SESSION_TEST_AFTER_DISPOSE");
                    Require((int)Field(form, "environmentRevision") == revision && !(bool)Field(form, "environmentReady"),
                        "disposed form accepted an OS callback or retained monitoring state");
                }
                using (var form = new ReceiveForm(audit, true))
                {
                    Set(form, "environmentReady", false); // A refused subscription is fail-closed.
                    Start(form);
                    Require(Field(form, "session") == null && !((System.Windows.Forms.Timer)Field(form, "countdown")).Enabled,
                        "unavailable OS monitor allowed Start");
                }
                audit.Dispose();
                Require(File.ReadAllText(audit.FilePath).Contains("environment_reason=\"SESSION_SessionLock\""), "lock reason missing from audit");
            }
            finally { audit.Dispose(); File.Delete(audit.FilePath); }
        }

        private static void TestMainForm(string directory)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object Field(MainForm form, string name) { return typeof(MainForm).GetField(name, flags).GetValue(form); }
            void Set(MainForm form, string name, object value) { typeof(MainForm).GetField(name, flags).SetValue(form, value); }
            void Call(MainForm form, string name, params object[] args) { typeof(MainForm).GetMethod(name, flags).Invoke(form, args); }
            var audit = new AuditLog(directory);
            string sessionCode = null;
            try
            {
                using (var form = new MainForm(audit))
                {
                    Require(form.Text == AppInfo.Title, "form title/version changed");
                    form.Size = new System.Drawing.Size(760, 460);
                    var buttons = 0;
                    var textBoxes = 0;
                    var heading = false;
                    foreach (System.Windows.Forms.Control control in form.Controls)
                    {
                        if (control.Text == AppInfo.Title + " - NO MANUAL HOVER / ONE SEND MAX") heading = true;
                        if (control is System.Windows.Forms.Button)
                        {
                            buttons++;
                            Require(control.Text == "Test once (5s)" ||
                                control.Text == "Stop" || control.Text == "Open Log Folder", "unexpected supervised-send button");
                        }
                        var textBox = control as System.Windows.Forms.TextBox;
                        if (textBox != null) { textBoxes++; Require(textBox.ReadOnly, "diagnostic text is editable"); }
                        if (control is System.Windows.Forms.Button || textBox != null || control is System.Windows.Forms.CheckBox)
                            Require(form.ClientRectangle.Contains(control.Bounds), "minimum window clips a test control");
                    }
                    Require(heading && buttons == 3 && textBoxes == 3 && typeof(MainForm).GetMethod("CopyCode", flags) == null,
                        "supervised-send UI has missing or obsolete controls");
                    var code = (System.Windows.Forms.TextBox)Field(form, "codeBox");
                    var startButton = (System.Windows.Forms.Button)Field(form, "testButton");
                    var stopButton = (System.Windows.Forms.Button)Field(form, "stopButton");
                    var confirmation = (System.Windows.Forms.CheckBox)Field(form, "confirmSendCheckBox");
                    var timer = (System.Windows.Forms.Timer)Field(form, "probeTimer");
                    var details = (System.Windows.Forms.TextBox)Field(form, "detailsBox");
                    sessionCode = code.Text;
                    Require(Protocol.IsDiagnosticMarker("DRAFT", sessionCode) && code.Font.Size >= 20 &&
                        code.Font.FontFamily.Name == "Consolas", "test code is not a large monospaced D code");
                    Require(!confirmation.Checked && confirmation.Enabled && !startButton.Enabled && !stopButton.Enabled &&
                        confirmation.Text.Contains("separate self-chat") && confirmation.Text.Contains("EMPTY") &&
                        confirmation.Text.Contains("automatic code entry once") && confirmation.Text.Contains("at most ONE real message") &&
                        confirmation.Text.Contains("actual mouse click") && confirmation.Text.Contains("moving the pointer") &&
                        !confirmation.Text.Contains("will hover"), "fresh explicit positioning/entry/click confirmation is missing");
                    var generation = Field(form, "generation");
                    Call(form, "BeginProbe");
                    Call(form, "ProbeTimerTick", null, EventArgs.Empty); // A disabled timer must return before native/UIA calls.
                    Require(!timer.Enabled && generation.Equals(Field(form, "generation")), "unconfirmed test started");

                    Set(form, "busy", true); // UI-only pending call, never execute the real provider.
                    confirmation.Checked = true;
                    Call(form, "UpdateButtons");
                    Call(form, "BeginProbe");
                    Require(!startButton.Enabled && !confirmation.Enabled && stopButton.Enabled &&
                        generation.Equals(Field(form, "generation")), "busy UI accepted a send test");
                    Set(form, "busy", false);
                    confirmation.Checked = false;
                    Call(form, "UpdateButtons");
                    confirmation.Checked = true;
                    Require(startButton.Enabled, "confirmation did not enable the send test");
                    Call(form, "BeginProbe");
                    var consent = (SupervisedSendTest.Consent)Field(form, "activeConsent");
                    Require(timer.Enabled && timer.Interval == 5000 && (bool)Field(form, "testStarted") &&
                        !confirmation.Checked && !confirmation.Enabled && !startButton.Enabled &&
                        stopButton.Enabled && consent != null && !consent.Attempted && !consent.WriteAttempted,
                        "countdown did not consume confirmation and lock this session");
                    generation = Field(form, "generation");
                    confirmation.Checked = true; // Programmatic events cannot bypass the permanent session latch.
                    Call(form, "BeginProbe");
                    Require(generation.Equals(Field(form, "generation")), "duplicate event bypassed the session latch");
                    Call(form, "StopProbe", "SELF_TEST_STOP");
                    Require(!timer.Enabled && consent.Cancelled && !consent.TryConsume(sessionCode) &&
                        !confirmation.Checked && !confirmation.Enabled && !startButton.Enabled &&
                        (bool)Field(form, "stopRequested") && !generation.Equals(Field(form, "generation")),
                        "countdown Stop retained consent or unlocked this session");
                    var stopped = details.Text;
                    Call(form, "ProbeTimerTick", null, EventArgs.Empty);
                    Require(details.Text == stopped && !(bool)Field(form, "busy"), "late timer resumed a stopped test");
                    confirmation.Checked = true;
                    generation = Field(form, "generation");
                    Call(form, "BeginProbe");
                    Require(!timer.Enabled && generation.Equals(Field(form, "generation")), "cancelled session restarted");
                    Call(form, "FailProbe", "SELF_TEST_PRIVATE_ERROR", new InvalidOperationException("PRIVATE_UI_TEXT " + sessionCode));
                    Require(!details.Text.Contains("PRIVATE_UI_TEXT") && !details.Text.Contains(sessionCode) &&
                        details.Text.Contains("No text entry or Send click was attempted"), "failure exposed private text or claimed an attempt");
                }

                using (var form = new MainForm(audit))
                {
                    Require(((System.Windows.Forms.TextBox)Field(form, "codeBox")).Text == sessionCode, "the app-session code changed");
                    var confirmation = (System.Windows.Forms.CheckBox)Field(form, "confirmSendCheckBox");
                    var timer = (System.Windows.Forms.Timer)Field(form, "probeTimer");
                    var details = (System.Windows.Forms.TextBox)Field(form, "detailsBox");
                    confirmation.Checked = true;
                    Call(form, "BeginProbe");
                    timer.Stop(); // Synthetic committed consent only: no native target, Run or Invoke call.
                    var consent = (SupervisedSendTest.Consent)Field(form, "activeConsent");
                    Require(consent.TryConsume(sessionCode) && consent.TryCommitMove() && consent.TryCommitWrite() && consent.TryCommit(), "synthetic send consent did not commit");
                    Set(form, "busy", true);
                    Call(form, "StopProbe", "SELF_TEST_ATTEMPTED_STOP");
                    Require(consent.Attempted && (bool)Field(form, "busy") && details.Text.Contains("may still be pending") &&
                        details.Text.Contains("cannot undo") && !details.Text.Contains("No text entry or Send click was attempted"),
                        "Stop lost the committed-send warning or pretended to abort it");
                    Call(form, "FailProbe", "SELF_TEST_ATTEMPTED_FAILURE", new InvalidOperationException("PRIVATE_PROVIDER_TEXT"));
                    Require(details.Text.Contains("may still be pending") && !details.Text.Contains("PRIVATE_PROVIDER_TEXT"),
                        "failure lost the sticky send warning or exposed provider text");
                    var closing = new System.Windows.Forms.FormClosingEventArgs(System.Windows.Forms.CloseReason.UserClosing, false);
                    Call(form, "MainFormClosing", form, closing);
                    Require(closing.Cancel && !confirmation.Enabled && details.Text.Contains("may still be pending"),
                        "close aborted an in-flight send or lost its warning");
                    Call(form, "ShowResult", "UNKNOWN - SEND_CANCELLED", Field(form, "generation"));
                    Require((bool)Field(form, "closeRequested") && details.Text.Contains("UNKNOWN - SEND_CANCELLED"),
                        "completed Stop result was hidden or normal closing was blocked");
                    Call(form, "ShowResult", "UNKNOWN - " + SupervisedSendTest.MouseReleaseWarning, Field(form, "generation"));
                    Require(!(bool)Field(form, "closeRequested") && details.Text.Contains(SupervisedSendTest.MouseReleaseWarning) &&
                        !((System.Windows.Forms.Button)Field(form, "testButton")).Enabled,
                        "unconfirmed mouse release auto-closed its warning or unlocked a retry");
                    Set(form, "busy", false);
                    closing = new System.Windows.Forms.FormClosingEventArgs(System.Windows.Forms.CloseReason.UserClosing, false);
                    Call(form, "MainFormClosing", form, closing);
                    Require(!closing.Cancel && !((System.Windows.Forms.Button)Field(form, "testButton")).Enabled,
                        "finished test prevented closing or unlocked sending");
                }

                using (var form = new MainForm(audit))
                {
                    ((System.Windows.Forms.CheckBox)Field(form, "confirmSendCheckBox")).Checked = true;
                    Call(form, "BeginProbe");
                    ((System.Windows.Forms.Timer)Field(form, "probeTimer")).Stop();
                    var consent = (SupervisedSendTest.Consent)Field(form, "activeConsent");
                    Require(consent.TryConsume(sessionCode) && consent.TryCommitMove() && consent.TryCommitWrite() && !consent.Attempted,
                        "synthetic write-only consent did not commit");
                    Set(form, "busy", true); // Synthetic write may still be pending; no native provider or input is called.
                    Call(form, "StopProbe", "SELF_TEST_WRITE_ONLY_STOP");
                    var details = (System.Windows.Forms.TextBox)Field(form, "detailsBox");
                    Require(consent.WriteAttempted && !consent.Attempted && !consent.TryCommit() &&
                        (bool)Field(form, "inputMayHaveChanged") && details.Text.Contains("No mouse click was attempted") &&
                        details.Text.Contains("code entry may have completed or may still be pending") &&
                        !details.Text.Contains("No text entry or Send click was attempted"), "Stop lost the write-only warning or allowed a click");
                    var closing = new System.Windows.Forms.FormClosingEventArgs(System.Windows.Forms.CloseReason.UserClosing, false);
                    Call(form, "MainFormClosing", form, closing);
                    Require(closing.Cancel && details.Text.Contains("code entry may have completed"), "close lost a pending write warning");
                    Call(form, "ShowResult", "UNKNOWN - SEND_CANCELLED", Field(form, "generation"));
                    Require(!(bool)Field(form, "closeRequested") && details.Text.Contains("No mouse click was attempted"),
                        "write-only result auto-closed before its warning could be read");
                    Set(form, "busy", false);
                    closing = new System.Windows.Forms.FormClosingEventArgs(System.Windows.Forms.CloseReason.UserClosing, false);
                    Call(form, "MainFormClosing", form, closing);
                    Require(!closing.Cancel && !((System.Windows.Forms.Button)Field(form, "testButton")).Enabled,
                        "completed write-only test prevented manual closing or unlocked retry");
                }

                using (var form = new MainForm(audit))
                {
                    ((System.Windows.Forms.CheckBox)Field(form, "confirmSendCheckBox")).Checked = true;
                    Call(form, "BeginProbe");
                    var closing = new System.Windows.Forms.FormClosingEventArgs(System.Windows.Forms.CloseReason.UserClosing, false);
                    Call(form, "MainFormClosing", form, closing);
                    Require(!closing.Cancel && !((System.Windows.Forms.Timer)Field(form, "probeTimer")).Enabled &&
                        ((SupervisedSendTest.Consent)Field(form, "activeConsent")).Cancelled,
                        "closing during countdown did not cancel consent");
                }

                using (var form = new MainForm(audit))
                {
                    audit.Dispose(); // Audit failure must block even the countdown and cancel fresh consent.
                    ((System.Windows.Forms.CheckBox)Field(form, "confirmSendCheckBox")).Checked = true;
                    Call(form, "BeginProbe");
                    Require(!((System.Windows.Forms.Timer)Field(form, "probeTimer")).Enabled &&
                        ((SupervisedSendTest.Consent)Field(form, "activeConsent")).Cancelled &&
                        (bool)Field(form, "testStarted") && (bool)Field(form, "stopRequested") &&
                        !((System.Windows.Forms.Button)Field(form, "testButton")).Enabled,
                        "failed audit allowed the test to start or restart");
                }
                using (var form = new MainForm(audit))
                {
                    ((System.Windows.Forms.CheckBox)Field(form, "confirmSendCheckBox")).Checked = true;
                    Call(form, "BeginProbe");
                    Require((bool)Field(form, "auditFailed") && !((System.Windows.Forms.Timer)Field(form, "probeTimer")).Enabled &&
                        !((System.Windows.Forms.Button)Field(form, "testButton")).Enabled,
                        "unavailable initial audit allowed starting");
                }
                var auditText = File.ReadAllText(audit.FilePath);
                Require(!auditText.Contains(sessionCode) && !auditText.Contains("PRIVATE_UI_TEXT") &&
                    !auditText.Contains("PRIVATE_PROVIDER_TEXT") && !auditText.Contains("TEST_CODE_COPY_REQUESTED"),
                    "UI logged private code/text or retained clipboard handling");
            }
            finally
            {
                audit.Dispose();
                File.Delete(audit.FilePath);
            }
        }

        private static void TestReadOnlyGuards()
        {
            var marker = "M234567";
            for (var i = 0; i < 32; i++)
            {
                var digits = Protocol.CreateDiagnosticDigits();
                Require(Protocol.IsDiagnosticMarker("MESSAGE", "M" + digits) && Protocol.IsDiagnosticMarker("DRAFT", "D" + digits),
                    "generated diagnostic digits contain ambiguous or extra characters");
            }
            Require(!Protocol.IsDiagnosticMarker("MESSAGE", "D234567") && !Protocol.IsDiagnosticMarker("DRAFT", marker), "stage marker was swapped");
            foreach (var invalid in new[] { "M23456", "M2345678", "M023456", "M123456", "m234567", "M 234567", "M234567\r\n", "M２３４５６７", "RM-DIAG w7-012345abcdef-message", null })
                Require(!Protocol.IsDiagnosticMarker("MESSAGE", invalid), "invalid diagnostic code was accepted");
            Require(!Protocol.IsDiagnosticMarker("OTHER", marker), "unknown diagnostic stage was accepted");
            Require(ReadOnlyProbe.MatchMarker(marker, marker) == "exact", "exact marker was not recognized");
            Require(ReadOnlyProbe.MatchMarker("time " + marker + "\r\n", marker) == "contains", "embedded marker was treated as a body");
            Require(ReadOnlyProbe.MatchMarker(marker + "\r\n", marker) == "contains", "newline changed an exact match silently");
            Require(ReadOnlyProbe.MatchMarker(" M234567 ", marker) == "contains", "spaces were silently normalized to exact");
            Require(ReadOnlyProbe.MatchMarker("M 234567", marker) == "none", "an internal space was silently removed");
            Require(ReadOnlyProbe.MatchMarker(marker.ToLowerInvariant(), marker) == "none", "case-insensitive marker was accepted");
            Require(ReadOnlyProbe.MatchMarker(null, marker) == "none" && ReadOnlyProbe.MatchMarker(marker, "") == "none", "empty marker matched");
            foreach (var action in new Action[]
            {
                () => AutomationTarget.Bind(IntPtr.Zero, null),
                () => ReadOnlyPair.SendDraftOnce(IntPtr.Zero, null, null, null),
                () => ((AutomationTarget)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(AutomationTarget))).SendPong(null, "test", null)
            })
            {
                try { action(); throw new InvalidOperationException("Legacy automation was not blocked."); }
                catch (MonitorException ex) { Require(ex.ReasonCode == "READ_ONLY_DIAGNOSTIC_BUILD", "legacy automation reached a live operation"); }
            }
        }

        private static void TestPointerAndShapeHints()
        {
            var point = new System.Windows.Point(-10, 25);
            var rect = new System.Windows.Rect(-20, 20, 20, 10);
            Require(ReadOnlyProbe.ContainsPoint(rect, point), "negative-screen coordinates were lost");
            Require(ReadOnlyProbe.ContainsPoint(rect, new System.Windows.Point(0, 30)), "rectangle boundary changed");
            foreach (var invalid in new[] { System.Windows.Rect.Empty, new System.Windows.Rect(0, 0, 0, 1),
                new System.Windows.Rect(0, 0, double.PositiveInfinity, 1), new System.Windows.Rect(0, 0, double.NaN, 1) })
                Require(!ReadOnlyProbe.ContainsPoint(invalid, new System.Windows.Point(0, 0.5)), "invalid rectangle was a candidate");
            Require(!ReadOnlyProbe.ContainsPoint(rect, new System.Windows.Point(double.NaN, 25)) &&
                !ReadOnlyProbe.ContainsPoint(rect, new System.Windows.Point(-10, 31)), "invalid or outside point matched");
            foreach (var stage in new[] { "HEADER", "SEND" })
            {
                Require(ReadOnlyProbe.IsValidRequest(stage, null, point), "valid pointer stage rejected");
                Require(!ReadOnlyProbe.IsValidRequest(stage, null, null) && !ReadOnlyProbe.IsValidRequest(stage, "M234567", point) &&
                    !ReadOnlyProbe.IsValidRequest(stage, null, new System.Windows.Point(double.PositiveInfinity, 0)), "invalid pointer request accepted");
            }
            Require(ReadOnlyProbe.IsValidRequest("SEND_METADATA", null, point) &&
                !ReadOnlyProbe.IsValidRequest("SEND_METADATA", "D234567", point) &&
                !ReadOnlyProbe.IsValidRequest("SEND_METADATA", null, null) &&
                !ReadOnlyProbe.IsValidRequest("SEND_METADATA", null, new System.Windows.Point(double.NaN, 0)),
                "metadata stage bypassed no-marker or finite-pointer requirements");
            Require(ReadOnlyProbe.IsValidRequest("MESSAGE", "M234567", null) && !ReadOnlyProbe.IsValidRequest("MESSAGE", "M234567", point) &&
                !ReadOnlyProbe.IsValidRequest("OTHER", null, point), "pointer mode bypassed stage validation");
            Require(ReadOnlyProbe.IsValidRequest("PAIR", "D234567", point) &&
                ReadOnlyProbe.IsValidRequest("PAIR_RECHECK", "D234567", null), "pair stage contract rejected");
            Require(ReadOnlyProbe.IsValidRequest("TARGET_SWITCH", "D234567", null) &&
                !ReadOnlyProbe.IsValidRequest("TARGET_SWITCH", "D234567", point) &&
                !ReadOnlyProbe.IsValidRequest("TARGET_SWITCH", "M234567", null), "switch stage bypassed marker or no-pointer requirements");
            Require(!ReadOnlyProbe.IsValidRequest("PAIR", "D234567", null) && !ReadOnlyProbe.IsValidRequest("PAIR", null, point) &&
                !ReadOnlyProbe.IsValidRequest("PAIR", "M234567", point) && !ReadOnlyProbe.IsValidRequest("PAIR_RECHECK", "D234567", point),
                "pair stage bypassed marker or no-pointer recheck requirements");
            Require(ReadOnlyProbe.NameShape("M234567", "M234567") == "MARKER_EXACT", "marker shape lost");
            Require(ReadOnlyProbe.NameShape("M234567.txt", "M234567") == "FILE_NAME_LIKE" &&
                ReadOnlyProbe.MatchMarker("M234567.txt", "M234567") == "contains", "filename erased marker or shape evidence");
            Require(ReadOnlyProbe.NameShape("오후 1:02", null) == "TIME_LIKE" && ReadOnlyProbe.NameShape("25:88", null) == "OTHER", "time hint is unbounded");
            Require(ReadOnlyProbe.NameShape("2026년 9월 7일", null) == "DATE_LIKE" &&
                ReadOnlyProbe.NameShape("report.PDF", null) == "FILE_NAME_LIKE", "metadata hints lost");
            Require(ReadOnlyProbe.NameShape("Send", null) == "OTHER" && ReadOnlyProbe.NameShape(" M234567 ", "M234567") == "MARKER_EMBEDDED" &&
                ReadOnlyProbe.NameShape(null, null) == "EMPTY", "shape hint normalized or exposed unknown content");
        }
    }
}
