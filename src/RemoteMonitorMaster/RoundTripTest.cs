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
        internal sealed class Outcome
        {
            internal readonly string Message;
            internal readonly SupervisedSendTest.Outcome Send;
            private readonly bool cleanCompletion;
            internal bool CleanCompletion { get { return cleanCompletion; } }
            internal Outcome(string message, SupervisedSendTest.Outcome send = null, bool? clean = null)
            { Message = message; Send = send; cleanCompletion = clean ?? (send != null && send.CleanCompletion); }
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
            ReceiveProbe.Baseline baseline = null, bool continuousWait = false)
        {
            var owner = new object();
            var reserved = false;
            var sendStageEntered = false;
            var phase = "REQUEST";
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
                    inspectSnapshot, baseline, continuousWait, consent.IsPlainCommands);
                Alive();
                if (received.Proof == null)
                {
                    Result(log, received.Status, received.Reason, false, false);
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
                    var sendProof = consent.IsPlainCommands &&
                        !(partNumber == 0 && received.Proof.Elapsed < TimeSpan.FromSeconds(5))
                        ? RefreshProof(received.Proof, owner, window, incomingMarker, Stopped, progress, log, inspectSnapshot)
                        : received.Proof;
                    sendStageEntered = true;
                    var outcome = SupervisedSendTest.RunBoundObserved(window, log, payload, new Point(), partConsent, Stopped, snapshot =>
                    {
                        Alive();
                        NativeMethods.WindowRectangle current;
                        Need(NativeMethods.GetWindowRect(window, out current) && current.Equals(sendProof.Bounds), "ROUNDTRIP_WINDOW_MOVED");
                        inspectSnapshot?.Invoke("HANDOFF", snapshot);
                        Alive();
                        AuthorizeHandoffCore(sendProof, owner, window, incomingMarker, snapshot, statePath, Stopped, !reserved, log);
                        reserved = true;
                        Alive();
                        Need(NativeMethods.GetWindowRect(window, out current) && current.Equals(sendProof.Bounds), "ROUNDTRIP_WINDOW_MOVED");
                        log.Write("INFO", "ROUNDTRIP_HANDOFF", AuditLog.Field("candidate_reobserved", true),
                            AuditLog.Field("diagnostic_token_reserved", reserved), AuditLog.Field("reply_part", partNumber),
                            AuditLog.Field("reply_parts", partCount), AuditLog.Field("progress_notice", partNumber == 0),
                            AuditLog.Field("delivery_verified", false));
                    });
                    reserved |= sendProof.DiagnosticTokenReserved;
                    return outcome;
                }
                var samplesPcStatus = consent.IsPcStatus && (!consent.IsPlainCommands ||
                    !observedCommand.StartsWith("help", StringComparison.Ordinal));
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
                Alive();
                if (samplesPcStatus)
                    log.Write("INFO", "PC_STATUS_READY", AuditLog.Field("sampled_after_request", true),
                        AuditLog.Field("source", consent.IsSlaveStatus ? "SLAVE" : "MASTER"),
                        AuditLog.Field("payload_characters", replies.Sum(value => value.Length)),
                        AuditLog.Field("prepared_replies", replies.Length));
                else if (consent.IsPlainCommands)
                    log.Write("INFO", "COMMAND_HELP_READY", AuditLog.Field("network_query", false),
                        AuditLog.Field("payload_characters", replies.Sum(value => value.Length)),
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
                    sent = SendPrepared(replies[index], partConsent, partNumber, replies.Length);
                    sendMessages.Add("PART " + partNumber + "/" + replies.Length + Environment.NewLine + sent.Message);
                    if (!sent.CleanCompletion)
                    {
                        Result(log, "SEND_STAGE_FINISHED", "PART_UNCERTAIN_ABORTED", reserved, true, replies.Length);
                        return new Outcome("ROUNDTRIP_SEND_STAGE_FINISHED - Reply sequence stopped after an uncertain part; delivery is NOT verified." +
                            Environment.NewLine + string.Join(Environment.NewLine, sendMessages), sent, false);
                    }
                }
                // RunBound already supplies its action/result details. Never parse that text into a success or delivery claim.
                Result(log, "SEND_STAGE_FINISHED", "SEE_SUPERVISED_SEND_RESULT", reserved, true, replies.Length);
                return new Outcome("ROUNDTRIP_SEND_STAGE_FINISHED - The approved reply stage has ended; delivery is NOT verified." +
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
                        Result(log, sendStageEntered ? "UNKNOWN" : "REJECTED", reason, reserved, sendStageEntered);
                    }
                }
                catch { }
                return new Outcome((sendStageEntered ? "UNKNOWN" : "REJECTED") + " - " + reason +
                    ". This one-run confirmation is consumed. Do not retry; inspect the result and collect the log." +
                    (sent == null ? "" : Environment.NewLine + sent.Message));
            }
            finally
            {
                // Even no-match, cancellation or a rejected handoff consumes this local test. Durable reservations are never removed.
                if (consent != null) consent.Cancel();
            }
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
            Need(window != IntPtr.Zero && proof.Window == window && proof.Elapsed < TimeSpan.FromSeconds(15),
                "ROUNDTRIP_PROOF_EXPIRED_OR_WINDOW_CHANGED");
            Need(proof.Baseline != null && proof.Previous != null && proof.Final != null &&
                proof.Baseline.Snapshot.Process.WindowHandle == window.ToInt64(), "ROUNDTRIP_PROOF_INCOMPLETE");
            var baseline = ReceiveProbe.CreateBaseline(proof.Baseline.Snapshot, marker,
                proof.Baseline.PlainCommands, proof.Baseline.ReadyRow);
            Need(baseline.MarkerHash == proof.Baseline.MarkerHash, "ROUNDTRIP_MARKER_MISMATCH");
            var previous = ReceiveProbe.Evaluate(baseline, proof.Previous, log);
            var final = ReceiveProbe.Evaluate(baseline, proof.Final, log);
            if (baseline.PlainCommands)
            {
                ReceiveProbe.ValidatePlainHistoryPrefix(proof.Previous, proof.Final, log, baseline);
                ReceiveProbe.ValidatePlainHistoryPrefix(proof.Final, fresh, log, baseline);
            }
            Need(ReferenceEquals(previous, proof.PreviousCandidate) && ReferenceEquals(final, proof.Candidate) &&
                ReceiveProbe.SameCandidate(proof.Previous, previous, proof.Final, final), "ROUNDTRIP_REPEAT_NOT_PRESENT");
            var current = ReceiveProbe.Evaluate(baseline, fresh, log);
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
            Need(proof.Elapsed < TimeSpan.FromSeconds(15), "ROUNDTRIP_PROOF_EXPIRED_OR_WINDOW_CHANGED");
        }

        private static ReceiveProbe.ObservationProof RefreshProof(ReceiveProbe.ObservationProof original, object owner,
            IntPtr window, string marker, Func<bool> stop, Action<string> progress, AuditLog log,
            Action<string, ProbeSnapshot> inspectSnapshot)
        {
            var observed = ReceiveProbe.Observe(window, log, marker, stop, ignored => progress("REVALIDATING"), owner, false,
                inspectSnapshot, original.Baseline, false, true);
            Need(observed.Status == "CANDIDATE_OBSERVED" && observed.Reason == "NONE" && observed.Proof != null,
                "ROUNDTRIP_REOBSERVATION_FAILED");
            return SelectRefreshedProof(original, observed.Proof, owner, window);
        }

        internal static ReceiveProbe.ObservationProof SelectRefreshedProof(ReceiveProbe.ObservationProof original,
            ReceiveProbe.ObservationProof refreshed, object owner, IntPtr window)
        {
            Need(original != null && refreshed != null && owner != null && ReferenceEquals(original.Owner, owner) &&
                ReferenceEquals(refreshed.Owner, owner), "ROUNDTRIP_PROOF_OWNER_MISMATCH");
            Need(original.Window == window && refreshed.Window == window && original.Bounds.Equals(refreshed.Bounds) &&
                refreshed.Elapsed < TimeSpan.FromSeconds(15), "ROUNDTRIP_PROOF_EXPIRED_OR_WINDOW_CHANGED");
            Need(original.Baseline != null && original.Baseline.PlainCommands &&
                ReferenceEquals(original.Baseline, refreshed.Baseline), "ROUNDTRIP_PROOF_INCOMPLETE");
            ReceiveProbe.ValidatePlainHistoryPrefix(original.Final, refreshed.Previous, baseline: original.Baseline);
            ReceiveProbe.ValidatePlainHistoryPrefix(refreshed.Previous, refreshed.Final, baseline: original.Baseline);
            Need(ReceiveProbe.SameCandidate(original.Final, original.Candidate, refreshed.Previous, refreshed.PreviousCandidate) &&
                ReceiveProbe.SameCandidate(original.Final, original.Candidate, refreshed.Final, refreshed.Candidate),
                "ROUNDTRIP_REOBSERVATION_CHANGED");
            return refreshed;
        }

        private static void Result(AuditLog log, string status, string reason, bool reserved, bool sendStageEntered, int replyCount = 1)
        {
            log.Write("INFO", "ROUNDTRIP_RESULT", AuditLog.Field("status", status), AuditLog.Field("reason", reason),
                AuditLog.Field("diagnostic_token_reserved", reserved), AuditLog.Field("send_stage_entered", sendStageEntered),
                AuditLog.Field("reservation_flag_means_confirmed", true), AuditLog.Field("failed_reservation_may_persist", true),
                AuditLog.Field("prepared_replies", replyCount), AuditLog.Field("plain_body_verified", false),
                AuditLog.Field("conversation_identity_verified", false), AuditLog.Field("delivery_verified", false),
                AuditLog.Field("automatic_send_allowed", false));
        }

        internal static void RunSelfTest(string directory)
        {
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
            ProbeSnapshot History(int extras)
            {
                var snapshot = ReceiveProbe.CreateTestSnapshot();
                ReceiveProbe.AppendTestHistoryText(snapshot, SupervisedSendTest.ReadyText("D345678"));
                ReceiveProbe.AppendTestHistoryText(snapshot, "pwrsi");
                for (var i = 0; i < extras; i++) ReceiveProbe.AppendTestHistoryText(snapshot, i % 2 == 0 ? "help" : "general message");
                return snapshot;
            }
            var baseline = ReceiveProbe.BindReadyBoundary(initial, History(0));
            ReceiveProbe.ObservationProof Proof(int extras, TimeSpan? age = null)
            {
                var previous = History(extras); var final = History(extras);
                return new ReceiveProbe.ObservationProof(owner, window, new NativeMethods.WindowRectangle(), baseline,
                    previous, ReceiveProbe.Evaluate(baseline, previous), final, ReceiveProbe.Evaluate(baseline, final), age);
            }
            void Reject(Action action)
            {
                try { action(); } catch (MonitorException) { return; }
                throw new InvalidOperationException("Unsafe operating handoff accepted.");
            }
            try
            {
                Reject(() => AuthorizeHandoffCore(Proof(0), owner, window, marker, History(1), path, () => false, false));
                Reject(() => AuthorizeHandoffCore(Proof(0, TimeSpan.FromSeconds(16)), owner, window, marker,
                    History(1), path, () => false, true));
                Need(!File.Exists(path), "OPERATING_EXPIRED_NOTICE_NO_RESERVATION");
                var busyProof = Proof(0);
                AuthorizeHandoffCore(busyProof, owner, window, marker, History(2), path, () => false, true);
                Need(busyProof.DiagnosticTokenReserved, "OPERATING_BUSY_RESERVED_ONCE");
                Reject(() => AuthorizeHandoffCore(busyProof, owner, window, marker, History(2), path, () => false, true));
                var refreshed = SelectRefreshedProof(busyProof, Proof(3), owner, window);
                AuthorizeHandoffCore(refreshed, owner, window, marker, History(4), path, () => false, false);
                Need(File.ReadAllLines(path).Length == 1, "OPERATING_REPLY_REUSES_RESERVATION");
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
                Reject(() => AuthorizeHandoff(Proof(ageOffset: TimeSpan.FromSeconds(16)), owner, window, marker,
                    History(true), path, () => false));
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
