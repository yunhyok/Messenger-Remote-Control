using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace RemoteMonitorMaster
{
    // Holds the one explicitly selected messenger root. It never discovers or adopts another window.
    internal sealed class OperationalTarget
    {
        private const int SwRestore = 9;
        private const uint DesktopReadObjects = 0x00020001;
        private const int UoiName = 2;
        private readonly IntPtr window;
        private readonly ProcessIdentity process;
        private readonly NativeMethods.WindowRectangle bounds;
        private readonly Func<bool> stop;
        private readonly Action<string> progress;
        private readonly AuditLog log;
        private readonly string rootRuntimeId, rootNameHash;
        private string reportedPhase;

        internal OperationalTarget(IntPtr window, ProcessIdentity process, NativeMethods.WindowRectangle bounds,
            Func<bool> stop, Action<string> progress, AuditLog log)
        {
            Need(window != IntPtr.Zero && process != null && stop != null && progress != null && log != null,
                "TARGET_REQUEST_INVALID");
            Need(NativeMethods.GetForegroundWindow() == window, "TARGET_INITIAL_FOREGROUND_REQUIRED");
            this.window = window;
            this.process = process;
            this.bounds = bounds;
            this.stop = stop;
            this.progress = progress;
            this.log = log;
            Check();
            VerifyFullIdentity();
            string runtimeId;
            string nameHash;
            CaptureRootIdentity(out runtimeId, out nameHash);
            rootRuntimeId = runtimeId;
            rootNameHash = nameHash;
        }

        internal void Check()
        {
            CheckNative();
            if (!IsMinimized(window))
            {
                NativeMethods.WindowRectangle current;
                Need(NativeMethods.GetWindowRect(window, out current) && current.Equals(bounds), "TARGET_WINDOW_MOVED");
            }
        }

        private void CheckNative()
        {
            Need(!stop(), "TARGET_CANCELLED");
            uint pid;
            Need(NativeMethods.IsWindow(window) && NativeMethods.GetWindowThreadProcessId(window, out pid) != 0 &&
                pid == process.ProcessId, "TARGET_WINDOW_OR_PID_CHANGED");
        }

        // Read-only capture is allowed while a normal target is behind another window.
        internal void PrepareRead()
        {
            reportedPhase = null; // Each prepared read reports its own waiting/activating phases again.
            Check();
            if (!IsMinimized(window)) return;
            RequireUsableInputDesktop();
            WaitForPcIdle();
            RequireUsableInputDesktop();
            Check();
            VerifyFullIdentity();
            VerifyRootIdentity();
            Report("RESTORING_TARGET");
            RunWhenVerified(VerifyMutationGuard, VerifyMutationRoot,
                () => Need(Attempted("ShowWindowAsync", ShowWindowAsync(window, SwRestore)), "TARGET_RESTORE_REJECTED"));
            WaitForNormalBounds();
            Check();
            VerifyFullIdentity();
            VerifyRootIdentity();
        }

        // Only the original selected root may be activated, and only immediately before input is needed.
        internal void PrepareSend()
        {
            PrepareRead();
            RequireUsableInputDesktop();
            WaitForPcIdle();
            RequireUsableInputDesktop();
            Check();
            VerifyFullIdentity();
            VerifyRootIdentity();
            if (NeedsActivation(NativeMethods.GetForegroundWindow() == window))
            {
                Report("ACTIVATING_TARGET");
                var priorForeground = NativeMethods.GetForegroundWindow();
                RunWhenVerified(VerifyMutationGuard, VerifyMutationRoot,
                    () => Need(TryActivateOnce(() => Attempted("SetForegroundWindow", SetForegroundWindow(window))),
                        "TARGET_ACTIVATION_REJECTED"));
                var wait = Stopwatch.StartNew();
                while (true)
                {
                    Check();
                    var foreground = NativeMethods.GetForegroundWindow();
                    var state = ActivationWaitState(stop(), foreground, window, priorForeground);
                    if (state == "READY") break;
                    Need(state != "CANCELLED", "TARGET_CANCELLED");
                    Need(state == "WAIT", "TARGET_FOREGROUND_INTERFERED");
                    Need(wait.Elapsed < TimeSpan.FromSeconds(2), "TARGET_FOREGROUND_NOT_ACQUIRED");
                    Thread.Sleep(50);
                }
            }
            Check();
            Need(NativeMethods.GetForegroundWindow() == window, "TARGET_FOREGROUND_INTERFERED");
            VerifyFullIdentity();
            VerifyRootIdentity();
        }

        internal static bool IsMinimized(IntPtr window)
        {
            return window != IntPtr.Zero && IsIconic(window);
        }

        // One sample of why PC input is not quiet yet. Never holds a window title, text or process path.
        internal sealed class IdleDiagnosis
        {
            internal string Reason = "IDLE";
            internal string HeldKeys = string.Empty;
            internal uint InputAgeMilliseconds, ForegroundProcessId, GuiFlags;
            internal bool ForegroundIsTarget, GuiQuiet, GuiAvailable, GuiActiveMatches, GuiCapture, GuiMenu, GuiMoveSize;
            internal bool OwnInput; // The last input tick is exactly the tick our own guarded click produced.
        }

        private void WaitForPcIdle()
        {
            var wait = Stopwatch.StartNew();
            var nextRecord = TimeSpan.FromSeconds(2); // First record after 2 s, then every 10 s while still waiting.
            var recorded = false;
            var reportedPlainPhase = false;
            string lastReason = null;
            var reasonClock = Stopwatch.StartNew(); // A flapping blocker is published at most once per second.
            while (true)
            {
                Check();
                uint lastInput;
                Need(MouseClickInput.TryGetLastInputTick(out lastInput), "TARGET_LAST_INPUT_UNAVAILABLE");
                // Our own injected click is read once per sample; a newer real input always carries a different tick.
                var state = Diagnose(unchecked((uint)Environment.TickCount), lastInput, MouseClickInput.LastOwnInputTick);
                if (state.Reason == "IDLE")
                {
                    if (recorded)
                        log.Write("INFO", "OPERATIONAL_TARGET_IDLE_END", AuditLog.Field("waited_ms", wait.ElapsedMilliseconds));
                    return;
                }
                if (!reportedPlainPhase) { Report("WAITING_FOR_PC_IDLE"); reportedPlainPhase = true; }
                if (state.Reason != lastReason && (lastReason == null || reasonClock.ElapsedMilliseconds >= 1000))
                {
                    Report("WAITING_FOR_PC_IDLE:" + state.Reason); // Only a changed blocker is published, rate-limited.
                    lastReason = state.Reason;
                    reasonClock.Restart();
                }
                if (wait.Elapsed >= TimeSpan.FromSeconds(60))
                {
                    RecordIdleWait(state, wait.ElapsedMilliseconds, true);
                    throw new MonitorException("TARGET_PC_NOT_IDLE",
                        "PC input did not become idle within 60 seconds: " + state.Reason);
                }
                if (wait.Elapsed >= nextRecord)
                {
                    RecordIdleWait(state, wait.ElapsedMilliseconds, false);
                    recorded = true;
                    nextRecord += TimeSpan.FromSeconds(10);
                }
                Thread.Sleep(50);
            }
        }

        private IdleDiagnosis Diagnose(uint now, uint lastInput, uint? ownInputTick)
        {
            var state = new IdleDiagnosis { InputAgeMilliseconds = unchecked(now - lastInput) };
            state.OwnInput = ownInputTick.HasValue && ownInputTick.Value == lastInput;
            state.HeldKeys = DescribeHeldKeys(GetAsyncKeyState);
            bool activeMatches, capture, menu, moveSize, available;
            uint flags;
            state.GuiQuiet = MouseClickInput.DescribeForegroundInput(out activeMatches, out capture, out menu,
                out moveSize, out flags, out available);
            state.GuiAvailable = available;
            state.GuiActiveMatches = activeMatches;
            state.GuiCapture = capture;
            state.GuiMenu = menu;
            state.GuiMoveSize = moveSize;
            state.GuiFlags = flags;
            var foreground = NativeMethods.GetForegroundWindow();
            state.ForegroundIsTarget = foreground == window;
            uint pid;
            state.ForegroundProcessId = NativeMethods.GetWindowThreadProcessId(foreground, out pid) == 0 ? 0 : pid;
            state.Reason = IdleWaitReason(IsInputRecent(now, lastInput, ownInputTick), state.HeldKeys.Length != 0, state.GuiQuiet,
                foreground != IntPtr.Zero);
            return state;
        }

        private void RecordIdleWait(IdleDiagnosis state, long waitedMilliseconds, bool final)
        {
            log.Write("INFO", "OPERATIONAL_TARGET_IDLE_WAIT", AuditLog.Field("reason", state.Reason),
                AuditLog.Field("input_age_ms", state.InputAgeMilliseconds), AuditLog.Field("held_keys", state.HeldKeys),
                AuditLog.Field("foreground_is_target", state.ForegroundIsTarget),
                AuditLog.Field("foreground_pid", state.ForegroundProcessId),
                AuditLog.Field("gui_available", state.GuiAvailable), AuditLog.Field("gui_active_matches", state.GuiActiveMatches),
                AuditLog.Field("gui_capture", state.GuiCapture), AuditLog.Field("gui_menu", state.GuiMenu),
                AuditLog.Field("gui_movesize", state.GuiMoveSize), AuditLog.Field("gui_flags", Hex(state.GuiFlags)),
                AuditLog.Field("waited_ms", waitedMilliseconds), AuditLog.Field("final", final),
                AuditLog.Field("own_input", state.OwnInput));
        }

        private static string Hex(uint value)
        {
            return "0x" + value.ToString("X", CultureInfo.InvariantCulture);
        }

        // Exactly one blocker so the operator has one action. IDLE means both guarded predicates already hold.
        internal static string IdleWaitReason(bool inputRecent, bool keyHeld, bool guiQuiet, bool hasForeground)
        {
            if (inputRecent) return "INPUT_RECENT";
            if (keyHeld) return "KEY_HELD";
            if (guiQuiet) return "IDLE";
            return hasForeground ? "FOREGROUND_BUSY" : "NO_FOREGROUND";
        }

        // Only the guarded button/modifier list, so the record can never become a keystroke transcript.
        internal static string DescribeHeldKeys(Func<int, short> getAsyncKeyState)
        {
            if (getAsyncKeyState == null) return string.Empty;
            var held = new StringBuilder();
            foreach (var key in MouseClickInput.HeldKeys)
                if ((((ushort)getAsyncKeyState(key)) & 0x8000) != 0)
                {
                    if (held.Length != 0) held.Append(',');
                    held.Append("0x").Append(key.ToString("X2", CultureInfo.InvariantCulture));
                }
            return held.ToString();
        }

        private void WaitForNormalBounds()
        {
            var wait = Stopwatch.StartNew();
            while (true)
            {
                CheckNative(); // ShowWindowAsync may clear iconic state before the final rectangle is restored.
                RequireUsableInputDesktop();
                NativeMethods.WindowRectangle current;
                if (!IsMinimized(window) && NativeMethods.GetWindowRect(window, out current) && current.Equals(bounds)) return;
                Need(wait.Elapsed < TimeSpan.FromSeconds(2), "TARGET_RESTORE_BOUNDS_CHANGED");
                Thread.Sleep(50);
            }
        }

        private void VerifyFullIdentity()
        {
            Check();
            Need(process.Equals(ProcessIdentity.Capture(window)), "TARGET_PROCESS_IDENTITY_CHANGED");
            Check();
        }

        private void CaptureRootIdentity(out string runtimeId, out string nameHash)
        {
            // A provider can drop the root between guards; a UIA/COM failure is a stop, not an unhandled crash.
            try
            {
                Check();
                var root = AutomationElement.FromHandle(window);
                Need(root != null, "TARGET_ROOT_UNAVAILABLE");
                object pid = root.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty, true);
                object password = root.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
                Need(pid is int && (int)pid == process.ProcessId && password is bool && !(bool)password,
                    "TARGET_ROOT_PROTECTED_OR_FOREIGN");
                object native = root.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty, true);
                Need(native is int && new IntPtr((int)native) == window, "TARGET_ROOT_WINDOW_CHANGED");
                runtimeId = UiaPointProbe.Format(root.GetRuntimeId());
                Need(!string.IsNullOrEmpty(runtimeId), "TARGET_ROOT_RUNTIME_UNAVAILABLE");
                // Read Name only after the PID/password guard; it can contain conversation content on a bad provider.
                nameHash = TokenStore.Hash(root.Current.Name ?? string.Empty);
                Check();
            }
            catch (MonitorException) { throw; }
            catch (Exception ex)
            {
                throw new MonitorException("TARGET_ROOT_UNAVAILABLE", "The operational target root could not be read.", ex);
            }
        }

        private void VerifyRootIdentity()
        {
            string runtimeId;
            string nameHash;
            CaptureRootIdentity(out runtimeId, out nameHash);
            Need(runtimeId == rootRuntimeId && nameHash == rootNameHash, "TARGET_ROOT_IDENTITY_CHANGED");
        }

        private bool VerifyMutationGuard()
        {
            Check();
            RequireUsableInputDesktop();
            VerifyFullIdentity();
            return true;
        }

        private bool VerifyMutationRoot()
        {
            VerifyRootIdentity();
            uint lastInput;
            Need(MouseClickInput.TryGetLastInputTick(out lastInput) && MouseClickInput.IsForegroundInputQuiet() &&
                IsInputIdle(unchecked((uint)Environment.TickCount), lastInput, MouseClickInput.LastOwnInputTick, GetAsyncKeyState),
                "TARGET_INPUT_CHANGED");
            Check();
            return true;
        }

        private void RequireUsableInputDesktop()
        {
            var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
            if (desktop == IntPtr.Zero) throw new MonitorException("TARGET_INPUT_DESKTOP_UNAVAILABLE", "The input desktop is unavailable.");
            try
            {
                uint needed;
                GetUserObjectInformation(desktop, UoiName, IntPtr.Zero, 0, out needed);
                Need(needed >= 2 && needed <= 512, "TARGET_INPUT_DESKTOP_INSECURE");
                var memory = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Need(GetUserObjectInformation(desktop, UoiName, memory, needed, out needed), "TARGET_INPUT_DESKTOP_INSECURE");
                    var name = Marshal.PtrToStringUni(memory) ?? string.Empty;
                    Need(string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase), "TARGET_INPUT_DESKTOP_INSECURE");
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            finally { CloseDesktop(desktop); }
        }

        // Record why Windows denied the call before the caller converts it into a stop reason. No retry.
        private bool Attempted(string call, bool result)
        {
            if (!result)
                log.Write("WARN", "OPERATIONAL_TARGET_DENIED", AuditLog.Field("call", call),
                    AuditLog.Field("win32_error", Marshal.GetLastWin32Error()));
            return result;
        }

        private void Report(string phase)
        {
            if (reportedPhase == phase) return;
            reportedPhase = phase;
            progress(phase);
            log.Write("INFO", "OPERATIONAL_TARGET_PHASE", AuditLog.Field("phase", phase));
        }

        private static bool NeedsActivation(bool targetForeground) { return !targetForeground; }

        private static string ActivationWaitState(bool stopped, IntPtr foreground, IntPtr target, IntPtr prior)
        {
            if (stopped) return "CANCELLED";
            if (foreground == target) return "READY";
            return foreground == IntPtr.Zero || foreground == prior ? "WAIT" : "INTERFERED";
        }

        private static bool TryActivateOnce(Func<bool> activate) { return activate != null && activate(); }

        private static void RunWhenVerified(Func<bool> guard, Func<bool> root, Action mutation)
        {
            if (guard == null || root == null || mutation == null || !guard() || !root())
                throw new MonitorException("TARGET_PRE_MUTATION_REJECTED", "Operational target changed before mutation.");
            mutation();
        }

        // A last-input tick in the future (or past the wrap window) counts as input happening right now.
        internal static bool IsInputRecent(uint now, uint lastInput)
        {
            var elapsed = unchecked(now - lastInput);
            return elapsed < 1000 || elapsed > int.MaxValue;
        }

        // Live rule: the tick our own guarded click produced is not user input, so the gate never waits for it. The
        // match is exact with no tolerance window; any other tick, including an asynchronously updated one, falls
        // back to the unchanged 1-second rule. The stored tick is never cleared, because real input moves the tick.
        internal static bool IsInputRecent(uint now, uint lastInput, uint? ownInputTick)
        {
            if (ownInputTick.HasValue && ownInputTick.Value == lastInput) return false;
            return IsInputRecent(now, lastInput);
        }

        // The high bit is physical key-down. Deliberately ignore the low toggle bit.
        // Only MouseClickInput.HeldKeys is swept: a held modifier or mouse button changes the meaning of the guarded
        // click/SetValue, while active typing is already covered by the 1-second input-age rule. A sweep over every
        // virtual key trips on phantom/IME key states (VK_HANGUL, VK_HANJA, stuck HID keys) and blocked the Win7 field
        // Master forever. The click-time guard uses the same list, so the two checks cannot disagree.
        internal static bool IsInputIdle(uint now, uint lastInput, Func<int, short> getAsyncKeyState)
        {
            return IsInputIdle(now, lastInput, null, getAsyncKeyState);
        }

        // Same sweep, with our own injected click recognized by its exact tick. Every other rule is unchanged.
        internal static bool IsInputIdle(uint now, uint lastInput, uint? ownInputTick, Func<int, short> getAsyncKeyState)
        {
            if (getAsyncKeyState == null || IsInputRecent(now, lastInput, ownInputTick)) return false;
            foreach (var key in MouseClickInput.HeldKeys)
                if ((((ushort)getAsyncKeyState(key)) & 0x8000) != 0) return false;
            return true;
        }

        internal static void RunSelfTest()
        {
            Need(!NeedsActivation(true) && NeedsActivation(false), "TARGET_ACTIVATION_TRANSITION_CHANGED");
            Need(!IsInputIdle(1000, 1, key => 0), "TARGET_IDLE_INTERVAL_CHANGED");
            Need(!IsInputIdle(2000, 1000, key => key == 0x11 ? unchecked((short)0x8000) : (short)1),
                "TARGET_HIGH_BIT_KEY_GUARD_CHANGED");
            // A phantom or IME key outside the guarded list (VK_HANGUL, a letter) must never hold the wait open.
            Need(IsInputIdle(2000, 1000, key => key == 0x15 || key == 0x19 || key == 0x41 ? unchecked((short)0x8000) : (short)1),
                "TARGET_UNLISTED_KEY_BLOCKED_IDLE");
            Need(IsInputIdle(2000, 1000, key => (short)1), "TARGET_TOGGLE_BIT_WAS_TREATED_AS_DOWN");
            Need(IsInputIdle(100, unchecked((uint)-1000), key => 0), "TARGET_IDLE_TICK_WRAP_CHANGED");
            Need(!IsInputIdle(1000, 2000, key => 0), "TARGET_FUTURE_INPUT_TICK_ACCEPTED");
            Need(!IsInputRecent(2000, 1000) && IsInputRecent(1000, 1) && IsInputRecent(1000, 2000) &&
                !IsInputRecent(100, unchecked((uint)-1000)), "TARGET_INPUT_AGE_RULE_CHANGED");
            // Our own injected click is recognized by its exact tick only; everything else keeps the 1-second rule.
            Need(!IsInputRecent(1000, 1, 1u) && IsInputRecent(1000, 1, 2u) && IsInputRecent(1000, 1, null) &&
                !IsInputRecent(2000, 1000, 999u) && !IsInputRecent(2000, 1000, null),
                "TARGET_OWN_INPUT_RULE_CHANGED");
            // The same wrap-around cases as TARGET_INPUT_AGE_RULE_CHANGED, with and without a matching own tick.
            Need(!IsInputRecent(1000, 2000, 2000u) && IsInputRecent(1000, 2000, 1999u) &&
                !IsInputRecent(100, unchecked((uint)-1000), unchecked((uint)-1000)) &&
                !IsInputRecent(100, unchecked((uint)-1000), 7u) &&
                IsInputRecent(100, unchecked((uint)-100), 7u) && !IsInputRecent(100, unchecked((uint)-100), unchecked((uint)-100)),
                "TARGET_OWN_INPUT_WRAP_RULE_CHANGED");
            // The own-input tick satisfies only the input-age rule; the guarded key sweep still holds the gate.
            Need(IsInputIdle(1000, 1, 1u, key => 0) && !IsInputIdle(1000, 1, null, key => 0) &&
                !IsInputIdle(1000, 1, 2u, key => 0) && !IsInputIdle(1000, 1, 1u, null) &&
                !IsInputIdle(1000, 1, 1u, key => key == 0x11 ? unchecked((short)0x8000) : (short)1) &&
                IsInputIdle(1000, 1, 1u, key => key == 0x15 ? unchecked((short)0x8000) : (short)1),
                "TARGET_OWN_INPUT_IDLE_RULE_CHANGED");
            Need(IdleWaitReason(true, true, false, false) == "INPUT_RECENT" &&
                IdleWaitReason(false, true, true, true) == "KEY_HELD" &&
                IdleWaitReason(false, false, false, true) == "FOREGROUND_BUSY" &&
                IdleWaitReason(false, false, false, false) == "NO_FOREGROUND" &&
                IdleWaitReason(false, false, true, true) == "IDLE", "TARGET_IDLE_REASON_PRIORITY_CHANGED");
            Need(DescribeHeldKeys(key => key == 0x11 || key == 0x01 ? unchecked((short)0x8000) : (short)1) == "0x01,0x11" &&
                DescribeHeldKeys(key => (short)1) == string.Empty && DescribeHeldKeys(null) == string.Empty &&
                DescribeHeldKeys(key => key == 0x15 ? unchecked((short)0x8000) : (short)0) == string.Empty,
                "TARGET_HELD_KEY_RECORD_CHANGED");
            Need(ActivationWaitState(false, new IntPtr(1), new IntPtr(1), new IntPtr(2)) == "READY" &&
                ActivationWaitState(false, IntPtr.Zero, new IntPtr(1), new IntPtr(2)) == "WAIT" &&
                ActivationWaitState(false, new IntPtr(2), new IntPtr(1), new IntPtr(2)) == "WAIT" &&
                ActivationWaitState(false, new IntPtr(3), new IntPtr(1), new IntPtr(2)) == "INTERFERED" &&
                ActivationWaitState(true, new IntPtr(1), new IntPtr(1), new IntPtr(2)) == "CANCELLED",
                "TARGET_ACTIVATION_WAIT_TRANSITION_CHANGED");
            var activationCalls = 0;
            Need(!TryActivateOnce(() => { activationCalls++; return false; }) && activationCalls == 1,
                "TARGET_ACTIVATION_DENIAL_WAS_RETRIED");
            var mutations = 0;
            Need(!TryRunWhenVerified(() => false, () => true, () => mutations++ ) && mutations == 0,
                "TARGET_CANCELLED_MUTATION_WAS_ALLOWED");
            Need(!TryRunWhenVerified(() => true, () => false, () => mutations++) && mutations == 0,
                "TARGET_ROOT_MISMATCH_MUTATION_WAS_ALLOWED");
            Need(TryRunWhenVerified(() => true, () => true, () => mutations++) && mutations == 1,
                "TARGET_VERIFIED_MUTATION_WAS_NOT_SINGLE");
        }

        private static bool TryRunWhenVerified(Func<bool> guard, Func<bool> root, Action mutation)
        {
            try { RunWhenVerified(guard, root, mutation); return true; }
            catch (MonitorException) { return false; }
        }

        private static void Need(bool condition, string code)
        {
            if (!condition) throw new MonitorException(code, "Operational target validation failed: " + code);
        }

        // The last-input tick reader lives in MouseClickInput, next to the click that records its own tick.
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index, IntPtr buffer, uint length, out uint needed);
    }
}
