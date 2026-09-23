using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace RemoteMonitorMaster
{
    // Locally pre-authorized fixed-output experiment. A displayed M label is a start condition, not an authenticated command.
    internal static class RoundTripTest
    {
        // ReceiveProbe caps one cooperative capture phase at 15 s (RECEIVE_PHASE_TIME_LIMIT); it owns that literal.
        internal static readonly TimeSpan ReceivePhaseTimeLimit = TimeSpan.FromSeconds(15);
        // A send proof is by design at least two distinct bounded snapshots old: the reobservation's first capture
        // starts the clock and the sender's fresh snapshot follows, each allowed up to ReceivePhaseTimeLimit, plus the
        // candidate/identity comparisons between them. 2 x 15 s + 5 s keeps the age bound consistent with that cap
        // instead of expiring deterministically on a long conversation. The first capture's age is still preserved and
        // still checked at every site; only the bound changes.
        internal static readonly TimeSpan ProofAgeLimit =
            TimeSpan.FromTicks(2 * ReceivePhaseTimeLimit.Ticks) + TimeSpan.FromSeconds(5);

        internal sealed class Outcome
        {
            internal readonly string Message;
            internal readonly SupervisedSendTest.Outcome Send;
            private readonly bool cleanCompletion;
            internal bool CleanCompletion { get { return cleanCompletion; } }
            // The idle wait was handed back to the session before any request was observed: not a completion, not an
            // abort. Only an outcome without a send can be yielded.
            internal bool Yielded { get; }
            internal Outcome(string message, SupervisedSendTest.Outcome send = null, bool? clean = null, bool yielded = false)
            {
                Message = message; Send = send; cleanCompletion = clean ?? (send != null && send.CleanCompletion);
                Yielded = yielded && send == null && !cleanCompletion;
            }
        }

        public static string Run(IntPtr window, AuditLog log, string incomingMarker, SupervisedSendTest.Consent consent,
            Func<bool> stop, Action<string> progress)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RemoteMonitorMaster", "state", "roundtrip-seen-tokens.txt");
            return RunWithStatePath(window, log, incomingMarker, consent, stop, progress, path);
        }

        internal static string RunWithStatePath(IntPtr window, AuditLog log, string incomingMarker, SupervisedSendTest.Consent consent,
            Func<bool> stop, Action<string> progress, string statePath)
        {
            return ObserveWithStatePath(window, log, incomingMarker, consent, stop, progress, statePath, null).Message;
        }

        internal static Outcome ObserveWithStatePath(IntPtr window, AuditLog log, string incomingMarker, SupervisedSendTest.Consent consent,
            Func<bool> stop, Action<string> progress, string statePath, Action<string, ProbeSnapshot> inspectSnapshot,
            ReceiveProbe.Baseline baseline = null, bool continuousWait = false, OperationalTarget target = null,
            Func<bool> yieldRequested = null)
        {
            var owner = new object();
            var reserved = false;
            var sendStageEntered = false;
            var phase = "REQUEST";
            var preparedReplyCount = 0;
            var confirmedReplyCount = 0;
            var failedPart = 0;
            SupervisedSendTest.Outcome sent = null;
            try
            {
                Need(log != null && stop != null && progress != null, "ROUNDTRIP_REQUEST_INVALID");
                Need(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "PROBE_REQUIRES_MTA");
                Need(Protocol.IsDiagnosticMarker("MESSAGE", incomingMarker), "ROUNDTRIP_MARKER_INVALID");
                var replyMarker = "D" + incomingMarker.Substring(1); // Request-bound nonce; remote text never becomes output.
                Need(consent != null && consent.Marker == replyMarker, "ROUNDTRIP_CONFIRMATION_REQUIRED");
                Need(consent.TryClaimRoundTrip(), "ROUNDTRIP_CONSENT_USED");
                bool Stopped() { return consent.Cancelled || stop(); }
                void Alive() { Need(!Stopped(), "ROUNDTRIP_CANCELLED"); }
                Alive();
                log.Write("INFO", "ROUNDTRIP_BEGIN", AuditLog.Field("locally_preauthorized_fixed_reply", !consent.IsPcStatus),
                    AuditLog.Field("locally_preauthorized_pc_status", consent.IsPcStatus),
                    AuditLog.Field("locally_preauthorized_plain_commands", consent.IsPlainCommands),
                    AuditLog.Field("condition", "DISPLAYED_HISTORY_LABEL_OBSERVED_TWICE"),
                    AuditLog.Field("body_or_attachment_distinguished", false), AuditLog.Field("metadata_repeated", false),
                    AuditLog.Field("plain_body_verified", false), AuditLog.Field("automatic_send_allowed", false));
                phase = "RECEIVE";
                var received = ReceiveProbe.Observe(window, log, incomingMarker, Stopped, progress, owner, false,
                    inspectSnapshot, baseline, continuousWait, consent.IsPlainCommands, target: target, yieldRequested: yieldRequested);
                Alive();
                if (received.Status == "WAIT_YIELDED" && received.Proof == null && received.Single == null)
                {
                    // Nothing was observed as a request; the send stage is never entered and the consent retires below.
                    Result(log, received.Status, received.Reason, false, false, preparedReplyCount, confirmedReplyCount, failedPart);
                    return new Outcome(received.Message, null, false, true);
                }
                if (received.Proof == null)
                {
                    Result(log, received.Status, received.Reason, false, false, preparedReplyCount,
                        confirmedReplyCount, failedPart);
                    return new Outcome(received.Message);
                }
                Need(received.Status == "CANDIDATE_OBSERVED" && received.Reason == "NONE", "ROUNDTRIP_OBSERVATION_INVALID");
                Need(ReferenceEquals(received.Proof.Owner, owner) && received.Proof.Baseline != null &&
                    received.Proof.Baseline.PlainCommands == consent.IsPlainCommands, "ROUNDTRIP_PROOF_OWNER_MISMATCH");
                string observedCommand = null;
                if (consent.IsPlainCommands)
                {
                    Need(ReadOnlyCommands.TryMatchNode(received.Proof.Candidate, out observedCommand),
                        "ROUNDTRIP_COMMAND_INVALID");
                    consent.BindCommand(observedCommand);
                    log.Write("INFO", "COMMAND_RECOGNIZED", AuditLog.Field("command_id", observedCommand.ToUpperInvariant().Replace(' ', '_')),
                        AuditLog.Field("name_format", received.Proof.Candidate.CommandNameFormat),
                        AuditLog.Field("observed_name_length", received.Proof.Candidate.Identity.NameLength),
                        AuditLog.Field("body_authenticated", false));
                }
                SupervisedSendTest.Outcome SendPrepared(string payload, SupervisedSendTest.Consent partConsent, int partNumber, int partCount)
                {
                    Alive();
                    target?.PrepareSend(); // Bring back only the selected chat, before either fresh reply observation.
                    Alive();
                    var single = consent.IsPlainCommands &&
                        !(target == null && partNumber == 0 && received.Proof.Elapsed < TimeSpan.FromSeconds(5))
                        ? RefreshObservation(received.Proof, owner, window, incomingMarker, Stopped, progress, log, inspectSnapshot)
                        : null;
                    var sendProof = received.Proof;
                    sendStageEntered = true;
                    var outcome = SupervisedSendTest.RunBoundObserved(window, log, payload, new Point(), partConsent, Stopped, snapshot =>
                    {
                        Alive();
                        NativeMethods.WindowRectangle current;
                        Need(NativeMethods.GetWindowRect(window, out current) && current.Equals(sendProof.Bounds), "ROUNDTRIP_WINDOW_MOVED");
                        inspectSnapshot?.Invoke("HANDOFF", snapshot);
                        Alive();
                        if (single != null)
                            sendProof = CompleteRefreshedProof(received.Proof, single, snapshot, owner, window, current, Stopped, log);
                        AuthorizeHandoffCore(sendProof, owner, window, incomingMarker, snapshot, statePath, Stopped, !reserved, log);
                        reserved = true;
                        Alive();
                        Need(NativeMethods.GetWindowRect(window, out current) && current.Equals(sendProof.Bounds), "ROUNDTRIP_WINDOW_MOVED");
                        log.Write("INFO", "ROUNDTRIP_HANDOFF", AuditLog.Field("candidate_reobserved", true),
                            AuditLog.Field("diagnostic_token_reserved", reserved), AuditLog.Field("reply_part", partNumber),
                            AuditLog.Field("reply_parts", partCount), AuditLog.Field("progress_notice", partNumber == 0),
                            AuditLog.Field("handoff_is_repeat_observation", single != null),
                            AuditLog.Field("delivery_verified", false));
                    });
                    reserved |= sendProof.DiagnosticTokenReserved;
                    return outcome;
                }
                var watchdogCommand = consent.IsPlainCommands && WatchdogCommand.IsWatchdogCommand(observedCommand);
                var samplesPcStatus = SamplesPcStatus(consent.IsPcStatus, consent.IsPlainCommands, observedCommand);
                if (continuousWait && consent.IsPlainCommands && samplesPcStatus)
                {
                    phase = "NOTICE_BUSY";
                    progress("NOTICE_BUSY");
                    var notice = consent.CreateProgressNotice(observedCommand == "pwrsi" ?
                        SupervisedSendTest.PowerSiBusyNotice : SupervisedSendTest.StatusBusyNotice);
                    sent = SendPrepared(notice.NoticeText, notice, 0, 0);
                    if (!sent.CleanCompletion)
                        return new Outcome("Progress notice was not confirmed. No Slave query was started. " + sent.Message, sent, false);
                    log.Write("INFO", "MASTER_NOTICE_SENT", AuditLog.Field("stage", "BUSY"), AuditLog.Field("delivery_verified", false));
                }
                phase = "PREPARE_REPLY";
                Alive();
                if (samplesPcStatus)
                {
                    progress(consent.IsSlaveStatus ? "SLAVE_QUERYING" : "PC_STATUS_QUERYING");
                    if (consent.IsSlaveStatus) log.Write("INFO", "SLAVE_QUERY_BEGIN");
                    Alive();
                }
                var replies = consent.PrepareReplies(); // Status is sampled once; every immutable part is authorized separately below.
                preparedReplyCount = replies.Length;
                Alive();
                if (samplesPcStatus)
                    log.Write("INFO", "PC_STATUS_READY", AuditLog.Field("sampled_after_request", true),
                        AuditLog.Field("source", consent.IsSlaveStatus ? "SLAVE" : "MASTER"),
                        AuditLog.Field("payload_characters", replies.Sum(value => (long)value.Length)),
                        AuditLog.Field("prepared_replies", replies.Length));
                else if (watchdogCommand)
                    log.Write("INFO", "WATCHDOG_COMMAND_READY",
                        AuditLog.Field("payload_characters", replies.Sum(value => (long)value.Length)),
                        AuditLog.Field("prepared_replies", replies.Length));
                else if (consent.IsPlainCommands)
                    log.Write("INFO", "COMMAND_HELP_READY", AuditLog.Field("network_query", false),
                        AuditLog.Field("payload_characters", replies.Sum(value => (long)value.Length)),
                        AuditLog.Field("prepared_replies", replies.Length));
                phase = "SEND";
                progress("ROUNDTRIP_SENDING");
                Alive();
                var sendMessages = new List<string>();
                for (var index = 0; index < replies.Length; index++)
                {
                    Alive();
                    var partNumber = index + 1;
                    var partConsent = consent.IsPcStatus ? consent.GetPreparedPart(index) : consent;
                    progress("ROUNDTRIP_SENDING:" + partNumber + "/" + replies.Length);
                    Alive();
                    failedPart = partNumber;
                    sent = SendPrepared(replies[index], partConsent, partNumber, replies.Length);
                    sendMessages.Add("PART " + partNumber + "/" + replies.Length + Environment.NewLine + sent.Message);
                    if (!sent.CleanCompletion)
                    {
                        Result(log, "SEND_STAGE_FINISHED", "PART_UNCERTAIN_ABORTED", reserved, true,
                            preparedReplyCount, confirmedReplyCount, failedPart);
                        return new Outcome("ROUNDTRIP_SEND_STAGE_FINISHED - Reply sequence stopped after an uncertain part; delivery is NOT verified." +
                            Environment.NewLine + string.Join(Environment.NewLine, sendMessages), sent, false);
                    }
                    confirmedReplyCount++;
                    failedPart = 0;
                    if (consent.IsPcStatus) consent.RecordPreparedPartOutcome(index, sent);
                }
                var historySaved = !consent.IsPcStatus || consent.CommitPreparedOutput();
                if (!historySaved) log.Write("WARN", "OUTPUT_HISTORY_NOT_SAVED", AuditLog.Field("reason", consent.OutputHistoryFailure));
                // RunBound already supplies its action/result details. Never parse that text into a success or delivery claim.
                Result(log, "SEND_STAGE_FINISHED", "SEE_SUPERVISED_SEND_RESULT", reserved, true,
                    preparedReplyCount, confirmedReplyCount, failedPart);
                return new Outcome("ROUNDTRIP_SEND_STAGE_FINISHED - The approved reply stage has ended; delivery is NOT verified." +
                    (historySaved ? string.Empty : " Output history could not be saved; the next request may repeat content.") +
                    Environment.NewLine + string.Join(Environment.NewLine, sendMessages), sent, true);
            }
            catch (Exception ex)
            {
                var reason = (ex as MonitorException)?.ReasonCode ?? "ROUNDTRIP_FAILED";
                try
                {
                    if (log != null)
                    {
                        log.WriteException("ROUNDTRIP_FAILED", ex, AuditLog.Field("phase", phase));
                        Result(log, sendStageEntered ? "UNKNOWN" : "REJECTED", reason, reserved, sendStageEntered,
                            preparedReplyCount, confirmedReplyCount, failedPart);
                    }
                }
                catch { }
                // Carry the last send outcome so the caller can tell a pre-input rejection from an uncertain send.
                // A null Send after the send stage was entered stays unknown and must be treated as attempted.
                return new Outcome((sendStageEntered ? "UNKNOWN" : "REJECTED") + " - " + reason +
                    ". This one-run confirmation is consumed. Do not retry; inspect the result and collect the log." +
                    (sent == null ? "" : Environment.NewLine + sent.Message), sent, false);
            }
            finally
            {
                // Even no-match, cancellation or a rejected handoff consumes this local test. Durable reservations are never removed.
                if (consent != null) consent.Cancel();
            }
        }

        // Like help, a watchdog command (watchdog on|off [PID], help watchdog) is a plain command reply: no BUSY notice,
        // no PC_STATUS_READY sample; its one prepared part comes from Consent.PrepareReplies. pwrsi/total status sample.
        internal static bool SamplesPcStatus(bool pcStatus, bool plainCommands, string command)
        {
            return pcStatus && (!plainCommands || !(command.StartsWith("help", StringComparison.Ordinal) ||
                WatchdogCommand.IsWatchdogCommand(command)));
        }

        internal static void AuthorizeHandoff(ReceiveProbe.ObservationProof proof, object owner, IntPtr window,
            string marker, ProbeSnapshot fresh, string statePath, Func<bool> stop, AuditLog log = null)
        {
            AuthorizeHandoffCore(proof, owner, window, marker, fresh, statePath, stop, true, log);
        }

        private static void AuthorizeHandoffCore(ReceiveProbe.ObservationProof proof, object owner, IntPtr window,
            string marker, ProbeSnapshot fresh, string statePath, Func<bool> stop, bool reserveToken, AuditLog log = null)
        {
            Need(stop != null && !stop(), "ROUNDTRIP_CANCELLED");
            Need(proof != null && owner != null && ReferenceEquals(proof.Owner, owner), "ROUNDTRIP_PROOF_OWNER_MISMATCH");
            Need(proof.TryConsume(), "ROUNDTRIP_PROOF_USED");
            Need(window != IntPtr.Zero && proof.Window == window && proof.Elapsed < ProofAgeLimit,
                "ROUNDTRIP_PROOF_EXPIRED_OR_WINDOW_CHANGED");
            Need(proof.Baseline != null && proof.Previous != null && proof.Final != null &&
                proof.Baseline.Snapshot.Process.WindowHandle == window.ToInt64(), "ROUNDTRIP_PROOF_INCOMPLETE");
            var baseline = ReceiveProbe.CreateBaseline(proof.Baseline.Snapshot, marker,
                proof.Baseline.PlainCommands, proof.Baseline.ReadyRow);
            Need(baseline.MarkerHash == proof.Baseline.MarkerHash, "ROUNDTRIP_MARKER_MISMATCH");
            var previous = baseline.PlainCommands ? ReceiveProbe.ReobserveCandidate(proof, proof.Previous, log) :
                ReceiveProbe.Evaluate(baseline, proof.Previous, log);
            var final = baseline.PlainCommands ? ReceiveProbe.ReobserveCandidate(proof, proof.Final, log) :
                ReceiveProbe.Evaluate(baseline, proof.Final, log);
            if (baseline.PlainCommands)
            {
                ReceiveProbe.ValidatePlainHistoryPrefix(proof.Previous, proof.Final, log, baseline, acceptedRequest: true);
                ReceiveProbe.ValidatePlainHistoryPrefix(proof.Final, fresh, log, baseline, acceptedRequest: true);
            }
            Need(ReferenceEquals(previous, proof.PreviousCandidate) && ReferenceEquals(final, proof.Candidate) &&
                ReceiveProbe.SameCandidate(proof.Previous, previous, proof.Final, final), "ROUNDTRIP_REPEAT_NOT_PRESENT");
            var current = baseline.PlainCommands ? ReceiveProbe.ReobserveCandidate(proof, fresh, log) :
                ReceiveProbe.Evaluate(baseline, fresh, log);
            Need(ReceiveProbe.SameCandidate(proof.Final, final, fresh, current), "ROUNDTRIP_HANDOFF_CANDIDATE_CHANGED");
            Need(!stop(), "ROUNDTRIP_CANCELLED");
            if (reserveToken)
            {
                try
                {
                    // Domain separated from legacy command tokens. A successful reservation means no retry, not authentication.
                    var store = new TokenStore(statePath);
                    Need(store.TryReserve("roundtrip-diagnostic-v1:" + marker), "ROUNDTRIP_TOKEN_ALREADY_RESERVED");
                    proof.DiagnosticTokenReserved = true;
                }
                catch (MonitorException) { throw; }
                catch (Exception ex) { throw new MonitorException("ROUNDTRIP_STATE_FAILED", "Diagnostic reservation failed.", ex); }
            }
            else
            {
                try { Need(new TokenStore(statePath).Contains("roundtrip-diagnostic-v1:" + marker),
                    "ROUNDTRIP_TOKEN_NOT_RESERVED"); }
                catch (MonitorException) { throw; }
                catch (Exception ex) { throw new MonitorException("ROUNDTRIP_STATE_FAILED", "Diagnostic reservation check failed.", ex); }
            }
            Need(!stop(), "ROUNDTRIP_CANCELLED");
            Need(proof.Elapsed < ProofAgeLimit, "ROUNDTRIP_PROOF_EXPIRED_OR_WINDOW_CHANGED");
        }

        private static ReceiveProbe.SingleObservation RefreshObservation(ReceiveProbe.ObservationProof original, object owner,
            IntPtr window, string marker, Func<bool> stop, Action<string> progress, AuditLog log,
            Action<string, ProbeSnapshot> inspectSnapshot)
        {
            var observed = ReceiveProbe.Observe(window, log, marker, stop, ignored => progress("REVALIDATING"), owner, false,
                inspectSnapshot, original.Baseline, false, true, original, singleRefresh: true);
            Need(observed.Status == "CANDIDATE_REOBSERVED_ONCE" && observed.Reason == "NONE" &&
                observed.Proof == null && observed.Single != null,
                "ROUNDTRIP_REOBSERVATION_FAILED_" + observed.Reason);
            return observed.Single;
        }

        internal static ReceiveProbe.ObservationProof CompleteRefreshedProof(ReceiveProbe.ObservationProof original,
            ReceiveProbe.SingleObservation single, ProbeSnapshot fresh, object owner, IntPtr window,
            NativeMethods.WindowRectangle bounds, Func<bool> stop, AuditLog log = null)
        {
            Need(stop != null && !stop(), "ROUNDTRIP_CANCELLED");
            Need(original != null && single != null && owner != null && ReferenceEquals(single.Accepted, original) &&
                ReferenceEquals(original.Owner, owner) && ReferenceEquals(single.Owner, owner), "ROUNDTRIP_PROOF_OWNER_MISMATCH");
            Need(single.TryConsume(), "ROUNDTRIP_SINGLE_OBSERVATION_USED");
            Need(window != IntPtr.Zero && original.Window == window && single.Window == window &&
                original.Bounds.Equals(single.Bounds) && single.Bounds.Equals(bounds) && single.Age != null && single.Age.IsRunning &&
                single.Elapsed < ProofAgeLimit, "ROUNDTRIP_PROOF_EXPIRED_OR_WINDOW_CHANGED");
            Need(original.Baseline != null && original.Baseline.PlainCommands && single.Snapshot != null && fresh != null &&
                !ReferenceEquals(single.Snapshot, fresh) && !ReferenceEquals(single.Snapshot, original.Previous) &&
                !ReferenceEquals(single.Snapshot, original.Final) && !ReferenceEquals(fresh, original.Previous) &&
                !ReferenceEquals(fresh, original.Final), "ROUNDTRIP_FRESH_OBSERVATIONS_REQUIRED");
            var previous = ReceiveProbe.ReobserveCandidate(original, single.Snapshot, log);
            Need(ReferenceEquals(previous, single.Candidate), "ROUNDTRIP_REOBSERVATION_CHANGED");
            var final = ReceiveProbe.ReobserveCandidate(original, fresh, log);
            // Both snapshots are current; the accepted proof supplies identity only. Share the first capture's clock.
            var refreshed = new ReceiveProbe.ObservationProof(owner, window, bounds, original.Baseline,
                single.Snapshot, previous, fresh, final, single.AgeOffset, single.Age);
            SelectRefreshedProof(original, refreshed, owner, window);
            Need(!stop(), "ROUNDTRIP_CANCELLED");
            Need(refreshed.Elapsed < ProofAgeLimit, "ROUNDTRIP_PROOF_EXPIRED_OR_WINDOW_CHANGED");
            return refreshed;
        }

        internal static ReceiveProbe.ObservationProof SelectRefreshedProof(ReceiveProbe.ObservationProof original,
            ReceiveProbe.ObservationProof refreshed, object owner, IntPtr window)
        {
            Need(original != null && refreshed != null && owner != null && ReferenceEquals(original.Owner, owner) &&
                ReferenceEquals(refreshed.Owner, owner), "ROUNDTRIP_PROOF_OWNER_MISMATCH");
            Need(original.Window == window && refreshed.Window == window && original.Bounds.Equals(refreshed.Bounds) &&
                refreshed.Elapsed < ProofAgeLimit, "ROUNDTRIP_PROOF_EXPIRED_OR_WINDOW_CHANGED");
            Need(original.Baseline != null && original.Baseline.PlainCommands &&
                ReferenceEquals(original.Baseline, refreshed.Baseline), "ROUNDTRIP_PROOF_INCOMPLETE");
            ReceiveProbe.ValidatePlainHistoryPrefix(original.Final, refreshed.Previous, baseline: original.Baseline, acceptedRequest: true);
            ReceiveProbe.ValidatePlainHistoryPrefix(refreshed.Previous, refreshed.Final, baseline: original.Baseline, acceptedRequest: true);
            Need(ReceiveProbe.SameCandidate(original.Final, original.Candidate, refreshed.Previous, refreshed.PreviousCandidate) &&
                ReceiveProbe.SameCandidate(original.Final, original.Candidate, refreshed.Final, refreshed.Candidate),
                "ROUNDTRIP_REOBSERVATION_CHANGED");
            return refreshed;
        }

        private static void Result(AuditLog log, string status, string reason, bool reserved, bool sendStageEntered,
            int preparedReplyCount, int confirmedReplyCount, int failedPart)
        {
            log.Write("INFO", "ROUNDTRIP_RESULT", AuditLog.Field("status", status), AuditLog.Field("reason", reason),
                AuditLog.Field("diagnostic_token_reserved", reserved), AuditLog.Field("send_stage_entered", sendStageEntered),
                AuditLog.Field("reservation_flag_means_confirmed", true), AuditLog.Field("failed_reservation_may_persist", true),
                AuditLog.Field("prepared_replies", preparedReplyCount), AuditLog.Field("confirmed_replies", confirmedReplyCount),
                AuditLog.Field("failed_part", failedPart == 0 ? (object)"NONE" : failedPart),
                AuditLog.Field("plain_body_verified", false),
                AuditLog.Field("conversation_identity_verified", false), AuditLog.Field("delivery_verified", false),
                AuditLog.Field("automatic_send_allowed", false));
        }

        internal static void RunSelfTest(string directory)
        {
            // The send proof spans two bounded captures, each capped at ReceivePhaseTimeLimit by ReceiveProbe.
            Need(ProofAgeLimit >= TimeSpan.FromTicks(2 * ReceivePhaseTimeLimit.Ticks) &&
                ReceivePhaseTimeLimit == TimeSpan.FromSeconds(15), "ROUNDTRIP_PROOF_AGE_LIMIT_COVERS_TWO_PHASES");
            // A yield is only ever an untouched, unsent, non-clean outcome; every existing outcome stays non-yielded.
            var rejectedSend = new SupervisedSendTest.Outcome("REJECTED", "TARGET_PC_NOT_IDLE", "test", false, false);
            Need(!new Outcome("plain").Yielded && !new Outcome("aborted", rejectedSend, false).Yielded &&
                !new Outcome("clean", new SupervisedSendTest.Outcome("ACTION_RETURNED", "NONE", "test", true)).Yielded &&
                new Outcome("yielded", null, false, true).Yielded && !new Outcome("yielded", null, false, true).CleanCompletion &&
                !new Outcome("sent", rejectedSend, false, true).Yielded && !new Outcome("clean", null, true, true).Yielded,
                "ROUNDTRIP_SELF_TEST_YIELD_OUTCOME");
            // Watchdog commands share help's reply path: never a BUSY notice or PC status sample; pwrsi/total status still do.
            Need(new[] { "watchdog on", "watchdog off", "watchdog on 4321", "watchdog off 42", "help watchdog", "help", "help pwrsi" }
                    .All(command => !SamplesPcStatus(true, true, command)) &&
                SamplesPcStatus(true, true, "pwrsi") && SamplesPcStatus(true, true, "total status") &&
                SamplesPcStatus(true, false, null) && !SamplesPcStatus(false, false, null),
                "ROUNDTRIP_SELF_TEST_WATCHDOG_NOT_SAMPLED");
            const string marker = "M234567";
            var statePath = Path.Combine(directory, "roundtrip-selftest-tokens.txt");
            var blockedPath = Path.Combine(directory, "roundtrip-selftest-blocked-state");
            var cancelPath = Path.Combine(directory, "roundtrip-selftest-cancelled-tokens.txt");
            var callbackCount = 0;
            var owner = new object();
            var window = new IntPtr(100);
            ProbeSnapshot WithCandidate()
            {
                var snapshot = ReceiveProbe.CreateTestSnapshot();
                ReceiveProbe.SetTestName(snapshot.Nodes.Single(n => n.Node == 52), marker);
                return snapshot;
            }
            ReceiveProbe.ObservationProof Proof()
            {
                var baseline = ReceiveProbe.CreateBaseline(ReceiveProbe.CreateTestSnapshot(), marker);
                var previous = WithCandidate(); var final = WithCandidate();
                return new ReceiveProbe.ObservationProof(owner, window, new NativeMethods.WindowRectangle(), baseline,
                    previous, ReceiveProbe.Evaluate(baseline, previous), final, ReceiveProbe.Evaluate(baseline, final));
            }
            void Gate(ReceiveProbe.ObservationProof proof, object expectedOwner, ProbeSnapshot fresh, string path, Func<bool> stop)
            {
                AuthorizeHandoff(proof, expectedOwner, window, marker, fresh, path, stop);
                callbackCount++; // Pure continuation count only; no native operation or simulated delivery assertion.
            }
            void Reject(Action operation)
            {
                var before = callbackCount;
                try { operation(); }
                catch (MonitorException) { Need(callbackCount == before, "ROUNDTRIP_SELF_TEST_CALLBACK"); return; }
                throw new InvalidOperationException("Unsafe roundtrip handoff was accepted.");
            }
            try
            {
                Reject(() => Gate(null, owner, WithCandidate(), statePath, () => false));
                Reject(() => Gate(Proof(), owner, WithCandidate(), statePath, () => true));
                Reject(() => Gate(Proof(), new object(), WithCandidate(), statePath, () => false));
                Reject(() => Gate(Proof(), owner, ReceiveProbe.CreateTestSnapshot(), statePath, () => false));
                foreach (var mutate in new Action<ProbeSnapshot>[] {
                    s => s.RootNameFingerprint = "changed", s => s.Complete = false,
                    s => s.LayoutRejection = "LAYOUT_VISIBLE_LIST", s => s.Nodes.Single(n => n.Node == 2).NativeHwnd++,
                    s => ReceiveProbe.SetTestName(s.Nodes.Single(n => n.Node == 52), marker, "replaced-runtime"),
                    s => ReceiveProbe.SetTestName(s.Nodes.Single(n => n.Node == 5), "private-draft") })
                {
                    var fresh = WithCandidate(); mutate(fresh);
                    Reject(() => Gate(Proof(), owner, fresh, statePath, () => false));
                }
                Directory.CreateDirectory(blockedPath);
                Reject(() => Gate(Proof(), owner, WithCandidate(), blockedPath, () => false));
                Need(callbackCount == 0, "ROUNDTRIP_SELF_TEST_NEGATIVES");
                var cancelledProof = Proof();
                var stopChecks = 0;
                Reject(() => Gate(cancelledProof, owner, WithCandidate(), cancelPath, () => ++stopChecks >= 3));
                Need(cancelledProof.DiagnosticTokenReserved && File.ReadAllLines(cancelPath).Length == 1 && callbackCount == 0,
                    "ROUNDTRIP_SELF_TEST_RESERVATION_SURVIVES_STOP");
                Reject(() => Gate(Proof(), owner, WithCandidate(), cancelPath, () => false));
                var proof = Proof();
                Gate(proof, owner, WithCandidate(), statePath, () => false);
                Need(callbackCount == 1, "ROUNDTRIP_SELF_TEST_ONCE");
                Reject(() => Gate(proof, owner, WithCandidate(), statePath, () => false));
                Reject(() => Gate(Proof(), owner, WithCandidate(), statePath, () => false));
                Need(callbackCount == 1 && File.ReadAllLines(statePath).Length == 1 && !File.ReadAllText(statePath).Contains(marker),
                    "ROUNDTRIP_SELF_TEST_DURABLE_REDACTED");
                var consent = new SupervisedSendTest.Consent("D234567", true);
                Need(consent.TryClaimRoundTrip() && !consent.TryClaimRoundTrip() && consent.TryConsume("D234567"),
                    "ROUNDTRIP_SELF_TEST_CLAIM");
                consent.Cancel();
                Need(!consent.TryCommitMove() && !consent.TryClaimRoundTrip(), "ROUNDTRIP_SELF_TEST_CANCEL");
                RunPlainHandoffSelfTest(directory);
                RunOperationalHandoffSelfTest(directory);
            }
            finally
            {
                try { if (File.Exists(statePath)) File.Delete(statePath); } catch { }
                try { if (File.Exists(cancelPath)) File.Delete(cancelPath); } catch { }
                try { if (Directory.Exists(blockedPath)) Directory.Delete(blockedPath); } catch { }
            }
        }

        private static void RunOperationalHandoffSelfTest(string directory)
        {
            const string marker = "M345678";
            var path = Path.Combine(directory, "roundtrip-operational-tokens.txt");
            var owner = new object();
            var initial = ReceiveProbe.CreateBaseline(ReceiveProbe.CreateTestSnapshot(), marker, true);
            var window = new IntPtr(initial.Snapshot.Process.WindowHandle);
            ProbeSnapshot History(int extras, bool candidateVisible = true, bool candidateEnabled = true)
            {
                var snapshot = ReceiveProbe.CreateTestSnapshot();
                ReceiveProbe.AppendTestHistoryText(snapshot, SupervisedSendTest.ReadyText("D345678"));
                var candidate = ReceiveProbe.AppendTestHistoryText(snapshot, "pwrsi");
                candidate.Visible = candidateVisible;
                candidate.Enabled = candidateEnabled;
                for (var i = 0; i < extras; i++) ReceiveProbe.AppendTestHistoryText(snapshot, i % 2 == 0 ? "help" : "general message");
                return snapshot;
            }
            var baseline = ReceiveProbe.BindReadyBoundary(initial, History(0));
            ReceiveProbe.ObservationProof Accepted()
            {
                var previous = History(0); var final = History(0);
                return new ReceiveProbe.ObservationProof(owner, window, new NativeMethods.WindowRectangle(), baseline,
                    previous, ReceiveProbe.Evaluate(baseline, previous), final, ReceiveProbe.Evaluate(baseline, final));
            }
            ReceiveProbe.ObservationProof Proof(int extras, TimeSpan? age = null, bool candidateVisible = true,
                bool candidateEnabled = true)
            {
                var previous = History(extras, candidateVisible, candidateEnabled);
                var final = History(extras, candidateVisible, candidateEnabled);
                var accepted = candidateVisible ? null : Accepted();
                return new ReceiveProbe.ObservationProof(owner, window, new NativeMethods.WindowRectangle(), baseline,
                    previous, candidateVisible ? ReceiveProbe.Evaluate(baseline, previous) :
                        ReceiveProbe.ReobserveCandidate(accepted, previous),
                    final, candidateVisible ? ReceiveProbe.Evaluate(baseline, final) :
                        ReceiveProbe.ReobserveCandidate(accepted, final), age);
            }
            ReceiveProbe.SingleObservation Single(ReceiveProbe.ObservationProof accepted, int extras, TimeSpan? age = null)
            {
                var first = History(extras, candidateVisible: false);
                return new ReceiveProbe.SingleObservation(accepted, owner, window, accepted.Bounds, first,
                    ReceiveProbe.ReobserveCandidate(accepted, first), System.Diagnostics.Stopwatch.StartNew(), age);
            }
            ReceiveProbe.ObservationProof Complete(ReceiveProbe.ObservationProof accepted, ReceiveProbe.SingleObservation single,
                ProbeSnapshot second, Func<bool> stop = null)
            {
                return CompleteRefreshedProof(accepted, single, second, owner, window, accepted.Bounds, stop ?? (() => false));
            }
            void Reject(Action action)
            {
                try { action(); } catch (MonitorException) { return; }
                throw new InvalidOperationException("Unsafe operating handoff accepted.");
            }
            try
            {
                Reject(() => AuthorizeHandoffCore(Proof(0), owner, window, marker, History(1), path, () => false, false));
                Reject(() => AuthorizeHandoffCore(Proof(0, ProofAgeLimit), owner, window, marker,
                    History(1), path, () => false, true));
                var accepted = Accepted();
                Reject(() => Complete(accepted, Single(accepted, 1, ProofAgeLimit), History(2, false)));
                var same = Single(accepted, 1);
                Reject(() => Complete(accepted, same, same.Snapshot));
                Reject(() => Complete(accepted, new ReceiveProbe.SingleObservation(accepted, owner, window, accepted.Bounds,
                    accepted.Final, accepted.Candidate, System.Diagnostics.Stopwatch.StartNew()), History(2, false)));
                Reject(() => Complete(accepted, Single(accepted, 1), accepted.Final));
                Reject(() => Complete(accepted, Single(accepted, 1), History(2, false), () => true));
                var stopChecks = 0;
                Reject(() => Complete(accepted, Single(accepted, 1), History(2, false), () => ++stopChecks >= 2));
                Reject(() => CompleteRefreshedProof(accepted, Single(accepted, 1), History(2, false), new object(),
                    window, accepted.Bounds, () => false));
                Reject(() => CompleteRefreshedProof(accepted, Single(accepted, 1), History(2, false), owner,
                    new IntPtr(window.ToInt64() + 1), accepted.Bounds, () => false));
                Reject(() => CompleteRefreshedProof(accepted, Single(accepted, 1), History(2, false), owner,
                    window, new NativeMethods.WindowRectangle { Left = 1 }, () => false));
                Reject(() => Complete(Accepted(), Single(accepted, 1), History(2, false)));
                foreach (var mutate in new Action<ProbeSnapshot>[] {
                    s => s.Complete = false, s => s.RootNameFingerprint = "changed", s => s.Process = null,
                    s => s.Nodes.Single(n => n.Node == 2).NativeHwnd++,
                    s => s.Nodes.Single(n => n.PlainCommand == "pwrsi").Enabled = false,
                    s => ReceiveProbe.SetTestName(s.Nodes.Single(n => n.PlainCommand == "pwrsi"), "help"),
                    s => ReceiveProbe.SetTestName(s.Nodes.Single(n => n.PlainCommand == "pwrsi"), "pwrsi", "replaced-runtime"),
                    s => s.Nodes.Remove(s.Nodes.Single(n => n.PlainCommand == "pwrsi")),
                    s => ReceiveProbe.SetTestName(s.Nodes.Single(n => n.Node ==
                        s.Nodes.Single(text => text.ReadyNoticeHash == TokenStore.Hash(SupervisedSendTest.ReadyText("D345678"))).Parent),
                        "", "recreated-ready-row") })
                {
                    var first = Single(accepted, 1);
                    mutate(first.Snapshot);
                    Reject(() => Complete(accepted, first, History(2, false)));
                    var second = History(2, false);
                    mutate(second);
                    Reject(() => Complete(accepted, Single(accepted, 1), second));
                }
                var agedFirst = Single(accepted, 1, TimeSpan.FromSeconds(12));
                var agedProof = Complete(accepted, agedFirst, History(2, false));
                Need(ReferenceEquals(agedProof.Age, agedFirst.Age) && agedProof.Elapsed >= TimeSpan.FromSeconds(12),
                    "OPERATING_REFRESH_PRESERVES_FIRST_CAPTURE_AGE");
                Need(!File.Exists(path), "OPERATING_EXPIRED_NOTICE_NO_RESERVATION");
                var busyProof = Proof(0);
                AuthorizeHandoffCore(busyProof, owner, window, marker, History(2), path, () => false, true);
                Need(busyProof.DiagnosticTokenReserved, "OPERATING_BUSY_RESERVED_ONCE");
                Reject(() => AuthorizeHandoffCore(busyProof, owner, window, marker, History(2), path, () => false, true));
                for (var part = 1; part <= 10; part++)
                {
                    var first = Single(busyProof, part + 2);
                    var second = History(part + 3, candidateVisible: false);
                    // The first six parts succeed; later snapshots refresh historical Ready display evidence.
                    if (part >= 7)
                    {
                        foreach (var snapshot in new[] { first.Snapshot, second })
                        {
                            var ready = snapshot.Nodes.Single(n => n.ReadyNoticeHash ==
                                TokenStore.Hash(SupervisedSendTest.ReadyText("D345678")));
                            if (part == 7) ReceiveProbe.SetTestName(ready, "refreshed Ready display");
                            else if (part == 8) ReceiveProbe.SetTestName(ready, "");
                            else if (part == 9) ready.ReadyNoticeHash = ready.ReadyNoticeSourceHash = null;
                            else ReceiveProbe.AppendTestHistorySiblingText(snapshot, ready, "display metadata");
                            Reject(() => ReceiveProbe.Evaluate(baseline, snapshot)); // New admission stays strict.
                        }
                    }
                    var refreshed = Complete(busyProof, first, second);
                    Need(ReferenceEquals(refreshed.Previous, first.Snapshot) && ReferenceEquals(refreshed.Final, second) &&
                        !ReferenceEquals(refreshed.Previous, refreshed.Final), "OPERATING_TWO_DISTINCT_FRESH_OBSERVATIONS");
                    Reject(() => Complete(busyProof, first, History(part + 3, false)));
                    AuthorizeHandoffCore(refreshed, owner, window, marker, second, path, () => false, false);
                    Reject(() => AuthorizeHandoffCore(refreshed, owner, window, marker, second, path, () => false, false));
                }
                Need(File.ReadAllLines(path).Length == 1, "OPERATING_TEN_PARTS_READY_REFRESH_REUSE_RESERVATION");
                // Two long but legal captures (16 s together) still authorize; only an age past the bound expires.
                var longFirst = Single(busyProof, 13, TimeSpan.FromSeconds(16));
                var longSecond = History(14, candidateVisible: false);
                var longProof = Complete(busyProof, longFirst, longSecond);
                Need(longProof.Elapsed >= TimeSpan.FromSeconds(16) && longProof.Elapsed < ProofAgeLimit,
                    "OPERATING_TWO_LONG_CAPTURES_ACCEPTED");
                AuthorizeHandoffCore(longProof, owner, window, marker, longSecond, path, () => false, false);
                Reject(() => Complete(busyProof, Single(busyProof, 13, ProofAgeLimit), History(14, candidateVisible: false)));
                Reject(() => AuthorizeHandoffCore(Proof(3, candidateEnabled: false), owner, window, marker,
                    History(4, candidateEnabled: false), path, () => false, false));
                var changed = Proof(3);
                ReceiveProbe.SetTestName(changed.Candidate, "help");
                Reject(() => SelectRefreshedProof(busyProof, changed, owner, window));
                var otherBoundary = ReceiveProbe.CreateBaseline(baseline.Snapshot, marker, true);
                var other = Proof(0);
                Reject(() => SelectRefreshedProof(busyProof, new ReceiveProbe.ObservationProof(owner, window,
                    other.Bounds, otherBoundary, other.Previous, other.PreviousCandidate, other.Final, other.Candidate), owner, window));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        private static void RunPlainHandoffSelfTest(string directory)
        {
            const string marker = "M567892";
            var path = Path.Combine(directory, "roundtrip-plain-selftest-tokens.txt");
            var owner = new object();
            ProbeSnapshot History(bool append)
            {
                var snapshot = ReceiveProbe.CreateTestSnapshot();
                ReceiveProbe.SetTestName(snapshot.Nodes.Single(n => n.Node == 52), "help ");
                if (append) ReceiveProbe.AppendTestHistoryText(snapshot, "help ");
                return snapshot;
            }
            var window = new IntPtr(History(false).Process.WindowHandle);
            ReceiveProbe.ObservationProof Proof(ReceiveProbe.Baseline supplied = null, TimeSpan? ageOffset = null,
                string candidateRuntime = null)
            {
                var baseline = supplied ?? ReceiveProbe.CreateBaseline(History(false), marker, true);
                var before = History(true); var after = History(true);
                if (candidateRuntime != null)
                {
                    ReceiveProbe.SetTestName(ReceiveProbe.SelectHistory(before).Texts.Last(), "help ", candidateRuntime);
                    ReceiveProbe.SetTestName(ReceiveProbe.SelectHistory(after).Texts.Last(), "help ", candidateRuntime);
                }
                return new ReceiveProbe.ObservationProof(owner, window, new NativeMethods.WindowRectangle(), baseline,
                    before, ReceiveProbe.Evaluate(baseline, before), after, ReceiveProbe.Evaluate(baseline, after), ageOffset);
            }
            void Reject(Action action)
            {
                try { action(); } catch (MonitorException) { return; }
                throw new InvalidOperationException("Unsafe plain-command handoff was accepted.");
            }
            try
            {
                Reject(() => AuthorizeHandoff(Proof(), owner, window, marker, History(false), path, () => false));
                var duplicate = History(true); ReceiveProbe.AppendTestHistoryText(duplicate, "pwrsi");
                Reject(() => AuthorizeHandoff(Proof(), owner, window, marker, duplicate, path, () => false));
                var replaced = History(true);
                ReceiveProbe.SetTestName(replaced.Nodes.Single(n => n.Node == 52), "changed");
                Reject(() => AuthorizeHandoff(Proof(), owner, window, marker, replaced, path, () => false));
                var trimmedAtHandoff = History(true);
                ReceiveProbe.SetTestName(ReceiveProbe.SelectHistory(trimmedAtHandoff).Texts.Last(), "help");
                Reject(() => AuthorizeHandoff(Proof(), owner, window, marker, trimmedAtHandoff, path, () => false));
                Reject(() => AuthorizeHandoff(Proof(), owner, window, marker, History(true), path, () => true));
                Reject(() => AuthorizeHandoff(Proof(ageOffset: ProofAgeLimit), owner, window, marker,
                    History(true), path, () => false));
                // The accepted request may be old; only the proof that authorizes the send must be inside the bound.
                var agedOriginal = Proof(ageOffset: TimeSpan.FromSeconds(16));
                var refreshed = Proof(agedOriginal.Baseline);
                Need(ReferenceEquals(SelectRefreshedProof(agedOriginal, refreshed, owner, window), refreshed),
                    "COMMAND_HANDOFF_SELFTEST_REFRESHED_PROOF");
                Reject(() => SelectRefreshedProof(agedOriginal,
                    Proof(agedOriginal.Baseline, candidateRuntime: "changed-command-runtime"), owner, window));
                Need(!File.Exists(path), "COMMAND_HANDOFF_SELFTEST_NO_EARLY_RESERVATION");
                var proof = Proof();
                AuthorizeHandoff(proof, owner, window, marker, History(true), path, () => false);
                Need(proof.DiagnosticTokenReserved && File.ReadAllLines(path).Length == 1,
                    "COMMAND_HANDOFF_SELFTEST_RESERVED");
                Reject(() => AuthorizeHandoff(proof, owner, window, marker, History(true), path, () => false));
                Reject(() => AuthorizeHandoff(Proof(), owner, window, marker, History(true), path, () => false));
                RunClockHandoffSelfTest(directory);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        private static void RunClockHandoffSelfTest(string directory)
        {
            const string marker = "M678924";
            var path = Path.Combine(directory, "roundtrip-clock-selftest-tokens.txt");
            var owner = new object();
            // Real message shape: history > row > wrapper > primary Text, optional clock sibling.
            // Node ordinals may move, but runtime identities and ancestry remain the same.
            ProbeSnapshot History(string[] clocks, bool append, int ordinalOffset = 0)
            {
                var snapshot = ReceiveProbe.CreateTestSnapshot();
                ProbeNode Node(int number, int parent, string type, string runtime, string text)
                {
                    var node = new ProbeNode { Node = number, Parent = parent, Document = 2, Enabled = true, Visible = true,
                        Identity = new ElementIdentity(runtime, snapshot.Process.ProcessId, "", "ControlType." + type,
                            "", "fixture", "", 0, TokenStore.Hash(""), "<redacted>") };
                    ReceiveProbe.SetTestName(node, text);
                    return node;
                }
                var primary = snapshot.Nodes.Single(n => n.Node == 52);
                ReceiveProbe.SetTestName(primary, "help");
                primary.Parent = 60;
                snapshot.Nodes.Insert(snapshot.Nodes.IndexOf(primary), Node(60, 51, "Custom", "old-wrapper", ""));
                for (var i = 0; i < clocks.Length; i++)
                    snapshot.Nodes.Insert(snapshot.Nodes.IndexOf(primary) + 1 + i,
                        Node(61 + i, 60, "Text", "old-clock-" + i, clocks[i]));
                if (append)
                {
                    snapshot.Nodes.Add(Node(70, 50, "Custom", "command-row", ""));
                    snapshot.Nodes.Add(Node(71, 70, "Custom", "command-wrapper", ""));
                    snapshot.Nodes.Add(Node(72, 71, "Text", "command-text", "help"));
                }
                foreach (var node in snapshot.Nodes)
                {
                    if (node.Node > 1) node.Node += ordinalOffset;
                    if (node.Parent > 1) node.Parent += ordinalOffset;
                    if (node.Document > 0) node.Document += ordinalOffset;
                }
                return snapshot;
            }
            var window = new IntPtr(History(new[] { "14:26" }, false).Process.WindowHandle);
            var baseline = ReceiveProbe.CreateBaseline(History(new[] { "14:26" }, false), marker, true);
            ReceiveProbe.ObservationProof Proof()
            {
                var before = History(new[] { "14:26" }, true);
                var final = History(new string[0], true, 100);
                var beforeCandidate = ReceiveProbe.Evaluate(baseline, before);
                var finalCandidate = ReceiveProbe.Evaluate(baseline, final);
                Need(beforeCandidate.Node != finalCandidate.Node &&
                    ReceiveProbe.SameCandidate(before, beforeCandidate, final, finalCandidate),
                    "COMMAND_CLOCK_HANDOFF_SELFTEST_STABLE_CANDIDATE");
                return new ReceiveProbe.ObservationProof(owner, window, new NativeMethods.WindowRectangle(), baseline,
                    before, beforeCandidate, final, finalCandidate);
            }
            void Reject(Action action, string expectedReason)
            {
                try { action(); }
                catch (MonitorException ex) { Need(ex.ReasonCode == expectedReason, "COMMAND_CLOCK_HANDOFF_SELFTEST_REASON"); return; }
                throw new InvalidOperationException("Unsafe clock handoff was accepted.");
            }
            using (var audit = new AuditLog(directory))
            {
                try
                {
                    Reject(() => ReceiveProbe.Evaluate(baseline, History(new[] { "14:26", "14:27" }, false), audit),
                        "RECEIVE_HISTORY_CLOCK_AMBIGUOUS");
                    audit.ReleaseFile(); // Read the finished records using the production attachment-close path.
                    var ambiguous = File.ReadAllLines(audit.FilePath).Single(line => line.Contains("code=\"RECEIVE_HISTORY_REJECTED\""));
                    Need(ambiguous.Contains("comparison=\"AFTER_CLOCKS\"") && ambiguous.Contains("row_index=\"0\"") &&
                        ambiguous.Contains("text_index=\"2\"") && ambiguous.Contains("after_shape=\"TIME_LIKE\"") &&
                        ambiguous.Contains("clock_counts_complete=\"False\""), "COMMAND_CLOCK_HANDOFF_SELFTEST_AMBIGUITY_LOG");
                    var unverified = History(new[] { "14:27" }, false);
                    unverified.Nodes.Single(n => n.Node == 61).NameShape = "UNAVAILABLE";
                    Reject(() => ReceiveProbe.Evaluate(baseline, unverified, audit), "RECEIVE_HISTORY_CONTENT_CHANGED");
                    var unavailable = File.ReadAllLines(audit.FilePath).Last();
                    Need(unavailable.Contains("comparison=\"CONTENT\"") && unavailable.Contains("text_index=\"1\"") &&
                        unavailable.Contains("before_shape=\"MISSING\"") && unavailable.Contains("after_shape=\"UNAVAILABLE\""),
                        "COMMAND_CLOCK_HANDOFF_SELFTEST_SHAPE_LOG");
                    var changedBody = History(new[] { "14:26" }, false);
                    ReceiveProbe.SetTestName(changedBody.Nodes.Single(n => n.Node == 52), "private body changed");
                    Reject(() => ReceiveProbe.Evaluate(baseline, changedBody, audit), "RECEIVE_HISTORY_CONTENT_CHANGED");
                    var body = File.ReadAllLines(audit.FilePath).Last();
                    Need(body.Contains("text_index=\"0\"") && body.Contains("path_equal=\"True\"") &&
                        body.Contains("name_equal=\"False\"") && body.Contains("identity_equal=\"True\""),
                        "COMMAND_CLOCK_HANDOFF_SELFTEST_CONTENT_LOG");
                    Need(!File.ReadAllText(audit.FilePath).Contains("private body changed") &&
                        !File.ReadAllText(audit.FilePath).Contains(TokenStore.Hash("private body changed")),
                        "COMMAND_CLOCK_HANDOFF_SELFTEST_REDACTED");
                    Need(!File.Exists(path), "COMMAND_CLOCK_HANDOFF_SELFTEST_NO_EARLY_RESERVATION");
                    var proof = Proof();
                    var fresh = History(new[] { "14:28" }, true, 200);
                    AuthorizeHandoff(proof, owner, window, marker, fresh, path, () => false, audit);
                    Need(proof.DiagnosticTokenReserved && File.ReadAllLines(path).Length == 1,
                        "COMMAND_CLOCK_HANDOFF_SELFTEST_RESERVED_ONCE");
                    Reject(() => AuthorizeHandoff(proof, owner, window, marker, fresh, path, () => false), "ROUNDTRIP_PROOF_USED");
                    Reject(() => AuthorizeHandoff(Proof(), owner, window, marker, fresh, path, () => false), "ROUNDTRIP_TOKEN_ALREADY_RESERVED");
                    Need(File.ReadAllLines(path).Length == 1, "COMMAND_CLOCK_HANDOFF_SELFTEST_NO_RETRY");
                }
                finally
                {
                    audit.Dispose();
                    if (File.Exists(audit.FilePath)) File.Delete(audit.FilePath);
                    if (File.Exists(path)) File.Delete(path);
                }
            }
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new MonitorException(reason, "Fixed-output roundtrip test rejected: " + reason + ".");
        }
    }
}
