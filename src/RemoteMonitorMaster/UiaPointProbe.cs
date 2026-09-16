using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace RemoteMonitorMaster
{
    internal static class UiaPointProbe
    {
        private const string FailureCallKey = "RemoteMonitorMaster.UiaPointCall";

        internal sealed class PathNode
        {
            internal readonly int Depth, NativeHwnd, ControlTypeId;
            internal readonly string RuntimeId;
            internal readonly bool ControlTypeAvailable;
            internal PathNode(int depth, string runtimeId, int nativeHwnd, int controlTypeId)
            {
                Depth = depth; RuntimeId = runtimeId; NativeHwnd = nativeHwnd; ControlTypeId = controlTypeId;
                ControlTypeAvailable = controlTypeId != 0;
            }
        }

        internal sealed class CaptureResult
        {
            internal readonly int[] RuntimeId;
            internal readonly IReadOnlyList<PathNode> Path;
            internal readonly bool InvokeAvailable;
            internal readonly bool? Enabled, Offscreen, PointerInside;
            internal CaptureResult(int[] runtimeId, List<PathNode> path, bool invokeAvailable, bool? enabled, bool? offscreen, bool? pointerInside)
            {
                RuntimeId = runtimeId; Path = path.AsReadOnly(); InvokeAvailable = invokeAvailable;
                Enabled = enabled; Offscreen = offscreen; PointerInside = pointerInside;
            }
        }

        // The caller supplies the shared whole-capture deadline/cancellation/foreground guard.
        // FromPoint uses physical coordinates and may return an ancestor, not a clickable control:
        // https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationelement.frompoint
        internal static CaptureResult Capture(Point point, int expectedPid, IntPtr expectedRoot, string expectedRootRuntimeId, Action guard,
            ProbeSnapshot snapshot, bool automaticSendSelection = false)
        {
            Need(SendMetadataProbe.IsNativePoint(point) && expectedPid > 0 && expectedRoot != IntPtr.Zero &&
                !string.IsNullOrEmpty(expectedRootRuntimeId) && guard != null, "POINT_REQUEST_INVALID");
            Need(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "PROBE_REQUIRES_MTA");
            var call = "SnapshotScope";
            T Read<T>(string name, Func<T> read) { call = name; guard(); var value = read(); guard(); return value; }
            try
            {
                guard();
                if (automaticSendSelection) ReadOnlyPair.SelectAutomaticSend(snapshot);
                else ReadOnlyPair.SelectHoveredSend(snapshot);
                var snapshotRoot = snapshot.Nodes.Single(n => n.Node == 1);
                Need(snapshot.Process.ProcessId == expectedPid && snapshotRoot.Identity.RuntimeId == expectedRootRuntimeId &&
                    new IntPtr(snapshotRoot.NativeHwnd) == expectedRoot, "POINT_SNAPSHOT_MISMATCH");
                var element = Read("FromPoint", () => AutomationElement.FromPoint(point)); // Exactly one lookup, never a fallback.
                Need(element != null, "POINT_ELEMENT_UNAVAILABLE");
                int WindowHandle(AutomationElement target)
                {
                    var pid = Read("ProcessId", () => target.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty, true));
                    Need(pid is int && (int)pid == expectedPid, "POINT_FOREIGN_PROCESS");
                    var password = Read("IsPassword", () => target.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true));
                    Need(password is bool && !(bool)password, "POINT_PROTECTED_OR_UNAVAILABLE");
                    // Only HWND keeps UIA's documented default 0 for virtual elements; security flags do not use defaults.
                    var handle = Read("NativeWindowHandle", () => target.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty, false));
                    Need(handle is int, "POINT_NATIVE_WINDOW_UNAVAILABLE");
                    var native = new IntPtr((int)handle);
                    if (native != IntPtr.Zero)
                    {
                        call = "NativeWindowScope";
                        guard();
                        uint nativePid;
                        Need(NativeMethods.IsWindow(native) &&
                            (native == expectedRoot || NativeMethods.IsChild(expectedRoot, native)) &&
                            NativeMethods.GetWindowThreadProcessId(native, out nativePid) != 0 && nativePid == expectedPid,
                            "POINT_NATIVE_WINDOW_OUTSIDE_TARGET");
                        guard();
                    }
                    return (int)handle;
                }
                int[] RuntimeId(AutomationElement target)
                {
                    var id = Read("GetRuntimeId", target.GetRuntimeId);
                    Need(ValidRuntimeId(id), "POINT_RUNTIME_ID_INVALID");
                    return id;
                }
                var originalWindow = WindowHandle(element);
                var originalRuntime = RuntimeId(element);
                var walker = Read("RawViewWalker", () => TreeWalker.RawViewWalker);
                Need(walker != null, "POINT_WALKER_UNAVAILABLE");
                var cursor = element;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var path = new List<PathNode>();
                var rootFound = false;
                for (var depth = 0; depth <= 32; depth++)
                {
                    var handle = WindowHandle(cursor);
                    var runtime = Format(RuntimeId(cursor));
                    Need(visited.Add(runtime), "POINT_ANCESTRY_CYCLE");
                    var controlType = Read("ControlType", () => cursor.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true));
                    path.Add(new PathNode(depth, runtime, handle, ObserveControlTypeId(controlType)));
                    if (new IntPtr(handle) == expectedRoot)
                    {
                        Need(runtime == expectedRootRuntimeId, "POINT_ROOT_IDENTITY_CHANGED");
                        rootFound = true;
                        break;
                    }
                    Need(depth < 32, "POINT_ANCESTRY_LIMIT");
                    cursor = Read("GetParent", () => walker.GetParent(cursor));
                    Need(cursor != null, "POINT_ROOT_NOT_FOUND");
                }
                // Same-object RuntimeId may be cached. This is a consistency guard, not a fresh temporal observation.
                Need(rootFound && WindowHandle(element) == originalWindow &&
                    RuntimeId(element).SequenceEqual(originalRuntime), "POINT_ELEMENT_CHANGED");
                T ReadPoint<T>(string name, Func<T> read)
                {
                    Need(WindowHandle(element) == originalWindow, "POINT_ELEMENT_CHANGED");
                    var value = Read(name, read);
                    Need(WindowHandle(element) == originalWindow, "POINT_ELEMENT_CHANGED");
                    return value;
                }
                // Metadata on the original point object only. The ordinary Capture never executes or retains its pattern.
                var enabled = ObserveBoolean(ReadPoint("IsEnabled", () => element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true)));
                var offscreen = ObserveBoolean(ReadPoint("IsOffscreen", () => element.GetCurrentPropertyValue(AutomationElement.IsOffscreenProperty, true)));
                var inside = ObserveBounds(ReadPoint("BoundingRectangle", () => element.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty, true)), point);
                var invoke = ReadPoint("InvokePattern", () =>
                {
                    object pattern;
                    var available = element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern);
                    Need(available ? pattern is InvokePattern : pattern == null, "POINT_PATTERN_INVALID");
                    return available;
                });
                // Validate the exact parent path again after metadata reads; no new point lookup or child enumeration.
                cursor = element;
                foreach (var node in path)
                {
                    Need(cursor != null && WindowHandle(cursor) == node.NativeHwnd &&
                        Format(RuntimeId(cursor)) == node.RuntimeId, "POINT_PARENT_PATH_CHANGED");
                    if (node.Depth + 1 < path.Count) cursor = Read("GetParent", () => walker.GetParent(cursor));
                }
                Need(WindowHandle(element) == originalWindow && RuntimeId(element).SequenceEqual(originalRuntime), "POINT_ELEMENT_CHANGED");
                guard();
                return new CaptureResult(originalRuntime, path, invoke, enabled, offscreen, inside);
            }
            catch (Exception ex)
            {
                ex.Data[FailureCallKey] = call; // Fixed labels only, not provider text or exception messages.
                throw;
            }
        }

        internal static bool? ObserveBoolean(object value)
        {
            if (ReferenceEquals(value, AutomationElement.NotSupported)) return null;
            Need(value is bool, "POINT_BOOLEAN_INVALID");
            return (bool)value;
        }

        internal static bool? ObserveBounds(object value, Point point)
        {
            if (ReferenceEquals(value, AutomationElement.NotSupported)) return null;
            Need(value is Rect, "POINT_BOUNDS_INVALID");
            return ReadOnlyProbe.ContainsPoint((Rect)value, point);
        }

        internal static string CompareRuntimeId(int[] runtimeId, ProbeSnapshot snapshot, bool automaticSendSelection = false)
        {
            var selected = automaticSendSelection ? ReadOnlyPair.SelectAutomaticSend(snapshot) : ReadOnlyPair.SelectHoveredSend(snapshot);
            var comparison = CompareRuntimeId(runtimeId, selected.Identity.RuntimeId, snapshot.Nodes.Select(n => n.Identity.RuntimeId));
            if (comparison != "MIRROR_OR_OTHER") return comparison;
            var observed = Format(runtimeId);
            var byNode = snapshot.Nodes.ToDictionary(n => n.Node);
            for (var parent = selected.Parent; parent != 0; parent = byNode[parent].Parent)
                if (byNode[parent].Identity.RuntimeId == observed) return "ANCESTOR_CONTAINER";
            return comparison;
        }

        internal static string CompareRuntimeId(int[] runtimeId, string canonicalRuntimeId, IEnumerable<string> snapshotRuntimeIds)
        {
            Need(ValidRuntimeId(runtimeId) && !string.IsNullOrEmpty(canonicalRuntimeId) && snapshotRuntimeIds != null,
                "POINT_RUNTIME_ID_INVALID");
            var candidates = snapshotRuntimeIds.Take(ReadOnlyProbe.MaxNodes + 1).ToArray();
            Need(candidates.Length > 0 && candidates.Length <= ReadOnlyProbe.MaxNodes && candidates.All(id => !string.IsNullOrEmpty(id)) &&
                candidates.Contains(canonicalRuntimeId, StringComparer.Ordinal), "POINT_RUNTIME_ID_INVALID");
            var observed = Format(runtimeId);
            if (observed == canonicalRuntimeId) return "CANONICAL";
            return candidates.Contains(observed, StringComparer.Ordinal) ? "MIRROR_OR_OTHER" : "NOT_IN_SNAPSHOT";
        }

        internal static string FailureCall(Exception exception)
        {
            var call = exception?.Data[FailureCallKey] as string;
            switch (call)
            {
                case "FromPoint": case "ProcessId": case "IsPassword": case "NativeWindowHandle":
                case "NativeWindowScope": case "GetRuntimeId": case "RawViewWalker": case "GetParent":
                case "ControlType": case "SnapshotScope": case "IsEnabled": case "IsOffscreen":
                case "BoundingRectangle": case "InvokePattern": return call;
                default: return "NONE";
            }
        }

        internal static int ObserveControlTypeId(object value)
        {
            // Extra descriptive metadata only: preserve exact NotSupported, never substitute default Custom.
            // Null, malformed values and API exceptions still reject; security-property guards are unchanged.
            if (ReferenceEquals(value, AutomationElement.NotSupported)) return 0;
            var controlType = value as ControlType;
            Need(controlType != null && controlType.Id > 0, "POINT_CONTROL_TYPE_UNAVAILABLE");
            return controlType.Id;
        }

        internal static void RunSelfTest()
        {
            Need(ObserveControlTypeId(AutomationElement.NotSupported) == 0 &&
                ObserveControlTypeId(ControlType.Button) == ControlType.Button.Id, "POINT_SELF_TEST_CONTROL_TYPE");
            foreach (var invalid in new object[] { null, 50000, "Button" })
            {
                try { ObserveControlTypeId(invalid); }
                catch (MonitorException ex) { Need(ex.ReasonCode == "POINT_CONTROL_TYPE_UNAVAILABLE", "POINT_SELF_TEST_REASON"); continue; }
                throw new InvalidOperationException("UIA point metadata accepted an invalid ControlType.");
            }
            Need(FailureCall(null) == "NONE", "POINT_SELF_TEST_FAILURE_CALL");
            var failure = new Exception();
            foreach (var call in new[] { "GetRuntimeId", "ControlType", "IsEnabled", "IsOffscreen", "BoundingRectangle", "InvokePattern" })
            {
                failure.Data[FailureCallKey] = call;
                Need(FailureCall(failure) == call, "POINT_SELF_TEST_FAILURE_CALL");
            }
            failure.Data[FailureCallKey] = "private provider text";
            Need(FailureCall(failure) == "NONE", "POINT_SELF_TEST_FAILURE_CALL");
            var ids = new[] { "42,100,2", "42,100,3" };
            var manyIds = Enumerable.Range(1, ReadOnlyProbe.MaxNodes).Select(i => "42,100," + i).ToArray();
            Need(CompareRuntimeId(new[] { 42, 100, 2 }, ids[0], manyIds.Take(401)) == "CANONICAL" &&
                CompareRuntimeId(new[] { 42, 100, 2 }, ids[0], manyIds) == "CANONICAL", "POINT_SELF_TEST_LARGE_SNAPSHOT");
            try
            {
                CompareRuntimeId(new[] { 42, 100, 2 }, ids[0], manyIds.Concat(new[] { "42,100,99999" }));
                throw new InvalidOperationException("UIA point comparison accepted an oversized snapshot.");
            }
            catch (MonitorException ex) { Need(ex.ReasonCode == "POINT_RUNTIME_ID_INVALID", "POINT_SELF_TEST_REASON"); }
            Need(CompareRuntimeId(new[] { 42, 100, 2 }, ids[0], ids) == "CANONICAL" &&
                CompareRuntimeId(new[] { 42, 100, 3 }, ids[0], ids) == "MIRROR_OR_OTHER" &&
                CompareRuntimeId(new[] { 42, 100, -4 }, ids[0], ids) == "NOT_IN_SNAPSHOT", "POINT_SELF_TEST_COMPARISON");
            foreach (var invalid in new[] { null, new int[0], new int[65] })
            {
                try { CompareRuntimeId(invalid, ids[0], ids); }
                catch (MonitorException ex) { Need(ex.ReasonCode == "POINT_RUNTIME_ID_INVALID", "POINT_SELF_TEST_REASON"); continue; }
                throw new InvalidOperationException("UIA point comparison accepted an invalid runtime ID.");
            }
            var testPoint = new Point(1, 1);
            Need(ObserveBoolean(true) == true && ObserveBoolean(false) == false && ObserveBoolean(AutomationElement.NotSupported) == null &&
                ObserveBounds(new Rect(0, 0, 2, 2), testPoint) == true && ObserveBounds(Rect.Empty, testPoint) == false &&
                ObserveBounds(new Rect(5, 5, 2, 2), testPoint) == false && ObserveBounds(AutomationElement.NotSupported, testPoint) == null,
                "POINT_SELF_TEST_CAPABILITIES");
            foreach (var invalid in new Action[] {
                () => ObserveBoolean(null), () => ObserveBoolean(1), () => ObserveBoolean("True"),
                () => ObserveBounds(null, testPoint), () => ObserveBounds("private", testPoint) })
            {
                try { invalid(); }
                catch (MonitorException) { continue; }
                throw new InvalidOperationException("Point capabilities accepted malformed metadata.");
            }
        }

        private static bool ValidRuntimeId(int[] value) { return value != null && value.Length > 0 && value.Length <= 64; }
        internal static string Format(int[] id)
        {
            Need(ValidRuntimeId(id), "POINT_RUNTIME_ID_INVALID");
            return string.Join(",", id.Select(n => n.ToString(CultureInfo.InvariantCulture)));
        }
        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new MonitorException(reason, "Read-only UIA point comparison rejected: " + reason + ".");
        }
    }
}
