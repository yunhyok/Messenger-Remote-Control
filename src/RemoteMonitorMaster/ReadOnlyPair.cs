using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Automation;

namespace RemoteMonitorMaster
{
    internal sealed class ProbeNode
    {
        public int Node, Parent, Document, NativeHwnd;
        public ElementIdentity Identity;
        public string NameShape;
        public string PlainCommand, CommandNameFormat, PlainCommandSourceHash;
        public bool Enabled, Visible, ValueWritable, Invoke, PointerInside, DraftExact;
    }

    internal sealed class ProbeSnapshot
    {
        public bool Complete;
        public string Summary, RootNameFingerprint, LayoutRejection;
        public ProcessIdentity Process;
        public List<ProbeNode> Nodes;
        // Only a fresh send-precheck may request these; never retained in a baseline.
        public AutomationElement MatchedElement, MatchedSend;
        public Dictionary<int, AutomationElement> ReceiveElements;
    }

    internal static class ReadOnlyPair
    {
        public static string Verify(IntPtr window, AuditLog log, string marker, System.Windows.Point pointer, Func<bool> stopRequested)
        {
            return CaptureBaseline(window, log, marker, pointer, stopRequested).Summary;
        }

        public sealed class PairResult
        {
            public readonly string Summary;
            public readonly Baseline Baseline;
            internal PairResult(string summary, Baseline baseline = null) { Summary = summary; Baseline = baseline; }
        }

        public sealed class Baseline
        {
            internal readonly ProbeSnapshot Snapshot;
            internal readonly string SendRuntimeId, Marker;
            internal readonly AuditLog Owner;
            internal readonly Stopwatch Age = Stopwatch.StartNew();
            internal int Used;

            internal Baseline(ProbeSnapshot snapshot, string sendRuntimeId, string marker, AuditLog owner)
            { Snapshot = snapshot; SendRuntimeId = sendRuntimeId; Marker = marker; Owner = owner; }
        }

        public sealed class SendConsent
        {
            internal readonly Baseline Baseline;
            private int state; // 0=confirmed, 1=cancelled, 2=one Invoke committed
            public SendConsent(Baseline baseline, bool confirmed)
            {
                Need(baseline != null && confirmed, "SEND_CONFIRMATION_REQUIRED");
                Baseline = baseline;
            }
            public bool Attempted { get { return Volatile.Read(ref state) == 2; } }
            internal bool Cancelled { get { return Volatile.Read(ref state) == 1; } }
            public bool Cancel() { return Interlocked.CompareExchange(ref state, 1, 0) == 2; }
            internal bool TryCommit() { return Interlocked.CompareExchange(ref state, 2, 0) == 0; }
        }

        public static string SendDraftOnce(IntPtr diagnosticWindow, AuditLog log, SendConsent consent, Func<bool> stopRequested)
        {
            AppInfo.RejectAutomationInDiagnosticBuild();
            return null;
        }

        internal static bool SendForegroundAllowed(IntPtr foreground, IntPtr diagnosticWindow, IntPtr targetWindow, bool afterInvoke)
        {
            // Only post-action observation may accept the exact bound window, never a same-process sibling/child/popup.
            return diagnosticWindow != IntPtr.Zero && targetWindow != IntPtr.Zero && diagnosticWindow != targetWindow &&
                (foreground == diagnosticWindow || (afterInvoke && foreground == targetWindow));
        }


        public static PairResult CaptureBaseline(IntPtr window, AuditLog log, string marker, System.Windows.Point pointer, Func<bool> stopRequested)
        {
            if (log == null || stopRequested == null)
                throw new ArgumentNullException(log == null ? nameof(log) : nameof(stopRequested));
            try
            {
                Need(ReadOnlyProbe.IsValidRequest("PAIR", marker, pointer), "PAIR_REQUEST_INVALID");
                CheckCancelled(stopRequested);
                var first = ReadOnlyProbe.CaptureSnapshot(window, log, "PAIR", marker, stopRequested, pointer);
                CheckCancelled(stopRequested);
                var selected = Select(first, null);
                LogSelection(log, "FIRST", selected);
                CheckCancelled(stopRequested);
                var second = ReadOnlyProbe.CaptureSnapshot(window, log, "PAIR_RECHECK", marker, stopRequested);
                CheckCancelled(stopRequested);
                var reacquired = Recheck(first, selected, second);
                LogSelection(log, "RECHECK", reacquired);
                CheckCancelled(stopRequested);
                LogResult(log, "VERIFIED_READ_ONLY", "NONE");
                log.Write("INFO", "BASELINE_READY", AuditLog.Field("ttl_seconds", 300), AuditLog.Field("one_use", true),
                    AuditLog.Field("conversation_identity_verified", false), AuditLog.Field("automatic_send_allowed", false));
                return new PairResult("VERIFIED_READ_ONLY - candidate mapping was reacquired without a pointer." + Environment.NewLine +
                    "Native document=" + reacquired.Document.Node + "; composer=" + reacquired.Composer.Node +
                    "; input candidate=" + reacquired.Input.Node + "; hovered Invoke candidate=" + reacquired.Send.Node + Environment.NewLine +
                    "Read-only mapping ready. Leave the existing draft unchanged. All input and sending are disabled.",
                    new Baseline(second, reacquired.Send.Identity.RuntimeId, marker, log));
            }
            catch (MonitorException ex)
            {
                LogResult(log, "REJECTED", ex.ReasonCode);
                return new PairResult(Rejected(ex.ReasonCode));
            }
            catch (Exception ex)
            {
                log.WriteException("PAIR_CHECK_FAILED", ex);
                LogResult(log, "REJECTED", "PAIR_READ_FAILED");
                return new PairResult(Rejected("PAIR_READ_FAILED"));
            }
        }

        public static string CheckTransition(IntPtr window, AuditLog log, Baseline baseline, Func<bool> stopRequested)
        {
            if (log == null || stopRequested == null)
                throw new ArgumentNullException(log == null ? nameof(log) : nameof(stopRequested));
            try
            {
                Need(baseline != null, "SWITCH_BASELINE_MISSING");
                CheckBaselineState(ReferenceEquals(baseline.Owner, log), Interlocked.Exchange(ref baseline.Used, 1) == 0, baseline.Age.Elapsed);
                CheckSwitchAlive(baseline, stopRequested);
                Need(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "PROBE_REQUIRES_MTA");
                log.Write("INFO", "TARGET_SWITCH_BEGIN", AuditLog.Field("baseline_hwnd", "0x" + baseline.Snapshot.Process.WindowHandle.ToString("X")),
                    AuditLog.Field("captured_hwnd", "0x" + window.ToInt64().ToString("X")),
                    AuditLog.Field("physical_pointer_captured", false), AuditLog.Field("read_only", true));
                Need(window != IntPtr.Zero && NativeMethods.IsWindow(window), "PROBE_NO_TARGET");
                var process = ProcessIdentity.Capture(window);
                Need(string.Equals(process.ProcessName, "KI-Messenger", StringComparison.OrdinalIgnoreCase), "PROBE_NOT_KI_MESSENGER");
                process.Log(log);
                CheckSwitchAlive(baseline, stopRequested);
                if (!baseline.Snapshot.Process.Equals(process))
                {
                    // A different KI window/process can be rejected without reading the other chat's content.
                    Need(process.Equals(ProcessIdentity.Capture(window)), "SWITCH_CURRENT_PROCESS_UNSTABLE");
                    CheckSwitchAlive(baseline, stopRequested);
                    return SwitchResult(log, true, baseline.Snapshot.Process.WindowHandle != process.WindowHandle
                        ? "SWITCH_WINDOW_CHANGED" : "SWITCH_PROCESS_CHANGED");
                }

                var current = ReadOnlyProbe.CaptureSnapshot(window, log, "TARGET_SWITCH", baseline.Marker, stopRequested);
                CheckSwitchAlive(baseline, stopRequested);
                bool changed;
                var reason = CompareTransition(baseline.Snapshot, baseline.SendRuntimeId, current, out changed);
                CheckSwitchAlive(baseline, stopRequested);
                return SwitchResult(log, changed, reason);
            }
            catch (MonitorException ex) { return SwitchResult(log, false, ex.ReasonCode); }
            catch (Exception ex)
            {
                log.WriteException("TARGET_SWITCH_FAILED", ex);
                return SwitchResult(log, false, "SWITCH_READ_FAILED");
            }
        }

        private static void CheckBaselineState(bool sameOwner, bool firstUse, TimeSpan age)
        {
            Need(sameOwner, "SWITCH_BASELINE_OWNER_MISMATCH");
            Need(firstUse, "SWITCH_BASELINE_ALREADY_USED");
            Need(age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(5), "SWITCH_BASELINE_EXPIRED");
        }

        private static void CheckSwitchAlive(Baseline baseline, Func<bool> stopRequested)
        {
            Need(!stopRequested(), "SWITCH_CANCELLED");
            CheckBaselineState(true, true, baseline.Age.Elapsed);
        }

        private static string SwitchResult(AuditLog log, bool changed, string reason)
        {
            var status = changed ? "TARGET_CHANGED_REJECTED" : "INCONCLUSIVE";
            log.Write("INFO", "TARGET_SWITCH_RESULT", AuditLog.Field("status", status), AuditLog.Field("reason", reason),
                AuditLog.Field("window_mode_verified", false),
                AuditLog.Field("conversation_identity_verified", false), AuditLog.Field("automatic_send_allowed", false));
            return status + " - " + reason + Environment.NewLine +
                (changed ? "A baseline target guard changed; this target is rejected. This does not authenticate a different conversation." :
                    "The switch test did not establish a target change. No unchanged result authenticates the same conversation.") + Environment.NewLine +
                "READ ONLY. No input or send. Baseline consumed; collect this log and leave the existing draft unchanged.";
        }

        private static string CompareTransition(ProbeSnapshot baseline, string sendRuntimeId, ProbeSnapshot current, out bool changed)
        {
            var beforeNodes = Validate(baseline);
            var afterNodes = Validate(current); // Incomplete reads are never evidence of a successfully detected switch.
            var selected = Select(baseline, sendRuntimeId);
            changed = true;
            if (!baseline.Process.Equals(current.Process)) return "SWITCH_PROCESS_CHANGED";
            if (baseline.RootNameFingerprint != current.RootNameFingerprint) return "SWITCH_ROOT_NAME_CHANGED";
            var afterRuntime = current.Nodes.ToDictionary(n => n.Identity.RuntimeId, StringComparer.Ordinal);
            foreach (var before in new[] { selected.Document, selected.Composer, selected.Input, selected.Toolbar, selected.SendGroup, selected.Send })
            {
                ProbeNode after;
                if (!afterRuntime.TryGetValue(before.Identity.RuntimeId, out after))
                {
                    changed = false; // Missing UIA identity cannot distinguish a chat switch from provider recreation.
                    return "SWITCH_ELEMENT_MISSING";
                }
                if (!before.Identity.Equals(after.Identity) || before.NativeHwnd != after.NativeHwnd) return "SWITCH_ELEMENT_CHANGED";
                if (after.Parent == 0 || beforeNodes[before.Parent].Identity.RuntimeId != afterNodes[after.Parent].Identity.RuntimeId)
                    return "SWITCH_ELEMENT_REPARENTED";
            }
            changed = false;
            var document = afterRuntime[selected.Document.Identity.RuntimeId];
            var drafts = current.Nodes.Count(n => n.Document == document.Node && Type(n, "Edit") && Usable(n) && n.ValueWritable && n.DraftExact);
            if (drafts == 0) return "SWITCH_DRAFT_CHANGED";
            if (drafts > 1) return "SWITCH_DRAFT_AMBIGUOUS";
            Recheck(baseline, selected, current);
            return "SWITCH_NO_OBSERVABLE_CHANGE";
        }

        private static string Rejected(string reason)
        {
            return "REJECTED - " + reason + Environment.NewLine +
                "Candidate mapping was not verified. READ ONLY. No input or send. Collect the diagnostic log.";
        }

        private static void LogResult(AuditLog log, string status, string reason)
        {
            log.Write("INFO", "PAIR_CHECK_RESULT", AuditLog.Field("status", status), AuditLog.Field("reason", reason),
                AuditLog.Field("window_mode_verified", false),
                AuditLog.Field("conversation_identity_verified", false), AuditLog.Field("automatic_send_allowed", false));
        }

        private static void LogSelection(AuditLog log, string phase, Selection selection)
        {
            log.Write("INFO", "PAIR_SELECTION", AuditLog.Field("phase", phase),
                AuditLog.Field("document_node", selection.Document.Node),
                AuditLog.Field("document_native_hwnd", "0x" + unchecked((uint)selection.Document.NativeHwnd).ToString("X", CultureInfo.InvariantCulture)),
                AuditLog.Field("ignored_non_native_documents", selection.IgnoredDocuments),
                AuditLog.Field("composer_node", selection.Composer.Node), AuditLog.Field("input_node", selection.Input.Node),
                AuditLog.Field("toolbar_node", selection.Toolbar.Node), AuditLog.Field("send_group_node", selection.SendGroup.Node),
                AuditLog.Field("invoke_candidate_node", selection.Send.Node));
        }

        private static void CheckCancelled(Func<bool> cancelled)
        {
            Need(!cancelled(), "PAIR_CANCELLED");
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new MonitorException(reason, "Read-only candidate mapping rejected: " + reason + ".");
        }

        private static bool Type(ProbeNode node, string type)
        {
            return node.Identity.ControlType == "ControlType." + type;
        }

        private static bool Usable(ProbeNode node) { return node.Visible && node.Enabled; }

        internal static string LayoutRejection(IEnumerable<ProbeNode> nodes)
        {
            // Deny-only evidence. Absence never proves a standalone window or grants input/send permission.
            // ponytail: rescans at most ReadOnlyProbe.MaxNodes nodes; cache counts only if profiling shows this scan matters.
            var visible = nodes.Where(n => n != null && n.Identity != null && n.Visible).ToList();
            if (visible.Any(n => Type(n, "List"))) return "LAYOUT_VISIBLE_LIST";
            // Separate provider Documents can mirror the same editor; never merge by Name or count them together.
            if (visible.Where(n => Type(n, "Edit") && n.ValueWritable).GroupBy(n => n.Document).Any(g => g.Count() > 1))
                return "LAYOUT_MULTIPLE_VISIBLE_EDITS";
            return null;
        }

        private static Dictionary<int, ProbeNode> Validate(ProbeSnapshot snapshot)
        {
            if (snapshot != null) Need(snapshot.LayoutRejection == null, snapshot.LayoutRejection);
            Need(snapshot != null && snapshot.Complete, "PAIR_SNAPSHOT_INCOMPLETE");
            Need(snapshot.Process != null && snapshot.Process.ProcessId > 0 &&
                string.Equals(snapshot.Process.ProcessName, "KI-Messenger", StringComparison.OrdinalIgnoreCase), "PAIR_PROCESS_UNAVAILABLE");
            Need(!string.IsNullOrEmpty(snapshot.RootNameFingerprint), "PAIR_ROOT_NAME_UNAVAILABLE");
            Need(snapshot.Nodes != null && snapshot.Nodes.Count > 0 && snapshot.Nodes.Count <= ReadOnlyProbe.MaxNodes, "PAIR_TREE_INVALID");
            var byNode = new Dictionary<int, ProbeNode>();
            var runtimeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in snapshot.Nodes)
            {
                Need(node != null && node.Node > 0 && node.Parent >= 0 && node.Identity != null &&
                    !string.IsNullOrEmpty(node.Identity.RuntimeId) && !string.IsNullOrEmpty(node.Identity.ControlType), "PAIR_NODE_IDENTITY_INVALID");
                Need(node.Identity.ProcessId == snapshot.Process.ProcessId, "PAIR_FOREIGN_PROCESS_NODE");
                Need(!byNode.ContainsKey(node.Node) && runtimeIds.Add(node.Identity.RuntimeId), "PAIR_DUPLICATE_NODE_IDENTITY");
                byNode.Add(node.Node, node);
            }
            ProbeNode root;
            Need(byNode.TryGetValue(1, out root) && root.Parent == 0, "PAIR_ROOT_INVALID");
            Need(root.NativeHwnd != 0 && unchecked((uint)root.NativeHwnd) == unchecked((uint)snapshot.Process.WindowHandle), "PAIR_ROOT_INVALID");
            foreach (var node in snapshot.Nodes)
            {
                var current = node;
                var nearestDocument = 0;
                var steps = 0;
                while (true)
                {
                    Need(++steps <= ReadOnlyProbe.MaxNodes, "PAIR_TREE_INVALID");
                    if (nearestDocument == 0 && Type(current, "Document")) nearestDocument = current.Node;
                    if (current.Parent == 0)
                    {
                        Need(current.Node == 1, "PAIR_TREE_INVALID");
                        break;
                    }
                    Need(byNode.TryGetValue(current.Parent, out current), "PAIR_TREE_INVALID");
                }
                Need(node.Document == nearestDocument, "PAIR_DOCUMENT_RELATION_INVALID");
            }
            var layoutReason = LayoutRejection(snapshot.Nodes);
            Need(layoutReason == null, layoutReason);
            return byNode;
        }

        private static bool Under(ProbeNode node, int ancestor, Dictionary<int, ProbeNode> byNode)
        {
            // Validate has already established an acyclic, bounded tree rooted at node 1.
            var steps = 0;
            while (node.Node != ancestor && node.Parent != 0)
            {
                Need(++steps <= ReadOnlyProbe.MaxNodes, "PAIR_TREE_INVALID");
                node = byNode[node.Parent];
            }
            return node.Node == ancestor;
        }

        private static ProbeNode One(IEnumerable<ProbeNode> nodes, string reason)
        {
            var matches = nodes.Take(2).ToArray();
            Need(matches.Length == 1, reason);
            return matches[0];
        }

        internal static ProbeNode SelectHoveredSend(ProbeSnapshot snapshot)
        {
            // Metadata inspection needs a unique input, not its private contents or a typed marker.
            return Select(snapshot, null, false).Send;
        }

        internal static ProbeNode SelectHoveredInput(ProbeSnapshot snapshot)
        {
            return Select(snapshot, null, false).Input;
        }

        internal static ProbeNode SelectAutomaticSend(ProbeSnapshot snapshot)
        {
            return Select(snapshot, null, false, true).Send;
        }

        internal static ProbeNode SelectAutomaticInput(ProbeSnapshot snapshot)
        {
            return Select(snapshot, null, false, true).Input;
        }

        private static Selection Select(ProbeSnapshot snapshot, string previousSendRuntimeId, bool requireDraft = true,
            bool automaticSendSelection = false)
        {
            var byNode = Validate(snapshot);
            // The native direct-child Document chooses a provider path, not a name/fingerprint merge.
            var document = One(snapshot.Nodes.Where(n => Type(n, "Document") && n.NativeHwnd != 0), "PAIR_NATIVE_DOCUMENT_NOT_UNIQUE");
            Need(document.Parent == 1 && Usable(document), "PAIR_NATIVE_DOCUMENT_INVALID");
            var within = snapshot.Nodes.Where(n => n.Document == document.Node).ToList();
            var input = One(within.Where(n => Type(n, "Edit") && Usable(n) && n.ValueWritable && (!requireDraft || n.DraftExact)),
                requireDraft ? "PAIR_DRAFT_NOT_UNIQUE" : "PAIR_INPUT_NOT_UNIQUE");
            ProbeNode send = null;
            if (!automaticSendSelection)
            {
                send = previousSendRuntimeId == null
                    ? One(within.Where(n => Type(n, "Hyperlink") && Usable(n) && n.Invoke && n.PointerInside), "PAIR_POINTER_NOT_UNIQUE")
                    : One(within.Where(n => n.Identity.RuntimeId == previousSendRuntimeId), "PAIR_SEND_NOT_REACQUIRED");
                Need(Type(send, "Hyperlink") && Usable(send) && send.Invoke, "PAIR_SEND_CANDIDATE_INVALID");
            }

            var composer = byNode[input.Parent];
            Need(Type(composer, "Custom") && Usable(composer) && composer.Document == document.Node, "PAIR_COMPOSER_INVALID");
            Need(snapshot.Nodes.Count(n => Type(n, "Edit") && n.ValueWritable && Under(n, composer.Node, byNode)) == 1, "PAIR_COMPOSER_EDIT_NOT_UNIQUE");
            var composerChildren = snapshot.Nodes.Where(n => n.Parent == composer.Node).ToList();
            Need(composerChildren.Count == 2, "PAIR_COMPOSER_SHAPE_CHANGED");
            var toolbar = One(composerChildren.Where(n => n.Node != input.Node && Type(n, "Custom") && Usable(n)), "PAIR_TOOLBAR_INVALID");
            if (automaticSendSelection)
            {
                // Structural candidate only: no labels, pointer, runtime-ID decoding or conversation approval.
                var groups = snapshot.Nodes.Where(n => n.Parent == toolbar.Node).ToList();
                Need(groups.Count == 2 && groups.All(n => Type(n, "Custom") && Usable(n) && n.Document == document.Node),
                    "PAIR_AUTOMATIC_TOOLBAR_SHAPE_CHANGED");
                send = One(within.Where(n => Type(n, "Hyperlink") && Usable(n) && n.Invoke &&
                    groups.Any(g => g.Node == n.Parent) && snapshot.Nodes.Count(child => child.Parent == n.Parent) == 1),
                    "PAIR_AUTOMATIC_SEND_NOT_UNIQUE");
                var otherGroup = groups.Single(n => n.Node != send.Parent);
                Need(snapshot.Nodes.Count(n => n.Parent == otherGroup.Node) > 1, "PAIR_AUTOMATIC_OTHER_GROUP_INVALID");
            }
            var sendGroup = byNode[send.Parent];
            Need(Type(sendGroup, "Custom") && Usable(sendGroup) && sendGroup.Parent == toolbar.Node, "PAIR_SEND_GROUP_INVALID");
            var groupChildren = snapshot.Nodes.Where(n => n.Parent == sendGroup.Node).ToList();
            Need(groupChildren.Count == 1 && groupChildren[0].Node == send.Node, "PAIR_SEND_GROUP_SHAPE_CHANGED");
            return new Selection
            {
                Document = document, Composer = composer, Input = input, Toolbar = toolbar, SendGroup = sendGroup, Send = send,
                IgnoredDocuments = snapshot.Nodes.Count(n => Type(n, "Document") && n.NativeHwnd == 0)
            };
        }

        private static Selection Recheck(ProbeSnapshot first, Selection selected, ProbeSnapshot second)
        {
            var reacquired = Select(second, selected.Send.Identity.RuntimeId);
            Need(first.Process.Equals(second.Process), "PAIR_PROCESS_CHANGED");
            Need(string.Equals(first.RootNameFingerprint, second.RootNameFingerprint, StringComparison.Ordinal), "PAIR_ROOT_NAME_CHANGED");
            var before = new[] { selected.Document, selected.Composer, selected.Input, selected.Toolbar, selected.SendGroup, selected.Send };
            var after = new[] { reacquired.Document, reacquired.Composer, reacquired.Input, reacquired.Toolbar, reacquired.SendGroup, reacquired.Send };
            for (var i = 0; i < before.Length; i++)
                Need(before[i].Identity.Equals(after[i].Identity) && before[i].NativeHwnd == after[i].NativeHwnd, "PAIR_SELECTION_CHANGED");
            return reacquired;
        }

        private sealed class Selection
        {
            public ProbeNode Document, Composer, Input, Toolbar, SendGroup, Send;
            public int IgnoredDocuments;
        }

        internal static void RunSelfTest()
        {
            var first = Fixture();
            var selected = Select(first, null);
            var pointSnapshot = Fixture();
            foreach (var node in pointSnapshot.Nodes)
                node.Identity = TestIdentity(node.Node.ToString(CultureInfo.InvariantCulture),
                    node.Identity.ControlType.Substring("ControlType.".Length));
            Require(UiaPointProbe.CompareRuntimeId(new[] { 12 }, pointSnapshot) == "CANONICAL" &&
                UiaPointProbe.CompareRuntimeId(new[] { 11 }, pointSnapshot) == "ANCESTOR_CONTAINER" &&
                UiaPointProbe.CompareRuntimeId(new[] { 1 }, pointSnapshot) == "ANCESTOR_CONTAINER" &&
                UiaPointProbe.CompareRuntimeId(new[] { 41 }, pointSnapshot) == "MIRROR_OR_OTHER" &&
                UiaPointProbe.CompareRuntimeId(new[] { 9 }, pointSnapshot) == "MIRROR_OR_OTHER" &&
                UiaPointProbe.CompareRuntimeId(new[] { 999 }, pointSnapshot) == "NOT_IN_SNAPSHOT",
                "UIA point comparison confused Send, ancestor, mirror or unknown elements");
            var withoutDraft = Fixture();
            foreach (var node in withoutDraft.Nodes) node.DraftExact = false;
            Require(SelectHoveredSend(withoutDraft).Node == 12, "metadata inspection required a draft or chose the mirror");
            Require(SelectHoveredInput(withoutDraft).Node == 5, "supervised precheck chose a different input provider");
            withoutDraft.Nodes.Single(n => n.Node == 5).ValueWritable = false;
            try { SelectHoveredSend(withoutDraft); throw new InvalidOperationException("metadata accepted a missing writable input"); }
            catch (MonitorException ex) { Require(ex.ReasonCode == "PAIR_INPUT_NOT_UNIQUE", "wrong metadata input rejection"); }
            Require(selected.Document.Node == 2 && selected.Input.Node == 5 && selected.Send.Node == 12 && selected.IgnoredDocuments == 1,
                "the native provider path was not selected");
            var automatic = Fixture();
            foreach (var node in automatic.Nodes) { node.PointerInside = false; node.DraftExact = false; }
            Require(SelectAutomaticSend(automatic).Node == 12 && SelectAutomaticInput(automatic).Node == 5,
                "automatic structural selection required a pointer/draft or chose the mirrored provider");
            foreach (var node in automatic.Nodes) node.PointerInside = true;
            Require(SelectAutomaticSend(automatic).Node == 12, "automatic structural selection used pointer flags");
            Require(ReadOnlyProbe.IsValidSnapshotRequest("SEND_METADATA", null, null, true, true) &&
                ReadOnlyProbe.IsValidSnapshotRequest("SEND_METADATA", null, new System.Windows.Point(1, 2), true, true) &&
                !ReadOnlyProbe.IsValidSnapshotRequest("SEND_METADATA", null, null, false, true) &&
                !ReadOnlyProbe.IsValidSnapshotRequest("SEND_METADATA", "D234567", null, true, true) &&
                !ReadOnlyProbe.IsValidSnapshotRequest("MESSAGE", "M234567", null, true, true) &&
                !ReadOnlyProbe.IsValidSnapshotRequest("SEND_METADATA", null, new System.Windows.Point(double.NaN, 2), true, true) &&
                !ReadOnlyProbe.IsValidSnapshotRequest("SEND_METADATA", null, null, true, false),
                "automatic snapshot request escaped its metadata/retention scope or changed legacy hover validation");
            var second = Fixture();
            foreach (var node in second.Nodes) node.PointerInside = false;
            Require(Recheck(first, selected, second).Send.Node == 12, "recheck depended on a pointer");

            ProbeSnapshot BoundedTree(int count)
            {
                var snapshot = Fixture();
                for (var node = 42; snapshot.Nodes.Count < count; node++)
                    snapshot.Nodes.Add(TestNode(node, 1, 0, visible: false));
                return snapshot;
            }
            Require(ReadOnlyProbe.MaxNodes == 2048, "node cap changed");
            Require(Select(BoundedTree(401), null).Input.Node == 5, "401-node tree was rejected");
            Require(Select(BoundedTree(ReadOnlyProbe.MaxNodes), null).Input.Node == 5, "max-node tree was rejected");

            void Reject(string label, Action<ProbeSnapshot> change, bool recheck = false, string reason = null, bool automaticSelection = false)
            {
                var altered = Fixture();
                change(altered);
                try
                {
                    if (recheck) Recheck(first, selected, altered); else Select(altered, null, !automaticSelection, automaticSelection);
                }
                catch (MonitorException ex) { Require(reason == null || reason == ex.ReasonCode, "wrong rejection: " + label); return; }
                throw new InvalidOperationException("Read-only pair self-test accepted " + label + ".");
            }

            ProbeNode At(ProbeSnapshot snapshot, int number) { return snapshot.Nodes.Single(n => n.Node == number); }
            Reject("automatic ambiguous singleton groups", s => s.Nodes.RemoveAll(n => n.Node == 8 || n.Node == 10),
                reason: "PAIR_AUTOMATIC_SEND_NOT_UNIQUE", automaticSelection: true);
            Reject("automatic wrong toolbar group", s => At(s, 12).Parent = 6, automaticSelection: true);
            Reject("automatic third toolbar group", s => s.Nodes.Add(TestNode(50, 6, 2)), automaticSelection: true);
            Reject("automatic singleton other group", s => s.Nodes.RemoveAll(n => n.Node == 9 || n.Node == 10),
                reason: "PAIR_AUTOMATIC_OTHER_GROUP_INVALID", automaticSelection: true);
            Reject("automatic non-singleton Send group", s => s.Nodes.Add(TestNode(50, 11, 2)), automaticSelection: true);
            Reject("automatic mirror-only Send", s => At(s, 12).Invoke = false,
                reason: "PAIR_AUTOMATIC_SEND_NOT_UNIQUE", automaticSelection: true);
            Reject("automatic hidden input", s => At(s, 5).Visible = false, automaticSelection: true);
            Reject("automatic disabled Send", s => At(s, 12).Enabled = false, automaticSelection: true);
            Reject("automatic visible mirrored list", s => s.Nodes.Add(TestNode(50, 22, 21, "List")),
                reason: "LAYOUT_VISIBLE_LIST", automaticSelection: true);
            Reject("tree above node cap", s => s.Nodes = BoundedTree(ReadOnlyProbe.MaxNodes + 1).Nodes, reason: "PAIR_TREE_INVALID");
            Reject("incomplete snapshot", s => { s.Nodes = BoundedTree(401).Nodes; s.Complete = false; }, reason: "PAIR_SNAPSHOT_INCOMPLETE");
            Reject("early layout rejection", s => { s.Complete = false; s.LayoutRejection = "LAYOUT_VISIBLE_LIST"; }, reason: "LAYOUT_VISIBLE_LIST");
            Reject("visible list outside composer", s => s.Nodes.Add(TestNode(50, 3, 2, "List")), reason: "LAYOUT_VISIBLE_LIST");
            Reject("disabled visible list", s => { var n = TestNode(50, 3, 2, "List"); n.Enabled = false; s.Nodes.Add(n); });
            Reject("list on mirrored provider", s => s.Nodes.Add(TestNode(50, 22, 21, "List")));
            Reject("additional visible editor outside composer", s => s.Nodes.Add(TestNode(50, 3, 2, "Edit")), reason: "LAYOUT_MULTIPLE_VISIBLE_EDITS");
            Reject("disabled additional visible editor", s => { var n = TestNode(50, 3, 2, "Edit"); n.Enabled = false; s.Nodes.Add(n); }, reason: "LAYOUT_MULTIPLE_VISIBLE_EDITS");
            Reject("visible list appearing on recheck", s => s.Nodes.Add(TestNode(50, 3, 2, "List")), true);
            Reject("extra editor appearing on recheck", s => s.Nodes.Add(TestNode(50, 3, 2, "Edit")), true);
            var hiddenTools = Fixture();
            hiddenTools.Nodes.Add(TestNode(50, 3, 2, "List", visible: false));
            hiddenTools.Nodes.Add(TestNode(51, 3, 2, "Edit", visible: false));
            Require(LayoutRejection(hiddenTools.Nodes) == null && Select(hiddenTools, null).Input.Node == 5,
                "hidden search tools or mirrored editor caused a layout rejection");
            Require(LayoutRejection(new ProbeNode[0]) == null, "empty partial read should not invent layout evidence");
            Reject("empty complete snapshot", s => s.Nodes.Clear());
            Reject("missing process", s => s.Process = null);
            Reject("missing root fingerprint", s => s.RootNameFingerprint = null);
            Reject("missing native document", s => At(s, 2).NativeHwnd = 0);
            Reject("two native documents", s => s.Nodes.Add(TestNode(30, 1, 30, "Document", native: 300)));
            Reject("non-direct native document", s => { s.Nodes.Add(TestNode(30, 1, 0)); At(s, 2).Parent = 30; });
            Reject("no exact draft", s => At(s, 5).DraftExact = false);
            Reject("two exact drafts", s => s.Nodes.Add(TestNode(30, 3, 2, "Edit", draft: true)));
            Reject("another hidden writable composer editor", s => s.Nodes.Add(TestNode(30, 7, 2, "Edit", visible: false)));
            Reject("hidden input", s => At(s, 5).Visible = false);
            Reject("disabled input", s => At(s, 5).Enabled = false);
            Reject("readonly input", s => At(s, 5).ValueWritable = false);
            Reject("hidden Send candidate", s => At(s, 12).Visible = false);
            Reject("disabled Send candidate", s => At(s, 12).Enabled = false);
            Reject("non-Invoke Send candidate", s => At(s, 12).Invoke = false);
            Reject("two pointer candidates", s => At(s, 9).PointerInside = true);
            Reject("no pointer candidate", s => At(s, 12).PointerInside = false);
            Reject("wrong toolbar Hyperlink", s => { At(s, 12).PointerInside = false; At(s, 9).PointerInside = true; });
            Reject("additional group child", s => s.Nodes.Add(TestNode(30, 11, 2)));
            Reject("additional composer child", s => s.Nodes.Add(TestNode(30, 4, 2)));
            Reject("nested document composer child", s => s.Nodes.Add(TestNode(30, 4, 30, "Document")));
            Reject("nested document group child", s => s.Nodes.Add(TestNode(30, 11, 30, "Document")));
            Reject("writable editor in nested document", s =>
            {
                s.Nodes.Add(TestNode(30, 7, 30, "Document"));
                s.Nodes.Add(TestNode(31, 30, 30, "Edit", visible: false));
            });
            Reject("group outside toolbar", s => At(s, 11).Parent = 3);
            Reject("foreign mirrored node", s => At(s, 27).Identity = TestIdentity("foreign", "Hyperlink", pid: 11));
            Reject("duplicate runtime identity", s => At(s, 27).Identity = At(s, 12).Identity);
            Reject("missing identity", s => At(s, 5).Identity = null);
            Reject("missing runtime identity", s => At(s, 5).Identity = TestIdentity("", "Edit"));
            Reject("duplicate node", s => At(s, 27).Node = 5);
            Reject("false document membership", s => At(s, 12).Document = 21);
            Reject("parent cycle", s => At(s, 3).Parent = 4);
            Reject("missing parent", s => At(s, 12).Parent = 999);

            Reject("changed process", s => s.Process = TestProcess(start: 2), true);
            Reject("changed root name", s => s.RootNameFingerprint = "different", true);
            Reject("stale document", s => At(s, 2).Identity = TestIdentity("new-document", "Document"), true);
            Reject("changed native document handle", s => At(s, 2).NativeHwnd = 201, true);
            Reject("stale input", s => At(s, 5).Identity = TestIdentity("new-input", "Edit"), true);
            Reject("stale Send", s => At(s, 12).Identity = TestIdentity("new-send", "Hyperlink"), true);
            Reject("changed input Name", s => At(s, 5).Identity = TestIdentity("node-5", "Edit", name: "changed"), true);
            Reject("reparented Send", s => { s.Nodes.Add(TestNode(30, 6, 2)); At(s, 12).Parent = 30; }, true);
            Reject("reparented input", s => { s.Nodes.Add(TestNode(30, 3, 2)); At(s, 5).Parent = 30; At(s, 6).Parent = 30; }, true);
            Reject("pointer fallback after stale Send", s => { At(s, 12).Identity = TestIdentity("new-send", "Hyperlink"); At(s, 9).PointerInside = true; }, true);

            // Snapshot ordinal numbers may change; identities and the proven parent relationships may not.
            var renumbered = Fixture();
            foreach (var node in renumbered.Nodes)
            {
                if (node.Node != 1) node.Node += 100;
                if (node.Parent > 1) node.Parent += 100;
                if (node.Document > 0) node.Document += 100;
                node.PointerInside = false;
            }
            Require(Recheck(first, selected, renumbered).Input.Node == 105, "recheck relied on snapshot ordinal numbers");
            var consentBaseline = new Baseline(first, "fixture", "D234567", null);
            foreach (var afterInvoke in new[] { false, true })
            {
                var diagnostic = new IntPtr(101);
                var target = new IntPtr(202);
                Require(SendForegroundAllowed(diagnostic, diagnostic, target, afterInvoke), "diagnostic foreground rejected");
                Require(SendForegroundAllowed(target, diagnostic, target, afterInvoke) == afterInvoke, "target allowed before Invoke");
                Require(!SendForegroundAllowed(new IntPtr(303), diagnostic, target, afterInvoke), "another foreground window allowed");
                Require(!SendForegroundAllowed(IntPtr.Zero, diagnostic, target, afterInvoke), "missing foreground allowed");
                Require(!SendForegroundAllowed(diagnostic, diagnostic, diagnostic, afterInvoke), "same diagnostic and target allowed");
                Require(!SendForegroundAllowed(diagnostic, diagnostic, IntPtr.Zero, afterInvoke), "missing target allowed");
                Require(!SendForegroundAllowed(target, IntPtr.Zero, target, afterInvoke), "missing diagnostic allowed");
            }
            var cancelledConsent = new SendConsent(consentBaseline, true);
            Require(!cancelledConsent.Cancel() && !cancelledConsent.TryCommit() && !cancelledConsent.Attempted,
                "cancel before commit allowed an Invoke");
            var committedConsent = new SendConsent(consentBaseline, true);
            Require(committedConsent.TryCommit() && !committedConsent.TryCommit() && committedConsent.Cancel() && committedConsent.Attempted,
                "one-shot commit was reusable or cancellation hid an attempted Invoke");
            foreach (var confirmed in new[] { false })
            {
                try { new SendConsent(consentBaseline, confirmed); }
                catch (MonitorException ex) { Require(ex.ReasonCode == "SEND_CONFIRMATION_REQUIRED", "wrong consent rejection"); continue; }
                throw new InvalidOperationException("An unconfirmed Send was authorized.");
            }
            RunTransitionSelfTest();
            try { CheckCancelled(() => true); }
            catch (MonitorException ex) { Require(ex.ReasonCode == "PAIR_CANCELLED", "wrong cancellation reason"); return; }
            throw new InvalidOperationException("Read-only pair self-test ignored cancellation.");
        }

        private static void RunTransitionSelfTest()
        {
            var baseline = Fixture();
            var runtime = Select(baseline, null).Send.Identity.RuntimeId;
            void Check(string label, Action<ProbeSnapshot> change, bool expectChanged, string expectReason)
            {
                var current = Fixture();
                foreach (var node in current.Nodes) node.PointerInside = false;
                change(current);
                bool changed;
                var reason = CompareTransition(baseline, runtime, current, out changed);
                Require(changed == expectChanged && reason == expectReason, "transition classification: " + label);
            }
            ProbeNode At(ProbeSnapshot snapshot, int number) { return snapshot.Nodes.Single(n => n.Node == number); }
            Check("same names/identities never authenticate", s => { }, false, "SWITCH_NO_OBSERVABLE_CHANGE");
            Check("renumbered nodes", s =>
            {
                foreach (var node in s.Nodes)
                {
                    if (node.Node > 1) node.Node += 100;
                    if (node.Parent > 1) node.Parent += 100;
                    if (node.Document > 0) node.Document += 100;
                }
            }, false, "SWITCH_NO_OBSERVABLE_CHANGE");
            Check("changed root with missing draft", s => { s.RootNameFingerprint = "other"; At(s, 5).DraftExact = false; }, true, "SWITCH_ROOT_NAME_CHANGED");
            Check("changed process", s => s.Process = TestProcess(2), true, "SWITCH_PROCESS_CHANGED");
            Check("missing selected runtime", s => At(s, 12).Identity = TestIdentity("other", "Hyperlink"), false, "SWITCH_ELEMENT_MISSING");
            Check("changed input metadata", s => At(s, 5).Identity = TestIdentity("node-5", "Edit", name: "changed"), true, "SWITCH_ELEMENT_CHANGED");
            Check("changed native handle", s => At(s, 2).NativeHwnd = 201, true, "SWITCH_ELEMENT_CHANGED");
            Check("changed parent", s => { s.Nodes.Add(TestNode(50, 6, 2)); At(s, 11).Parent = 50; }, true, "SWITCH_ELEMENT_REPARENTED");
            Check("draft alone disappeared", s => At(s, 5).DraftExact = false, false, "SWITCH_DRAFT_CHANGED");
            foreach (var mutate in new Action<ProbeSnapshot>[]
            {
                s => { s.Complete = false; s.RootNameFingerprint = "changed"; },
                s => { s.Complete = false; s.LayoutRejection = "LAYOUT_VISIBLE_LIST"; },
                s => s.Nodes.Add(TestNode(50, 3, 2, "List")),
                s => s.Nodes.Add(TestNode(50, 3, 2, "Edit", draft: true)),
                s => At(s, 12).Identity = null,
                s => At(s, 12).Identity = At(s, 5).Identity,
                s => At(s, 12).Document = 21
            })
            {
                var current = Fixture(); mutate(current);
                try { bool changed; CompareTransition(baseline, runtime, current, out changed); }
                catch (MonitorException) { continue; }
                throw new InvalidOperationException("Incomplete/invalid transition evidence was accepted.");
            }
            CheckBaselineState(true, true, TimeSpan.Zero);
            CheckBaselineState(true, true, TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
            foreach (var check in new Action[]
            {
                () => CheckBaselineState(false, true, TimeSpan.Zero),
                () => CheckBaselineState(true, false, TimeSpan.Zero),
                () => CheckBaselineState(true, true, TimeSpan.FromMinutes(5)),
                () => CheckBaselineState(true, true, TimeSpan.FromTicks(-1))
            })
            {
                try { check(); }
                catch (MonitorException) { continue; }
                throw new InvalidOperationException("Invalid transition baseline state was accepted.");
            }
        }

        private static ProbeSnapshot Fixture()
        {
            // Small synthetic graph, with distinct runtime IDs for the non-native mirrored provider.
            return new ProbeSnapshot
            {
                Complete = true, Process = TestProcess(), RootNameFingerprint = "same-run-root-fingerprint",
                Nodes = new List<ProbeNode>
                {
                    TestNode(1, 0, 0, "Pane", native: 100), TestNode(2, 1, 2, "Document", native: 200),
                    TestNode(3, 2, 2), TestNode(4, 3, 2), TestNode(5, 4, 2, "Edit", draft: true), TestNode(6, 4, 2),
                    TestNode(7, 6, 2), TestNode(8, 7, 2), TestNode(9, 7, 2, "Hyperlink"), TestNode(10, 7, 2),
                    TestNode(11, 6, 2), TestNode(12, 11, 2, "Hyperlink", pointer: true),
                    TestNode(20, 1, 0, "Pane"), TestNode(21, 20, 21, "Document"), TestNode(22, 21, 21),
                    TestNode(23, 22, 21), TestNode(24, 23, 21, "Edit", draft: true), TestNode(25, 23, 21),
                    TestNode(26, 25, 21), TestNode(27, 26, 21), TestNode(28, 26, 21, "Hyperlink"), TestNode(29, 26, 21),
                    TestNode(40, 25, 21), TestNode(41, 40, 21, "Hyperlink", pointer: true)
                }
            };
        }

        private static ProbeNode TestNode(int node, int parent, int document, string type = "Custom", int native = 0,
            bool draft = false, bool pointer = false, bool visible = true)
        {
            return new ProbeNode { Node = node, Parent = parent, Document = document, NativeHwnd = native,
                Identity = TestIdentity("node-" + node, type), Enabled = true, Visible = visible,
                ValueWritable = type == "Edit", Invoke = type == "Hyperlink", DraftExact = draft, PointerInside = pointer };
        }

        private static ElementIdentity TestIdentity(string runtime, string type, int pid = 10, string name = "")
        {
            return new ElementIdentity(runtime, pid, "", "ControlType." + type, "", "fixture", "",
                name.Length, TokenStore.Hash(name), "<redacted>");
        }

        private static ProcessIdentity TestProcess(long start = 1)
        {
            // Exercise the existing full identity equality without opening any process, executable, or private log.
            return (ProcessIdentity)Activator.CreateInstance(typeof(ProcessIdentity), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { 10, start, "KI-Messenger", @"C:\fixture\KI-Messenger.exe", "fixture", "1", "1", 1L, 1L,
                    "UNSIGNED", "", "", "fixture", 100L }, CultureInfo.InvariantCulture);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Read-only pair self-test failed: " + message);
        }
    }
}
