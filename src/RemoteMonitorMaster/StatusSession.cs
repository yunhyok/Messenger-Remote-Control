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
        private CancellationTokenSource watchdogQuery; // The in-flight periodic check; Cancel() revokes it.
        // Consecutive rounds that ended without a clean completion and without any input; the third one ends the session.
        internal const int AbortResumeLimit = 3;
        // A watchdog notice rejected before any input is offered again at an idle yield no sooner than this, so a
        // persistent pre-input rejection cannot re-activate the chat in a tight loop. A due check still yields at once.
        internal static readonly TimeSpan WatchdogNoticeRetryDelay = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan WatchdogNoticeRetryCeiling = TimeSpan.FromMinutes(10);

        // Consecutive pre-input deferrals back off 30 s, 60 s, 120 s ... up to 10 minutes, so a persistently blocked
        // notice (for example a busy Master PC) does not re-arm the idle gate every half minute; a clean send resets it.
        internal static TimeSpan WatchdogNoticeRetryAfter(int consecutiveDeferrals)
        {
            if (consecutiveDeferrals <= 1) return WatchdogNoticeRetryDelay;
            var ticks = WatchdogNoticeRetryDelay.Ticks;
            for (var i = 1; i < consecutiveDeferrals && ticks < WatchdogNoticeRetryCeiling.Ticks; i++) ticks *= 2;
            return ticks < WatchdogNoticeRetryCeiling.Ticks ? TimeSpan.FromTicks(ticks) : WatchdogNoticeRetryCeiling;
        }

        // Session-owned watch list (plain-command sessions). Commands arm/disarm it through the attached consent;
        // the session runs the periodic check at an idle yield of the command wait. It ends with the session.
        internal WatchdogState Watchdog { get; }
        // WatchdogState reads its schedule under its own lock; safe from the UI thread.
        internal string WatchdogSummary { get { return Watchdog.Summary(DateTime.Now); } }

        // One queued WATCHDOG notice: a fresh code per notice, parts sent in order and never resent once clean.
        private sealed class QueuedNotice
        {
            internal readonly string Kind, RequestMarker, Marker;
            internal readonly string[] Parts;
            internal int Sent;
            internal QueuedNotice(string kind, string requestMarker, string[] parts)
            { Kind = kind; RequestMarker = requestMarker; Marker = "D" + requestMarker.Substring(1); Parts = parts; }
        }

        internal StatusSession(string firstMarker, bool confirmed, SlaveEndpoint slave)
            : this(firstMarker, confirmed, slave, false) { }

        internal StatusSession(string firstMarker, bool confirmed, SlaveEndpoint slave, bool plainCommands)
        {
            Need(confirmed && Protocol.IsDiagnosticMarker("MESSAGE", firstMarker), "STATUS_APPROVAL_REQUIRED");
            this.firstMarker = firstMarker;
            this.slave = slave;
            this.plainCommands = plainCommands;
            // Run applies the saved 30/60-minute interval; construction stays free of file access.
            Watchdog = new WatchdogState(TimeSpan.FromMinutes(WatchdogState.DefaultIntervalMinutes));
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
            try { Volatile.Read(ref watchdogQuery)?.Cancel(); } catch (ObjectDisposedException) { }
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
                if (plainCommands) consent.AttachWatchdog(Watchdog); // watchdog on/off replies arm/disarm this session's list.
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

        // Unknown counts as attempted: any committed move/write/click of this round, or a send outcome that reports one.
        // A round that never reached a send keeps Flags(consent) == 0 and either no send outcome or one with
        // InputAttempted false, which is the only case the caller may resume from.
        private static bool InputAttempted(SupervisedSendTest.Consent consent, RoundTripTest.Outcome outcome)
        {
            return Flags(consent) != 0 || outcome == null || (outcome.Send != null && outcome.Send.InputAttempted);
        }

        // Pure rule for a round that did not complete cleanly. Uncertain delivery always wins: an attempted send is
        // never retried or resumed, only reported. Cancellation stops as well; only an untouched round may resume.
        internal static string DecideAfterAbort(bool cancelled, bool inputAttempted, int consecutiveAborts, int limit)
        {
            if (inputAttempted) return "STATUS_REQUEST_STOPPED";
            if (cancelled) return "STATUS_SESSION_CANCELLED";
            if (consecutiveAborts >= limit) return "STATUS_REQUEST_ABORT_LIMIT";
            return "RESUME";
        }

        // Retires an aborted round without completing it: no completed round, no cancellation, no consent reuse.
        // The round's attempt bits are folded into the session before the consent is dropped.
        private bool TryReleaseAbortedRequest(SupervisedSendTest.Consent consent, RoundTripTest.Outcome outcome)
        {
            lock (sync)
            {
                if (Cancelled || !ReferenceEquals(active, consent) || outcome == null || outcome.CleanCompletion ||
                    InputAttempted(consent, outcome)) return false;
                attempts |= Flags(consent);
                Volatile.Write(ref active, null);
                return true;
            }
        }

        // Retires a request whose idle wait was handed back for a watchdog task: nothing was observed, prepared or
        // typed, so it is neither a completed round nor an abort. The caller re-enters with the same code and baseline.
        private bool TryReleaseYieldedRequest(SupervisedSendTest.Consent consent, RoundTripTest.Outcome outcome)
        {
            lock (sync)
            {
                if (Cancelled || !ReferenceEquals(active, consent) || outcome == null || !outcome.Yielded ||
                    outcome.CleanCompletion || outcome.Send != null || Flags(consent) != 0) return false;
                attempts |= Flags(consent);
                Volatile.Write(ref active, null);
                return true;
            }
        }

        // Pure rule for one WATCHDOG notice part. Only a clean guarded send advances; a part rejected before any
        // cursor move, write or click stays queued for a later idle yield (not a resend: nothing was typed); any
        // attempted or contradictory send is uncertain and ends the session without retry.
        internal static string DecideAfterNotice(bool clean, bool inputAttempted)
        {
            if (clean && inputAttempted) return "SENT";
            if (!clean && !inputAttempted) return "DEFERRED";
            return "STATUS_WATCHDOG_NOTICE_UNCERTAIN";
        }

        // A notice never goes out while a command is waiting after the current Ready: that command is handled first.
        // waitBaseline is the pre-Ready baseline of the current wait; the Ready row is re-bound on this snapshot.
        internal static void RequireNoPendingCommand(ReceiveProbe.Baseline waitBaseline, ProbeSnapshot snapshot, AuditLog log = null)
        {
            Need(waitBaseline != null && waitBaseline.PlainCommands && waitBaseline.ReadyRow < 0 && snapshot != null,
                "WATCHDOG_NOTICE_BASELINE_REQUIRED");
            var bound = ReceiveProbe.BindReadyBoundary(waitBaseline, snapshot, log);
            Need(bound != null, "WATCHDOG_NOTICE_READY_NOT_BOUND");
            bool blocked;
            var candidate = ReceiveProbe.Evaluate(bound, snapshot, log, out blocked);
            Need(candidate == null && !blocked, "WATCHDOG_NOTICE_COMMAND_PENDING");
        }

        // Phone-facing reason for a Slave failure streak; a code, never exception text.
        internal static string CheckFailureCode(Exception exception)
        {
            if (exception is TimeoutException) return "SLAVE_TIMEOUT";
            if (exception is System.Security.Authentication.AuthenticationException) return "SLAVE_AUTHENTICATION";
            if (exception is InvalidDataException) return "SLAVE_RESPONSE_INVALID";
            if (exception is IOException || exception is System.Net.Sockets.SocketException) return "SLAVE_CONNECTION";
            return "SLAVE_QUERY_FAILED";
        }

        private static bool IsExpectedCheckFailure(Exception exception)
        {
            return exception is MonitorException || exception is TimeoutException || exception is InvalidDataException ||
                exception is IOException || exception is System.Net.Sockets.SocketException ||
                exception is System.Security.Authentication.AuthenticationException || exception is OperationCanceledException;
        }

        internal string Run(IntPtr window, AuditLog log, Func<bool> stop, Action<int, string, string> progress)
        {
            var clock = Stopwatch.StartNew();
            string last = null;
            var pendingNotices = new List<QueuedNotice>(); // Watchdog notices prepared by a check and not yet sent cleanly.
            try
            {
                Need(TryClaim(), "STATUS_SESSION_ALREADY_USED_OR_CANCELLED");
                Need(log != null && stop != null && progress != null, "STATUS_REQUEST_INVALID");
                Need(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "PROBE_REQUIRES_MTA");
                // Fail before the expensive identity capture; OperationalTarget still performs its own check.
                Need(!plainCommands || NativeMethods.GetForegroundWindow() == window, "TARGET_INITIAL_FOREGROUND_REQUIRED");
                var process = ProcessIdentity.Capture(window);
                Need(string.Equals(process.ProcessName, "KI-Messenger", StringComparison.OrdinalIgnoreCase),
                    "PROBE_NOT_KI_MESSENGER");
                NativeMethods.WindowRectangle bounds;
                Need(NativeMethods.GetWindowRect(window, out bounds), "STATUS_WINDOW_UNAVAILABLE");
                var reported = firstMarker; // The background phase callback must name the current round, not the first.
                var target = plainCommands ? new OperationalTarget(window, process, bounds,
                    () => Cancelled || stop(), phase => progress(CompletedRounds, phase, reported), log) : null;
                bool Stopped()
                {
                    if (Cancelled || stop()) { Cancel(); return true; }
                    if (target != null) { target.Check(); return false; }
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
                reported = marker;
                ReceiveProbe.Baseline original = null, baseline = null;
                var consecutiveAborts = 0;
                var resumedBaselineCheck = false; // A resumed round re-proves reply absence for its new code.
                void SendReady(string requestMarker)
                {
                    Alive();
                    var notice = SupervisedSendTest.Consent.ForNotice("D" + requestMarker.Substring(1), SupervisedSendTest.ReadyNotice);
                    Volatile.Write(ref activeNotice, notice);
                    Alive();
                    progress(CompletedRounds, "NOTICE_READY", requestMarker);
                    target?.PrepareSend();
                    var result = SupervisedSendTest.RunBoundObserved(window, log, notice.NoticeText,
                        new System.Windows.Point(), notice, Stopped, snapshot =>
                        {
                            Alive();
                            Need(process.Equals(snapshot.Process), "STATUS_PROCESS_CHANGED");
                            if (original == null)
                            {
                                original = ReceiveProbe.CreateBaseline(snapshot, requestMarker, true);
                            }
                            else
                            {
                                ReceiveProbe.ValidateContinuity(original, snapshot);
                            }
                            // The next receive binds to this exact Ready, without swallowing a fast following command.
                            ReceiveProbe.RequireReadyAbsent(snapshot, requestMarker);
                            baseline = ReceiveProbe.CreateBaseline(snapshot, requestMarker, true);
                        });
                    last = result.Message;
                    Need(result.CleanCompletion, "STATUS_READY_NOTICE_UNCERTAIN");
                    lock (sync) { attempts |= Flags(notice); Volatile.Write(ref activeNotice, null); }
                    notice.Cancel();
                    log.Write("INFO", "MASTER_NOTICE_SENT", AuditLog.Field("stage", "READY"), AuditLog.Field("delivery_verified", false));
                    progress(CompletedRounds, "WATCHDOG:" + Watchdog.Summary(DateTime.Now), requestMarker);
                }
                // A later notice attempt waits at least WatchdogNoticeRetryDelay after a deferral; a due check yields at once.
                var noticeRetryUtc = DateTime.MinValue;
                var noticeDeferrals = 0; // Consecutive deferrals since the last clean notice part.
                void QueueNotice(string kind, Func<string, string[]> build)
                {
                    // Same allocation as a request code, so a notice never shares a D code with any Ready/reply of this session.
                    var requestMarker = Allocate(store, allocated, "M" + Protocol.CreateDiagnosticDigits());
                    var parts = build("D" + requestMarker.Substring(1));
                    if (parts == null || parts.Length == 0) return;
                    pendingNotices.Add(new QueuedNotice(kind, requestMarker, parts));
                    log.Write("INFO", "WATCHDOG_NOTICE_QUEUED", AuditLog.Field("kind", kind), AuditLog.Field("parts", parts.Length),
                        AuditLog.Field("payload_characters", parts.Sum(part => (long)part.Length)),
                        AuditLog.Field("queued_notices", pendingNotices.Count));
                }
                // One periodic check. A failed collection is recorded and watching continues; it never ends the session.
                // Only completion markers of the returned report are evaluated: PowerSI Output history is never read or advanced.
                void RunWatchdogCheck(int checkRound, string waitMarker)
                {
                    Alive();
                    progress(checkRound, "WATCHDOG_CHECKING", waitMarker);
                    int entries, intervalMinutes, streak;
                    DateTime? due;
                    Watchdog.ReadSchedule(out entries, out due, out intervalMinutes, out streak);
                    log.Write("INFO", "WATCHDOG_CHECK_BEGIN", AuditLog.Field("entries", entries),
                        AuditLog.Field("interval_min", intervalMinutes), AuditLog.Field("failure_streak", streak));
                    PowerSiReport report = null;
                    Alive(); // A stop or target change before the query is never a failed check.
                    var query = new CancellationTokenSource();
                    Volatile.Write(ref watchdogQuery, query);
                    try
                    {
                        if (Cancelled) query.Cancel(); // Cancel() may have run before the source was published.
                        var status = StatusClient.QueryAsync(slave, query.Token, true).GetAwaiter().GetResult();
                        query.Token.ThrowIfCancellationRequested();
                        report = status?.PowerSiReport;
                        if (report == null) throw new InvalidDataException("PowerSI report is missing.");
                    }
                    catch (Exception ex)
                    {
                        Alive(); // Stop or a target change ends the session; it is never counted as a failed check.
                        var type = ex.GetType().Name;
                        Watchdog.RecordCheckFailure(type, DateTime.UtcNow);
                        var failures = Watchdog.FailureStreak;
                        log.Write(IsExpectedCheckFailure(ex) ? "INFO" : "WARN", "WATCHDOG_CHECK_FAILED",
                            AuditLog.Field("exception_type", type), AuditLog.Field("failure_streak", failures));
                        if (failures == WatchdogState.FailureWarningThreshold)
                            QueueNotice("WARNING", noticeMarker => WatchdogText.WarningNotice(noticeMarker,
                                WatchdogText.KindSlaveFailure, CheckFailureCode(ex), Watchdog, DateTime.Now));
                        progress(checkRound, "WATCHDOG:" + Watchdog.Summary(DateTime.Now), waitMarker);
                        return;
                    }
                    finally { Interlocked.CompareExchange(ref watchdogQuery, null, query); }
                    var result = Watchdog.ApplyCheck(WatchdogReport.FromPowerSiReport(report), DateTime.UtcNow,
                        WatchdogReport.AbsentMeansMissing(report));
                    foreach (var outcome in result.Outcomes) // Identity, states and counts only; never names or Output text.
                        log.Write("INFO", "WATCHDOG_TARGET_EVALUATED", AuditLog.Field("pid", outcome.Target.Pid),
                            AuditLog.Field("start_utc_ticks", outcome.Target.StartUtcTicks), AuditLog.Field("outcome", outcome.Outcome),
                            AuditLog.Field("report_state", outcome.ReportState), AuditLog.Field("source", outcome.Source),
                            AuditLog.Field("output_length", outcome.OutputLength),
                            AuditLog.Field("consecutive_unjudged", outcome.ConsecutiveUnjudged));
                    log.Write("INFO", "WATCHDOG_CHECK_RESULT", AuditLog.Field("finished", result.Finished.Length),
                        AuditLog.Field("missing", result.Missing.Length), AuditLog.Field("remaining", result.Remaining),
                        AuditLog.Field("all_cleared", result.AllCleared),
                        AuditLog.Field("unjudged_streak", result.UnjudgedStreakReached.Length),
                        AuditLog.Field("absent_means_missing", WatchdogReport.AbsentMeansMissing(report)));
                    if (result.Finished.Length + result.Missing.Length > 0)
                        QueueNotice("COMPLETION", noticeMarker =>
                            WatchdogText.CompletionNotice(noticeMarker, result, Watchdog, DateTime.Now));
                    if (result.UnjudgedStreakReached.Length > 0)
                        QueueNotice("WARNING", noticeMarker => WatchdogText.WarningNotice(noticeMarker, WatchdogText.KindUnjudgeable,
                            WatchdogText.UnjudgedDetail(result.UnjudgedStreakReached), Watchdog, DateTime.Now));
                    progress(checkRound, "WATCHDOG:" + Watchdog.Summary(DateTime.Now), waitMarker);
                }
                // Sends queued parts in order through the Ready notice path. True when the queue is empty. A part rejected
                // before any input (idle gate, pending command, proof/guard failure) stays queued; an attempted but unclean
                // part ends the session and is never retried. Texts are sent as prepared: a later watchdog off does not
                // rebuild them, because they describe checks that already happened.
                bool SendQueuedNotices(int noticeRound, string waitMarker, out int sentParts)
                {
                    sentParts = 0;
                    while (pendingNotices.Count > 0)
                    {
                        var notice = pendingNotices[0];
                        var text = notice.Parts[notice.Sent];
                        var part = (notice.Sent + 1) + "/" + notice.Parts.Length;
                        Alive();
                        var noticeConsent = SupervisedSendTest.Consent.ForWatchdogNotice(notice.Marker, text);
                        Volatile.Write(ref activeNotice, noticeConsent);
                        Alive();
                        progress(noticeRound, "NOTICE_WATCHDOG", waitMarker);
                        SupervisedSendTest.Outcome result = null;
                        string reason;
                        try
                        {
                            target?.PrepareSend(); // Idle gate and on-demand activation; its failure is a deferral.
                            result = SupervisedSendTest.RunBoundObserved(window, log, text, new System.Windows.Point(), noticeConsent,
                                Stopped, snapshot =>
                                {
                                    Alive();
                                    Need(process.Equals(snapshot.Process), "STATUS_PROCESS_CHANGED");
                                    ReceiveProbe.ValidateContinuity(original, snapshot);
                                    RequireReplyAbsent(snapshot, notice.RequestMarker);
                                    RequireNoPendingCommand(baseline, snapshot, log);
                                });
                            last = result.Message;
                            reason = result.Reason;
                        }
                        catch (MonitorException ex) when (result == null) { reason = ex.ReasonCode; }
                        var inputAttempted = Flags(noticeConsent) != 0 || (result != null && result.InputAttempted);
                        var decision = DecideAfterNotice(result != null && result.CleanCompletion, inputAttempted);
                        // Uncertain delivery keeps the notice consent visible for the final summary, like an unclean Ready.
                        if (decision == "STATUS_WATCHDOG_NOTICE_UNCERTAIN")
                            throw new MonitorException(decision, "A watchdog notice was not confirmed. It is never retried.");
                        lock (sync) { attempts |= Flags(noticeConsent); Volatile.Write(ref activeNotice, null); }
                        noticeConsent.Cancel();
                        Alive(); // A stop during the attempt ends the session; it is never a deferral.
                        if (decision == "DEFERRED")
                        {
                            noticeDeferrals++;
                            noticeRetryUtc = DateTime.UtcNow + WatchdogNoticeRetryAfter(noticeDeferrals);
                            log.Write("INFO", "WATCHDOG_NOTICE_DEFERRED", AuditLog.Field("kind", notice.Kind),
                                AuditLog.Field("part", part), AuditLog.Field("reason", reason ?? "UNKNOWN"),
                                AuditLog.Field("queued_parts", pendingNotices.Sum(item => item.Parts.Length - item.Sent)),
                                AuditLog.Field("input_attempted", false), AuditLog.Field("delivery_verified", false));
                            return false;
                        }
                        notice.Sent++;
                        sentParts++;
                        if (notice.Sent == notice.Parts.Length) pendingNotices.RemoveAt(0);
                        noticeDeferrals = 0;
                        log.Write("INFO", "WATCHDOG_NOTICE_SENT", AuditLog.Field("kind", notice.Kind), AuditLog.Field("part", part),
                            AuditLog.Field("delivery_verified", false));
                    }
                    return true;
                }
                // Checked only at the idle point of the command wait (ReceiveProbe.MayYield); never while a command is pending.
                Func<bool> yieldRequested = () => (pendingNotices.Count > 0 && DateTime.UtcNow >= noticeRetryUtc) ||
                    (Watchdog.IsArmed && Watchdog.IsCheckDue(DateTime.UtcNow));
                log.Write("INFO", "STATUS_SESSION_BEGIN", AuditLog.Field("slave_status", slave != null),
                    AuditLog.Field("plain_commands", plainCommands),
                    AuditLog.Field("idle_timeout", "NONE"), AuditLog.Field("per_request_replies", plainCommands ? "BOUNDED_PREPARED_PARTS" : "ONE"),
                    AuditLog.Field("background_receive", target != null), AuditLog.Field("on_demand_activation", target != null),
                    AuditLog.Field("next_format", plainCommands ? "NONE" : "DIGITS_ONLY"), AuditLog.Field("compact_receive_log", true));
                if (plainCommands)
                {
                    Watchdog.TrySetIntervalMinutes(WatchdogSettings.LoadIntervalMinutes(WatchdogSettings.DefaultPath));
                    log.Write("INFO", "WATCHDOG_INTERVAL", AuditLog.Field("minutes", (int)Watchdog.Interval.TotalMinutes));
                }
                if (plainCommands) SendReady(marker);
                string carriedNext = null; // After a yield without a new Ready, the wait keeps its code and next code.
                while (true)
                {
                    Alive();
                    var round = CompletedRounds;
                    var nextMarker = carriedNext ?? Allocate(store, allocated, "M" + Protocol.CreateDiagnosticDigits());
                    carriedNext = null;
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
                            if (resumedBaselineCheck && phase == "BASELINE")
                            {
                                // Same guard a completed round applies to its next code, on the resumed round's baseline.
                                resumedBaselineCheck = false;
                                RequireReplyAbsent(snapshot, marker);
                            }
                            if (phase == "HANDOFF")
                            {
                                // Capture BEFORE sending. A fast following request is compared to this earlier snapshot.
                                nextBaseline = ReceiveProbe.CreateBaseline(snapshot, nextMarker, plainCommands);
                                RequireReplyAbsent(snapshot, nextMarker);
                            }
                            Alive();
                        }, baseline, true, target, plainCommands ? yieldRequested : null);
                    last = outcome.Message;
                    if (outcome != null && outcome.Yielded && TryReleaseYieldedRequest(consent, outcome))
                    {
                        var checkDue = Watchdog.IsArmed && Watchdog.IsCheckDue(DateTime.UtcNow);
                        log.Write("INFO", "STATUS_WAIT_YIELDED", AuditLog.Field("round_index", round),
                            AuditLog.Field("check_due", checkDue),
                            AuditLog.Field("queued_parts", pendingNotices.Sum(item => item.Parts.Length - item.Sent)));
                        if (checkDue) RunWatchdogCheck(round, marker);
                        var sentParts = 0;
                        var allSent = pendingNotices.Count == 0 || SendQueuedNotices(round, marker, out sentParts);
                        Alive();
                        if (allSent && sentParts > 0)
                        {
                            // The phone sees a fresh Master Ready after the notice; the new Ready binds the next wait.
                            marker = Allocate(store, allocated, "M" + Protocol.CreateDiagnosticDigits());
                            reported = marker;
                            SendReady(marker);
                        }
                        else
                        {
                            // Nothing new in the chat from Master, or a deferred part: keep the same code, the same pre-Ready
                            // baseline and the same next code. The re-entered wait re-binds the same Ready row and finds any
                            // command that arrived meanwhile; a queued part waits until that command's round is done.
                            carriedNext = nextMarker;
                        }
                        continue;
                    }
                    if (outcome == null || !outcome.CleanCompletion)
                    {
                        // Nothing typed or clicked: re-arm receiving with a new code and a new Ready. Never a resend.
                        var inputAttempted = InputAttempted(consent, outcome);
                        var abortReason = outcome?.Send?.Reason ?? "UNKNOWN";
                        consecutiveAborts++;
                        var decision = DecideAfterAbort(Cancelled || stop(), inputAttempted, consecutiveAborts, AbortResumeLimit);
                        var resumed = decision == "RESUME" && TryReleaseAbortedRequest(consent, outcome);
                        log.Write("INFO", "STATUS_REQUEST_ABORTED", AuditLog.Field("reason", abortReason),
                            AuditLog.Field("input_attempted", inputAttempted), AuditLog.Field("consecutive", consecutiveAborts),
                            AuditLog.Field("resumed", resumed), AuditLog.Field("round_index", round),
                            AuditLog.Field("delivery_verified", false));
                        if (!resumed)
                        {
                            FinishRequest(consent, outcome, nextBaseline); // Cancels; keeps the attempts/draft visible.
                            throw new MonitorException(decision == "RESUME" ? "STATUS_REQUEST_STOPPED" : decision,
                                "No next request will run.");
                        }
                        Alive();
                        progress(round, "REQUEST_RESUMED:" + abortReason, marker);
                        Alive();
                        // A fresh code, and a baseline rebuilt by the new Ready; the aborted proof/marker is never reused.
                        marker = Allocate(store, allocated, "M" + Protocol.CreateDiagnosticDigits());
                        reported = marker;
                        baseline = null;
                        resumedBaselineCheck = !plainCommands;
                        if (plainCommands) SendReady(marker);
                        continue;
                    }
                    if (!FinishRequest(consent, outcome, nextBaseline))
                        throw new MonitorException("STATUS_REQUEST_STOPPED", "No next request will run.");
                    log.Write("INFO", "STATUS_REQUEST_COMPLETE", AuditLog.Field("completed_rounds", CompletedRounds),
                        AuditLog.Field("delivery_verified", false));
                    consecutiveAborts = 0;
                    Alive();
                    progress(round, "ROUND_COMPLETE", marker);
                    Alive();
                    baseline = nextBaseline;
                    marker = nextMarker;
                    reported = marker;
                    if (plainCommands) SendReady(marker);
                }
            }
            catch (Exception ex)
            {
                Cancel();
                var reason = (ex as MonitorException)?.ReasonCode ?? "STATUS_SESSION_FAILED";
                // An unexpected failure keeps its exception identity; a declared stop reason stays INFO.
                if (!(ex is MonitorException)) { try { log?.WriteException("STATUS_SESSION_FAILED", ex); } catch { } }
                try
                {
                    // Watching ends with the session; unsent notices are dropped (never sent by another session).
                    var watched = Watchdog.Count;
                    var unsent = pendingNotices.Sum(item => item.Parts.Length - item.Sent);
                    Watchdog.ClearAll("SESSION_END");
                    pendingNotices.Clear();
                    if (watched > 0 || unsent > 0)
                        log?.Write("INFO", "WATCHDOG_CLEARED", AuditLog.Field("reason", "SESSION_END"), AuditLog.Field("entries", watched),
                            AuditLog.Field("unsent_notice_parts", unsent));
                }
                catch { }
                try { log?.Write(reason == "STATUS_SESSION_FAILED" ? "WARN" : "INFO", "STATUS_SESSION_END", AuditLog.Field("reason", reason),
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
            // Pure abort decision table first: an attempted send never resumes, cancellation never resumes,
            // the third consecutive untouched abort ends the session, and only the earlier ones re-arm receiving.
            Need(DecideAfterAbort(false, false, 1, AbortResumeLimit) == "RESUME" &&
                DecideAfterAbort(false, false, AbortResumeLimit - 1, AbortResumeLimit) == "RESUME" &&
                DecideAfterAbort(false, false, AbortResumeLimit, AbortResumeLimit) == "STATUS_REQUEST_ABORT_LIMIT" &&
                DecideAfterAbort(false, false, AbortResumeLimit + 1, AbortResumeLimit) == "STATUS_REQUEST_ABORT_LIMIT" &&
                DecideAfterAbort(false, true, 1, AbortResumeLimit) == "STATUS_REQUEST_STOPPED" &&
                DecideAfterAbort(true, true, 1, AbortResumeLimit) == "STATUS_REQUEST_STOPPED" &&
                DecideAfterAbort(true, true, AbortResumeLimit, AbortResumeLimit) == "STATUS_REQUEST_STOPPED" &&
                DecideAfterAbort(true, false, 1, AbortResumeLimit) == "STATUS_SESSION_CANCELLED" &&
                DecideAfterAbort(true, false, AbortResumeLimit, AbortResumeLimit) == "STATUS_SESSION_CANCELLED",
                "STATUS_SELFTEST_ABORT_DECISION");
            RunWatchdogSelfTest(); // Pure; runs before the PC-status capture below.
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
            Need(noticeSession.activeNotice.TryConsume(noticeSession.activeNotice.NoticeText) && noticeSession.activeNotice.TryCommitMove() &&
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
                var aborted = operation.StartRequest("M456789", "M567892");
                Need(aborted.TryClaimRoundTrip(), "STATUS_SELFTEST_ABORT_CLAIM");
                aborted.Cancel(); // The real RoundTripTest always retires the consent on return.
                var rejectedSend = new RoundTripTest.Outcome("aborted", new SupervisedSendTest.Outcome("REJECTED",
                    "ROUNDTRIP_PROOF_EXPIRED_OR_WINDOW_CHANGED", "test", false, false), false);
                Need(!operation.TryReleaseAbortedRequest(aborted, new RoundTripTest.Outcome("clean",
                    new SupervisedSendTest.Outcome("ACTION_RETURNED", "NONE", "test", true))) &&
                    !operation.TryReleaseAbortedRequest(aborted, new RoundTripTest.Outcome("unknown send",
                        new SupervisedSendTest.Outcome("UNKNOWN", "SEND_TIME_LIMIT", "test", false))),
                    "STATUS_SELFTEST_ABORT_ONLY_UNTOUCHED");
                Need(operation.TryReleaseAbortedRequest(aborted, rejectedSend) && !operation.Cancelled &&
                    operation.CompletedRounds == 2 && !operation.PendingWrite && !aborted.TryCommit(),
                    "STATUS_SELFTEST_ABORT_RESUME");
                Need(!operation.TryReleaseAbortedRequest(aborted, rejectedSend), "STATUS_SELFTEST_ABORT_RESUME_ONCE");
                var failed = operation.StartRequest("M456789", "M567892");
                Need(failed.TryClaimRoundTrip(), "STATUS_SELFTEST_FAILED_APPROVAL");
                var pending = failed.PrepareReply();
                Need(failed.TryConsume(pending) && failed.TryCommitMove() && failed.TryCommitWrite(), "STATUS_SELFTEST_DRAFT");
                Need(!operation.TryReleaseAbortedRequest(failed, new RoundTripTest.Outcome("uncertain")) &&
                    !operation.Cancelled, "STATUS_SELFTEST_ABORT_INPUT_ATTEMPTED");
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

        // Watchdog wiring without Win32: the notice decision, the yielded-request release, the pending-command guard and
        // the one-use notice consent built from real WatchdogText parts.
        private static void RunWatchdogSelfTest()
        {
            Need(DecideAfterNotice(true, true) == "SENT" && DecideAfterNotice(false, false) == "DEFERRED" &&
                DecideAfterNotice(false, true) == "STATUS_WATCHDOG_NOTICE_UNCERTAIN" &&
                DecideAfterNotice(true, false) == "STATUS_WATCHDOG_NOTICE_UNCERTAIN", "STATUS_SELFTEST_NOTICE_DECISION");
            Need(WatchdogNoticeRetryAfter(0) == WatchdogNoticeRetryDelay && WatchdogNoticeRetryAfter(1) == WatchdogNoticeRetryDelay &&
                WatchdogNoticeRetryAfter(2) == TimeSpan.FromSeconds(60) && WatchdogNoticeRetryAfter(3) == TimeSpan.FromSeconds(120) &&
                WatchdogNoticeRetryAfter(6) == WatchdogNoticeRetryCeiling && WatchdogNoticeRetryAfter(40) == WatchdogNoticeRetryCeiling,
                "STATUS_SELFTEST_NOTICE_BACKOFF");
            Need(CheckFailureCode(new TimeoutException()) == "SLAVE_TIMEOUT" &&
                CheckFailureCode(new System.Security.Authentication.AuthenticationException()) == "SLAVE_AUTHENTICATION" &&
                CheckFailureCode(new InvalidDataException()) == "SLAVE_RESPONSE_INVALID" &&
                CheckFailureCode(new IOException()) == "SLAVE_CONNECTION" &&
                CheckFailureCode(new System.Net.Sockets.SocketException()) == "SLAVE_CONNECTION" &&
                CheckFailureCode(new InvalidOperationException()) == "SLAVE_QUERY_FAILED", "STATUS_SELFTEST_CHECK_FAILURE_CODE");
            void Reject(Action action, string expected)
            {
                try { action(); }
                catch (MonitorException ex) { Need(ex.ReasonCode == expected, "STATUS_SELFTEST_WATCHDOG_REASON"); return; }
                throw new InvalidOperationException("Unsafe watchdog transition accepted.");
            }

            // Yielded requests: only an untouched, unsent, yielded outcome of the active request is released.
            var endpoint = new SlaveEndpoint(System.Net.IPAddress.Loopback, 1, new string('0', 64),
                Convert.ToBase64String(new byte[32]));
            var session = new StatusSession("M234567", true, endpoint, true);
            try
            {
                Need(session.TryClaim() && session.Watchdog != null && !session.Watchdog.IsArmed &&
                    session.Watchdog.Interval == TimeSpan.FromMinutes(WatchdogState.DefaultIntervalMinutes) &&
                    session.WatchdogSummary == "감시 없음", "STATUS_SELFTEST_WATCHDOG_OWNER");
                var waiting = session.StartRequest("M234567", "M345678");
                Need(ReferenceEquals(waiting.Watchdog, session.Watchdog) && waiting.TryClaimRoundTrip(),
                    "STATUS_SELFTEST_WATCHDOG_ATTACHED");
                waiting.Cancel(); // RoundTripTest retires every consent on return, a yield included.
                var yielded = new RoundTripTest.Outcome("yielded", null, false, true);
                var rejected = new SupervisedSendTest.Outcome("REJECTED", "RECEIVE_PHASE_TIME_LIMIT", "test", false, false);
                Need(!session.TryReleaseYieldedRequest(waiting, null) &&
                    !session.TryReleaseYieldedRequest(waiting, new RoundTripTest.Outcome("aborted", rejected, false)) &&
                    !session.TryReleaseYieldedRequest(waiting, new RoundTripTest.Outcome("not yielded")) &&
                    !session.TryReleaseYieldedRequest(new SupervisedSendTest.Consent("D234567", true, true, endpoint, null, true),
                        yielded), "STATUS_SELFTEST_YIELD_ONLY_UNTOUCHED");
                Need(session.TryReleaseYieldedRequest(waiting, yielded) && !session.Cancelled && session.CompletedRounds == 0 &&
                    !session.PendingWrite && !session.CursorMoveAttempted && !session.SendAttempted, "STATUS_SELFTEST_YIELD_RELEASED");
                Need(!session.TryReleaseYieldedRequest(waiting, yielded), "STATUS_SELFTEST_YIELD_ONCE");
                // Re-entry keeps the code and next code: a fresh one-use consent with the same watchdog.
                var resumed = session.StartRequest("M234567", "M345678");
                Need(!ReferenceEquals(resumed, waiting) && resumed.Marker == waiting.Marker &&
                    ReferenceEquals(resumed.Watchdog, session.Watchdog), "STATUS_SELFTEST_YIELD_REENTRY");
                Reject(() => session.StartRequest("M234567", "M345678"), "STATUS_SESSION_CANCELLED_OR_BUSY");
                Need(resumed.TryClaimRoundTrip(), "STATUS_SELFTEST_YIELD_REENTRY_CLAIM");
                resumed.BindCommand("help");
                var draft = resumed.PrepareReply(); // help is local; the dummy endpoint is never contacted.
                Need(resumed.TryConsume(draft) && resumed.TryCommitMove(), "STATUS_SELFTEST_YIELD_TOUCHED");
                resumed.Cancel();
                Need(!session.TryReleaseYieldedRequest(resumed, yielded) && !session.Cancelled && session.CursorMoveAttempted,
                    "STATUS_SELFTEST_YIELD_TOUCHED_KEPT");
                session.Cancel();
                Need(!session.TryReleaseYieldedRequest(resumed, yielded), "STATUS_SELFTEST_YIELD_CANCELLED");
            }
            finally { session.Cancel(); }

            // The notice waits while a command is pending after the current Ready, visible or pinned offscreen.
            const string marker = "M234567";
            var readyText = SupervisedSendTest.ReadyText("D234567");
            ProbeSnapshot History(bool ready, bool notice = false, string command = null, bool visible = true)
            {
                var snapshot = ReceiveProbe.CreateTestSnapshot();
                ReceiveProbe.SetTestName(snapshot.Nodes.Single(n => n.Node == 52), "old message");
                ReceiveProbe.SetTestName(snapshot.Nodes.Single(n => n.Node == 54), "old reply");
                if (ready) ReceiveProbe.AppendTestHistoryText(snapshot, readyText);
                if (notice)
                    ReceiveProbe.AppendTestHistoryText(snapshot,
                        "WATCHDOG D456789 | 경고\r\nSlave 조회 3회 연속 실패 (SLAVE_TIMEOUT). 감시는 유지합니다.");
                if (command != null) ReceiveProbe.AppendTestHistoryText(snapshot, command).Visible = visible;
                return snapshot;
            }
            var preReady = ReceiveProbe.CreateBaseline(History(false), marker, true);
            RequireNoPendingCommand(preReady, History(true));
            RequireNoPendingCommand(preReady, History(true, true)); // An earlier notice part is not a command.
            Reject(() => RequireNoPendingCommand(preReady, History(true, false, "pwrsi")), "WATCHDOG_NOTICE_COMMAND_PENDING");
            Reject(() => RequireNoPendingCommand(preReady, History(true, true, "help")), "WATCHDOG_NOTICE_COMMAND_PENDING");
            Reject(() => RequireNoPendingCommand(preReady, History(true, true, "pwrsi", false)), "WATCHDOG_NOTICE_COMMAND_PENDING");
            Reject(() => RequireNoPendingCommand(preReady, History(false)), "WATCHDOG_NOTICE_READY_NOT_BOUND");
            Reject(() => RequireNoPendingCommand(ReceiveProbe.BindReadyBoundary(preReady, History(true)), History(true)),
                "WATCHDOG_NOTICE_BASELINE_REQUIRED");

            // Real notice parts: each part is exactly one one-use consent bound to its own notice code.
            var state = new WatchdogState(TimeSpan.FromMinutes(30));
            var t0 = new DateTime(2026, 9, 23, 3, 0, 0, DateTimeKind.Utc);
            state.Arm(new[] { new WatchdogTarget(4321, t0.AddHours(-2).Ticks, "PowerSI.exe"),
                new WatchdogTarget(4322, t0.AddHours(-1).Ticks, "PowerSI.exe") }, null, t0);
            var check = state.ApplyCheck(new WatchdogReport[0], t0.AddMinutes(30), true); // Complete report: both gone.
            Need(check.Missing.Length == 2 && !state.IsArmed, "STATUS_SELFTEST_WATCHDOG_CHECK");
            var parts = WatchdogText.CompletionNotice("D456789", check, state, t0.ToLocalTime());
            Need(parts.Length >= 1 && parts.All(part =>
                {
                    var once = SupervisedSendTest.Consent.ForWatchdogNotice("D456789", part);
                    return part.Length <= PcStatusReport.MaxPhoneLength && once.IsAuthorizedReply(part) &&
                        !once.IsAuthorizedReply(part + " ") && once.TryConsume(part) && !once.TryConsume(part);
                }), "STATUS_SELFTEST_WATCHDOG_NOTICE_CONSENT");
            Reject(() => SupervisedSendTest.Consent.ForWatchdogNotice("D567892", parts[0]), "SEND_NOTICE_INVALID");
            Reject(() => SupervisedSendTest.Consent.ForWatchdogNotice("D456789", readyText), "SEND_NOTICE_INVALID");
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
                var untouched = operation.StartRequest(codes[2], codes[3]);
                Need(untouched.TryClaimRoundTrip(), "COMMAND_SESSION_SELFTEST_ABORT_CLAIM");
                untouched.BindCommand("pwrsi");
                untouched.Cancel();
                Need(operation.TryReleaseAbortedRequest(untouched, new RoundTripTest.Outcome("aborted",
                        new SupervisedSendTest.Outcome("REJECTED", "ROUNDTRIP_PROOF_EXPIRED_OR_WINDOW_CHANGED", "test", false, false), false)) &&
                    !operation.Cancelled && operation.CompletedRounds == 2 && !operation.PendingWrite,
                    "COMMAND_SESSION_SELFTEST_ABORT_RESUME");
                var pending = operation.StartRequest(codes[2], codes[3]);
                Need(pending.TryClaimRoundTrip(), "COMMAND_SESSION_SELFTEST_PENDING_CLAIM");
                pending.BindCommand("help");
                var draft = pending.PrepareReply();
                Need(pending.TryConsume(draft) && pending.TryCommitMove() && pending.TryCommitWrite(), "COMMAND_SESSION_SELFTEST_PENDING_WRITE");
                Need(!operation.TryReleaseAbortedRequest(pending, new RoundTripTest.Outcome("uncertain")) &&
                    !operation.Cancelled, "COMMAND_SESSION_SELFTEST_ABORT_INPUT_ATTEMPTED");
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
