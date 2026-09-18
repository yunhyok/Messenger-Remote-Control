using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteMonitorLink
{
    internal sealed class LinkVersionMismatchException : IOException
    {
        internal LinkVersionMismatchException(string actualVersion)
            : base("Protocol version mismatch: expected " + LinkVersion.Value + ", received " + actualVersion + ".")
        {
            ActualVersion = actualVersion;
        }

        internal string ActualVersion { get; private set; }
    }

    internal static class LinkVersion
    {
        internal const string AppValue = "0.3.2";
        internal const string Value = "0.3.0";
    }

    // Read-only metrics and bounded process names; never command lines, window titles or paths.
    internal sealed class MachineStatus
    {
        internal DateTime LocalTime;
        internal ulong UptimeMinutes;
        internal ulong AvailableMiB;
        internal ulong TotalMiB;
        internal string Version;
        internal ProcessInventory Processes;
        internal bool PowerSiOnly;
        internal PowerSiObservation PowerSi;
        internal PowerSiReport PowerSiReport;

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            internal uint Length, Load;
            internal ulong TotalPhys, AvailablePhys, TotalPageFile, AvailablePageFile,
                TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        internal static MachineStatus Capture()
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (!GlobalMemoryStatusEx(ref memory))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read physical memory status.");

            var status = new MachineStatus
            {
                LocalTime = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified),
                UptimeMinutes = GetTickCount64() / 60000,
                AvailableMiB = memory.AvailablePhys / LinkProtocol.BytesPerMiB,
                TotalMiB = memory.TotalPhys / LinkProtocol.BytesPerMiB,
                Version = LinkVersion.Value
            };
            status.Validate();
            return status;
        }

        internal void Validate()
        {
            if (LocalTime.Kind == DateTimeKind.Utc || LocalTime.Year < 2000 ||
                UptimeMinutes > ulong.MaxValue / 60000 ||
                TotalMiB == 0 || TotalMiB > LinkProtocol.MaxPhysicalMiB || AvailableMiB > TotalMiB ||
                !string.Equals(Version, LinkVersion.Value, StringComparison.Ordinal))
                throw new InvalidDataException("Invalid machine status.");
            Processes?.Validate();
            PowerSiReport?.Validate();
        }
    }

    internal sealed class SlaveEndpoint
    {
        private readonly IPAddress address;
        private readonly int port;
        private readonly string pin;
        private readonly string token;

        internal SlaveEndpoint(IPAddress address, int port, string pin, string token)
        {
            this.address = LinkProtocol.NormalizeAddress(address);
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port", "Invalid endpoint port.");

            byte[] pinBytes;
            if (!LinkProtocol.TryDecodePin(pin, out pinBytes)) throw new ArgumentException("Invalid certificate pin.", "pin");
            Array.Clear(pinBytes, 0, pinBytes.Length);

            byte[] tokenBytes;
            if (!LinkProtocol.TryDecodeToken(token, out tokenBytes)) throw new ArgumentException("Invalid shared token.", "token");
            Array.Clear(tokenBytes, 0, tokenBytes.Length);

            this.port = port;
            this.pin = pin.ToLowerInvariant();
            this.token = token;
        }

        internal IPAddress Address { get { return new IPAddress(address.GetAddressBytes()); } }
        internal int Port { get { return port; } }
        internal string Pin { get { return pin; } }
        internal string Token { get { return token; } }

        internal static SlaveEndpoint Parse(string text)
        {
            if (text == null || text.Length < 1 || text.Length > LinkProtocol.MaxPairingLength ||
                text.IndexOfAny(new[] { '\r', '\n', '\t', ' ' }) >= 0)
                throw new FormatException("Invalid pairing code.");

            string[] parts = text.Split('|');
            IPAddress parsedAddress;
            int parsedPort;
            byte[] ignored;
            if (parts.Length != 5 || parts[0] != LinkProtocol.PairingVersion ||
                !LinkProtocol.TryParseCanonicalIpv4(parts[1], out parsedAddress) ||
                !LinkProtocol.TryParsePort(parts[2], out parsedPort) ||
                !LinkProtocol.TryDecodePin(parts[3], out ignored))
                throw new FormatException("Invalid pairing code.");
            Array.Clear(ignored, 0, ignored.Length);
            if (!LinkProtocol.TryDecodeToken(parts[4], out ignored))
                throw new FormatException("Invalid pairing code.");
            Array.Clear(ignored, 0, ignored.Length);

            try
            {
                return new SlaveEndpoint(parsedAddress, parsedPort, parts[3], parts[4]);
            }
            catch (ArgumentException)
            {
                throw new FormatException("Invalid pairing code.");
            }
        }
    }

    internal static class LinkProtocol
    {
        internal const string PairingVersion = "RMS1";
        internal const ulong BytesPerMiB = 1048576;
        internal const ulong MaxPhysicalMiB = ulong.MaxValue / BytesPerMiB;
        internal const int MaxPairingLength = 160;
        internal const int MaxRequestLength = 64;
        internal const int MaxResponseLength = ProcessInventory.MaxWireLength + 256;
        private const string StatusPrefix = "RMS1|STATUS|";
        private const string PowerSiPrefix = "RMS1|PWRSI|";

        internal static string CreatePairing(IPAddress address, int port, string pin, string token)
        {
            var endpoint = new SlaveEndpoint(address, port, pin, token);
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}", PairingVersion,
                endpoint.Address, endpoint.Port, endpoint.Pin, endpoint.Token);
        }

        internal static string FormatStatus(MachineStatus status, bool powerSiOnly = false)
        {
            if (status == null) throw new ArgumentNullException("status");
            status.Validate();
            if (status.Processes == null) throw new InvalidDataException("Process snapshot is missing.");
            if (powerSiOnly && !IsPowerSiInventory(status.Processes))
                throw new InvalidDataException("Invalid PowerSI process snapshot.");
            if (powerSiOnly && (status.PowerSi == null) == (status.PowerSiReport == null))
                throw new InvalidDataException("PowerSI result is missing or ambiguous.");
            if (powerSiOnly && status.PowerSiReport != null)
                ValidatePowerSiReportInventory(status.Processes, status.PowerSiReport);
            string line = string.Format(CultureInfo.InvariantCulture,
                "{0}{1:yyyy-MM-ddTHH:mm:ss}|{2}|{3}|{4}|{5}|{6}", powerSiOnly ? PowerSiPrefix : StatusPrefix, status.LocalTime,
                status.UptimeMinutes, status.AvailableMiB, status.TotalMiB, status.Version, status.Processes.Serialize());
            if (powerSiOnly) line += "|" + (status.PowerSiReport != null ?
                PowerSiReport.WireVersion : status.PowerSi.Serialize());
            if (line.Length > MaxResponseLength) throw new InvalidDataException("Invalid machine status.");
            return line;
        }

        internal static MachineStatus ParseStatus(string line, bool powerSiOnly = false)
        {
            if (line == null || line.Length < 1 || line.Length > MaxResponseLength || !IsPrintableAscii(line))
                throw new InvalidDataException("Invalid status response.");
            string[] parts = line.Split('|');
            DateTime sampledAt;
            ulong uptime, available, total;
            if (parts.Length != (powerSiOnly ? 9 : 8) || parts[0] != PairingVersion ||
                parts[1] != (powerSiOnly ? "PWRSI" : "STATUS") ||
                parts[2].Length != 19 ||
                !DateTime.TryParseExact(parts[2], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out sampledAt) ||
                !TryParseUnsigned(parts[3], 15, ulong.MaxValue / 60000, out uptime) ||
                !TryParseUnsigned(parts[4], 14, MaxPhysicalMiB, out available) ||
                !TryParseUnsigned(parts[5], 14, MaxPhysicalMiB, out total) ||
                parts[6].Length < 1 || parts[6].Length > 32)
                throw new InvalidDataException("Invalid status response.");
            if (!string.Equals(parts[6], LinkVersion.Value, StringComparison.Ordinal))
                throw new LinkVersionMismatchException(parts[6]);

            PowerSiReport report = null;
            PowerSiObservation observation = null;
            if (powerSiOnly)
            {
                if (parts[8] != PowerSiReport.WireVersion) observation = PowerSiObservation.Parse(parts[8]);
            }

            var status = new MachineStatus
            {
                LocalTime = DateTime.SpecifyKind(sampledAt, DateTimeKind.Unspecified),
                UptimeMinutes = uptime,
                AvailableMiB = available,
                TotalMiB = total,
                Version = parts[6],
                Processes = ProcessInventory.Parse(parts[7]),
                PowerSiOnly = powerSiOnly,
                PowerSi = observation,
                PowerSiReport = report
            };
            status.Validate();
            if (powerSiOnly && !IsPowerSiInventory(status.Processes))
                throw new InvalidDataException("Invalid PowerSI status response.");
            if (powerSiOnly && status.PowerSiReport != null)
                ValidatePowerSiReportInventory(status.Processes, status.PowerSiReport);
            return status;
        }

        internal static string CreateRequest(string token, bool powerSiOnly = false)
        {
            byte[] ignored;
            if (!TryDecodeToken(token, out ignored)) throw new ArgumentException("Invalid shared token.", "token");
            Array.Clear(ignored, 0, ignored.Length);
            return (powerSiOnly ? PowerSiPrefix : StatusPrefix) + token;
        }

        internal static bool TryParseRequest(string line, out byte[] token)
        {
            bool powerSiOnly;
            if (!TryParseRequest(line, out token, out powerSiOnly)) return false;
            if (!powerSiOnly) return true;
            Array.Clear(token, 0, token.Length);
            token = null;
            return false;
        }

        internal static bool TryParseRequest(string line, out byte[] token, out bool powerSiOnly)
        {
            token = null;
            powerSiOnly = false;
            string prefix;
            if (line != null && line.Length == StatusPrefix.Length + 44 &&
                line.StartsWith(StatusPrefix, StringComparison.Ordinal))
                prefix = StatusPrefix;
            else if (line != null && line.Length == PowerSiPrefix.Length + 44 &&
                line.StartsWith(PowerSiPrefix, StringComparison.Ordinal))
            {
                prefix = PowerSiPrefix;
                powerSiOnly = true;
            }
            else return false;
            if (TryDecodeToken(line.Substring(prefix.Length), out token)) return true;
            powerSiOnly = false;
            return false;
        }

        internal static IPAddress NormalizeAddress(IPAddress address)
        {
            if (address == null) throw new ArgumentNullException("address");
            if (address.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("An IPv4 address is required.", "address");
            byte[] bytes = address.GetAddressBytes();
            if ((bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0) ||
                bytes[0] >= 224)
                throw new ArgumentException("A unicast IPv4 address is required.", "address");
            return new IPAddress(bytes);
        }

        internal static bool TryParseCanonicalIpv4(string text, out IPAddress address)
        {
            address = null;
            if (text == null || text.Length < 7 || text.Length > 15) return false;
            string[] parts = text.Split('.');
            var bytes = new byte[4];
            if (parts.Length != 4) return false;
            for (int i = 0; i < parts.Length; i++)
            {
                int value = 0;
                string part = parts[i];
                if (part.Length < 1 || part.Length > 3 || (part.Length > 1 && part[0] == '0')) return false;
                for (int j = 0; j < part.Length; j++)
                {
                    if (part[j] < '0' || part[j] > '9') return false;
                    value = value * 10 + part[j] - '0';
                }
                if (value > 255) return false;
                bytes[i] = (byte)value;
            }
            try
            {
                address = NormalizeAddress(new IPAddress(bytes));
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        internal static bool TryParsePort(string text, out int port)
        {
            port = 0;
            if (text == null || text.Length < 1 || text.Length > 5 || (text.Length > 1 && text[0] == '0')) return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9') return false;
                port = port * 10 + text[i] - '0';
            }
            return port >= 1 && port <= 65535;
        }

        internal static string ComputePin(byte[] certificateDer)
        {
            if (certificateDer == null) throw new ArgumentNullException("certificateDer");
            using (SHA256 sha256 = SHA256.Create())
                return ToLowerHex(sha256.ComputeHash(certificateDer));
        }

        internal static bool TryDecodePin(string value, out byte[] bytes)
        {
            bytes = null;
            if (value == null || value.Length != 64) return false;
            var result = new byte[32];
            for (int i = 0; i < result.Length; i++)
            {
                int high = HexValue(value[i * 2]);
                int low = HexValue(value[i * 2 + 1]);
                if (high < 0 || low < 0)
                {
                    Array.Clear(result, 0, result.Length);
                    return false;
                }
                result[i] = (byte)((high << 4) | low);
            }
            bytes = result;
            return true;
        }

        internal static bool TryDecodeToken(string value, out byte[] bytes)
        {
            bytes = null;
            if (value == null || value.Length != 44 || value[43] != '=') return false;
            for (int i = 0; i < 43; i++)
            {
                char c = value[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '+' || c == '/')) return false;
            }
            try
            {
                byte[] decoded = Convert.FromBase64String(value);
                if (decoded.Length != 32 || Convert.ToBase64String(decoded) != value)
                {
                    Array.Clear(decoded, 0, decoded.Length);
                    return false;
                }
                bytes = decoded;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        internal static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null) return false;
            int difference = left.Length ^ right.Length;
            int count = Math.Min(left.Length, right.Length);
            for (int i = 0; i < count; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        internal static async Task<string> ReadLineAsync(Stream stream, int maxLength, CancellationToken cancellation)
        {
            return await new LineReader(stream).ReadLineAsync(maxLength, cancellation).ConfigureAwait(false);
        }

        internal static bool IsFramedPowerSiStatus(string line)
        {
            return line != null && line.EndsWith("|" + PowerSiReport.WireVersion, StringComparison.Ordinal);
        }

        // Keeps over-read bytes between bounded lines so large PS4 reports do not perform one async read per byte.
        internal sealed class LineReader
        {
            private readonly Stream stream;
            private readonly byte[] buffer = new byte[8192];
            private int offset, count;

            internal LineReader(Stream stream)
            {
                this.stream = stream ?? throw new ArgumentNullException("stream");
            }

            internal async Task<string> ReadLineAsync(int maxLength, CancellationToken cancellation)
            {
                if (maxLength < 1) throw new ArgumentOutOfRangeException("maxLength");
                cancellation.ThrowIfCancellationRequested();
                var line = new byte[maxLength];
                int length = 0;
                while (true)
                {
                    if (offset == count)
                    {
                        count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false);
                        offset = 0;
                        if (count == 0) throw new EndOfStreamException("Truncated protocol line.");
                    }
                    int newline = Array.IndexOf(buffer, (byte)'\n', offset, count - offset);
                    int end = newline < 0 ? count : newline;
                    int available = end - offset;
                    if (length + available > maxLength) throw new InvalidDataException("Protocol line is too long.");
                    for (int i = offset; i < end; i++)
                        if (buffer[i] < 0x20 || buffer[i] > 0x7e) throw new InvalidDataException("Invalid protocol line.");
                    Buffer.BlockCopy(buffer, offset, line, length, available);
                    length += available;
                    offset = newline < 0 ? count : newline + 1;
                    if (newline >= 0) return Encoding.ASCII.GetString(line, 0, length);
                }
            }
        }

        internal static async Task WriteLineAsync(Stream stream, string line, int maxLength, CancellationToken cancellation)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (line == null || line.Length > maxLength || !IsPrintableAscii(line))
                throw new InvalidDataException("Invalid protocol line.");
            byte[] bytes = Encoding.ASCII.GetBytes(line + "\n");
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false);
            await stream.FlushAsync(cancellation).ConfigureAwait(false);
        }

        internal static async Task AwaitWithCancellation(Task operation, CancellationToken cancellation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            cancellation.ThrowIfCancellationRequested();
            var canceled = new TaskCompletionSource<object>();
            using (cancellation.Register(state => ((TaskCompletionSource<object>)state).TrySetResult(null), canceled))
            {
                if (operation != await Task.WhenAny(operation, canceled.Task).ConfigureAwait(false))
                {
                    ObserveFailure(operation);
                    throw new OperationCanceledException(cancellation);
                }
            }
            await operation.ConfigureAwait(false);
        }

        private static void ObserveFailure(Task operation)
        {
            operation.ContinueWith(task => GC.KeepAlive(task.Exception), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static bool TryParseUnsigned(string text, int maxDigits, ulong maximum, out ulong value)
        {
            value = 0;
            return text != null && text.Length >= 1 && text.Length <= maxDigits &&
                (text.Length == 1 || text[0] != '0') &&
                ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= maximum;
        }

        private static bool IsPrintableAscii(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] < 0x20 || text[i] > 0x7e) return false;
            return true;
        }

        private static bool IsPowerSiInventory(ProcessInventory inventory)
        {
            foreach (ProcessState item in inventory.Items)
                if (!ProcessInventory.IsPowerSiName(item.Name)) return false;
            return true;
        }

        internal static void ValidatePowerSiReportInventory(ProcessInventory inventory, PowerSiReport report)
        {
            report.Validate();
            if (report.SessionId != inventory.SessionId || report.Omitted != inventory.Omitted ||
                report.Unreadable != inventory.Unreadable)
                throw new InvalidDataException("PowerSI report does not match its process snapshot.");

            foreach (PowerSiTargetReport target in report.Targets)
            {
                ProcessState process = null;
                foreach (ProcessState candidate in inventory.Items)
                    if (candidate.Pid == target.Pid) { process = candidate; break; }
                if (process == null || process.StartUtcTicks != target.StartUtcTicks ||
                    !string.Equals(process.FullName ?? process.Name, target.ProcessName, StringComparison.Ordinal))
                    throw new InvalidDataException("PowerSI target does not match its process identity.");
            }
            if (report.Code == "OK" && report.Targets.Length != inventory.Items.Length)
                throw new InvalidDataException("PowerSI report omitted a target record.");
        }

        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            return -1;
        }

        private static string ToLowerHex(byte[] bytes)
        {
            const string digits = "0123456789abcdef";
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = digits[bytes[i] >> 4];
                chars[i * 2 + 1] = digits[bytes[i] & 15];
            }
            return new string(chars);
        }
    }
}
