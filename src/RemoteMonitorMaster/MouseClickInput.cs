using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace RemoteMonitorMaster
{
    internal static class MouseClickInput
    {
        internal const int PressDurationMilliseconds = 120;

        internal sealed class Result
        {
            public int Inserted = -1;
            public int DownInserted = -1;
            public int ReleaseInserted = -1;
            public bool Returned;
            public bool ReleaseAttempted;
            public int Error;
            public int ReleaseError;
            public long HoldMilliseconds;
            public int PressSamples;
            public bool PrimaryDownObserved;
            public bool? PointerStable, ForegroundStable, TargetStable, HitThreadSameAsRoot;
            public string CaptureRelation = "UNAVAILABLE", FocusRelation = "UNAVAILABLE";
            public bool TargetCaptureObserved;
            public bool GuiUnavailableObserved;
        }

        // Only MOUSEINPUT is used; it is the largest INPUT union member on x86 and x64.
        [StructLayout(LayoutKind.Sequential)]
        private struct Input { public uint Type; public MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X, Y;
            public uint Data, Flags, Time;
            public UIntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public uint Size, Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public int Left, Top, Right, Bottom;
        }

        private delegate uint InsertEvents(Input[] events, int size, out int error);
        // The field-proven guarded-input set: mouse buttons and Shift/Ctrl/Alt/Win. Shared with OperationalTarget.
        internal static readonly int[] HeldKeys = { 0x01, 0x02, 0x04, 0x05, 0x06, 0x10, 0x11, 0x12, 0x5B, 0x5C };

        internal static void PositionOnce(System.Windows.Point target, int expectedPid, IntPtr expectedRoot,
            AuditLog log, Action guard, Action beforeMove)
        {
            Need(SendMetadataProbe.IsNativePoint(target) && expectedPid > 0 && expectedRoot != IntPtr.Zero &&
                log != null && guard != null && beforeMove != null, "POSITION_REQUEST_INVALID");
            var destination = new NativeMethods.ScreenPoint { X = (int)target.X, Y = (int)target.Y };
            IntPtr originalHit = IntPtr.Zero;
            uint originalRootThread = 0, originalHitThread = 0;
            Func<int, int, bool> move = SetPhysicalCursorPos; // Prepare the native delegate before movement consent.
            void Check()
            {
                guard();
                uint rootPid, hitPid;
                var thread = NativeMethods.GetWindowThreadProcessId(expectedRoot, out rootPid);
                Need(NativeMethods.IsWindow(expectedRoot) && NativeMethods.GetForegroundWindow() == expectedRoot &&
                    thread != 0 && rootPid == expectedPid && (originalRootThread == 0 || originalRootThread == thread),
                    "POSITION_TARGET_CHANGED");
                var hit = WindowFromPhysicalPoint(destination);
                var hitThread = NativeMethods.GetWindowThreadProcessId(hit, out hitPid);
                Need(hit != IntPtr.Zero && NativeMethods.IsWindow(hit) && hitThread != 0 && hitPid == expectedPid &&
                    (hit == expectedRoot || NativeMethods.IsChild(expectedRoot, hit)) &&
                    (originalHit == IntPtr.Zero || originalHit == hit) && (originalHitThread == 0 || originalHitThread == hitThread),
                    "POSITION_DESTINATION_CHANGED");
                originalHit = hit;
                originalRootThread = thread;
                originalHitThread = hitThread;
                CheckIdleInput(expectedRoot, thread, hitThread); // Current pointer may be outside the target; no drag/held input may exist.
                guard();
            }

            Check();
            log.Write("INFO", "MOUSE_POSITION_READY", AuditLog.Field("maximum_position_calls", 1),
                AuditLog.Field("foreground_activation_calls", 0), AuditLog.Field("cursor_restore_requested", false));
            Check();
            // Positioning is global and cannot lock the desktop. The caller must switch to strict pointer checks after return.
            MoveOnce(destination.X, destination.Y, beforeMove, move);
            guard();
            NativeMethods.ScreenPoint current;
            Need(NativeMethods.GetPhysicalCursorPos(out current) && current.X == destination.X && current.Y == destination.Y,
                "POSITION_NOT_VERIFIED");
            Check();
            log.Write("INFO", "MOUSE_POSITION_RESULT", AuditLog.Field("status", "POSITIONED"),
                AuditLog.Field("position_calls", 1), AuditLog.Field("exact_position_verified", true),
                AuditLog.Field("target_ownership_verified", true), AuditLog.Field("cursor_restore_requested", false));
            guard();
        }

        private static void MoveOnce(int x, int y, Action beforeMove, Func<int, int, bool> move)
        {
            beforeMove(); // Caller consumes movement consent/records the attempt; no logging or provider call may follow here.
            Need(move(x, y), "POSITION_MOVE_FAILED"); // One movement call, never a retry or automatic restore.
        }

        internal static void RequireIdleInput(IntPtr expectedRoot)
        {
            uint pid, hitPid;
            var thread = NativeMethods.GetWindowThreadProcessId(expectedRoot, out pid);
            Need(NativeMethods.IsWindow(expectedRoot) && NativeMethods.GetForegroundWindow() == expectedRoot && thread != 0,
                "CLICK_TARGET_CHANGED");
            NativeMethods.ScreenPoint cursor;
            Need(NativeMethods.GetPhysicalCursorPos(out cursor), "CLICK_POINTER_MOVED");
            var hit = WindowFromPhysicalPoint(cursor);
            var hitThread = NativeMethods.GetWindowThreadProcessId(hit, out hitPid);
            Need(hit != IntPtr.Zero && NativeMethods.IsWindow(hit) && hitThread != 0 && hitPid == pid &&
                (hit == expectedRoot || NativeMethods.IsChild(expectedRoot, hit)), "CLICK_POINT_WINDOW_CHANGED");
            CheckIdleInput(expectedRoot, thread, hitThread);
        }

        // Read-only: operational activation uses this without requiring the pointer to be over its target.
        internal static bool IsForegroundInputQuiet()
        {
            bool activeMatches, capture, menu, moveSize, available;
            uint flags;
            return DescribeForegroundInput(out activeMatches, out capture, out menu, out moveSize, out flags, out available);
        }

        // Same observation as IsForegroundInputQuiet, with the individual reasons kept for diagnostics. No titles or text.
        internal static bool DescribeForegroundInput(out bool activeMatches, out bool capture, out bool menu,
            out bool moveSize, out uint flags, out bool available)
        {
            activeMatches = capture = menu = moveSize = available = false;
            flags = 0;
            var foreground = NativeMethods.GetForegroundWindow();
            uint pid;
            var thread = NativeMethods.GetWindowThreadProcessId(foreground, out pid);
            if (foreground == IntPtr.Zero || thread == 0 || pid == 0) return false;
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if (!GetGUIThreadInfo(thread, ref info)) return false;
            available = true;
            activeMatches = info.Active == foreground;
            capture = info.Capture != IntPtr.Zero;
            menu = info.MenuOwner != IntPtr.Zero;
            moveSize = info.MoveSize != IntPtr.Zero;
            flags = info.Flags;
            return activeMatches && !capture && !menu && !moveSize && (flags & 0x1E) == 0;
        }

        private static void CheckIdleInput(IntPtr expectedRoot, uint thread, uint hitThread)
        {
            void CheckGui(uint targetThread, bool requireActive)
            {
                var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
                Need(GetGUIThreadInfo(targetThread, ref info), "CLICK_GUI_THREAD_UNAVAILABLE");
                Need((!requireActive || info.Active == expectedRoot) && info.Capture == IntPtr.Zero && info.MenuOwner == IntPtr.Zero &&
                    info.MoveSize == IntPtr.Zero && (info.Flags & 0x1E) == 0, "CLICK_CAPTURE_MENU_OR_MOVE_ACTIVE");
            }
            CheckGui(thread, true);
            if (hitThread != thread) CheckGui(hitThread, false);
            foreach (var key in HeldKeys)
                Need(GetAsyncKeyState(key) >= 0, "CLICK_BUTTON_OR_MODIFIER_HELD"); // High bit only; physical button states.
        }

        internal static void ClickOnce(System.Windows.Point expectedPoint, int expectedPid, IntPtr expectedRoot,
            AuditLog log, Action guard, Action commit, Result result)
        {
            Need(SendMetadataProbe.IsNativePoint(expectedPoint) && expectedPid > 0 && expectedRoot != IntPtr.Zero &&
                log != null && guard != null && commit != null && result != null, "CLICK_REQUEST_INVALID");
            Need(!result.Returned && !result.ReleaseAttempted && result.Inserted == -1 && result.DownInserted == -1 && result.ReleaseInserted == -1,
                "CLICK_RESULT_USED");
            var size = Marshal.SizeOf(typeof(Input));
            Need(size == (IntPtr.Size == 8 ? 40 : 28), "CLICK_INPUT_LAYOUT_INVALID");
            var swapped = GetSystemMetrics(23) != 0; // SM_SWAPBUTTON: primary physical button may be right.
            var down = new[] { Button(swapped, false) };
            var release = new[] { Button(swapped, true) }; // Allocate the one release packet before consent commit.
            InsertEvents insert = InsertNative;
            IntPtr originalHit = IntPtr.Zero;
            uint originalThread = 0, originalHitThread = 0;
            var holdClock = new Stopwatch();

            void Check()
            {
                guard();
                uint pid;
                var thread = NativeMethods.GetWindowThreadProcessId(expectedRoot, out pid);
                Need(NativeMethods.IsWindow(expectedRoot) && NativeMethods.GetForegroundWindow() == expectedRoot &&
                    thread != 0 && pid == expectedPid && (originalThread == 0 || originalThread == thread), "CLICK_TARGET_CHANGED");
                originalThread = thread;
                NativeMethods.ScreenPoint cursor;
                Need(NativeMethods.GetPhysicalCursorPos(out cursor) && cursor.X == expectedPoint.X && cursor.Y == expectedPoint.Y,
                    "CLICK_POINTER_MOVED");
                var hit = WindowFromPhysicalPoint(cursor);
                var hitThread = NativeMethods.GetWindowThreadProcessId(hit, out pid);
                Need(hit != IntPtr.Zero && NativeMethods.IsWindow(hit) &&
                    (hit == expectedRoot || NativeMethods.IsChild(expectedRoot, hit)) &&
                    hitThread != 0 && pid == expectedPid && (originalHit == IntPtr.Zero || originalHit == hit) &&
                    (originalHitThread == 0 || originalHitThread == hitThread), "CLICK_POINT_WINDOW_CHANGED");
                originalHit = hit;
                originalHitThread = hitThread;
                result.HitThreadSameAsRoot = thread == hitThread;
                CheckIdleInput(expectedRoot, thread, hitThread);
                Need((GetSystemMetrics(23) != 0) == swapped, "CLICK_PRIMARY_BUTTON_CHANGED");
                Need(NativeMethods.GetForegroundWindow() == expectedRoot &&
                    NativeMethods.GetPhysicalCursorPos(out cursor) && cursor.X == expectedPoint.X && cursor.Y == expectedPoint.Y,
                    "CLICK_TARGET_OR_POINTER_CHANGED");
                guard();
            }

            string Relation(IntPtr handle)
            {
                if (handle == IntPtr.Zero) return "NONE";
                uint pid;
                if (!NativeMethods.IsWindow(handle) || NativeMethods.GetWindowThreadProcessId(handle, out pid) == 0 || pid != expectedPid)
                    return "OTHER";
                return handle == expectedRoot ? "TARGET_ROOT" : NativeMethods.IsChild(expectedRoot, handle) ? "TARGET_CHILD" : "OTHER";
            }
            void ObserveGui(uint thread)
            {
                var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
                if (!GetGUIThreadInfo(thread, ref info)) { result.GuiUnavailableObserved = true; return; }
                var capture = Relation(info.Capture);
                var targetCapture = capture == "TARGET_ROOT" || capture == "TARGET_CHILD";
                if (targetCapture || !result.TargetCaptureObserved || capture == "OTHER") result.CaptureRelation = capture;
                result.TargetCaptureObserved |= targetCapture; // Preserve evidence even if a later sample shows release.
                result.FocusRelation = Relation(info.Focus);
                Need(capture != "OTHER" && info.MenuOwner == IntPtr.Zero && info.MoveSize == IntPtr.Zero && (info.Flags & 0x1E) == 0,
                    "CLICK_HELD_CAPTURE_OR_MODE_CHANGED");
            }
            void Hold()
            {
                holdClock.Restart();
                try
                {
                    do
                    {
                        guard();
                        result.PressSamples++;
                        NativeMethods.ScreenPoint cursor;
                        var pointerStable = NativeMethods.GetPhysicalCursorPos(out cursor) && cursor.X == expectedPoint.X && cursor.Y == expectedPoint.Y;
                        result.PointerStable = pointerStable;
                        result.ForegroundStable = NativeMethods.GetForegroundWindow() == expectedRoot;
                        uint rootPid, hitPid;
                        var rootThread = NativeMethods.GetWindowThreadProcessId(expectedRoot, out rootPid);
                        var hitThread = NativeMethods.GetWindowThreadProcessId(originalHit, out hitPid);
                        result.TargetStable = NativeMethods.IsWindow(expectedRoot) && NativeMethods.IsWindow(originalHit) &&
                            rootThread == originalThread && hitThread == originalHitThread && rootPid == expectedPid && hitPid == expectedPid &&
                            (originalHit == expectedRoot || NativeMethods.IsChild(expectedRoot, originalHit)) &&
                            pointerStable && WindowFromPhysicalPoint(cursor) == originalHit;
                        result.PrimaryDownObserved |= GetAsyncKeyState(swapped ? 0x02 : 0x01) < 0;
                        Need(result.PointerStable == true && result.ForegroundStable == true && result.TargetStable == true,
                            "CLICK_HELD_TARGET_OR_POINTER_CHANGED");
                        Need((GetSystemMetrics(23) != 0) == swapped, "CLICK_PRIMARY_BUTTON_CHANGED");
                        foreach (var key in HeldKeys)
                            if (key != (swapped ? 0x02 : 0x01))
                                Need(GetAsyncKeyState(key) >= 0, "CLICK_HELD_BUTTON_OR_MODIFIER_CHANGED");
                        ObserveGui(originalThread);
                        if (originalHitThread != originalThread) ObserveGui(originalHitThread);
                        guard();
                        var remaining = PressDurationMilliseconds - holdClock.ElapsedMilliseconds;
                        if (remaining <= 0) break;
                        Thread.Sleep((int)Math.Min(10, remaining));
                    } while (true);
                }
                finally { result.HoldMilliseconds = holdClock.ElapsedMilliseconds; }
            }

            Action hold = Hold; // Allocate the held-phase delegate before committing the one DOWN attempt.
            Check();
            log.Write("INFO", "MOUSE_CLICK_READY", AuditLog.Field("input_events", 2),
                AuditLog.Field("requested_press_ms", PressDurationMilliseconds), AuditLog.Field("hit_thread_same_as_root", result.HitThreadSameAsRoot),
                AuditLog.Field("primary_button_swapped", swapped), AuditLog.Field("cursor_move_events", 0),
                AuditLog.Field("foreground_activation_calls", 0), AuditLog.Field("delivery_verified", false));
            Check();
            // ponytail: global input cannot atomically lock foreground/cursor; abort observed changes and require user inactivity.
            commit(); // Caller performs only its atomic consent commit here. No provider calls or logging follow it.
            PressOnce(result, down, release, size, insert, hold);
        }

        private static Input Button(bool swapped, bool up)
        {
            return new Input { Mouse = new MouseInput { Flags = swapped ? (up ? 0x0010u : 0x0008u) : (up ? 0x0004u : 0x0002u) } };
        }

        private static uint InsertNative(Input[] events, int size, out int error)
        {
            var inserted = SendInput((uint)events.Length, events, size);
            error = inserted == events.Length ? 0 : Marshal.GetLastWin32Error();
            return inserted;
        }

        private static void PressOnce(Result result, Input[] down, Input[] release, int size, InsertEvents insert, Action hold)
        {
            var downReturned = false;
            var upReturned = false;
            try
            {
                int error;
                var inserted = insert(down, size, out error); // Exactly one DOWN packet; no DOWN retries.
                result.DownInserted = checked((int)inserted);
                result.Error = error;
                downReturned = true;
                Need(inserted == 1, inserted == 0 ? "CLICK_NO_EVENTS_INSERTED" : "CLICK_INVALID_DOWN_COUNT");
                hold(); // Native-only observations; no provider calls or log I/O while the button is held.
            }
            finally
            {
                try
                {
                    if (!downReturned || result.DownInserted != 0)
                    {
                        // Release once after known DOWN or unknown DOWN exception, regardless of Stop/focus/cursor changes.
                        result.ReleaseAttempted = true;
                        int error;
                        result.ReleaseInserted = checked((int)insert(release, size, out error));
                        result.ReleaseError = error;
                        upReturned = true;
                    }
                }
                finally
                {
                    result.Returned = downReturned && (!result.ReleaseAttempted || upReturned);
                    result.Inserted = result.Returned ? result.DownInserted + (result.ReleaseAttempted ? result.ReleaseInserted : 0) : -1;
                }
            }
            Need(result.ReleaseInserted == 1, "CLICK_RELEASE_NOT_INSERTED");
        }

        internal static void RunSelfTest()
        {
            foreach (var moveSucceeds in new[] { false, true })
            {
                var attempts = 0;
                var callbacks = 0;
                var correctOrder = false;
                var rejected = false;
                try
                {
                    MoveOnce(-10, 20, () => callbacks++, (x, y) =>
                    {
                        attempts++;
                        correctOrder = callbacks == 1 && x == -10 && y == 20;
                        return moveSucceeds;
                    });
                }
                catch (MonitorException) { rejected = true; }
                Need(correctOrder && callbacks == 1 && attempts == 1 && rejected != moveSucceeds, "POSITION_SELF_TEST_ONCE");
            }
            var cancelledMoves = 0;
            try
            {
                MoveOnce(0, 0, () => { throw new MonitorException("SEND_CANCELLED", "Synthetic cancellation."); },
                    (x, y) => { cancelledMoves++; return true; });
            }
            catch (MonitorException) { }
            Need(cancelledMoves == 0, "POSITION_SELF_TEST_CANCEL");
            var failedMoves = 0;
            try
            {
                MoveOnce(0, 0, () => { }, (x, y) => { failedMoves++; throw new InvalidOperationException("Synthetic movement failure."); });
            }
            catch (InvalidOperationException) { }
            Need(failedMoves == 1, "POSITION_SELF_TEST_EXCEPTION");
            var size = Marshal.SizeOf(typeof(Input));
            Need(size == (IntPtr.Size == 8 ? 40 : 28) && Marshal.OffsetOf(typeof(Input), "Mouse").ToInt32() == (IntPtr.Size == 8 ? 8 : 4) &&
                Marshal.SizeOf(typeof(GuiThreadInfo)) == (IntPtr.Size == 8 ? 72 : 48) && PressDurationMilliseconds == 120, "CLICK_SELF_TEST_LAYOUT");
            // OperationalTarget reuses this exact list; a change here changes the idle contract too.
            var expectedHeld = new[] { 0x01, 0x02, 0x04, 0x05, 0x06, 0x10, 0x11, 0x12, 0x5B, 0x5C };
            Need(HeldKeys.Length == expectedHeld.Length, "CLICK_SELF_TEST_HELD_KEYS");
            for (var index = 0; index < expectedHeld.Length; index++)
                Need(HeldKeys[index] == expectedHeld[index], "CLICK_SELF_TEST_HELD_KEYS");
            foreach (var swapped in new[] { false, true })
            {
                var down = Button(swapped, false);
                var up = Button(swapped, true);
                Need(down.Type == 0 && up.Type == 0 && down.Mouse.Flags == (swapped ? 8u : 2u) &&
                    up.Mouse.Flags == (swapped ? 16u : 4u) && down.Mouse.X == 0 && down.Mouse.Y == 0 &&
                    down.Mouse.Data == 0 && down.Mouse.Time == 0 && down.Mouse.ExtraInfo == UIntPtr.Zero, "CLICK_SELF_TEST_BUTTONS");
                foreach (var downCount in new[] { -1, 0, 1 })
                foreach (var upCount in new[] { -1, 0, 1 })
                {
                    var result = new Result();
                    var calls = 0;
                    var holds = 0;
                    var packetsValid = true;
                    uint Insert(Input[] events, int cbSize, out int error)
                    {
                        calls++;
                        packetsValid &= cbSize == size && calls <= 2 && events.Length == 1 && events[0].Type == 0 &&
                            events[0].Mouse.Flags == (calls == 1 ? down.Mouse.Flags : up.Mouse.Flags) &&
                            events[0].Mouse.X == 0 && events[0].Mouse.Y == 0 && events[0].Mouse.Data == 0 &&
                            events[0].Mouse.Time == 0 && events[0].Mouse.ExtraInfo == UIntPtr.Zero;
                        var count = calls == 1 ? downCount : upCount;
                        error = count == 0 ? (calls == 1 ? 5 : 6) : 0;
                        if (count < 0) throw new InvalidOperationException("Synthetic edge exception.");
                        return (uint)count;
                    }
                    var rejected = false;
                    try { PressOnce(result, new[] { down }, new[] { up }, size, Insert, () => holds++); }
                    catch (Exception) { rejected = true; }
                    var returned = downCount >= 0 && (downCount == 0 || upCount >= 0);
                    Need(packetsValid && rejected == (downCount != 1 || upCount != 1) && holds == (downCount == 1 ? 1 : 0) &&
                        calls == (downCount == 0 ? 1 : 2) && result.DownInserted == downCount && result.Returned == returned &&
                        result.ReleaseAttempted == (downCount != 0) && result.ReleaseInserted == (downCount != 0 ? upCount : -1) &&
                        result.Inserted == (returned ? downCount + (downCount == 0 ? 0 : upCount) : -1) &&
                        result.Error == (downCount == 0 ? 5 : 0) && result.ReleaseError == (downCount != 0 && upCount == 0 ? 6 : 0),
                        "CLICK_SELF_TEST_EDGE_RESULTS");
                }
            }
            foreach (var cancelHold in new[] { false, true })
            {
                var result = new Result();
                var calls = 0;
                uint Insert(Input[] events, int cbSize, out int error)
                {
                    error = 0;
                    calls++;
                    return 1;
                }
                var rejected = false;
                try
                {
                    PressOnce(result, new[] { Button(false, false) }, new[] { Button(false, true) }, size, Insert, () =>
                    {
                        if (cancelHold) throw new MonitorException("SEND_CANCELLED", "Synthetic cancellation.");
                        throw new InvalidOperationException("Synthetic held observation failure.");
                    });
                }
                catch (Exception) { rejected = true; }
                Need(rejected && calls == 2 && result.Returned && result.Inserted == 2 && result.DownInserted == 1 &&
                    result.ReleaseAttempted && result.ReleaseInserted == 1, "CLICK_SELF_TEST_HELD_FAILURE_RELEASE");
            }
        }

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new MonitorException(reason, "Supervised mouse click rejected: " + reason + ".");
        }

        // Win7-compatible user32 entry points. INPUT contains no movement, keyboard or absolute-coordinate events.
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr WindowFromPhysicalPoint(NativeMethods.ScreenPoint point);
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setphysicalcursorpos (Vista+)
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetPhysicalCursorPos(int x, int y);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    }
}
