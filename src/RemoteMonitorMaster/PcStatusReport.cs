using System;
using System.Globalization;
using System.Threading;
using System.Text.RegularExpressions;
using System.Linq;
using System.Text;
using RemoteMonitorLink;

namespace RemoteMonitorMaster
{
    // Canonical, bounded status only. Process names are normalized lowercase to prevent M-code echo.
    internal static class PcStatusReport
    {
        internal const int MaxPhoneProcesses = 8;
        internal const int MaxPhoneLength = 1400;
        internal static string Capture(string nonce)
        {
            if (!Protocol.IsDiagnosticMarker("DRAFT", nonce)) throw new MonitorException("PC_STATUS_NONCE_INVALID", "Invalid status request.");
            var state = MachineStatus.Capture();
            return Format(nonce, state.LocalTime, state.UptimeMinutes, state.AvailableMiB, state.TotalMiB);
        }

        internal static string CaptureSlave(string nonce, SlaveEndpoint endpoint, CancellationToken cancellation)
        {
            if (!Protocol.IsDiagnosticMarker("DRAFT", nonce)) throw new MonitorException("PC_STATUS_NONCE_INVALID", "Invalid status request.");
            var state = StatusClient.QueryAsync(endpoint, cancellation).GetAwaiter().GetResult();
            cancellation.ThrowIfCancellationRequested();
            return FormatSlave(nonce, state);
        }

        internal static string FormatSlave(string nonce, MachineStatus state)
        { return FormatSlave(nonce, state, MaxPhoneProcesses); }

        internal static string FormatSlave(string nonce, MachineStatus state, int maxProcesses)
        {
            if (maxProcesses < 1 || maxProcesses > MaxPhoneProcesses) throw new ArgumentOutOfRangeException("maxProcesses");
            state.Validate();
            if (state.Processes == null) throw new MonitorException("SLAVE_PROCESSES_MISSING", "Process snapshot missing.");
            var text = FormatReply("SLAVE STATUS", nonce, state.LocalTime, state.UptimeMinutes, state.AvailableMiB, state.TotalMiB) +
                ProcessSummary(state.Processes, maxProcesses);
            if (!IsSlaveReply(text, nonce)) throw new MonitorException("SLAVE_PROCESS_REPLY_INVALID", "Invalid process summary.");
            return text;
        }

        private static string ProcessSummary(ProcessInventory inventory, int maxProcesses)
        {
            inventory.Validate();
            var selected = inventory.Items.Take(maxProcesses).ToArray(); // Validate enforces the shared ranking.
            var total = inventory.Items.Length + inventory.Omitted;
            var text = new StringBuilder(string.Format(CultureInfo.InvariantCulture,
                " | PROCS {0}/{1} OMIT {2} UNKNOWN {3}", selected.Length, total, total - selected.Length, inventory.Unreadable));
            foreach (var process in selected)
                text.AppendFormat(CultureInfo.InvariantCulture, " | {0}#{1} CPU={2}% RAM={3}MiB AGE={4}m",
                    process.Name, process.Pid,
                    process.CpuPermille.HasValue ? (process.CpuPermille.Value / 10m).ToString("0.0", CultureInfo.InvariantCulture) : "?",
                    Number(process.WorkingSetMiB), Number(process.AgeSeconds.HasValue ? process.AgeSeconds.Value / 60 : (long?)null));
            return text.Append(" | PROGRESS n/a").ToString();
        }

        private static string Number(long? value)
        { return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "?"; }

        internal static string Format(string nonce, DateTime sampledAt, ulong uptimeMinutes, ulong availableMiB, ulong totalMiB)
        { return FormatReply("PC STATUS", nonce, sampledAt, uptimeMinutes, availableMiB, totalMiB); }

        private static string FormatReply(string prefix, string nonce, DateTime sampledAt, ulong uptimeMinutes, ulong availableMiB, ulong totalMiB)
        {
            var text = string.Format(CultureInfo.InvariantCulture,
                "{6} {0} | {1:yyyy-MM-dd HH:mm:ss} | UP {2} min | RAM {3}/{4} MiB | RM {5}",
                nonce, sampledAt, uptimeMinutes, availableMiB, totalMiB, AppInfo.Version, prefix);
            if (!ValidateReply(text, nonce, prefix)) throw new MonitorException("PC_STATUS_DATA_INVALID", "Invalid PC status values.");
            return text;
        }

        internal static bool IsReply(string text, string nonce, string nextMarker = null)
        { return ValidateReply(text, nonce, "PC STATUS", nextMarker); }

        internal static bool IsSlaveReply(string text, string nonce, string nextMarker = null)
        { return ValidateReply(text, nonce, "SLAVE STATUS", nextMarker); }

        internal static string WithNext(string text, string nonce, bool slave, string nextMarker)
        {
            if (!Protocol.IsDiagnosticMarker("MESSAGE", nextMarker))
                throw new MonitorException("STATUS_NEXT_MARKER_INVALID", "Invalid next status request.");
            var prefix = slave ? "SLAVE STATUS" : "PC STATUS";
            // Never print the next full M marker: a split outgoing Text child must not trigger another reply.
            var combined = text + " | NEXT " + nextMarker.Substring(1);
            if (!ValidateReply(text, nonce, prefix) || !ValidateReply(combined, nonce, prefix, nextMarker))
                throw new MonitorException("STATUS_NEXT_MARKER_INVALID", "Invalid next status request.");
            return combined;
        }

        private static bool ValidateReply(string text, string nonce, string prefix, string nextMarker = null)
        {
            if (!Protocol.IsDiagnosticMarker("DRAFT", nonce) || text == null || text.Length > MaxPhoneLength) return false;
            if (nextMarker != null && (!Protocol.IsDiagnosticMarker("MESSAGE", nextMarker) ||
                nextMarker.Substring(1) == nonce.Substring(1))) return false;
            if (nextMarker != null)
            {
                var suffix = " | NEXT " + nextMarker.Substring(1);
                if (!text.EndsWith(suffix, StringComparison.Ordinal)) return false;
                text = text.Substring(0, text.Length - suffix.Length);
            }
            var fields = text.Split(new[] { " | " }, StringSplitOptions.None);
            if (fields.Length < 5) return false;
            if (fields.Length > 5 && (prefix != "SLAVE STATUS" || !ValidateProcesses(fields))) return false;
            text = string.Join(" | ", fields.Take(5));
            var match = Regex.Match(text, @"\A" + prefix + " " + nonce +
                @" \| (?<time>[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2})" +
                @" \| UP (?<up>0|[1-9][0-9]{0,14}) min \| RAM (?<free>0|[1-9][0-9]{0,13})/(?<total>[1-9][0-9]{0,13}) MiB \| RM " +
                Regex.Escape(AppInfo.Version) + @"\z", RegexOptions.CultureInvariant);
            DateTime sampledAt;
            ulong free, total;
            return match.Success && DateTime.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out sampledAt) &&
                ulong.TryParse(match.Groups["free"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out free) &&
                ulong.TryParse(match.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out total) && free <= total;
        }

        private static bool ValidateProcesses(string[] fields)
        {
            if (fields.Length < 7 || fields.Length > 7 + MaxPhoneProcesses || fields[fields.Length - 1] != "PROGRESS n/a") return false;
            var header = Regex.Match(fields[5], @"\APROCS (?<shown>[0-8])/(?<total>0|[1-9][0-9]{0,5}) OMIT (?<omit>0|[1-9][0-9]{0,5}) UNKNOWN (?<unknown>0|[1-9][0-9]{0,5})\z");
            if (!header.Success) return false;
            var shown = int.Parse(header.Groups["shown"].Value, CultureInfo.InvariantCulture);
            var total = int.Parse(header.Groups["total"].Value, CultureInfo.InvariantCulture);
            var omitted = int.Parse(header.Groups["omit"].Value, CultureInfo.InvariantCulture);
            if (shown != fields.Length - 7 || total < shown || omitted != total - shown) return false;
            var pids = new System.Collections.Generic.HashSet<int>();
            for (var i = 0; i < shown; i++)
            {
                var row = Regex.Match(fields[6 + i], @"\A(?<name>[^#|]{1,40})#(?<pid>[1-9][0-9]{0,9}) CPU=(?<cpu>\?|(?:0|[1-9][0-9]?|100)\.[0-9])% RAM=(?<ram>\?|0|[1-9][0-9]{0,13})MiB AGE=(?<age>\?|0|[1-9][0-9]{0,13})m\z");
                int pid;
                decimal cpu;
                if (!row.Success || row.Groups["name"].Value != ProcessInventory.NormalizeName(row.Groups["name"].Value) ||
                    !int.TryParse(row.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out pid) || !pids.Add(pid) ||
                    (row.Groups["cpu"].Value != "?" && (!decimal.TryParse(row.Groups["cpu"].Value, NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out cpu) || cpu > 100))) return false;
            }
            return true;
        }

        internal static void RunSelfTest()
        {
            const string nonce = "D234567";
            var text = Format(nonce, new DateTime(2026, 9, 10, 12, 34, 56), 72001, 4096, 8192);
            if (!IsReply(text, nonce) || !text.Contains("UP 72001 min | RAM 4096/8192 MiB"))
                throw new InvalidOperationException("PC status format failed.");
            var remote = FormatReply("SLAVE STATUS", nonce, new DateTime(2026, 9, 10, 12, 34, 56), 72001, 4096, 8192);
            if (!IsSlaveReply(remote, nonce) || IsReply(remote, nonce) || IsSlaveReply(text, nonce) || IsSlaveReply(remote, "D345678"))
                throw new InvalidOperationException("Master/Slave reply origin or nonce mixed.");
            foreach (var invalid in new[] { null, text + "\n", " " + text, text.Replace("4096/8192", "8193/8192"),
                text.Replace("2026-09-10", "2026-99-10"), text.Replace(nonce, "D345678"), text.Replace("RAM", "RUN"),
                text.Replace("72001", "-1"), text.Replace("4096/8192", "0/0"), new string('X', 181) })
                if (IsReply(invalid, nonce)) throw new InvalidOperationException("Unbounded PC status payload accepted.");
            if (!IsReply(Capture(nonce), nonce)) throw new InvalidOperationException("Local PC status read failed.");
            const string next = "M345678";
            var continued = WithNext(remote, nonce, true, next);
            if (!IsSlaveReply(continued, nonce, next) || continued.Contains(next) || IsSlaveReply(continued, nonce) ||
                IsSlaveReply(continued, nonce, "M456789") || IsSlaveReply(continued + " ", nonce, next) ||
                IsSlaveReply(continued + " | NEXT M456789", nonce, next) || IsReply(continued, nonce, next))
                throw new InvalidOperationException("Next request not bound to exact status payload.");
            foreach (var invalid in new[] { null, "M234567", "M123456", "M345678 ", "D345678" })
            {
                try { WithNext(remote, nonce, true, invalid); throw new InvalidOperationException("Invalid next request accepted."); }
                catch (MonitorException) { }
            }
            var consent = new SupervisedSendTest.Consent(nonce, true, true, null, next);
            if (!consent.TryClaimRoundTrip()) throw new InvalidOperationException("Operating consent was unavailable.");
            var prepared = consent.PrepareReply();
            if (!consent.IsAuthorizedReply(prepared) || !IsReply(prepared, nonce, next) ||
                prepared.Contains(next) || consent.IsAuthorizedReply(prepared.Replace("NEXT 345678", "NEXT 456789")) ||
                !consent.TryConsume(prepared) || consent.TryConsume(prepared))
                throw new InvalidOperationException("Operating consent exact/one-use guard failed.");
            consent.Cancel();
            if (consent.TryCommitMove()) throw new InvalidOperationException("Cancelled operating reply moved cursor.");
            TestProcessReply();
        }

        private static void TestProcessReply()
        {
            var snapshot = new MachineStatus
            {
                LocalTime = new DateTime(2026, 9, 10, 12, 34, 56), UptimeMinutes = 60,
                AvailableMiB = 4096, TotalMiB = 8192, Version = LinkVersion.Value,
                Processes = new ProcessInventory { Items = Enumerable.Range(1, 10).Select(i => new ProcessState
                {
                    Pid = i, Name = ProcessInventory.NormalizeName(i == 1 ? "M345678" : "계산_solver" + i),
                    HasWindow = i == 1, CpuPermille = i >= 2 && i <= 4 ? 120 : (int?)null, WorkingSetMiB = 1024, AgeSeconds = null
                }).OrderByDescending(p => p.CpuPermille ?? 0).ThenByDescending(p => p.HasWindow).ThenBy(p => p.Pid).ToArray() }
            };
            var reply = FormatSlave("D234567", snapshot);
            var continued = WithNext(reply, "D234567", true, "M345678");
            if (!IsSlaveReply(continued, "D234567", "M345678") || continued.Contains("M345678") ||
                !reply.Contains("PROCS 8/10 OMIT 2") || !reply.Contains("CPU=?%") || !reply.Contains("AGE=?m") ||
                IsReply(reply, "D234567") || continued.Length > MaxPhoneLength)
                throw new InvalidOperationException("Process reply bounds, unknown metrics or M-code echo failed.");
            foreach (var invalid in new[] { reply.Replace("m345678", "M345678"), reply.Replace("OMIT 2", "OMIT 0"),
                reply.Replace("CPU=12.0%", "CPU=100.1%"), reply.Replace("PROGRESS n/a", "PROGRESS complete"),
                reply + " | extra", reply.Replace("#2 ", "#1 "), reply + "\n" })
                if (IsSlaveReply(invalid, "D234567")) throw new InvalidOperationException("Invalid process reply accepted.");
            snapshot.Processes.Items = new ProcessState[0];
            if (!FormatSlave("D234567", snapshot).Contains("PROCS 0/0 OMIT 0"))
                throw new InvalidOperationException("Empty process snapshot was not explicit.");
            snapshot.Processes.Items = Enumerable.Range(1, ProcessInventory.MaxItems).Select(i => new ProcessState
            {
                Pid = i, Name = new string('계', ProcessInventory.MaxNameLength), CpuPermille = 1000,
                WorkingSetMiB = long.MaxValue / 1048576, AgeSeconds = 3155760000L
            }).ToArray();
            var maximum = WithNext(FormatSlave("D234567", snapshot), "D234567", true, "M345678");
            if (maximum.Length > MaxPhoneLength || !IsSlaveReply(maximum, "D234567", "M345678") ||
                ReadOnlyProbe.MatchMarker(maximum, "M345678") != "none")
                throw new InvalidOperationException("Maximum process summary exceeded output or marker bounds.");
            LinkProtocol.ParseStatus(LinkProtocol.FormatStatus(snapshot));
        }
    }
}
