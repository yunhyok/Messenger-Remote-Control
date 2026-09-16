using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using RemoteMonitorLink;

namespace RemoteMonitorMaster
{
    // One PC status reply or the legacy two fixed replies; both reuse the same bounded transport.
    internal sealed class RoundTripSession
    {
        internal readonly string FirstMarker, SecondMarker;
        internal string FirstReply { get { return consents[0].Marker; } }
        internal string SecondReply { get { return consents.Length == 2 ? consents[1].Marker : null; } }
        internal int MaximumReplies { get { return consents.Length; } }
        private readonly SupervisedSendTest.Consent[] consents;
        private int cancelled, claimed, completedRounds;

        internal RoundTripSession(string firstMarker, string secondMarker, bool confirmed)
            : this(firstMarker, secondMarker, confirmed, false) { }

        internal RoundTripSession(string firstMarker, string secondMarker, bool confirmed, bool pcStatus)
            : this(firstMarker, secondMarker, confirmed, pcStatus, null) { }

        internal RoundTripSession(string firstMarker, string secondMarker, bool confirmed, bool pcStatus, SlaveEndpoint slave)
        {
            Need(confirmed, "SESSION_CONFIRMATION_REQUIRED");
            Need(slave == null || pcStatus, "SLAVE_STATUS_MODE_REQUIRED");
            Need(Protocol.IsDiagnosticMarker("MESSAGE", firstMarker) && (pcStatus ||
                (Protocol.IsDiagnosticMarker("MESSAGE", secondMarker) && firstMarker != secondMarker)), "SESSION_DISTINCT_MARKERS_REQUIRED");
            FirstMarker = firstMarker; SecondMarker = pcStatus ? null : secondMarker;
            consents = pcStatus ? new[] { new SupervisedSendTest.Consent("D" + firstMarker.Substring(1), true, true, slave) }
                : new[] { new SupervisedSendTest.Consent("D" + firstMarker.Substring(1), true),
                    new SupervisedSendTest.Consent("D" + secondMarker.Substring(1), true) };
        }

        internal bool Cancelled { get { return Volatile.Read(ref cancelled) != 0; } }
        internal bool CursorMoveAttempted { get { return consents.Any(c => c.CursorMoveAttempted); } }
        internal bool WriteAttempted { get { return consents.Any(c => c.WriteAttempted); } }
        internal bool SendAttempted { get { return consents.Any(c => c.Attempted); } }
        internal bool PendingWrite { get { return consents.Any(c => c.WriteAttempted && !c.Attempted); } }
        internal int CompletedRounds { get { return Volatile.Read(ref completedRounds); } }
        internal SupervisedSendTest.Consent GetConsent(int round)
        {
            Need(round >= 0 && round < MaximumReplies, "SESSION_ROUND_INVALID");
            return consents[round];
        }
        internal void Cancel()
        {
            Interlocked.Exchange(ref cancelled, 1);
            foreach (var consent in consents) consent.Cancel();
        }
        private bool TryClaim() { return !Cancelled && Interlocked.CompareExchange(ref claimed, 1, 0) == 0; }

        internal string Run(IntPtr window, AuditLog log, Func<bool> stop, Action<int, string> progress)
        {
            return RunWithStatePath(window, log, stop, progress,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RemoteMonitorMaster", "state", "roundtrip-seen-tokens.txt"));
        }

        internal string RunWithStatePath(IntPtr window, AuditLog log, Func<bool> stop, Action<int, string> progress, string statePath)
        {
            var clock = Stopwatch.StartNew();
            ReceiveProbe.Baseline original = null;
            NativeMethods.WindowRectangle? originalBounds = null;
            var round = -1;
            string guardFailure = null, lastMessage = null;
            try
            {
                Need(TryClaim(), Volatile.Read(ref claimed) != 0 ? "SESSION_ALREADY_USED" : "SESSION_CANCELLED");
                Need(log != null && stop != null && progress != null && !string.IsNullOrEmpty(statePath), "SESSION_REQUEST_INVALID");
                Need(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "PROBE_REQUIRES_MTA");
                bool Stopped()
                {
                    if (guardFailure != null) return true;
                    if (Cancelled || stop()) guardFailure = "SESSION_CANCELLED";
                    else if (clock.Elapsed >= TimeSpan.FromSeconds(180)) guardFailure = "SESSION_TIME_LIMIT";
                    else if (window == IntPtr.Zero || !NativeMethods.IsWindow(window)) guardFailure = "SESSION_NO_TARGET";
                    else if (NativeMethods.GetForegroundWindow() != window) guardFailure = "SESSION_FOREGROUND_CHANGED";
                    if (guardFailure == null && originalBounds.HasValue)
                    {
                        NativeMethods.WindowRectangle current;
                        if (!NativeMethods.GetWindowRect(window, out current) || !current.Equals(originalBounds.Value))
                            guardFailure = "SESSION_WINDOW_MOVED";
                    }
                    if (guardFailure == null && original != null)
                    {
                        uint pid;
                        if (NativeMethods.GetWindowThreadProcessId(window, out pid) == 0 || pid != original.Snapshot.Process.ProcessId)
                            guardFailure = "SESSION_PROCESS_CHANGED";
                    }
                    if (guardFailure != null) Cancel();
                    return guardFailure != null;
                }
                void Alive() { Need(!Stopped(), guardFailure); }
                Alive();
                NativeMethods.WindowRectangle bounds;
                Need(NativeMethods.GetWindowRect(window, out bounds), "SESSION_WINDOW_BOUNDS_UNAVAILABLE");
                originalBounds = bounds;
                Alive();
                log.Write("INFO", "ROUNDTRIP_SESSION_BEGIN", AuditLog.Field("approved_rounds", MaximumReplies),
                    AuditLog.Field("maximum_replies", MaximumReplies), AuditLog.Field("pc_status", consents[0].IsPcStatus),
                    AuditLog.Field("cooperative_seconds", 180),
                    AuditLog.Field("individual_call_timeout", false), AuditLog.Field("plain_body_verified", false),
                    AuditLog.Field("automatic_send_allowed", false));
                for (round = 0; round < MaximumReplies; round++)
                {
                    Alive();
                    var activeRound = round;
                    var marker = round == 0 ? FirstMarker : SecondMarker;
                    var priorBaselineCount = -1;
                    var priorLastCount = -1;
                    log.Write("INFO", "ROUNDTRIP_SESSION_ROUND_BEGIN", AuditLog.Field("round_index", round),
                        AuditLog.Field("completed_rounds", CompletedRounds));
                    var result = RoundTripTest.ObserveWithStatePath(window, log, marker, consents[round], Stopped,
                        phase => { Alive(); progress(activeRound, phase); Alive(); }, statePath, (phase, snapshot) =>
                        {
                            Alive();
                            if (original == null)
                            {
                                Need(activeRound == 0 && phase == "BASELINE", "SESSION_INITIAL_BASELINE_REQUIRED");
                                original = ValidateInitial(snapshot, FirstMarker, SecondMarker);
                            }
                            else ReceiveProbe.ValidateContinuity(original, snapshot);
                            Need(original.Snapshot.Process.WindowHandle == window.ToInt64(), "SESSION_WINDOW_CHANGED");
                            if (phase == "BASELINE")
                            {
                                // A future code sent before its own READY is not accepted as a new round.
                                ReceiveProbe.CreateBaseline(snapshot, marker);
                                RequireAbsent(snapshot, consents[activeRound].Marker);
                            }
                            if (activeRound == 1)
                            {
                                var count = PreviousLabelCount(snapshot, FirstMarker);
                                if (phase == "BASELINE") priorBaselineCount = count;
                                else if (phase == "POLL" && count > priorBaselineCount && count != priorLastCount)
                                    log.Write("INFO", "ROUNDTRIP_SESSION_PREVIOUS_LABEL", AuditLog.Field("round_index", activeRound),
                                        AuditLog.Field("baseline_count", priorBaselineCount), AuditLog.Field("observed_count", count),
                                        AuditLog.Field("classification", "ADDITIONAL_PRIOR_LABEL_OBSERVED"),
                                        AuditLog.Field("reply_eligible", false), AuditLog.Field("plain_body_verified", false));
                                priorLastCount = count;
                            }
                            Alive();
                        });
                    lastMessage = result.Message;
                    Alive();
                    if (!TryCompleteRound(round, result))
                    {
                        Cancel();
                        LogResult(log, "STOPPED_UNCLEAN", "SESSION_ROUND_NOT_CLEAN", round, clock.ElapsedMilliseconds);
                        return "SESSION_STOPPED - This round was not cleanly completed; no later round will run." +
                            Environment.NewLine + lastMessage;
                    }
                    log.Write("INFO", "ROUNDTRIP_SESSION_ROUND_COMPLETE", AuditLog.Field("round_index", round),
                        AuditLog.Field("completed_rounds", CompletedRounds), AuditLog.Field("clean_completion", true),
                        AuditLog.Field("delivery_verified", false));
                    progress(round, "ROUND_COMPLETE");
                    Alive();
                }
                LogResult(log, "COMPLETE", "NONE", MaximumReplies - 1, clock.ElapsedMilliseconds);
                return "SESSION_COMPLETE - " + MaximumReplies + " of " + MaximumReplies + " reply stages ended cleanly. Phone delivery is NOT verified." +
                    Environment.NewLine + lastMessage;
            }
            catch (Exception ex)
            {
                Cancel();
                var reason = (ex as MonitorException)?.ReasonCode ?? "SESSION_FAILED";
                try
                {
                    if (log != null)
                    {
                        log.WriteException("ROUNDTRIP_SESSION_FAILED", ex, AuditLog.Field("round_index", round));
                        LogResult(log, "STOPPED", reason, round, clock.ElapsedMilliseconds);
                    }
                }
                catch { }
                return "SESSION_STOPPED - " + reason + ". No further round will run. Do not retry." +
                    (lastMessage == null ? "" : Environment.NewLine + lastMessage);
            }
            finally
            {
                // Retire any unused approval even on normal completion; neither consent nor this session can be reset.
                foreach (var consent in consents) consent.Cancel();
            }
        }

        private bool TryCompleteRound(int round, RoundTripTest.Outcome outcome)
        {
            return !Cancelled && Volatile.Read(ref claimed) == 1 && round >= 0 && round < MaximumReplies &&
                outcome != null && outcome.CleanCompletion && consents[round].CursorMoveAttempted &&
                consents[round].WriteAttempted && consents[round].Attempted &&
                Interlocked.CompareExchange(ref completedRounds, round + 1, round) == round;
        }

        private static ReceiveProbe.Baseline ValidateInitial(ProbeSnapshot snapshot, string first, string second)
        {
            var baseline = ReceiveProbe.CreateBaseline(snapshot, first);
            if (second == null) RequireAbsent(snapshot, first, "D" + first.Substring(1));
            else RequireAbsent(snapshot, first, second, "D" + first.Substring(1), "D" + second.Substring(1));
            return baseline;
        }
        private static void RequireAbsent(ProbeSnapshot snapshot, params string[] labels)
        {
            var hashes = labels.Select(TokenStore.Hash).ToArray();
            Need(!snapshot.Nodes.Any(n => hashes.Contains(n.Identity.NameHash)), "SESSION_APPROVED_LABEL_ALREADY_PRESENT");
        }
        private static int PreviousLabelCount(ProbeSnapshot snapshot, string marker)
        {
            var hash = TokenStore.Hash(marker);
            return ReceiveProbe.SelectHistory(snapshot).Texts.Count(n => n.Visible && n.Enabled && n.Identity.NameHash == hash);
        }
        private void LogResult(AuditLog log, string status, string reason, int round, long elapsed)
        {
            log.Write("INFO", "ROUNDTRIP_SESSION_RESULT", AuditLog.Field("status", status), AuditLog.Field("reason", reason),
                AuditLog.Field("round_index", round), AuditLog.Field("completed_rounds", CompletedRounds),
                AuditLog.Field("maximum_replies", MaximumReplies), AuditLog.Field("pc_status", consents[0].IsPcStatus), AuditLog.Field("elapsed_ms", elapsed),
                AuditLog.Field("cursor_move_attempted", CursorMoveAttempted), AuditLog.Field("write_attempted", WriteAttempted),
                AuditLog.Field("send_attempted", SendAttempted), AuditLog.Field("pending_write", PendingWrite),
                AuditLog.Field("plain_body_verified", false), AuditLog.Field("conversation_identity_verified", false),
                AuditLog.Field("delivery_verified", false), AuditLog.Field("automatic_send_allowed", false));
        }

        internal static void RunSelfTest(string directory)
        {
            const string first = "M234567", second = "M345678";
            void Reject(Action operation)
            {
                try { operation(); } catch (MonitorException) { return; }
                throw new InvalidOperationException("Unsafe fixed-round session was accepted.");
            }
            void Commit(SupervisedSendTest.Consent consent)
            {
                Need(consent.TryConsume(consent.Marker) && consent.TryCommitMove() && consent.TryCommitWrite() && consent.TryCommit(),
                    "SESSION_SELF_TEST_SYNTHETIC_COMMIT");
            }
            var clean = new RoundTripTest.Outcome("synthetic only", new SupervisedSendTest.Outcome("ACTION_RETURNED", "NONE", "", true));
            var unclean = new RoundTripTest.Outcome("synthetic only", new SupervisedSendTest.Outcome("UNKNOWN", "TEST", "", false));
            var statusSession = new RoundTripSession(first, null, true, true);
            Need(statusSession.MaximumReplies == 1 && statusSession.SecondReply == null && statusSession.TryClaim(), "STATUS_SESSION_SELF_TEST_BOUND");
            Reject(() => statusSession.GetConsent(1));
            var statusConsent = statusSession.GetConsent(0);
            Need(statusConsent.IsPcStatus && statusConsent.TryClaimRoundTrip(), "STATUS_SESSION_SELF_TEST_CONSENT");
            var statusReply = statusConsent.PrepareReply();
            Need(statusConsent.TryConsume(statusReply) && statusConsent.TryCommitMove() && statusConsent.TryCommitWrite() &&
                statusConsent.TryCommit() && statusSession.TryCompleteRound(0, clean) &&
                !statusSession.TryCompleteRound(1, clean) && statusSession.CompletedRounds == 1 && !statusSession.TryClaim(),
                "STATUS_SESSION_SELF_TEST_ONCE");
            statusSession.Cancel();
            Need(statusConsent.Cancelled, "STATUS_SESSION_SELF_TEST_CANCEL");
            Reject(() => new RoundTripSession(first, second, false));
            Reject(() => new RoundTripSession(first, first, true));
            Reject(() => new RoundTripSession(first, "M123456", true));
            var stopped = new RoundTripSession(first, second, true);
            stopped.Cancel();
            Need(stopped.Cancelled && stopped.GetConsent(0).Cancelled && stopped.GetConsent(1).Cancelled && !stopped.TryClaim(),
                "SESSION_SELF_TEST_CANCEL");
            var session = new RoundTripSession(first, second, true);
            Need(session.TryClaim() && !session.TryClaim() && session.FirstReply == "D234567" && session.SecondReply == "D345678",
                "SESSION_SELF_TEST_CLAIM");
            Need(!session.TryCompleteRound(0, clean), "SESSION_SELF_TEST_REAL_FIELDS_REQUIRED");
            Commit(session.GetConsent(0));
            Need(!session.TryCompleteRound(0, unclean) && !session.TryCompleteRound(1, clean) && session.CompletedRounds == 0,
                "SESSION_SELF_TEST_UNCLEAN_OR_ORDER");
            Need(session.TryCompleteRound(0, clean) && !session.TryCompleteRound(0, clean) && session.CompletedRounds == 1,
                "SESSION_SELF_TEST_ONCE");
            var next = session.GetConsent(1);
            Need(next.TryConsume(next.Marker) && next.TryCommitMove() && next.TryCommitWrite(), "SESSION_SELF_TEST_PENDING_SETUP");
            session.Cancel();
            Need(session.PendingWrite && session.WriteAttempted && session.SendAttempted && session.CursorMoveAttempted &&
                !next.TryCommit() && !session.TryCompleteRound(1, clean) && session.CompletedRounds == 1, "SESSION_SELF_TEST_PENDING_CANCEL");
            var complete = new RoundTripSession(first, second, true);
            Need(complete.TryClaim(), "SESSION_SELF_TEST_START");
            for (var round = 0; round < 2; round++)
            {
                Commit(complete.GetConsent(round));
                Need(complete.TryCompleteRound(round, clean), "SESSION_SELF_TEST_TWO_ROUNDS");
            }
            Need(complete.CompletedRounds == 2 && !complete.TryCompleteRound(2, clean) && !complete.TryClaim(), "SESSION_SELF_TEST_BOUND");
            var baseline = ValidateInitial(ReceiveProbe.CreateTestSnapshot(), first, second);
            foreach (var label in new[] { first, second, "D234567", "D345678" })
            {
                var old = ReceiveProbe.CreateTestSnapshot();
                ReceiveProbe.SetTestName(old.Nodes.Single(n => n.Node == 52), label);
                Reject(() => ValidateInitial(old, first, second));
            }
            var duplicate = ReceiveProbe.CreateTestSnapshot();
            foreach (var node in duplicate.Nodes.Where(n => n.Node == 52 || n.Node == 54)) ReceiveProbe.SetTestName(node, first);
            ReceiveProbe.ValidateContinuity(baseline, duplicate); // An old-code duplicate is ignored, not a new eligible reply.
            Need(PreviousLabelCount(duplicate, first) == 2 && ReceiveProbe.Evaluate(ReceiveProbe.CreateBaseline(duplicate, second), duplicate) == null,
                "SESSION_SELF_TEST_PRIOR_DUPLICATE");
            duplicate.RootNameFingerprint = "changed";
            Reject(() => ReceiveProbe.ValidateContinuity(baseline, duplicate));
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new MonitorException(reason, "Fixed-round session rejected: " + reason + ".");
        }
    }
}
