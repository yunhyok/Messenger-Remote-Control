using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RemoteMonitorLink;

namespace RemoteMonitorMaster
{
    // One watched PowerSI identity. Pid + StartUtcTicks is exact; a restarted process is a different target.
    internal sealed class WatchdogTarget
    {
        internal readonly int Pid;
        internal readonly long StartUtcTicks;
        internal readonly string ProcessName;

        internal DateTime ArmedUtc { get; set; }
        internal DateTime? LastCheckUtc { get; set; } // Last check attempt, successful or failed.
        internal string LastState { get; set; } // Last WatchdogTargetOutcome.Outcome, null before the first judged check.
        internal int ConsecutiveUnjudged { get; set; }

        internal WatchdogTarget(int pid, long startUtcTicks, string processName)
        {
            Pid = pid;
            StartUtcTicks = startUtcTicks;
            ProcessName = processName ?? string.Empty;
        }

        internal bool HasExactIdentity
        {
            get { return Pid > 0 && StartUtcTicks > 0 && StartUtcTicks <= DateTime.MaxValue.Ticks; }
        }

        internal WatchdogTarget Clone()
        {
            return new WatchdogTarget(Pid, StartUtcTicks, ProcessName)
            {
                ArmedUtc = ArmedUtc, LastCheckUtc = LastCheckUtc, LastState = LastState,
                ConsecutiveUnjudged = ConsecutiveUnjudged
            };
        }

        // PowerSI processes with an exact start time from a Slave inventory; others cannot be watched.
        internal static WatchdogTarget[] CandidatesFrom(ProcessInventory inventory)
        {
            if (inventory == null || inventory.Items == null) return new WatchdogTarget[0];
            return inventory.Items
                .Where(item => item != null && item.Pid > 0 && item.StartUtcTicks.HasValue && item.StartUtcTicks.Value > 0 &&
                    ProcessInventory.IsPowerSiName(item.Name))
                .Select(item => new WatchdogTarget(item.Pid, item.StartUtcTicks.Value, item.FullName ?? item.Name))
                .OrderBy(item => item.Pid).ToArray();
        }
    }

    // Simple per-target DTO built by the caller from a Slave PowerSiReport.
    internal sealed class WatchdogReport
    {
        internal int Pid;
        internal long StartUtcTicks; // 0 when the Slave could not read the start time.
        internal string ProcessName;
        internal string State;
        internal string Source;
        internal string OutputText;

        internal static WatchdogReport[] FromPowerSiReport(PowerSiReport report)
        {
            if (report == null || report.Targets == null) return new WatchdogReport[0];
            return report.Targets.Where(target => target != null).Select(target => new WatchdogReport
            {
                Pid = target.Pid, StartUtcTicks = target.StartUtcTicks ?? 0, ProcessName = target.ProcessName,
                State = target.State, Source = target.Source, OutputText = target.OutputText
            }).ToArray();
        }

        // Only a complete report proves that an absent PID is gone; partial/omitted reports keep the watch.
        internal static bool AbsentMeansMissing(PowerSiReport report)
        {
            return report != null && report.Targets != null && report.Code == "OK" && !report.Partial &&
                report.Omitted == 0 && report.Unreadable == 0;
        }
    }

    internal sealed class WatchdogArmResult
    {
        internal const string KindArmed = "ARMED";
        internal const string KindAlreadyWatched = "ALREADY_WATCHED";
        internal const string KindNoPowerSi = "NO_POWERSI";
        internal const string KindPidNotFound = "PID_NOT_FOUND";

        internal string Kind;
        internal WatchdogTarget[] Added = new WatchdogTarget[0];
        internal WatchdogTarget[] AlreadyWatched = new WatchdogTarget[0];
        internal int Total;
        internal int? RequestedPid;
    }

    internal sealed class WatchdogDisarmResult
    {
        internal const string KindClearedAll = "CLEARED_ALL";
        internal const string KindClearedOne = "CLEARED_ONE";
        internal const string KindNothingWatched = "NOTHING_WATCHED";
        internal const string KindPidNotWatched = "PID_NOT_WATCHED";

        internal string Kind;
        internal int Cleared, Remaining;
        internal int? RequestedPid;
        internal WatchdogTarget[] Removed = new WatchdogTarget[0];
    }

    internal sealed class WatchdogTargetOutcome
    {
        internal const string OutcomeFinished = "FINISHED";
        internal const string OutcomeRunning = "RUNNING";
        internal const string OutcomeFinishPending = "FINISH_PENDING";
        internal const string OutcomeNoMarkers = "NO_MARKERS";
        internal const string OutcomeUnjudgeable = "UNJUDGEABLE";
        internal const string OutcomeMissing = "MISSING";
        internal const string ReportStateAbsent = "ABSENT"; // Master-local label: no record for this PID.

        internal WatchdogTarget Target; // Copy after this check.
        internal string Outcome;
        internal long SamplingPoints;
        internal string Source;
        internal int OutputLength;
        internal int ConsecutiveUnjudged;
        internal string ReportState; // Slave record State (READ/PENDING/...) or ABSENT.
        internal string LastFrequencyLine; // Output text for the phone reply only; never log it.
    }

    internal sealed class WatchdogCheckResult
    {
        internal WatchdogTargetOutcome[] Outcomes = new WatchdogTargetOutcome[0];
        internal WatchdogTargetOutcome[] Finished = new WatchdogTargetOutcome[0];
        internal WatchdogTargetOutcome[] Missing = new WatchdogTargetOutcome[0];
        internal int Remaining;
        internal bool AllCleared;
        internal WatchdogTargetOutcome[] UnjudgedStreakReached = new WatchdogTargetOutcome[0];
    }

    // ponytail: in-memory watch list owned by StatusSession; it never logs, sends or touches Output history.
    internal sealed class WatchdogState
    {
        internal static readonly int DefaultIntervalMinutes = 30;
        internal const int UnjudgedWarningThreshold = 3;
        internal const int FailureWarningThreshold = 3;
        private const int MaxReasonLength = 64;

        private readonly object sync = new object();
        private readonly SortedDictionary<int, WatchdogTarget> entries = new SortedDictionary<int, WatchdogTarget>();
        private TimeSpan interval;
        private int failureStreak;
        private int unjudgedWarningsSent;
        private string lastFailureReason;
        private string lastClearReason;

        internal WatchdogState(TimeSpan interval)
        {
            if (!IsValidInterval(interval)) throw new ArgumentOutOfRangeException("interval", "Watchdog interval must be 30 or 60 minutes.");
            this.interval = interval;
        }

        internal static bool IsValidIntervalMinutes(int minutes)
        {
            return minutes == 30 || minutes == 60;
        }

        internal static bool IsValidInterval(TimeSpan interval)
        {
            return interval == TimeSpan.FromMinutes(30) || interval == TimeSpan.FromMinutes(60);
        }

        internal TimeSpan Interval
        {
            get { lock (sync) return interval; }
        }

        internal bool TrySetIntervalMinutes(int minutes)
        {
            if (!IsValidIntervalMinutes(minutes)) return false;
            lock (sync) interval = TimeSpan.FromMinutes(minutes);
            return true;
        }

        internal int Count
        {
            get { lock (sync) return entries.Count; }
        }

        internal bool IsArmed
        {
            get { lock (sync) return entries.Count > 0; }
        }

        internal int FailureStreak
        {
            get { lock (sync) return failureStreak; }
        }

        internal string LastFailureReason
        {
            get { lock (sync) return lastFailureReason; }
        }

        internal string LastClearReason
        {
            get { lock (sync) return lastClearReason; }
        }

        internal int UnjudgedWarningsSent
        {
            get { lock (sync) return unjudgedWarningsSent; }
            set { lock (sync) unjudgedWarningsSent = value; }
        }

        internal WatchdogTarget[] Snapshot()
        {
            lock (sync) return entries.Values.Select(entry => entry.Clone()).ToArray();
        }

        internal DateTime? NextCheckUtc
        {
            get { lock (sync) return NextCheckLocked(); }
        }

        internal bool IsCheckDue(DateTime nowUtc)
        {
            DateTime? next = NextCheckUtc;
            return next.HasValue && Utc(nowUtc) >= next.Value;
        }

        internal void ReadSchedule(out int count, out DateTime? nextCheckUtc, out int intervalMinutes, out int streak)
        {
            lock (sync)
            {
                count = entries.Count;
                nextCheckUtc = NextCheckLocked();
                intervalMinutes = (int)interval.TotalMinutes;
                streak = failureStreak;
            }
        }

        internal WatchdogArmResult Arm(IEnumerable<WatchdogTarget> candidates, int? pid, DateTime nowUtc)
        {
            DateTime now = Utc(nowUtc);
            var valid = new SortedDictionary<int, WatchdogTarget>();
            if (candidates != null)
                foreach (WatchdogTarget candidate in candidates)
                    if (candidate != null && candidate.HasExactIdentity && !valid.ContainsKey(candidate.Pid))
                        valid.Add(candidate.Pid, candidate);

            var added = new List<WatchdogTarget>();
            var already = new List<WatchdogTarget>();
            lock (sync)
            {
                bool wasArmed = entries.Count > 0;
                string kind;
                if (valid.Count == 0) kind = WatchdogArmResult.KindNoPowerSi;
                else if (pid.HasValue && !valid.ContainsKey(pid.Value)) kind = WatchdogArmResult.KindPidNotFound;
                else
                {
                    foreach (WatchdogTarget candidate in valid.Values)
                    {
                        if (pid.HasValue && candidate.Pid != pid.Value) continue;
                        WatchdogTarget existing;
                        if (entries.TryGetValue(candidate.Pid, out existing) && existing.StartUtcTicks == candidate.StartUtcTicks)
                        {
                            already.Add(existing.Clone());
                            continue;
                        }
                        // Same PID with another start time means the old process is gone; watch the current one.
                        var entry = new WatchdogTarget(candidate.Pid, candidate.StartUtcTicks, candidate.ProcessName) { ArmedUtc = now };
                        entries[candidate.Pid] = entry;
                        added.Add(entry.Clone());
                    }
                    kind = added.Count > 0 ? WatchdogArmResult.KindArmed : WatchdogArmResult.KindAlreadyWatched;
                }
                if (!wasArmed && added.Count > 0)
                {
                    failureStreak = 0;
                    unjudgedWarningsSent = 0;
                    lastFailureReason = null;
                    lastClearReason = null;
                }
                return new WatchdogArmResult
                {
                    Kind = kind, Added = added.ToArray(), AlreadyWatched = already.ToArray(), Total = entries.Count,
                    RequestedPid = pid
                };
            }
        }

        internal WatchdogDisarmResult Disarm(int? pid)
        {
            lock (sync)
            {
                if (entries.Count == 0)
                    return new WatchdogDisarmResult { Kind = WatchdogDisarmResult.KindNothingWatched, RequestedPid = pid };
                if (!pid.HasValue)
                {
                    WatchdogTarget[] removed = entries.Values.Select(entry => entry.Clone()).ToArray();
                    ResetLocked("WATCHDOG_OFF");
                    return new WatchdogDisarmResult
                    {
                        Kind = WatchdogDisarmResult.KindClearedAll, Cleared = removed.Length, Remaining = 0, Removed = removed
                    };
                }
                WatchdogTarget target;
                if (!entries.TryGetValue(pid.Value, out target))
                    return new WatchdogDisarmResult
                    {
                        Kind = WatchdogDisarmResult.KindPidNotWatched, Remaining = entries.Count, RequestedPid = pid
                    };
                entries.Remove(pid.Value);
                if (entries.Count == 0) ResetLocked("WATCHDOG_OFF");
                return new WatchdogDisarmResult
                {
                    Kind = WatchdogDisarmResult.KindClearedOne, Cleared = 1, Remaining = entries.Count, RequestedPid = pid,
                    Removed = new[] { target.Clone() }
                };
            }
        }

        internal WatchdogCheckResult ApplyCheck(IEnumerable<WatchdogReport> reports, DateTime nowUtc)
        {
            return ApplyCheck(reports, nowUtc, true);
        }

        // absentMeansMissing=false keeps entries whose PID has no record (partial Slave report) as UNJUDGEABLE.
        internal WatchdogCheckResult ApplyCheck(IEnumerable<WatchdogReport> reports, DateTime nowUtc, bool absentMeansMissing)
        {
            DateTime now = Utc(nowUtc);
            var byPid = new Dictionary<int, WatchdogReport>();
            if (reports != null)
                foreach (WatchdogReport report in reports)
                    if (report != null && report.Pid > 0 && !byPid.ContainsKey(report.Pid)) byPid.Add(report.Pid, report);

            var outcomes = new List<WatchdogTargetOutcome>();
            var finished = new List<WatchdogTargetOutcome>();
            var missing = new List<WatchdogTargetOutcome>();
            var reached = new List<WatchdogTargetOutcome>();
            lock (sync)
            {
                foreach (WatchdogTarget entry in entries.Values.ToArray())
                {
                    WatchdogReport report;
                    byPid.TryGetValue(entry.Pid, out report);
                    var outcome = new WatchdogTargetOutcome
                    {
                        Source = report == null ? "NONE" : report.Source,
                        OutputLength = report == null || report.OutputText == null ? 0 : report.OutputText.Length,
                        ReportState = report == null ? WatchdogTargetOutcome.ReportStateAbsent : report.State
                    };
                    bool unjudged = false;
                    if (report == null ? absentMeansMissing : report.StartUtcTicks > 0 && report.StartUtcTicks != entry.StartUtcTicks)
                        outcome.Outcome = WatchdogTargetOutcome.OutcomeMissing;
                    else if (report == null || report.StartUtcTicks <= 0 || report.State != "READ" ||
                        !PowerSiCompletion.IsJudgeableSource(report.Source))
                    {
                        outcome.Outcome = WatchdogTargetOutcome.OutcomeUnjudgeable;
                        unjudged = true;
                    }
                    else
                    {
                        PowerSiCompletionResult evaluation = PowerSiCompletion.Evaluate(report.OutputText);
                        outcome.LastFrequencyLine = evaluation.LastFrequencyLine;
                        switch (evaluation.State)
                        {
                            case PowerSiCompletionState.Finished:
                                outcome.Outcome = WatchdogTargetOutcome.OutcomeFinished;
                                outcome.SamplingPoints = evaluation.SamplingPoints;
                                break;
                            case PowerSiCompletionState.Running:
                                outcome.Outcome = WatchdogTargetOutcome.OutcomeRunning;
                                break;
                            case PowerSiCompletionState.FinishPending:
                                outcome.Outcome = WatchdogTargetOutcome.OutcomeFinishPending;
                                break;
                            default:
                                // No visible simulation marker is not evidence of progress.
                                outcome.Outcome = WatchdogTargetOutcome.OutcomeNoMarkers;
                                unjudged = true;
                                break;
                        }
                    }

                    if (unjudged)
                    {
                        if (entry.ConsecutiveUnjudged < int.MaxValue) entry.ConsecutiveUnjudged++;
                    }
                    else if (outcome.Outcome == WatchdogTargetOutcome.OutcomeRunning ||
                        outcome.Outcome == WatchdogTargetOutcome.OutcomeFinishPending)
                        entry.ConsecutiveUnjudged = 0;
                    entry.LastCheckUtc = now;
                    entry.LastState = outcome.Outcome;
                    outcome.ConsecutiveUnjudged = entry.ConsecutiveUnjudged;
                    outcome.Target = entry.Clone();
                    outcomes.Add(outcome);

                    if (outcome.Outcome == WatchdogTargetOutcome.OutcomeFinished) finished.Add(outcome);
                    else if (outcome.Outcome == WatchdogTargetOutcome.OutcomeMissing) missing.Add(outcome);
                    else if (unjudged && entry.ConsecutiveUnjudged == UnjudgedWarningThreshold) reached.Add(outcome);
                    if (outcome.Outcome == WatchdogTargetOutcome.OutcomeFinished ||
                        outcome.Outcome == WatchdogTargetOutcome.OutcomeMissing) entries.Remove(entry.Pid);
                }

                failureStreak = 0;
                lastFailureReason = null;
                bool allCleared = outcomes.Count > 0 && entries.Count == 0;
                if (allCleared) ResetLocked("ALL_CLEARED");
                return new WatchdogCheckResult
                {
                    Outcomes = outcomes.ToArray(), Finished = finished.ToArray(), Missing = missing.ToArray(),
                    Remaining = entries.Count, AllCleared = allCleared, UnjudgedStreakReached = reached.ToArray()
                };
            }
        }

        // A failed collection still counts as this interval's attempt, so the next try waits a full interval.
        internal void RecordCheckFailure(string reason, DateTime nowUtc)
        {
            DateTime now = Utc(nowUtc);
            lock (sync)
            {
                if (entries.Count == 0) return;
                if (failureStreak < int.MaxValue) failureStreak++;
                lastFailureReason = ReasonCode(reason);
                foreach (WatchdogTarget entry in entries.Values) entry.LastCheckUtc = now;
            }
        }

        internal string Summary(DateTime nowLocal)
        {
            int count, intervalMinutes, streak;
            DateTime? next;
            ReadSchedule(out count, out next, out intervalMinutes, out streak);
            if (count == 0 || !next.HasValue) return "감시 없음";
            string text = "감시 " + Number(count) + "개 · 다음 확인 " + WatchdogText.NextCheckClock(next.Value, nowLocal);
            return streak > 0 ? text + " · 조회 실패 " + Number(streak) + "회 연속" : text;
        }

        internal void ClearAll(string reason)
        {
            lock (sync) ResetLocked(ReasonCode(reason) ?? "CLEARED");
        }

        private void ResetLocked(string reason)
        {
            entries.Clear();
            failureStreak = 0;
            unjudgedWarningsSent = 0;
            lastFailureReason = null;
            lastClearReason = reason;
        }

        private DateTime? NextCheckLocked()
        {
            DateTime? earliest = null;
            foreach (WatchdogTarget entry in entries.Values)
            {
                DateTime basis = entry.LastCheckUtc.HasValue && entry.LastCheckUtc.Value > entry.ArmedUtc
                    ? entry.LastCheckUtc.Value : entry.ArmedUtc;
                DateTime due = basis.Ticks > DateTime.MaxValue.Ticks - interval.Ticks
                    ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
                    : DateTime.SpecifyKind(basis + interval, DateTimeKind.Utc);
                if (!earliest.HasValue || due < earliest.Value) earliest = due;
            }
            return earliest;
        }

        private static DateTime Utc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        // Reason codes are short ASCII identifiers; anything else is reduced so no text is ever retained.
        private static string ReasonCode(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return null;
            var code = new StringBuilder(Math.Min(reason.Length, MaxReasonLength));
            foreach (char value in reason)
            {
                if (code.Length >= MaxReasonLength) break;
                char upper = value >= 'a' && value <= 'z' ? (char)(value - 'a' + 'A') : value;
                code.Append((upper >= 'A' && upper <= 'Z') || (upper >= '0' && upper <= '9') ? upper : '_');
            }
            return code.ToString();
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        internal static void RunSelfTest()
        {
            DateTime t0 = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
            long s1 = t0.AddHours(-3).Ticks, s2 = t0.AddHours(-2).Ticks, s3 = t0.AddHours(-1).Ticks;
            WatchdogTarget[] candidates =
            {
                new WatchdogTarget(303, s3, "pwrsi"), new WatchdogTarget(101, s1, "PowerSI"),
                new WatchdogTarget(202, s2, "PowerSI"), new WatchdogTarget(101, s2, "duplicate PID ignored")
            };
            const string finishedText = "AFS Current Frequency ( GHz ) = 1.515\r\nAFS Finished\r\nTotal Sampling Points = 118\r\n";
            const string runningText = "AFS Finished\r\nTotal Sampling Points = 118\r\nAFS Current Frequency ( MHz ) = 7.500\r\n";

            Need(IsValidIntervalMinutes(30) && IsValidIntervalMinutes(60) && !IsValidIntervalMinutes(0) &&
                !IsValidIntervalMinutes(29) && !IsValidIntervalMinutes(45) && !IsValidIntervalMinutes(61) &&
                !IsValidIntervalMinutes(-30) && DefaultIntervalMinutes == 30 && IsValidInterval(TimeSpan.FromHours(1)) &&
                !IsValidInterval(TimeSpan.FromMinutes(30.5)), "INTERVAL_TABLE");
            bool rejected = false;
            try { new WatchdogState(TimeSpan.FromMinutes(45)); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Need(rejected, "INVALID_INTERVAL_ACCEPTED");

            var state = new WatchdogState(TimeSpan.FromMinutes(30));
            Need(!state.IsArmed && state.Count == 0 && state.NextCheckUtc == null && !state.IsCheckDue(t0.AddDays(1)) &&
                state.Summary(t0.ToLocalTime()) == "감시 없음" && state.Snapshot().Length == 0, "INITIAL");
            WatchdogArmResult arm = state.Arm(new WatchdogTarget[0], null, t0);
            Need(arm.Kind == WatchdogArmResult.KindNoPowerSi && arm.Total == 0 && !state.IsArmed, "EMPTY_NO_POWERSI");
            arm = state.Arm(null, 101, t0);
            Need(arm.Kind == WatchdogArmResult.KindNoPowerSi && arm.RequestedPid == 101, "NULL_NO_POWERSI");
            arm = state.Arm(new[] { new WatchdogTarget(0, s1, "PowerSI"), new WatchdogTarget(404, 0, "PowerSI"), null }, null, t0);
            Need(arm.Kind == WatchdogArmResult.KindNoPowerSi && state.Count == 0, "INEXACT_CANDIDATES_IGNORED");
            arm = state.Arm(candidates, 999, t0);
            Need(arm.Kind == WatchdogArmResult.KindPidNotFound && arm.Added.Length == 0 && arm.Total == 0, "PID_NOT_FOUND");

            arm = state.Arm(candidates, 202, t0);
            Need(arm.Kind == WatchdogArmResult.KindArmed && arm.Added.Length == 1 && arm.Added[0].Pid == 202 &&
                arm.Added[0].StartUtcTicks == s2 && arm.Added[0].ArmedUtc == t0 && arm.Total == 1 && state.IsArmed, "ARM_ONE");
            arm = state.Arm(candidates, 202, t0.AddMinutes(5));
            Need(arm.Kind == WatchdogArmResult.KindAlreadyWatched && arm.AlreadyWatched.Length == 1 &&
                arm.AlreadyWatched[0].ArmedUtc == t0 && arm.Total == 1, "ARM_ONE_AGAIN");
            arm = state.Arm(candidates, null, t0.AddMinutes(10));
            Need(arm.Kind == WatchdogArmResult.KindArmed && arm.Added.Select(item => item.Pid).SequenceEqual(new[] { 101, 303 }) &&
                arm.Added[0].ProcessName == "PowerSI" && arm.AlreadyWatched.Length == 1 && arm.AlreadyWatched[0].Pid == 202 &&
                arm.Total == 3, "ARM_ALL_MERGES");
            arm = state.Arm(candidates, null, t0.AddMinutes(11));
            Need(arm.Kind == WatchdogArmResult.KindAlreadyWatched && arm.Added.Length == 0 && arm.AlreadyWatched.Length == 3,
                "ARM_ALL_ALREADY_WATCHED");

            WatchdogTarget[] snapshot = state.Snapshot();
            Need(snapshot.Select(item => item.Pid).SequenceEqual(new[] { 101, 202, 303 }), "SNAPSHOT_ORDER");
            snapshot[0].LastState = "TAMPERED";
            snapshot[0].ConsecutiveUnjudged = 99;
            Need(state.Snapshot()[0].LastState == null && state.Snapshot()[0].ConsecutiveUnjudged == 0, "SNAPSHOT_IS_COPY");
            Need(state.NextCheckUtc == t0.AddMinutes(30) && !state.IsCheckDue(t0.AddMinutes(30).AddTicks(-1)) &&
                state.IsCheckDue(t0.AddMinutes(30)) && state.IsCheckDue(t0.AddMinutes(31)), "FIRST_DUE");
            DateTime nextLocal = t0.AddMinutes(30).ToLocalTime();
            Need(state.Summary(t0.ToLocalTime()) == "감시 3개 · 다음 확인 " +
                nextLocal.ToString("HH:mm", CultureInfo.InvariantCulture), "SUMMARY");
            Need(state.Summary(nextLocal) == "감시 3개 · 다음 확인 곧", "SUMMARY_DUE");

            WatchdogCheckResult check = state.ApplyCheck(new[]
            {
                Report(101, s1, "READ", "BUFFER", finishedText),
                Report(202, s2, "PENDING", "NONE", string.Empty),
                Report(303, s3, "READ", "OCR", finishedText),
                Report(505, s3, "READ", "BUFFER", finishedText)
            }, t0.AddMinutes(30));
            Need(check.Outcomes.Length == 3 && check.Finished.Length == 1 && check.Finished[0].Target.Pid == 101 &&
                check.Finished[0].SamplingPoints == 118 && check.Finished[0].Source == "BUFFER" &&
                check.Finished[0].OutputLength == finishedText.Length &&
                check.Finished[0].LastFrequencyLine == "AFS Current Frequency ( GHz ) = 1.515" &&
                check.Missing.Length == 0 && check.Remaining == 2 && !check.AllCleared && check.UnjudgedStreakReached.Length == 0,
                "CHECK_1");
            Need(check.Outcomes[1].Outcome == WatchdogTargetOutcome.OutcomeUnjudgeable && check.Outcomes[1].ReportState == "PENDING" &&
                check.Outcomes[1].ConsecutiveUnjudged == 1 && check.Outcomes[2].Outcome == WatchdogTargetOutcome.OutcomeUnjudgeable &&
                check.Outcomes[2].Source == "OCR" && check.Outcomes[2].SamplingPoints == 0, "PENDING_AND_OCR_NOT_JUDGED");
            Need(state.Count == 2 && state.NextCheckUtc == t0.AddMinutes(60) && state.Snapshot()[0].LastCheckUtc == t0.AddMinutes(30) &&
                state.Snapshot()[0].LastState == WatchdogTargetOutcome.OutcomeUnjudgeable, "AFTER_CHECK_1");

            check = state.ApplyCheck(new[]
            {
                Report(202, s2, "PENDING", "NONE", string.Empty), Report(303, s3, "READ", "AUTO_COPY", runningText)
            }, t0.AddMinutes(60));
            Need(check.Outcomes[0].ConsecutiveUnjudged == 2 && check.Outcomes[1].Outcome == WatchdogTargetOutcome.OutcomeRunning &&
                check.Outcomes[1].ConsecutiveUnjudged == 0 && check.Finished.Length == 0 && check.Remaining == 2, "CHECK_2");
            check = state.ApplyCheck(new[]
            {
                Report(202, s2, "PENDING", "NONE", string.Empty), Report(303, s3, "READ", "AUTO_COPY", "no simulation text")
            }, t0.AddMinutes(90));
            Need(check.Outcomes[0].ConsecutiveUnjudged == 3 && check.Outcomes[1].Outcome == WatchdogTargetOutcome.OutcomeNoMarkers &&
                check.Outcomes[1].ConsecutiveUnjudged == 1 && check.UnjudgedStreakReached.Length == 1 &&
                check.UnjudgedStreakReached[0].Target.Pid == 202, "STREAK_REACHED_ONCE");
            state.UnjudgedWarningsSent = 1;
            check = state.ApplyCheck(new[]
            {
                Report(202, s2, "UNAVAILABLE", "NONE", string.Empty),
                Report(303, s3, "READ", "BUFFER", "AFS Current Frequency ( GHz ) = 1\nAFS Finished\n")
            }, t0.AddMinutes(120));
            Need(check.Outcomes[0].ConsecutiveUnjudged == 4 && check.UnjudgedStreakReached.Length == 0 &&
                check.Outcomes[1].Outcome == WatchdogTargetOutcome.OutcomeFinishPending && check.Outcomes[1].ConsecutiveUnjudged == 0 &&
                state.UnjudgedWarningsSent == 1, "STREAK_NOT_REPEATED");

            state.RecordCheckFailure("SLAVE_BUSY", t0.AddMinutes(150));
            state.RecordCheckFailure("slave unreachable: 10.0.0.1", t0.AddMinutes(180));
            Need(state.FailureStreak == 2 && state.LastFailureReason == "SLAVE_UNREACHABLE__10_0_0_1" && state.Count == 2 &&
                state.NextCheckUtc == t0.AddMinutes(210) && !state.IsCheckDue(t0.AddMinutes(209)), "FAILURE_WAITS_INTERVAL");
            state.RecordCheckFailure(null, t0.AddMinutes(210));
            Need(state.FailureStreak == 3 && state.LastFailureReason == null, "FAILURE_STREAK_THREE");
            Need(state.Summary(t0.ToLocalTime()).EndsWith(" · 조회 실패 3회 연속", StringComparison.Ordinal), "SUMMARY_FAILURE");

            check = state.ApplyCheck(new[] { Report(303, s3 + 1, "READ", "BUFFER", runningText) }, t0.AddMinutes(240), false);
            Need(check.Outcomes[0].Outcome == WatchdogTargetOutcome.OutcomeUnjudgeable &&
                check.Outcomes[0].ReportState == WatchdogTargetOutcome.ReportStateAbsent &&
                check.Outcomes[1].Outcome == WatchdogTargetOutcome.OutcomeMissing && check.Missing.Length == 1 &&
                check.Missing[0].Target.Pid == 303 && check.Remaining == 1 && state.FailureStreak == 0, "PARTIAL_AND_RESTARTED");
            check = state.ApplyCheck(new[] { Report(202, 0, "UNAVAILABLE", "NONE", string.Empty) }, t0.AddMinutes(270));
            Need(check.Outcomes.Length == 1 && check.Outcomes[0].Outcome == WatchdogTargetOutcome.OutcomeUnjudgeable &&
                state.Count == 1, "UNKNOWN_START_NOT_MISSING");
            check = state.ApplyCheck(new WatchdogReport[0], t0.AddMinutes(300));
            Need(check.Missing.Length == 1 && check.Missing[0].Target.Pid == 202 && check.AllCleared && check.Remaining == 0 &&
                !state.IsArmed && state.NextCheckUtc == null && state.UnjudgedWarningsSent == 0 &&
                state.LastClearReason == "ALL_CLEARED", "ALL_CLEARED");
            check = state.ApplyCheck(new[] { Report(101, s1, "READ", "BUFFER", finishedText) }, t0.AddMinutes(330));
            Need(check.Outcomes.Length == 0 && !check.AllCleared && check.Remaining == 0, "CHECK_WHEN_OFF");
            state.RecordCheckFailure("SLAVE_BUSY", t0.AddMinutes(331));
            Need(state.FailureStreak == 0, "FAILURE_WHEN_OFF");

            WatchdogDisarmResult disarm = state.Disarm(null);
            Need(disarm.Kind == WatchdogDisarmResult.KindNothingWatched && disarm.Cleared == 0 && disarm.Remaining == 0, "OFF_EMPTY");
            disarm = state.Disarm(101);
            Need(disarm.Kind == WatchdogDisarmResult.KindNothingWatched && disarm.RequestedPid == 101, "OFF_PID_EMPTY");
            state.Arm(candidates, null, t0);
            disarm = state.Disarm(999);
            Need(disarm.Kind == WatchdogDisarmResult.KindPidNotWatched && disarm.Remaining == 3 && disarm.Cleared == 0, "OFF_PID_NOT_WATCHED");
            disarm = state.Disarm(202);
            Need(disarm.Kind == WatchdogDisarmResult.KindClearedOne && disarm.Cleared == 1 && disarm.Remaining == 2 &&
                disarm.Removed.Length == 1 && disarm.Removed[0].Pid == 202 && state.Count == 2, "OFF_ONE");
            disarm = state.Disarm(null);
            Need(disarm.Kind == WatchdogDisarmResult.KindClearedAll && disarm.Cleared == 2 && disarm.Remaining == 0 &&
                !state.IsArmed && state.LastClearReason == "WATCHDOG_OFF", "OFF_ALL");
            state.Arm(candidates, 101, t0);
            disarm = state.Disarm(101);
            Need(disarm.Kind == WatchdogDisarmResult.KindClearedOne && disarm.Remaining == 0 && !state.IsArmed, "OFF_LAST_ONE");

            state.Arm(candidates, null, t0);
            state.RecordCheckFailure("SLAVE_BUSY", t0.AddMinutes(30));
            state.UnjudgedWarningsSent = 2;
            state.ClearAll("SESSION_END");
            Need(!state.IsArmed && state.FailureStreak == 0 && state.UnjudgedWarningsSent == 0 && state.NextCheckUtc == null &&
                state.LastClearReason == "SESSION_END", "CLEAR_ALL");

            arm = state.Arm(candidates, 101, t0);
            arm = state.Arm(new[] { new WatchdogTarget(101, s1 + 7, "PowerSI") }, 101, t0.AddMinutes(20));
            Need(arm.Kind == WatchdogArmResult.KindArmed && state.Count == 1 && state.Snapshot()[0].StartUtcTicks == s1 + 7 &&
                state.Snapshot()[0].ArmedUtc == t0.AddMinutes(20), "RESTARTED_PID_REPLACED");
            state.ClearAll(null);

            var hourly = new WatchdogState(TimeSpan.FromMinutes(60));
            hourly.Arm(candidates, null, t0);
            Need(hourly.Interval == TimeSpan.FromMinutes(60) && hourly.NextCheckUtc == t0.AddMinutes(60), "HOURLY");
            Need(!hourly.TrySetIntervalMinutes(45) && hourly.Interval == TimeSpan.FromMinutes(60) &&
                hourly.TrySetIntervalMinutes(30) && hourly.NextCheckUtc == t0.AddMinutes(30), "SET_INTERVAL");
            hourly.Arm(new[] { new WatchdogTarget(606, s1, "PowerSI") }, null, t0.AddMinutes(20));
            Need(hourly.NextCheckUtc == t0.AddMinutes(30), "EARLIEST_ENTRY_GOVERNS");
            hourly.ApplyCheck(WatchdogReportsFor(hourly, runningText), t0.AddMinutes(31));
            Need(hourly.NextCheckUtc == t0.AddMinutes(61), "CHECK_ALIGNS_ENTRIES");
            var localKind = new WatchdogState(TimeSpan.FromMinutes(30));
            localKind.Arm(candidates, 101, t0.ToLocalTime());
            Need(localKind.NextCheckUtc == t0.AddMinutes(30) && localKind.NextCheckUtc.Value.Kind == DateTimeKind.Utc, "LOCAL_KIND_NORMALIZED");

            var inventory = new ProcessInventory
            {
                Items = new[]
                {
                    new ProcessState { Pid = 9, Name = "powersi", FullName = "PowerSI", StartUtcTicks = s1 },
                    new ProcessState { Pid = 7, Name = "pwrsi", FullName = null, StartUtcTicks = s2 },
                    new ProcessState { Pid = 8, Name = "powersi", FullName = "PowerSI", StartUtcTicks = null },
                    new ProcessState { Pid = 6, Name = "notepad", FullName = "notepad", StartUtcTicks = s3 }
                }
            };
            WatchdogTarget[] fromInventory = WatchdogTarget.CandidatesFrom(inventory);
            Need(fromInventory.Length == 2 && fromInventory[0].Pid == 7 && fromInventory[0].ProcessName == "pwrsi" &&
                fromInventory[1].Pid == 9 && fromInventory[1].StartUtcTicks == s1 && WatchdogTarget.CandidatesFrom(null).Length == 0,
                "CANDIDATES_FROM_INVENTORY");

            var powerSi = new PowerSiReport
            {
                CapturedUtc = t0, Targets = new[]
                {
                    new PowerSiTargetReport { Pid = 9, StartUtcTicks = s1, ProcessName = "PowerSI", State = "READ", Source = "AUTO_COPY",
                        OutputText = finishedText },
                    new PowerSiTargetReport { Pid = 7, StartUtcTicks = null, ProcessName = "pwrsi", State = "UNAVAILABLE", Source = "NONE",
                        OutputText = string.Empty }
                }
            };
            WatchdogReport[] converted = WatchdogReport.FromPowerSiReport(powerSi);
            Need(converted.Length == 2 && converted[0].Pid == 9 && converted[0].StartUtcTicks == s1 &&
                converted[0].Source == "AUTO_COPY" && converted[0].OutputText == finishedText && converted[1].StartUtcTicks == 0 &&
                WatchdogReport.FromPowerSiReport(null).Length == 0, "FROM_POWERSI_REPORT");
            Need(WatchdogReport.AbsentMeansMissing(powerSi) && !WatchdogReport.AbsentMeansMissing(null), "ABSENT_MEANS_MISSING");
            powerSi.Omitted = 1;
            Need(!WatchdogReport.AbsentMeansMissing(powerSi), "OMITTED_KEEPS_WATCH");
            powerSi.Omitted = 0;
            powerSi.Partial = true;
            powerSi.Code = "COLLECTION_TIMEOUT";
            Need(!WatchdogReport.AbsentMeansMissing(powerSi), "PARTIAL_KEEPS_WATCH");
        }

        private static WatchdogReport Report(int pid, long start, string state, string source, string output)
        {
            return new WatchdogReport
            {
                Pid = pid, StartUtcTicks = start, ProcessName = "PowerSI", State = state, Source = source, OutputText = output
            };
        }

        private static WatchdogReport[] WatchdogReportsFor(WatchdogState state, string output)
        {
            return state.Snapshot().Select(item => Report(item.Pid, item.StartUtcTicks, "READ", "BUFFER", output)).ToArray();
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Watchdog state self-test failed: " + reason + ".");
        }
    }

    // All phone-facing watchdog text. Every reply/notice part stays within PcStatusReport.MaxPhoneLength.
    internal static class WatchdogText
    {
        internal const string CommandOn = "watchdog on", CommandOff = "watchdog off", HelpWatchdog = "help watchdog";
        internal const string KindSlaveFailure = "SLAVE_FAILURE";
        internal const string KindUnjudgeable = "UNJUDGEABLE";
        private const string Prefix = "WATCHDOG ";
        private const string CompletionTitle = "완료 알림";
        private const string WarningTitle = "경고";
        private const int ListBudget = 480;
        private const int MaxReasonText = 200;
        private const int MaxDetailLine = 600;
        private const int MaxParts = 999;

        internal static string ReplyHeader(string nonce)
        {
            NeedMarker(nonce);
            return Prefix + nonce;
        }

        internal static string ArmReply(string nonce, WatchdogArmResult result, WatchdogState state, DateTime nowLocal)
        {
            if (result == null || state == null) throw new ArgumentNullException(result == null ? "result" : "state");
            var lines = new List<string> { ReplyHeader(nonce) };
            switch (result.Kind)
            {
                case WatchdogArmResult.KindArmed:
                    lines.Add("감시 시작: " + PackList(result.Added, ListBudget));
                    if (result.AlreadyWatched != null && result.AlreadyWatched.Length > 0)
                        lines.Add("이미 감시 중: " + PackList(result.AlreadyWatched, ListBudget));
                    lines.Add(ScheduleLine(state, nowLocal));
                    lines.Add("버퍼·자동 복사 Output에서 AFS Finished 뒤 Total Sampling Points가 보이면 알리고 해당 감시를 해제합니다. " +
                        "Pending·수집 실패·OCR은 완료로 판정하지 않습니다.");
                    break;
                case WatchdogArmResult.KindAlreadyWatched:
                    lines.Add("이미 감시 중입니다: " + PackList(result.AlreadyWatched, ListBudget));
                    lines.Add(ScheduleLine(state, nowLocal));
                    break;
                case WatchdogArmResult.KindNoPowerSi:
                    lines.Add("Slave에서 실행 중인 PowerSI를 찾지 못해 감시를 시작하지 않았습니다.");
                    lines.Add(ScheduleLine(state, nowLocal));
                    break;
                case WatchdogArmResult.KindPidNotFound:
                    lines.Add("Slave에서 PID " + Pid(result.RequestedPid) + " PowerSI를 찾지 못해 감시를 시작하지 않았습니다.");
                    lines.Add(ScheduleLine(state, nowLocal));
                    break;
                default:
                    throw new ArgumentException("Unknown watchdog arm result.", "result");
            }
            return Bounded(string.Join("\r\n", lines));
        }

        internal static string DisarmReply(string nonce, WatchdogDisarmResult result, WatchdogState state)
        {
            if (result == null || state == null) throw new ArgumentNullException(result == null ? "result" : "state");
            var lines = new List<string> { ReplyHeader(nonce) };
            switch (result.Kind)
            {
                case WatchdogDisarmResult.KindClearedAll:
                    lines.Add("감시 " + Number(result.Cleared) + "개를 모두 해제했습니다. watchdog 꺼짐.");
                    break;
                case WatchdogDisarmResult.KindClearedOne:
                    lines.Add((result.Removed != null && result.Removed.Length == 1 ? TargetLabel(result.Removed[0])
                        : "PID " + Pid(result.RequestedPid)) + " 감시를 해제했습니다.");
                    lines.Add(ScheduleLine(state, null));
                    break;
                case WatchdogDisarmResult.KindNothingWatched:
                    lines.Add("감시 중인 PowerSI가 없습니다. watchdog은 이미 꺼져 있습니다.");
                    break;
                case WatchdogDisarmResult.KindPidNotWatched:
                    lines.Add("PID " + Pid(result.RequestedPid) + "는 감시 대상이 아닙니다. 변경하지 않았습니다.");
                    lines.Add(ScheduleLine(state, null));
                    break;
                default:
                    throw new ArgumentException("Unknown watchdog disarm result.", "result");
            }
            return Bounded(string.Join("\r\n", lines));
        }

        internal static string SlaveFailureReply(string nonce, string reasonText)
        {
            string reason = Clean(reasonText, MaxReasonText);
            return Bounded(ReplyHeader(nonce) + "\r\nSlave에 연결하지 못해 감시를 시작하지 않았습니다 (" +
                (reason.Length == 0 ? "원인 미확인" : reason) + "). 자동 재시도하지 않습니다.");
        }

        internal static string HelpBody()
        {
            return "watchdog on은 Slave의 모든 PowerSI를, watchdog on <PID>는 해당 PowerSI만 감시 목록에 추가합니다. " +
                "watchdog off는 모든 감시를, watchdog off <PID>는 해당 감시를 해제합니다. " +
                "Master 설정 간격(30분 또는 60분)마다 PowerSI Output을 수집해 버퍼·자동 복사 Output 끝에 AFS Finished와 " +
                "Total Sampling Points가 보이면 알리고 그 대상을 해제하며, 모두 끝나면 watchdog이 꺼집니다. " +
                "Pending·수집 실패·OCR은 완료로 판정하지 않으며 Master Stop 시 감시가 끝납니다.";
        }

        internal static string HelpUsage()
        {
            return "사용법: watchdog on | watchdog on <PID> | watchdog off | watchdog off <PID> (단어 사이 한 칸)";
        }

        internal static string[] CompletionNotice(string marker, WatchdogCheckResult result, WatchdogState state, DateTime nowLocal)
        {
            NeedMarker(marker);
            if (result == null || state == null) throw new ArgumentNullException(result == null ? "result" : "state");
            WatchdogTargetOutcome[] finished = result.Finished ?? new WatchdogTargetOutcome[0];
            WatchdogTargetOutcome[] missing = result.Missing ?? new WatchdogTargetOutcome[0];
            if (finished.Length == 0 && missing.Length == 0) return new string[0];

            var lines = new List<string>();
            foreach (WatchdogTargetOutcome outcome in finished)
                lines.Add(TargetLabel(outcome.Target) + ": AFS Finished, Total Sampling Points = " +
                    outcome.SamplingPoints.ToString(CultureInfo.InvariantCulture) + " (출처: " + SourceName(outcome.Source) + ")");
            foreach (WatchdogTargetOutcome outcome in missing)
                lines.Add(TargetLabel(outcome.Target) + ": 종료되었거나 다시 시작되어 감시를 해제했습니다");
            if (state.Count == 0)
                lines.Add(result.AllCleared && missing.Length == 0 ? "모든 대상 완료 — watchdog 꺼짐" : "남은 감시 없음 — watchdog 꺼짐");
            else lines.Add(TailLine(state, nowLocal));
            return Pack(marker, CompletionTitle, lines);
        }

        internal static string[] WarningNotice(string marker, string kind, string detail, WatchdogState state, DateTime nowLocal)
        {
            NeedMarker(marker);
            if (state == null) throw new ArgumentNullException("state");
            var lines = new List<string>();
            if (kind == KindSlaveFailure)
            {
                string reason = Clean(detail, MaxReasonText);
                lines.Add("Slave 조회 " + Number(Math.Max(1, state.FailureStreak)) + "회 연속 실패 (" +
                    (reason.Length == 0 ? "원인 미확인" : reason) + "). 감시는 유지합니다.");
            }
            else if (kind == KindUnjudgeable)
            {
                foreach (string raw in (detail ?? string.Empty).Split('\n'))
                {
                    string line = Clean(raw, MaxDetailLine);
                    if (line.Length > 0) lines.Add(line + ". 감시 유지.");
                }
                if (lines.Count == 0) return new string[0];
            }
            else throw new ArgumentException("Unknown watchdog warning kind.", "kind");
            lines.Add(TailLine(state, nowLocal));
            return Pack(marker, WarningTitle, lines);
        }

        // Detail for WarningNotice(UNJUDGEABLE): one "<name> (PID n): 3회 연속 완료 판정 불가 (<reason>)" line per target.
        internal static string UnjudgedDetail(IEnumerable<WatchdogTargetOutcome> reached)
        {
            if (reached == null) return string.Empty;
            return string.Join("\n", reached.Where(item => item != null).Select(item => TargetLabel(item.Target) + ": " +
                Number(item.ConsecutiveUnjudged) + "회 연속 완료 판정 불가 (" + UnjudgedReason(item) + ")"));
        }

        internal static string UnjudgedReason(WatchdogTargetOutcome outcome)
        {
            if (outcome == null) return "보고 없음";
            if (outcome.Outcome == WatchdogTargetOutcome.OutcomeNoMarkers) return "완료 표시 없음";
            switch (outcome.ReportState)
            {
                case "PENDING": return "Pending";
                case "UNAVAILABLE": return "수집 실패";
                case "TIMEOUT": return "시간 초과";
                case "NOT_ATTEMPTED": return "수집 안 함";
                case "VISIBLE_EMPTY": return "Output 비어 있음";
                case "READ": return outcome.Source == "OCR" ? "OCR은 판정 안 함" : "판정할 수 없는 출처";
                default: return "보고 없음";
            }
        }

        internal static bool IsNotice(string text)
        {
            const int markerLength = 7;
            if (text == null || text.Length < Prefix.Length + markerLength + 3 ||
                !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            return IsMarker(text.Substring(Prefix.Length, markerLength)) &&
                string.CompareOrdinal(text, Prefix.Length + markerLength, " | ", 0, 3) == 0;
        }

        internal static string TargetLabel(WatchdogTarget target)
        {
            if (target == null) return "PowerSI";
            string name = Clean(target.ProcessName, ProcessInventory.MaxFullNameLength);
            return (name.Length == 0 ? "PowerSI" : name) + " (PID " + Number(target.Pid) + ")";
        }

        internal static string NextCheckClock(DateTime nextUtc, DateTime? nowLocal)
        {
            DateTime local = DateTime.SpecifyKind(nextUtc, DateTimeKind.Utc).ToLocalTime();
            if (nowLocal.HasValue && local.Ticks <= nowLocal.Value.Ticks) return "곧";
            return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        private static string ScheduleLine(WatchdogState state, DateTime? nowLocal)
        {
            int count, intervalMinutes, streak;
            DateTime? next;
            state.ReadSchedule(out count, out next, out intervalMinutes, out streak);
            if (count == 0 || !next.HasValue) return "감시 없음 — watchdog 꺼짐";
            string clock = NextCheckClock(next.Value, nowLocal);
            return "감시 " + Number(count) + "개 · " + Number(intervalMinutes) + "분마다 확인 · 다음 확인 " +
                (clock == "곧" ? clock : "약 " + clock);
        }

        private static string TailLine(WatchdogState state, DateTime nowLocal)
        {
            int count, intervalMinutes, streak;
            DateTime? next;
            state.ReadSchedule(out count, out next, out intervalMinutes, out streak);
            if (count == 0 || !next.HasValue) return "남은 감시 없음 — watchdog 꺼짐";
            string clock = NextCheckClock(next.Value, nowLocal);
            return "남은 감시 " + Number(count) + "개 | 다음 확인 " + (clock == "곧" ? clock : "약 " + clock);
        }

        private static string PackList(IList<WatchdogTarget> targets, int budget)
        {
            if (targets == null || targets.Count == 0) return "없음";
            var text = new StringBuilder();
            int shown = 0;
            for (; shown < targets.Count; shown++)
            {
                string label = TargetLabel(targets[shown]);
                int rest = targets.Count - shown - 1;
                int reserve = rest > 0 ? (" 외 " + Number(rest) + "개").Length : 0;
                if (text.Length + (shown > 0 ? 2 : 0) + label.Length + reserve > budget) break;
                if (shown > 0) text.Append(", ");
                text.Append(label);
            }
            if (shown == 0) return "PowerSI " + Number(targets.Count) + "개";
            if (shown < targets.Count) text.Append(" 외 ").Append(Number(targets.Count - shown)).Append('개');
            return text.ToString();
        }

        private static string[] Pack(string marker, string title, IList<string> lines)
        {
            int capacity = PcStatusReport.MaxPhoneLength - PartHeader(marker, title, MaxParts, MaxParts).Length - 2;
            var bodies = new List<StringBuilder> { new StringBuilder() };
            foreach (string raw in lines)
            {
                string line = Truncate(raw, capacity);
                StringBuilder current = bodies[bodies.Count - 1];
                if (current.Length > 0 && current.Length + 2 + line.Length > capacity)
                {
                    current = new StringBuilder();
                    bodies.Add(current);
                }
                if (current.Length > 0) current.Append("\r\n");
                current.Append(line);
            }
            if (bodies.Count > MaxParts) throw new InvalidOperationException("Watchdog notice has too many parts.");
            var parts = new string[bodies.Count];
            for (int index = 0; index < parts.Length; index++)
                parts[index] = Bounded(PartHeader(marker, title, index + 1, parts.Length) + "\r\n" + bodies[index]);
            return parts;
        }

        private static string PartHeader(string marker, string title, int index, int count)
        {
            string header = Prefix + marker + " | " + title;
            return count == 1 ? header : header + string.Format(CultureInfo.InvariantCulture, " | PART {0:000}/{1:000}", index, count);
        }

        private static string SourceName(string source)
        {
            if (source == "BUFFER") return "버퍼";
            if (source == "AUTO_COPY") return "자동 복사";
            string clean = Clean(source, 32);
            return clean.Length == 0 ? "없음" : clean;
        }

        // Phone text only: control characters and broken surrogates become spaces; long values end with "…".
        private static string Clean(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var text = new StringBuilder(Math.Min(value.Length, maximum + 1));
            for (int index = 0; index < value.Length && text.Length <= maximum; index++)
            {
                char current = value[index];
                if (char.IsHighSurrogate(current) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    text.Append(current).Append(value[++index]);
                    continue;
                }
                text.Append(char.IsControl(current) || char.IsSurrogate(current) ? ' ' : current);
            }
            return Truncate(text.ToString().Trim(), maximum);
        }

        private static string Truncate(string value, int maximum)
        {
            if (value.Length <= maximum) return value;
            int length = maximum - 1;
            if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, Math.Max(0, length)) + "…";
        }

        private static string Bounded(string text)
        {
            if (text.Length > PcStatusReport.MaxPhoneLength)
                throw new InvalidOperationException("Watchdog phone text exceeds the part limit.");
            return text;
        }

        private static bool IsMarker(string marker)
        {
            return Protocol.IsDiagnosticMarker("DRAFT", marker) || Protocol.IsDiagnosticMarker("MESSAGE", marker);
        }

        private static void NeedMarker(string marker)
        {
            if (!IsMarker(marker)) throw new ArgumentException("Invalid watchdog marker.", "marker");
        }

        private static string Pid(int? pid)
        {
            return pid.HasValue ? Number(pid.Value) : "?";
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        internal static void RunSelfTest()
        {
            const string nonce = "D234567", marker = "M345678";
            DateTime t0 = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
            DateTime nowLocal = t0.ToLocalTime();
            string next30 = t0.AddMinutes(30).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            long start = t0.AddHours(-1).Ticks;
            var state = new WatchdogState(TimeSpan.FromMinutes(30));
            var candidates = new[] { new WatchdogTarget(101, start, "PowerSI"), new WatchdogTarget(202, start + 1, "PowerSI") };

            Need(ReplyHeader(nonce) == "WATCHDOG D234567" && ReplyHeader(marker) == "WATCHDOG M345678", "HEADER");
            bool thrown = false;
            try { ReplyHeader("D123456"); }
            catch (ArgumentException) { thrown = true; }
            Need(thrown, "INVALID_NONCE_ACCEPTED");

            string reply = ArmReply(nonce, state.Arm(candidates, null, t0), state, nowLocal);
            Need(reply.StartsWith("WATCHDOG D234567\r\n감시 시작: PowerSI (PID 101), PowerSI (PID 202)\r\n", StringComparison.Ordinal) &&
                reply.Contains("감시 2개 · 30분마다 확인 · 다음 확인 약 " + next30) && reply.Contains("OCR은 완료로 판정하지 않습니다") &&
                !IsNotice(reply) && reply.Length <= PcStatusReport.MaxPhoneLength, "ARM_REPLY");
            reply = ArmReply(nonce, state.Arm(candidates, 101, t0), state, nowLocal);
            Need(reply.Contains("이미 감시 중입니다: PowerSI (PID 101)") && reply.Contains("감시 2개"), "ALREADY_REPLY");
            reply = ArmReply(nonce, state.Arm(candidates, 77, t0), state, nowLocal);
            Need(reply.Contains("PID 77 PowerSI를 찾지 못해") && reply.Contains("감시 2개"), "PID_NOT_FOUND_REPLY");
            var idle = new WatchdogState(TimeSpan.FromMinutes(60));
            reply = ArmReply(nonce, idle.Arm(new WatchdogTarget[0], null, t0), idle, nowLocal);
            Need(reply == "WATCHDOG D234567\r\nSlave에서 실행 중인 PowerSI를 찾지 못해 감시를 시작하지 않았습니다.\r\n감시 없음 — watchdog 꺼짐",
                "NO_POWERSI_REPLY");

            reply = DisarmReply(nonce, state.Disarm(999), state);
            Need(reply.Contains("PID 999는 감시 대상이 아닙니다") && reply.Contains("감시 2개"), "PID_NOT_WATCHED_REPLY");
            reply = DisarmReply(nonce, state.Disarm(202), state);
            Need(reply.StartsWith("WATCHDOG D234567\r\nPowerSI (PID 202) 감시를 해제했습니다.\r\n감시 1개", StringComparison.Ordinal),
                "CLEARED_ONE_REPLY");
            reply = DisarmReply(nonce, state.Disarm(null), state);
            Need(reply == "WATCHDOG D234567\r\n감시 1개를 모두 해제했습니다. watchdog 꺼짐.", "CLEARED_ALL_REPLY");
            reply = DisarmReply(nonce, state.Disarm(null), state);
            Need(reply.Contains("watchdog은 이미 꺼져 있습니다"), "NOTHING_WATCHED_REPLY");

            reply = SlaveFailureReply(nonce, "BUSY\r\n" + new string('x', 500));
            Need(reply.StartsWith("WATCHDOG D234567\r\nSlave에 연결하지 못해 감시를 시작하지 않았습니다 (BUSY", StringComparison.Ordinal) &&
                reply.EndsWith("…). 자동 재시도하지 않습니다.", StringComparison.Ordinal) && reply.Split('\n').Length == 2 &&
                reply.Length < 400, "SLAVE_FAILURE_REPLY");
            Need(SlaveFailureReply(nonce, null).Contains("(원인 미확인)"), "SLAVE_FAILURE_NO_REASON");

            string help = HelpBody(), usage = HelpUsage();
            Need(help.IndexOf('\r') < 0 && help.IndexOf('\n') < 0 && usage.IndexOf('\r') < 0 && usage.IndexOf('\n') < 0 &&
                help.Contains("watchdog on <PID>") && help.Contains("watchdog off <PID>") && help.Contains("30분") &&
                help.Contains("60분") && help.Contains("Pending") && help.Contains("OCR") && help.Contains("Master Stop") &&
                usage == "사용법: watchdog on | watchdog on <PID> | watchdog off | watchdog off <PID> (단어 사이 한 칸)" &&
                help.Length + usage.Length + 40 <= PcStatusReport.MaxPhoneLength && !WatchdogCommand.IsWatchdogCommand(help) &&
                !WatchdogCommand.IsWatchdogCommand(usage), "HELP");

            const string finished = "AFS Current Frequency ( GHz ) = 1\nAFS Finished\nTotal Sampling Points = 118\n";
            state.Arm(candidates, null, t0);
            WatchdogCheckResult check = state.ApplyCheck(new[]
            {
                new WatchdogReport { Pid = 101, StartUtcTicks = start, State = "READ", Source = "AUTO_COPY", OutputText = finished },
                new WatchdogReport { Pid = 202, StartUtcTicks = start + 1, State = "PENDING", Source = "NONE", OutputText = string.Empty }
            }, t0.AddMinutes(30));
            string next60 = t0.AddMinutes(60).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            string[] notice = CompletionNotice(marker, check, state, nowLocal);
            Need(notice.Length == 1 && notice[0] == "WATCHDOG M345678 | 완료 알림\r\n" +
                "PowerSI (PID 101): AFS Finished, Total Sampling Points = 118 (출처: 자동 복사)\r\n남은 감시 1개 | 다음 확인 약 " + next60 &&
                IsNotice(notice[0]), "COMPLETION_ONE");
            check = state.ApplyCheck(new WatchdogReport[0], t0.AddMinutes(60));
            notice = CompletionNotice(marker, check, state, nowLocal);
            Need(notice.Length == 1 && notice[0] == "WATCHDOG M345678 | 완료 알림\r\n" +
                "PowerSI (PID 202): 종료되었거나 다시 시작되어 감시를 해제했습니다\r\n남은 감시 없음 — watchdog 꺼짐", "COMPLETION_MISSING");
            state.Arm(candidates, 101, t0);
            check = state.ApplyCheck(new[]
            {
                new WatchdogReport { Pid = 101, StartUtcTicks = start, State = "READ", Source = "BUFFER", OutputText = finished }
            }, t0.AddMinutes(30));
            notice = CompletionNotice(marker, check, state, nowLocal);
            Need(notice.Length == 1 && notice[0].EndsWith("(출처: 버퍼)\r\n모든 대상 완료 — watchdog 꺼짐", StringComparison.Ordinal),
                "COMPLETION_ALL");
            Need(CompletionNotice(marker, new WatchdogCheckResult(), state, nowLocal).Length == 0, "COMPLETION_NOTHING");

            var many = new List<WatchdogTarget>();
            var reports = new List<WatchdogReport>();
            for (int index = 1; index <= 128; index++)
            {
                string name = "PowerSI " + index.ToString("000", CultureInfo.InvariantCulture) + new string('가', 244) + "\u0001";
                many.Add(new WatchdogTarget(1000 + index, start + index, name));
                reports.Add(new WatchdogReport
                {
                    Pid = 1000 + index, StartUtcTicks = start + index, State = "READ", Source = "BUFFER",
                    OutputText = index % 2 == 0 ? finished : "AFS Current Frequency ( GHz ) = 2\n"
                });
            }
            var large = new WatchdogState(TimeSpan.FromMinutes(30));
            large.Arm(many.Take(64), null, t0);
            reply = ArmReply(nonce, large.Arm(many, null, t0), large, nowLocal);
            Need(reply.Length <= PcStatusReport.MaxPhoneLength && reply.Contains(" 외 63개") && reply.Contains(" 외 ") &&
                reply.Contains("감시 128개") && reply.IndexOf('\u0001') < 0, "LARGE_ARM_REPLY");
            reply = DisarmReply(nonce, large.Disarm(1001), large);
            Need(reply.Length <= PcStatusReport.MaxPhoneLength && reply.Contains("(PID 1001) 감시를 해제했습니다."), "LARGE_DISARM_ONE");
            large.Arm(many, 1001, t0);
            reports.RemoveAt(0);
            check = large.ApplyCheck(reports, t0.AddMinutes(30));
            notice = CompletionNotice(marker, check, large, nowLocal);
            string joined = string.Join("\n", notice);
            Need(check.Finished.Length == 64 && check.Missing.Length == 1 && notice.Length > 1 &&
                notice.All(part => part.Length <= PcStatusReport.MaxPhoneLength && IsNotice(part) &&
                    part.StartsWith("WATCHDOG M345678 | 완료 알림 | PART ", StringComparison.Ordinal)) &&
                notice[0].StartsWith("WATCHDOG M345678 | 완료 알림 | PART 001/" +
                    notice.Length.ToString("000", CultureInfo.InvariantCulture) + "\r\n", StringComparison.Ordinal) &&
                check.Finished.All(item => joined.Contains(TargetLabel(item.Target) + ": AFS Finished")) &&
                joined.Contains("(PID 1001): 종료되었거나") && joined.EndsWith("남은 감시 63개 | 다음 확인 약 " + next60, StringComparison.Ordinal),
                "LARGE_COMPLETION_PARTS");

            var warnings = new WatchdogState(TimeSpan.FromMinutes(30));
            warnings.Arm(candidates, null, t0);
            for (int round = 1; round <= 3; round++) warnings.RecordCheckFailure("SLAVE_BUSY", t0.AddMinutes(30 * round));
            string next120 = t0.AddMinutes(120).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            notice = WarningNotice(marker, KindSlaveFailure, "Slave BUSY", warnings, nowLocal);
            Need(notice.Length == 1 && notice[0] == "WATCHDOG M345678 | 경고\r\nSlave 조회 3회 연속 실패 (Slave BUSY). 감시는 유지합니다.\r\n" +
                "남은 감시 2개 | 다음 확인 약 " + next120 && IsNotice(notice[0]), "WARNING_SLAVE");
            WatchdogCheckResult unjudged = null;
            for (int round = 4; round <= 6; round++)
                unjudged = warnings.ApplyCheck(new[]
                {
                    new WatchdogReport { Pid = 101, StartUtcTicks = start, State = "PENDING", Source = "NONE", OutputText = string.Empty },
                    new WatchdogReport { Pid = 202, StartUtcTicks = start + 1, State = "READ", Source = "OCR", OutputText = finished }
                }, t0.AddMinutes(30 * round));
            string detail = UnjudgedDetail(unjudged.UnjudgedStreakReached);
            Need(detail == "PowerSI (PID 101): 3회 연속 완료 판정 불가 (Pending)\nPowerSI (PID 202): 3회 연속 완료 판정 불가 (OCR은 판정 안 함)",
                "UNJUDGED_DETAIL");
            notice = WarningNotice(marker, KindUnjudgeable, detail, warnings, nowLocal);
            Need(notice.Length == 1 && notice[0].Contains("\r\nPowerSI (PID 101): 3회 연속 완료 판정 불가 (Pending). 감시 유지.\r\n") &&
                notice[0].Contains("(OCR은 판정 안 함). 감시 유지.") && IsNotice(notice[0]), "WARNING_UNJUDGEABLE");
            Need(WarningNotice(marker, KindUnjudgeable, string.Empty, warnings, nowLocal).Length == 0, "WARNING_EMPTY");
            thrown = false;
            try { WarningNotice(marker, "OTHER", "x", warnings, nowLocal); }
            catch (ArgumentException) { thrown = true; }
            Need(thrown, "WARNING_UNKNOWN_KIND");
            Need(UnjudgedReason(new WatchdogTargetOutcome { Outcome = WatchdogTargetOutcome.OutcomeNoMarkers, ReportState = "READ" }) ==
                "완료 표시 없음" && UnjudgedReason(new WatchdogTargetOutcome
                {
                    Outcome = WatchdogTargetOutcome.OutcomeUnjudgeable, ReportState = WatchdogTargetOutcome.ReportStateAbsent
                }) == "보고 없음", "UNJUDGED_REASON");

            foreach (string text in new[]
            {
                null, string.Empty, "WATCHDOG", "WATCHDOG D234567", "WATCHDOG D234567\r\n감시 시작", "WATCHDOG D123456 | 완료 알림",
                "WATCHDOG X234567 | 완료 알림", "watchdog M345678 | 완료 알림", " WATCHDOG M345678 | 완료 알림",
                "WATCHDOG M345678| 완료 알림", "WATCHDOG M3456789 | 완료 알림", "WATCHDOG  M345678 | 완료 알림"
            })
                Need(!IsNotice(text), "NOT_NOTICE");
            Need(IsNotice("WATCHDOG D234567 | 경고") && IsNotice("WATCHDOG M345678 | x"), "NOTICE");
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Watchdog text self-test failed: " + reason + ".");
        }
    }

    // ponytail: exact lowercase phrases only; the caller has already removed outer spaces (OUTER_SPACES rule).
    internal static class WatchdogCommand
    {
        private const int MaxPidDigits = 10;

        internal static bool TryParse(string command, out bool on, out int? pid)
        {
            on = false;
            pid = null;
            if (command == null) return false;
            if (command == WatchdogText.CommandOn)
            {
                on = true;
                return true;
            }
            if (command == WatchdogText.CommandOff) return true;

            bool isOn;
            string digits;
            if (command.StartsWith(WatchdogText.CommandOn + " ", StringComparison.Ordinal))
            {
                isOn = true;
                digits = command.Substring(WatchdogText.CommandOn.Length + 1);
            }
            else if (command.StartsWith(WatchdogText.CommandOff + " ", StringComparison.Ordinal))
            {
                isOn = false;
                digits = command.Substring(WatchdogText.CommandOff.Length + 1);
            }
            else return false;

            int value;
            if (!TryParsePid(digits, out value)) return false;
            on = isOn;
            pid = value;
            return true;
        }

        internal static bool IsWatchdogCommand(string command)
        {
            bool on;
            int? pid;
            return TryParse(command, out on, out pid) || string.Equals(command, WatchdogText.HelpWatchdog, StringComparison.Ordinal);
        }

        // PID = [1-9][0-9]{0,9} and <= int.MaxValue; ASCII digits only.
        private static bool TryParsePid(string digits, out int pid)
        {
            pid = 0;
            if (digits.Length == 0 || digits.Length > MaxPidDigits || digits[0] < '1' || digits[0] > '9') return false;
            long value = 0;
            foreach (char digit in digits)
            {
                if (digit < '0' || digit > '9') return false;
                value = value * 10 + (digit - '0');
            }
            if (value > int.MaxValue) return false;
            pid = (int)value;
            return true;
        }

        internal static void RunSelfTest()
        {
            bool on;
            int? pid;
            Need(TryParse("watchdog on", out on, out pid) && on && !pid.HasValue, "ON");
            Need(TryParse("watchdog off", out on, out pid) && !on && !pid.HasValue, "OFF");
            Need(TryParse("watchdog on 1234", out on, out pid) && on && pid == 1234, "ON_PID");
            Need(TryParse("watchdog off 7", out on, out pid) && !on && pid == 7, "OFF_PID");
            Need(TryParse("watchdog on 2147483647", out on, out pid) && on && pid == int.MaxValue, "MAX_PID");
            Need(TryParse("watchdog off 1000000000", out on, out pid) && !on && pid == 1000000000, "TEN_DIGIT_PID");
            foreach (string command in new[]
            {
                null, string.Empty, "watchdog", "watchdog ", "watchdog on ", "watchdog off ", "watchdog on 0", "watchdog on 01",
                "watchdog on 2147483648", "watchdog on 9999999999", "watchdog on 12345678901", "watchdog  on", "watchdog on  12",
                "watchdog\ton", "watchdog on\t12", "Watchdog on", "WATCHDOG ON", "watchdog On", "watchdog on 12 extra",
                "watchdog on 12 ", " watchdog on", "watchdog on -1", "watchdog on +1", "watchdog on 1e3", "watchdog on 12a",
                "watchdog on １２", "watchdog on ١", "watchdog onn", "watchdog of", "watchdog on 12",
                "watchdog on 12\r\n", "watchdog on\r\n", "watchdog status", "watchdog off all", "help watchdog", "watchdogon"
            })
            {
                on = true;
                pid = 5;
                Need(!TryParse(command, out on, out pid) && !on && !pid.HasValue, "REJECTED");
            }
            Need(IsWatchdogCommand("watchdog on") && IsWatchdogCommand("watchdog off 42") && IsWatchdogCommand("help watchdog") &&
                !IsWatchdogCommand("help  watchdog") && !IsWatchdogCommand("Help watchdog") && !IsWatchdogCommand("help watchdog ") &&
                !IsWatchdogCommand(null) && !IsWatchdogCommand("pwrsi"), "IS_WATCHDOG_COMMAND");
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Watchdog command self-test failed: " + reason + ".");
        }
    }

    // Master-only settings file. It stores the watchdog interval and nothing else from this feature.
    internal static class WatchdogSettings
    {
        private const string IntervalKey = "watchdog_interval_minutes=";
        private const int MaxFileBytes = 64 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static string DefaultPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RemoteMonitorMaster", "state", "master-settings-v1.txt");
            }
        }

        // Never throws. The last valid interval line wins; invalid lines are ignored.
        internal static int LoadIntervalMinutes(string path)
        {
            int result = WatchdogState.DefaultIntervalMinutes;
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return result;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxFileBytes) return result;
                foreach (string line in ReadLines(info.FullName, new UTF8Encoding(false, false)))
                {
                    int minutes;
                    if (TryParseIntervalLine(line, out minutes)) result = minutes;
                }
                return result;
            }
            catch (Exception)
            {
                return WatchdogState.DefaultIntervalMinutes;
            }
        }

        // Atomic rewrite (temp + replace). Other lines are kept verbatim; every interval line becomes one canonical line.
        internal static void SaveIntervalMinutes(string path, int minutes)
        {
            if (!WatchdogState.IsValidIntervalMinutes(minutes))
                throw new ArgumentOutOfRangeException("minutes", "Watchdog interval must be 30 or 60 minutes.");
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Settings path is required.", "path");
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("Settings path must include a directory.", "path");
            Directory.CreateDirectory(directory);

            var existing = new List<string>();
            if (File.Exists(fullPath))
            {
                if (new FileInfo(fullPath).Length > MaxFileBytes) throw new InvalidDataException("Master settings file is too large.");
                existing.AddRange(ReadLines(fullPath, StrictUtf8)); // Undecodable files are not overwritten.
            }
            string canonical = IntervalKey + minutes.ToString(CultureInfo.InvariantCulture);
            var lines = new List<string>();
            bool placed = false;
            foreach (string line in existing)
            {
                if (!line.StartsWith(IntervalKey, StringComparison.Ordinal))
                {
                    lines.Add(line);
                    continue;
                }
                if (!placed) lines.Add(canonical);
                placed = true;
            }
            if (!placed) lines.Add(canonical);

            byte[] bytes = StrictUtf8.GetBytes(string.Join("\r\n", lines) + "\r\n");
            string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(fullPath)) File.Replace(temporaryPath, fullPath, null);
                else File.Move(temporaryPath, fullPath);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // Explicit UTF-8 (no BOM sniffing into other encodings); CR, LF and CRLF end a line like File.ReadAllLines.
        private static List<string> ReadLines(string path, UTF8Encoding encoding)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            string text = encoding.GetString(bytes, offset, bytes.Length - offset);
            var lines = new List<string>();
            int start = 0;
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] != '\r' && text[index] != '\n') continue;
                lines.Add(text.Substring(start, index - start));
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                start = index + 1;
            }
            if (start < text.Length) lines.Add(text.Substring(start));
            return lines;
        }

        private static bool TryParseIntervalLine(string line, out int minutes)
        {
            minutes = 0;
            if (line == null || !line.StartsWith(IntervalKey, StringComparison.Ordinal)) return false;
            string digits = line.Substring(IntervalKey.Length);
            if (digits.Length == 0 || digits.Length > 4 || digits[0] < '1' || digits[0] > '9') return false;
            int value = 0;
            foreach (char digit in digits)
            {
                if (digit < '0' || digit > '9') return false;
                value = value * 10 + (digit - '0');
            }
            if (!WatchdogState.IsValidIntervalMinutes(value)) return false;
            minutes = value;
            return true;
        }

        internal static void RunSelfTest()
        {
            string folder = Path.Combine(Path.GetTempPath(), "RemoteMonitorMaster-watchdog-settings-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, "state", "master-settings-v1.txt");
            try
            {
                string defaultPath = DefaultPath;
                Need(Path.GetFileName(defaultPath) == "master-settings-v1.txt" &&
                    Path.GetFileName(Path.GetDirectoryName(defaultPath)) == "state" &&
                    Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(defaultPath))) == "RemoteMonitorMaster", "DEFAULT_PATH");
                Need(LoadIntervalMinutes(path) == 30 && LoadIntervalMinutes(null) == 30 && LoadIntervalMinutes(string.Empty) == 30 &&
                    LoadIntervalMinutes("bad\0path") == 30 && !File.Exists(path), "MISSING_DEFAULT");

                SaveIntervalMinutes(path, 60);
                Need(LoadIntervalMinutes(path) == 60 &&
                    File.ReadAllText(path, StrictUtf8) == "watchdog_interval_minutes=60\r\n", "SAVE_NEW");
                SaveIntervalMinutes(path, 30);
                Need(LoadIntervalMinutes(path) == 30, "SAVE_30");

                File.WriteAllText(path, "# 한글 comment\r\nother_setting=1\r\nwatchdog_interval_minutes=45\r\n" +
                    "watchdog_interval_minutes= 60\r\nwatchdog_interval_minutes=060\r\nWATCHDOG_INTERVAL_MINUTES=60\r\n" +
                    " watchdog_interval_minutes=60\r\nwatchdog_interval_minutes=60 \r\nwatchdog_interval_minutes=+60\r\n" +
                    "watchdog_interval_minutes=\r\ngarbage\r\n", StrictUtf8);
                Need(LoadIntervalMinutes(path) == 30, "INVALID_LINES_IGNORED");
                File.AppendAllText(path, "watchdog_interval_minutes=60\n", StrictUtf8);
                Need(LoadIntervalMinutes(path) == 60, "VALID_LINE_AMONG_INVALID");

                SaveIntervalMinutes(path, 60);
                string[] saved = File.ReadAllLines(path, StrictUtf8);
                Need(saved.SequenceEqual(new[]
                {
                    "# 한글 comment", "other_setting=1", "watchdog_interval_minutes=60", "WATCHDOG_INTERVAL_MINUTES=60",
                    " watchdog_interval_minutes=60", "garbage"
                }) && LoadIntervalMinutes(path) == 60, "OTHER_LINES_KEPT");
                Need(Directory.GetFiles(Path.GetDirectoryName(path)).Length == 1, "NO_TEMP_FILES_LEFT");

                bool thrown = false;
                try { SaveIntervalMinutes(path, 45); }
                catch (ArgumentOutOfRangeException) { thrown = true; }
                Need(thrown && LoadIntervalMinutes(path) == 60, "INVALID_SAVE_REJECTED");

                File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE, 0x00, 0x41 });
                Need(LoadIntervalMinutes(path) == 30, "BINARY_DEFAULT");
                thrown = false;
                try { SaveIntervalMinutes(path, 60); }
                catch (DecoderFallbackException) { thrown = true; }
                Need(thrown && File.ReadAllBytes(path).Length == 4, "UNDECODABLE_NOT_OVERWRITTEN");

                File.WriteAllText(path, new string('a', MaxFileBytes + 1) + "\r\nwatchdog_interval_minutes=60\r\n", StrictUtf8);
                Need(LoadIntervalMinutes(path) == 30, "OVERSIZED_DEFAULT");
                Need(LoadIntervalMinutes(Path.GetDirectoryName(path)) == 30, "DIRECTORY_DEFAULT");
            }
            finally
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Watchdog settings self-test failed: " + reason + ".");
        }
    }

    internal static class WatchdogSelfTest
    {
        internal static void RunSelfTest()
        {
            WatchdogCommand.RunSelfTest();
            WatchdogSettings.RunSelfTest();
            WatchdogState.RunSelfTest();
            WatchdogText.RunSelfTest();
        }
    }
}
