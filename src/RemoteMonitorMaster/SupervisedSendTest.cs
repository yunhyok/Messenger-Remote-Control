using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using RemoteMonitorLink;

namespace RemoteMonitorMaster
{
    // A manually confirmed, single-message functional test. This does not enable the legacy or unattended sender.
    internal static class SupervisedSendTest
    {
        internal const string MouseReleaseWarning = "Mouse-button release was NOT confirmed.";
        internal const string ReadyNotice = "Master Ready. help, total status, pwrsi 중 하나를 보내세요.";
        internal const string PowerSiBusyNotice = "Processing pwrsi. 답장과 다음 Master Ready를 기다리세요. 그 사이 보낸 메시지는 무시됩니다.";
        internal const string StatusBusyNotice = "Processing total status. 답장과 다음 Master Ready를 기다리세요. 그 사이 보낸 메시지는 무시됩니다.";

        internal static string ReadyText(string marker)
        {
            Need(IsValidMarker(marker), "SEND_MARKER_INVALID");
            return ReadyNotice + " [" + marker + "]";
        }

        internal sealed class Outcome
        {
            internal readonly string Status, Reason, Message;
            internal readonly bool CleanCompletion;
            internal Outcome(string status, string reason, string message, bool cleanCompletion)
            { Status = status; Reason = reason; Message = message; CleanCompletion = cleanCompletion; }
        }

        internal sealed class Consent
        {
            internal readonly string Marker;
            internal readonly bool IsPcStatus;
            internal readonly bool IsPlainCommands;
            private string command;
            private readonly SlaveEndpoint slave;
            private readonly string nextMarker;
            private readonly CancellationTokenSource slaveCancellation;
            internal bool IsSlaveStatus { get { return slave != null; } }
            private string preparedReply;
            private string[] preparedReplies;
            private Consent[] preparedParts;
            private PreparedPowerSiOutput preparedOutput;
            private int confirmedReplyParts, outputCommitted;
            private Consent progressNotice;
            private bool isNotice;
            internal bool IsOperational { get { return IsPlainCommands || isNotice; } }
            internal string NoticeText { get { Need(isNotice, "SEND_NOTICE_REQUIRED"); return preparedReply; } }
            private int replyPartIndex = 1, replyPartCount = 1;
            // Low bits: 0=confirmed, 1=consumed, 2=move, 3=write, 4=click committed. Mask 8 preserves attempts on cancellation.
            private int state;
            private int roundTripClaimed;

            public Consent(string marker, bool confirmed)
                : this(marker, confirmed, false) { }

            internal Consent(string marker, bool confirmed, bool pcStatus)
                : this(marker, confirmed, pcStatus, null) { }

            internal Consent(string marker, bool confirmed, bool pcStatus, SlaveEndpoint slave)
                : this(marker, confirmed, pcStatus, slave, null) { }

            internal Consent(string marker, bool confirmed, bool pcStatus, SlaveEndpoint slave, string nextMarker, bool plainCommands = false)
            {
                Need(confirmed, "SEND_CONFIRMATION_REQUIRED");
                Need(IsValidMarker(marker), "SEND_MARKER_INVALID");
                Need(slave == null || pcStatus, "SLAVE_STATUS_MODE_REQUIRED");
                Need(!plainCommands || (pcStatus && slave != null && nextMarker == null), "COMMAND_SLAVE_MODE_REQUIRED");
                Need(nextMarker == null || (pcStatus && Protocol.IsDiagnosticMarker("MESSAGE", nextMarker) &&
                    nextMarker.Substring(1) != marker.Substring(1)), "STATUS_NEXT_MARKER_INVALID");
                Marker = marker;
                IsPcStatus = pcStatus;
                IsPlainCommands = plainCommands;
                this.slave = slave;
                this.nextMarker = nextMarker;
                if (slave != null) slaveCancellation = new CancellationTokenSource();
                preparedReply = pcStatus ? null : marker;
            }

            private Consent(Consent parent, string payload, int index, int count)
            {
                Marker = parent.Marker;
                IsPcStatus = true;
                IsPlainCommands = true;
                command = parent.command;
                preparedReply = payload;
                replyPartIndex = index;
                replyPartCount = count;
                roundTripClaimed = 1;
            }

            internal static Consent ForNotice(string marker, string text)
            {
                Need(text == ReadyNotice || text == PowerSiBusyNotice || text == StatusBusyNotice, "SEND_NOTICE_INVALID");
                return new Consent(marker, true) { isNotice = true,
                    preparedReply = text == ReadyNotice ? ReadyText(marker) : text };
            }

            internal Consent CreateProgressNotice(string text)
            {
                Need(IsPlainCommands && !Cancelled, "SEND_NOTICE_INVALID");
                var notice = ForNotice(Marker, text);
                Need(Interlocked.CompareExchange(ref progressNotice, notice, null) == null, "SEND_NOTICE_USED");
                if (Cancelled) notice.Cancel();
                return notice;
            }

            internal void BindCommand(string value)
            {
                Need(IsPlainCommands && ReadOnlyCommands.IsCommand(value) && Volatile.Read(ref state) == 0 &&
                    Volatile.Read(ref roundTripClaimed) == 1 && Volatile.Read(ref preparedReply) == null,
                    "COMMAND_CONSENT_INVALID");
                Need(Interlocked.CompareExchange(ref command, value, null) == null, "COMMAND_CONSENT_USED");
                Need(Volatile.Read(ref state) == 0, "SEND_CANCELLED");
            }

            internal string PrepareReply()
            {
                var replies = PrepareReplies();
                Need(replies.Length == 1, "PC_STATUS_MULTIPART_REQUIRED");
                return replies[0];
            }

            internal string[] PrepareReplies()
            {
                if (!IsPcStatus) return new[] { Marker };
                Need(Volatile.Read(ref state) == 0 && Volatile.Read(ref roundTripClaimed) == 1 &&
                    Volatile.Read(ref preparedReply) == null, "PC_STATUS_CONSENT_USED");
                PreparedPowerSiOutput output = null;
                var replies = IsPlainCommands ? ReadOnlyCommands.CaptureReplies(Volatile.Read(ref command), Marker, slave, slaveCancellation.Token, out output) :
                    new[] { slave == null ? PcStatusReport.Capture(Marker) :
                        PcStatusReport.CaptureSlave(Marker, slave, slaveCancellation.Token) };
                if (nextMarker != null) replies[0] = PcStatusReport.WithNext(replies[0], Marker, IsSlaveStatus, nextMarker);
                BindPreparedReplies(replies, output);
                return (string[])replies.Clone();
            }

            internal void BindPreparedReplies(string[] replies, PreparedPowerSiOutput output = null)
            {
                Need(replies != null && replies.Length > 0 && replies.Length <= ReadOnlyCommands.MaxReportParts &&
                    Volatile.Read(ref state) == 0 && Volatile.Read(ref preparedReply) == null, "PC_STATUS_CONSENT_USED");
                var bound = (string[])replies.Clone();
                for (var i = 0; i < bound.Length; i++)
                    Need(bound[i] != null && (IsPlainCommands ?
                        ReadOnlyCommands.IsReplyPart(bound[i], Volatile.Read(ref command), Marker, i + 1, bound.Length) :
                        i == 0 && bound.Length == 1 && (IsSlaveStatus ? PcStatusReport.IsSlaveReply(bound[i], Marker, nextMarker) :
                            PcStatusReport.IsReply(bound[i], Marker, nextMarker))), "PC_STATUS_REPLY_INVALID");
                Need(Volatile.Read(ref state) == 0, "SEND_CANCELLED");
                Need(Interlocked.CompareExchange(ref preparedReply, bound[0], null) == null, "PC_STATUS_CONSENT_USED");
                replyPartIndex = 1;
                replyPartCount = bound.Length;
                var children = new Consent[Math.Max(0, bound.Length - 1)];
                for (var i = 1; i < bound.Length; i++) children[i - 1] = new Consent(this, bound[i], i + 1, bound.Length);
                Volatile.Write(ref preparedParts, children);
                Volatile.Write(ref preparedReplies, bound);
                preparedOutput = output;
                Need(Volatile.Read(ref state) == 0, "SEND_CANCELLED");
            }

            internal void RecordPreparedPartOutcome(int index, Outcome outcome)
            {
                var part = GetPreparedPart(index);
                Need(!Cancelled && index == confirmedReplyParts && outcome != null && outcome.CleanCompletion &&
                    part.OwnAttempted && !part.Cancelled, "PC_STATUS_PART_NOT_CONFIRMED");
                confirmedReplyParts++;
            }

            internal bool CommitPreparedOutput()
            {
                Need(!Cancelled && PreparedReplyCount > 0 && confirmedReplyParts == PreparedReplyCount &&
                    Interlocked.CompareExchange(ref outputCommitted, 1, 0) == 0, "PC_STATUS_OUTPUT_NOT_CONFIRMED");
                return preparedOutput == null || preparedOutput.Commit();
            }

            internal string OutputHistoryFailure { get { return preparedOutput?.FailureReason ?? "NONE"; } }

            internal int PreparedReplyCount { get { return Volatile.Read(ref preparedReplies)?.Length ?? (IsPcStatus ? 0 : 1); } }

            internal Consent GetPreparedPart(int index)
            {
                var replies = Volatile.Read(ref preparedReplies);
                Need(replies != null && index >= 0 && index < replies.Length, "PC_STATUS_PART_INVALID");
                return index == 0 ? this : Volatile.Read(ref preparedParts)[index - 1];
            }

            internal bool IsAuthorizedReply(string text)
            {
                return text != null && text == Volatile.Read(ref preparedReply) &&
                    (isNotice ? text == ReadyText(Marker) || text == PowerSiBusyNotice || text == StatusBusyNotice :
                    IsPlainCommands ? ReadOnlyCommands.IsReplyPart(text, Volatile.Read(ref command), Marker, replyPartIndex, replyPartCount) :
                        IsSlaveStatus ? PcStatusReport.IsSlaveReply(text, Marker, nextMarker) :
                        IsPcStatus ? PcStatusReport.IsReply(text, Marker, nextMarker) : IsValidMarker(text));
            }

            private bool OwnCursorMoveAttempted { get { return (Volatile.Read(ref state) & 7) >= 2; } }
            private bool OwnWriteAttempted { get { return (Volatile.Read(ref state) & 7) >= 3; } }
            private bool OwnAttempted { get { return (Volatile.Read(ref state) & 7) == 4; } }
            private bool OwnPendingWrite { get { return (Volatile.Read(ref state) & 7) == 3; } }
            private bool AnyPart(Func<Consent, bool> predicate)
            {
                var parts = Volatile.Read(ref preparedParts);
                var notice = Volatile.Read(ref progressNotice);
                return (notice != null && predicate(notice)) || (parts != null && parts.Any(predicate));
            }
            public bool CursorMoveAttempted { get { return OwnCursorMoveAttempted || AnyPart(part => part.OwnCursorMoveAttempted); } }
            public bool WriteAttempted { get { return OwnWriteAttempted || AnyPart(part => part.OwnWriteAttempted); } }
            public bool Attempted { get { return OwnAttempted || AnyPart(part => part.OwnAttempted); } }
            internal bool PendingWrite { get { return OwnPendingWrite || AnyPart(part => part.OwnPendingWrite); } }
            public bool Cancelled { get { return (Volatile.Read(ref state) & 8) != 0; } }
            public bool Cancel()
            {
                var attempted = false;
                while (true)
                {
                    var current = Volatile.Read(ref state);
                    if (Interlocked.CompareExchange(ref state, current | 8, current) == current)
                    {
                        attempted = (current & 7) == 4;
                        slaveCancellation?.Cancel();
                        break;
                    }
                }
                var parts = Volatile.Read(ref preparedParts);
                if (parts != null) foreach (var part in parts) attempted |= part.Cancel();
                var notice = Volatile.Read(ref progressNotice);
                if (notice != null) attempted |= notice.Cancel();
                return attempted;
            }
            internal bool TryConsume(string marker)
            {
                // Even a mismatched request consumes the confirmation. A failed run is never reusable.
                var matches = IsAuthorizedReply(marker);
                return Interlocked.CompareExchange(ref state, matches ? 1 : 8, 0) == 0 && matches;
            }
            internal bool TryCommitMove() { return Interlocked.CompareExchange(ref state, 2, 1) == 1; }
            internal bool TryCommitWrite() { return Interlocked.CompareExchange(ref state, 3, 2) == 2; }
            internal bool TryCommit() { return Interlocked.CompareExchange(ref state, 4, 3) == 3; }
            internal bool TryClaimRoundTrip()
            {
                return Interlocked.CompareExchange(ref roundTripClaimed, 1, 0) == 0 && Volatile.Read(ref state) == 0;
            }
        }

        public static string Run(IntPtr window, AuditLog log, string marker, Point pointer, Consent consent, Func<bool> stopRequested)
        {
            return RunBound(window, log, marker, pointer, consent, stopRequested, null);
        }

        internal static string RunBound(IntPtr window, AuditLog log, string marker, Point pointer, Consent consent,
            Func<bool> stopRequested, Action<ProbeSnapshot> validateSnapshot)
        {
            return RunBoundObserved(window, log, marker, pointer, consent, stopRequested, validateSnapshot).Message;
        }

        internal static Outcome RunBoundObserved(IntPtr window, AuditLog log, string marker, Point pointer, Consent consent,
            Func<bool> stopRequested, Action<ProbeSnapshot> validateSnapshot)
        {
            var clock = Stopwatch.StartNew();
            var attempted = false;
            var writeCalls = 0;
            var writeReturned = false;
            var writeVerified = false;
            var positioned = false;
            var moveRequested = false;
            var click = new MouseClickInput.Result();
            var inputState = "UNAVAILABLE";
            var postObservations = 0;
            var property = "request";
            string guardFailure = null;
            bool? pointerFailureRead = null;
            NativeMethods.ScreenPoint failedPointer = default(NativeMethods.ScreenPoint);
            ProcessIdentity process = null;
            NativeMethods.WindowRectangle? windowBounds = null;
            try
            {
                Need(consent != null, "SEND_CONFIRMATION_REQUIRED");
                var consumed = consent.TryConsume(marker);
                Need(consent.IsAuthorizedReply(marker), "SEND_CONSENT_MARKER_MISMATCH");
                Need(!consent.Cancelled, "SEND_CANCELLED");
                Need(consumed, "SEND_CONSENT_USED");
                Need(log != null && stopRequested != null, "SEND_REQUEST_INVALID");
                Need(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "PROBE_REQUIRES_MTA");

                bool GuardedStop()
                {
                    if (guardFailure != null) return true;
                    if (consent.Cancelled || stopRequested()) guardFailure = "SEND_CANCELLED";
                    else if (clock.Elapsed >= TimeSpan.FromSeconds(15)) guardFailure = "SEND_TIME_LIMIT";
                    else if (window == IntPtr.Zero || !NativeMethods.IsWindow(window)) guardFailure = "SEND_NO_TARGET";
                    else if (NativeMethods.GetForegroundWindow() != window) guardFailure = "SEND_FOREGROUND_CHANGED";
                    else if (process != null)
                    {
                        uint pid;
                        if (NativeMethods.GetWindowThreadProcessId(window, out pid) == 0 || pid != process.ProcessId)
                            guardFailure = "SEND_PROCESS_CHANGED";
                    }
                    if (guardFailure == null && windowBounds.HasValue)
                    {
                        NativeMethods.WindowRectangle current;
                        if (!NativeMethods.GetWindowRect(window, out current) || !current.Equals(windowBounds.Value))
                            guardFailure = "SEND_WINDOW_BOUNDS_CHANGED";
                    }
                    if (guardFailure == null && positioned && !attempted)
                    {
                        NativeMethods.ScreenPoint current;
                        var readSucceeded = NativeMethods.GetPhysicalCursorPos(out current);
                        guardFailure = PointerFailure(readSucceeded, current, pointer);
                        if (guardFailure != null) { pointerFailureRead = readSucceeded; failedPointer = current; }
                    }
                    return guardFailure != null;
                }
                void Alive() { Need(!GuardedStop(), guardFailure); }
                T Read<T>(string name, Func<T> read)
                {
                    property = name;
                    Alive();
                    var value = read();
                    Alive();
                    return value;
                }

                Alive();
                log.Write("INFO", "SUPERVISED_SEND_BEGIN", AuditLog.Field("manual_self_chat_confirmed", true),
                    AuditLog.Field("send_method", "TIMED_MOUSE_CLICK"), AuditLog.Field("actual_mouse_click_confirmed", true),
                    AuditLog.Field("automatic_cursor_positioning_confirmed", true), AuditLog.Field("initial_hover_required", false),
                    AuditLog.Field("automatic_entry_confirmed", true), AuditLog.Field("empty_input_required", true), AuditLog.Field("entry_method", "UIA_SETVALUE"),
                    AuditLog.Field("single_message_confirmed", true), AuditLog.Field("consent_consumed", true),
                    AuditLog.Field("cooperative_seconds", 15), AuditLog.Field("budget_scope", "WHOLE_TEST"),
                    AuditLog.Field("individual_call_timeout", false), AuditLog.Field("automatic_send_allowed", false));
                process = Read("process", () => ProcessIdentity.Capture(window));
                Need(string.Equals(process.ProcessName, "KI-Messenger", StringComparison.OrdinalIgnoreCase), "PROBE_NOT_KI_MESSENGER");
                NativeMethods.WindowRectangle initialBounds;
                Need(NativeMethods.GetWindowRect(window, out initialBounds), "SEND_WINDOW_BOUNDS_UNAVAILABLE");
                windowBounds = initialBounds;
                var snapshot = ReadOnlyProbe.CaptureSnapshot(window, log, "SEND_METADATA", null, GuardedStop,
                    retainSelectedInput: true, automaticSendSelection: true, compactLog: consent.IsOperational);
                Alive();
                var selected = ReadOnlyPair.SelectAutomaticInput(snapshot);
                var selectedSend = ReadOnlyPair.SelectAutomaticSend(snapshot);
                var rootNode = snapshot.Nodes.Single(n => n.Node == 1);
                var document = snapshot.Nodes.Single(n => n.Node == selected.Document);
                var input = snapshot.MatchedElement;
                var send = snapshot.MatchedSend;
                List<UiaPointProbe.PathNode> inputPath = null, sendPath = null;
                Need(process.Equals(snapshot.Process), "SEND_PROCESS_CHANGED");
                Need(input != null && send != null, "SEND_INPUT_OR_BUTTON_UNAVAILABLE");
                if (validateSnapshot != null)
                {
                    property = "bound_receive_snapshot";
                    validateSnapshot(snapshot); // May reject/reserve only. This is before any movement, input or click.
                    Alive();
                }

                UiaPointProbe.PathNode CheckScope(AutomationElement element, ProbeNode expected = null, int depth = 0)
                {
                    Need(element != null, "SEND_ELEMENT_UNAVAILABLE");
                    // Security-only batches keep repeated ancestry checks bounded without caching message content or patterns.
                    var request = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Condition.TrueCondition,
                        AutomationElementMode = AutomationElementMode.Full };
                    foreach (var cachedProperty in new[] { AutomationElement.ProcessIdProperty, AutomationElement.IsPasswordProperty,
                        AutomationElement.NativeWindowHandleProperty, AutomationElement.RuntimeIdProperty }) request.Add(cachedProperty);
                    var cached = Read("element_security_cache", () => element.GetUpdatedCache(request));
                    Need(cached != null, "SEND_ELEMENT_UNAVAILABLE");
                    var pid = cached.GetCachedPropertyValue(AutomationElement.ProcessIdProperty, true);
                    Need(IsCachedProcessId(pid, process.ProcessId), "SEND_FOREIGN_PROCESS");
                    var password = cached.GetCachedPropertyValue(AutomationElement.IsPasswordProperty, true);
                    Need(IsCachedPassword(password), "SEND_PROTECTED_OR_UNAVAILABLE");
                    var handle = cached.GetCachedPropertyValue(AutomationElement.NativeWindowHandleProperty, false);
                    Need(IsCachedNativeHwnd(handle) && (expected == null || (int)handle == expected.NativeHwnd), "SEND_NATIVE_WINDOW_CHANGED");
                    var native = new IntPtr((int)handle);
                    if (native != IntPtr.Zero)
                    {
                        uint nativePid;
                        Need(NativeMethods.IsWindow(native) && (native == window || NativeMethods.IsChild(window, native)) &&
                            NativeMethods.GetWindowThreadProcessId(native, out nativePid) != 0 && nativePid == process.ProcessId,
                            "SEND_NATIVE_WINDOW_OUTSIDE_TARGET");
                    }
                    var runtimeId = cached.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty, true);
                    Need(IsCachedRuntimeId(runtimeId), "POINT_RUNTIME_ID_INVALID");
                    var runtime = UiaPointProbe.Format((int[])runtimeId);
                    Need(expected == null || runtime == expected.Identity.RuntimeId,
                        "SEND_ELEMENT_CHANGED");
                    Alive();
                    return new UiaPointProbe.PathNode(depth, runtime, (int)handle, 0);
                }

                void CheckRoot()
                {
                    Need(process.Equals(Read("process_recheck", () => ProcessIdentity.Capture(window))), "SEND_PROCESS_CHANGED");
                    var root = Read("root", () => AutomationElement.FromHandle(window));
                    CheckScope(root, rootNode);
                    Need(log.Fingerprint(Read("root_name", () => root.Current.Name)) == snapshot.RootNameFingerprint, "SEND_ROOT_CHANGED");
                    CheckScope(root, rootNode);
                }

                void CheckPath(AutomationElement element, ProbeNode expected, ref List<UiaPointProbe.PathNode> baseline, string kind)
                {
                    // Retain the canonical element, but baseline its live upward path separately: provider traversal
                    // directions need not expose the same virtual ancestors. Exact native document/root remain mandatory.
                    var cursor = element;
                    var walker = TreeWalker.RawViewWalker;
                    var currentPath = new List<UiaPointProbe.PathNode>();
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    var documentFound = false;
                    for (var depth = 0; ; depth++)
                    {
                        Need(depth <= 32, "SEND_" + kind + "_PATH_LIMIT");
                        var node = CheckScope(cursor, depth == 0 ? expected : null, depth);
                        Need(visited.Add(node.RuntimeId), "SEND_" + kind + "_PATH_CYCLE");
                        if (baseline != null)
                            Need(depth < baseline.Count && baseline[depth].RuntimeId == node.RuntimeId &&
                                baseline[depth].NativeHwnd == node.NativeHwnd, "SEND_" + kind + "_PATH_CHANGED");
                        currentPath.Add(node);
                        if (node.NativeHwnd == document.NativeHwnd && node.RuntimeId == document.Identity.RuntimeId)
                        {
                            Need(Equals(Read("path_document_type", () => cursor.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true)), ControlType.Document),
                                "SEND_" + kind + "_DOCUMENT_CHANGED");
                            documentFound = true;
                        }
                        if (new IntPtr(node.NativeHwnd) == window)
                        {
                            Need(node.RuntimeId == rootNode.Identity.RuntimeId && documentFound &&
                                (baseline == null || baseline.Count == currentPath.Count), "SEND_" + kind + "_ROOT_CHANGED");
                            break;
                        }
                        cursor = Read("path_parent", () => walker.GetParent(cursor));
                    }
                    if (baseline == null)
                    {
                        baseline = currentPath;
                        foreach (var node in baseline)
                        {
                            Alive();
                            log.Write("INFO", "SUPERVISED_SEND_" + kind + "_PATH", AuditLog.Field("depth", node.Depth),
                                AuditLog.Field("runtime_id", node.RuntimeId), AuditLog.Field("native_hwnd", node.NativeHwnd),
                                AuditLog.Field("conversation_identity_verified", false));
                        }
                    }
                }

                ValuePattern PrepareInput()
                {
                    CheckPath(input, selected, ref inputPath, "INPUT");
                    Need(Equals(Read("input_type", () => input.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true)), ControlType.Edit),
                        "SEND_INPUT_TYPE_CHANGED");
                    Need(UiaPointProbe.ObserveBoolean(Read("input_enabled", () => input.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true))) == true &&
                        UiaPointProbe.ObserveBoolean(Read("input_offscreen", () => input.GetCurrentPropertyValue(AutomationElement.IsOffscreenProperty, true))) == false,
                        "SEND_INPUT_UNUSABLE");
                    var pattern = Read("input_pattern", () =>
                    {
                        object value;
                        Need(input.TryGetCurrentPattern(ValuePattern.Pattern, out value) && value is ValuePattern, "SEND_VALUE_PATTERN_UNAVAILABLE");
                        return (ValuePattern)value;
                    });
                    Need(!Read("input_readonly", () => pattern.Current.IsReadOnly), "SEND_INPUT_READ_ONLY");
                    return pattern;
                }

                string ReadInput(ValuePattern pattern)
                {
                    CheckScope(input, selected);
                    Need(UiaPointProbe.ObserveBoolean(Read("input_enabled", () => input.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true))) == true &&
                        UiaPointProbe.ObserveBoolean(Read("input_offscreen", () => input.GetCurrentPropertyValue(AutomationElement.IsOffscreenProperty, true))) == false,
                        "SEND_INPUT_UNUSABLE");
                    Need(!Read("input_readonly", () => pattern.Current.IsReadOnly), "SEND_INPUT_READ_ONLY");
                    var contents = Read("input_value", () => pattern.Current.Value);
                    CheckScope(input, selected);
                    Need(contents != null, "SEND_INPUT_VALUE_UNAVAILABLE");
                    var state = ClassifyInput(contents, marker);
                    if (writeCalls != 0 && !writeVerified)
                        log.Write("INFO", "SEND_DRAFT_READBACK", AuditLog.Field("classification", state),
                            AuditLog.Field("expected_length", marker.Length), AuditLog.Field("actual_length", contents.Length),
                            AuditLog.Field("expected_cr", marker.Count(c => c == '\r')), AuditLog.Field("actual_cr", contents.Count(c => c == '\r')),
                            AuditLog.Field("expected_lf", marker.Count(c => c == '\n')), AuditLog.Field("actual_lf", contents.Count(c => c == '\n')),
                            AuditLog.Field("exact", contents == marker), AuditLog.Field("line_endings_equivalent", state == "UNCHANGED"),
                            AuditLog.Field("actual_fingerprint", log.Fingerprint(contents)));
                    return state; // No draft text or excerpt enters the log.
                }

                CheckRoot();
                inputState = ReadInput(PrepareInput());
                Need(inputState == "EMPTY", "SEND_INPUT_NOT_EMPTY");
                Rect? sendBounds = null;
                void PrepareSend()
                {
                    CheckPath(send, selectedSend, ref sendPath, "BUTTON");
                    Need(Equals(Read("send_type", () => send.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true)), ControlType.Hyperlink),
                        "SEND_BUTTON_TYPE_CHANGED");
                    Need(UiaPointProbe.ObserveBoolean(Read("send_enabled", () => send.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true))) == true &&
                        UiaPointProbe.ObserveBoolean(Read("send_offscreen", () => send.GetCurrentPropertyValue(AutomationElement.IsOffscreenProperty, true))) == false,
                        "SEND_BUTTON_UNUSABLE");
                    var bounds = Read("send_bounds", () => send.Current.BoundingRectangle);
                    var center = SendCenter(bounds);
                    Need(sendBounds == null || bounds == sendBounds.Value, "SEND_BUTTON_BOUNDS_CHANGED");
                    var root = Read("bounds_root", () => AutomationElement.FromHandle(window));
                    CheckScope(root, rootNode);
                    Need(Read("root_bounds", () => root.Current.BoundingRectangle).Contains(bounds), "SEND_BUTTON_OUTSIDE_ROOT");
                    CheckScope(send, selectedSend);
                    if (sendBounds == null) { sendBounds = bounds; pointer = center; }
                }
                PrepareSend();
                UiaPointProbe.CaptureResult CheckPoint()
                {
                    property = "uia_point";
                    var point = UiaPointProbe.Capture(pointer, process.ProcessId, window, rootNode.Identity.RuntimeId, Alive, snapshot,
                        automaticSendSelection: true);
                    Need(point.Path[0].ControlTypeId == ControlType.Hyperlink.Id && point.InvokeAvailable &&
                        point.Enabled == true && point.Offscreen == false && point.PointerInside == true,
                        "POINT_SEND_CAPABILITIES_REQUIRED");
                    Need(point.Path.Any(n => n.ControlTypeId == ControlType.Document.Id && n.NativeHwnd == document.NativeHwnd &&
                        n.RuntimeId == document.Identity.RuntimeId), "POINT_SEND_DOCUMENT_MISMATCH");
                    return point;
                }
                var observation = CheckPoint();
                // Validate Send before writing too. One empty-input write, exact readback, then the proven click.
                property = "msaa_point";
                SendMetadataProbe.WithValidatedPoint(pointer, process.ProcessId, window, log, Alive,
                    recheckPoint =>
                    {
                        Alive();
                        log.Write("INFO", "SUPERVISED_SEND_READY", AuditLog.Field("point_runtime_id", UiaPointProbe.Format(observation.RuntimeId)),
                            AuditLog.Field("send_method", "TIMED_MOUSE_CLICK"),
                            AuditLog.Field("point_comparison", UiaPointProbe.CompareRuntimeId(observation.RuntimeId, snapshot, automaticSendSelection: true)),
                            AuditLog.Field("invoke_pattern_available", observation.InvokeAvailable),
                            AuditLog.Field("enabled", observation.Enabled), AuditLog.Field("offscreen", observation.Offscreen),
                            AuditLog.Field("pointer_inside_bounds", observation.PointerInside), AuditLog.Field("native_document_matched", true),
                            AuditLog.Field("elapsed_ms", clock.ElapsedMilliseconds), AuditLog.Field("conversation_identity_verified", false),
                            AuditLog.Field("uia_msaa_identity_verified", false), AuditLog.Field("automatic_send_allowed", false));
                        CheckRoot();
                        var pattern = PrepareInput();
                        PrepareSend();
                        recheckPoint();
                        inputState = ReadInput(pattern);
                        Need(inputState == "EMPTY", "SEND_INPUT_NOT_EMPTY");
                        property = "cursor_position";
                        MouseClickInput.PositionOnce(pointer, process.ProcessId, window, log, Alive, () =>
                        {
                            Need(consent.TryCommitMove(), "SEND_CANCELLED");
                            moveRequested = true;
                        });
                        positioned = true;
                        Alive();
                        CheckRoot();
                        pattern = PrepareInput();
                        PrepareSend();
                        recheckPoint();
                        log.Write("INFO", "SUPERVISED_WRITE_READY", AuditLog.Field("maximum_setvalue_calls", 1),
                            AuditLog.Field("empty_input_required", true));
                        inputState = ReadInput(pattern);
                        Need(inputState == "EMPTY", "SEND_INPUT_NOT_EMPTY");
                        MouseClickInput.RequireIdleInput(window);
                        Alive();
                        property = "input_setvalue";
                        // ponytail: UIA has no compare-and-set; this final empty check requires an untouched chat, not a draft lock.
                        Need(consent.TryCommitWrite(), "SEND_CANCELLED");
                        writeCalls = 1;
                        inputState = "UNAVAILABLE"; // Never report stale pre-write EMPTY if the provider throws or Stop wins.
                        pattern.SetValue(marker); // Exactly once. No clipboard, key input, clear or write retry.
                        writeReturned = true;
                        Alive();
                        CheckRoot();
                        inputState = WaitForWrittenInput(() => ReadInput(PrepareInput()), Alive);
                        Need(inputState == "UNCHANGED", "SEND_WRITE_NOT_VERIFIED");
                        writeVerified = true;
                        log.Write("INFO", "SUPERVISED_WRITE_VERIFIED", AuditLog.Field("setvalue_calls", writeCalls),
                            AuditLog.Field("content_verified", true), AuditLog.Field("normalization", "CRLF_LF_ONLY"));
                        PrepareSend();
                        var afterWrite = CheckPoint();
                        Need(afterWrite.Path.Count == observation.Path.Count && afterWrite.Path.Zip(observation.Path,
                            (a, b) => a.RuntimeId == b.RuntimeId && a.NativeHwnd == b.NativeHwnd && a.ControlTypeId == b.ControlTypeId).All(same => same),
                            "SEND_POINT_PATH_CHANGED");
                        CheckRoot();
                        pattern = PrepareInput();
                        recheckPoint();
                        PrepareSend();
                        inputState = ReadInput(pattern);
                        Need(inputState == "UNCHANGED", "SEND_DRAFT_MISMATCH");
                        Alive();
                        property = "mouse_click";
                        MouseClickInput.ClickOnce(pointer, process.ProcessId, window, log, Alive, () =>
                        {
                            Need(consent.TryCommit(), "SEND_CANCELLED"); // Commit only after the final cheap native checks.
                            attempted = true;
                        }, click);
                    });
                // Queued mouse input may update the draft later. Observe briefly; never retry the action.
                var observationDeadline = clock.Elapsed + TimeSpan.FromSeconds(2);
                do
                {
                    CheckRoot();
                    inputState = ReadInput(PrepareInput());
                    postObservations++;
                    log.Write("INFO", "SUPERVISED_SEND_OBSERVATION", AuditLog.Field("sample", postObservations),
                        AuditLog.Field("input_state", inputState), AuditLog.Field("elapsed_ms", clock.ElapsedMilliseconds));
                    if (inputState != "UNCHANGED" || postObservations >= 9 || clock.Elapsed >= observationDeadline) break;
                    Thread.Sleep(250);
                    Alive();
                } while (clock.Elapsed < observationDeadline);
                Alive();
                var status = inputState == "OTHER" ? "UNKNOWN" : inputState == "EMPTY" ? "ACTION_RETURNED" : "NO_INPUT_CHANGE";
                LogResult(log, status, inputState == "OTHER" ? "SEND_INPUT_CHANGED" : "NONE", attempted, click, inputState, clock.ElapsedMilliseconds, true,
                    postObservations, writeCalls, writeReturned, writeVerified, moveRequested, positioned);
                var message = status + " - Input: " + inputState + "." + Environment.NewLine +
                    "One mouse click was queued. Message delivery is NOT verified. Check the PC and phone once; do not repeat this test.";
                return new Outcome(status, inputState == "OTHER" ? "SEND_INPUT_CHANGED" : "NONE", message,
                    IsCleanCompletion(attempted, click, inputState, writeCalls, writeReturned, writeVerified,
                        moveRequested && positioned, consent.Cancelled));
            }
            catch (Exception ex)
            {
                var reason = (ex as MonitorException)?.ReasonCode ?? "SEND_TEST_FAILED";
                var status = attempted || writeCalls != 0 ? "UNKNOWN" : "REJECTED";
                try
                {
                    if (log != null)
                    {
                        if (pointerFailureRead.HasValue)
                            log.Write("INFO", "POINTER_GUARD_RESULT", AuditLog.Field("read_succeeded", pointerFailureRead.Value),
                                AuditLog.Field("expected_x", pointer.X), AuditLog.Field("expected_y", pointer.Y),
                                AuditLog.Field("actual_x", pointerFailureRead.Value ? (object)failedPointer.X : "UNAVAILABLE"),
                                AuditLog.Field("actual_y", pointerFailureRead.Value ? (object)failedPointer.Y : "UNAVAILABLE"));
                        log.WriteException("SUPERVISED_SEND_FAILED", ex, AuditLog.Field("property", property),
                            AuditLog.Field("msaa_call", SendMetadataProbe.FailureCall(ex)),
                            AuditLog.Field("uia_call", UiaPointProbe.FailureCall(ex)), AuditLog.Field("elapsed_ms", clock.ElapsedMilliseconds));
                        LogResult(log, status, reason, attempted, click, inputState, clock.ElapsedMilliseconds, consent != null,
                            postObservations, writeCalls, writeReturned, writeVerified, moveRequested, positioned);
                    }
                }
                catch { }
                var message = status + " - " + reason + Environment.NewLine +
                    (attempted ? "The one mouse-click attempt was committed; its effect is unknown. Do not retry." :
                        writeCalls != 0 ? "Automatic input was attempted, but no Send click was attempted. Leave the draft untouched; do not retry." :
                        "No automatic text input or Send action was attempted. This confirmation is consumed. Collect the log; do not retry.") +
                    (moveRequested ? Environment.NewLine + "Cursor positioning was attempted; the pointer is not restored automatically." : "") +
                    (click.ReleaseAttempted && click.ReleaseInserted != 1 ? Environment.NewLine +
                        MouseReleaseWarning + " Move away from Send, then manually press and release the mouse button once. Collect the log." : "");
                return new Outcome(status, reason, message, false);
            }
        }

        internal static bool IsValidMarker(string value)
        {
            return Protocol.IsDiagnosticMarker("DRAFT", value);
        }

        internal static bool IsCleanCompletion(bool attempted, MouseClickInput.Result click, string inputState, int writeCalls,
            bool writeReturned, bool writeVerified, bool positioned, bool cancelled)
        {
            // Only called on the fully validated normal return path, never from an exception or from rendered result text.
            return !cancelled && attempted && positioned && writeCalls == 1 && writeReturned && writeVerified && inputState == "EMPTY" &&
                click != null && click.Returned && click.Inserted == 2 && click.DownInserted == 1 && click.ReleaseAttempted &&
                click.ReleaseInserted == 1 && click.Error == 0 && click.ReleaseError == 0;
        }

        internal static string ClassifyInput(string value, string marker)
        {
            // Chromium/Windows providers may expose CRLF for an LF payload. Preserve every other character.
            return value == null ? "UNAVAILABLE" : value.Length == 0 ? "EMPTY" :
                marker != null && value.Replace("\r\n", "\n") == marker.Replace("\r\n", "\n") ? "UNCHANGED" : "OTHER";
        }

        private static bool IsCachedProcessId(object value, int processId) { return value is int && (int)value == processId; }
        private static bool IsCachedPassword(object value) { return value is bool && !(bool)value; }
        private static bool IsCachedNativeHwnd(object value) { return value is int; }
        private static bool IsCachedRuntimeId(object value)
        {
            var runtime = value as int[];
            return runtime != null && runtime.Length > 0 && runtime.Length <= 64;
        }

        internal static string WaitForWrittenInput(Func<string> read, Action alive)
        {
            string state = null;
            for (var sample = 0; sample < 6; sample++)
            {
                alive();
                state = read();
                alive();
                if (state == "UNCHANGED" || sample == 5) break;
                Thread.Sleep(100); // Read-only propagation wait; SetValue and Send are never repeated.
            }
            return state;
        }

        internal static Point SendCenter(Rect bounds)
        {
            var center = new Point(Math.Floor(bounds.Left + bounds.Width / 2), Math.Floor(bounds.Top + bounds.Height / 2));
            Need(ReadOnlyProbe.ContainsPoint(bounds, center) && SendMetadataProbe.IsNativePoint(center) &&
                center.X < bounds.Right && center.Y < bounds.Bottom, "SEND_BUTTON_BOUNDS_INVALID");
            return center;
        }

        private static string PointerFailure(bool readSucceeded, NativeMethods.ScreenPoint current, Point expected)
        {
            return !readSucceeded ? "CLICK_POINTER_UNAVAILABLE" :
                current.X != expected.X || current.Y != expected.Y ? "CLICK_POINTER_CHANGED" : null;
        }

        private static void LogResult(AuditLog log, string status, string reason, bool attempted, MouseClickInput.Result click, string inputState, long elapsed,
            bool manualConfirmed, int observations, int writeCalls, bool writeReturned, bool writeVerified, bool moveRequested, bool positioned)
        {
            log.Write("INFO", "SUPERVISED_SEND_RESULT", AuditLog.Field("status", status), AuditLog.Field("reason", reason),
                AuditLog.Field("send_method", "TIMED_MOUSE_CLICK"), AuditLog.Field("entry_method", "UIA_SETVALUE"), AuditLog.Field("attempted", attempted),
                AuditLog.Field("action_returned", click.Returned), AuditLog.Field("invoke_returned", false), AuditLog.Field("invoke_calls", 0),
                AuditLog.Field("input_state", inputState), AuditLog.Field("elapsed_ms", elapsed), AuditLog.Field("delivery_verified", false),
                AuditLog.Field("post_observations", observations), AuditLog.Field("setvalue_calls", writeCalls),
                AuditLog.Field("write_attempted", writeCalls != 0), AuditLog.Field("write_returned", writeReturned), AuditLog.Field("write_verified", writeVerified),
                AuditLog.Field("default_action_calls", 0), AuditLog.Field("mouse_click_attempts", attempted ? 1 : 0),
                AuditLog.Field("mouse_events_inserted", click.Inserted), AuditLog.Field("sendinput_error", click.Error),
                AuditLog.Field("mouse_down_events_inserted", click.DownInserted), AuditLog.Field("mouse_up_events_inserted", click.ReleaseInserted),
                AuditLog.Field("requested_press_ms", MouseClickInput.PressDurationMilliseconds), AuditLog.Field("actual_hold_ms", click.HoldMilliseconds),
                AuditLog.Field("press_samples", click.PressSamples), AuditLog.Field("primary_down_observed", click.PrimaryDownObserved),
                AuditLog.Field("press_pointer_stable", (object)click.PointerStable ?? "UNAVAILABLE"),
                AuditLog.Field("press_foreground_stable", (object)click.ForegroundStable ?? "UNAVAILABLE"),
                AuditLog.Field("press_target_stable", (object)click.TargetStable ?? "UNAVAILABLE"),
                AuditLog.Field("hit_thread_same_as_root", (object)click.HitThreadSameAsRoot ?? "UNAVAILABLE"),
                AuditLog.Field("press_capture_relation", click.CaptureRelation), AuditLog.Field("press_focus_relation", click.FocusRelation),
                AuditLog.Field("target_capture_observed", click.TargetCaptureObserved), AuditLog.Field("native_observations_only", true),
                AuditLog.Field("gui_unavailable_observed", click.GuiUnavailableObserved),
                AuditLog.Field("release_only_attempted", click.ReleaseAttempted), AuditLog.Field("release_events_inserted", click.ReleaseInserted),
                AuditLog.Field("release_error", click.ReleaseError), AuditLog.Field("cursor_movement_requested", moveRequested),
                AuditLog.Field("cursor_move_verified", positioned), AuditLog.Field("initial_hover_required", false),
                AuditLog.Field("manual_self_chat_confirmed", manualConfirmed),
                AuditLog.Field("conversation_identity_verified", false), AuditLog.Field("uia_msaa_identity_verified", false),
                AuditLog.Field("automatic_send_allowed", false));
        }

        internal static void RunSelfTest()
        {
            Need(ClassifyInput("한글 123\r\noutput\r\n", "한글 123\noutput\n") == "UNCHANGED" &&
                ClassifyInput("한글 123\routput", "한글 123\noutput") == "OTHER" &&
                ClassifyInput("한글 124\r\noutput", "한글 123\noutput") == "OTHER" &&
                ClassifyInput("a b", "a\nb") == "OTHER" && ClassifyInput("a\n", "a") == "OTHER",
                "SEND_SELF_TEST_MULTILINE_READBACK");
            Need(IsCachedProcessId(17, 17) && !IsCachedProcessId(null, 17) && !IsCachedProcessId("17", 17) &&
                IsCachedPassword(false) && !IsCachedPassword(null) && !IsCachedPassword(true) &&
                IsCachedNativeHwnd(0) && !IsCachedNativeHwnd(null) && !IsCachedNativeHwnd("0") &&
                IsCachedRuntimeId(new[] { 1 }) && !IsCachedRuntimeId(null) && !IsCachedRuntimeId(new int[0]) &&
                !IsCachedRuntimeId(new object()) && !IsCachedRuntimeId(new int[65]), "SEND_SELF_TEST_CACHED_SECURITY_METADATA");
            var reads = 0;
            Need(WaitForWrittenInput(() => ++reads == 2 ? "UNCHANGED" : "OTHER", () => { }) == "UNCHANGED" && reads == 2,
                "SEND_SELF_TEST_ASYNC_READBACK");
            reads = 0;
            Need(WaitForWrittenInput(() => { reads++; return "OTHER"; }, () => { }) == "OTHER" && reads == 6,
                "SEND_SELF_TEST_NO_WRITE_RETRY");
            var readyNotice = Consent.ForNotice("D234567", ReadyNotice);
            Need(!readyNotice.TryConsume(PowerSiBusyNotice) && !readyNotice.TryConsume(ReadyNotice), "SEND_SELF_TEST_NOTICE_EXACT");
            var onceNotice = Consent.ForNotice("D234567", ReadyNotice);
            Need(onceNotice.TryConsume(onceNotice.NoticeText) && !onceNotice.TryConsume(onceNotice.NoticeText), "SEND_SELF_TEST_NOTICE_ONCE");
            const string marker = "D234567";
            var observedPointer = new NativeMethods.ScreenPoint { X = -10, Y = 20 };
            Need(PointerFailure(true, observedPointer, new Point(-10, 20)) == null &&
                PointerFailure(false, observedPointer, new Point(-10, 20)) == "CLICK_POINTER_UNAVAILABLE" &&
                PointerFailure(true, observedPointer, new Point(-10, 21)) == "CLICK_POINTER_CHANGED",
                "SEND_SELF_TEST_POINTER_DIAGNOSIS");
            var completedClick = new MouseClickInput.Result { Returned = true, Inserted = 2, DownInserted = 1,
                ReleaseAttempted = true, ReleaseInserted = 1 };
            Need(IsCleanCompletion(true, completedClick, "EMPTY", 1, true, true, true, false), "SEND_SELF_TEST_CLEAN");
            Need(!IsCleanCompletion(true, completedClick, "UNCHANGED", 1, true, true, true, false) &&
                !IsCleanCompletion(true, completedClick, "EMPTY", 1, true, true, true, true) &&
                !IsCleanCompletion(true, completedClick, "EMPTY", 1, true, false, true, false), "SEND_SELF_TEST_UNCLEAN");
            completedClick.ReleaseInserted = 0;
            Need(!IsCleanCompletion(true, completedClick, "EMPTY", 1, true, true, true, false), "SEND_SELF_TEST_RELEASE_REQUIRED");
            Need(IsValidMarker(marker) && IsValidMarker("D999999"), "SEND_SELF_TEST_MARKER");
            var unprepared = new Consent(marker, true, true);
            Need(!unprepared.IsAuthorizedReply(null) && !unprepared.IsAuthorizedReply(marker) &&
                !unprepared.TryConsume(null) && unprepared.Cancelled && !unprepared.TryCommitMove(), "STATUS_SELF_TEST_UNPREPARED");
            var statusConsent = new Consent(marker, true, true);
            Need(statusConsent.TryClaimRoundTrip(), "STATUS_SELF_TEST_CLAIM");
            var statusReply = statusConsent.PrepareReply();
            Need(PcStatusReport.IsReply(statusReply, marker) && !IsValidMarker(statusReply) &&
                statusConsent.IsAuthorizedReply(statusReply) && !statusConsent.IsAuthorizedReply(statusReply + " ") &&
                !statusConsent.TryConsume(statusReply + " ") && statusConsent.Cancelled && !statusConsent.TryConsume(statusReply),
                "STATUS_SELF_TEST_EXACT_BINDING");
            var statusOnce = new Consent(marker, true, true);
            Need(statusOnce.TryClaimRoundTrip(), "STATUS_SELF_TEST_CLAIM");
            statusReply = statusOnce.PrepareReply();
            bool refused = false;
            try { statusOnce.PrepareReply(); } catch (MonitorException) { refused = true; }
            Need(refused && statusOnce.TryConsume(statusReply) && statusOnce.TryCommitMove() && statusOnce.TryCommitWrite() &&
                statusOnce.TryCommit() && !statusOnce.TryConsume(statusReply), "STATUS_SELF_TEST_ONE_REPLY");
            foreach (var prepareFirst in new[] { false, true })
            {
                var cancelledStatus = new Consent(marker, true, true);
                Need(cancelledStatus.TryClaimRoundTrip(), "STATUS_SELF_TEST_CLAIM");
                var text = prepareFirst ? cancelledStatus.PrepareReply() : null;
                cancelledStatus.Cancel();
                refused = false;
                try { cancelledStatus.PrepareReply(); } catch (MonitorException) { refused = true; }
                Need(refused && !cancelledStatus.TryConsume(text) && !cancelledStatus.TryCommitMove(), "STATUS_SELF_TEST_CANCELLED");
            }
            var endpoint = new SlaveEndpoint(System.Net.IPAddress.Loopback, 1, new string('0', 64), Convert.ToBase64String(new byte[32]));
            var pendingNotice = new Consent(marker, true, true, endpoint, null, true);
            Need(pendingNotice.TryClaimRoundTrip(), "NOTICE_SELFTEST_CLAIM");
            pendingNotice.BindCommand("pwrsi");
            var busyNotice = pendingNotice.CreateProgressNotice(PowerSiBusyNotice);
            Need(busyNotice.TryConsume(PowerSiBusyNotice) && busyNotice.TryCommitMove() && busyNotice.TryCommitWrite() &&
                pendingNotice.PendingWrite && !pendingNotice.Attempted, "NOTICE_SELFTEST_PENDING_AGGREGATE");
            pendingNotice.Cancel();
            Need(busyNotice.Cancelled && !busyNotice.TryCommit() && pendingNotice.PendingWrite, "NOTICE_SELFTEST_CANCEL_BEFORE_CLICK");
            reads = 0;
            try
            {
                WaitForWrittenInput(() => { reads++; return "OTHER"; }, () => { throw new MonitorException("SEND_CANCELLED", "stopped"); });
                throw new InvalidOperationException("Cancelled readback continued.");
            }
            catch (MonitorException) { Need(reads == 0, "NOTICE_SELFTEST_READBACK_CANCEL"); }
            var multipartPayloads = new[]
            {
                "PWRSI REPORT " + marker + " | PART 001/002\r\nfirst",
                "PWRSI REPORT " + marker + " | PART 002/002\r\nsecond"
            };
            var multipart = new Consent(marker, true, true, endpoint, null, true);
            Need(multipart.TryClaimRoundTrip(), "MULTIPART_SELF_TEST_CLAIM");
            multipart.BindCommand("pwrsi");
            multipart.BindPreparedReplies(multipartPayloads);
            var firstPart = multipart.GetPreparedPart(0);
            var secondPart = multipart.GetPreparedPart(1);
            Need(multipart.PreparedReplyCount == 2 && ReferenceEquals(firstPart, multipart) &&
                firstPart.IsAuthorizedReply(multipartPayloads[0]) && secondPart.IsAuthorizedReply(multipartPayloads[1]) &&
                !secondPart.IsAuthorizedReply(multipartPayloads[0]) &&
                firstPart.TryConsume(multipartPayloads[0]) && firstPart.TryCommitMove() && firstPart.TryCommitWrite() && firstPart.TryCommit() &&
                secondPart.TryConsume(multipartPayloads[1]) && secondPart.TryCommitMove() && secondPart.TryCommitWrite() && secondPart.TryCommit() &&
                multipart.CursorMoveAttempted && multipart.WriteAttempted && multipart.Attempted && !multipart.PendingWrite,
                "MULTIPART_SELF_TEST_SEQUENCE");
            try { multipart.CommitPreparedOutput(); throw new InvalidOperationException("Unconfirmed output was committed."); }
            catch (MonitorException) { }
            var cleanPart = new Outcome("SENT", "NONE", "self-test", true);
            multipart.RecordPreparedPartOutcome(0, cleanPart);
            try { multipart.CommitPreparedOutput(); throw new InvalidOperationException("Partially confirmed output was committed."); }
            catch (MonitorException) { }
            try { multipart.RecordPreparedPartOutcome(1, new Outcome("UNKNOWN", "FAILED", "self-test", false));
                throw new InvalidOperationException("Uncertain part was confirmed."); }
            catch (MonitorException) { }
            multipart.RecordPreparedPartOutcome(1, cleanPart);
            Need(multipart.CommitPreparedOutput(), "MULTIPART_SELF_TEST_HISTORY_COMMIT");
            try { multipart.CommitPreparedOutput(); throw new InvalidOperationException("Output was committed twice."); }
            catch (MonitorException) { }
            multipart.Cancel();

            var partial = new Consent(marker, true, true, endpoint, null, true);
            Need(partial.TryClaimRoundTrip(), "MULTIPART_SELF_TEST_PARTIAL_CLAIM");
            partial.BindCommand("pwrsi");
            partial.BindPreparedReplies(multipartPayloads);
            firstPart = partial.GetPreparedPart(0);
            secondPart = partial.GetPreparedPart(1);
            Need(firstPart.TryConsume(multipartPayloads[0]) && firstPart.TryCommitMove() && firstPart.TryCommitWrite() && firstPart.TryCommit() &&
                secondPart.TryConsume(multipartPayloads[1]) && secondPart.TryCommitMove() && secondPart.TryCommitWrite(),
                "MULTIPART_SELF_TEST_PARTIAL_WRITE");
            partial.Cancel();
            Need(partial.Cancelled && partial.Attempted && partial.PendingWrite && !secondPart.TryCommit(),
                "MULTIPART_SELF_TEST_ABORT_REMAINING");
            try { partial.CommitPreparedOutput(); throw new InvalidOperationException("Cancelled output was committed."); }
            catch (MonitorException) { }
            foreach (var invalid in new[] { null, "", "D123456", "D23456", "D2345678", "d234567", "D23 567" })
            {
                try { new Consent(invalid, true); }
                catch (MonitorException) { continue; }
                throw new InvalidOperationException("A malformed supervised marker was accepted.");
            }
            var cancelled = new Consent(marker, true);
            Need(!cancelled.Cancel() && cancelled.Cancelled && !cancelled.TryConsume(marker) && !cancelled.TryCommitMove() && !cancelled.TryCommitWrite() &&
                !cancelled.TryCommit() && !cancelled.WriteAttempted && !cancelled.Attempted,
                "SEND_SELF_TEST_CANCEL");
            var consumed = new Consent(marker, true);
            Need(consumed.TryConsume(marker) && !consumed.TryConsume(marker) && !consumed.TryCommitWrite() && !consumed.TryCommit() && !consumed.Cancel() &&
                !consumed.TryCommitMove() && !consumed.TryCommitWrite() && !consumed.TryCommit(), "SEND_SELF_TEST_CONSUME");
            var moved = new Consent(marker, true);
            Need(!moved.TryCommitMove() && moved.TryConsume(marker) && moved.TryCommitMove() && !moved.TryCommitMove() &&
                moved.CursorMoveAttempted && !moved.WriteAttempted && !moved.Cancel() && !moved.TryCommitWrite() && !moved.TryCommit(),
                "SEND_SELF_TEST_MOVE_CANCEL");
            var written = new Consent(marker, true);
            Need(!written.TryCommitWrite() && written.TryConsume(marker) && written.TryCommitMove() && written.TryCommitWrite() && !written.TryCommitWrite() &&
                written.WriteAttempted && !written.Attempted && !written.Cancel() && written.Cancelled && written.WriteAttempted &&
                !written.TryCommit() && !written.TryCommitWrite(), "SEND_SELF_TEST_WRITE_CANCEL");
            var committed = new Consent(marker, true);
            Need(!committed.TryCommit() && committed.TryConsume(marker) && committed.TryCommitMove() && committed.TryCommitWrite() && committed.TryCommit() &&
                !committed.TryCommit() && !committed.TryCommitWrite() && committed.Attempted && committed.WriteAttempted &&
                committed.Cancel() && committed.Cancelled && committed.Attempted && committed.WriteAttempted && !committed.TryConsume(marker), "SEND_SELF_TEST_COMMIT");
            var mismatch = new Consent(marker, true);
            Need(!mismatch.TryConsume("D999999") && !mismatch.TryConsume(marker) && !mismatch.TryCommitWrite() && !mismatch.TryCommit(), "SEND_SELF_TEST_MISMATCH");
            Need(ClassifyInput(null, marker) == "UNAVAILABLE" && ClassifyInput("", marker) == "EMPTY" &&
                ClassifyInput(marker, marker) == "UNCHANGED" && ClassifyInput("private", marker) == "OTHER" &&
                ClassifyInput(" ", marker) == "OTHER" && ClassifyInput("\r\n", marker) == "OTHER", "SEND_SELF_TEST_INPUT");
            Need(SendCenter(new Rect(-100, 20, 31, 29)) == new Point(-85, 34), "SEND_SELF_TEST_CENTER");
            foreach (var bounds in new[] { Rect.Empty, new Rect(0, 0, 0, 10), new Rect(0.1, 0.1, 0.1, 0.1),
                new Rect(double.PositiveInfinity, 0, 10, 10), new Rect((double)int.MaxValue + 1, 0, 10, 10) })
            {
                try { SendCenter(bounds); }
                catch (MonitorException) { continue; }
                throw new InvalidOperationException("Unsafe Send bounds were accepted.");
            }
            try { new Consent(marker, false); }
            catch (MonitorException) { return; }
            throw new InvalidOperationException("An unconfirmed supervised send was accepted.");
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new MonitorException(reason, "Supervised send test rejected: " + reason + ".");
        }
    }
}
