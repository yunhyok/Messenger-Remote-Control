using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using Accessibility;

namespace RemoteMonitorMaster
{
    internal static class SendMetadataProbe
    {
        private const int ProtectedState = 0x20000000;
        private const string SendFailureCallKey = "MsaaSendCall";

        // Keep the original native object alive for metadata rechecks; MSAA never executes an action here.
        internal static void WithValidatedPoint(System.Windows.Point pointer, int expectedPid, IntPtr expectedRoot,
            AuditLog log, Action guard, Action<Action> withinValidatedPoint)
        {
            IAccessible accessible = null;
            var call = "Request";
            try
            {
                Need(IsNativePoint(pointer) && expectedPid > 0 && expectedRoot != IntPtr.Zero &&
                    log != null && guard != null && withinValidatedPoint != null, "MSAA_SEND_REQUEST_INVALID");
                Need(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "PROBE_REQUIRES_MTA");
                T Read<T>(string name, Func<T> read) { call = name; guard(); var value = read(); guard(); return value; }
                var point = new NativeMethods.ScreenPoint { X = (int)pointer.X, Y = (int)pointer.Y };
                object child = null;
                var result = Read("AccessibleObjectFromPoint", () => AccessibleObjectFromPoint(point, out accessible, out child));
                Need(result == 0 && accessible != null, "MSAA_POINT_UNAVAILABLE");
                Need(child is int && (int)child >= 0, "MSAA_CHILD_ID_INVALID");
                IntPtr originalWindow = IntPtr.Zero;

                void CheckWindow()
                {
                    IntPtr currentWindow = IntPtr.Zero;
                    Need(Read("WindowFromAccessibleObject", () => WindowFromAccessibleObject(accessible, out currentWindow)) == 0 &&
                        currentWindow != IntPtr.Zero, "MSAA_WINDOW_UNAVAILABLE");
                    call = "NativeWindowScope";
                    guard();
                    uint pid;
                    Need(NativeMethods.IsWindow(currentWindow) &&
                        (currentWindow == expectedRoot || NativeMethods.IsChild(expectedRoot, currentWindow)) &&
                        NativeMethods.GetWindowThreadProcessId(currentWindow, out pid) != 0 && pid == expectedPid,
                        "MSAA_POINT_OUTSIDE_TARGET");
                    Need(originalWindow == IntPtr.Zero || currentWindow == originalWindow, "MSAA_WINDOW_CHANGED");
                    originalWindow = currentWindow;
                    guard();
                }
                int ReadState()
                {
                    CheckWindow();
                    var state = Read("get_accState", () => accessible.get_accState(child));
                    CheckWindow();
                    Need(state is int && IsSendStateUsable((int)state), "MSAA_SEND_STATE_UNUSABLE");
                    return (int)state;
                }
                T ReadMetadata<T>(string name, Func<T> read)
                {
                    var before = ReadState();
                    var value = Read(name, read);
                    Need(ReadState() == before, "MSAA_STATE_CHANGED");
                    return value;
                }
                int[] baselineBounds = null;
                string baselineAction = null;
                int lastState = 0;
                void Recheck()
                {
                    var role = ReadMetadata("get_accRole", () => accessible.get_accRole(child));
                    Need(role is int && (int)role == 30, "MSAA_SEND_LINK_REQUIRED");
                    var action = ReadMetadata("get_accDefaultAction", () => accessible.get_accDefaultAction(child));
                    var classification = ClassifyDefaultAction(action);
                    Need(classification == "CLICK_LIKE" || classification == "SEND_LIKE" || classification == "ACTIVATE_LIKE",
                        "MSAA_SEND_ACTION_UNSUPPORTED");
                    Need(baselineAction == null || classification == baselineAction, "MSAA_SEND_ACTION_CHANGED");
                    var bounds = ReadMetadata("accLocation", () =>
                    {
                        int left, top, width, height;
                        accessible.accLocation(out left, out top, out width, out height, child);
                        return new[] { left, top, width, height };
                    });
                    Need(ContainsMsaaPoint(bounds[0], bounds[1], bounds[2], bounds[3], pointer), "MSAA_SEND_POINT_OUTSIDE_BOUNDS");
                    Need(baselineBounds == null || baselineBounds.SequenceEqual(bounds), "MSAA_SEND_BOUNDS_CHANGED");
                    lastState = ReadState();
                    baselineBounds = bounds;
                    baselineAction = classification;
                    guard();
                }

                Recheck();
                log.Write("INFO", "MSAA_POINT_READY", AuditLog.Field("child_id", (int)child),
                    AuditLog.Field("native_hwnd", "0x" + originalWindow.ToInt64().ToString("X", CultureInfo.InvariantCulture)), AuditLog.Field("role_id", 30),
                    AuditLog.Field("state_bits", Hex(lastState)), AuditLog.Field("action_classification", baselineAction),
                    AuditLog.Field("pointer_inside_bounds", true), AuditLog.Field("uia_msaa_identity_verified", false),
                    AuditLog.Field("conversation_identity_verified", false), AuditLog.Field("automatic_send_allowed", false));
                // The caller rechecks root/input/this metadata, then enters its separately guarded mouse-click route.
                call = "ValidatedPointCallback";
                withinValidatedPoint(Recheck);
            }
            catch (Exception ex)
            {
                ex.Data[SendFailureCallKey] = call; // Fixed labels only, never provider strings.
                throw;
            }
            finally
            {
                if (accessible != null && Marshal.IsComObject(accessible))
                {
                    try { Marshal.ReleaseComObject(accessible); } catch { }
                }
            }
        }

        internal static string FailureCall(Exception exception)
        {
            var call = exception?.Data[SendFailureCallKey] as string;
            switch (call)
            {
                case "AccessibleObjectFromPoint": case "WindowFromAccessibleObject": case "NativeWindowScope":
                case "get_accState": case "get_accRole": case "get_accDefaultAction": case "accLocation": return call;
                default: return "NONE";
            }
        }

        internal static bool IsSendStateUsable(int state)
        {
            // MSAA unavailable, invisible, offscreen and protected states prohibit this one supervised action.
            return state >= 0 && (state & (0x00000001 | 0x00008000 | 0x00010000 | ProtectedState)) == 0;
        }

        internal static bool ContainsMsaaPoint(int left, int top, int width, int height, System.Windows.Point point)
        {
            return IsNativePoint(point) && width > 0 && height > 0 && point.X >= left && point.Y >= top &&
                point.X < (long)left + width && point.Y < (long)top + height;
        }

        public static string Capture(IntPtr window, AuditLog log, System.Windows.Point pointer, Func<bool> stopRequested)
        {
            var clock = Stopwatch.StartNew();
            IAccessible accessible = null;
            string property = "request";
            string currentPhase = "REQUEST";
            try
            {
                Need(log != null && stopRequested != null, "META_REQUEST_INVALID");
                Need(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "PROBE_REQUIRES_MTA");
                Need(ReadOnlyProbe.IsValidRequest("SEND_METADATA", null, pointer) && IsNativePoint(pointer), "META_POINTER_INVALID");
                ProcessIdentity process = null;
                string guardFailure = null;
                bool GuardedStop()
                {
                    if (guardFailure != null) return true;
                    if (stopRequested()) guardFailure = "META_CANCELLED";
                    else if (clock.Elapsed >= TimeSpan.FromSeconds(15)) guardFailure = "META_TIME_LIMIT";
                    else if (window == IntPtr.Zero || !NativeMethods.IsWindow(window)) guardFailure = "META_NO_TARGET";
                    else if (NativeMethods.GetForegroundWindow() != window) guardFailure = "META_FOREGROUND_CHANGED";
                    else if (process != null)
                    {
                        uint pid;
                        if (NativeMethods.GetWindowThreadProcessId(window, out pid) == 0 || pid != process.ProcessId)
                            guardFailure = "META_PROCESS_CHANGED";
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
                void Phase(string phase, string status)
                {
                    currentPhase = phase;
                    Alive();
                    log.Write("INFO", "SEND_METADATA_PHASE", AuditLog.Field("phase", phase), AuditLog.Field("status", status),
                        AuditLog.Field("elapsed_ms", clock.ElapsedMilliseconds));
                    Alive();
                }

                log.Write("INFO", "SEND_METADATA_BEGIN", AuditLog.Field("read_only", true),
                    AuditLog.Field("cooperative_seconds", 15), AuditLog.Field("budget_scope", "WHOLE_CAPTURE"),
                    AuditLog.Field("individual_call_timeout", false));
                Alive();
                process = Read("process", () => ProcessIdentity.Capture(window));
                Need(string.Equals(process.ProcessName, "KI-Messenger", StringComparison.OrdinalIgnoreCase), "PROBE_NOT_KI_MESSENGER");
                Phase("SNAPSHOT", "BEGIN");
                var snapshot = ReadOnlyProbe.CaptureSnapshot(window, log, "SEND_METADATA", null, GuardedStop, pointer);
                Alive();
                var selected = ReadOnlyPair.SelectHoveredSend(snapshot);
                Need(process.Equals(snapshot.Process), "META_PROCESS_CHANGED");
                log.Write("INFO", "SEND_METADATA_UIA", AuditLog.Field("phase", "SNAPSHOT"), AuditLog.Field("document_node", selected.Document),
                    AuditLog.Field("send_node", selected.Node), AuditLog.Field("send_runtime_id", selected.Identity.RuntimeId),
                    AuditLog.Field("uia_msaa_identity_verified", false));
                Phase("SNAPSHOT", "COMPLETE");
                var rootRuntimeId = snapshot.Nodes.Single(n => n.Node == 1).Identity.RuntimeId;

                void CheckRoot()
                {
                    Need(process.Equals(Read("process_recheck", () => ProcessIdentity.Capture(window))), "META_PROCESS_CHANGED");
                    var root = Read("root", () => AutomationElement.FromHandle(window));
                    Need(root != null && Read("root_pid", () => root.Current.ProcessId) == process.ProcessId &&
                        new IntPtr(Read("root_hwnd", () => root.Current.NativeWindowHandle)) == window &&
                        !Read("root_password", () => root.Current.IsPassword), "META_ROOT_CHANGED");
                    Need(log.Fingerprint(Read("root_name", () => root.Current.Name)) == snapshot.RootNameFingerprint, "META_ROOT_CHANGED");
                    Need(UiaPointProbe.Format(Read("root_runtime_id", () => root.GetRuntimeId())) == rootRuntimeId, "META_ROOT_CHANGED");
                }
                Phase("MSAA_METADATA", "BEGIN");
                CheckRoot();
                var point = new NativeMethods.ScreenPoint { X = (int)pointer.X, Y = (int)pointer.Y };
                object child = null;
                var result = Read("AccessibleObjectFromPoint", () => AccessibleObjectFromPoint(point, out accessible, out child));
                log.Write("INFO", "MSAA_POINT_RESULT", AuditLog.Field("hresult", Hex(result)),
                    AuditLog.Field("object_obtained", accessible != null), AuditLog.Field("child_id_is_int32", child is int));
                Need(result == 0 && accessible != null, "MSAA_POINT_UNAVAILABLE");
                Need(child is int && (int)child >= 0, "MSAA_CHILD_ID_INVALID");

                IntPtr accessibleWindow = IntPtr.Zero;
                IntPtr CheckMsaaWindow()
                {
                    IntPtr currentWindow = IntPtr.Zero;
                    Need(Read("WindowFromAccessibleObject", () => WindowFromAccessibleObject(accessible, out currentWindow)) == 0 &&
                        currentWindow != IntPtr.Zero, "MSAA_WINDOW_UNAVAILABLE");
                    uint pid;
                    Need(NativeMethods.IsWindow(currentWindow) &&
                        (currentWindow == window || NativeMethods.IsChild(window, currentWindow)) &&
                        NativeMethods.GetWindowThreadProcessId(currentWindow, out pid) != 0 && pid == process.ProcessId,
                        "MSAA_POINT_OUTSIDE_TARGET");
                    Need(accessibleWindow == IntPtr.Zero || currentWindow == accessibleWindow, "MSAA_WINDOW_CHANGED");
                    Alive();
                    return currentWindow;
                }
                accessibleWindow = CheckMsaaWindow();
                log.Write("INFO", "MSAA_WINDOW", AuditLog.Field("hwnd", "0x" + accessibleWindow.ToInt64().ToString("X", CultureInfo.InvariantCulture)),
                    AuditLog.Field("relation", accessibleWindow == window ? "TARGET_ROOT" : "TARGET_DESCENDANT"),
                    AuditLog.Field("child_id", (int)child), AuditLog.Field("uia_msaa_identity_verified", false));

                int ReadState()
                {
                    CheckMsaaWindow();
                    var state = Read("get_accState", () => accessible.get_accState(child));
                    Need(state is int, "MSAA_STATE_UNAVAILABLE");
                    Need(((int)state & ProtectedState) == 0, "MSAA_PROTECTED");
                    return (int)state;
                }
                var stateBefore = ReadState();
                log.Write("INFO", "MSAA_STATE", AuditLog.Field("phase", "BEFORE"), AuditLog.Field("state_bits", Hex(stateBefore)));
                CheckMsaaWindow();
                var role = Read("get_accRole", () => accessible.get_accRole(child));
                Need(role is int, "MSAA_ROLE_UNAVAILABLE");
                log.Write("INFO", "MSAA_ROLE", AuditLog.Field("role_id", (int)role));
                ReadState(); // Do not read even a default-action string from a newly protected object.
                var action = Read("get_accDefaultAction", () => accessible.get_accDefaultAction(child));
                var stateAfter = ReadState();
                Need(action == null || action.Length <= 256, "MSAA_DEFAULT_ACTION_TOO_LONG");
                CheckRoot();
                CheckMsaaWindow();
                log.Write("INFO", "MSAA_DEFAULT_ACTION", AuditLog.Field("classification", ClassifyDefaultAction(action)),
                    AuditLog.Field("length", action == null ? 0 : action.Length),
                    AuditLog.Field("fingerprint", log.Fingerprint(action)), AuditLog.Field("state_after_bits", Hex(stateAfter)),
                    AuditLog.Field("state_stable", stateBefore == stateAfter), AuditLog.Field("action_executed", false));
                Phase("MSAA_METADATA", "COMPLETE");

                Phase("POINT_CAPABILITIES", "BEGIN");
                CheckRoot();
                var pointStateBefore = ReadState();
                property = "uia_point_capabilities";
                var observation = UiaPointProbe.Capture(pointer, process.ProcessId, window, rootRuntimeId, Alive, snapshot);
                var comparison = UiaPointProbe.CompareRuntimeId(observation.RuntimeId, snapshot);
                Alive();
                CheckRoot();
                CheckMsaaWindow();
                Need(ReadState() == pointStateBefore, "META_MSAA_STATE_CHANGED");
                foreach (var node in observation.Path)
                {
                    Alive();
                    log.Write("INFO", "UIA_POINT_PATH", AuditLog.Field("phase", "POINT_CAPABILITIES"), AuditLog.Field("depth", node.Depth),
                        AuditLog.Field("runtime_id", node.RuntimeId), AuditLog.Field("native_hwnd", Hex(node.NativeHwnd)),
                        AuditLog.Field("control_type_id", node.ControlTypeId), AuditLog.Field("control_type_available", node.ControlTypeAvailable));
                }
                var runtimeId = UiaPointProbe.Format(observation.RuntimeId);
                var matched = snapshot.Nodes.SingleOrDefault(n => n.Identity.RuntimeId == runtimeId);
                Alive();
                log.Write("INFO", "UIA_POINT_COMPARISON", AuditLog.Field("phase", "POINT_CAPABILITIES"), AuditLog.Field("snapshot", "SNAPSHOT"),
                    AuditLog.Field("status", comparison), AuditLog.Field("reason", "NONE"),
                    AuditLog.Field("runtime_id", runtimeId), AuditLog.Field("matching_node", matched == null ? 0 : matched.Node),
                    AuditLog.Field("pointer_candidate", matched != null && matched.PointerInside),
                    AuditLog.Field("comparison_scope", "THIS_SNAPSHOT_ONLY"), AuditLog.Field("uia_msaa_identity_verified", false),
                    AuditLog.Field("clickability_verified", false), AuditLog.Field("conversation_identity_verified", false),
                    AuditLog.Field("send_behavior_verified", false), AuditLog.Field("automatic_send_allowed", false));
                Alive();
                log.Write("INFO", "UIA_POINT_CAPABILITIES", AuditLog.Field("runtime_id", runtimeId),
                    AuditLog.Field("invoke_pattern_available", observation.InvokeAvailable),
                    AuditLog.Field("enabled", (object)observation.Enabled ?? "UNSUPPORTED"),
                    AuditLog.Field("offscreen", (object)observation.Offscreen ?? "UNSUPPORTED"),
                    AuditLog.Field("pointer_inside_bounds", (object)observation.PointerInside ?? "UNSUPPORTED"),
                    AuditLog.Field("elapsed_ms", clock.ElapsedMilliseconds), AuditLog.Field("observations_only", true),
                    AuditLog.Field("action_executed", false), AuditLog.Field("uia_msaa_identity_verified", false),
                    AuditLog.Field("clickability_verified", false), AuditLog.Field("send_behavior_verified", false),
                    AuditLog.Field("conversation_identity_verified", false), AuditLog.Field("automatic_send_allowed", false));
                Phase("POINT_CAPABILITIES", "COMPLETE");
                Alive();
                LogResult(log, "CAPTURED_READ_ONLY", "NONE");
                return "CAPTURED_READ_ONLY - Point: " + comparison + "; Invoke supported: " + observation.InvokeAvailable + "." + Environment.NewLine +
                    "Support is metadata only, NOT a Send test or identity approval. No action was executed." + Environment.NewLine +
                    "No input or action was attempted. Collect the log; do not send or repeat the failed Send test.";
            }
            catch (Exception ex)
            {
                var reason = (ex as MonitorException)?.ReasonCode ?? "META_READ_FAILED";
                try
                {
                    if (log != null)
                    {
                        log.WriteException("SEND_METADATA_READ_FAILED", ex, AuditLog.Field("property", property),
                            AuditLog.Field("phase", currentPhase), AuditLog.Field("elapsed_ms", clock.ElapsedMilliseconds),
                            AuditLog.Field("uia_call", UiaPointProbe.FailureCall(ex)));
                        LogResult(log, "REJECTED", reason);
                    }
                }
                catch { }
                return "REJECTED - " + reason + Environment.NewLine +
                    "Metadata was not completely verified. No input, Invoke, default action or send was attempted. Collect the log.";
            }
            finally
            {
                // The one private native lookup owns one RCW reference; no final-release or COM-object traversal.
                if (accessible != null && Marshal.IsComObject(accessible))
                {
                    try { Marshal.ReleaseComObject(accessible); } catch { }
                }
            }
        }

        private static void LogResult(AuditLog log, string status, string reason)
        {
            log.Write("INFO", "SEND_METADATA_RESULT", AuditLog.Field("status", status), AuditLog.Field("reason", reason),
                AuditLog.Field("read_only", true), AuditLog.Field("invoke_calls", 0), AuditLog.Field("default_action_calls", 0),
                AuditLog.Field("setvalue_calls", 0), AuditLog.Field("uia_msaa_identity_verified", false),
                AuditLog.Field("window_mode_verified", false), AuditLog.Field("conversation_identity_verified", false),
                AuditLog.Field("automatic_send_allowed", false));
        }

        internal static bool IsNativePoint(System.Windows.Point point)
        {
            return point.X >= int.MinValue && point.X <= int.MaxValue && point.Y >= int.MinValue && point.Y <= int.MaxValue &&
                point.X == Math.Truncate(point.X) && point.Y == Math.Truncate(point.Y);
        }

        internal static string ClassifyDefaultAction(string action)
        {
            if (string.IsNullOrEmpty(action)) return "NONE";
            if (action.Length > 256) return "TOO_LONG";
            // Lexical hints only: localized "Press" describes an action, not what it will accomplish.
            switch (action.Trim().ToUpperInvariant())
            {
                case "CLICK": case "PRESS": case "클릭": case "누르기": return "CLICK_LIKE";
                case "SEND": case "전송": case "보내기": return "SEND_LIKE";
                case "SELECT": case "FOCUS": case "선택": return "SELECT_LIKE";
                case "ACTIVATE": case "활성화": return "ACTIVATE_LIKE";
                case "OPEN": case "JUMP": case "열기": case "이동": return "OPEN_LIKE";
                default: return "OTHER";
            }
        }

        internal static void RunSelfTest()
        {
            Need(IsSendStateUsable(0) && IsSendStateUsable(4) &&
                new[] { 1, 0x8000, 0x10000, ProtectedState, -1, int.MinValue }.All(state => !IsSendStateUsable(state)),
                "META_SELF_TEST_SEND_STATE");
            Need(ContainsMsaaPoint(-10, -10, 20, 20, new System.Windows.Point(0, 0)) &&
                !ContainsMsaaPoint(0, 0, 0, 10, new System.Windows.Point(0, 0)) &&
                !ContainsMsaaPoint(0, 0, 10, -1, new System.Windows.Point(0, 0)) &&
                !ContainsMsaaPoint(0, 0, 10, 10, new System.Windows.Point(10, 0)) &&
                !ContainsMsaaPoint(0, 0, 10, 10, new System.Windows.Point(double.NaN, 0)) &&
                ContainsMsaaPoint(int.MaxValue - 1, 0, 10, 10, new System.Windows.Point(int.MaxValue, 0)) &&
                !ContainsMsaaPoint(int.MaxValue - 1, 0, 10, 10, new System.Windows.Point(int.MinValue, 0)),
                "META_SELF_TEST_SEND_BOUNDS");
            var sendFailure = new Exception();
            sendFailure.Data[SendFailureCallKey] = "accLocation";
            Need(FailureCall(sendFailure) == "accLocation" && FailureCall(null) == "NONE", "META_SELF_TEST_SEND_CALL");
            sendFailure.Data[SendFailureCallKey] = "private arbitrary provider text";
            Need(FailureCall(sendFailure) == "NONE", "META_SELF_TEST_SEND_CALL");
            Need(ClassifyDefaultAction(null) == "NONE" && ClassifyDefaultAction("") == "NONE" &&
                ClassifyDefaultAction("Press") == "CLICK_LIKE" && ClassifyDefaultAction("보내기") == "SEND_LIKE" &&
                ClassifyDefaultAction("Select") == "SELECT_LIKE" && ClassifyDefaultAction("Activate") == "ACTIVATE_LIKE" &&
                ClassifyDefaultAction("Jump") == "OPEN_LIKE" &&
                ClassifyDefaultAction("private arbitrary action") == "OTHER" && ClassifyDefaultAction(new string('x', 257)) == "TOO_LONG",
                "META_SELF_TEST_CLASSIFICATION");
            Need(IsNativePoint(new System.Windows.Point(-10, 20)) &&
                !IsNativePoint(new System.Windows.Point(0.5, 20)) && !IsNativePoint(new System.Windows.Point(double.NaN, 0)) &&
                !IsNativePoint(new System.Windows.Point(double.PositiveInfinity, 0)) && !IsNativePoint(new System.Windows.Point((double)int.MaxValue + 1, 0)),
                "META_SELF_TEST_POINT");
        }

        private static string Hex(int value) { return "0x" + unchecked((uint)value).ToString("X8", CultureInfo.InvariantCulture); }
        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new MonitorException(reason, "Read-only metadata rejected: " + reason + ".");
        }

        // oleacc.h: POINT is passed by value; AccessibleObjectFromPoint returns a VARIANT child ID (VT_I4).
        [DllImport("oleacc.dll", ExactSpelling = true)]
        private static extern int AccessibleObjectFromPoint(NativeMethods.ScreenPoint point,
            [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible, [MarshalAs(UnmanagedType.Struct)] out object child);

        [DllImport("oleacc.dll", ExactSpelling = true)]
        private static extern int WindowFromAccessibleObject([MarshalAs(UnmanagedType.Interface)] IAccessible accessible, out IntPtr window);

    }
}
