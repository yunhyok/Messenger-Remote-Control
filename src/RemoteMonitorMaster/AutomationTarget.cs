using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace RemoteMonitorMaster
{
    internal sealed class AutomationTarget
    {
        private static readonly Regex MetadataCamelBoundary = new Regex(
            @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex ExcludedMessageMetadata = new Regex(
            @"(?:\A|[^A-Za-z0-9])(?:file|attach|download)|파일|첨부",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly IntPtr window;
        private readonly ProcessIdentity process;
        private readonly AutomationElement originalRoot;
        private readonly ElementIdentity windowRoot;
        private readonly ElementIdentity chatContainer;
        private readonly ElementIdentity composer;
        private readonly ElementIdentity header;
        private readonly ElementIdentity transcript;
        private readonly ElementIdentity input;
        private readonly ElementIdentity send;
        private readonly object writeGate = new object();
        private volatile bool writesCancelled;
        private int invokeCommitState;

        private AutomationTarget(
            IntPtr window,
            ProcessIdentity process,
            AutomationElement originalRoot,
            ElementIdentity windowRoot,
            ElementIdentity chatContainer,
            ElementIdentity composer,
            ElementIdentity header,
            ElementIdentity transcript,
            ElementIdentity input,
            ElementIdentity send)
        {
            this.window = window;
            this.process = process;
            this.originalRoot = originalRoot;
            this.windowRoot = windowRoot;
            this.chatContainer = chatContainer;
            this.composer = composer;
            this.header = header;
            this.transcript = transcript;
            this.input = input;
            this.send = send;
        }

        // Legacy self-test-only diagnostic path: unreachable from Program/MasterHubForm; only Core.cs exercises it.
        // RejectAutomationInDiagnosticBuild below throws unconditionally, so no bind, input or send can follow.
        public static AutomationTarget Bind(IntPtr window, AuditLog log)
        {
            AppInfo.RejectAutomationInDiagnosticBuild();
            RequireMta();
            if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
            {
                throw new MonitorException("BIND_NO_WINDOW", "The foreground window no longer exists.");
            }

            var process = ProcessIdentity.Capture(window);
            process.Log(log);
            AutomationElement root;
            ControlType rootType;
            try
            {
                root = AutomationElement.FromHandle(window);
                rootType = root == null ? null : root.Current.ControlType;
                log.Write("INFO", "UIA_ROOT_PROBE",
                    AuditLog.Field("root_available", root != null),
                    AuditLog.Field("expected_pid", process.ProcessId),
                    AuditLog.Field("captured_hwnd", FormatHandle(window)),
                    AuditLog.Field("control_type", rootType == null ? "<none>" : rootType.ProgrammaticName));
            }
            catch (Exception ex)
            {
                throw new MonitorException("UNSUPPORTED_UIA_ROOT_READ_FAILED",
                    "UIA root: <read/audit failed>" + Environment.NewLine + process.Summary + Environment.NewLine +
                    "No input or send was attempted.", ex);
            }

            if (root != null)
            {
                try
                {
                    log.Write("INFO", "UIA_ROOT_PROVIDER_DETAILS",
                        AuditLog.Field("process_id", root.Current.ProcessId),
                        AuditLog.Field("native_hwnd", FormatHandle(new IntPtr(root.Current.NativeWindowHandle))),
                        AuditLog.Field("framework_id_fingerprint", log.Fingerprint(root.Current.FrameworkId)));
                }
                catch (Exception ex)
                {
                    // Preserve the root/type evidence even when optional provider details are unavailable.
                    log.WriteException("UIA_ROOT_PROVIDER_PROBE_FAILED", ex);
                }
            }

            var rootRejection = RootRejectionReason(root != null, rootType);
            if (rootRejection != null)
            {
                if (root != null)
                {
                    try
                    {
                        ElementIdentity.Capture(root).Log(log, "unsupported_window_root");
                    }
                    catch (Exception ex)
                    {
                        log.WriteException("UIA_ROOT_IDENTITY_PROBE_FAILED", ex);
                    }

                    try
                    {
                        LogTreeSnapshot(root.FindAll(TreeScope.Descendants, Condition.TrueCondition), log);
                    }
                    catch (Exception ex)
                    {
                        log.WriteException("UIA_ROOT_TREE_PROBE_FAILED", ex);
                    }
                }

                throw new MonitorException(rootRejection,
                    "UIA root: " + (rootType == null ? "<none>" : rootType.ProgrammaticName) + Environment.NewLine + process.Summary + Environment.NewLine +
                    "Bind is blocked; no input or send was attempted. Use Open Log Folder to collect the diagnostic log.");
            }

            var rootIdentity = ElementIdentity.Capture(root);
            rootIdentity.Log(log, "window_root");
            if (rootIdentity.NameLength == 0)
            {
                throw new MonitorException("UNSUPPORTED_WINDOW_NAME_EMPTY", "The foreground UIA Window has no stable Name.");
            }

            if (root.Current.IsOffscreen)
            {
                throw new MonitorException("UNSUPPORTED_WINDOW_OFFSCREEN", "The foreground UIA Window is reported offscreen.");
            }

            var live = Discover(root, process.ProcessId, log, "BIND", true);
            EnsureElements(live, process.ProcessId);
            var header = ElementIdentity.Capture(live.Header);
            if (header.NameLength == 0)
            {
                throw new MonitorException("UNSUPPORTED_HEADER_NAME_EMPTY", "The chat container has no stable UIA header Name.");
            }

            var target = new AutomationTarget(
                window,
                process,
                root,
                rootIdentity,
                ElementIdentity.Capture(live.ChatContainer),
                ElementIdentity.Capture(live.Composer),
                header,
                ElementIdentity.Capture(live.Transcript),
                ElementIdentity.Capture(live.Input),
                ElementIdentity.Capture(live.Send));

            target.Validate(log, "BIND_FINAL");
            target.chatContainer.Log(log, "chat_container");
            target.composer.Log(log, "composer");
            header.Log(log, "chat_header");
            target.transcript.Log(log, "transcript");
            target.input.Log(log, "input");
            target.send.Log(log, "send");
            log.Write("INFO", "BIND_OK", AuditLog.Field("hwnd", FormatHandle(window)));
            return target;
        }

        internal static string RootRejectionReason(bool rootAvailable, ControlType rootType)
        {
            if (!rootAvailable)
            {
                return "UNSUPPORTED_UIA_ROOT_MISSING";
            }

            return rootType == ControlType.Window ? null : "UNSUPPORTED_UIA_ROOT_TYPE";
        }

        public IList<string> ReadPingTokens(AuditLog log, string phase, bool includeOffscreen = false)
        {
            var live = Validate(log, phase + "_PRE_READ");
            log.Write("INFO", "READ_BEGIN", AuditLog.Field("phase", phase));

            try
            {
                int exposedMessageCount;
                int offscreenMessageCount;
                var messages = StructuredMessages(live.Transcript, process.ProcessId, includeOffscreen, out exposedMessageCount, out offscreenMessageCount);
                if (exposedMessageCount == 0)
                {
                    throw new MonitorException(
                        "UNSUPPORTED_MESSAGE_STRUCTURE",
                        "No individually exposed text-message elements remain in the transcript.");
                }

                var tokens = new List<string>();
                var terminalNewlineCount = 0;
                var longMessageCount = 0;
                foreach (var message in messages)
                {
                    object patternObject;
                    if (!message.TryGetCurrentPattern(TextPattern.Pattern, out patternObject))
                    {
                        throw new MonitorException("UNSUPPORTED_MESSAGE_TEXT_PATTERN", "A message lost TextPattern support.");
                    }

                    var text = ((TextPattern)patternObject).DocumentRange.GetText(513) ?? string.Empty;
                    if (text.EndsWith("\r", StringComparison.Ordinal) || text.EndsWith("\n", StringComparison.Ordinal))
                    {
                        terminalNewlineCount++;
                    }
                    if (text.Length > 512)
                    {
                        longMessageCount++;
                        continue;
                    }

                    string token;
                    if (Protocol.TryParsePing(text, out token))
                    {
                        tokens.Add(token);
                    }
                }

                log.Write("INFO", "READ_OK",
                    AuditLog.Field("phase", phase),
                    AuditLog.Field("exposed_message_elements", exposedMessageCount),
                    AuditLog.Field("offscreen_message_elements", offscreenMessageCount),
                    AuditLog.Field("message_elements", messages.Count),
                    AuditLog.Field("include_offscreen", includeOffscreen),
                    AuditLog.Field("terminal_newline_messages", terminalNewlineCount),
                    AuditLog.Field("long_messages_skipped", longMessageCount),
                    AuditLog.Field("ping_candidates", tokens.Count));
                return tokens;
            }
            catch (MonitorException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new MonitorException("READ_UIA_FAILED", "UIA could not read structured message text.", ex);
            }
        }

        // Legacy self-test-only diagnostic path: unreachable from Program/MasterHubForm; only Core.cs exercises it.
        // RejectAutomationInDiagnosticBuild below throws unconditionally, so no automatic reply is ever sent.
        public void SendPong(AuditLog log, string token, Func<bool> stopRequested)
        {
            AppInfo.RejectAutomationInDiagnosticBuild();
            var response = Protocol.Pong(token);
            var valueMayBePresent = false;

            try
            {
                StopIfRequested(stopRequested);
                var beforeRead = Validate(log, "WRITE_PRE_INPUT_READ");
                StopIfRequested(stopRequested);
                EnsureTargetIsBackground();
                var valueBefore = ReadInputValue(beforeRead.Input);
                log.Write("INFO", "INPUT_READ_OK", AuditLog.Field("value_length", valueBefore.Length));
                if (valueBefore.Length != 0)
                {
                    throw new MonitorException("INPUT_NOT_EMPTY", "The bound input contains a user draft; it was not overwritten.");
                }

                StopIfRequested(stopRequested);
                var beforeSet = Validate(log, "WRITE_PRE_SET_VALUE");
                StopIfRequested(stopRequested);
                EnsureTargetIsBackground();
                try
                {
                    object valuePatternObject;
                    if (!beforeSet.Input.TryGetCurrentPattern(ValuePattern.Pattern, out valuePatternObject))
                    {
                        throw new MonitorException("UNSUPPORTED_INPUT_VALUE_PATTERN", "The input lost ValuePattern support.");
                    }

                    lock (writeGate)
                    {
                        StopIfRequested(stopRequested);
                        EnsureTargetIsBackground();
                        if (ReadInputValue(beforeSet.Input).Length != 0)
                        {
                            throw new MonitorException("INPUT_CHANGED_BEFORE_SET", "The input changed before SetValue; it was not overwritten.");
                        }

                        valueMayBePresent = true;
                        ((ValuePattern)valuePatternObject).SetValue(response);
                    }

                    log.Write("INFO", "WRITE_SET_VALUE_OK", AuditLog.Field("token_sha256", TokenStore.Hash(token)));
                }
                catch (MonitorException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new MonitorException("WRITE_SET_VALUE_FAILED", "ValuePattern.SetValue failed.", ex);
                }

                StopIfRequested(stopRequested);
                var beforeVerify = Validate(log, "WRITE_PRE_VERIFY_READ");
                StopIfRequested(stopRequested);
                EnsureTargetIsBackground();
                var valueAfter = ReadInputValue(beforeVerify.Input);
                if (!string.Equals(valueAfter, response, StringComparison.Ordinal))
                {
                    throw new MonitorException("WRITE_VALUE_MISMATCH", "The input value changed before send; Invoke was not called.");
                }

                StopIfRequested(stopRequested);
                var beforeInvoke = Validate(log, "WRITE_PRE_INVOKE");
                StopIfRequested(stopRequested);
                EnsureTargetIsBackground();
                if (!beforeInvoke.Send.Current.IsEnabled)
                {
                    throw new MonitorException("SEND_NOT_ENABLED", "The send control did not become enabled after writing the response.");
                }

                try
                {
                    object invokePatternObject;
                    if (!beforeInvoke.Send.TryGetCurrentPattern(InvokePattern.Pattern, out invokePatternObject))
                    {
                        throw new MonitorException("UNSUPPORTED_SEND_INVOKE_PATTERN", "The send control lost InvokePattern support.");
                    }

                    lock (writeGate)
                    {
                        StopIfRequested(stopRequested);
                        EnsureTargetIsBackground();
                        if (!beforeInvoke.Send.Current.IsEnabled)
                        {
                            throw new MonitorException("SEND_DISABLED_BEFORE_INVOKE", "The send control became disabled; Invoke was not called.");
                        }

                        if (!string.Equals(ReadInputValue(beforeInvoke.Input), response, StringComparison.Ordinal))
                        {
                            throw new MonitorException("WRITE_VALUE_CHANGED_BEFORE_INVOKE", "The input changed immediately before send; Invoke was not called.");
                        }

                        if (Interlocked.CompareExchange(ref invokeCommitState, 2, 0) != 0)
                        {
                            throw new MonitorException("DISARM_REQUESTED", "Disarm won the send commit race; Invoke was not called.");
                        }

                        try
                        {
                            ((InvokePattern)invokePatternObject).Invoke();
                        }
                        finally
                        {
                            Interlocked.CompareExchange(ref invokeCommitState, 0, 2);
                        }
                    }

                    log.Write("INFO", "SEND_INVOKED", AuditLog.Field("token_sha256", TokenStore.Hash(token)));
                }
                catch (MonitorException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new MonitorException("WRITE_INVOKE_FAILED", "InvokePattern.Invoke failed; the token remains reserved.", ex);
                }
            }
            catch (Exception ex)
            {
                if (valueMayBePresent && !TryClearGeneratedDraft(log, response, stopRequested))
                {
                    throw new MonitorException(
                        "AUTOMATION_DRAFT_MAY_REMAIN",
                        "A generated PONG may remain in the bound composer; inspect it manually before using KI-Messenger.",
                        ex);
                }

                throw;
            }
        }

        private bool TryClearGeneratedDraft(AuditLog log, string response, Func<bool> stopRequested)
        {
            try
            {
                StopIfRequested(stopRequested);
                var live = Validate(log, "CLEANUP_PRE_CLEAR");
                StopIfRequested(stopRequested);
                EnsureTargetIsBackground();
                var current = ReadInputValue(live.Input);
                if (current.Length == 0)
                {
                    return true;
                }

                if (!string.Equals(current, response, StringComparison.Ordinal))
                {
                    throw new MonitorException("CLEANUP_VALUE_CHANGED", "The composer no longer contains the exact generated response.");
                }

                object valuePatternObject;
                if (!live.Input.TryGetCurrentPattern(ValuePattern.Pattern, out valuePatternObject))
                {
                    throw new MonitorException("CLEANUP_VALUE_PATTERN_MISSING", "The composer lost ValuePattern support.");
                }

                lock (writeGate)
                {
                    StopIfRequested(stopRequested);
                    EnsureTargetIsBackground();
                    if (!string.Equals(ReadInputValue(live.Input), response, StringComparison.Ordinal))
                    {
                        throw new MonitorException("CLEANUP_VALUE_CHANGED", "The composer changed before cleanup.");
                    }

                    ((ValuePattern)valuePatternObject).SetValue(string.Empty);
                }

                var afterClear = Validate(log, "CLEANUP_POST_CLEAR");
                if (ReadInputValue(afterClear.Input).Length != 0)
                {
                    throw new MonitorException("CLEANUP_NOT_EMPTY", "The provider did not confirm an empty composer after cleanup.");
                }

                log.Write("WARN", "AUTOMATION_DRAFT_CLEARED");
                return true;
            }
            catch (Exception cleanupException)
            {
                try
                {
                    log.WriteException("AUTOMATION_DRAFT_CLEANUP_FAILED", cleanupException);
                }
                catch
                {
                    // The caller raises the prominent fail-closed reason even if the log is unavailable.
                }

                return false;
            }
        }

        public bool CancelWrites()
        {
            writesCancelled = true;
            return Interlocked.CompareExchange(ref invokeCommitState, 1, 0) == 2;
        }

        public string Summary
        {
            get
            {
                return process.Summary + Environment.NewLine +
                    "Chat header Name: <redacted>, length " + header.NameLength.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                    "Transcript: " + transcript.ControlType + Environment.NewLine +
                    "Input: " + input.ControlType + Environment.NewLine +
                    "Send: " + send.ControlType + " / Name=" + send.SafeName;
            }
        }

        private LiveElements Validate(AuditLog log, string phase)
        {
            RequireMta();
            log.Write("INFO", "REVALIDATE_BEGIN", AuditLog.Field("phase", phase));
            try
            {
                if (!NativeMethods.IsWindow(window))
                {
                    throw new MonitorException("TARGET_HWND_GONE", "The bound HWND no longer exists.");
                }

                var currentProcess = ProcessIdentity.Capture(window);
                if (!process.Equals(currentProcess))
                {
                    throw new MonitorException("TARGET_PROCESS_CHANGED", "PID, process start, path, version, file metadata, signature, HWND, or class changed.");
                }

                var root = AutomationElement.FromHandle(window);
                if (root == null || !Automation.Compare(originalRoot, root) || !windowRoot.Equals(ElementIdentity.Capture(root)))
                {
                    throw new MonitorException("TARGET_WINDOW_CHANGED", "The original UIA window identity changed or was recreated.");
                }

                // ponytail: a full UIA scan is deliberately strict; replace it with verified app-specific IDs only if profiling proves it too slow.
                if (root.Current.IsOffscreen)
                {
                    throw new MonitorException("TARGET_WINDOW_OFFSCREEN", "The bound UIA Window is reported offscreen.");
                }

                var live = Discover(root, process.ProcessId, log, phase, false);
                EnsureElements(live, process.ProcessId);
                if (!chatContainer.Matches(ElementIdentity.Capture(live.ChatContainer), compareName: false))
                {
                    throw new MonitorException("TARGET_CHAT_CONTAINER_CHANGED", "The semantic chat container UIA element changed.");
                }

                if (!composer.Matches(ElementIdentity.Capture(live.Composer), compareName: false))
                {
                    throw new MonitorException("TARGET_COMPOSER_CHANGED", "The input/send composer UIA element changed.");
                }

                if (!header.Equals(ElementIdentity.Capture(live.Header)))
                {
                    throw new MonitorException("TARGET_HEADER_CHANGED", "The unique chat header/container UIA element changed.");
                }

                if (!transcript.Matches(ElementIdentity.Capture(live.Transcript), compareName: false))
                {
                    throw new MonitorException("TARGET_TRANSCRIPT_CHANGED", "The unique transcript UIA element changed.");
                }

                if (!input.Matches(ElementIdentity.Capture(live.Input), compareName: false))
                {
                    throw new MonitorException("TARGET_INPUT_CHANGED", "The unique input UIA element changed.");
                }

                if (!send.Equals(ElementIdentity.Capture(live.Send)))
                {
                    throw new MonitorException("TARGET_SEND_CHANGED", "The unique send UIA element changed.");
                }

                log.Write("INFO", "REVALIDATE_OK",
                    AuditLog.Field("phase", phase),
                    AuditLog.Field("pid", process.ProcessId),
                    AuditLog.Field("hwnd", FormatHandle(window)));
                return live;
            }
            catch (MonitorException ex)
            {
                log.WriteException("REVALIDATE_FAILED", ex,
                    AuditLog.Field("phase", phase),
                    AuditLog.Field("reason", ex.ReasonCode));
                throw;
            }
            catch (Exception ex)
            {
                log.WriteException("REVALIDATE_FAILED", ex,
                    AuditLog.Field("phase", phase),
                    AuditLog.Field("reason", "TARGET_UIA_UNAVAILABLE"));
                throw new MonitorException("TARGET_UIA_UNAVAILABLE", "The bound UIA tree could not be reacquired.", ex);
            }
        }

        private static LiveElements Discover(
            AutomationElement root,
            int expectedProcessId,
            AuditLog log,
            string phase,
            bool logDetails)
        {
            AutomationElementCollection all;
            try
            {
                all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            }
            catch (Exception ex)
            {
                throw new MonitorException("UNSUPPORTED_UIA_TREE", "The foreground window did not expose a readable UIA tree.", ex);
            }

            if (logDetails)
            {
                LogTreeSnapshot(all, log);
            }

            var inputs = new List<AutomationElement>();
            var sends = new List<AutomationElement>();
            var invokeButtons = new List<AutomationElement>();
            var transcripts = new List<AutomationElement>();
            var transcriptSurfaces = new List<AutomationElement>();

            foreach (AutomationElement element in all)
            {
                object pattern;
                var type = element.Current.ControlType;

                if (type == ControlType.Edit &&
                    element.Current.IsEnabled &&
                    !element.Current.IsOffscreen &&
                    element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) &&
                    !((ValuePattern)pattern).Current.IsReadOnly)
                {
                    inputs.Add(element);
                }

                if (type == ControlType.Button &&
                    !element.Current.IsOffscreen &&
                    element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                {
                    invokeButtons.Add(element);
                    if (IsSendLabel(element.Current.Name))
                    {
                        sends.Add(element);
                    }
                }

                if ((type == ControlType.List || type == ControlType.Document) && !element.Current.IsOffscreen)
                {
                    transcriptSurfaces.Add(element);
                    int exposedMessageCount;
                    int offscreenMessageCount;
                    if (StructuredMessages(element, expectedProcessId, true, out exposedMessageCount, out offscreenMessageCount).Count > 0)
                    {
                        transcripts.Add(element);
                    }
                }
            }

            log.Write("INFO", "UIA_DISCOVERY",
                AuditLog.Field("phase", phase),
                AuditLog.Field("writable_edits", inputs.Count),
                AuditLog.Field("invoke_buttons", invokeButtons.Count),
                AuditLog.Field("send_matches", sends.Count),
                AuditLog.Field("transcript_surfaces", transcriptSurfaces.Count),
                AuditLog.Field("structured_transcripts", transcripts.Count));

            if (logDetails)
            {
                LogCandidates(log, inputs, "input_candidate");
                LogCandidates(log, invokeButtons, "invoke_candidate");
                LogCandidates(log, transcriptSurfaces, "transcript_surface");
            }

            var input = SelectUnique(inputs, IsInputSemantic, "UNSUPPORTED_INPUT_NOT_UNIQUE");
            var send = SelectExactlyOne(sends, "UNSUPPORTED_SEND_NOT_UNIQUE");
            var transcript = SelectUnique(transcripts, IsTranscriptSemantic, "UNSUPPORTED_TRANSCRIPT_NOT_UNIQUE");
            var chatContainer = SelectChatContainer(root, transcript, input, send);
            var composer = SelectComposerContainer(chatContainer, input, send);
            var header = SelectConversationHeader(chatContainer, log, logDetails);
            return new LiveElements(chatContainer, composer, header, transcript, input, send);
        }

        private static void LogTreeSnapshot(AutomationElementCollection all, AuditLog log)
        {
            // ponytail: cap the read-only diagnostic snapshot; raise only if a real failed Bind needs more evidence.
            log.Write("INFO", "UIA_TREE_SNAPSHOT", AuditLog.Field("total_nodes", all.Count),
                AuditLog.Field("limit", 200), AuditLog.Field("truncated", all.Count > 200));
            foreach (AutomationElement element in all.Cast<AutomationElement>().Take(200))
            {
                try
                {
                    var parent = TreeWalker.RawViewWalker.GetParent(element);
                    var identity = ElementIdentity.Capture(element);
                    identity.Log(log, "tree_node");
                    log.Write("INFO", "UIA_TREE_NODE",
                        AuditLog.Field("runtime_id", identity.RuntimeId),
                        AuditLog.Field("parent_runtime_id", parent == null ? "" : RuntimeKey(parent)),
                        AuditLog.Field("enabled", element.Current.IsEnabled),
                        AuditLog.Field("offscreen", element.Current.IsOffscreen),
                        AuditLog.Field("input_semantic", IsInputSemantic(element)),
                        AuditLog.Field("transcript_semantic", IsTranscriptSemantic(element)),
                        AuditLog.Field("chat_container_semantic", IsChatContainerSemantic(element)),
                        AuditLog.Field("composer_semantic", IsComposerSemantic(element)),
                        AuditLog.Field("text_envelope_semantic", IsTextMessageEnvelope(element)));
                }
                catch (Exception ex)
                {
                    log.WriteException("UIA_TREE_NODE_UNAVAILABLE", ex);
                }
            }
        }

        private static IList<AutomationElement> StructuredMessages(AutomationElement transcript, int expectedProcessId, bool includeOffscreen, out int exposedMessageCount, out int offscreenMessageCount)
        {
            exposedMessageCount = 0;
            offscreenMessageCount = 0;
            var result = new List<AutomationElement>();
            var leavesByEnvelope = new Dictionary<string, List<AutomationElement>>(StringComparer.Ordinal);
            var envelopes = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
            var descendants = transcript.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement element in descendants)
            {
                var type = element.Current.ControlType;
                object pattern;
                if ((type != ControlType.Text && type != ControlType.Document) ||
                    !element.TryGetCurrentPattern(TextPattern.Pattern, out pattern) ||
                    HasTextPatternDescendant(element))
                {
                    continue;
                }

                var envelope = FindMessageEnvelope(element, transcript);
                if (envelope == null ||
                    envelope.Current.ProcessId != expectedProcessId ||
                    !IsTextMessageEnvelope(envelope))
                {
                    continue;
                }

                var key = RuntimeKey(envelope);
                List<AutomationElement> leaves;
                if (!leavesByEnvelope.TryGetValue(key, out leaves))
                {
                    leaves = new List<AutomationElement>();
                    leavesByEnvelope.Add(key, leaves);
                    envelopes.Add(key, envelope);
                }

                leaves.Add(element);
            }

            foreach (var pair in leavesByEnvelope)
            {
                var envelope = envelopes[pair.Key];
                // Count hidden/foreign bodies too: hiding a second body must not make an ambiguous envelope eligible.
                var body = pair.Value[0];
                if (pair.Value.Count == 1 &&
                    body.Current.ProcessId == expectedProcessId && !HasFileOrActionContent(envelope))
                {
                    exposedMessageCount++;
                    var visible = !body.Current.IsOffscreen && !envelope.Current.IsOffscreen;
                    if (!visible)
                    {
                        offscreenMessageCount++;
                    }

                    if (IsMessageEligible(pair.Value.Count, visible, includeOffscreen))
                    {
                        result.Add(body);
                    }
                }
            }

            return result;
        }

        internal static bool IsMessageEligible(int bodyCount, bool visible, bool includeOffscreen)
        {
            return bodyCount == 1 && (visible || includeOffscreen);
        }

        private static bool HasTextPatternDescendant(AutomationElement element)
        {
            var descendants = element.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement descendant in descendants)
            {
                object ignored;
                if (descendant.TryGetCurrentPattern(TextPattern.Pattern, out ignored))
                {
                    return true;
                }
            }

            return false;
        }

        private static AutomationElement FindMessageEnvelope(AutomationElement element, AutomationElement transcript)
        {
            var walker = TreeWalker.RawViewWalker;
            var current = walker.GetParent(element);
            AutomationElement outermost = null;
            while (current != null && !Automation.Compare(current, transcript))
            {
                var type = current.Current.ControlType;
                if (type == ControlType.ListItem || type == ControlType.Group)
                {
                    outermost = current;
                }

                current = walker.GetParent(current);
            }

            return outermost;
        }

        private static bool IsTextMessageEnvelope(AutomationElement envelope)
        {
            if (MatchesExcludedMessageMetadata(envelope.Current.AutomationId, envelope.Current.ClassName))
            {
                return false;
            }

            return ContainsMetadataSemantic(envelope, "textmessage", "textbubble", "plaintext", "plainmessage", "chattext", "텍스트메시지", "텍스트말풍선");
        }

        private static string RuntimeKey(AutomationElement element)
        {
            var runtimeId = element.GetRuntimeId();
            if (runtimeId == null || runtimeId.Length == 0)
            {
                throw new MonitorException("UNSUPPORTED_UIA_RUNTIME_ID", "A message envelope has no runtime ID.");
            }

            return string.Join(",", runtimeId.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        }

        private static bool HasFileOrActionContent(AutomationElement envelope)
        {
            object ignored;
            if (envelope.TryGetCurrentPattern(InvokePattern.Pattern, out ignored))
            {
                return true;
            }

            var descendants = envelope.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement element in descendants)
            {
                var type = element.Current.ControlType;
                if (type == ControlType.Image || type == ControlType.Hyperlink || type == ControlType.Button ||
                    MatchesExcludedMessageMetadata(element.Current.AutomationId, element.Current.ClassName) ||
                    element.TryGetCurrentPattern(InvokePattern.Pattern, out ignored))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ReadInputValue(AutomationElement input)
        {
            try
            {
                object pattern;
                if (!input.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                {
                    throw new MonitorException("UNSUPPORTED_INPUT_VALUE_PATTERN", "The input does not expose ValuePattern.");
                }

                return ((ValuePattern)pattern).Current.Value ?? string.Empty;
            }
            catch (MonitorException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new MonitorException("INPUT_VALUE_READ_FAILED", "The current input value could not be read.", ex);
            }
        }

        private static void LogCandidates(AuditLog log, IEnumerable<AutomationElement> candidates, string role)
        {
            foreach (var candidate in candidates.Take(50))
            {
                try
                {
                    ElementIdentity.Capture(candidate).Log(log, role);
                }
                catch (Exception ex)
                {
                    log.WriteException("UIA_CANDIDATE_LOG_FAILED", ex, AuditLog.Field("role", role));
                }
            }
        }

        private static void EnsureElements(LiveElements live, int expectedProcessId)
        {
            var elements = new[] { live.ChatContainer, live.Composer, live.Header, live.Transcript, live.Input, live.Send };
            if (elements.Any(element => element.Current.ProcessId != expectedProcessId))
            {
                throw new MonitorException(
                    "UNSUPPORTED_ELEMENT_PROCESS_MISMATCH",
                    "A required UIA element belongs to a different process than the bound window.");
            }

            if (elements.Any(element => element.Current.IsOffscreen))
            {
                throw new MonitorException(
                    "UNSUPPORTED_ELEMENT_OFFSCREEN",
                    "A required UIA element is reported offscreen; hidden chat panes are not eligible.");
            }
        }

        private static AutomationElement SelectUnique(
            IList<AutomationElement> candidates,
            Func<AutomationElement, bool> semanticMatch,
            string reason)
        {
            var semantic = candidates.Where(semanticMatch).ToList();
            if (candidates.Count == 1 && semantic.Count == 1)
            {
                return semantic[0];
            }

            throw new MonitorException(reason, "Required UIA element count was " + candidates.Count.ToString(CultureInfo.InvariantCulture) +
                " (semantic matches: " + semantic.Count.ToString(CultureInfo.InvariantCulture) + ").");
        }

        private static AutomationElement SelectExactlyOne(IList<AutomationElement> candidates, string reason)
        {
            if (candidates.Count != 1)
            {
                throw new MonitorException(reason, "Required UIA element count was " + candidates.Count.ToString(CultureInfo.InvariantCulture) + ".");
            }

            return candidates[0];
        }

        private static AutomationElement SelectChatContainer(
            AutomationElement root,
            AutomationElement transcript,
            AutomationElement input,
            AutomationElement send)
        {
            var candidates = new List<AutomationElement>();
            var walker = TreeWalker.RawViewWalker;
            var current = walker.GetParent(transcript);
            while (current != null && !Automation.Compare(current, root))
            {
                if (!current.Current.IsOffscreen &&
                    IsChatContainerSemantic(current) &&
                    IsDescendantOrSelf(input, current) &&
                    IsDescendantOrSelf(send, current))
                {
                    candidates.Add(current);
                }

                current = walker.GetParent(current);
            }

            if (candidates.Count != 1)
            {
                throw new MonitorException(
                    "UNSUPPORTED_CHAT_CONTAINER_NOT_UNIQUE",
                    "Expected one semantic non-root chat container shared by transcript/input/send; found " +
                    candidates.Count.ToString(CultureInfo.InvariantCulture) + ".");
            }

            return candidates[0];
        }

        private static AutomationElement SelectConversationHeader(
            AutomationElement chatContainer,
            AuditLog log,
            bool logDetails)
        {
            var candidates = new List<AutomationElement>();
            var descendants = chatContainer.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement element in descendants)
            {
                var type = element.Current.ControlType;
                if ((type == ControlType.Text || type == ControlType.Header || type == ControlType.Group || type == ControlType.TitleBar) &&
                    !element.Current.IsOffscreen &&
                    !string.IsNullOrEmpty(element.Current.Name) &&
                    ContainsMetadataSemantic(element, "chatheader", "conversationheader", "conversationtitle", "chattitle", "roomtitle", "dialogtitle", "대화상대", "대화방"))
                {
                    candidates.Add(element);
                }
            }

            if (logDetails)
            {
                LogCandidates(log, candidates, "chat_header_candidate");
            }

            if (candidates.Count != 1)
            {
                throw new MonitorException(
                    "UNSUPPORTED_CHAT_HEADER_NOT_UNIQUE",
                    "Expected one semantic selected-conversation header; found " +
                    candidates.Count.ToString(CultureInfo.InvariantCulture) + ".");
            }

            return candidates[0];
        }

        private static AutomationElement SelectComposerContainer(
            AutomationElement chatContainer,
            AutomationElement input,
            AutomationElement send)
        {
            var candidates = new List<AutomationElement>();
            var walker = TreeWalker.RawViewWalker;
            var current = walker.GetParent(input);
            while (current != null && !Automation.Compare(current, chatContainer))
            {
                if (!current.Current.IsOffscreen &&
                    IsComposerSemantic(current) &&
                    IsDescendantOrSelf(send, current))
                {
                    candidates.Add(current);
                }

                current = walker.GetParent(current);
            }

            if (candidates.Count != 1)
            {
                throw new MonitorException(
                    "UNSUPPORTED_COMPOSER_NOT_UNIQUE",
                    "Expected one semantic non-root composer shared by input/send; found " +
                    candidates.Count.ToString(CultureInfo.InvariantCulture) + ".");
            }

            return candidates[0];
        }

        private static bool IsDescendantOrSelf(AutomationElement element, AutomationElement ancestor)
        {
            var walker = TreeWalker.RawViewWalker;
            var current = element;
            while (current != null)
            {
                if (Automation.Compare(current, ancestor))
                {
                    return true;
                }

                current = walker.GetParent(current);
            }

            return false;
        }

        private static bool IsChatContainerSemantic(AutomationElement element)
        {
            return ContainsMetadataSemantic(element, "chat", "conversation", "messagepane", "dialog", "대화", "메시지");
        }

        private static bool IsInputSemantic(AutomationElement element)
        {
            return MatchesInputMetadata(element.Current.AutomationId, element.Current.ClassName);
        }

        internal static bool MatchesInputMetadata(string automationId, string className)
        {
            return ContainsMetadata(
                automationId,
                className,
                "messageinput", "chatinput", "compose", "composer", "messageentry", "chatentry",
                "txtmessage", "editmessage", "메시지입력", "대화입력");
        }

        internal static bool MatchesExcludedMessageMetadata(string automationId, string className)
        {
            var metadata = (automationId ?? string.Empty) + " " + (className ?? string.Empty);
            return ExcludedMessageMetadata.IsMatch(MetadataCamelBoundary.Replace(metadata, " "));
        }

        private static bool IsTranscriptSemantic(AutomationElement element)
        {
            return ContainsMetadataSemantic(element, "message", "chat", "history", "conversation", "dialog", "대화", "메시지");
        }

        private static bool IsComposerSemantic(AutomationElement element)
        {
            return ContainsMetadataSemantic(element,
                "composer", "compose", "messageinput", "messageentry", "inputarea", "chatinput",
                "sendarea", "writearea", "메시지입력", "작성");
        }

        private static bool ContainsMetadataSemantic(AutomationElement element, params string[] terms)
        {
            return ContainsMetadata(element.Current.AutomationId, element.Current.ClassName, terms);
        }

        private static bool ContainsMetadata(string automationId, string className, params string[] terms)
        {
            var text = ((automationId ?? string.Empty) + " " + (className ?? string.Empty)).ToLowerInvariant();
            return terms.Any(term => text.IndexOf(term, StringComparison.Ordinal) >= 0);
        }

        private static bool IsSendLabel(string name)
        {
            var normalized = (name ?? string.Empty).Replace("&", string.Empty).Trim();
            return string.Equals(normalized, "Send", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "보내기", StringComparison.Ordinal) ||
                string.Equals(normalized, "전송", StringComparison.Ordinal);
        }

        private static void RequireMta()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            {
                throw new MonitorException("INTERNAL_UIA_THREAD_NOT_MTA", "UIA work must run on the dedicated MTA worker.");
            }
        }

        private void StopIfRequested(Func<bool> stopRequested)
        {
            if (writesCancelled || (stopRequested != null && stopRequested()))
            {
                throw new MonitorException("DISARM_REQUESTED", "Disarm or application close was requested; no further write was attempted.");
            }
        }

        private void EnsureTargetIsBackground()
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                throw new MonitorException(
                    "FOREGROUND_WINDOW_UNAVAILABLE",
                    "The foreground window could not be identified; no write or send was attempted.");
            }

            uint foregroundProcessId;
            if (NativeMethods.GetWindowThreadProcessId(foreground, out foregroundProcessId) == 0 || foregroundProcessId == 0)
            {
                throw new MonitorException(
                    "FOREGROUND_PROCESS_UNAVAILABLE",
                    "The foreground process could not be identified; no write or send was attempted.");
            }

            if (foregroundProcessId == process.ProcessId)
            {
                throw new MonitorException(
                    "TARGET_PROCESS_FOREGROUND_DURING_WRITE",
                    "KI-Messenger is foreground and may be in use; no write or send was attempted.");
            }
        }

        private static string FormatHandle(IntPtr value)
        {
            return "0x" + value.ToInt64().ToString("X", CultureInfo.InvariantCulture);
        }

        private sealed class LiveElements
        {
            public readonly AutomationElement ChatContainer;
            public readonly AutomationElement Composer;
            public readonly AutomationElement Header;
            public readonly AutomationElement Transcript;
            public readonly AutomationElement Input;
            public readonly AutomationElement Send;

            public LiveElements(
                AutomationElement chatContainer,
                AutomationElement composer,
                AutomationElement header,
                AutomationElement transcript,
                AutomationElement input,
                AutomationElement send)
            {
                ChatContainer = chatContainer;
                Composer = composer;
                Header = header;
                Transcript = transcript;
                Input = input;
                Send = send;
            }
        }
    }

    internal sealed class ProcessIdentity : IEquatable<ProcessIdentity>
    {
        public readonly int ProcessId;
        public readonly long StartUtcTicks;
        public readonly string ProcessName;
        public readonly string Path;
        public readonly string ProductName;
        public readonly string ProductVersion;
        public readonly string FileVersion;
        public readonly long FileLength;
        public readonly long FileWriteUtcTicks;
        public readonly string SignatureStatus;
        public readonly string SignerSubject;
        public readonly string SignerThumbprint;
        public readonly string WindowClass;
        public readonly long WindowHandle;

        private ProcessIdentity(
            int processId,
            long startUtcTicks,
            string processName,
            string path,
            string productName,
            string productVersion,
            string fileVersion,
            long fileLength,
            long fileWriteUtcTicks,
            string signatureStatus,
            string signerSubject,
            string signerThumbprint,
            string windowClass,
            long windowHandle)
        {
            ProcessId = processId;
            StartUtcTicks = startUtcTicks;
            ProcessName = processName;
            Path = path;
            ProductName = productName;
            ProductVersion = productVersion;
            FileVersion = fileVersion;
            FileLength = fileLength;
            FileWriteUtcTicks = fileWriteUtcTicks;
            SignatureStatus = signatureStatus;
            SignerSubject = signerSubject;
            SignerThumbprint = signerThumbprint;
            WindowClass = windowClass;
            WindowHandle = windowHandle;
        }

        public static ProcessIdentity Capture(IntPtr window)
        {
            uint processId;
            if (NativeMethods.GetWindowThreadProcessId(window, out processId) == 0 || processId == 0)
            {
                throw new MonitorException("PROCESS_ID_UNAVAILABLE", "The target PID could not be read.");
            }

            try
            {
                using (var process = Process.GetProcessById((int)processId))
                {
                    if (process.HasExited)
                    {
                        throw new MonitorException("PROCESS_EXITED", "The target process exited.");
                    }

                    var path = System.IO.Path.GetFullPath(process.MainModule.FileName);
                    var version = FileVersionInfo.GetVersionInfo(path);
                    var file = new FileInfo(path);
                    string signatureStatus;
                    string signerSubject;
                    string signerThumbprint;
                    ReadSignature(path, out signatureStatus, out signerSubject, out signerThumbprint);

                    return new ProcessIdentity(
                        (int)processId,
                        process.StartTime.ToUniversalTime().Ticks,
                        process.ProcessName,
                        path,
                        version.ProductName ?? string.Empty,
                        version.ProductVersion ?? string.Empty,
                        version.FileVersion ?? string.Empty,
                        file.Length,
                        file.LastWriteTimeUtc.Ticks,
                        signatureStatus,
                        signerSubject,
                        signerThumbprint,
                        NativeMethods.WindowClass(window),
                        window.ToInt64());
                }
            }
            catch (MonitorException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new MonitorException("PROCESS_IDENTITY_UNAVAILABLE", "The target process identity could not be captured.", ex);
            }
        }

        public string Summary
        {
            get
            {
                return "PID: " + ProcessId.ToString(CultureInfo.InvariantCulture) +
                    " / HWND: 0x" + WindowHandle.ToString("X", CultureInfo.InvariantCulture) + Environment.NewLine +
                    "Process: " + ProcessName + " / Version: " + FileVersion + Environment.NewLine +
                    "Path: " + Path + Environment.NewLine +
                    "Window class: " + WindowClass + Environment.NewLine +
                    "Signature: " + SignatureStatus + " / " + (string.IsNullOrEmpty(SignerSubject) ? "<none>" : SignerSubject);
            }
        }

        public void Log(AuditLog log)
        {
            log.Write("INFO", "PROCESS_IDENTITY",
                AuditLog.Field("pid", ProcessId),
                AuditLog.Field("start_utc_ticks", StartUtcTicks),
                AuditLog.Field("hwnd", "0x" + WindowHandle.ToString("X", CultureInfo.InvariantCulture)),
                AuditLog.Field("window_class", WindowClass),
                AuditLog.Field("process_name", ProcessName),
                AuditLog.Field("path", Path),
                AuditLog.Field("product_name", ProductName),
                AuditLog.Field("product_version", ProductVersion),
                AuditLog.Field("file_version", FileVersion),
                AuditLog.Field("file_length", FileLength),
                AuditLog.Field("file_write_utc_ticks", FileWriteUtcTicks),
                AuditLog.Field("signature_status", SignatureStatus),
                AuditLog.Field("signer_subject", SignerSubject),
                AuditLog.Field("signer_thumbprint", SignerThumbprint));
        }

        public bool Equals(ProcessIdentity other)
        {
            return other != null &&
                ProcessId == other.ProcessId &&
                StartUtcTicks == other.StartUtcTicks &&
                WindowHandle == other.WindowHandle &&
                FileLength == other.FileLength &&
                FileWriteUtcTicks == other.FileWriteUtcTicks &&
                Equal(ProcessName, other.ProcessName) &&
                Equal(Path, other.Path) &&
                Equal(ProductName, other.ProductName) &&
                Equal(ProductVersion, other.ProductVersion) &&
                Equal(FileVersion, other.FileVersion) &&
                Equal(SignatureStatus, other.SignatureStatus) &&
                Equal(SignerSubject, other.SignerSubject) &&
                Equal(SignerThumbprint, other.SignerThumbprint) &&
                Equal(WindowClass, other.WindowClass);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ProcessIdentity);
        }

        public override int GetHashCode()
        {
            return ProcessId;
        }

        private static bool Equal(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static void ReadSignature(string path, out string status, out string subject, out string thumbprint)
        {
            uint trustResult;
            try
            {
                trustResult = NativeMethods.VerifyAuthenticode(path);
            }
            catch (Exception ex)
            {
                throw new MonitorException("SIGNATURE_VERIFICATION_FAILED", "WinVerifyTrust could not inspect the target executable.", ex);
            }

            status = trustResult == 0
                ? "VALID_TRUSTED_OFFLINE"
                : trustResult == 0x800B0100
                    ? "NO_SIGNATURE_OR_INVALID"
                    : "INVALID_OR_UNTRUSTED_0x" + trustResult.ToString("X8", CultureInfo.InvariantCulture);

            try
            {
                using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                {
                    subject = certificate.Subject ?? string.Empty;
                    thumbprint = certificate.Thumbprint ?? string.Empty;
                }
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                subject = string.Empty;
                thumbprint = string.Empty;
            }
        }
    }

    internal sealed class ElementIdentity : IEquatable<ElementIdentity>
    {
        public readonly string RuntimeId;
        public readonly int ProcessId;
        public readonly string AutomationId;
        public readonly string ControlType;
        public readonly string ClassName;
        public readonly string FrameworkId;
        public readonly string Patterns;
        public readonly int NameLength;
        public readonly string NameHash;
        public readonly string SafeName;

        internal ElementIdentity(
            string runtimeId,
            int processId,
            string automationId,
            string controlType,
            string className,
            string frameworkId,
            string patterns,
            int nameLength,
            string nameHash,
            string safeName)
        {
            RuntimeId = runtimeId;
            ProcessId = processId;
            AutomationId = automationId;
            ControlType = controlType;
            ClassName = className;
            FrameworkId = frameworkId;
            Patterns = patterns;
            NameLength = nameLength;
            NameHash = nameHash;
            SafeName = safeName;
        }

        public static ElementIdentity Capture(AutomationElement element)
        {
            var runtimeId = element.GetRuntimeId();
            if (runtimeId == null || runtimeId.Length == 0)
            {
                throw new MonitorException("UNSUPPORTED_UIA_RUNTIME_ID", "A required UIA element has no runtime ID.");
            }

            var name = element.Current.Name ?? string.Empty;
            var patternNames = element.GetSupportedPatterns()
                .Select(pattern => pattern.ProgrammaticName)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            return new ElementIdentity(
                string.Join(",", runtimeId.Select(value => value.ToString(CultureInfo.InvariantCulture))),
                element.Current.ProcessId,
                element.Current.AutomationId ?? string.Empty,
                element.Current.ControlType.ProgrammaticName ?? string.Empty,
                element.Current.ClassName ?? string.Empty,
                element.Current.FrameworkId ?? string.Empty,
                string.Join(",", patternNames),
                name.Length,
                TokenStore.Hash(name),
                SafeElementName(name));
        }

        public void Log(AuditLog log, string prefix)
        {
            log.Write("INFO", "UIA_ELEMENT",
                AuditLog.Field("role", prefix),
                AuditLog.Field("process_id", ProcessId),
                AuditLog.Field("name", SafeName),
                AuditLog.Field("name_length", NameLength),
                AuditLog.Field("name_fingerprint", log.Fingerprint(SafeName == "<redacted>" ? NameHash : SafeName)),
                AuditLog.Field("automation_id_length", AutomationId.Length),
                AuditLog.Field("automation_id_fingerprint", log.Fingerprint(AutomationId)),
                AuditLog.Field("control_type", ControlType),
                AuditLog.Field("class_name_length", ClassName.Length),
                AuditLog.Field("class_name_fingerprint", log.Fingerprint(ClassName)),
                AuditLog.Field("framework_id_fingerprint", log.Fingerprint(FrameworkId)),
                AuditLog.Field("runtime_id", RuntimeId),
                AuditLog.Field("patterns", Patterns));
        }

        public bool Equals(ElementIdentity other)
        {
            return Matches(other, compareName: true);
        }

        internal bool Matches(ElementIdentity other, bool compareName)
        {
            // Content-bearing controls may expose their changing contents as Name; the conversation header stays strict.
            return other != null &&
                ProcessId == other.ProcessId &&
                string.Equals(RuntimeId, other.RuntimeId, StringComparison.Ordinal) &&
                string.Equals(AutomationId, other.AutomationId, StringComparison.Ordinal) &&
                string.Equals(ControlType, other.ControlType, StringComparison.Ordinal) &&
                string.Equals(ClassName, other.ClassName, StringComparison.Ordinal) &&
                string.Equals(FrameworkId, other.FrameworkId, StringComparison.Ordinal) &&
                string.Equals(Patterns, other.Patterns, StringComparison.Ordinal) &&
                (!compareName || (NameLength == other.NameLength && string.Equals(NameHash, other.NameHash, StringComparison.Ordinal)));
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ElementIdentity);
        }

        public override int GetHashCode()
        {
            return RuntimeId.GetHashCode();
        }

        private static string SafeElementName(string name)
        {
            return AutomationTargetNameIsSafe(name) ? name : "<redacted>";
        }

        private static bool AutomationTargetNameIsSafe(string name)
        {
            var value = (name ?? string.Empty).Replace("&", string.Empty).Trim();
            return string.Equals(value, "Send", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "보내기", StringComparison.Ordinal) ||
                string.Equals(value, "전송", StringComparison.Ordinal);
        }
    }
}
