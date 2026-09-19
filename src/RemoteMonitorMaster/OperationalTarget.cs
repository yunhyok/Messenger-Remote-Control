using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
                () => Need(ShowWindowAsync(window, SwRestore), "TARGET_RESTORE_REJECTED"));
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
                    () => Need(TryActivateOnce(() => SetForegroundWindow(window)), "TARGET_ACTIVATION_REJECTED"));
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

        private void WaitForPcIdle()
        {
            while (true)
            {
                Check();
                uint lastInput;
                Need(TryGetLastInputTick(out lastInput), "TARGET_LAST_INPUT_UNAVAILABLE");
                if (MouseClickInput.IsForegroundInputQuiet() &&
                    IsInputIdle(unchecked((uint)Environment.TickCount), lastInput, GetAsyncKeyState)) return;
                Report("WAITING_FOR_PC_IDLE");
                Thread.Sleep(50);
            }
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
            Need(TryGetLastInputTick(out lastInput) && MouseClickInput.IsForegroundInputQuiet() &&
                IsInputIdle(unchecked((uint)Environment.TickCount), lastInput, GetAsyncKeyState), "TARGET_INPUT_CHANGED");
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

        // The high bit is physical key-down. Deliberately ignore the low toggle bit.
        internal static bool IsInputIdle(uint now, uint lastInput, Func<int, short> getAsyncKeyState)
        {
            var elapsed = unchecked(now - lastInput);
            if (getAsyncKeyState == null || elapsed < 1000 || elapsed > int.MaxValue) return false;
            for (var key = 1; key <= 254; key++)
                if ((((ushort)getAsyncKeyState(key)) & 0x8000) != 0) return false;
            return true;
        }

        internal static void RunSelfTest()
        {
            Need(!NeedsActivation(true) && NeedsActivation(false), "TARGET_ACTIVATION_TRANSITION_CHANGED");
            Need(!IsInputIdle(1000, 1, key => 0), "TARGET_IDLE_INTERVAL_CHANGED");
            Need(!IsInputIdle(2000, 1000, key => key == 20 ? unchecked((short)0x8000) : (short)1),
                "TARGET_HIGH_BIT_KEY_GUARD_CHANGED");
            Need(IsInputIdle(2000, 1000, key => (short)1), "TARGET_TOGGLE_BIT_WAS_TREATED_AS_DOWN");
            Need(IsInputIdle(100, unchecked((uint)-1000), key => 0), "TARGET_IDLE_TICK_WRAP_CHANGED");
            Need(!IsInputIdle(1000, 2000, key => 0), "TARGET_FUTURE_INPUT_TICK_ACCEPTED");
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

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo { public uint Size, Time; }

        private static bool TryGetLastInputTick(out uint tick)
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
            tick = 0;
            if (!GetLastInputInfo(ref info)) return false;
            tick = info.Time;
            return true;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetLastInputInfo(ref LastInputInfo info);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index, IntPtr buffer, uint length, out uint needed);
    }
}
