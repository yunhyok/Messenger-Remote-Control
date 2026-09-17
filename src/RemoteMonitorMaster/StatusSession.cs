using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using RemoteMonitorLink;

namespace RemoteMonitorMaster
{
    // One bound self-chat, sequential status requests, fresh one-use approval for each reply.
    internal sealed class StatusSession
    {
        private readonly string firstMarker;
        private readonly SlaveEndpoint slave;
        private readonly bool plainCommands;
        private readonly object sync = new object();
        private SupervisedSendTest.Consent active;
        private SupervisedSendTest.Consent activeNotice;
        private int cancelled, claimed, completedRounds, attempts;

        internal StatusSession(string firstMarker, bool confirmed, SlaveEndpoint slave)
            : this(firstMarker, confirmed, slave, false) { }

        internal StatusSession(string firstMarker, bool confirmed, SlaveEndpoint slave, bool plainCommands)
        {
            Need(confirmed && Protocol.IsDiagnosticMarker("MESSAGE", firstMarker), "STATUS_APPROVAL_REQUIRED");
            this.firstMarker = firstMarker;
            this.slave = slave;
            this.plainCommands = plainCommands;
        }

        internal bool Cancelled { get { return Volatile.Read(ref cancelled) != 0; } }
        internal int CompletedRounds { get { return Volatile.Read(ref completedRounds); } }
        private int Attempts { get { lock (sync) return attempts | Flags(active) | Flags(activeNotice); } }
        internal bool CursorMoveAttempted { get { return (Attempts & 1) != 0; } }
        internal bool WriteAttempted { get { return (Attempts & 2) != 0; } }
        internal bool SendAttempted { get { return (Attempts & 4) != 0; } }
        internal bool PendingWrite { get { lock (sync) return (active != null && active.PendingWrite) ||
            (activeNotice != null && activeNotice.PendingWrite); } }

        internal void Cancel()
        {
            Interlocked.Exchange(ref cancelled, 1);
            Volatile.Read(ref active)?.Cancel();
            Volatile.Read(ref activeNotice)?.Cancel();
        }

        private bool TryClaim() { return !Cancelled && Interlocked.CompareExchange(ref claimed, 1, 0) == 0; }
        private static int Flags(SupervisedSendTest.Consent consent)
        {
            return consent == null ? 0 : (consent.CursorMoveAttempted ? 1 : 0) |
                (consent.WriteAttempted ? 2 : 0) | (consent.Attempted ? 4 : 0);
        }

        private SupervisedSendTest.Consent StartRequest(string marker, string next)
        {
            lock (sync)
            {
                Need(!Cancelled && Volatile.Read(ref claimed) == 1 && active == null, "STATUS_SESSION_CANCELLED_OR_BUSY");
                var consent = new SupervisedSendTest.Consent("D" + marker.Substring(1), true, true, slave,
                    plainCommands ? null : next, plainCommands);
                Volatile.Write(ref active, consent);
                // Cancellation can arrive between the initial check and publication.
                if (Cancelled) { consent.Cancel(); throw new MonitorException("STATUS_SESSION_CANCELLED", "Status session stopped."); }
                return consent;
            }
        }

        private bool FinishRequest(SupervisedSendTest.Consent consent, RoundTripTest.Outcome outcome, ReceiveProbe.Baseline next)
        {
            lock (sync)
            {
                if (Cancelled || !ReferenceEquals(active, consent) || outcome == null || !outcome.CleanCompletion ||
                    Flags(consent) != 7 || next == null || next.PlainCommands != plainCommands)
                {
                    Cancel();
                    return false; // Keep the active attempts/draft visible; never start another request.
                }
                attempts |= Flags(consent);
                Interlocked.Increment(ref completedRounds);
                Volatile.Write(ref active, null);
                return true;
            }
        }

        internal string Run(IntPtr window, AuditLog log, Func<bool> stop, Action<int, string, string> progress)
        {
            var clock = Stopwatch.StartNew();
            string last = null;
            try
            {
                Need(TryClaim(), "STATUS_SESSION_ALREADY_USED_OR_CANCELLED");
                Need(log != null && stop != null && progress != null, "STATUS_REQUEST_INVALID");
                Need(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "PROBE_REQUIRES_MTA");
                var process = ProcessIdentity.Capture(window);
                NativeMethods.WindowRectangle bounds;
                Need(NativeMethods.GetWindowRect(window, out bounds), "STATUS_WINDOW_UNAVAILABLE");
                bool Stopped()
                {
                    if (Cancelled || stop()) { Cancel(); return true; }
                    uint pid;
                    NativeMethods.WindowRectangle current;
                    Need(window != IntPtr.Zero && NativeMethods.IsWindow(window) &&
                        NativeMethods.GetForegroundWindow() == window, "STATUS_TARGET_CHANGED");
                    Need(NativeMethods.GetWindowThreadProcessId(window, out pid) != 0 && pid == process.ProcessId,
                        "STATUS_PROCESS_CHANGED");
                    Need(NativeMethods.GetWindowRect(window, out current) && current.Equals(bounds), "STATUS_WINDOW_MOVED");
                    return false;
                }
                void Alive() { Need(!Stopped(), "STATUS_SESSION_CANCELLED"); }
                Alive();
                var statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RemoteMonitorMaster", "state", "roundtrip-seen-tokens.txt");
                var store = new TokenStore(statePath);
                var allocated = new HashSet<string>(StringComparer.Ordinal);
                var marker = Allocate(store, allocated, firstMarker);
                ReceiveProbe.Baseline original = null, baseline = null;
                void SendReady(string requestMarker)
                {
                    Alive();
                    var notice = SupervisedSendTest.Consent.ForNotice("D" + requestMarker.Substring(1), SupervisedSendTest.ReadyNotice);
                    Volatile.Write(ref activeNotice, notice);
                    Alive();
                    progress(CompletedRounds, "NOTICE_READY", requestMarker);
                    var result = SupervisedSendTest.RunBoundObserved(window, log, notice.NoticeText,
                        new System.Windows.Point(), notice, Stopped, snapshot =>
                        {
                            Alive();
                            Need(process.Equals(snapshot.Process), "STATUS_PROCESS_CHANGED");
                            if (original == null)
                            {
                                // Baseline before Ready preserves a command arriving immediately after the notice.
                                original = baseline = ReceiveProbe.CreateBaseline(snapshot, requestMarker, true);
                            }
                            else
                            {
                                ReceiveProbe.ValidateContinuity(original, snapshot);
                                ReceiveProbe.ValidatePlainHistoryPrefix(baseline.Snapshot, snapshot, log);
                            }
                        });
                    last = result.Message;
                    Need(result.CleanCompletion, "STATUS_READY_NOTICE_UNCERTAIN");
                    lock (sync) { attempts |= Flags(notice); Volatile.Write(ref activeNotice, null); }
                    notice.Cancel();
                    log.Write("INFO", "MASTER_NOTICE_SENT", AuditLog.Field("stage", "READY"), AuditLog.Field("delivery_verified", false));
                }
                log.Write("INFO", "STATUS_SESSION_BEGIN", AuditLog.Field("slave_status", slave != null),
                    AuditLog.Field("plain_commands", plainCommands),
                    AuditLog.Field("idle_timeout", "NONE"), AuditLog.Field("per_request_replies", plainCommands ? "BOUNDED_PREPARED_PARTS" : "ONE"),
                    AuditLog.Field("next_format", plainCommands ? "NONE" : "DIGITS_ONLY"), AuditLog.Field("compact_receive_log", true));
                if (plainCommands) SendReady(marker);
                while (true)
                {
                    Alive();
                    var round = CompletedRounds;
                    var nextMarker = Allocate(store, allocated, "M" + Protocol.CreateDiagnosticDigits());
                    var consent = StartRequest(marker, nextMarker);
                    ReceiveProbe.Baseline nextBaseline = null;
                    log.Write("INFO", "STATUS_REQUEST_BEGIN", AuditLog.Field("round_index", round));
                    var outcome = RoundTripTest.ObserveWithStatePath(window, log, marker, consent, Stopped,
                        phase => { Alive(); progress(round, phase, marker); Alive(); }, statePath, (phase, snapshot) =>
                        {
                            Alive();
                            Need(process.Equals(snapshot.Process), "STATUS_PROCESS_CHANGED");
                            if (original == null)
                            {
                                Need(phase == "BASELINE", "STATUS_BASELINE_REQUIRED");
                                original = ReceiveProbe.CreateBaseline(snapshot, marker, plainCommands);
                                RequireReplyAbsent(snapshot, marker);
                            }
                            else ReceiveProbe.ValidateContinuity(original, snapshot);
                            if (phase == "HANDOFF")
                            {
                                // Capture BEFORE sending. A fast following request is compared to this earlier snapshot.
                                nextBaseline = ReceiveProbe.CreateBaseline(snapshot, nextMarker, plainCommands);
                                RequireReplyAbsent(snapshot, nextMarker);
                            }
                            Alive();
                        }, baseline, true);
                    last = outcome.Message;
                    if (!FinishRequest(consent, outcome, nextBaseline))
                        throw new MonitorException("STATUS_REQUEST_STOPPED", "No next request will run.");
                    log.Write("INFO", "STATUS_REQUEST_COMPLETE", AuditLog.Field("completed_rounds", CompletedRounds),
                        AuditLog.Field("delivery_verified", false));
                    Alive();
                    progress(round, "ROUND_COMPLETE", marker);
                    Alive();
                    baseline = nextBaseline;
                    marker = nextMarker;
                    if (plainCommands) SendReady(marker);
                }
            }
            catch (Exception ex)
            {
                Cancel();
                var reason = (ex as MonitorException)?.ReasonCode ?? "STATUS_SESSION_FAILED";
                try { log?.Write("INFO", "STATUS_SESSION_END", AuditLog.Field("reason", reason),
                    AuditLog.Field("completed_rounds", CompletedRounds), AuditLog.Field("pending_write", PendingWrite),
                    AuditLog.Field("send_attempted", SendAttempted), AuditLog.Field("elapsed_ms", clock.ElapsedMilliseconds)); }
                catch { }
                return "STATUS_SESSION_STOPPED — " + reason + "; completed=" + CompletedRounds +
                    ". No automatic retry or resume." + (last == null ? "" : Environment.NewLine + last);
            }
            finally { Cancel(); }
        }

        private static void RequireReplyAbsent(ProbeSnapshot snapshot, string marker)
        {
            var hash = TokenStore.Hash("D" + marker.Substring(1));
            Need(!snapshot.Nodes.Any(n => n.Identity.NameHash == hash),
                "STATUS_REPLY_ALREADY_PRESENT");
        }

        private static string Allocate(TokenStore store, HashSet<string> allocated, string preferred)
        {
            var candidate = preferred;
            for (var count = 0; count < 262144; count++) // Six base-8 digits: finite even if the pool is exhausted.
            {
                if (!allocated.Contains(candidate) && !store.Contains("roundtrip-diagnostic-v1:" + candidate))
                { allocated.Add(candidate); return candidate; }
                var digits = candidate.ToCharArray();
                for (var i = 6; i >= 1; i--) { if (digits[i] < '9') { digits[i]++; break; } digits[i] = '2'; }
                candidate = new string(digits);
            }
            throw new MonitorException("STATUS_CODE_POOL_EXHAUSTED", "No unused status code remains.");
        }

        internal static void RunSelfTest(string directory)
        {
            var path = Path.Combine(directory, "status-session-tokens.txt");
            void Reject(Action action)
            {
                try { action(); } catch (MonitorException) { return; }
                throw new InvalidOperationException("Unsafe operating transition accepted.");
            }
            ProbeSnapshot Snapshot(string first = "", string second = "")
            {
                var snapshot = ReceiveProbe.CreateTestSnapshot();
                ReceiveProbe.SetTestName(snapshot.Nodes.Single(n => n.Node == 52), first);
                ReceiveProbe.SetTestName(snapshot.Nodes.Single(n => n.Node == 54), second);
                return snapshot;
            }
            var operation = new StatusSession("M234567", true, null);
            var noticeSession = new StatusSession("M234567", true, null);
            noticeSession.activeNotice = SupervisedSendTest.Consent.ForNotice("D234567", SupervisedSendTest.ReadyNotice);
            Need(noticeSession.activeNotice.TryConsume(SupervisedSendTest.ReadyNotice) && noticeSession.activeNotice.TryCommitMove() &&
                noticeSession.activeNotice.TryCommitWrite() && noticeSession.PendingWrite, "STATUS_SELFTEST_READY_PENDING");
            noticeSession.Cancel();
            Need(noticeSession.activeNotice.Cancelled && !noticeSession.activeNotice.TryCommit() && noticeSession.PendingWrite,
                "STATUS_SELFTEST_READY_CANCEL");
            try
            {
                var store = new TokenStore(path);
                Need(store.TryReserve("roundtrip-diagnostic-v1:M222222"), "STATUS_SELFTEST_RESERVATION");
                var used = new HashSet<string>();
                Need(Allocate(store, used, "M222222") == "M222223" && Allocate(store, used, "M222223") == "M222224",
                    "STATUS_SELFTEST_FRESH_ALLOCATION");
                Need(operation.TryClaim() && !operation.TryClaim(), "STATUS_SELFTEST_ONCE");
                var baseline = ReceiveProbe.CreateBaseline(Snapshot(), "M234567");
                foreach (var codes in new[] { new[] { "M234567", "M345678" }, new[] { "M345678", "M456789" } })
                {
                    var current = codes[0]; var next = codes[1];
                    var consent = operation.StartRequest(current, next);
                    var before = Snapshot(current); var after = Snapshot(current);
                    var owner = new object(); var window = new IntPtr(before.Process.WindowHandle);
                    var proof = new ReceiveProbe.ObservationProof(owner, window, new NativeMethods.WindowRectangle(), baseline,
                        before, ReceiveProbe.Evaluate(baseline, before), after, ReceiveProbe.Evaluate(baseline, after));
                    var handoff = Snapshot(current);
                    var future = ReceiveProbe.CreateBaseline(handoff, next);
                    RoundTripTest.AuthorizeHandoff(proof, owner, window, current, handoff, path, () => false);
                    Need(!new TokenStore(path).TryReserve("roundtrip-diagnostic-v1:" + current), "STATUS_SELFTEST_DURABLE_ONCE");
                    Need(consent.TryClaimRoundTrip(), "STATUS_SELFTEST_APPROVAL");
                    var reply = consent.PrepareReply();
                    Need(!reply.Contains(next) && consent.TryConsume(reply) && consent.TryCommitMove() &&
                        consent.TryCommitWrite() && consent.TryCommit(), "STATUS_SELFTEST_SEND_ONCE");
                    consent.Cancel(); // The real RoundTripTest always retires the consent on return.
                    Need(operation.FinishRequest(consent, new RoundTripTest.Outcome("test",
                        new SupervisedSendTest.Outcome("ACTION_RETURNED", "NONE", "test", true)), future), "STATUS_SELFTEST_ADVANCE");
                    Need(ReceiveProbe.Evaluate(future, Snapshot(current, current)) == null &&
                        ReceiveProbe.Evaluate(future, Snapshot(current, next.Substring(1))) == null, "STATUS_SELFTEST_NO_ECHO_OR_OLD_CODE");
                    var early = Snapshot(current, next); var repeated = Snapshot(current, next);
                    Need(ReceiveProbe.SameCandidate(early, ReceiveProbe.Evaluate(future, early), repeated,
                        ReceiveProbe.Evaluate(future, repeated)), "STATUS_SELFTEST_EARLY_NEXT");
                    Reject(() => ReceiveProbe.Evaluate(future, Snapshot(next, next)));
                    var changed = Snapshot(current, next); changed.RootNameFingerprint = "other";
                    Reject(() => ReceiveProbe.Evaluate(future, changed));
                    baseline = future;
                }
                Need(operation.CompletedRounds == 2 && operation.SendAttempted, "STATUS_SELFTEST_TWO_REQUESTS");
                var failed = operation.StartRequest("M456789", "M567892");
                Need(failed.TryClaimRoundTrip(), "STATUS_SELFTEST_FAILED_APPROVAL");
                var pending = failed.PrepareReply();
                Need(failed.TryConsume(pending) && failed.TryCommitMove() && failed.TryCommitWrite(), "STATUS_SELFTEST_DRAFT");
                Need(!operation.FinishRequest(failed, new RoundTripTest.Outcome("uncertain"), baseline) &&
                    operation.Cancelled && operation.PendingWrite && operation.CompletedRounds == 2, "STATUS_SELFTEST_FAIL_STOP");
                Reject(() => operation.StartRequest("M567892", "M678923"));
                Need(!failed.TryCommit(), "STATUS_SELFTEST_CANCELLED_CLICK");
                var stopped = new StatusSession("M678923", true, null);
                Need(stopped.TryClaim(), "STATUS_SELFTEST_CANCEL_CLAIM");
                stopped.Cancel();
                Reject(() => stopped.StartRequest("M678923", "M789234"));
                RunPlainCommandSelfTest(directory);
            }
            finally { operation.Cancel(); if (File.Exists(path)) File.Delete(path); }
        }

        private static void RunPlainCommandSelfTest(string directory)
        {
            var path = Path.Combine(directory, "plain-status-session-tokens.txt");
            var endpoint = new SlaveEndpoint(System.Net.IPAddress.Loopback, 1, new string('0', 64),
                Convert.ToBase64String(new byte[32]));
            var operation = new StatusSession("M678923", true, endpoint, true);
            ProbeSnapshot History(int appended)
            {
                var snapshot = ReceiveProbe.CreateTestSnapshot();
                ReceiveProbe.SetTestName(snapshot.Nodes.Single(n => n.Node == 52), "help");
                for (var i = 0; i < appended; i++)
                {
                    if (i > 0) ReceiveProbe.AppendTestHistoryText(snapshot, "HELP RESPONSE");
                    ReceiveProbe.AppendTestHistoryText(snapshot, "help");
                }
                return snapshot;
            }
            void Reject(Action action)
            {
                try { action(); } catch (MonitorException) { return; }
                throw new InvalidOperationException("Unsafe plain-command session transition was accepted.");
            }
            try
            {
                Need(operation.TryClaim(), "COMMAND_SESSION_SELFTEST_CLAIM");
                var baseline = ReceiveProbe.CreateBaseline(History(0), "M678923", true);
                var codes = new[] { "M678923", "M789234", "M892345", "M923456" };
                for (var round = 0; round < 2; round++)
                {
                    var consent = operation.StartRequest(codes[round], codes[round + 1]);
                    Need(consent.IsPlainCommands && consent.TryClaimRoundTrip(), "COMMAND_SESSION_SELFTEST_APPROVAL");
                    var before = History(round + 1); var after = History(round + 1);
                    var owner = new object(); var window = new IntPtr(before.Process.WindowHandle);
                    var proof = new ReceiveProbe.ObservationProof(owner, window, new NativeMethods.WindowRectangle(), baseline,
                        before, ReceiveProbe.Evaluate(baseline, before), after, ReceiveProbe.Evaluate(baseline, after));
                    string command;
                    Need(ReadOnlyCommands.TryMatchHash(proof.Candidate.Identity.NameHash, out command), "COMMAND_SESSION_SELFTEST_MATCH");
                    consent.BindCommand(command);
                    var reply = consent.PrepareReply(); // Help is local; this dummy endpoint must never be contacted.
                    Need(!reply.Contains("NEXT") && ReadOnlyCommands.IsReply(reply, command, consent.Marker),
                        "COMMAND_SESSION_SELFTEST_REPLY");
                    var handoff = History(round + 1);
                    var future = ReceiveProbe.CreateBaseline(handoff, codes[round + 1], true);
                    RoundTripTest.AuthorizeHandoff(proof, owner, window, codes[round], handoff, path, () => false);
                    Need(!new TokenStore(path).TryReserve("roundtrip-diagnostic-v1:" + codes[round]), "COMMAND_SESSION_SELFTEST_DURABLE");
                    Need(consent.TryConsume(reply) && consent.TryCommitMove() && consent.TryCommitWrite() && consent.TryCommit(),
                        "COMMAND_SESSION_SELFTEST_ONE_SEND");
                    consent.Cancel();
                    Need(operation.FinishRequest(consent, new RoundTripTest.Outcome("test",
                        new SupervisedSendTest.Outcome("ACTION_RETURNED", "NONE", "test", true)), future), "COMMAND_SESSION_SELFTEST_ADVANCE");
                    Need(ReceiveProbe.Evaluate(future, History(round + 1)) == null, "COMMAND_SESSION_SELFTEST_OLD_IGNORED");
                    baseline = future;
                }
                Need(operation.CompletedRounds == 2 && operation.SendAttempted, "COMMAND_SESSION_SELFTEST_TWO_REQUESTS");
                var pending = operation.StartRequest(codes[2], codes[3]);
                Need(pending.TryClaimRoundTrip(), "COMMAND_SESSION_SELFTEST_PENDING_CLAIM");
                pending.BindCommand("help");
                var draft = pending.PrepareReply();
                Need(pending.TryConsume(draft) && pending.TryCommitMove() && pending.TryCommitWrite(), "COMMAND_SESSION_SELFTEST_PENDING_WRITE");
                Need(!operation.FinishRequest(pending, new RoundTripTest.Outcome("uncertain"), baseline) &&
                    operation.Cancelled && operation.PendingWrite && operation.CompletedRounds == 2 && !pending.TryCommit(),
                    "COMMAND_SESSION_SELFTEST_UNCERTAIN_STOP");
                Reject(() => operation.StartRequest(codes[3], "M234568"));
            }
            finally { operation.Cancel(); if (File.Exists(path)) File.Delete(path); }
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new MonitorException(reason, "Status session stopped: " + reason + ".");
        }
    }
}
