using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Automation;

namespace RemoteMonitorMaster
{
    internal static class ReceiveProbe
    {
        internal sealed class HistorySelection
        {
            internal ProbeNode Root, Document, Composer, Input, History;
            internal Dictionary<int, ProbeNode> ByNode;
            internal List<ProbeNode> Texts;
        }

        internal sealed class Baseline
        {
            internal readonly ProbeSnapshot Snapshot;
            internal readonly HistorySelection Selection;
            internal readonly string Marker;
            internal readonly string MarkerHash;
            internal readonly bool PlainCommands;
            internal readonly int ReadyRow;

            internal Baseline(ProbeSnapshot snapshot, HistorySelection selection, string marker, string markerHash,
                bool plainCommands = false, int readyRow = -1)
            {
                Snapshot = snapshot; Selection = selection; Marker = marker; MarkerHash = markerHash;
                PlainCommands = plainCommands;
                ReadyRow = readyRow;
            }
        }

        // An observation handed to the same locally authorized run, never a credential or a body-authentication result.
        internal sealed class ObservationProof
        {
            internal readonly object Owner;
            internal readonly IntPtr Window;
            internal readonly NativeMethods.WindowRectangle Bounds;
            internal readonly Baseline Baseline;
            internal readonly ProbeSnapshot Previous, Final;
            internal readonly ProbeNode PreviousCandidate, Candidate;
            internal readonly Stopwatch Age;
            internal readonly TimeSpan AgeOffset;
            internal TimeSpan Elapsed { get { return Age.Elapsed + AgeOffset; } }
            internal bool DiagnosticTokenReserved;
            private int used;

            internal ObservationProof(object owner, IntPtr window, NativeMethods.WindowRectangle bounds, Baseline baseline,
                ProbeSnapshot previous, ProbeNode previousCandidate, ProbeSnapshot final, ProbeNode candidate,
                TimeSpan? ageOffset = null, Stopwatch ageClock = null)
            {
                Owner = owner; Window = window; Bounds = bounds; Baseline = baseline;
                Previous = previous; PreviousCandidate = previousCandidate; Final = final; Candidate = candidate;
                Age = ageClock ?? Stopwatch.StartNew();
                AgeOffset = ageOffset ?? TimeSpan.Zero;
            }
            internal bool TryConsume() { return Interlocked.CompareExchange(ref used, 1, 0) == 0; }
        }

        // One fresh accepted-request observation, not permission to send. The sender supplies the second snapshot.
        internal sealed class SingleObservation
        {
            internal readonly ObservationProof Accepted;
            internal readonly object Owner;
            internal readonly IntPtr Window;
            internal readonly NativeMethods.WindowRectangle Bounds;
            internal readonly ProbeSnapshot Snapshot;
            internal readonly ProbeNode Candidate;
            internal readonly Stopwatch Age;
            internal readonly TimeSpan AgeOffset;
            internal TimeSpan Elapsed { get { return Age.Elapsed + AgeOffset; } }
            private int used;

            internal SingleObservation(ObservationProof accepted, object owner, IntPtr window,
                NativeMethods.WindowRectangle bounds, ProbeSnapshot snapshot, ProbeNode candidate,
                Stopwatch age, TimeSpan? ageOffset = null)
            {
                Accepted = accepted; Owner = owner; Window = window; Bounds = bounds;
                Snapshot = snapshot; Candidate = candidate; Age = age;
                AgeOffset = ageOffset ?? TimeSpan.Zero;
            }
            internal bool TryConsume() { return Interlocked.CompareExchange(ref used, 1, 0) == 0; }
        }

        internal sealed class ObservationResult
        {
            internal readonly string Status, Reason, Message;
            internal readonly ObservationProof Proof;
            internal readonly SingleObservation Single;
            internal ObservationResult(string status, string reason, string message, ObservationProof proof = null,
                SingleObservation single = null)
            { Status = status; Reason = reason; Message = message; Proof = proof; Single = single; }
        }

        internal static HistorySelection SelectHistory(ProbeSnapshot snapshot)
        {
            var input = ReadOnlyPair.SelectAutomaticInput(snapshot); // Complete, bounded, acyclic graph and deny-only layout guards.
            var byNode = snapshot.Nodes.ToDictionary(n => n.Node);
            var composer = byNode[input.Parent];
            var within = snapshot.Nodes.Where(n => n.Document == input.Document).ToList();
            bool Under(ProbeNode node, int ancestor)
            {
                while (node.Node != ancestor && node.Parent != 0) node = byNode[node.Parent];
                return node.Node == ancestor;
            }
            var textNodes = within.Where(n => Type(n, "Text")).ToList();
            var histories = within.Where(n => n.Node != composer.Node && n.Parent == composer.Parent && Type(n, "Custom") && Usable(n))
                .Where(n =>
                {
                    var rows = within.Where(row => row.Parent == n.Node).ToList();
                    return rows.Count > 0 && rows.All(row => Type(row, "Custom") && textNodes.Any(text => Under(text, row.Node)));
                }).Take(2).ToArray();
            Need(histories.Length == 1, "RECEIVE_HISTORY_NOT_UNIQUE");
            return new HistorySelection { Root = byNode[1], Document = byNode[input.Document], Composer = composer,
                Input = input, History = histories[0], ByNode = byNode,
                Texts = textNodes.Where(n => Under(n, histories[0].Node)).ToList() };
        }

        // Includes only the history Texts and their Custom parents/grandparents. Capture retains these same live objects.
        internal static List<ProbeNode> MetadataNodes(ProbeSnapshot snapshot, ProbeNode priority)
        {
            var selection = SelectHistory(snapshot);
            var result = new List<ProbeNode>();
            var seen = new HashSet<int>();
            foreach (var text in selection.Texts.OrderByDescending(n => priority != null && n.Node == priority.Node)
                .ThenByDescending(Usable).ThenBy(n => n.Node))
            {
                var node = text;
                for (var depth = 0; depth < 3; depth++)
                {
                    if (node.Document != selection.Document.Node || (depth != 0 && !Type(node, "Custom"))) break;
                    if (seen.Add(node.Node)) result.Add(node);
                    if (node.Node == selection.History.Node || node.Parent == 0) break;
                    node = selection.ByNode[node.Parent];
                }
            }
            return result;
        }

        internal static Baseline CreateBaseline(ProbeSnapshot snapshot, string marker, bool plainCommands = false, int readyRow = -1)
        {
            Need(Protocol.IsDiagnosticMarker("MESSAGE", marker), "RECEIVE_MARKER_INVALID");
            var selection = SelectHistory(snapshot);
            var hash = TokenStore.Hash(marker);
            Need(snapshot.Nodes.All(n => !string.IsNullOrEmpty(n.Identity.NameHash)), "RECEIVE_NAME_METADATA_UNAVAILABLE");
            if (!plainCommands) Need(!snapshot.Nodes.Any(n => n.Identity.NameHash == hash), "RECEIVE_MARKER_ALREADY_PRESENT");
            if (readyRow >= 0)
                Need(plainCommands && IsReadyRow(selection, marker, readyRow), "RECEIVE_READY_BOUNDARY_CHANGED");
            return new Baseline(snapshot, selection, marker, hash, plainCommands, readyRow);
        }

        private static int ReadyRow(HistorySelection selection, string marker)
        {
            var hash = TokenStore.Hash(SupervisedSendTest.ReadyText("D" + marker.Substring(1)));
            var matches = HistoryRows(selection).Select((row, index) => new { Row = row, Index = index })
                .Where(row => row.Row.Any(n => MatchesReady(n, hash)))
                .Where(row => { var content = RowContent(row.Row); return content.Length == 1 && MatchesReady(content[0], hash); })
                .Take(2).ToArray();
            Need(matches.Length <= 1, "RECEIVE_READY_BOUNDARY_AMBIGUOUS");
            return matches.Length == 0 ? -1 : matches[0].Index;
        }

        private static bool IsReadyRow(HistorySelection selection, string marker, int index)
        {
            var rows = HistoryRows(selection);
            if (index < 0 || index >= rows.Count) return false;
            var content = RowContent(rows[index]);
            return content.Length == 1 && MatchesReady(content[0],
                TokenStore.Hash(SupervisedSendTest.ReadyText("D" + marker.Substring(1))));
        }

        private static bool MatchesReady(ProbeNode node, string hash)
        {
            return node.ReadyNoticeHash == hash && node.ReadyNoticeSourceHash == node.Identity.NameHash;
        }

        internal static void RequireReadyAbsent(ProbeSnapshot snapshot, string marker)
        {
            var hash = TokenStore.Hash(SupervisedSendTest.ReadyText("D" + marker.Substring(1)));
            Need(snapshot.Nodes.All(n => !MatchesReady(n, hash)), "RECEIVE_READY_ALREADY_PRESENT");
        }

        internal static Baseline BindReadyBoundary(Baseline before, ProbeSnapshot current, AuditLog log = null)
        {
            Need(before != null && before.PlainCommands && before.ReadyRow < 0, "RECEIVE_READY_BASELINE_REQUIRED");
            RequireReadyAbsent(before.Snapshot, before.Marker);
            var selected = ValidateContinuity(before, current);
            // A completed local Ready send starts a new admission interval. Old bodies are never requests.
            RequireHistoryPrefix(before.Selection, selected, log, int.MaxValue);
            var ready = ReadyRow(selected, before.Marker);
            if (ready < 0) return null; // The caller waits read-only within the existing 15-second phase budget.
            Need(ready >= HistoryRows(before.Selection).Count, "RECEIVE_READY_NOT_APPENDED");
            log?.Write("INFO", "RECEIVE_READY_BOUNDARY", AuditLog.Field("ready_row", ready),
                AuditLog.Field("earlier_rows_ignored", ready), AuditLog.Field("clock_is_order_key", false));
            return CreateBaseline(current, before.Marker, true, ready);
        }

        private static int FirstCommandRow(List<ProbeNode[]> rows, int start)
        {
            for (var i = start; i < rows.Count; i++)
            {
                var content = RowContent(rows[i]);
                string command;
                if (content.Length == 1 && ReadOnlyCommands.TryMatchNode(content[0], out command)) return i;
            }
            return rows.Count;
        }

        internal static ProbeNode Evaluate(Baseline baseline, ProbeSnapshot current, AuditLog log = null)
        {
            return EvaluateCore(baseline, current, log, true);
        }

        private static ProbeNode EvaluateCore(Baseline baseline, ProbeSnapshot current, AuditLog log, bool requireVisible)
        {
            var selected = ValidateContinuity(baseline, current);
            if (baseline.PlainCommands)
            {
                ValidatePlainHistoryPrefix(baseline.Snapshot, current, log, baseline, acceptedRequest: !requireVisible);
                // A request is one complete new message row. After removing one validated clock sibling,
                // split reply/LLM Text nodes and command fragments can never become a request.
                var rows = HistoryRows(selected);
                var oldCount = baseline.ReadyRow >= 0 ? baseline.ReadyRow + 1 : HistoryRows(baseline.Selection).Count;
                var firstCommandRow = baseline.ReadyRow >= 0 ? FirstCommandRow(rows, oldCount) : rows.Count;
                var appendedRows = rows.Skip(oldCount).Take(firstCommandRow - oldCount + 1).ToArray();
                var appended = appendedRows.Select(row =>
                {
                    var content = RowContent(row);
                    string command;
                    return content.Length == 1 && ReadOnlyCommands.TryMatchNode(content[0], out command)
                        ? content[0] : null;
                }).Where(node => node != null).Take(baseline.ReadyRow >= 0 ? 1 : 2).ToArray();
                if (log != null)
                {
                    var newContent = appendedRows.SelectMany(RowContent).ToArray();
                    log.Write("INFO", "COMMAND_HISTORY_SCAN", AuditLog.Field("baseline_rows", oldCount),
                        AuditLog.Field("current_rows", rows.Count), AuditLog.Field("appended_matches_capped", appended.Length),
                        AuditLog.Field("whole_row_required", true),
                        AuditLog.Field("first_request_only", baseline.ReadyRow >= 0),
                        AuditLog.Field("new_texts", newContent.Length),
                        AuditLog.Field("new_name_lengths", string.Join(",", newContent.Take(8).Select(n => n.Identity.NameLength))),
                        AuditLog.Field("new_name_formats", string.Join(",", newContent.Take(8).Select(n => n.CommandNameFormat ?? "UNAVAILABLE"))),
                        AuditLog.Field("candidate_visible", appended.Length == 1 ? (object)appended[0].Visible : "NOT_UNIQUE"),
                        AuditLog.Field("candidate_enabled", appended.Length == 1 ? (object)appended[0].Enabled : "NOT_UNIQUE"),
                        AuditLog.Field("observation_mode", requireVisible ? "FRESH_ADMISSION" : "ACCEPTED_REOBSERVATION"),
                        AuditLog.Field("details_truncated", newContent.Length > 8));
                }
                Need(appended.Length <= 1, "RECEIVE_COMMAND_NOT_UNIQUE");
                return appended.Length == 1 && appended[0].Enabled && (!requireVisible || appended[0].Visible) ? appended[0] : null;
            }
            var matches = selected.Texts.Where(n => Usable(n) && n.Identity.NameHash == baseline.MarkerHash).Take(2).ToArray();
            Need(matches.Length <= 1, "RECEIVE_CANDIDATE_NOT_UNIQUE");
            return matches.SingleOrDefault();
        }

        internal static ProbeNode ReobserveCandidate(ObservationProof accepted, ProbeSnapshot current, AuditLog log = null)
        {
            Need(accepted != null && accepted.Baseline != null && accepted.Baseline.PlainCommands &&
                accepted.Final != null && accepted.Candidate != null, "RECEIVE_ACCEPTED_PROOF_REQUIRED");
            var candidate = EvaluateCore(accepted.Baseline, current, log, false);
            Need(SameCandidate(accepted.Final, accepted.Candidate, current, candidate),
                "RECEIVE_ACCEPTED_CANDIDATE_CHANGED");
            return candidate;
        }

        internal static void ValidatePlainHistoryPrefix(ProbeSnapshot previous, ProbeSnapshot current, AuditLog log = null,
            Baseline baseline = null, bool acceptedRequest = false)
        {
            var before = SelectHistory(previous);
            var after = SelectHistory(current);
            if (baseline != null && baseline.ReadyRow >= 0)
            {
                if (!acceptedRequest)
                    Need(IsReadyRow(before, baseline.Marker, baseline.ReadyRow) &&
                        IsReadyRow(after, baseline.Marker, baseline.ReadyRow), "RECEIVE_READY_BOUNDARY_CHANGED");
                // After admission, Ready's display body is historical. Its row identity/order still stays exact,
                // and ReobserveCandidate binds every current whole-message result to the accepted command.
                RequireHistoryPrefix(before, after, log, baseline.ReadyRow + (acceptedRequest ? 1 : 0),
                    FirstCommandRow(HistoryRows(before), baseline.ReadyRow + 1));
            }
            else RequireHistoryPrefix(before, after, log);
        }

        private static ProbeNode RowRoot(HistorySelection selection, ProbeNode text)
        {
            var node = text;
            while (node.Parent != selection.History.Node) node = selection.ByNode[node.Parent];
            return node;
        }

        private static List<ProbeNode[]> HistoryRows(HistorySelection selection)
        {
            return selection.Texts.GroupBy(text => RowRoot(selection, text).Node).Select(group => group.ToArray()).ToList();
        }

        private static ProbeNode[] RowContent(ProbeNode[] row)
        {
            // ponytail: only secondary clock-shaped siblings are volatile; primary text/date/counters stay strict.
            // A clock-like first message is still content, and adding a command to an old row remains forbidden.
            Need(row.Skip(1).Count(node => node.Parent == row[0].Parent && node.NameShape == "TIME_LIKE") <= 1,
                "RECEIVE_HISTORY_CLOCK_AMBIGUOUS");
            return row.Where((node, index) => index == 0 || node.Parent != row[0].Parent || node.NameShape != "TIME_LIKE").ToArray();
        }

        private static void RequireHistoryPrefix(HistorySelection previous, HistorySelection current, AuditLog log = null,
            int? protectedStart = null, int protectedEnd = int.MaxValue)
        {
            var before = HistoryRows(previous);
            var after = HistoryRows(current);
            var protectedTailStart = protectedStart ?? Math.Max(0, before.Count - 3);
            var protectedCount = Math.Max(0, Math.Min(before.Count - 1, protectedEnd) - protectedTailStart + 1);
            var rowIndex = -1;
            var textIndex = -1;
            var ignoredBefore = 0;
            var ignoredAfter = 0;
            var clockCountsComplete = false;
            var comparison = "COUNTS";
            ProbeNode oldEvidence = null, newEvidence = null;
            bool? pathEqual = null, nameEqual = null, identityEqual = null, nativeEqual = null;
            void Evidence(ProbeNode oldNode, ProbeNode newNode)
            {
                oldEvidence = oldNode; newEvidence = newNode;
                pathEqual = nameEqual = identityEqual = nativeEqual = null;
                if (oldNode == null || newNode == null) return;
                pathEqual = PathKey(previous, oldNode) == PathKey(current, newNode);
                nameEqual = oldNode.Identity.NameLength == newNode.Identity.NameLength &&
                    oldNode.Identity.NameHash == newNode.Identity.NameHash;
                identityEqual = oldNode.Identity.Matches(newNode.Identity, false);
                nativeEqual = oldNode.NativeHwnd == newNode.NativeHwnd;
            }
            ProbeNode[] Content(ProbeNode[] row, bool oldSide)
            {
                comparison = oldSide ? "BEFORE_CLOCKS" : "AFTER_CLOCKS";
                try { return RowContent(row); }
                catch (MonitorException)
                {
                    var clocks = 0;
                    for (textIndex = 1; textIndex < row.Length; textIndex++)
                        if (row[textIndex].Parent == row[0].Parent && row[textIndex].NameShape == "TIME_LIKE" && ++clocks == 2) break;
                    Evidence(rowIndex < before.Count && textIndex < before[rowIndex].Length ? before[rowIndex][textIndex] : null,
                        rowIndex < after.Count && textIndex < after[rowIndex].Length ? after[rowIndex][textIndex] : null);
                    throw;
                }
            }
            try
            {
                for (rowIndex = protectedTailStart; rowIndex < before.Count && rowIndex <= protectedEnd; rowIndex++)
                    ignoredBefore += before[rowIndex].Length - Content(before[rowIndex], true).Length;
                for (rowIndex = protectedTailStart; rowIndex < after.Count && rowIndex <= protectedEnd; rowIndex++)
                    ignoredAfter += after[rowIndex].Length - Content(after[rowIndex], false).Length;
                clockCountsComplete = true;
                rowIndex = -1; textIndex = -1; comparison = "COUNTS";
                // Complete covers the exposed tree, not all server history or sender/body authentication.
                Need(after.Count >= before.Count, "RECEIVE_HISTORY_PRUNED");
                Need(current.Texts.All(n => !string.IsNullOrEmpty(n.Identity.NameHash)), "RECEIVE_NAME_METADATA_UNAVAILABLE");
                for (rowIndex = 0; rowIndex < before.Count; rowIndex++)
                {
                    var oldRoot = RowRoot(previous, before[rowIndex][0]);
                    var newRoot = RowRoot(current, after[rowIndex][0]);
                    comparison = "ROW"; textIndex = -1; Evidence(oldRoot, newRoot);
                    Need(oldRoot.NativeHwnd == newRoot.NativeHwnd && oldRoot.Identity.Matches(newRoot.Identity, false) &&
                        PathKey(previous, oldRoot) == PathKey(current, newRoot), "RECEIVE_HISTORY_ROW_CHANGED");
                    // ponytail: older bodies are display state, not requests. Keep every row identity/order,
                    // the last three complete bodies, and exact new-candidate checks; never rebase or replay old rows.
                    if (rowIndex < protectedTailStart || rowIndex > protectedEnd) continue;
                    var oldContent = RowContent(before[rowIndex]);
                    var newContent = RowContent(after[rowIndex]);
                    comparison = "CONTENT";
                    for (textIndex = 0; textIndex < Math.Max(oldContent.Length, newContent.Length); textIndex++)
                    {
                        var oldText = textIndex < oldContent.Length ? oldContent[textIndex] : null;
                        var newText = textIndex < newContent.Length ? newContent[textIndex] : null;
                        Evidence(oldText, newText);
                        Need(oldText != null && newText != null && oldText.NativeHwnd == newText.NativeHwnd && oldText.Identity.Equals(newText.Identity) &&
                            PathKey(previous, oldText) == PathKey(current, newText), "RECEIVE_HISTORY_CONTENT_CHANGED");
                    }
                }
                if (log != null && (previous.Texts.Count != current.Texts.Count || ignoredBefore != ignoredAfter))
                    log.Write("INFO", "RECEIVE_HISTORY_COMPARED", AuditLog.Field("before_rows", before.Count),
                        AuditLog.Field("after_rows", after.Count), AuditLog.Field("before_texts", previous.Texts.Count),
                        AuditLog.Field("after_texts", current.Texts.Count), AuditLog.Field("before_clock_siblings", ignoredBefore),
                        AuditLog.Field("after_clock_siblings", ignoredAfter), AuditLog.Field("row_prefix_preserved", true),
                        AuditLog.Field("clock_count_scope", "PROTECTED_TAIL_AND_APPENDED"),
                        AuditLog.Field("protected_tail_rows", protectedCount),
                        AuditLog.Field("older_content_excluded", Math.Min(before.Count, protectedTailStart)));
            }
            catch (MonitorException ex)
            {
                log?.Write("INFO", "RECEIVE_HISTORY_REJECTED", AuditLog.Field("reason", ex.ReasonCode),
                    AuditLog.Field("comparison", comparison), AuditLog.Field("row_index", rowIndex), AuditLog.Field("text_index", textIndex),
                    AuditLog.Field("before_shape", oldEvidence == null ? "MISSING" : oldEvidence.NameShape ?? "UNAVAILABLE"),
                    AuditLog.Field("after_shape", newEvidence == null ? "MISSING" : newEvidence.NameShape ?? "UNAVAILABLE"),
                    AuditLog.Field("path_equal", pathEqual?.ToString() ?? "NOT_COMPARED"),
                    AuditLog.Field("name_equal", nameEqual?.ToString() ?? "NOT_COMPARED"),
                    AuditLog.Field("identity_equal", identityEqual?.ToString() ?? "NOT_COMPARED"),
                    AuditLog.Field("native_equal", nativeEqual?.ToString() ?? "NOT_COMPARED"),
                    AuditLog.Field("before_rows", before.Count), AuditLog.Field("after_rows", after.Count),
                    AuditLog.Field("protected_tail_rows", protectedCount),
                    AuditLog.Field("before_texts", previous.Texts.Count), AuditLog.Field("after_texts", current.Texts.Count),
                    AuditLog.Field("clock_count_scope", "PROTECTED_TAIL_AND_APPENDED"),
                    AuditLog.Field("before_clock_siblings", ignoredBefore), AuditLog.Field("after_clock_siblings", ignoredAfter),
                    AuditLog.Field("clock_counts_complete", clockCountsComplete));
                throw;
            }
        }

        internal static HistorySelection ValidateContinuity(Baseline baseline, ProbeSnapshot current)
        {
            Need(baseline != null, "RECEIVE_BASELINE_REQUIRED");
            var selected = SelectHistory(current);
            Need(baseline.Snapshot.Process.Equals(current.Process), "RECEIVE_PROCESS_CHANGED");
            Need(baseline.Snapshot.RootNameFingerprint == current.RootNameFingerprint, "RECEIVE_ROOT_CHANGED");
            var before = new[] { baseline.Selection.Root, baseline.Selection.Document, baseline.Selection.Composer,
                baseline.Selection.Input, baseline.Selection.History };
            var after = new[] { selected.Root, selected.Document, selected.Composer, selected.Input, selected.History };
            for (var i = 0; i < before.Length; i++)
            {
                // Document/history may aggregate changing content as Name. This is structural continuity, not authentication.
                Need(before[i].Identity.Matches(after[i].Identity, i != 1 && i != 4) && before[i].NativeHwnd == after[i].NativeHwnd,
                    "RECEIVE_ANCHOR_CHANGED");
                Need(PathKey(baseline.Selection, before[i]) == PathKey(selected, after[i]), "RECEIVE_ANCHOR_PATH_CHANGED");
            }
            return selected;
        }

        internal static bool SameCandidate(ProbeSnapshot previous, ProbeNode before, ProbeSnapshot current, ProbeNode after)
        {
            return before != null && after != null && before.NativeHwnd == after.NativeHwnd && before.Identity.Equals(after.Identity) &&
                PathKey(SelectHistory(previous), before) == PathKey(SelectHistory(current), after);
        }

        private static string PathKey(HistorySelection selection, ProbeNode node)
        {
            var parts = new List<string>();
            while (true)
            {
                parts.Add(node.Identity.RuntimeId + ":" + node.NativeHwnd);
                if (node.Parent == 0) break;
                node = selection.ByNode[node.Parent];
            }
            return string.Join("/", parts);
        }

        public static string Run(IntPtr window, AuditLog log, string marker, Func<bool> stop, Action<string> progress)
        {
            return Observe(window, log, marker, stop, progress, new object(), true).Message;
        }

        internal static ObservationResult Observe(IntPtr window, AuditLog log, string marker, Func<bool> stop,
            Action<string> progress, object owner, bool collectMetadata, Action<string, ProbeSnapshot> inspectSnapshot = null,
            Baseline suppliedBaseline = null, bool continuousWait = false, bool plainCommands = false,
            ObservationProof acceptedProof = null, bool singleRefresh = false, OperationalTarget target = null)
        {
            var overall = Stopwatch.StartNew();
            var phaseClock = Stopwatch.StartNew();
            Stopwatch receiving = null;
            ProcessIdentity process = null;
            NativeMethods.WindowRectangle? bounds = null;
            string guardFailure = null;
            var phase = "BASELINE";
            var polls = 0;
            try
            {
                Need(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "PROBE_REQUIRES_MTA");
                Need(log != null && stop != null && progress != null && owner != null &&
                    Protocol.IsDiagnosticMarker("MESSAGE", marker), "RECEIVE_REQUEST_INVALID");
                if (acceptedProof != null)
                    Need(plainCommands && !continuousWait && suppliedBaseline != null &&
                        ReferenceEquals(acceptedProof.Owner, owner) && acceptedProof.Window == window &&
                        ReferenceEquals(acceptedProof.Baseline, suppliedBaseline), "RECEIVE_ACCEPTED_PROOF_MISMATCH");
                Need(!singleRefresh || (acceptedProof != null && !collectMetadata), "RECEIVE_SINGLE_OBSERVATION_INVALID");
                Need(target == null || (plainCommands && continuousWait && !collectMetadata && acceptedProof == null),
                    "RECEIVE_BACKGROUND_SCOPE_INVALID");
                bool Stopped()
                {
                    if (guardFailure != null) return true;
                    if (stop()) guardFailure = "RECEIVE_CANCELLED";
                    else if (phaseClock.Elapsed >= TimeSpan.FromSeconds(15)) guardFailure = "RECEIVE_PHASE_TIME_LIMIT";
                    else if (!continuousWait && receiving != null && receiving.IsRunning && receiving.Elapsed >= TimeSpan.FromSeconds(60))
                        guardFailure = "RECEIVE_WAIT_TIME_LIMIT";
                    else if (window == IntPtr.Zero || !NativeMethods.IsWindow(window)) guardFailure = "RECEIVE_NO_TARGET";
                    else if (target == null && NativeMethods.GetForegroundWindow() != window) guardFailure = "RECEIVE_FOREGROUND_CHANGED";
                    else if (process != null)
                    {
                        uint pid;
                        NativeMethods.WindowRectangle current;
                        if (NativeMethods.GetWindowThreadProcessId(window, out pid) == 0 || pid != process.ProcessId) guardFailure = "RECEIVE_PROCESS_CHANGED";
                        else if (target == null && bounds.HasValue && (!NativeMethods.GetWindowRect(window, out current) || !current.Equals(bounds.Value)))
                            guardFailure = "RECEIVE_WINDOW_MOVED";
                    }
                    if (guardFailure == null) target?.Check();
                    return guardFailure != null;
                }
                void Alive() { Need(!Stopped(), guardFailure); }
                T Read<T>(Func<T> read) { Alive(); var value = read(); Alive(); return value; }
                void SetPhase(string value)
                {
                    phase = value;
                    phaseClock.Restart();
                    Alive();
                    progress(value);
                    log.Write("INFO", "RECEIVE_PHASE", AuditLog.Field("phase", value), AuditLog.Field("elapsed_ms", overall.ElapsedMilliseconds));
                    Alive();
                }
                ProbeSnapshot Capture()
                {
                    var snapshot = ReadOnlyProbe.CaptureSnapshot(window, log, "SEND_METADATA", null, Stopped,
                        retainSelectedInput: true, automaticSendSelection: true, retainReceiveElements: collectMetadata,
                        compactLog: continuousWait || (suppliedBaseline != null && suppliedBaseline.ReadyRow >= 0));
                    Alive();
                    Need(process.Equals(snapshot.Process), "RECEIVE_PROCESS_CHANGED");
                    return snapshot;
                }
                void CheckRoot(ProbeSnapshot snapshot)
                {
                    Need(process.Equals(Read(() => ProcessIdentity.Capture(window))), "RECEIVE_PROCESS_CHANGED");
                    var root = Read(() => AutomationElement.FromHandle(window));
                    Need(root != null, "RECEIVE_ROOT_CHANGED");
                    var pid = Read(() => root.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty, true));
                    var password = Read(() => root.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true));
                    Need(pid is int && (int)pid == process.ProcessId && password is bool && !(bool)password, "RECEIVE_ROOT_CHANGED");
                    Need(new IntPtr(Read(() => root.Current.NativeWindowHandle)) == window &&
                        UiaPointProbe.Format(Read(root.GetRuntimeId)) == snapshot.Nodes.Single(n => n.Node == 1).Identity.RuntimeId, "RECEIVE_ROOT_CHANGED");
                    Need(log.Fingerprint(Read(() => root.Current.Name)) == snapshot.RootNameFingerprint, "RECEIVE_ROOT_CHANGED");
                }
                void CheckMetadataPath(AutomationElement element, ProbeNode expected, HistorySelection selection)
                {
                    // Security-only batches keep the bounded live ancestry check cheap; no content is cached here.
                    var request = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Condition.TrueCondition,
                        AutomationElementMode = AutomationElementMode.Full };
                    foreach (var property in new[] { AutomationElement.ProcessIdProperty, AutomationElement.IsPasswordProperty,
                        AutomationElement.NativeWindowHandleProperty, AutomationElement.RuntimeIdProperty }) request.Add(property);
                    var cursor = Read(() => element.GetUpdatedCache(request));
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    var documentFound = false;
                    for (var depth = 0; depth <= 32; depth++)
                    {
                        Need(cursor != null, "RECEIVE_METADATA_ROOT_MISSING");
                        var pid = cursor.GetCachedPropertyValue(AutomationElement.ProcessIdProperty, true);
                        var password = cursor.GetCachedPropertyValue(AutomationElement.IsPasswordProperty, true);
                        Need(pid is int && (int)pid == process.ProcessId && password is bool && !(bool)password, "RECEIVE_METADATA_PROTECTED_OR_FOREIGN");
                        var hwnd = cursor.GetCachedPropertyValue(AutomationElement.NativeWindowHandleProperty, false);
                        Need(hwnd is int, "RECEIVE_METADATA_NATIVE_UNAVAILABLE");
                        var native = new IntPtr((int)hwnd);
                        var runtime = UiaPointProbe.Format(cursor.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty, true) as int[]);
                        Need(visited.Add(runtime), "RECEIVE_METADATA_PATH_CYCLE");
                        if (depth == 0) Need(runtime == expected.Identity.RuntimeId && (int)hwnd == expected.NativeHwnd, "RECEIVE_METADATA_ELEMENT_CHANGED");
                        if (native != IntPtr.Zero)
                        {
                            uint nativePid;
                            Need(NativeMethods.IsWindow(native) && (native == window || NativeMethods.IsChild(window, native)) &&
                                NativeMethods.GetWindowThreadProcessId(native, out nativePid) != 0 && nativePid == process.ProcessId,
                                "RECEIVE_METADATA_NATIVE_OUTSIDE_TARGET");
                        }
                        if ((int)hwnd == selection.Document.NativeHwnd && runtime == selection.Document.Identity.RuntimeId) documentFound = true;
                        if (native == window)
                        {
                            Need(documentFound && runtime == selection.Root.Identity.RuntimeId, "RECEIVE_METADATA_ROOT_CHANGED");
                            Alive();
                            return;
                        }
                        Need(depth < 32, "RECEIVE_METADATA_PATH_LIMIT");
                        cursor = Read(() => TreeWalker.RawViewWalker.GetParent(cursor, request));
                    }
                }
                void Metadata(ProbeSnapshot snapshot, ProbeNode candidate, string metadataPhase)
                {
                    SetPhase(metadataPhase == "BASELINE" ? "BASELINE_METADATA" : "AFTER_METADATA");
                    CheckRoot(snapshot);
                    var selection = SelectHistory(snapshot);
                    var availableNodes = MetadataNodes(snapshot, candidate);
                    var nodes = availableNodes.Take(64).ToArray();
                    Need(snapshot.ReceiveElements != null, "RECEIVE_LIVE_METADATA_UNAVAILABLE");
                    var completed = 0;
                    var partial = false;
                    try
                    {
                        foreach (var node in nodes)
                        {
                            Alive();
                            AutomationElement element;
                            Need(snapshot.ReceiveElements.TryGetValue(node.Node, out element), "RECEIVE_LIVE_METADATA_UNAVAILABLE");
                            CheckMetadataPath(element, node, selection);
                            ReceiveMetadata.Capture(element, node, log, Alive, metadataPhase);
                            Alive();
                            completed++;
                        }
                    }
                    catch (MonitorException ex) when (ex.ReasonCode == "RECEIVE_PHASE_TIME_LIMIT")
                    {
                        // Optional metadata stops at its cap. Cancellation/scope/security errors never take this branch.
                        partial = true;
                        guardFailure = null;
                        phaseClock.Restart();
                        Alive();
                    }
                    CheckRoot(snapshot);
                    log.Write("INFO", "RECEIVE_METADATA_COMPLETE", AuditLog.Field("phase", metadataPhase),
                        AuditLog.Field("elements", completed), AuditLog.Field("requested_elements", availableNodes.Count),
                        AuditLog.Field("truncated", availableNodes.Count > 64), AuditLog.Field("partial", partial),
                        AuditLog.Field("limit", 64), AuditLog.Field("plain_body_verified", false));
                }
                target?.PrepareRead();
                phaseClock.Restart();
                Alive();
                process = ProcessIdentity.Capture(window);
                Alive();
                Need(string.Equals(process.ProcessName, "KI-Messenger", StringComparison.OrdinalIgnoreCase), "PROBE_NOT_KI_MESSENGER");
                NativeMethods.WindowRectangle initial;
                Need(NativeMethods.GetWindowRect(window, out initial), "RECEIVE_WINDOW_BOUNDS_UNAVAILABLE");
                if (singleRefresh) Need(initial.Equals(acceptedProof.Bounds), "RECEIVE_WINDOW_MOVED");
                bounds = initial;
                SetPhase("BASELINE");
                var firstCaptureAge = singleRefresh ? Stopwatch.StartNew() : null;
                var firstSnapshot = Capture();
                var baseline = suppliedBaseline == null ? CreateBaseline(firstSnapshot, marker, plainCommands) :
                    AcceptSuppliedBaseline(suppliedBaseline, firstSnapshot, marker, window, plainCommands);
                if (continuousWait && plainCommands)
                {
                    Baseline ready;
                    while ((ready = BindReadyBoundary(baseline, firstSnapshot, log)) == null)
                    {
                        Alive();
                        ReleaseLive(firstSnapshot);
                        Thread.Sleep(100);
                        firstSnapshot = Capture();
                    }
                    baseline = ready;
                }
                ProbeNode Candidate(ProbeSnapshot snapshot)
                {
                    return acceptedProof == null ? Evaluate(baseline, snapshot, log) :
                        ReobserveCandidate(acceptedProof, snapshot, log);
                }
                var firstCandidate = suppliedBaseline == null ? null : Candidate(firstSnapshot);
                inspectSnapshot?.Invoke("BASELINE", firstSnapshot);
                Alive();
                if (collectMetadata) Metadata(firstSnapshot, null, "BASELINE");
                else CheckRoot(firstSnapshot);
                ReleaseLive(firstSnapshot);
                if (singleRefresh)
                {
                    Need(firstCandidate != null, "RECEIVE_ACCEPTED_CANDIDATE_CHANGED");
                    ValidatePlainHistoryPrefix(acceptedProof.Final, firstSnapshot, log, baseline, acceptedRequest: true);
                    Alive();
                    Result(log, "CANDIDATE_REOBSERVED_ONCE", "NONE", polls);
                    return new ObservationResult("CANDIDATE_REOBSERVED_ONCE", "NONE",
                        "One fresh accepted-request observation is ready; the sender must independently observe it again before any action.",
                        single: new SingleObservation(acceptedProof, owner, window, initial, firstSnapshot,
                            firstCandidate, firstCaptureAge));
                }
                SetPhase("READY_TO_RECEIVE");
                receiving = Stopwatch.StartNew();
                ProbeSnapshot previous = suppliedBaseline == null && !plainCommands ? null : firstSnapshot;
                ProbeNode previousCandidate = firstCandidate;
                while (true)
                {
                    phaseClock.Restart(); // The inter-snapshot wait is not charged to the preceding snapshot's 15-second cap.
                    for (var i = 0; i < (baseline.ReadyRow >= 0 ? 2 : 10); i++) { Alive(); Thread.Sleep(100); }
                    target?.PrepareRead();
                    SetPhase("POLLING");
                    var current = Capture();
                    polls++;
                    if (plainCommands && previous != null)
                        ValidatePlainHistoryPrefix(previous, current, log, baseline, acceptedRequest: acceptedProof != null);
                    var candidate = Candidate(current);
                    inspectSnapshot?.Invoke("POLL", current);
                    Alive();
                    log.Write("INFO", "RECEIVE_OBSERVATION", AuditLog.Field("poll", polls), AuditLog.Field("nodes", current.Nodes.Count),
                        AuditLog.Field("candidate_node", candidate == null ? 0 : candidate.Node),
                        AuditLog.Field("classification", candidate == null ? "NO_EXACT_HISTORY_CANDIDATE" : "HISTORY_CANDIDATE"),
                        AuditLog.Field("plain_body_verified", false), AuditLog.Field("automatic_send_allowed", false));
                    Alive();
                    if (SameCandidate(previous, previousCandidate, current, candidate))
                    {
                        receiving.Stop();
                        if (collectMetadata) Metadata(current, candidate, "AFTER");
                        else CheckRoot(current);
                        ReleaseLive(current);
                        Result(log, "CANDIDATE_OBSERVED", "NONE", polls);
                        Alive();
                        return new ObservationResult("CANDIDATE_OBSERVED", "NONE",
                            "CANDIDATE_OBSERVED - one exact history Text candidate was observed twice. Plain-message body and delivery are NOT certified. No action was executed.",
                            new ObservationProof(owner, window, initial, baseline, previous, previousCandidate, current, candidate));
                    }
                    ReleaseLive(current);
                    previous = current;
                    previousCandidate = candidate;
                    if (candidate != null) progress("WAITING_FOR_REPEAT");
                    else if (plainCommands && HistoryRows(SelectHistory(current)).Skip(baseline.ReadyRow >= 0 ?
                        baseline.ReadyRow + 1 : HistoryRows(baseline.Selection).Count)
                        .SelectMany(RowContent).Any(n => Usable(n) && n.Identity.NameLength <= 64 && n.PlainCommand == null))
                        progress("COMMAND_NOT_MATCHED");
                    else if (target != null) progress("READY_TO_RECEIVE");
                }
            }
            catch (Exception ex)
            {
                var reason = (ex as MonitorException)?.ReasonCode ?? "RECEIVE_READ_FAILED";
                var status = reason == "RECEIVE_WAIT_TIME_LIMIT" ? "NO_NEW_CANDIDATE" : "REJECTED";
                try
                {
                    if (log != null)
                    {
                        log.WriteException("RECEIVE_FAILED", ex, AuditLog.Field("phase", phase), AuditLog.Field("elapsed_ms", overall.ElapsedMilliseconds));
                        Result(log, status, reason, polls);
                    }
                }
                catch { }
                return new ObservationResult(status, reason,
                    status + " - " + reason + ". No input, click or send was executed. Collect this one log.");
            }
        }

        private static Baseline AcceptSuppliedBaseline(Baseline baseline, ProbeSnapshot current, string marker, IntPtr window,
            bool plainCommands = false)
        {
            Need(baseline != null && baseline.Snapshot != null && baseline.Selection != null &&
                baseline.Marker == marker && baseline.MarkerHash == TokenStore.Hash(marker) &&
                baseline.PlainCommands == plainCommands, "RECEIVE_BASELINE_MARKER_MISMATCH");
            Need(baseline.Snapshot.Process != null && baseline.Snapshot.Process.WindowHandle == window.ToInt64(),
                "RECEIVE_BASELINE_WINDOW_MISMATCH");
            ValidateContinuity(baseline, current);
            return baseline;
        }

        private static void ReleaseLive(ProbeSnapshot snapshot)
        {
            snapshot.MatchedElement = null;
            snapshot.MatchedSend = null;
            snapshot.ReceiveElements = null;
        }

        private static void Result(AuditLog log, string status, string reason, int polls)
        {
            log.Write("INFO", "RECEIVE_RESULT", AuditLog.Field("status", status), AuditLog.Field("reason", reason),
                AuditLog.Field("polls", polls), AuditLog.Field("plain_body_verified", false), AuditLog.Field("conversation_identity_verified", false),
                AuditLog.Field("automatic_send_allowed", false), AuditLog.Field("setvalue_calls", 0), AuditLog.Field("click_calls", 0),
                AuditLog.Field("invoke_calls", 0), AuditLog.Field("default_action_calls", 0));
        }

        internal static void RunSelfTest()
        {
            const string marker = "M234567";
            ProbeNode At(ProbeSnapshot snapshot, int number) { return snapshot.Nodes.Single(n => n.Node == number); }
            void Name(ProbeNode node, string text, string runtime = null)
            {
                SetTestName(node, text, runtime);
            }
            ProbeSnapshot Fixture()
            {
                return CreateTestSnapshot();
            }
            void Reject(Action operation)
            {
                try { operation(); }
                catch (MonitorException) { return; }
                throw new InvalidOperationException("Receive self-test accepted unsafe evidence.");
            }
            var baseline = CreateBaseline(Fixture(), marker);
            Need(baseline.Selection.History.Node == 50 && baseline.Selection.Texts.Count == 2 &&
                !MetadataNodes(baseline.Snapshot, null).Any(n => n.Node == 57 || n.Node == 5 || n.Document != 2), "RECEIVE_SELF_TEST_SCOPE");
            foreach (var number in new[] { 52, 57, 5, 41 })
            {
                var old = Fixture(); Name(At(old, number), marker);
                Reject(() => CreateBaseline(old, marker));
            }
            foreach (var number in new[] { 57, 41 })
            {
                var outside = Fixture(); Name(At(outside, number), marker);
                Need(Evaluate(baseline, outside) == null, "RECEIVE_SELF_TEST_OUTSIDE");
            }
            var noContent = Fixture(); Name(At(noContent, 52), "", "new-runtime");
            Need(Evaluate(baseline, noContent) == null, "RECEIVE_SELF_TEST_RUNTIME_NOT_FRESHNESS");
            var first = Fixture(); Name(At(first, 52), marker);
            var firstHit = Evaluate(baseline, first); // Reusing a pre-existing RuntimeId is not identity/freshness proof.
            Need(firstHit != null && firstHit.Node == 52, "RECEIVE_SELF_TEST_EXACT_CONTENT");
            var repeat = Fixture(); Name(At(repeat, 52), marker);
            Need(SameCandidate(first, firstHit, repeat, Evaluate(baseline, repeat)), "RECEIVE_SELF_TEST_REPEAT");
            Name(At(repeat, 52), marker, "replacement-runtime");
            Need(!SameCandidate(first, firstHit, repeat, Evaluate(baseline, repeat)), "RECEIVE_SELF_TEST_RECREATION");
            var renumbered = Fixture(); Name(At(renumbered, 52), marker);
            foreach (var node in renumbered.Nodes)
            {
                if (node.Node > 1) node.Node += 100;
                if (node.Parent > 1) node.Parent += 100;
                if (node.Document > 0) node.Document += 100;
            }
            Need(SameCandidate(first, firstHit, renumbered, Evaluate(baseline, renumbered)), "RECEIVE_SELF_TEST_ORDINALS");
            foreach (var mutate in new Action<ProbeSnapshot>[] {
                s => Name(At(s, 5), marker), s => s.RootNameFingerprint = "changed",
                s => s.Process = (ProcessIdentity)typeof(ReadOnlyPair).GetMethod("TestProcess", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { 2L }),
                s => At(s, 2).NativeHwnd++, s => Name(At(s, 50), "", "new-history"),
                s => s.Complete = false, s => s.LayoutRejection = "LAYOUT_VISIBLE_LIST",
                s => { Name(At(s, 52), marker); Name(At(s, 54), marker); } })
            {
                var invalid = Fixture(); mutate(invalid); Reject(() => Evaluate(baseline, invalid));
            }
            Reject(() => CreateBaseline(Fixture(), "M123456"));
            // A fast NEXT may already be present on the next call's first capture; never rebase over it.
            var boundWindow = new IntPtr(baseline.Snapshot.Process.WindowHandle);
            Need(ReferenceEquals(AcceptSuppliedBaseline(baseline, first, marker, boundWindow), baseline),
                "RECEIVE_SELF_TEST_CARRIED_BASELINE");
            Reject(() => AcceptSuppliedBaseline(baseline, first, "M345678", boundWindow));
            Reject(() => AcceptSuppliedBaseline(baseline, first, marker, new IntPtr(boundWindow.ToInt64() + 1)));
            var wrongRoot = Fixture(); wrongRoot.RootNameFingerprint = "other";
            Reject(() => AcceptSuppliedBaseline(baseline, wrongRoot, marker, boundWindow));
            var wrongHash = new Baseline(baseline.Snapshot, baseline.Selection, marker, "invalid");
            Reject(() => AcceptSuppliedBaseline(wrongHash, first, marker, boundWindow));
            RunPlainCommandSelfTest();
        }

        private static void RunPlainCommandSelfTest()
        {
            const string marker = "M234567";
            void Reject(Action action)
            {
                try { action(); } catch (MonitorException) { return; }
                throw new InvalidOperationException("Unsafe plain-command history was accepted.");
            }
            ProbeSnapshot History()
            {
                var snapshot = CreateTestSnapshot();
                SetTestName(snapshot.Nodes.Single(n => n.Node == 52), "help");
                SetTestName(snapshot.Nodes.Single(n => n.Node == 54), "old history");
                return snapshot;
            }
            var before = History();
            before.Nodes.Single(n => n.Node == 52).Visible = false;
            var baseline = CreateBaseline(before, marker, true);
            Need(Evaluate(baseline, History()) == null, "COMMAND_SELFTEST_OLD_VISIBILITY_NOT_FRESH");
            var recycledOld = History();
            SetTestName(recycledOld.Nodes.Single(n => n.Node == 52), "help", "new-old-runtime");
            Reject(() => Evaluate(baseline, recycledOld)); // Changed old identity stops; it never makes old text a new command.
            var uppercase = History(); AppendTestHistoryText(uppercase, "HELP");
            Need(Evaluate(baseline, uppercase) == null, "COMMAND_SELFTEST_NO_OWN_REPLY");
            var nonce = History(); AppendTestHistoryText(nonce, marker);
            Need(Evaluate(baseline, nonce) == null, "COMMAND_SELFTEST_MODE_ISOLATED");
            foreach (var text in new[] { SupervisedSendTest.ReadyNotice, SupervisedSendTest.PowerSiBusyNotice, SupervisedSendTest.StatusBusyNotice })
            {
                var withNotice = History();
                AppendTestHistoryText(withNotice, text);
                Need(Evaluate(baseline, withNotice) == null, "COMMAND_SELFTEST_NOTICE_NOT_COMMAND");
                var immediate = AppendTestHistoryText(withNotice, "pwrsi");
                Need(Evaluate(baseline, withNotice) == immediate, "COMMAND_SELFTEST_IMMEDIATE_AFTER_READY");
            }
            foreach (var fragments in new[] {
                new[] { "PWRSI REPORT D234567 | PART 001/001", "pwrsi" },
                new[] { "help", "HELP RESPONSE" },
                new[] { "status", "help", "pwrsi" } })
            {
                var splitReply = History();
                var primary = AppendTestHistoryText(splitReply, fragments[0]);
                for (var i = 1; i < fragments.Length; i++) AppendTestHistorySiblingText(splitReply, primary, fragments[i]);
                Need(Evaluate(baseline, splitReply) == null, "COMMAND_SELFTEST_FRAGMENT_NOT_WHOLE_ROW");
            }
            var timestamped = History();
            var timestampedCommand = AppendTestHistoryText(timestamped, "pwrsi");
            AppendTestHistorySiblingText(timestamped, timestampedCommand, "14:26");
            Need(Evaluate(baseline, timestamped) == timestampedCommand,
                "COMMAND_SELFTEST_VALIDATED_CLOCK_IGNORED");

            var first = History(); AppendTestHistoryText(first, "help");
            var second = History(); AppendTestHistoryText(second, "help");
            var candidate = Evaluate(baseline, first);
            Need(candidate != null && SameCandidate(first, candidate, second, Evaluate(baseline, second)),
                "COMMAND_SELFTEST_APPEND_AND_REPEAT");
            var accepted = new ObservationProof(new object(), new IntPtr(first.Process.WindowHandle),
                new NativeMethods.WindowRectangle(), baseline, first, candidate, second, Evaluate(baseline, second));
            ProbeSnapshot Reobservation(bool visible = false, bool enabled = true, string text = "help",
                string runtime = null)
            {
                var snapshot = History();
                var node = AppendTestHistoryText(snapshot, text);
                node.Visible = visible;
                node.Enabled = enabled;
                if (runtime != null) SetTestName(node, text, runtime);
                return snapshot;
            }
            var offscreen = Reobservation();
            Need(Evaluate(baseline, offscreen) == null &&
                ReobserveCandidate(accepted, offscreen) == SelectHistory(offscreen).Texts.Last(),
                "COMMAND_SELFTEST_ACCEPTED_OFFSCREEN_REOBSERVED");
            foreach (var invalid in new Action<ProbeSnapshot>[] {
                s => SelectHistory(s).Texts.Last().Enabled = false,
                s => { var node = SelectHistory(s).Texts.Last(); var row = RowRoot(SelectHistory(s), node); s.Nodes.RemoveAll(n => n.Node == node.Node || n.Node == row.Node); },
                s => SetTestName(SelectHistory(s).Texts.Last(), "help", "recreated-command-runtime"),
                s => SetTestName(SelectHistory(s).Texts.Last(), "help "),
                s => s.RootNameFingerprint = "other chat",
                s => AppendTestHistorySiblingText(s, SelectHistory(s).Texts.Last(), "not a clock") })
            {
                var changed = Reobservation(); invalid(changed); Reject(() => ReobserveCandidate(accepted, changed));
            }
            var replacement = Reobservation(text: "pwrsi");
            Reject(() => ReobserveCandidate(accepted, replacement));
            var future = CreateBaseline(second, "M345678", true);
            Need(Evaluate(future, second) == null, "COMMAND_SELFTEST_CONSUMED_OCCURRENCE");
            var next = History(); AppendTestHistoryText(next, "help"); AppendTestHistoryText(next, "HELP RESPONSE");
            AppendTestHistoryText(next, "help");
            Need(Evaluate(future, next) != null, "COMMAND_SELFTEST_SAME_COMMAND_NEW_OCCURRENCE");

            // An identical last pre-READY message must not suppress a new occurrence of any allowed command.
            foreach (var command in new[] { "help", "help help", "help total status", "help pwrsi", "total status", "pwrsi" })
            foreach (var padding in new[] { "", " ", "\u00a0" })
            {
                ProbeSnapshot Repeated(int appended)
                {
                    var snapshot = History();
                    SetTestName(snapshot.Nodes.Single(n => n.Node == 54), padding + command + padding);
                    for (var i = 0; i < appended; i++) AppendTestHistoryText(snapshot, padding + command + padding);
                    return snapshot;
                }
                var original = CreateBaseline(Repeated(0), marker, true);
                Need(Evaluate(original, Repeated(0)) == null, "COMMAND_SELFTEST_OLD_LAST_IGNORED");
                var observed = Repeated(1); var confirmed = Repeated(1);
                var fresh = Evaluate(original, observed);
                Need(fresh != null && fresh.Node != 54 && SameCandidate(observed, fresh, confirmed, Evaluate(original, confirmed)),
                    "COMMAND_SELFTEST_IDENTICAL_LAST_AND_NEW");
                var consumed = CreateBaseline(confirmed, "M345678", true);
                Need(Evaluate(consumed, Repeated(1)) == null && Evaluate(consumed, Repeated(2)) != null,
                    "COMMAND_SELFTEST_IDENTICAL_NEXT_OCCURRENCE");
                Reject(() => Evaluate(original, Repeated(2))); // Two unconsumed requests remain ambiguous.
            }

            var padded = History(); var paddedNode = AppendTestHistoryText(padded, "pwrsi ");
            var unpadded = History(); AppendTestHistoryText(unpadded, "pwrsi");
            Need(Evaluate(baseline, padded) == paddedNode && paddedNode.CommandNameFormat == "OUTER_SPACES",
                "COMMAND_SELFTEST_OUTER_SPACE");
            Reject(() => ValidatePlainHistoryPrefix(padded, unpadded)); // Same normalized command, different observed body.
            Need(!SameCandidate(padded, paddedNode, unpadded, Evaluate(baseline, unpadded)), "COMMAND_SELFTEST_RAW_IDENTITY_STRICT");
            foreach (var invalid in new[] { "PWRSI", "pwrsi\t", "pwrsi\n", "pwr\nsi", "pwr si", "pwrsi\u200b", "total  status", "total\u00a0status" })
            {
                var unsupported = History(); AppendTestHistoryText(unsupported, invalid);
                Need(Evaluate(baseline, unsupported) == null, "COMMAND_SELFTEST_NO_FUZZY_MATCH");
            }
            var tooLong = History(); AppendTestHistoryText(tooLong, new string(' ', 64) + "pwrsi");
            Need(Evaluate(baseline, tooLong) == null, "COMMAND_SELFTEST_NAME_BOUND");
            ReadOnlyCommands.ObserveName(paddedNode, "pwrsi"); // Inconsistent second Name read cannot produce a canonical command.
            string mismatched;
            Need(!ReadOnlyCommands.TryMatchNode(paddedNode, out mismatched), "COMMAND_SELFTEST_NAME_READ_CHANGED");
            ReadOnlyCommands.ObserveName(paddedNode, " help ");
            Need(!ReadOnlyCommands.TryMatchNode(paddedNode, out mismatched), "COMMAND_SELFTEST_SAME_LENGTH_READ_CHANGED");
            var mixedDuplicate = History(); AppendTestHistoryText(mixedDuplicate, "pwrsi");
            AppendTestHistoryText(mixedDuplicate, "pwrsi ").Visible = false;
            Reject(() => Evaluate(baseline, mixedDuplicate));

            var duplicate = History(); AppendTestHistoryText(duplicate, "help"); AppendTestHistoryText(duplicate, "pwrsi");
            Reject(() => Evaluate(baseline, duplicate));
            SelectHistory(duplicate).Texts.Last().Visible = false;
            Reject(() => Evaluate(baseline, duplicate));
            var replaced = History(); SetTestName(replaced.Nodes.Single(n => n.Node == 54), "changed history");
            AppendTestHistoryText(replaced, "help"); Reject(() => Evaluate(baseline, replaced));
            var pruned = History(); pruned.Nodes.RemoveAll(n => n.Node == 53 || n.Node == 54);
            Reject(() => Evaluate(baseline, pruned));
            var reordered = History();
            SetTestName(reordered.Nodes.Single(n => n.Node == 52), "old history");
            SetTestName(reordered.Nodes.Single(n => n.Node == 54), "help");
            Reject(() => Evaluate(baseline, reordered));
            var idle = History(); AppendTestHistoryText(idle, "unrecognized");
            Need(Evaluate(baseline, idle) == null, "COMMAND_SELFTEST_UNKNOWN_IGNORED");
            Reject(() => ValidatePlainHistoryPrefix(idle, first));
            var malformed = History(); malformed.Complete = false;
            Reject(() => Evaluate(baseline, malformed));
            var window = new IntPtr(before.Process.WindowHandle);
            Need(ReferenceEquals(AcceptSuppliedBaseline(baseline, first, marker, window, true), baseline),
                "COMMAND_SELFTEST_CARRIED_BASELINE");
            Reject(() => AcceptSuppliedBaseline(baseline, first, marker, window));
            RunClockSiblingSelfTest();
            RunLongHistorySelfTest();
            RunReadyBoundarySelfTest();
        }

        private static void RunReadyBoundarySelfTest()
        {
            const string marker = "M234567";
            var readyText = SupervisedSendTest.ReadyText("D234567");
            void Reject(Action action)
            {
                try { action(); } catch (MonitorException) { return; }
                throw new InvalidOperationException("Unsafe Ready boundary accepted.");
            }
            ProbeSnapshot History()
            {
                var snapshot = CreateTestSnapshot();
                while (HistoryRows(SelectHistory(snapshot)).Count < 22) AppendTestHistoryText(snapshot, "old message");
                return snapshot;
            }
            ProbeNode Body(ProbeSnapshot snapshot, int row) { return HistoryRows(SelectHistory(snapshot))[row][0]; }
            var before = CreateBaseline(History(), marker, true);
            Need(BindReadyBoundary(before, History()) == null, "READY_BOUNDARY_DELAYED");
            var immediate = History();
            SetTestName(Body(immediate, 19), "old display refreshed"); // The real field shape: 22 -> 25 rows.
            AppendTestHistoryText(immediate, "pwrsi"); // Arrived before Ready: never a request.
            var ready = AppendTestHistoryText(immediate, readyText);
            var command = AppendTestHistoryText(immediate, "pwrsi");
            AppendTestHistorySiblingText(immediate, command, "오후 3:44");
            Reject(() => Evaluate(before, immediate)); // No general old-body exception.
            var bound = BindReadyBoundary(before, immediate);
            Need(bound != null && bound.ReadyRow == 23 && Evaluate(bound, immediate) == command,
                "READY_BOUNDARY_IMMEDIATE_COMMAND");
            var next = History();
            AppendTestHistoryText(next, "pwrsi");
            AppendTestHistoryText(next, readyText);
            var repeated = AppendTestHistoryText(next, "pwrsi");
            AppendTestHistorySiblingText(next, repeated, "오후 3:44");
            var ignored = AppendTestHistoryText(next, "help");
            AppendTestHistorySiblingText(next, ignored, "15:44");
            AppendTestHistorySiblingText(next, ignored, "15:45"); // Ignored traffic cannot fail clock parsing.
            ValidatePlainHistoryPrefix(immediate, next, baseline: bound);
            Need(Evaluate(bound, next) == repeated && SameCandidate(immediate, command, next, repeated),
                "READY_BOUNDARY_BUSY_TRAFFIC_IGNORED");
            SetTestName(ignored, "different ignored message");
            AppendTestHistoryText(next, readyText); // A later quotation cannot replace the bound Ready row.
            Need(Evaluate(bound, next) == repeated, "READY_BOUNDARY_IGNORED_BODY_REFRESH");
            SetTestName(repeated, "help");
            Reject(() => Evaluate(bound, next));
            SetTestName(repeated, "pwrsi");
            var beforeNextReady = new ProbeSnapshot { Complete = next.Complete, Process = next.Process,
                RootNameFingerprint = next.RootNameFingerprint, LayoutRejection = next.LayoutRejection,
                Nodes = new List<ProbeNode>(next.Nodes) };
            var readyAgain = CreateBaseline(beforeNextReady, "M345678", true);
            AppendTestHistoryText(next, SupervisedSendTest.ReadyText("D345678"));
            var newBound = BindReadyBoundary(readyAgain, next);
            Need(newBound != null && Evaluate(newBound, next) == null, "READY_BOUNDARY_NO_QUEUE");
            Need(Evaluate(newBound, next) == null, "READY_BOUNDARY_OLD_COMMANDS_IGNORED");
            var later = new ProbeSnapshot { Complete = next.Complete, Process = next.Process,
                RootNameFingerprint = next.RootNameFingerprint, LayoutRejection = next.LayoutRejection,
                Nodes = new List<ProbeNode>(next.Nodes) };
            var newCommand = AppendTestHistoryText(later, "total status");
            Need(Evaluate(newBound, later) == newCommand, "READY_BOUNDARY_NEXT_REQUEST");

            var paddedReady = History(); var padded = AppendTestHistoryText(paddedReady, "\u00a0" + readyText + " ");
            var paddedBound = BindReadyBoundary(before, paddedReady);
            Need(paddedBound != null, "READY_BOUNDARY_OUTER_SPACES");
            padded.ReadyNoticeSourceHash = "unmatched name read";
            Need(BindReadyBoundary(before, paddedReady) == null, "READY_BOUNDARY_NAME_READ_MISMATCH");

            foreach (var text in new[] { SupervisedSendTest.ReadyNotice, SupervisedSendTest.ReadyText("D345678") })
            {
                var wrong = History(); AppendTestHistoryText(wrong, text); AppendTestHistoryText(wrong, "pwrsi");
                Need(BindReadyBoundary(before, wrong) == null, "READY_BOUNDARY_WRONG_TOKEN");
            }
            var split = History(); var fragment = AppendTestHistoryText(split, readyText);
            AppendTestHistorySiblingText(split, fragment, "other text");
            Need(BindReadyBoundary(before, split) == null, "READY_BOUNDARY_WHOLE_ROW");
            var duplicate = History(); AppendTestHistoryText(duplicate, readyText); AppendTestHistoryText(duplicate, readyText);
            Reject(() => BindReadyBoundary(before, duplicate));
            Reject(() => BindReadyBoundary(CreateBaseline(immediate, marker, true), immediate));
            foreach (var mutate in new Action<ProbeSnapshot>[] {
                s => SetTestName(Body(s, 23), "Ready changed"),
                s => SetTestName(RowRoot(SelectHistory(s), Body(s, 0)), "", "recycled-row"),
                s => s.RootNameFingerprint = "other chat",
                s => s.Complete = false })
            {
                var invalid = History(); AppendTestHistoryText(invalid, "pwrsi");
                AppendTestHistoryText(invalid, readyText); AppendTestHistoryText(invalid, "pwrsi");
                mutate(invalid); Reject(() => Evaluate(bound, invalid));
            }
        }

        private static void RunLongHistorySelfTest()
        {
            // Reproduce the field's 64-row shape, not just the two-row fixture used for basic guards.
            ProbeSnapshot History()
            {
                var snapshot = CreateTestSnapshot();
                SetTestName(snapshot.Nodes.Single(n => n.Node == 52), "2026년 9월 10일");
                for (var i = 2; i < 64; i++) AppendTestHistoryText(snapshot, "OLD " + i);
                return snapshot;
            }
            ProbeNode Body(ProbeSnapshot snapshot, int row) { return HistoryRows(SelectHistory(snapshot))[row][0]; }
            var baseline = CreateBaseline(History(), "M234567", true);
            var first = History();
            SetTestName(Body(first, 0), "old-file.xlsx", "changed-old-child");
            SetTestName(Body(first, 39), "old text refreshed");
            SetTestName(Body(first, 10), "help"); // Mutating an old body into a command must not execute it.
            Need(Evaluate(baseline, first) == null, "COMMAND_OLD_BODY_NOT_REQUEST");
            var candidate = AppendTestHistoryText(first, "pwrsi");
            Need(Evaluate(baseline, first) == candidate, "COMMAND_LONG_HISTORY_APPEND");
            var second = History();
            SetTestName(Body(second, 0), "another-old-file.png", "another-old-child");
            SetTestName(Body(second, 39), "old text refreshed again");
            var stable = AppendTestHistoryText(second, "pwrsi");
            ValidatePlainHistoryPrefix(first, second);
            Need(SameCandidate(first, candidate, second, Evaluate(baseline, second)) && Evaluate(baseline, second) == stable,
                "COMMAND_LONG_HISTORY_STABLE_CANDIDATE");
            var oldClocks = History();
            var oldBody = Body(oldClocks, 10);
            for (var i = 0; i < 2; i++)
            {
                var clock = new ProbeNode { Node = 1000 + i, Parent = oldBody.Parent, Document = oldBody.Document,
                    Visible = true, Enabled = true, Identity = new ElementIdentity("old-clock-" + i, oldClocks.Process.ProcessId,
                        "", "ControlType.Text", "", "fixture", "", 0, TokenStore.Hash(""), "<redacted>") };
                SetTestName(clock, "15:20");
                oldClocks.Nodes.Insert(oldClocks.Nodes.IndexOf(oldBody) + 1 + i, clock);
            }
            Need(Evaluate(baseline, oldClocks) == null, "COMMAND_OLD_CLOCKS_NOT_REQUEST");
            foreach (var mutate in new Action<ProbeSnapshot>[] {
                s => SetTestName(Body(s, 63), "changed last body"),
                s => SetTestName(Body(s, 61), "changed tail boundary"),
                s => SetTestName(RowRoot(SelectHistory(s), Body(s, 0)), "", "changed-old-row"),
                s => { var body = Body(s, 0); var row = RowRoot(SelectHistory(s), body); s.Nodes.RemoveAll(n => n.Node == body.Node || n.Node == row.Node); },
                s => { var a = RowRoot(SelectHistory(s), Body(s, 0)); var b = RowRoot(SelectHistory(s), Body(s, 1)); var id = a.Identity; a.Identity = b.Identity; b.Identity = id; },
                s => AppendTestHistoryText(s, "help"),
                s => { for (var i = 0; i < 4; i++) AppendTestHistoryText(s, "new non-command"); SetTestName(Body(s, 61), "changed previously protected body"); } })
            {
                var invalid = History(); AppendTestHistoryText(invalid, "pwrsi"); mutate(invalid);
                try { Evaluate(baseline, invalid); }
                catch (MonitorException) { continue; }
                throw new InvalidOperationException("Unsafe long-history change accepted.");
            }
        }

        private static void RunClockSiblingSelfTest()
        {
            void Reject(Action action)
            {
                try { action(); } catch (MonitorException) { return; }
                throw new InvalidOperationException("Old row content change was accepted as a new command.");
            }
            ProbeSnapshot History(string clock, string appended = null)
            {
                var snapshot = CreateTestSnapshot();
                SetTestName(snapshot.Nodes.Single(n => n.Node == 52), "help");
                SetTestName(snapshot.Nodes.Single(n => n.Node == 54), "old message");
                if (clock != null)
                {
                    var stamp = new ProbeNode { Node = 60, Parent = 51, Document = 2, Enabled = true, Visible = true,
                        Identity = new ElementIdentity("clock", snapshot.Process.ProcessId, "", "ControlType.Text", "", "fixture", "",
                            0, TokenStore.Hash(""), "<redacted>") };
                    SetTestName(stamp, clock);
                    snapshot.Nodes.Insert(snapshot.Nodes.FindIndex(n => n.Node == 52) + 1, stamp);
                }
                if (appended != null) AppendTestHistoryText(snapshot, appended);
                return snapshot;
            }
            var baseline = CreateBaseline(History("오후 2:26"), "M234567", true);
            foreach (var clock in new[] { null, "오후 2:27", "14:28" })
            {
                var changed = History(clock, "help");
                Need(Evaluate(baseline, changed) != null, "COMMAND_CLOCK_APPEND_SELFTEST");
                ValidatePlainHistoryPrefix(History("오후 2:26"), changed);
            }
            Need(Evaluate(baseline, History(null)) == null, "COMMAND_CLOCK_REMOVAL_NOT_REQUEST");
            ValidatePlainHistoryPrefix(History(null), History("오후 2:26"));
            Reject(() => Evaluate(baseline, History("help"))); // Replacing an old timestamp with a command must stop.
            Reject(() => Evaluate(baseline, History("1"))); // No unproven unread-counter exception.
            var primary = History(null); SetTestName(primary.Nodes.Single(n => n.Node == 52), "14:26");
            var primaryBaseline = CreateBaseline(primary, "M234567", true);
            Reject(() => Evaluate(primaryBaseline, History(null))); // Clock-like primary is protected content.
            var changedBody = History(null, "help"); SetTestName(changedBody.Nodes.Single(n => n.Node == 54), "changed");
            Reject(() => Evaluate(baseline, changedBody));
            var unverifiedClock = History("오후 2:27", "help"); unverifiedClock.Nodes.Single(n => n.Node == 60).NameShape = "UNAVAILABLE";
            Reject(() => Evaluate(baseline, unverifiedClock));
            var renamedRow = History(null, "help"); SetTestName(renamedRow.Nodes.Single(n => n.Node == 51), "", "new-row");
            Reject(() => Evaluate(baseline, renamedRow));
        }

        internal static ProbeNode AppendTestHistoryText(ProbeSnapshot snapshot, string text)
        {
            var history = SelectHistory(snapshot).History;
            var number = snapshot.Nodes.Max(n => n.Node) + 1;
            var row = new ProbeNode { Node = number, Parent = history.Node, Document = history.Document, Enabled = true, Visible = true,
                Identity = new ElementIdentity("append-" + number, snapshot.Process.ProcessId, "", "ControlType.Custom",
                    "", "fixture", "", 0, TokenStore.Hash(""), "<redacted>") };
            var node = new ProbeNode { Node = number + 1, Parent = number, Document = history.Document, Enabled = true, Visible = true,
                Identity = new ElementIdentity("append-" + (number + 1), snapshot.Process.ProcessId, "", "ControlType.Text",
                    "", "fixture", "", text.Length, TokenStore.Hash(text), "<redacted>") };
            SetTestName(node, text);
            snapshot.Nodes.Add(row); snapshot.Nodes.Add(node);
            return node;
        }

        internal static ProbeNode AppendTestHistorySiblingText(ProbeSnapshot snapshot, ProbeNode primary, string text)
        {
            var number = snapshot.Nodes.Max(n => n.Node) + 1;
            var node = new ProbeNode { Node = number, Parent = primary.Parent, Document = primary.Document,
                Enabled = true, Visible = true,
                Identity = new ElementIdentity("append-sibling-" + number, snapshot.Process.ProcessId, "", "ControlType.Text",
                    "", "fixture", "", 0, TokenStore.Hash(""), "<redacted>") };
            SetTestName(node, text);
            snapshot.Nodes.Add(node);
            return node;
        }

        internal static void SetTestName(ProbeNode node, string text, string runtime = null)
        {
            var id = node.Identity;
            node.Identity = new ElementIdentity(runtime ?? id.RuntimeId, id.ProcessId, id.AutomationId, id.ControlType,
                id.ClassName, id.FrameworkId, id.Patterns, text.Length, TokenStore.Hash(text), "<redacted>");
            node.NameShape = ReadOnlyProbe.NameShape(text, null);
            ReadOnlyCommands.ObserveName(node, text);
        }

        internal static ProbeSnapshot CreateTestSnapshot()
        {
            var snapshot = (ProbeSnapshot)typeof(ReadOnlyPair).GetMethod("Fixture", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            void Add(int number, int parent, string type)
            {
                snapshot.Nodes.Add(new ProbeNode { Node = number, Parent = parent, Document = 2, Enabled = true, Visible = true,
                    Identity = new ElementIdentity("node-" + number, snapshot.Process.ProcessId, "", "ControlType." + type,
                        "", "fixture", "", 0, TokenStore.Hash(""), "<redacted>") });
            }
            Add(50, 3, "Custom"); Add(51, 50, "Custom"); Add(52, 51, "Text"); Add(53, 50, "Custom"); Add(54, 53, "Text");
            Add(55, 3, "Custom"); Add(56, 55, "Custom"); Add(57, 56, "Text"); Add(58, 55, "Custom"); Add(59, 58, "Button");
            foreach (var node in snapshot.Nodes) node.PointerInside = false;
            return snapshot;
        }

        private static bool Type(ProbeNode node, string type) { return node.Identity.ControlType == "ControlType." + type; }
        private static bool Usable(ProbeNode node) { return node.Visible && node.Enabled; }
        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new MonitorException(reason, "Read-only receive diagnostic rejected: " + reason + ".");
        }
    }
}
