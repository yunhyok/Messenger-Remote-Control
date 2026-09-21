using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Forms;

namespace RemoteMonitorMaster
{
    // One element-only provider round trip. No cached value survives a node visit or snapshot.
    internal sealed class ProbeElementCache
    {
        private static readonly AutomationPattern[] Patterns = {
            DockPattern.Pattern,
            ExpandCollapsePattern.Pattern,
            GridItemPattern.Pattern,
            GridPattern.Pattern,
            InvokePattern.Pattern,
            ItemContainerPattern.Pattern,
            MultipleViewPattern.Pattern,
            RangeValuePattern.Pattern,
            ScrollItemPattern.Pattern,
            ScrollPattern.Pattern,
            SelectionItemPattern.Pattern,
            SelectionPattern.Pattern,
            SynchronizedInputPattern.Pattern,
            TableItemPattern.Pattern,
            TablePattern.Pattern,
            TextPattern.Pattern,
            TogglePattern.Pattern,
            TransformPattern.Pattern,
            ValuePattern.Pattern,
            VirtualizedItemPattern.Pattern,
            WindowPattern.Pattern
        };

        private readonly Dictionary<AutomationPattern, object> supportedPatterns;
        private readonly string automationId;
        private readonly string className;
        private readonly string frameworkId;
        private readonly string patternNames;
        private readonly string runtimeId;

        internal int ProcessId { get; private set; }
        internal bool IsPassword { get; private set; }
        internal ControlType ControlType { get; private set; }
        internal int NativeWindowHandle { get; private set; }
        internal bool IsEnabled { get; private set; }
        internal bool IsOffscreen { get; private set; }
        internal bool HasKeyboardFocus { get; private set; }
        internal Rect BoundingRectangle { get; private set; }

        private ProbeElementCache(AutomationElement cached, int expectedProcessId, bool includeBoundingRectangle)
        {
            ProcessId = expectedProcessId;
            IsPassword = false;
            runtimeId = ValidateSecurityValues(
                cached.GetCachedPropertyValue(AutomationElement.ProcessIdProperty, true),
                // Both values come from this batch, not from an earlier live read: the batch itself is the
                // pre-content guard. false matches Current.IsPassword's documented default.
                cached.GetCachedPropertyValue(AutomationElement.IsPasswordProperty, false),
                cached.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty, true),
                expectedProcessId);

            automationId = Cached<string>(cached, AutomationElement.AutomationIdProperty);
            ControlType = Cached<ControlType>(cached, AutomationElement.ControlTypeProperty);
            className = Cached<string>(cached, AutomationElement.ClassNameProperty);
            frameworkId = Cached<string>(cached, AutomationElement.FrameworkIdProperty);
            NativeWindowHandle = Cached<int>(cached, AutomationElement.NativeWindowHandleProperty);
            IsEnabled = Cached<bool>(cached, AutomationElement.IsEnabledProperty);
            IsOffscreen = Cached<bool>(cached, AutomationElement.IsOffscreenProperty);
            HasKeyboardFocus = Cached<bool>(cached, AutomationElement.HasKeyboardFocusProperty);
            BoundingRectangle = includeBoundingRectangle
                ? Cached<Rect>(cached, AutomationElement.BoundingRectangleProperty)
                : Rect.Empty;

            supportedPatterns = new Dictionary<AutomationPattern, object>();
            var names = new List<string>();
            foreach (var pattern in Patterns)
            {
                object value;
                if (!cached.TryGetCachedPattern(pattern, out value)) continue;
                supportedPatterns.Add(pattern, value);
                names.Add(pattern.ProgrammaticName);
            }
            names.Sort(StringComparer.Ordinal);
            patternNames = string.Join(",", names.ToArray());
        }

        private static AutomationElement CaptureUpdated(AutomationElement element, int expectedProcessId,
            bool includeBoundingRectangle)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (expectedProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(expectedProcessId));

            // Bulk properties/patterns avoid GetSupportedPatterns' documented per-pattern provider queries.
            // https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/caching-in-ui-automation-clients
            var request = new CacheRequest {
                TreeScope = TreeScope.Element,
                TreeFilter = Condition.TrueCondition,
                AutomationElementMode = AutomationElementMode.Full
            };
            foreach (var property in new[] {
                AutomationElement.ProcessIdProperty,
                AutomationElement.IsPasswordProperty,
                AutomationElement.RuntimeIdProperty,
                AutomationElement.AutomationIdProperty,
                AutomationElement.ControlTypeProperty,
                AutomationElement.ClassNameProperty,
                AutomationElement.FrameworkIdProperty,
                AutomationElement.NativeWindowHandleProperty,
                AutomationElement.IsEnabledProperty,
                AutomationElement.IsOffscreenProperty,
                AutomationElement.HasKeyboardFocusProperty
            }) request.Add(property);
            if (includeBoundingRectangle) request.Add(AutomationElement.BoundingRectangleProperty);
            foreach (var pattern in Patterns) request.Add(pattern);

            var cached = element.GetUpdatedCache(request);
            if (cached == null)
                throw new MonitorException("PROBE_CACHE_UNAVAILABLE", "The UI Automation element cache is unavailable.");
            return cached;
        }

        internal static ProbeElementCache Capture(AutomationElement element, int expectedProcessId,
            bool includeBoundingRectangle = false)
        {
            return new ProbeElementCache(CaptureUpdated(element, expectedProcessId, includeBoundingRectangle),
                expectedProcessId, includeBoundingRectangle);
        }

        // The batch fetches ProcessId/IsPassword fresh from the provider, so the caller can tell a foreign or
        // password element apart from an unknown batch failure without a second live guard round trip.
        // No batched value is exposed and nothing beyond the failing check name leaves this method.
        internal static bool TryCapture(AutomationElement element, int expectedProcessId,
            bool includeBoundingRectangle, out ProbeElementCache cache, out string rejection)
        {
            cache = null;
            var cached = CaptureUpdated(element, expectedProcessId, includeBoundingRectangle);
            rejection = SecurityRejection(
                cached.GetCachedPropertyValue(AutomationElement.ProcessIdProperty, true),
                // Match Current.IsPassword's documented false default, exactly as the constructor does.
                cached.GetCachedPropertyValue(AutomationElement.IsPasswordProperty, false),
                expectedProcessId);
            if (rejection != null) return false;
            cache = new ProbeElementCache(cached, expectedProcessId, includeBoundingRectangle);
            return true;
        }

        internal ElementIdentity CreateIdentity(string guardedCurrentName)
        {
            var name = guardedCurrentName ?? string.Empty;
            return new ElementIdentity(runtimeId, ProcessId, automationId,
                ControlType.ProgrammaticName ?? string.Empty, className, frameworkId, patternNames,
                name.Length, TokenStore.Hash(name), "<redacted>");
        }

        internal bool TryGetPattern(AutomationPattern pattern, out object value)
        {
            return supportedPatterns.TryGetValue(pattern, out value);
        }

        internal static int PatternCount { get { return Patterns.Length; } }

        internal const string ForeignProcessRejection = "FOREIGN_OR_UNREADABLE_PID";
        internal const string PasswordRejection = "PASSWORD_OR_UNREADABLE_GUARD";

        // Names the failing security check without exposing any value; null means both checks passed.
        internal static string SecurityRejection(object processId, object password, int expectedProcessId)
        {
            if (!(processId is int) || (int)processId != expectedProcessId) return ForeignProcessRejection;
            if (!(password is bool) || (bool)password) return PasswordRejection;
            return null;
        }

        internal static string ValidateSecurityValues(object processId, object password, object runtimeId,
            int expectedProcessId)
        {
            if (SecurityRejection(processId, password, expectedProcessId) != null)
                throw new MonitorException("PROBE_CONTENT_BLOCKED",
                    "The cached element is not a verified non-password target-process element.");
            var values = runtimeId as int[];
            if (values == null || values.Length == 0)
                throw new MonitorException("UNSUPPORTED_UIA_RUNTIME_ID", "A required UIA element has no cached runtime ID.");
            var text = new string[values.Length];
            for (var index = 0; index < values.Length; index++)
                text[index] = values[index].ToString(CultureInfo.InvariantCulture);
            return string.Join(",", text);
        }

        private static T Cached<T>(AutomationElement element, AutomationProperty property)
        {
            // false preserves the same documented property defaults exposed by element.Current.
            var value = element.GetCachedPropertyValue(property, false);
            if (!(value is T))
                throw new MonitorException("PROBE_CACHE_INVALID", "A cached UI Automation property has an invalid type.");
            return (T)value;
        }

        internal static void RunSelfTest()
        {
            Need(PatternCount == 21, "PROBE_CACHE_PATTERN_COUNT");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pattern in Patterns) Need(unique.Add(pattern.ProgrammaticName), "PROBE_CACHE_PATTERN_DUPLICATE");
            var frameworkPatterns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in typeof(BasePattern).Assembly.GetExportedTypes())
            {
                if (!type.IsSubclassOf(typeof(BasePattern))) continue;
                var field = type.GetField("Pattern", BindingFlags.Public | BindingFlags.Static);
                var pattern = field?.GetValue(null) as AutomationPattern;
                Need(pattern != null && frameworkPatterns.Add(pattern.ProgrammaticName), "PROBE_CACHE_PATTERN_REFLECTION_INVALID");
            }
            Need(unique.SetEquals(frameworkPatterns), "PROBE_CACHE_PATTERN_REGISTRY_INCOMPLETE");

            RejectSecurity(AutomationElement.NotSupported, false, new[] { 1 }, 1);
            RejectSecurity(1, AutomationElement.NotSupported, new[] { 1 }, 1);
            RejectSecurity(1, true, new[] { 1 }, 1);
            RejectSecurity(2, false, new[] { 1 }, 1);
            RejectSecurity(1, false, AutomationElement.NotSupported, 1);
            RejectSecurity(1, false, new int[0], 1);
            Need(ValidateSecurityValues(1, false, new[] { 42, 7 }, 1) == "42,7",
                "PROBE_CACHE_SECURITY_VALID_REJECTED");
            Need(SecurityRejection(AutomationElement.NotSupported, false, 1) == ForeignProcessRejection &&
                SecurityRejection(2, false, 1) == ForeignProcessRejection &&
                SecurityRejection(1, AutomationElement.NotSupported, 1) == PasswordRejection &&
                SecurityRejection(1, true, 1) == PasswordRejection &&
                SecurityRejection(1, false, 1) == null,
                "PROBE_CACHE_SECURITY_REJECTION_REASON");

            Form fixture = null;
            IntPtr[] handles = null;
            Exception fixtureFailure = null;
            using (var ready = new ManualResetEventSlim())
            {
                var ui = new Thread(() =>
                {
                    try
                    {
                        fixture = new ProbeFixtureForm {
                            Text = "Probe cache fixture", ShowInTaskbar = false,
                            StartPosition = FormStartPosition.Manual,
                            Location = new System.Drawing.Point(-30000, -30000)
                        };
                        var box = new TextBox { Name = "fixtureInput", Text = "stable value", Left = 10, Top = 10 };
                        var button = new Button { Name = "fixtureSend", Text = "Send", Left = 10, Top = 45 };
                        fixture.Controls.Add(box);
                        fixture.Controls.Add(button);
                        fixture.Shown += (sender, args) =>
                        {
                            handles = new[] { fixture.Handle, box.Handle, button.Handle };
                            ready.Set();
                        };
                        Application.Run(fixture);
                    }
                    catch (Exception ex)
                    {
                        fixtureFailure = ex;
                        ready.Set();
                    }
                    finally { fixture?.Dispose(); }
                });
                ui.IsBackground = true;
                ui.SetApartmentState(ApartmentState.STA);
                ui.Start();
                Need(ready.Wait(TimeSpan.FromSeconds(10)), "PROBE_CACHE_FIXTURE_TIMEOUT");
                if (fixtureFailure != null) throw new InvalidOperationException("Probe cache fixture failed.", fixtureFailure);

                Exception comparisonFailure = null;
                var comparison = new Thread(() =>
                {
                    try
                    {
                        foreach (var handle in handles)
                        {
                            var element = AutomationElement.FromHandle(handle);
                            var legacy = ElementIdentity.Capture(element);
                            ProbeElementCache batch;
                            string rejection;
                            Need(TryCapture(element, legacy.ProcessId, true, out batch, out rejection) &&
                                rejection == null && batch != null, "PROBE_CACHE_TRY_CAPTURE_REJECTED");
                            ProbeElementCache foreign;
                            Need(!TryCapture(element, legacy.ProcessId + 1, false, out foreign, out rejection) &&
                                foreign == null && rejection == ForeignProcessRejection,
                                "PROBE_CACHE_TRY_CAPTURE_FOREIGN_ACCEPTED");
                            var current = element.Current;
                            Need(batch.CreateIdentity(current.Name).Equals(legacy), "PROBE_CACHE_IDENTITY_MISMATCH");
                            Need(batch.ProcessId == current.ProcessId && batch.ControlType == current.ControlType &&
                                batch.NativeWindowHandle == current.NativeWindowHandle && batch.IsEnabled == current.IsEnabled &&
                                batch.IsOffscreen == current.IsOffscreen && batch.HasKeyboardFocus == current.HasKeyboardFocus &&
                                batch.BoundingRectangle == current.BoundingRectangle,
                                "PROBE_CACHE_METADATA_MISMATCH");
                        }
                        object pattern;
                        Need(Capture(AutomationElement.FromHandle(handles[1]),
                                ElementIdentity.Capture(AutomationElement.FromHandle(handles[1])).ProcessId)
                            .TryGetPattern(ValuePattern.Pattern, out pattern) && pattern is ValuePattern,
                            "PROBE_CACHE_VALUE_PATTERN_MISSING");
                        Need(Capture(AutomationElement.FromHandle(handles[2]),
                                ElementIdentity.Capture(AutomationElement.FromHandle(handles[2])).ProcessId)
                            .TryGetPattern(InvokePattern.Pattern, out pattern) && pattern is InvokePattern,
                            "PROBE_CACHE_INVOKE_PATTERN_MISSING");
                    }
                    catch (Exception ex) { comparisonFailure = ex; }
                });
                comparison.IsBackground = true;
                comparison.SetApartmentState(ApartmentState.MTA);
                var comparisonComplete = false;
                var fixtureClosed = false;
                try
                {
                    comparison.Start();
                    comparisonComplete = comparison.Join(TimeSpan.FromSeconds(15));
                }
                finally
                {
                    try { fixture?.BeginInvoke(new Action(fixture.Close)); } catch { }
                    fixtureClosed = ui.Join(TimeSpan.FromSeconds(10));
                }
                Need(comparisonComplete, "PROBE_CACHE_COMPARISON_TIMEOUT");
                Need(fixtureClosed, "PROBE_CACHE_FIXTURE_CLOSE_TIMEOUT");
                if (comparisonFailure != null) throw new InvalidOperationException("Probe cache comparison failed.", comparisonFailure);
            }
        }

        private sealed class ProbeFixtureForm : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var value = base.CreateParams;
                    value.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                    return value;
                }
            }
        }

        private static void RejectSecurity(object processId, object password, object runtimeId, int expectedProcessId)
        {
            try
            {
                ValidateSecurityValues(processId, password, runtimeId, expectedProcessId);
                throw new InvalidOperationException("Self-test failed: PROBE_CACHE_SECURITY_ACCEPTED");
            }
            catch (MonitorException) { }
        }

        private static void Need(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Self-test failed: " + message);
        }
    }
}
