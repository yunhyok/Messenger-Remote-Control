using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
            bool blocked;
            return EvaluateCore(baseline, current, log, true, out blocked);
        }

        // blocked: the pinned first command row exists but is offscreen or disabled, so the round cannot advance.
        internal static ProbeNode Evaluate(Baseline baseline, ProbeSnapshot current, AuditLog log, out bool blocked)
        {
            return EvaluateCore(baseline, current, log, true, out blocked);
        }

        // shift > 0 only from ReobserveCandidate, after the bound Ready body was found that many rows higher (re-anchor).
        private static ProbeNode EvaluateCore(Baseline baseline, ProbeSnapshot current, AuditLog log, bool requireVisible,
            out bool blocked, int shift = 0)
        {
            blocked = false;
            var selected = ValidateContinuity(baseline, current);
            if (baseline.PlainCommands)
            {
                if (shift > 0) RequireShiftedPrefix(baseline.Selection, 0, selected, shift, baseline.ReadyRow, log);
                else ValidatePlainHistoryPrefix(baseline.Snapshot, current, log, baseline, acceptedRequest: !requireVisible);
                // A request is one complete new message row. After removing one validated clock sibling,
                // split reply/LLM Text nodes and command fragments can never become a request.
                var rows = HistoryRows(selected);
                var oldCount = baseline.ReadyRow >= 0 ? baseline.ReadyRow - shift + 1 : HistoryRows(baseline.Selection).Count;
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
                        AuditLog.Field("candidate_visible", appended.Length == 1 ? (object)appended[0].Visible :
                            appended.Length == 0 ? "NONE" : "NOT_UNIQUE"),
                        AuditLog.Field("candidate_enabled", appended.Length == 1 ? (object)appended[0].Enabled :
                            appended.Length == 0 ? "NONE" : "NOT_UNIQUE"),
                        AuditLog.Field("observation_mode", requireVisible ? "FRESH_ADMISSION" : "ACCEPTED_REOBSERVATION"),
                        AuditLog.Field("details_truncated", newContent.Length > 8), AuditLog.Field("history_shift", shift));
                }
                Need(appended.Length <= 1, "RECEIVE_COMMAND_NOT_UNIQUE");
                var admissible = appended.Length == 1 && appended[0].Enabled && (!requireVisible || appended[0].Visible);
                blocked = baseline.ReadyRow >= 0 && appended.Length == 1 && !admissible;
                return admissible ? appended[0] : null;
            }
            var matches = selected.Texts.Where(n => Usable(n) && n.Identity.NameHash == baseline.MarkerHash).Take(2).ToArray();
            Need(matches.Length <= 1, "RECEIVE_CANDIDATE_NOT_UNIQUE");
            return matches.SingleOrDefault();
        }

        internal static ProbeNode ReobserveCandidate(ObservationProof accepted, ProbeSnapshot current, AuditLog log = null)
        {
            Need(accepted != null && accepted.Baseline != null && accepted.Baseline.PlainCommands &&
                accepted.Final != null && accepted.Candidate != null, "RECEIVE_ACCEPTED_PROOF_REQUIRED");
            string reason;
            var candidate = Reobserve(accepted, current, log, out reason);
            if (candidate == null) log?.Write("INFO", "RECEIVE_ACCEPTED_CANDIDATE_LOST", LostFields(accepted, current, reason));
            Need(candidate != null, "RECEIVE_ACCEPTED_CANDIDATE_CHANGED");
            return candidate;
        }

        // Null (with a content-free reason) when the accepted command is not re-observed; the caller stops the send.
        private static ProbeNode Reobserve(ObservationProof accepted, ProbeSnapshot current, AuditLog log, out string reason)
        {
            var baseline = accepted.Baseline;
            var trail = TrailFor(accepted);
            // The identity path stays first for every snapshot whose bound Ready body has not visibly moved up. A unique
            // exact Ready body above its bound row means recycled slots, where an identity "match" is some other row.
            var ready = trail == null ? -1 : LocateRow(HistoryRows(SelectHistory(current)), trail.Ready);
            bool blocked;
            if (ready >= 0 && ready < baseline.ReadyRow)
            {
                var shift = baseline.ReadyRow - ready;
                var shifted = EvaluateCore(baseline, current, log, false, out blocked, shift);
                return Reanchor(trail, current, shift, shifted, log, out reason) == null ? null : shifted;
            }
            var candidate = EvaluateCore(baseline, current, log, false, out blocked);
            var same = SameIdentity(accepted.Final, accepted.Candidate, current, candidate);
            // After a re-anchor, positional ids alone no longer name the accepted row; Ready must be back at its bound row.
            if (same && (trail == null || !trail.Shifted || ready == baseline.ReadyRow))
            {
                if (trail != null) NoteObservation(trail, current, candidate);
                reason = "NONE";
                return candidate;
            }
            reason = same ? "READY_NOT_ANCHORED" : trail != null ? "IDENTITY_CHANGED" :
                baseline.ReadyRow < 0 ? "NOT_READY_BOUND" : "TRAIL_UNAVAILABLE";
            return null;
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
                // A snapshot that ReobserveCandidate re-anchored keeps the accepted range at its recorded offset.
                var previousShift = acceptedRequest ? RecordedShift(previous, baseline) : 0;
                var currentShift = acceptedRequest ? RecordedShift(current, baseline) : 0;
                if (previousShift != 0 || currentShift != 0)
                {
                    RequireShiftedPrefix(before, previousShift, after, currentShift, baseline.ReadyRow, log);
                    return;
                }
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
            var old = Recorded(previous);
            var fresh = Recorded(current);
            var trail = old != null && old.Shift > 0 ? old.Trail : fresh != null && fresh.Shift > 0 ? fresh.Trail : null;
            if (trail == null) return SameIdentity(previous, before, current, after);
            // Recycled slots reuse ids, so a re-anchored snapshot names the accepted message only through its anchor.
            return before != null && after != null && Anchored(trail, previous, before, old) &&
                Anchored(trail, current, after, fresh) && SameShape(before, after);
        }

        private static bool SameIdentity(ProbeSnapshot previous, ProbeNode before, ProbeSnapshot current, ProbeNode after)
        {
            return before != null && after != null && before.NativeHwnd == after.NativeHwnd && before.Identity.Equals(after.Identity) &&
                PathKey(SelectHistory(previous), before) == PathKey(SelectHistory(current), after);
        }

        private static bool Anchored(HistoryTrail trail, ProbeSnapshot snapshot, ProbeNode node, HistoryAnchor anchor)
        {
            if (anchor != null && anchor.Shift > 0) return anchor.Trail == trail && ReferenceEquals(anchor.Candidate, node);
            return SameIdentity(trail.AcceptedFinal, trail.Accepted, snapshot, node);
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

        // ---- Accepted-request history shift (re-anchor) ----------------------------------------------------------------
        // Field shape (Master 0.3.10, KI-Messenger): once the history list is full, appending a tall reply part evicts its
        // oldest row while recycled row nodes keep positional runtime ids. Older rows still pass the slot-by-slot identity
        // check, but the accepted command now sits higher under another id. Only an already accepted request may follow it,
        // and only by exact content: the bound Ready row's whole body found uniquely above its bound row and at most
        // MaxEvictedRows rows above the previous observation's Ready row; the accepted command's whole body, shape and
        // enabled state at its accepted offset after Ready; and every row after Ready unchanged and in order, with at most
        // the one part just sent appended. Fresh admission never uses this. Only lengths, SHA-256 name hashes and flags are
        // kept, and nothing about bodies is logged.
        internal const int MaxEvictedRows = 8;

        private sealed class HistoryTrail
        {
            internal ProbeSnapshot BaselineSnapshot, AcceptedFinal;
            internal ProbeNode Ready, Accepted; // Bound Ready Text and accepted command Text; only identity/hash/length are read.
            internal string MarkerHash;
            internal int ReadyRow, CommandOffset, AcceptedDepth, LastReadyRow;
            internal List<string> Rows; // Rows after Ready as last observed: one "length:sha256|..." signature per row.
            internal bool Shifted;

            internal bool Matches(Baseline baseline)
            {
                return baseline != null && ReferenceEquals(BaselineSnapshot, baseline.Snapshot) && ReadyRow == baseline.ReadyRow &&
                    string.Equals(MarkerHash, baseline.MarkerHash, StringComparison.Ordinal);
            }
        }

        private sealed class HistoryAnchor
        {
            internal HistoryTrail Trail;
            internal ProbeNode Candidate;
            internal int Shift, ReadyBefore, ReadyAfter, RowsAfterReady, AppendedRows; // Shift is relative to the bound Ready row.
        }

        private static readonly object TrailGate = new object();
        private static readonly ConditionalWeakTable<Baseline, HistoryTrail> Trails = new ConditionalWeakTable<Baseline, HistoryTrail>();
        private static readonly ConditionalWeakTable<ProbeSnapshot, HistoryAnchor> Anchors =
            new ConditionalWeakTable<ProbeSnapshot, HistoryAnchor>();

        // One trail per Ready-bound accepted request, derived from its unshifted accepted observation. Null disables the
        // re-anchor (the identity rule then stands alone), e.g. before Ready binding or when the shape cannot be derived.
        private static HistoryTrail TrailFor(ObservationProof accepted)
        {
            var baseline = accepted.Baseline;
            if (!baseline.PlainCommands || baseline.ReadyRow < 0) return null;
            lock (TrailGate)
            {
                HistoryTrail trail;
                if (Trails.TryGetValue(baseline, out trail) && SameName(trail.Accepted, accepted.Candidate)) return trail;
                trail = NewTrail(accepted);
                Trails.Remove(baseline);
                if (trail != null) Trails.Add(baseline, trail);
                return trail;
            }
        }

        private static HistoryTrail NewTrail(ObservationProof accepted)
        {
            var baseline = accepted.Baseline;
            try
            {
                var bound = HistoryRows(baseline.Selection);
                var ready = baseline.ReadyRow < bound.Count ? SafeContent(bound[baseline.ReadyRow]) : null;
                if (ready == null || ready.Length != 1) return null;
                var final = SelectHistory(accepted.Final);
                var rows = HistoryRows(final);
                if (LocateRow(rows, ready[0]) != baseline.ReadyRow) return null;
                var command = rows.FindIndex(row =>
                {
                    var content = SafeContent(row);
                    return content != null && content.Length == 1 && ReferenceEquals(content[0], accepted.Candidate);
                });
                if (command <= baseline.ReadyRow) return null;
                return new HistoryTrail { BaselineSnapshot = baseline.Snapshot, AcceptedFinal = accepted.Final, Ready = ready[0],
                    Accepted = accepted.Candidate, MarkerHash = baseline.MarkerHash, ReadyRow = baseline.ReadyRow,
                    CommandOffset = command - baseline.ReadyRow - 1, AcceptedDepth = Depth(final, accepted.Candidate),
                    LastReadyRow = baseline.ReadyRow, Rows = Signatures(rows, baseline.ReadyRow + 1) };
            }
            catch (MonitorException) { return null; }
        }

        // Index of the one row whose whole content is exactly this Text's body (length + hash); -1 none, -2 ambiguous.
        private static int LocateRow(List<ProbeNode[]> rows, ProbeNode text)
        {
            var found = -1;
            for (var i = 0; i < rows.Count; i++)
            {
                if (!rows[i].Any(node => SameName(node, text))) continue;
                var content = SafeContent(rows[i]);
                if (content == null || content.Length != 1 || !SameName(content[0], text)) continue;
                if (found >= 0) return -2;
                found = i;
            }
            return found;
        }

        private static ProbeNode[] SafeContent(ProbeNode[] row)
        {
            try { return RowContent(row); }
            catch (MonitorException) { return null; }
        }

        private static bool SameName(ProbeNode a, ProbeNode b)
        {
            return a != null && b != null && a.Identity.NameLength == b.Identity.NameLength &&
                string.Equals(a.Identity.NameHash, b.Identity.NameHash, StringComparison.Ordinal);
        }

        // Everything but the recycled runtime id: native hwnd, process, automation id, type, class, framework, patterns, body.
        private static bool SameShape(ProbeNode a, ProbeNode b)
        {
            var x = a.Identity;
            var y = b.Identity;
            return a.NativeHwnd == b.NativeHwnd && x.ProcessId == y.ProcessId &&
                string.Equals(x.AutomationId, y.AutomationId, StringComparison.Ordinal) &&
                string.Equals(x.ControlType, y.ControlType, StringComparison.Ordinal) &&
                string.Equals(x.ClassName, y.ClassName, StringComparison.Ordinal) &&
                string.Equals(x.FrameworkId, y.FrameworkId, StringComparison.Ordinal) &&
                string.Equals(x.Patterns, y.Patterns, StringComparison.Ordinal) && SameName(a, b);
        }

        private static int Depth(HistorySelection selection, ProbeNode text)
        {
            var depth = 0;
            for (var node = text; node.Parent != selection.History.Node; node = selection.ByNode[node.Parent]) depth++;
            return depth;
        }

        private static List<string> Signatures(List<ProbeNode[]> rows, int start)
        {
            var result = new List<string>();
            for (var i = start; i < rows.Count; i++)
                result.Add(string.Join("|", RowContent(rows[i]).Select(n => n.Identity.NameLength + ":" + n.Identity.NameHash)));
            return result;
        }

        private static HistoryAnchor Recorded(ProbeSnapshot snapshot)
        {
            HistoryAnchor anchor;
            return snapshot != null && Anchors.TryGetValue(snapshot, out anchor) ? anchor : null;
        }

        private static int RecordedShift(ProbeSnapshot snapshot, Baseline baseline)
        {
            var anchor = Recorded(snapshot);
            return anchor != null && anchor.Trail.Matches(baseline) ? anchor.Shift : 0;
        }

        private static void Remember(ProbeSnapshot snapshot, HistoryAnchor anchor)
        {
            lock (TrailGate)
            {
                Anchors.Remove(snapshot);
                Anchors.Add(snapshot, anchor);
            }
        }

        // First sight of an identity-path snapshot records its rows after Ready. It never rejects anything.
        private static void NoteObservation(HistoryTrail trail, ProbeSnapshot current, ProbeNode candidate)
        {
            lock (trail)
            {
                var known = Recorded(current);
                if (known != null && known.Trail == trail) return;
                List<string> rows;
                try { rows = Signatures(HistoryRows(SelectHistory(current)), trail.ReadyRow + 1); }
                catch (MonitorException) { return; } // Keeps the previous record, so a later shift cannot be accepted from it.
                Remember(current, new HistoryAnchor { Trail = trail, Candidate = candidate, ReadyBefore = trail.LastReadyRow,
                    ReadyAfter = trail.ReadyRow, RowsAfterReady = rows.Count, AppendedRows = rows.Count - trail.Rows.Count });
                trail.Rows = rows;
                trail.LastReadyRow = trail.ReadyRow;
            }
        }

        // Accepts a snapshot whose bound Ready body sits `shift` rows above the bound row, or returns null with the reason.
        // A snapshot is compared with the trail once, at first sight; re-evaluating it (the handoff re-checks the same two
        // snapshots) reuses that result after the structural checks, so the order of re-checks cannot change the outcome.
        private static HistoryAnchor Reanchor(HistoryTrail trail, ProbeSnapshot current, int shift, ProbeNode candidate,
            AuditLog log, out string reason)
        {
            var selection = SelectHistory(current);
            var rows = HistoryRows(selection);
            var ready = trail.ReadyRow - shift;
            var commandRow = ready + 1 + trail.CommandOffset;
            var content = commandRow < rows.Count ? SafeContent(rows[commandRow]) : null;
            var command = content != null && content.Length == 1 ? content[0] : null;
            string ignored;
            if (command == null || !ReadOnlyCommands.TryMatchNode(command, out ignored)) reason = "COMMAND_MISSING";
            else if (!SameShape(trail.Accepted, command) || Depth(selection, command) != trail.AcceptedDepth) reason = "COMMAND_CHANGED";
            else if (command.Enabled != trail.Accepted.Enabled) reason = "COMMAND_DISABLED";
            else if (!ReferenceEquals(command, candidate)) reason = "COMMAND_NOT_FIRST";
            else reason = null;
            if (reason != null) return null;
            lock (trail)
            {
                var known = Recorded(current);
                if (known != null && known.Trail == trail)
                {
                    var same = known.Shift == shift && ReferenceEquals(known.Candidate, candidate);
                    reason = same ? "NONE" : "ANCHOR_CHANGED";
                    return same ? known : null;
                }
                var step = trail.LastReadyRow - ready;
                List<string> after = null;
                if (step < 0) reason = "SHIFT_REVERSED";
                else if (step > MaxEvictedRows) reason = "SHIFT_OUT_OF_RANGE";
                else
                {
                    try { after = Signatures(rows, ready + 1); }
                    catch (MonitorException) { reason = "ROW_UNREADABLE"; }
                }
                if (reason == null)
                {
                    // Rows after Ready keep order and bodies; only the one part just sent may be appended at the end.
                    var before = trail.Rows;
                    if (after.Count < before.Count) reason = "ROWS_LOST";
                    else if (after.Count > before.Count + 1) reason = "ROWS_APPENDED_EXCESS";
                    else if (!before.SequenceEqual(after.Take(before.Count), StringComparer.Ordinal)) reason = "ROWS_CHANGED";
                }
                if (reason != null) return null;
                var anchor = new HistoryAnchor { Trail = trail, Candidate = candidate, Shift = shift, ReadyBefore = trail.LastReadyRow,
                    ReadyAfter = ready, RowsAfterReady = after.Count, AppendedRows = after.Count - trail.Rows.Count };
                Remember(current, anchor);
                trail.Rows = after;
                trail.LastReadyRow = ready;
                trail.Shifted = true;
                reason = "NONE";
                log?.Write("INFO", "RECEIVE_HISTORY_SHIFTED", ShiftedFields(anchor));
                return anchor;
            }
        }

        // Row identity/order stays positional (recycled slots keep their ids), so every exposed row is still compared slot
        // by slot; the protected Ready..command range moves with the re-anchored Ready row and is compared by content.
        private static void RequireShiftedPrefix(HistorySelection previous, int previousShift, HistorySelection current,
            int currentShift, int readyRow, AuditLog log)
        {
            RequireHistoryPrefix(previous, current, log, int.MaxValue);
            var before = HistoryRows(previous);
            var after = HistoryRows(current);
            var oldReady = readyRow - previousShift;
            var newReady = readyRow - currentShift;
            var end = Math.Min(FirstCommandRow(before, oldReady + 1), before.Count - 1);
            for (var row = oldReady + 1; row <= end; row++)
            {
                var target = row - oldReady + newReady;
                var oldContent = RowContent(before[row]);
                var newContent = target < after.Count ? RowContent(after[target]) : null;
                if (newContent != null && oldContent.Length == newContent.Length &&
                    oldContent.Select((node, index) => SameShape(node, newContent[index])).All(same => same)) continue;
                log?.Write("INFO", "RECEIVE_HISTORY_REJECTED", AuditLog.Field("reason", "RECEIVE_HISTORY_CONTENT_CHANGED"),
                    AuditLog.Field("comparison", "SHIFTED_CONTENT"), AuditLog.Field("row_index", row),
                    AuditLog.Field("target_row_index", target), AuditLog.Field("before_shift", previousShift),
                    AuditLog.Field("after_shift", currentShift), AuditLog.Field("before_rows", before.Count),
                    AuditLog.Field("after_rows", after.Count));
                Need(false, "RECEIVE_HISTORY_CONTENT_CHANGED");
            }
        }

        private static AuditLog.LogField[] ShiftedFields(HistoryAnchor anchor)
        {
            return new[] {
                AuditLog.Field("shift", anchor.ReadyBefore - anchor.ReadyAfter), AuditLog.Field("total_shift", anchor.Shift),
                AuditLog.Field("bound_ready_row", anchor.Trail.ReadyRow), AuditLog.Field("ready_row_before", anchor.ReadyBefore),
                AuditLog.Field("ready_row_after", anchor.ReadyAfter), AuditLog.Field("rows_after_ready", anchor.RowsAfterReady),
                AuditLog.Field("appended_rows", anchor.AppendedRows), AuditLog.Field("command_offset", anchor.Trail.CommandOffset),
                AuditLog.Field("content_compared", "LENGTH_SHA256_ONLY") };
        }

        // Content-free description of where the accepted command went; never a name, body or title.
        private static AuditLog.LogField[] LostFields(ObservationProof accepted, ProbeSnapshot current, string reason)
        {
            var baseline = accepted.Baseline;
            var name = accepted.Candidate;
            int runtimeRow = -1, exactRows = 0, readyNow = -1, rowCount = -1;
            bool nameEqual = false, readyAtBound = false;
            HistoryTrail trail;
            int last;
            lock (TrailGate) last = Trails.TryGetValue(baseline, out trail) ? trail.LastReadyRow : baseline.ReadyRow;
            try
            {
                var selection = SelectHistory(current);
                var rows = HistoryRows(selection);
                rowCount = rows.Count;
                for (var i = 0; i < rows.Count; i++)
                {
                    if (runtimeRow < 0 && rows[i].Any(n => string.Equals(n.Identity.RuntimeId, name.Identity.RuntimeId,
                        StringComparison.Ordinal))) runtimeRow = i;
                    if (!rows[i].Any(n => SameName(n, name))) continue;
                    nameEqual = true;
                    var content = SafeContent(rows[i]);
                    if (content != null && content.Length == 1 && SameName(content[0], name)) exactRows++;
                }
                if (baseline.ReadyRow >= 0)
                {
                    try { readyAtBound = IsReadyRow(selection, baseline.Marker, baseline.ReadyRow); }
                    catch (MonitorException) { }
                    var bound = HistoryRows(baseline.Selection);
                    var ready = baseline.ReadyRow < bound.Count ? SafeContent(bound[baseline.ReadyRow]) : null;
                    if (ready != null && ready.Length == 1) readyNow = LocateRow(rows, ready[0]);
                }
            }
            catch (MonitorException) { } // Diagnostics only; the rejection stands either way.
            return new[] {
                AuditLog.Field("reanchor_result", reason), AuditLog.Field("accepted_runtime_present", runtimeRow >= 0),
                AuditLog.Field("accepted_runtime_row", runtimeRow), AuditLog.Field("accepted_name_equal", nameEqual),
                AuditLog.Field("exact_name_rows", exactRows), AuditLog.Field("bound_ready_row", baseline.ReadyRow),
                AuditLog.Field("last_ready_row", last), AuditLog.Field("ready_at_bound_row", readyAtBound),
                AuditLog.Field("ready_row_now", readyNow), AuditLog.Field("current_rows", rowCount),
                AuditLog.Field("rows_after_ready", readyNow >= 0 ? rowCount - readyNow - 1 : -1),
                AuditLog.Field("shift_candidate", readyNow >= 0 && readyNow <= last ? last - readyNow : -1),
                AuditLog.Field("max_evicted_rows", MaxEvictedRows) };
        }

        // ---- Cheap idle change trigger -------------------------------------------------------------------------
        // These pick only WHEN the next full snapshot starts. They read no content, prove nothing and never take part
        // in admission: a command still needs the same exact whole message in two distinct full snapshots.
        internal const int TailDepthLimit = 32;
        private const int TailPathStepLimit = 128; // Raw-walker hops per sample; a wider recorded shape keeps today's cadence.
        private static readonly TimeSpan TailWaitCap = TimeSpan.FromSeconds(3); // Idle full-snapshot cadence bound; a change the tail chain cannot see waits at most this long.

        // Sibling indices from the snapshot root (node 1) down to the history container, in raw-walker order.
        // Null when the recorded shape cannot be re-walked cheaply; the caller then captures unconditionally.
        internal static int[] TailPath(ProbeSnapshot snapshot, ProbeNode history)
        {
            if (snapshot == null || snapshot.Nodes == null || history == null) return null;
            var byNode = new Dictionary<int, ProbeNode>();
            foreach (var node in snapshot.Nodes)
            {
                if (byNode.ContainsKey(node.Node)) return null;
                byNode.Add(node.Node, node);
            }
            ProbeNode recorded;
            if (!byNode.TryGetValue(history.Node, out recorded) || !ReferenceEquals(recorded, history)) return null;
            var indices = new List<int>();
            var steps = 0;
            var cursor = history;
            while (cursor.Parent != 0)
            {
                ProbeNode parent;
                if (!byNode.TryGetValue(cursor.Parent, out parent)) return null;
                var index = 0;
                foreach (var sibling in snapshot.Nodes)
                    if (sibling.Parent == cursor.Parent && sibling.Node < cursor.Node) index++;
                indices.Add(index);
                steps += index + 1;
                if (indices.Count > TailDepthLimit || steps > TailPathStepLimit) return null; // Also breaks a cyclic parent chain.
                cursor = parent;
            }
            if (cursor.Node != 1 || indices.Count == 0) return null;
            indices.Reverse();
            return indices.ToArray();
        }

        // Unknown (null) is never "same": an unreadable tail always starts the next full snapshot.
        internal static bool SameTailChain(string[] previous, string[] current)
        {
            if (previous == null || current == null || previous.Length != current.Length) return false;
            for (var i = 0; i < previous.Length; i++)
                if (!string.Equals(previous[i], current[i], StringComparison.Ordinal)) return false;
            return true;
        }

        // Pure decision for the change trigger. Null means "keep sampling"; every other value starts a full snapshot.
        internal static string TriggerReason(bool idle, bool minimized, bool failed, bool changed, TimeSpan waited, TimeSpan cap)
        {
            if (!idle) return "NOT_IDLE";
            if (minimized) return "MINIMIZED";
            if (failed) return "PROBE_FAILED";
            if (changed) return "TAIL_CHANGED";
            return waited >= cap ? "PERIODIC" : null;
        }

        // Pure rule for the idle command wait; the cheap tail trigger and the yield point share it. Only a background
        // plain-command wait that has completed at least one poll after binding Ready, with no candidate waiting for its
        // repeat observation and no pinned-but-blocked command row, is idle. Everything else keeps today's cadence and
        // can never be interrupted: a pending candidate always completes its repeat observation first.
        internal static bool MayYield(bool continuousWait, bool plainCommands, bool hasAcceptedProof, bool collectMetadata,
            int polls, bool candidatePending, bool commandBlocked, int readyRow)
        {
            return continuousWait && plainCommands && !hasAcceptedProof && !collectMetadata && polls > 0 &&
                !candidatePending && !commandBlocked && readyRow >= 0;
        }

        public static string Run(IntPtr window, AuditLog log, string marker, Func<bool> stop, Action<string> progress)
        {
            return Observe(window, log, marker, stop, progress, new object(), true).Message;
        }

        internal static ObservationResult Observe(IntPtr window, AuditLog log, string marker, Func<bool> stop,
            Action<string> progress, object owner, bool collectMetadata, Action<string, ProbeSnapshot> inspectSnapshot = null,
            Baseline suppliedBaseline = null, bool continuousWait = false, bool plainCommands = false,
            ObservationProof acceptedProof = null, bool singleRefresh = false, OperationalTarget target = null,
            Func<bool> yieldRequested = null)
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
                    Need(new IntPtr(Read(() => root.Current.NativeWindowHandle)) == window, "RECEIVE_ROOT_CHANGED");
                    // A dropped root returns no runtime id; report the receive stage instead of the point-probe reason.
                    var rootRuntime = Read(root.GetRuntimeId);
                    Need(rootRuntime != null && rootRuntime.Length > 0 && rootRuntime.Length <= 64 &&
                        UiaPointProbe.Format(rootRuntime) == snapshot.Nodes.Single(n => n.Node == 1).Identity.RuntimeId, "RECEIVE_ROOT_CHANGED");
                    Need(log.Fingerprint(Read(() => root.Current.Name)) == snapshot.RootNameFingerprint, "RECEIVE_ROOT_CHANGED");
                }
                // Cheap structural tail probe for the idle wait: raw-walker navigation plus runtime ids only.
                // No Name/Value/TextPattern read, no window discovery, no focus or activation, no element kept
                // between samples, and nothing it returns is ever admitted as evidence. Null means changed/unknown.
                string[] SampleTailChain(int[] path, string historyRuntimeId, string rootRuntimeId)
                {
                    var walker = TreeWalker.RawViewWalker;
                    AutomationElement Guarded(AutomationElement element)
                    {
                        if (element == null) return null;
                        var elementPid = element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty, true);
                        var password = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
                        return elementPid is int && (int)elementPid == process.ProcessId && password is bool && !(bool)password
                            ? element : null;
                    }
                    string Runtime(AutomationElement element)
                    {
                        var id = element.GetRuntimeId();
                        return id != null && id.Length > 0 && id.Length <= 64 ? UiaPointProbe.Format(id) : null;
                    }
                    try
                    {
                        Alive();
                        var cursor = Guarded(AutomationElement.FromHandle(window));
                        if (cursor == null || Runtime(cursor) != rootRuntimeId) return null;
                        foreach (var index in path)
                        {
                            Alive();
                            var child = Guarded(walker.GetFirstChild(cursor));
                            for (var i = 0; child != null && i < index; i++) child = Guarded(walker.GetNextSibling(child));
                            if (child == null) return null;
                            cursor = child;
                        }
                        Alive();
                        var history = Runtime(cursor);
                        if (history == null || history != historyRuntimeId) return null;
                        var chain = new List<string> { history };
                        for (var depth = 0; depth < TailDepthLimit; depth++)
                        {
                            Alive();
                            var last = walker.GetLastChild(cursor);
                            if (last == null) break;
                            cursor = Guarded(last);
                            if (cursor == null) return null;
                            var runtime = Runtime(cursor);
                            if (runtime == null) return null;
                            chain.Add(runtime);
                        }
                        Alive();
                        return chain.ToArray();
                    }
                    catch (MonitorException) { throw; } // Cancellation, target and budget guards keep their own reason.
                    catch (Exception) { return null; }  // An unavailable or foreign tail is treated as changed/unknown.
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
                        var runtimeId = cursor.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty, true) as int[];
                        Need(runtimeId != null && runtimeId.Length > 0 && runtimeId.Length <= 64, "RECEIVE_METADATA_ELEMENT_CHANGED");
                        var runtime = UiaPointProbe.Format(runtimeId);
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
                    var readyWait = Stopwatch.StartNew();
                    while ((ready = BindReadyBoundary(baseline, firstSnapshot, log)) == null)
                    {
                        phaseClock.Restart(); // Each retry capture gets its own snapshot budget; the wait is bounded below.
                        Alive();
                        Need(readyWait.Elapsed < TimeSpan.FromSeconds(60), "RECEIVE_READY_NOT_OBSERVED");
                        ReleaseLive(firstSnapshot);
                        Thread.Sleep(100);
                        firstSnapshot = Capture();
                    }
                    baseline = ready;
                }
                var commandBlocked = false;
                ProbeNode Candidate(ProbeSnapshot snapshot)
                {
                    if (acceptedProof != null) { commandBlocked = false; return ReobserveCandidate(acceptedProof, snapshot, log); }
                    bool blocked;
                    var evaluated = Evaluate(baseline, snapshot, log, out blocked);
                    commandBlocked = blocked;
                    return evaluated;
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
                // Structure-only state for the idle change trigger; never an element and never evidence.
                int[] tailPath = null;
                string tailHistoryRuntime = null, tailRootRuntime = null;
                string[] previousChain = null;
                // Yield point: the caller may take over the idle wait for a scheduled Master task (watchdog check or
                // notice). It returns no proof and nothing observed is carried over; the caller re-enters with the same
                // pre-Ready baseline, which re-binds the same Ready row and re-evaluates every row after it.
                bool YieldNow()
                {
                    if (yieldRequested == null || !yieldRequested()) return false;
                    Alive(); // A stop or target change keeps its own reason instead of looking like a yield.
                    return true;
                }
                ObservationResult Yielded()
                {
                    Result(log, "WAIT_YIELDED", "NONE", polls);
                    return new ObservationResult("WAIT_YIELDED", "NONE",
                        "WAIT_YIELDED - the idle command wait paused for a scheduled Master task. No input, click or send was executed.");
                }
                while (true)
                {
                    phaseClock.Restart(); // The inter-snapshot wait is not charged to the preceding snapshot's 15-second cap.
                    var wait = Stopwatch.StartNew();
                    // Idle waiting state only. Waiting for the repeat observation, a pinned not-yet-visible command row,
                    // the first poll after Ready and every non-background flow keep the unconditional capture cadence.
                    var idleWait = MayYield(continuousWait, plainCommands, acceptedProof != null, collectMetadata, polls,
                        previousCandidate != null, commandBlocked, baseline.ReadyRow);
                    if (idleWait && YieldNow()) return Yielded();
                    var samples = 0;
                    var chainDepth = 0;
                    var trigger = TriggerReason(idleWait, idleWait && OperationalTarget.IsMinimized(window),
                        idleWait && tailPath == null, false, TimeSpan.Zero, TailWaitCap);
                    if (trigger == null)
                    {
                        var reference = previousChain; // The tail as it stood when the last full snapshot was decided.
                        while (trigger == null)
                        {
                            var sample = SampleTailChain(tailPath, tailHistoryRuntime, tailRootRuntime);
                            samples++;
                            previousChain = sample;
                            if (sample == null)
                            {
                                trigger = TriggerReason(true, false, true, false, wait.Elapsed, TailWaitCap);
                                break;
                            }
                            chainDepth = sample.Length;
                            trigger = TriggerReason(true, false, false, reference != null && !SameTailChain(reference, sample),
                                wait.Elapsed, TailWaitCap);
                            if (reference == null) reference = sample;
                            if (trigger != null) break;
                            if (YieldNow()) return Yielded(); // Only an unchanged tail; a change starts the full snapshot first.
                            for (var i = 0; i < 5; i++) { Alive(); Thread.Sleep(50); }
                        }
                    }
                    else previousChain = null; // Nothing was sampled, so the next wait starts from an unknown tail.
                    // The probe never shortens the existing pause, so no full snapshot starts earlier than it does today.
                    var minimumWait = baseline.ReadyRow >= 0 ? 200 : 1000;
                    while (wait.ElapsedMilliseconds < minimumWait) { Alive(); Thread.Sleep(50); }
                    // samples=0 with PROBE_FAILED means the recorded shape was unusable, not that navigation failed.
                    log.Write("INFO", "RECEIVE_CHANGE_TRIGGER", AuditLog.Field("reason", trigger),
                        AuditLog.Field("waited_ms", wait.ElapsedMilliseconds), AuditLog.Field("samples", samples),
                        AuditLog.Field("chain_depth", chainDepth));
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
                    tailPath = null;
                    tailHistoryRuntime = null;
                    tailRootRuntime = null;
                    try
                    {
                        // Structure only: the sibling path plus the two runtime ids the cheap probe re-verifies.
                        var tail = SelectHistory(current);
                        tailPath = TailPath(current, tail.History);
                        tailHistoryRuntime = tail.History.Identity.RuntimeId;
                        tailRootRuntime = tail.Root.Identity.RuntimeId;
                    }
                    catch (MonitorException) { tailPath = null; } // No cheap probe without a usable shape; the cadence stays as before.
                    if (candidate != null) progress("WAITING_FOR_REPEAT");
                    else if (commandBlocked)
                    {
                        // The pinned command row stays; it is not admissible yet and no later row may replace it.
                        log.Write("INFO", "RECEIVE_COMMAND_NOT_VISIBLE", AuditLog.Field("poll", polls));
                        progress("COMMAND_NOT_VISIBLE");
                    }
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
            RunChangeTriggerSelfTest();
            RunYieldSelfTest();
        }

        // The change trigger decides only WHEN a full snapshot starts, so these cover the pure parts alone:
        // path computation from the recorded Node/Parent graph, chain comparison and the decision itself.
        private static void RunChangeTriggerSelfTest()
        {
            var cap = TimeSpan.FromSeconds(5);
            var snapshot = CreateTestSnapshot();
            var path = TailPath(snapshot, SelectHistory(snapshot).History);
            Need(path != null && path.SequenceEqual(new[] { 0, 0, 1 }), "RECEIVE_SELF_TEST_TAIL_PATH");
            var renumbered = CreateTestSnapshot();
            foreach (var node in renumbered.Nodes)
            {
                if (node.Node > 1) node.Node += 100;
                if (node.Parent > 1) node.Parent += 100;
                if (node.Document > 0) node.Document += 100;
            }
            // Preorder ordinals are not identity: the same shape keeps the same sibling path.
            Need(path.SequenceEqual(TailPath(renumbered, SelectHistory(renumbered).History)), "RECEIVE_SELF_TEST_TAIL_PATH_ORDINALS");
            var inserted = CreateTestSnapshot();
            var moved = inserted.Nodes.Single(n => n.Node == 50);
            inserted.Nodes.Add(new ProbeNode { Node = 45, Parent = 3, Document = 2 });
            Need(TailPath(inserted, moved).SequenceEqual(new[] { 0, 0, 2 }), "RECEIVE_SELF_TEST_TAIL_PATH_SIBLINGS");
            var orphan = CreateTestSnapshot();
            var orphanHistory = orphan.Nodes.Single(n => n.Node == 50);
            orphan.Nodes.RemoveAll(n => n.Node == 3);
            Need(TailPath(orphan, orphanHistory) == null, "RECEIVE_SELF_TEST_TAIL_PATH_BROKEN");
            var detached = CreateTestSnapshot();
            detached.Nodes.Single(n => n.Node == 2).Parent = 0;
            Need(TailPath(detached, detached.Nodes.Single(n => n.Node == 50)) == null, "RECEIVE_SELF_TEST_TAIL_PATH_ROOTLESS");
            var cycle = CreateTestSnapshot();
            cycle.Nodes.Single(n => n.Node == 3).Parent = 50;
            Need(TailPath(cycle, cycle.Nodes.Single(n => n.Node == 50)) == null, "RECEIVE_SELF_TEST_TAIL_PATH_CYCLE");
            var wide = CreateTestSnapshot();
            for (var i = 0; i < 200; i++) wide.Nodes.Add(new ProbeNode { Node = 1000 + i, Parent = 3, Document = 2 });
            var far = new ProbeNode { Node = 2000, Parent = 3, Document = 2 };
            wide.Nodes.Add(far);
            Need(TailPath(wide, far) == null, "RECEIVE_SELF_TEST_TAIL_PATH_WIDTH"); // Too many hops to stay cheap.
            var duplicated = CreateTestSnapshot();
            duplicated.Nodes.Add(new ProbeNode { Node = 50, Parent = 3, Document = 2 });
            Need(TailPath(duplicated, duplicated.Nodes.First(n => n.Node == 50)) == null, "RECEIVE_SELF_TEST_TAIL_PATH_DUPLICATE");
            var chain = new[] { "7,1", "7,2", "7,3" };
            Need(SameTailChain(chain, new[] { "7,1", "7,2", "7,3" }), "RECEIVE_SELF_TEST_TAIL_CHAIN_SAME");
            Need(!SameTailChain(chain, new[] { "7,1", "7,2" }) && !SameTailChain(chain, new[] { "7,1", "7,2", "7,4" }) &&
                !SameTailChain(chain, null) && !SameTailChain(null, chain) && !SameTailChain(null, null),
                "RECEIVE_SELF_TEST_TAIL_CHAIN_DIFFERENT");
            // Mirrors the live GetLastChild descent on the recorded graph: both appended-row and appended-Text
            // (grouped consecutive messages inside the existing last row) change the tail chain.
            string[] Descent(ProbeSnapshot source)
            {
                var walked = new List<string>();
                var cursor = SelectHistory(source).History;
                for (var depth = 0; depth <= TailDepthLimit; depth++)
                {
                    walked.Add(cursor.Identity.RuntimeId);
                    var last = source.Nodes.Where(n => n.Parent == cursor.Node).OrderBy(n => n.Node).LastOrDefault();
                    if (last == null) break;
                    cursor = last;
                }
                return walked.ToArray();
            }
            var idle = Descent(CreateTestSnapshot());
            Need(SameTailChain(idle, Descent(CreateTestSnapshot())), "RECEIVE_SELF_TEST_TAIL_IDLE");
            var newRow = CreateTestSnapshot();
            AppendTestHistoryText(newRow, "help");
            Need(!SameTailChain(idle, Descent(newRow)), "RECEIVE_SELF_TEST_TAIL_NEW_ROW");
            var grouped = CreateTestSnapshot();
            AppendTestHistorySiblingText(grouped, grouped.Nodes.Single(n => n.Node == 54), "help");
            Need(!SameTailChain(idle, Descent(grouped)), "RECEIVE_SELF_TEST_TAIL_GROUPED_TEXT");
            Need(TriggerReason(false, false, false, true, TimeSpan.FromSeconds(9), cap) == "NOT_IDLE" &&
                TriggerReason(true, true, true, true, TimeSpan.FromSeconds(9), cap) == "MINIMIZED" &&
                TriggerReason(true, false, true, true, TimeSpan.Zero, cap) == "PROBE_FAILED" &&
                TriggerReason(true, false, false, true, TimeSpan.Zero, cap) == "TAIL_CHANGED" &&
                TriggerReason(true, false, false, false, cap, cap) == "PERIODIC" &&
                TriggerReason(true, false, false, false, TimeSpan.FromSeconds(6), cap) == "PERIODIC" &&
                TriggerReason(true, false, false, false, TimeSpan.FromSeconds(4.9), cap) == null,
                "RECEIVE_SELF_TEST_TRIGGER_REASON");
            // The cap stays below one field snapshot (5.6-6.8 s), so the worst-case cadence is not slower than before.
            Need(TailWaitCap <= TimeSpan.FromSeconds(5) && TailDepthLimit == 32, "RECEIVE_SELF_TEST_TRIGGER_BOUNDS");
        }

        // The yield rule and the caller's re-entry: after a yield the caller keeps the SAME pre-Ready baseline and marker,
        // Observe re-binds the unique Ready row, and a watchdog notice row (a non-command Master row after Ready) never
        // hides or replaces the first command row that follows it.
        private static void RunYieldSelfTest()
        {
            Need(MayYield(true, true, false, false, 1, false, false, 0) && MayYield(true, true, false, false, 9, false, false, 23),
                "RECEIVE_SELF_TEST_YIELD_IDLE");
            Need(!MayYield(false, true, false, false, 1, false, false, 0) && !MayYield(true, false, false, false, 1, false, false, 0) &&
                !MayYield(true, true, true, false, 1, false, false, 0) && !MayYield(true, true, false, true, 1, false, false, 0) &&
                !MayYield(true, true, false, false, 0, false, false, 0) && !MayYield(true, true, false, false, 1, true, false, 0) &&
                !MayYield(true, true, false, false, 1, false, true, 0) && !MayYield(true, true, false, false, 1, false, false, -1),
                "RECEIVE_SELF_TEST_YIELD_GUARDS");
            var allowed = 0;
            for (var bits = 0; bits < 256; bits++)
                if (MayYield((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0 ? 1 : 0,
                    (bits & 32) != 0, (bits & 64) != 0, (bits & 128) != 0 ? 0 : -1)) allowed++;
            Need(allowed == 1, "RECEIVE_SELF_TEST_YIELD_TABLE");

            const string marker = "M234567";
            var readyText = SupervisedSendTest.ReadyText("D234567");
            var notices = new[] {
                "WATCHDOG D345678 | 완료 알림 | PART 001/002\r\n" +
                    "PowerSI (PID 4321): AFS Finished, Total Sampling Points = 1201 (출처: 버퍼)",
                "WATCHDOG D345678 | 완료 알림 | PART 002/002\r\n남은 감시 없음 — watchdog 꺼짐" };
            Need(notices.All(WatchdogText.IsNotice), "RECEIVE_SELF_TEST_YIELD_NOTICE_FIXTURE");
            ProbeSnapshot History(bool ready, int noticeRows = 0, string command = null, bool commandVisible = true)
            {
                var snapshot = CreateTestSnapshot();
                SetTestName(snapshot.Nodes.Single(n => n.Node == 52), "old message");
                SetTestName(snapshot.Nodes.Single(n => n.Node == 54), "old reply");
                if (ready) AppendTestHistoryText(snapshot, readyText);
                for (var i = 0; i < noticeRows; i++)
                    AppendTestHistorySiblingText(snapshot, AppendTestHistoryText(snapshot, notices[i]), "14:0" + i);
                if (command != null) AppendTestHistoryText(snapshot, command).Visible = commandVisible;
                return snapshot;
            }
            void Reject(Action action)
            {
                try { action(); } catch (MonitorException) { return; }
                throw new InvalidOperationException("Unsafe yield re-entry accepted.");
            }
            var preReady = CreateBaseline(History(false), marker, true);
            var window = new IntPtr(preReady.Snapshot.Process.WindowHandle);
            bool blocked;
            var idle = History(true);
            var bound = BindReadyBoundary(preReady, idle);
            Need(bound != null && Evaluate(bound, idle, null, out blocked) == null && !blocked &&
                MayYield(true, true, false, false, 1, false, blocked, bound.ReadyRow), "RECEIVE_SELF_TEST_YIELD_FIRST_WAIT");
            // Re-entry after a check: same marker, same pre-Ready baseline object, Ready re-bound to the same row.
            foreach (var noticeRows in new[] { 0, 1, 2 })
            {
                var entry = History(true, noticeRows);
                Need(ReferenceEquals(AcceptSuppliedBaseline(preReady, entry, marker, window, true), preReady),
                    "RECEIVE_SELF_TEST_YIELD_SAME_BASELINE");
                var rebound = BindReadyBoundary(preReady, entry);
                Need(rebound != null && rebound.ReadyRow == bound.ReadyRow && Evaluate(rebound, entry, null, out blocked) == null &&
                    !blocked, "RECEIVE_SELF_TEST_YIELD_REBOUND_IDLE");
                // A command that arrived during the check is found on the re-entry's first capture, then repeated.
                var first = History(true, noticeRows, "pwrsi"); var second = History(true, noticeRows, "pwrsi");
                var reboundFirst = BindReadyBoundary(preReady, first);
                var candidate = Evaluate(reboundFirst, first, null, out blocked);
                Need(candidate != null && !blocked && candidate == SelectHistory(first).Texts.Last() &&
                    SameCandidate(first, candidate, second, Evaluate(reboundFirst, second)) &&
                    !MayYield(true, true, false, false, 1, true, false, reboundFirst.ReadyRow), "RECEIVE_SELF_TEST_YIELD_COMMAND_ADMITTED");
                ValidatePlainHistoryPrefix(entry, first, baseline: rebound); // The poll-to-poll prefix still holds.
                // A pinned offscreen command blocks the round and the yield alike; no later row takes its place.
                var hidden = History(true, noticeRows, "pwrsi", false);
                Need(Evaluate(BindReadyBoundary(preReady, hidden), hidden, null, out blocked) == null && blocked &&
                    !MayYield(true, true, false, false, 1, false, blocked, 0), "RECEIVE_SELF_TEST_YIELD_BLOCKED");
            }
            // The re-entry never binds anything but the one exact Ready row of this marker.
            var noReady = History(false, 0);
            AppendTestHistoryText(noReady, notices[0]);
            Need(BindReadyBoundary(preReady, noReady) == null, "RECEIVE_SELF_TEST_YIELD_READY_REQUIRED");
            var twice = History(true, 1); AppendTestHistoryText(twice, readyText);
            Reject(() => BindReadyBoundary(preReady, twice));
            var recycled = History(true, 1); SetTestName(recycled.Nodes.Single(n => n.Node == 51), "", "recycled-row");
            Reject(() => BindReadyBoundary(preReady, recycled)); // Old rows keep identity and order across the yield.
            Reject(() => BindReadyBoundary(bound, History(true, 1))); // A bound baseline is never carried as pre-Ready.
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
            ReadOnlyCommands.ObserveName(paddedNode, "pwrsi"); // A name that disagrees with the identity hash cannot produce a canonical command.
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
            RunHistoryShiftSelfTest();
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

            // A pinned first command that is not yet admissible blocks the round; no later row may take its place.
            var hidden = History(); AppendTestHistoryText(hidden, readyText);
            AppendTestHistoryText(hidden, "pwrsi").Visible = false;
            var hiddenBound = BindReadyBoundary(before, hidden);
            bool blockedCommand;
            Need(hiddenBound != null && Evaluate(hiddenBound, hidden, null, out blockedCommand) == null && blockedCommand,
                "READY_BOUNDARY_COMMAND_NOT_VISIBLE");
            var hiddenLater = new ProbeSnapshot { Complete = hidden.Complete, Process = hidden.Process,
                RootNameFingerprint = hidden.RootNameFingerprint, LayoutRejection = hidden.LayoutRejection,
                Nodes = new List<ProbeNode>(hidden.Nodes) };
            AppendTestHistoryText(hiddenLater, "pwrsi");
            Need(Evaluate(hiddenBound, hiddenLater, null, out blockedCommand) == null && blockedCommand,
                "READY_BOUNDARY_BLOCKED_COMMAND_PINNED");

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

        // Field shape (Master 0.3.10, 2026-10-04): a 40-part pwrsi reply with ReadyRow 29 in a 43-row window. From part 13
        // on, each appended part evicted the oldest row and the recycled slots kept positional runtime ids, so the command
        // was no longer at/after slot 30. These cases cover the bounded content re-anchor and the refusals it must keep.
        private static void RunHistoryShiftSelfTest()
        {
            const string marker = "M234567";
            var readyText = SupervisedSendTest.ReadyText("D234567");
            var parts = new string[41];
            for (var i = 1; i < parts.Length; i++)
                parts[i] = i == 1 ? new string('a', 781) : ("PART " + i.ToString("D3") + " ").PadRight(1000, (char)('a' + i % 26));
            parts[3] = parts[2]; // KI-Messenger's 1,000-character Name cap can make separate parts identical.
            parts[10] = parts[9];
            List<string> Old() { return Enumerable.Range(0, 29).Select(i => "old message " + i).ToList(); }
            // 29 old rows, Ready (row 29), the command (row 30), then the busy notice and reply parts 1..replyParts.
            List<string> Rows(int replyParts, bool command = true, string ready = null, string commandText = "pwrsi")
            {
                var rows = Old();
                rows.Add(ready ?? readyText);
                if (command) rows.Add(commandText);
                if (replyParts >= 0) rows.Add(SupervisedSendTest.PowerSiBusyNotice);
                for (var i = 1; i <= replyParts; i++) rows.Add(parts[i]);
                return rows;
            }
            List<string> Swap(List<string> rows, int a, int b) { var copy = rows.ToList(); copy[a] = rows[b]; copy[b] = rows[a]; return copy; }
            List<string> Without(List<string> rows, int index) { var copy = rows.ToList(); copy.RemoveAt(index); return copy; }
            ProbeSnapshot Window(IList<string> rows, bool commandVisible = false)
            {
                var snapshot = CreateTestSnapshot();
                var texts = SelectHistory(snapshot).Texts;
                SetTestName(texts[0], rows[0]);
                SetTestName(texts[1], rows[1]);
                for (var i = 2; i < rows.Count; i++) AppendTestHistoryText(snapshot, rows[i]);
                foreach (var node in snapshot.Nodes.Where(n => n.PlainCommand != null)) node.Visible = commandVisible;
                return snapshot;
            }
            void RemoveRow(ProbeSnapshot snapshot, int index)
            {
                var selection = SelectHistory(snapshot);
                var row = HistoryRows(selection)[index];
                var root = RowRoot(selection, row[0]);
                snapshot.Nodes.RemoveAll(n => n == root || row.Contains(n));
            }
            // The window drops `evicted` top rows; `edit` runs next; then every slot takes the runtime ids of the same slot
            // in an unshifted window of the same size, as recycled list rows do. Names, hashes and lengths are unchanged.
            ProbeSnapshot Shifted(IList<string> rows, int evicted, Action<ProbeSnapshot> edit = null, bool commandVisible = false)
            {
                var snapshot = Window(rows, commandVisible);
                for (var i = 0; i < evicted; i++) RemoveRow(snapshot, 0);
                edit?.Invoke(snapshot);
                Rekey(snapshot, Window(Enumerable.Repeat("slot", HistoryRows(SelectHistory(snapshot)).Count).ToList()));
                return snapshot;
            }
            ProbeNode Slot(ProbeSnapshot snapshot, int row) { return HistoryRows(SelectHistory(snapshot))[row][0]; }
            bool Logged(AuditLog.LogField[] fields, params object[] expected)
            {
                for (var i = 0; i + 1 < expected.Length; i += 2)
                    if (!Equals(fields.Single(f => f.Key == (string)expected[i]).Value, expected[i + 1])) return false;
                return true;
            }
            ObservationProof Accept()
            {
                var preReady = CreateBaseline(Window(Old()), marker, true);
                var bound = BindReadyBoundary(preReady, Window(Rows(-1, command: false)));
                var previous = Window(Rows(-1), true);
                var final = Window(Rows(-1), true);
                var first = Evaluate(bound, previous);
                var second = Evaluate(bound, final);
                Need(bound != null && bound.ReadyRow == 29 && first != null && SameCandidate(previous, first, final, second),
                    "RECEIVE_SELF_TEST_SHIFT_ACCEPTED");
                return new ObservationProof(new object(), new IntPtr(final.Process.WindowHandle), new NativeMethods.WindowRectangle(),
                    bound, previous, first, final, second);
            }
            // The ReceiveProbe calls one reply part makes: Observe(singleRefresh), then RoundTripTest's
            // CompleteRefreshedProof/SelectRefreshedProof and AuthorizeHandoffCore with the sender's fresh snapshot.
            ProbeNode Handoff(ObservationProof original, ProbeSnapshot first, ProbeSnapshot fresh)
            {
                var baseline = original.Baseline;
                var single = ReobserveCandidate(original, first);
                ValidatePlainHistoryPrefix(original.Final, first, null, baseline, acceptedRequest: true);
                Need(ReferenceEquals(ReobserveCandidate(original, first), single), "RECEIVE_SELF_TEST_SHIFT_SINGLE");
                var final = ReobserveCandidate(original, fresh);
                var refreshed = new ObservationProof(original.Owner, original.Window, original.Bounds, baseline, first, single,
                    fresh, final);
                ValidatePlainHistoryPrefix(original.Final, first, baseline: baseline, acceptedRequest: true);
                ValidatePlainHistoryPrefix(first, fresh, baseline: baseline, acceptedRequest: true);
                Need(SameCandidate(original.Final, original.Candidate, first, single) &&
                    SameCandidate(original.Final, original.Candidate, fresh, final), "RECEIVE_SELF_TEST_SHIFT_REFRESHED");
                var rebound = CreateBaseline(baseline.Snapshot, marker, true, baseline.ReadyRow);
                Need(ReferenceEquals(ReobserveCandidate(refreshed, first), single) &&
                    ReferenceEquals(ReobserveCandidate(refreshed, fresh), final), "RECEIVE_SELF_TEST_SHIFT_HANDOFF_REPEAT");
                ValidatePlainHistoryPrefix(first, fresh, null, rebound, acceptedRequest: true);
                ValidatePlainHistoryPrefix(fresh, fresh, null, rebound, acceptedRequest: true);
                var current = ReobserveCandidate(refreshed, fresh);
                Need(SameCandidate(first, single, fresh, final) && SameCandidate(fresh, final, fresh, current),
                    "RECEIVE_SELF_TEST_SHIFT_HANDOFF");
                return final;
            }
            ObservationProof Primed()
            {
                var proof = Accept();
                Handoff(proof, Window(Rows(11)), Window(Rows(11))); // Part 12, unshifted: 43 rows, Ready 29, command 30.
                return proof;
            }
            void Lost(ObservationProof proof, ProbeSnapshot changed, string expected)
            {
                string reason;
                Need(Reobserve(proof, changed, null, out reason) == null && reason == expected,
                    "RECEIVE_SELF_TEST_SHIFT_REFUSED_" + expected);
                try { ReobserveCandidate(proof, changed); }
                catch (MonitorException ex) when (ex.ReasonCode == "RECEIVE_ACCEPTED_CANDIDATE_CHANGED") { return; }
                throw new InvalidOperationException("Unsafe history shift was re-anchored.");
            }

            // No shift: long, capped and identical parts alone never disturb the accepted command (parts 1..13).
            var steady = Accept();
            for (var part = 1; part <= 13; part++)
            {
                var first = Window(Rows(part - 1));
                var fresh = Window(Rows(part - 1));
                Need(Handoff(steady, first, fresh) == Slot(fresh, 30) && Recorded(fresh).Shift == 0 &&
                    Recorded(fresh).RowsAfterReady == part + 1, "RECEIVE_SELF_TEST_SHIFT_NONE");
            }

            // The field sequence: part 12 unshifted, then from part 13 one eviction per part (43 rows each, Ready 28..1).
            var field = Accept();
            Handoff(field, Window(Rows(11)), Window(Rows(11)));
            ProbeSnapshot lastShifted = null;
            for (var part = 13; part <= 40; part++)
            {
                var evicted = part - 12;
                var first = Shifted(Rows(part - 1), evicted);
                var fresh = Shifted(Rows(part - 1), evicted);
                Need(Handoff(field, first, fresh) == Slot(fresh, 30 - evicted) && HistoryRows(SelectHistory(fresh)).Count == 43 &&
                    Logged(ShiftedFields(Recorded(first)), "shift", 1, "total_shift", evicted, "ready_row_before", 30 - evicted,
                        "ready_row_after", 29 - evicted, "rows_after_ready", part + 1, "appended_rows", 1) &&
                    Logged(ShiftedFields(Recorded(fresh)), "shift", 0, "total_shift", evicted, "ready_row_after", 29 - evicted,
                        "appended_rows", 0), "RECEIVE_SELF_TEST_SHIFT_FIELD_SEQUENCE");
                lastShifted = fresh;
            }
            // A re-anchored snapshot is never fresh admission evidence; the Ready boundary stays strict there.
            try { Evaluate(field.Baseline, lastShifted); throw new InvalidOperationException("A shifted window was admitted."); }
            catch (MonitorException ex) when (ex.ReasonCode == "RECEIVE_READY_BOUNDARY_CHANGED") { }
            // Ready itself evicted: nothing anchors the request, and recycled ids alone are not trusted after a shift.
            Lost(field, Shifted(Rows(40), 30), "IDENTITY_CHANGED");

            // Bounded jumps of 3 and 8 rows with the one part just sent appended.
            foreach (var evicted in new[] { 3, MaxEvictedRows })
            {
                var proof = Primed();
                var first = Shifted(Rows(12), evicted);
                var fresh = Shifted(Rows(12), evicted);
                Need(Handoff(proof, first, fresh) == Slot(fresh, 30 - evicted) &&
                    Logged(ShiftedFields(Recorded(first)), "shift", evicted, "total_shift", evicted, "ready_row_before", 29,
                        "ready_row_after", 29 - evicted, "rows_after_ready", 14, "appended_rows", 1),
                    "RECEIVE_SELF_TEST_SHIFT_BOUNDED_JUMP");
            }

            // An ignored "pwrsi" typed during the reply lands in the accepted command's old slot after two evictions: its
            // recycled ids equal the accepted ones, yet only the re-anchored row is the accepted command.
            List<string> WithUser(int replyParts) { var rows = Rows(replyParts); rows.Insert(32, "pwrsi"); return rows; }
            var duplicate = Accept();
            Handoff(duplicate, Window(WithUser(11)), Window(WithUser(11)));
            var recycled = Shifted(WithUser(12), 2);
            var recycledFresh = Shifted(WithUser(12), 2);
            Need(Handoff(duplicate, recycled, recycledFresh) == Slot(recycledFresh, 28) &&
                Slot(recycled, 30).PlainCommand == "pwrsi" &&
                SameIdentity(duplicate.Final, duplicate.Candidate, recycled, Slot(recycled, 30)) &&
                !SameCandidate(duplicate.Final, duplicate.Candidate, recycled, Slot(recycled, 30)),
                "RECEIVE_SELF_TEST_SHIFT_RECYCLED_DUPLICATE");

            // Refusals: each stops the send with RECEIVE_ACCEPTED_CANDIDATE_CHANGED and a content-free reason.
            Lost(Primed(), Shifted(Rows(12), 1, s => RemoveRow(s, 29)), "COMMAND_MISSING");
            Lost(Primed(), Shifted(Rows(12), 1, s =>
            {
                var selection = SelectHistory(s);
                var rows = HistoryRows(selection);
                var command = rows[29][0];
                var commandRoot = RowRoot(selection, command);
                command.Parent = RowRoot(selection, rows[28][0]).Node;
                Rekey(command, "merged-command-text"); // A second Text in the Ready row keeps an id of its own.
                s.Nodes.Remove(commandRoot);
            }), "IDENTITY_CHANGED"); // Command Text merged under the Ready row root: Ready is no longer a whole row.
            Lost(Primed(), Shifted(Rows(12), MaxEvictedRows + 1), "SHIFT_OUT_OF_RANGE");
            var changedProof = Primed();
            var changedBody = Shifted(Rows(12, commandText: "pwrsi "), 1);
            Lost(changedProof, changedBody, "COMMAND_CHANGED");
            Need(Logged(LostFields(changedProof, changedBody, "COMMAND_CHANGED"), "reanchor_result", "COMMAND_CHANGED",
                "accepted_runtime_present", true, "accepted_runtime_row", 30, "accepted_name_equal", false, "exact_name_rows", 0,
                "ready_at_bound_row", false, "ready_row_now", 28, "rows_after_ready", 14, "shift_candidate", 1),
                "RECEIVE_SELF_TEST_SHIFT_LOST_FIELDS");
            Lost(Primed(), Shifted(Rows(12), 1, s => Slot(s, 29).Enabled = false), "COMMAND_DISABLED");
            Lost(Primed(), Shifted(Swap(Rows(12), 31 + 5, 31 + 6), 1), "ROWS_CHANGED");
            Lost(Primed(), Shifted(Without(Rows(12), 31 + 7), 1), "ROWS_CHANGED");
            Lost(Primed(), Shifted(Rows(12, ready: SupervisedSendTest.ReadyText("D345678")), 1), "IDENTITY_CHANGED");
            Lost(Primed(), Shifted(Rows(13), 1), "ROWS_APPENDED_EXCESS");
        }

        // Test-only: give each history row (root and Texts) the runtime id found at the same index of an unshifted
        // reference window, as recycled list slots do. Name hash and length stay the same.
        private static void Rekey(ProbeSnapshot snapshot, ProbeSnapshot reference)
        {
            var selection = SelectHistory(snapshot);
            var rows = HistoryRows(selection);
            var expected = SelectHistory(reference);
            var slots = HistoryRows(expected);
            Need(rows.Count == slots.Count, "RECEIVE_SELF_TEST_REKEY_SHAPE");
            for (var i = 0; i < rows.Count; i++)
            {
                Rekey(RowRoot(selection, rows[i][0]), RowRoot(expected, slots[i][0]).Identity.RuntimeId);
                for (var j = 0; j < Math.Min(rows[i].Length, slots[i].Length); j++) Rekey(rows[i][j], slots[i][j].Identity.RuntimeId);
            }
        }

        private static void Rekey(ProbeNode node, string runtime)
        {
            var id = node.Identity;
            node.Identity = new ElementIdentity(runtime, id.ProcessId, id.AutomationId, id.ControlType, id.ClassName,
                id.FrameworkId, id.Patterns, id.NameLength, id.NameHash, id.SafeName);
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
