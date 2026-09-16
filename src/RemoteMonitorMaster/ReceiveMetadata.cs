using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using Accessibility;

namespace RemoteMonitorMaster
{
    // Read-only evidence, never a plain-body eligibility decision or permission to respond.
    internal static class ReceiveMetadata
    {
        private const int TextLimit = 4096;
        private static readonly AutomationProperty[] SecurityProperties = {
            AutomationElement.ProcessIdProperty, AutomationElement.IsPasswordProperty,
            AutomationElement.NativeWindowHandleProperty, AutomationElement.RuntimeIdProperty,
            AutomationElement.ControlTypeProperty
        };
        private static readonly AutomationProperty[] TextProperties = {
            AutomationElement.HelpTextProperty, AutomationElement.ItemTypeProperty,
            AutomationElement.ItemStatusProperty, AutomationElement.LocalizedControlTypeProperty
        };
        private static readonly string[] PropertyLabels = { "HelpText", "ItemType", "ItemStatus", "LocalizedControlType" };

        internal static void Capture(AutomationElement element, ProbeNode node, AuditLog log, Action guard, string phase)
        {
            Need(element != null && node?.Identity != null && node.Node > 0 && node.Identity.ProcessId > 0 &&
                !string.IsNullOrEmpty(node.Identity.RuntimeId) && log != null && guard != null &&
                (phase == "BASELINE" || phase == "AFTER"), "RECEIVE_METADATA_REQUEST_INVALID");
            Need(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "PROBE_REQUIRES_MTA");
            guard();
            var root = NativeMethods.GetForegroundWindow();
            void CheckNative()
            {
                guard();
                uint pid;
                Need(root != IntPtr.Zero && NativeMethods.IsWindow(root) && NativeMethods.GetForegroundWindow() == root &&
                    NativeMethods.GetWindowThreadProcessId(root, out pid) != 0 && pid == node.Identity.ProcessId,
                    "RECEIVE_METADATA_TARGET_CHANGED");
                var native = new IntPtr(node.NativeHwnd);
                Need(native == IntPtr.Zero || (NativeMethods.IsWindow(native) &&
                    (native == root || NativeMethods.IsChild(root, native)) &&
                    NativeMethods.GetWindowThreadProcessId(native, out pid) != 0 && pid == node.Identity.ProcessId),
                    "RECEIVE_METADATA_NATIVE_SCOPE_CHANGED");
                guard();
            }
            void CheckCache(AutomationElement cached)
            {
                Need(cached != null, "RECEIVE_METADATA_CACHE_UNAVAILABLE");
                var pid = cached.GetCachedPropertyValue(AutomationElement.ProcessIdProperty, true);
                var password = cached.GetCachedPropertyValue(AutomationElement.IsPasswordProperty, true);
                // Virtual elements keep UIA's documented native-handle default of zero, as in the existing probe.
                var handle = cached.GetCachedPropertyValue(AutomationElement.NativeWindowHandleProperty, false);
                var runtime = cached.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty, true) as int[];
                var type = cached.GetCachedPropertyValue(AutomationElement.ControlTypeProperty, true);
                Need(pid is int && (int)pid == node.Identity.ProcessId && password is bool && !(bool)password &&
                    handle is int && (int)handle == node.NativeHwnd && runtime != null &&
                    UiaPointProbe.Format(runtime) == node.Identity.RuntimeId &&
                    TypeMatches(type, node.Identity.ControlType), "RECEIVE_METADATA_SCOPE_CHANGED");
            }
            void LogValue(string property, object value, string failure = null, int? hresult = null)
            {
                guard();
                var state = failure ?? ValueStatus(value);
                var text = value as string;
                var bounded = text != null && text.Length <= TextLimit && failure == null;
                log.Write("INFO", "RECEIVE_METADATA_PROPERTY", AuditLog.Field("phase", phase),
                    AuditLog.Field("node", node.Node), AuditLog.Field("parent_node", node.Parent),
                    AuditLog.Field("document_node", node.Document), AuditLog.Field("property", property),
                    AuditLog.Field("status", state), AuditLog.Field("length", text?.Length ?? -1),
                    AuditLog.Field("fingerprint", bounded ? log.Fingerprint(text) : "NOT_COMPUTED"),
                    AuditLog.Field("lexical_hint", bounded ? (property == "MSAA.DefaultAction" ?
                        SendMetadataProbe.ClassifyDefaultAction(text) : Classify(text)) : "UNAVAILABLE"),
                    AuditLog.Field("hresult", hresult.HasValue ? "0x" + hresult.Value.ToString("X8") : "NONE"),
                    AuditLog.Field("plain_body_verified", false));
                guard();
            }

            // Two element-only bulk reads, never a native call for each optional property or a subtree scan.
            // https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/caching-in-ui-automation-clients
            CheckNative();
            var security = element.GetUpdatedCache(Request(false));
            CheckNative();
            CheckCache(security); // No optional text is requested before this password/PID/identity check.
            CheckNative();
            AutomationElement metadata = null;
            Exception batchFailure = null;
            try { metadata = element.GetUpdatedCache(Request(true)); }
            catch (Exception ex) { batchFailure = ex; } // No fallback/per-property provider retries.
            CheckNative();
            if (batchFailure == null) CheckCache(metadata);
            for (var index = 0; index < TextProperties.Length; index++)
            {
                if (batchFailure != null) { LogValue(PropertyLabels[index], null, "BATCH_EXCEPTION", batchFailure.HResult); continue; }
                object value = null;
                Exception failure = null;
                try { value = metadata.GetCachedPropertyValue(TextProperties[index], true); }
                catch (Exception ex) { failure = ex; }
                LogValue(PropertyLabels[index], value, failure == null ? null : "EXCEPTION", failure?.HResult);
            }
            // Verified absent from the net48 managed reference assembly, not evidence that the KI provider lacks them.
            // A separate native MSAA point observation below is not this missing managed pattern or a same-object bridge.
            log.Write("INFO", "RECEIVE_METADATA_API_GAP", AuditLog.Field("phase", phase), AuditLog.Field("node", node.Node),
                AuditLog.Field("aria_role", "MANAGED_API_UNAVAILABLE"), AuditLog.Field("aria_properties", "MANAGED_API_UNAVAILABLE"),
                AuditLog.Field("legacy_role_state_description_default_action", "MANAGED_API_UNAVAILABLE"),
                AuditLog.Field("control_type_default_used", ReferenceEquals(
                    security.GetCachedPropertyValue(AutomationElement.ControlTypeProperty, true), AutomationElement.NotSupported)),
                AuditLog.Field("maximum_cache_calls", 2), AuditLog.Field("individual_call_timeout", false),
                AuditLog.Field("plain_body_verified", false));
            CheckNative();

            var nativeStage = "Bounds";
            IAccessible accessible = null;
            try
            {
                var center = new Point();
                Need(batchFailure == null && metadata != null &&
                    Equals(metadata.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty, true), false) &&
                    TryCenter(metadata.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty, true), out center),
                    "RECEIVE_MSAA_POINT_NOT_EXPOSED");
                var point = new NativeMethods.ScreenPoint { X = (int)center.X, Y = (int)center.Y };
                nativeStage = "PointScope";
                var hit = WindowFromPhysicalPoint(point);
                uint pid;
                Need(hit != IntPtr.Zero && NativeMethods.IsWindow(hit) && (hit == root || NativeMethods.IsChild(root, hit)) &&
                    NativeMethods.GetWindowThreadProcessId(hit, out pid) != 0 && pid == node.Identity.ProcessId,
                    "RECEIVE_MSAA_POINT_NOT_EXPOSED");
                CheckNative();
                object child;
                nativeStage = "AccessibleObjectFromPoint";
                Need(AccessibleObjectFromPoint(point, out accessible, out child) == 0 && accessible != null && child is int && (int)child >= 0,
                    "RECEIVE_MSAA_POINT_UNAVAILABLE");
                IntPtr originalWindow = IntPtr.Zero;
                void Scope()
                {
                    CheckNative();
                    Need(WindowFromPhysicalPoint(point) == hit, "RECEIVE_MSAA_POINT_CHANGED");
                    IntPtr current;
                    Need(WindowFromAccessibleObject(accessible, out current) == 0 && current != IntPtr.Zero &&
                        NativeMethods.IsWindow(current) && (current == root || NativeMethods.IsChild(root, current)) &&
                        NativeMethods.GetWindowThreadProcessId(current, out pid) != 0 && pid == node.Identity.ProcessId &&
                        (originalWindow == IntPtr.Zero || current == originalWindow), "RECEIVE_MSAA_WINDOW_CHANGED");
                    originalWindow = current;
                    CheckNative();
                }
                int State()
                {
                    Scope();
                    var value = accessible.get_accState(child);
                    Scope();
                    Need(value is int && SendMetadataProbe.IsSendStateUsable((int)value), "RECEIVE_MSAA_STATE_UNUSABLE");
                    return (int)value;
                }
                object ReadNative(string property, Func<object> read)
                {
                    nativeStage = property;
                    var before = State();
                    object value = null;
                    Exception failure = null;
                    try { value = read(); } catch (Exception ex) { failure = ex; }
                    Need(State() == before, "RECEIVE_MSAA_STATE_CHANGED");
                    if (property != "Role") LogValue("MSAA." + property, value,
                        failure == null ? null : NativeFailureStatus(failure.HResult), failure?.HResult);
                    else log.Write("INFO", "RECEIVE_METADATA_MSAA_ROLE", AuditLog.Field("phase", phase), AuditLog.Field("node", node.Node),
                        AuditLog.Field("status", failure != null ? NativeFailureStatus(failure.HResult) : value is int ? "PRESENT" : "INVALID_TYPE"),
                        AuditLog.Field("role_id", value is int ? (int)value : -1), AuditLog.Field("state_bits", "0x" + before.ToString("X8")),
                        AuditLog.Field("hresult", failure == null ? "NONE" : "0x" + failure.HResult.ToString("X8")));
                    return failure == null ? value : null;
                }
                ReadNative("Role", () => accessible.get_accRole(child));
                var name = ReadNative("Name", () => accessible.get_accName(child)) as string;
                ReadNative("Description", () => accessible.get_accDescription(child));
                ReadNative("DefaultAction", () => accessible.get_accDefaultAction(child));
                Scope();
                log.Write("INFO", "RECEIVE_METADATA_MSAA_RESULT", AuditLog.Field("phase", phase), AuditLog.Field("node", node.Node),
                    AuditLog.Field("status", "OBSERVED"), AuditLog.Field("point_lookup_calls", 1),
                    AuditLog.Field("name_hash_match", name == null || name.Length > TextLimit ? "UNAVAILABLE" :
                        (TokenStore.Hash(name) == node.Identity.NameHash).ToString()),
                    AuditLog.Field("uia_msaa_identity_verified", false), AuditLog.Field("plain_body_verified", false),
                    AuditLog.Field("cursor_movement_calls", 0), AuditLog.Field("action_calls", 0));
            }
            catch (Exception ex)
            {
                CheckNative(); // Cancellation/foreground loss still propagates, not an optional-metadata success.
                log.Write("INFO", "RECEIVE_METADATA_MSAA_RESULT", AuditLog.Field("phase", phase), AuditLog.Field("node", node.Node),
                    AuditLog.Field("status", ex is MonitorException ? "NOT_OBSERVED" : "EXCEPTION"), AuditLog.Field("native_call", nativeStage),
                    AuditLog.Field("reason", (ex as MonitorException)?.ReasonCode ?? "RECEIVE_MSAA_READ_FAILED"),
                    AuditLog.Field("hresult", "0x" + ex.HResult.ToString("X8")), AuditLog.Field("uia_msaa_identity_verified", false),
                    AuditLog.Field("plain_body_verified", false), AuditLog.Field("cursor_movement_calls", 0), AuditLog.Field("action_calls", 0));
            }
            finally
            {
                if (accessible != null && Marshal.IsComObject(accessible))
                    try { Marshal.ReleaseComObject(accessible); } catch { } // One lookup reference, no FinalRelease or traversal.
            }
            CheckNative();
        }

        private static CacheRequest Request(bool includeMetadata)
        {
            var request = new CacheRequest { TreeScope = TreeScope.Element,
                TreeFilter = Automation.RawViewCondition, AutomationElementMode = AutomationElementMode.None };
            foreach (var property in SecurityProperties) request.Add(property);
            if (includeMetadata)
            {
                foreach (var property in TextProperties) request.Add(property);
                request.Add(AutomationElement.BoundingRectangleProperty);
                request.Add(AutomationElement.IsOffscreenProperty);
            }
            return request;
        }

        internal static string ValueStatus(object value)
        {
            if (ReferenceEquals(value, AutomationElement.NotSupported)) return "NOT_SUPPORTED";
            if (value == null) return "NULL";
            var text = value as string;
            return text == null ? "INVALID_TYPE" : text.Length == 0 ? "EMPTY" : text.Length > TextLimit ? "OVER_LIMIT" : "PRESENT";
        }

        internal static bool TypeMatches(object value, string expected)
        {
            // Current.ControlType in the snapshot uses UIA's Custom default for an unspecified MSAA role.
            // Match only that exact documented default; absent Text or malformed values still reject.
            if (ReferenceEquals(value, AutomationElement.NotSupported)) return expected == ControlType.Custom.ProgrammaticName;
            var type = value as ControlType;
            return type != null && type.ProgrammaticName == expected;
        }

        internal static string Classify(string text)
        {
            if (text == null) return "UNAVAILABLE";
            if (text.Length == 0) return "EMPTY";
            if (text.Length > TextLimit) return "OVER_LIMIT";
            // ponytail: fixed lexical hints can be false positives; no hint authenticates a message body.
            var value = text.ToLowerInvariant();
            if (new[] { "attachment", "file", "download", "첨부", "파일", "다운로드" }.Any(value.Contains)) return "FILE_OR_ATTACHMENT_LIKE";
            if (new[] { "plain text", "plaintext", "textmessage", "text message", "텍스트 메시지" }.Any(value.Contains)) return "PLAIN_TEXT_LIKE";
            if (new[] { "hyperlink", "link", "링크" }.Any(value.Contains)) return "LINK_LIKE";
            if (new[] { "message", "bubble", "메시지", "말풍선" }.Any(value.Contains)) return "MESSAGE_LIKE";
            return "OTHER";
        }

        internal static bool TryCenter(object value, out Point center)
        {
            center = new Point();
            if (!(value is Rect)) return false;
            var bounds = (Rect)value;
            center = new Point(Math.Floor(bounds.Left + bounds.Width / 2), Math.Floor(bounds.Top + bounds.Height / 2));
            return SendMetadataProbe.IsNativePoint(center) && ReadOnlyProbe.ContainsPoint(bounds, center) &&
                center.X < bounds.Right && center.Y < bounds.Bottom;
        }

        internal static string NativeFailureStatus(int hresult)
        {
            return hresult == unchecked((int)0x80020003) || hresult == unchecked((int)0x80004001) ? "NOT_SUPPORTED" : "EXCEPTION";
        }

        internal static void RunSelfTest()
        {
            Need(TypeMatches(AutomationElement.NotSupported, ControlType.Custom.ProgrammaticName) &&
                TypeMatches(ControlType.Custom, ControlType.Custom.ProgrammaticName) &&
                TypeMatches(ControlType.Text, ControlType.Text.ProgrammaticName) &&
                !TypeMatches(AutomationElement.NotSupported, ControlType.Text.ProgrammaticName) &&
                !TypeMatches(ControlType.Custom, ControlType.Text.ProgrammaticName) &&
                !TypeMatches(null, ControlType.Custom.ProgrammaticName) && !TypeMatches(50025, ControlType.Custom.ProgrammaticName) &&
                !TypeMatches("ControlType.Custom", ControlType.Custom.ProgrammaticName), "RECEIVE_METADATA_SELF_TEST_TYPE_DEFAULT");
            Need(ValueStatus(AutomationElement.NotSupported) == "NOT_SUPPORTED" && ValueStatus(null) == "NULL" &&
                ValueStatus(7) == "INVALID_TYPE" && ValueStatus("") == "EMPTY" && ValueStatus(" ") == "PRESENT" &&
                ValueStatus(new string('x', TextLimit + 1)) == "OVER_LIMIT", "RECEIVE_METADATA_SELF_TEST_AVAILABILITY");
            Need(Classify("plain text message") == "PLAIN_TEXT_LIKE" && Classify("file attachment") == "FILE_OR_ATTACHMENT_LIKE" &&
                Classify("첨부 파일") == "FILE_OR_ATTACHMENT_LIKE" && Classify("hyperlink") == "LINK_LIKE" &&
                Classify("message bubble") == "MESSAGE_LIKE" && Classify("private customer text") == "OTHER" &&
                Classify(null) == "UNAVAILABLE" && Classify("") == "EMPTY", "RECEIVE_METADATA_SELF_TEST_CLASSIFICATION");
            var request = Request(true);
            Need(request.TreeScope == TreeScope.Element && request.AutomationElementMode == AutomationElementMode.None &&
                ReferenceEquals(request.TreeFilter, Automation.RawViewCondition), "RECEIVE_METADATA_SELF_TEST_CACHE_SCOPE");
            Point center;
            Need(TryCenter(new Rect(-10, 5, 8, 8), out center) && center == new Point(-6, 9) &&
                !TryCenter(Rect.Empty, out center) && !TryCenter(AutomationElement.NotSupported, out center) &&
                !TryCenter(new Rect(0, 0, 0, 5), out center) && !TryCenter(new Rect(double.PositiveInfinity, 0, 5, 5), out center),
                "RECEIVE_METADATA_SELF_TEST_BOUNDS");
            Need(NativeFailureStatus(unchecked((int)0x80020003)) == "NOT_SUPPORTED" &&
                NativeFailureStatus(unchecked((int)0x80004001)) == "NOT_SUPPORTED" && NativeFailureStatus(-1) == "EXCEPTION",
                "RECEIVE_METADATA_SELF_TEST_NATIVE_STATUS");
        }

        // Existing oleacc contract: physical point by value and original IAccessible plus VT_I4 child; read-only use only.
        [DllImport("oleacc.dll", ExactSpelling = true)]
        private static extern int AccessibleObjectFromPoint(NativeMethods.ScreenPoint point,
            [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible, [MarshalAs(UnmanagedType.Struct)] out object child);
        [DllImport("oleacc.dll", ExactSpelling = true)]
        private static extern int WindowFromAccessibleObject([MarshalAs(UnmanagedType.Interface)] IAccessible accessible, out IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr WindowFromPhysicalPoint(NativeMethods.ScreenPoint point);

        private static void Need(bool condition, string reason)
        {
            if (!condition) throw new MonitorException(reason, "Receive metadata rejected: " + reason + ".");
        }
    }
}
