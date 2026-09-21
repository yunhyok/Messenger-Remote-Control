using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace RemoteMonitorMaster
{
    internal static class ReadOnlyProbe
    {
        // ponytail: finite diagnostic ceiling from field evidence; raise only with evidence, never unbounded traversal or a longer timeout.
        internal const int MaxNodes = 2048;

        public static string Capture(IntPtr window, AuditLog log, string stage, string marker, Func<bool> stopRequested,
            System.Windows.Point? pointer = null)
        {
            return CaptureSnapshot(window, log, stage, marker, stopRequested, pointer).Summary;
        }

        internal static ProbeSnapshot CaptureSnapshot(IntPtr window, AuditLog log, string stage, string marker, Func<bool> stopRequested,
            System.Windows.Point? pointer = null, string requestedRuntimeId = null, string requestedSendRuntimeId = null,
            bool retainSelectedInput = false, bool automaticSendSelection = false, bool retainReceiveElements = false,
            bool compactLog = false)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            {
                throw new MonitorException("PROBE_REQUIRES_MTA", "The read-only probe must run on an MTA worker.");
            }
            if (!IsValidSnapshotRequest(stage, marker, pointer, retainSelectedInput, automaticSendSelection))
            {
                throw new ArgumentException("A valid stage with its generated marker or physical pointer is required.");
            }
            if (log == null || stopRequested == null)
            {
                throw new ArgumentNullException(log == null ? nameof(log) : nameof(stopRequested));
            }
            if (retainSelectedInput && (stage != "SEND_METADATA" || requestedRuntimeId != null || requestedSendRuntimeId != null))
                throw new ArgumentException("Retaining the selected input requires a standalone metadata snapshot request.");
            if (retainReceiveElements && (!automaticSendSelection || !retainSelectedInput || stage != "SEND_METADATA" || pointer.HasValue))
                throw new ArgumentException("Receive retention requires a no-pointer automatic metadata snapshot.");

            const int depthLimit = 32;
            var clock = Stopwatch.StartNew();
            var walker = TreeWalker.RawViewWalker;
            var nodes = 0;
            var failures = 0;
            var skipped = 0;
            var edits = 0;
            var invokes = 0;
            var truncatedTextReads = 0;
            var pointerHits = 0;
            var pointerInvokes = 0;
            var reads = new int[3];
            var exact = new int[3];
            var contains = new int[3];
            var observed = new List<ProbeNode>();
            var retainedElements = retainSelectedInput ? new Dictionary<int, AutomationElement>() : null;
            string stopReason = null;
            string layoutRejection = null;
            AutomationElement matchedElement = null, matchedSend = null;

            bool Continue()
            {
                if (stopReason == null && stopRequested()) stopReason = "CANCELLED";
                if (stopReason == null && clock.Elapsed >= TimeSpan.FromSeconds(15)) stopReason = "TIME_LIMIT";
                return stopReason == null;
            }

            bool Read<T>(int node, string property, Func<T> get, out T value)
            {
                value = default(T);
                if (!Continue()) return false;
                try
                {
                    value = get();
                    Continue();
                    return true;
                }
                catch (Exception ex)
                {
                    failures++;
                    log.WriteException("PROBE_READ_FAILED", ex, AuditLog.Field("stage", stage),
                        AuditLog.Field("node", node), AuditLog.Field("property", property));
                    Continue();
                    return false;
                }
            }

            log.Write("INFO", "READ_ONLY_PROBE_BEGIN", AuditLog.Field("stage", stage),
                AuditLog.Field("node_limit", MaxNodes), AuditLog.Field("depth_limit", depthLimit),
                AuditLog.Field("automatic_structural_selection", automaticSendSelection),
                AuditLog.Field("cooperative_seconds", 15), AuditLog.Field("individual_call_timeout", false));
            if (!Continue())
            {
                throw new MonitorException("PROBE_STOPPED", "The probe was stopped before inspecting the target.");
            }
            if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
            {
                throw new MonitorException("PROBE_NO_TARGET", "No current window is available for the read-only probe.");
            }

            var process = ProcessIdentity.Capture(window);
            if (!string.Equals(process.ProcessName, "KI-Messenger", StringComparison.OrdinalIgnoreCase))
            {
                throw new MonitorException("PROBE_NOT_KI_MESSENGER", "Select a KI-Messenger window. No content was inspected.");
            }
            process.Log(log);

            bool NativeTargetMatches()
            {
                uint pid;
                return NativeMethods.IsWindow(window) &&
                    NativeMethods.GetWindowThreadProcessId(window, out pid) != 0 && pid == process.ProcessId;
            }

            bool RootMatches(AutomationElement element, string phase)
            {
                int pid;
                int hwnd;
                var pidRead = Read(0, phase + "_root_pid", () => element.Current.ProcessId, out pid);
                var hwndRead = Read(0, phase + "_root_hwnd", () => element.Current.NativeWindowHandle, out hwnd);
                var match = pidRead && hwndRead && pid == process.ProcessId && new IntPtr(hwnd) == window;
                log.Write("INFO", "PROBE_ROOT_CHECK", AuditLog.Field("phase", phase),
                    AuditLog.Field("pid_read", pidRead), AuditLog.Field("hwnd_read", hwndRead),
                    AuditLog.Field("same_pid_and_hwnd", match));
                return match;
            }

            // Recheck before each content read; a password/foreign element is never a content source.
            void CheckContentAllowed(AutomationElement element)
            {
                if (element.Current.ProcessId != process.ProcessId || element.Current.IsPassword)
                {
                    throw new MonitorException("PROBE_CONTENT_BLOCKED", "The element is no longer a non-password KI-Messenger element.");
                }
            }

            void RecordMatch(int node, int sourceIndex, string source, string text)
            {
                reads[sourceIndex]++;
                var match = MatchMarker(text, marker);
                if (match == "exact") exact[sourceIndex]++;
                if (match == "contains") contains[sourceIndex]++;
                var limited = source == "TextPattern" && text != null && text.Length == 513;
                if (limited) truncatedTextReads++;
                if (!compactLog)
                    log.Write("INFO", "PROBE_MARKER_MATCH", AuditLog.Field("stage", stage),
                        AuditLog.Field("node", node), AuditLog.Field("source", source), AuditLog.Field("match", match),
                        AuditLog.Field("content_length", text == null ? 0 : text.Length),
                        AuditLog.Field("text_limit_reached", limited));
            }

            bool MatchSource(int node, AutomationElement element, int sourceIndex, string source, Func<string> get)
            {
                string text;
                if (Read(node, source, () => { CheckContentAllowed(element); return get(); }, out text))
                {
                    RecordMatch(node, sourceIndex, source, text);
                    return MatchMarker(text, marker) == "exact";
                }
                return false;
            }

            void Visit(AutomationElement element, int parent, int depth, int document)
            {
                if (!Continue()) return;
                if (nodes == MaxNodes)
                {
                    stopReason = "NODE_LIMIT";
                    return;
                }

                var node = ++nodes;
                int pid;
                bool password;
                if (!Read(node, "process_id", () => element.Current.ProcessId, out pid) ||
                    pid != process.ProcessId ||
                    !Read(node, "is_password", () => element.Current.IsPassword, out password) || password)
                {
                    skipped++;
                    log.Write("INFO", "PROBE_NODE_SKIPPED", AuditLog.Field("stage", stage),
                        AuditLog.Field("node", node), AuditLog.Field("parent_node", parent), AuditLog.Field("depth", depth),
                        AuditLog.Field("reason", pid != process.ProcessId ? "FOREIGN_OR_UNREADABLE_PID" : "PASSWORD_OR_UNREADABLE_GUARD"));
                    return; // Do not traverse an untrusted/password subtree either.
                }

                ProbeElementCache cached;
                if (!Read(node, "metadata_cache", () => ProbeElementCache.Capture(element, process.ProcessId, pointer.HasValue), out cached))
                    return; // No per-property fallback after an unknown batch result.

                var fields = compactLog ? null : new List<AuditLog.LogField>();
                void Detail(string key, object value)
                {
                    if (fields != null) fields.Add(AuditLog.Field(key, value));
                }
                Detail("stage", stage); Detail("node", node); Detail("parent_node", parent);
                Detail("depth", depth); Detail("process_id", pid);
                var type = cached.ControlType;
                var typeRead = true;
                if (type == ControlType.Document) document = node;
                Detail("document_node", document);
                Detail("control_type", typeRead && type != null ? type.ProgrammaticName : "<unavailable>");
                var nativeHandle = cached.NativeWindowHandle;
                if (fields != null) Detail("native_hwnd", "0x" + unchecked((uint)nativeHandle).ToString("X", CultureInfo.InvariantCulture));
                var enabled = cached.IsEnabled;
                Detail("enabled", enabled);
                var offscreen = cached.IsOffscreen;
                Detail("offscreen", offscreen);
                Detail("focused", cached.HasKeyboardFocus);

                ElementIdentity identity = null;
                string identityName;
                if (Read(node, "identity_name", () => { CheckContentAllowed(element); return element.Current.Name; }, out identityName))
                {
                    identity = cached.CreateIdentity(identityName);
                    if (identity.ProcessId != process.ProcessId)
                    {
                        skipped++;
                        log.Write("INFO", "PROBE_NODE_SKIPPED", AuditLog.Field("stage", stage),
                            AuditLog.Field("node", node), AuditLog.Field("parent_node", parent),
                            AuditLog.Field("reason", "PROCESS_CHANGED_DURING_IDENTITY_READ"));
                        return;
                    }
                    if (!compactLog) identity.Log(log, "probe_node_" + node.ToString(CultureInfo.InvariantCulture));
                    Detail("runtime_id", identity.RuntimeId);
                }

                string name;
                if (Read(node, "Name", () => { CheckContentAllowed(element); return element.Current.Name; }, out name))
                {
                    RecordMatch(node, 0, "Name", name);
                    if (fields != null)
                    {
                        Detail("name_shape_hint", NameShape(name, marker));
                        var label = (name ?? string.Empty).Replace("&", string.Empty).Trim();
                        Detail("send_label_match", string.Equals(label, "Send", StringComparison.OrdinalIgnoreCase) ||
                            label == "보내기" || label == "전송");
                    }
                }
                else Detail("send_label_match", "<unavailable>");

                object textPattern;
                cached.TryGetPattern(TextPattern.Pattern, out textPattern);
                var textRead = true;
                Detail("text_pattern", textRead ? (object)(textPattern != null) : "<unavailable>");
                if (textPattern != null && stage != "SEND_METADATA")
                    MatchSource(node, element, 1, "TextPattern", () => ((TextPattern)textPattern).DocumentRange.GetText(513));

                object valuePattern;
                var valueWritable = false;
                var draftExact = false;
                cached.TryGetPattern(ValuePattern.Pattern, out valuePattern);
                var valueRead = true;
                Detail("value_pattern", valueRead ? (object)(valuePattern != null) : "<unavailable>");
                if (valuePattern != null)
                {
                    bool readOnly;
                    var readOnlyRead = Read(node, "value_read_only", () => ((ValuePattern)valuePattern).Current.IsReadOnly, out readOnly);
                    valueWritable = readOnlyRead && !readOnly;
                    Detail("value_read_only", readOnlyRead ? (object)readOnly : "<unavailable>");
                    if (stage != "SEND_METADATA")
                        draftExact = MatchSource(node, element, 2, "ValuePattern", () => ((ValuePattern)valuePattern).Current.Value);
                }

                object invokePattern;
                var invoke = cached.TryGetPattern(InvokePattern.Pattern, out invokePattern);
                var invokeRead = true;
                if (type == ControlType.Edit) edits++;
                if (invokeRead && invoke) invokes++;
                Detail("edit_candidate", typeRead ? (object)(type == ControlType.Edit) : "<unavailable>");
                Detail("invoke_candidate", invokeRead ? (object)invoke : "<unavailable>");
                Detail("envelope_shape_candidate", typeRead
                    ? (object)(type == ControlType.ListItem || type == ControlType.Group || type == ControlType.Custom) : "<unavailable>");
                var pointerInside = false;
                if (pointer.HasValue)
                {
                    var bounds = cached.BoundingRectangle;
                    var boundsRead = true;
                    var hit = ContainsPoint(bounds, pointer.Value);
                    pointerInside = hit;
                    if (hit) pointerHits++;
                    if (hit && invokeRead && invoke) pointerInvokes++;
                    // Bounds overlap on ancestors and duplicate providers. This is not a hit-test or a Send selector.
                    Detail("pointer_inside_bounds", boundsRead ? (object)hit : "<unavailable>");
                }
                if (fields != null) log.Write("INFO", "PROBE_NODE", fields.ToArray());
                observed.Add(new ProbeNode { Node = node, Parent = parent, Document = document, NativeHwnd = nativeHandle,
                    Identity = identity, Enabled = enabled, Visible = !offscreen, ValueWritable = valueWritable,
                    Invoke = invokeRead && invoke, PointerInside = pointerInside, DraftExact = draftExact,
                    NameShape = identity != null && TokenStore.Hash(name ?? string.Empty) == identity.NameHash
                        ? NameShape(name, null) : "UNAVAILABLE" });
                ReadOnlyCommands.ObserveName(observed[observed.Count - 1], name);
                if (requestedRuntimeId != null && identity != null && identity.RuntimeId == requestedRuntimeId) matchedElement = element;
                if (requestedSendRuntimeId != null && identity != null && identity.RuntimeId == requestedSendRuntimeId) matchedSend = element;
                if (retainedElements != null && (type == ControlType.Edit || (automaticSendSelection && type == ControlType.Hyperlink) ||
                    (retainReceiveElements && (type == ControlType.Text || type == ControlType.Custom))))
                    retainedElements.Add(node, element);

                if (Continue() && failures == 0 && skipped == 0 && truncatedTextReads == 0 &&
                    (stage == "PAIR" || stage == "PAIR_RECHECK" || stage == "TARGET_SWITCH" || stage == "SEND_METADATA"))
                {
                    layoutRejection = ReadOnlyPair.LayoutRejection(observed);
                    if (layoutRejection != null)
                    {
                        stopReason = layoutRejection;
                        log.Write("INFO", "LAYOUT_GUARD_REJECTED", AuditLog.Field("stage", stage),
                            AuditLog.Field("node", node), AuditLog.Field("reason", layoutRejection),
                            AuditLog.Field("window_mode_verified", false), AuditLog.Field("automatic_send_allowed", false));
                        return; // Do not read list children or continue toward the node limit after negative evidence.
                    }
                }

                // ponytail: bounded raw traversal, not FindAll; the budget cannot interrupt an individual blocked UIA call.
                AutomationElement child;
                if (!Read(node, "first_child", () => { CheckContentAllowed(element); return walker.GetFirstChild(element); }, out child)) return;
                if (child != null && depth == depthLimit)
                {
                    stopReason = "DEPTH_LIMIT";
                    return;
                }
                while (child != null && Continue())
                {
                    Visit(child, node, depth + 1, document);
                    if (!Continue()) return;
                    AutomationElement next;
                    if (!Read(node, "next_sibling", () => walker.GetNextSibling(child), out next)) return;
                    child = next;
                }
            }

            AutomationElement root;
            var rootRead = Read(0, "root", () => AutomationElement.FromHandle(window), out root);
            var rootVerified = rootRead && root != null && NativeTargetMatches() && RootMatches(root, "BEFORE");
            string rootNameBefore = null;
            string rootNameAfter = null;
            bool ReadRootName(AutomationElement element, string phase, out string name)
            {
                var read = Read(0, "root_name_" + phase, () => { CheckContentAllowed(element); return element.Current.Name; }, out name);
                if (read) log.Write("INFO", "PROBE_ROOT_NAME", AuditLog.Field("stage", stage), AuditLog.Field("phase", phase),
                    AuditLog.Field("name_length", name == null ? 0 : name.Length), AuditLog.Field("name_fingerprint", log.Fingerprint(name)));
                return read;
            }
            var rootNameBeforeRead = rootVerified && ReadRootName(root, "BEFORE", out rootNameBefore);
            var rootNameAfterRead = false;
            if (rootVerified) Visit(root, 0, 0, 0); // Pane roots may be observed; this snapshot never grants send permission.
            else if (stopReason == null) stopReason = "ROOT_UNVERIFIED";

            var nativeVerified = NativeTargetMatches();
            var finalProcessVerified = false;
            var finalRootVerified = false;
            var finalValidation = "NOT_RUN";
            if (rootVerified && nativeVerified && Continue())
            {
                finalValidation = "NOT_VERIFIED";
                ProcessIdentity finalProcess;
                finalProcessVerified = Read(0, "final_process", () => ProcessIdentity.Capture(window), out finalProcess) && process.Equals(finalProcess);
                AutomationElement finalRoot;
                finalRootVerified = Read(0, "final_root", () => AutomationElement.FromHandle(window), out finalRoot) &&
                    finalRoot != null && RootMatches(finalRoot, "AFTER");
                if (finalRootVerified) rootNameAfterRead = ReadRootName(finalRoot, "AFTER", out rootNameAfter);
                if (finalProcessVerified && finalRootVerified) finalValidation = "VERIFIED";
            }
            nativeVerified = NativeTargetMatches();
            Continue();
            if (stopReason == null && (!nativeVerified || !finalProcessVerified || !finalRootVerified))
                stopReason = "TARGET_REVALIDATION_FAILED";
            var rootNameStable = rootNameBeforeRead && rootNameAfterRead && string.Equals(rootNameBefore, rootNameAfter, StringComparison.Ordinal);
            if (stopReason == null && !rootNameStable) stopReason = "ROOT_NAME_UNVERIFIED_OR_CHANGED";
            var complete = rootVerified && nativeVerified && finalProcessVerified && finalRootVerified &&
                failures == 0 && skipped == 0 && truncatedTextReads == 0 && stopReason == null;
            log.Write("INFO", "READ_ONLY_PROBE_RESULT", AuditLog.Field("stage", stage), AuditLog.Field("complete", complete),
                AuditLog.Field("nodes", nodes), AuditLog.Field("read_failures", failures), AuditLog.Field("skipped_subtrees", skipped),
                AuditLog.Field("truncated_text_reads", truncatedTextReads),
                AuditLog.Field("stop_reason", stopReason ?? "NONE"), AuditLog.Field("elapsed_ms", clock.ElapsedMilliseconds),
                AuditLog.Field("initial_root_verified", rootVerified), AuditLog.Field("final_native_verified", nativeVerified),
                AuditLog.Field("final_process_verified", finalProcessVerified), AuditLog.Field("final_root_verified", finalRootVerified),
                AuditLog.Field("final_validation", finalValidation),
                AuditLog.Field("root_name_stable", rootNameStable), AuditLog.Field("conversation_identity_verified", false),
                AuditLog.Field("pointer_candidates", pointerHits), AuditLog.Field("pointer_invoke_candidates", pointerInvokes),
                AuditLog.Field("name_reads", reads[0]), AuditLog.Field("text_reads", reads[1]), AuditLog.Field("value_reads", reads[2]),
                AuditLog.Field("name_exact", exact[0]), AuditLog.Field("text_exact", exact[1]), AuditLog.Field("value_exact", exact[2]),
                AuditLog.Field("name_contains", contains[0]), AuditLog.Field("text_contains", contains[1]), AuditLog.Field("value_contains", contains[2]),
                AuditLog.Field("edit_candidates", edits), AuditLog.Field("invoke_candidates", invokes));

            var summary = stage + " probe: nodes=" + nodes + "; complete=" + complete + Environment.NewLine +
                "PID=" + process.ProcessId + "; HWND=0x" + window.ToInt64().ToString("X", CultureInfo.InvariantCulture) + Environment.NewLine +
                "Exact marker: Name=" + exact[0] + ", TextPattern=" + exact[1] + ", ValuePattern=" + exact[2] + Environment.NewLine +
                "Contains marker: Name=" + contains[0] + ", TextPattern=" + contains[1] + ", ValuePattern=" + contains[2] + Environment.NewLine +
                "Root name stable=" + rootNameStable + " (NOT a conversation identity). Pointer bounds candidates=" + pointerHits + ", with Invoke=" + pointerInvokes + Environment.NewLine +
                "Candidates only: Edit=" + edits + ", Invoke=" + invokes + "; skipped=" + skipped + "; read failures=" + failures + "; text limited=" + truncatedTextReads + Environment.NewLine +
                "Stop=" + (stopReason ?? "NONE") + "; final check=" + finalValidation + ". READ ONLY. No input or send. Not an automation compatibility PASS.";
            var snapshot = new ProbeSnapshot { Complete = complete, Summary = summary, Process = process, Nodes = observed, LayoutRejection = layoutRejection,
                MatchedElement = complete ? matchedElement : null,
                MatchedSend = complete ? matchedSend : null,
                RootNameFingerprint = rootNameStable ? log.Fingerprint(rootNameBefore) : null };
            if (complete && retainSelectedInput)
            {
                var selectedInput = automaticSendSelection ? ReadOnlyPair.SelectAutomaticInput(snapshot) : ReadOnlyPair.SelectHoveredInput(snapshot);
                snapshot.MatchedElement = retainedElements[selectedInput.Node];
                if (automaticSendSelection)
                    snapshot.MatchedSend = retainedElements[ReadOnlyPair.SelectAutomaticSend(snapshot).Node];
                if (retainReceiveElements)
                {
                    snapshot.ReceiveElements = new Dictionary<int, AutomationElement>();
                    foreach (var node in ReceiveProbe.MetadataNodes(snapshot, null))
                        snapshot.ReceiveElements.Add(node.Node, retainedElements[node.Node]);
                }
            }
            return snapshot;
        }

        internal static bool IsValidSnapshotRequest(string stage, string marker, System.Windows.Point? pointer,
            bool retainSelectedInput, bool automaticSendSelection)
        {
            return automaticSendSelection
                ? stage == "SEND_METADATA" && marker == null && retainSelectedInput &&
                    (!pointer.HasValue || (Finite(pointer.Value.X) && Finite(pointer.Value.Y)))
                : IsValidRequest(stage, marker, pointer);
        }

        internal static string MatchMarker(string text, string marker)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(marker)) return "none";
            if (string.Equals(text, marker, StringComparison.Ordinal)) return "exact";
            return text.IndexOf(marker, StringComparison.Ordinal) >= 0 ? "contains" : "none";
        }

        internal static bool IsPointerStage(string stage) { return stage == "HEADER" || stage == "SEND" || stage == "PAIR" || stage == "SEND_METADATA"; }

        internal static bool IsValidRequest(string stage, string marker, System.Windows.Point? pointer)
        {
            if (stage == "PAIR" || stage == "PAIR_RECHECK" || stage == "TARGET_SWITCH")
                return Protocol.IsDiagnosticMarker("DRAFT", marker) && (stage != "PAIR" ? !pointer.HasValue :
                    pointer.HasValue && Finite(pointer.Value.X) && Finite(pointer.Value.Y));
            return IsPointerStage(stage)
                ? marker == null && pointer.HasValue && Finite(pointer.Value.X) && Finite(pointer.Value.Y)
                : !pointer.HasValue && Protocol.IsDiagnosticMarker(stage, marker);
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        internal static bool ContainsPoint(System.Windows.Rect bounds, System.Windows.Point point)
        {
            return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 &&
                Finite(bounds.Left) && Finite(bounds.Top) && Finite(bounds.Right) && Finite(bounds.Bottom) &&
                Finite(point.X) && Finite(point.Y) && bounds.Contains(point);
        }

        internal static string NameShape(string name, string marker)
        {
            if (string.IsNullOrEmpty(name)) return "EMPTY";
            var match = MatchMarker(name, marker);
            if (match == "exact") return "MARKER_EXACT";
            if (name.Length > 256) return "OTHER";
            // ponytail: lexical hints only, never evidence that a node is a body or attachment.
            if (Regex.IsMatch(name, @"\A(?:(?:AM|PM|오전|오후) )?(?:[01]?[0-9]|2[0-3]):[0-5][0-9](?: ?(?:AM|PM))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "TIME_LIKE";
            if (Regex.IsMatch(name, @"\A(?:[0-9]{4}[-./][0-9]{1,2}[-./][0-9]{1,2}|[0-9]{4}년 [0-9]{1,2}월 [0-9]{1,2}일)\z", RegexOptions.CultureInvariant)) return "DATE_LIKE";
            if (Regex.IsMatch(name, @"\A[^\r\n\\/]+\.(?:txt|pdf|docx?|xlsx?|pptx?|zip|7z|png|jpe?g|csv|log)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "FILE_NAME_LIKE";
            if (match == "contains") return "MARKER_EMBEDDED";
            return "OTHER";
        }
    }
}
