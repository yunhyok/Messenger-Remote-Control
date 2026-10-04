using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace RemoteMonitorLink
{
    internal sealed class OutputBufferResult
    {
        internal string Text, Method, Code, Detail;
        internal int CharacterCount, LineCount;
    }

    // ponytail: standard HWND text contract only; custom controls need a separately verified copy path. RichEdit 1.0 and
    // ANSI RichEdit stop at 64K; Unicode RichEdit 2.0+ may exceed it only as a whole, stable, untruncated read.
    // No input, activation, selection changes or clipboard access. Caller isolates UIA/provider hangs in its worker.
    internal static class PowerSiOutputBuffer
    {
        internal const int MaxCharacters = 8 * 1024 * 1024;
        private const int MaxNodes = 1536;
        internal const int MaxUiaNodes = 512; // Also the node bound of the Slave's PowerSI/PowerDC window-mode scan.
        private const int RichEditClassicLimit = 65535;
        private const uint Visible = 0x10000000, Child = 0x40000000, Multiline = 4, Password = 0x20;
        private sealed class Node
        {
            internal IntPtr Handle, Parent;
            internal string Class;
            internal uint Style;
            internal bool Visible, Output;
        }
        private sealed class ReadFailure : Exception
        {
            internal ReadFailure(string code) : base(code) { }
        }

        internal static OutputBufferResult Read(IntPtr root)
        {
            var nodes = new List<Node>();
            var scopes = new HashSet<IntPtr>();
            int uiaVisited = 0, textCandidates = 0;
            string code = "BUFFER_FAILED";
            string largeRead = ""; // "|E|kind|declared|copied|stable" once a Unicode RichEdit above 64K was read.
            string noHwndRect = ""; // "|R|x|y|w|h" of the one HWND-less "Output" element's UIA geometry (BUFFER_OUTPUT_NO_HWND).
            IntPtr output = IntPtr.Zero; // Set only once exactly one outer Output scope HWND is identified.
            try
            {
                uint pid;
                Require(root != IntPtr.Zero && IsWindow(root) && IsWindowVisible(root) && !IsIconic(root) &&
                    GetWindowThreadProcessId(root, out pid) != 0, "BUFFER_ROOT_INVALID");
                GetWindowThreadProcessId(root, out pid);
                var clock = Stopwatch.StartNew();
                string enumerationFailure = null;
                WindowCallback callback = (window, ignored) =>
                {
                    try
                    {
                        Require(nodes.Count < MaxNodes && clock.ElapsedMilliseconds < 2500, "BUFFER_TREE_LIMIT");
                        uint owner;
                        Require(IsWindow(window) && IsChild(root, window) && GetWindowThreadProcessId(window, out owner) != 0 && owner == pid,
                            "BUFFER_TARGET_CHANGED");
                        var name = new StringBuilder(256);
                        Require(GetClassName(window, name, name.Capacity) > 0, "BUFFER_TARGET_CHANGED");
                        var node = new Node { Handle = window, Parent = GetParent(window), Class = name.ToString(),
                            Style = unchecked((uint)GetWindowLong(window, -16)), Visible = IsWindowVisible(window) };
                        if (node.Visible && !IsTextClass(node.Class) && !IsInteractiveClass(node.Class))
                        {
                            // Only keep an exact label bit, never arbitrary titles or control text in diagnostics.
                            var title = new StringBuilder(64);
                            GetWindowText(window, title, title.Capacity);
                            node.Output = string.Equals(title.ToString().Trim(), "Output", StringComparison.OrdinalIgnoreCase);
                        }
                        nodes.Add(node);
                        return true;
                    }
                    catch (ReadFailure ex) { enumerationFailure = ex.Message; return false; }
                    catch { enumerationFailure = "BUFFER_TREE_FAILED"; return false; }
                };
                EnumChildWindows(root, callback, IntPtr.Zero);
                if (enumerationFailure != null) throw new ReadFailure(enumerationFailure);
                foreach (var marker in nodes.Where(n => n.Output))
                {
                    var scope = string.Equals(marker.Class, "Static", StringComparison.OrdinalIgnoreCase) &&
                        !nodes.Any(n => n.Parent == marker.Handle) ? marker.Parent : marker.Handle;
                    if (scope != root && scope != IntPtr.Zero && IsChild(root, scope) && IsWindowVisible(scope)) scopes.Add(scope);
                }
                // Native captions and UIA names are different contracts. In this same attempt, link the semantic dock
                // to its non-root HWND rather than guessing a body rectangle beneath a label.
                var geometry = Rectangle.Empty;
                if (scopes.Count == 0)
                    geometry = DiscoverUiaScopes(root, pid, scopes, ref uiaVisited);
                var outerScopes = scopes.Where(s => !scopes.Any(other => other != s && IsChild(other, s))).ToArray();
                // Found by name but windowless (no HWND to read): only its UIA geometry, for the separately guarded SCOPE copy.
                if (outerScopes.Length == 0 && !geometry.IsEmpty)
                {
                    noHwndRect = RectSuffix(geometry);
                    throw new ReadFailure("BUFFER_OUTPUT_NO_HWND");
                }
                Require(outerScopes.Length != 0, "BUFFER_OUTPUT_NOT_IDENTIFIED");
                Require(outerScopes.Length == 1, "BUFFER_OUTPUT_AMBIGUOUS");
                output = outerScopes[0];
                var candidates = nodes.Where(n => n.Visible && IsTextClass(n.Class) && (n.Style & Multiline) != 0 &&
                    (n.Style & Password) == 0 && IsChild(output, n.Handle)).ToArray();
                textCandidates = candidates.Length;
                Require(candidates.Length != 0, "BUFFER_STANDARD_TEXT_NOT_FOUND");
                Require(candidates.Length == 1, "BUFFER_TEXT_AMBIGUOUS");
                var selected = candidates[0];
                ValidateTarget(root, output, selected, pid);
                bool richEdit = IsRichEdit(selected.Class);
                bool unicodeRichEdit = richEdit && IsUnicodeRichEdit(selected.Class) && IsWindowUnicode(selected.Handle);
                int length = ReadLength(selected.Handle);
                ValidateLength(length, richEdit, unicodeRichEdit);
                bool large = richEdit && length > RichEditClassicLimit; // Only a Unicode RichEdit 2.0+ passes ValidateLength here.
                int capacity;
                UIntPtr copied;
                string text = ReadText(selected.Handle, length, out copied, out capacity);
                Require(copied.ToUInt64() <= MaxCharacters, "BUFFER_TOO_LARGE");
                Require(copied.ToUInt64() == (ulong)text.Length, "BUFFER_INCOMPLETE");
                bool stable = true;
                if (large)
                {
                    // A second whole read in this same attempt must return exactly the same text (same length and content).
                    int ignored;
                    UIntPtr again;
                    string second = ReadText(selected.Handle, length, out again, out ignored);
                    stable = again.ToUInt64() == copied.ToUInt64() && string.Equals(second, text, StringComparison.Ordinal);
                    largeRead = LargeReadSuffix(selected.Class, length, text.Length, stable);
                }
                int after = ReadLength(selected.Handle);
                ValidateLength(after, richEdit, unicodeRichEdit);
                Require(length == after, "BUFFER_CHANGED_DURING_READ");
                if (large)
                {
                    var verdict = LargeRichEditVerdict(length, text.Length, capacity, LineBreaks(text),
                        text.IndexOf("\r\n", StringComparison.Ordinal) >= 0, stable);
                    Require(verdict == null, verdict);
                }
                else
                {
                    Require(text.Length < capacity - 1 && (length == 0 || text.Length > 0), "BUFFER_INCOMPLETE");
                    // ANSI conversion can overestimate WM_GETTEXTLENGTH; Unicode controls must agree exactly.
                    Require(!IsWindowUnicode(selected.Handle) || text.Length == length, "BUFFER_INCOMPLETE");
                }
                ValidateTarget(root, output, selected, pid);
                Require(text.IndexOf('\0') < 0, "BUFFER_TEXT_INVALID");
                return new OutputBufferResult { Code = text.Length == 0 ? "BUFFER_EMPTY" : "BUFFER_READ",
                    Method = "NATIVE_WM_GETTEXT", Text = text, CharacterCount = text.Length, LineCount = CountLines(text),
                    Detail = Detail(nodes.Count, scopes.Count, textCandidates, uiaVisited) + largeRead };
            }
            catch (ReadFailure ex) { code = ex.Message; }
            catch { code = "BUFFER_FAILED"; }
            // One identified Output HWND whose text could not be read whole: add its rectangle (geometry only) so a
            // separately guarded copy fallback can aim without vision. Every other result keeps the 5-field B1 shape,
            // followed by the large-RichEdit read fields when that read was attempted.
            var detail = Detail(nodes.Count, scopes.Count, textCandidates, uiaVisited) + largeRead;
            if (output != IntPtr.Zero && ScopeRectCodes.Contains(code, StringComparer.Ordinal)) detail += ScopeRectSuffix(root, output);
            else if (code == "BUFFER_OUTPUT_NO_HWND") detail += noHwndRect;
            return new OutputBufferResult { Code = code, Method = "NATIVE_WM_GETTEXT", Detail = detail };
        }

        // Codes of an identified Output scope HWND that carry its rectangle. BUFFER_OUTPUT_NO_HWND carries the UIA geometry
        // of its single windowless "Output" element instead (same "|R|x|y|w|h" root-client shape).
        private static readonly string[] ScopeRectCodes = { "BUFFER_STANDARD_TEXT_NOT_FOUND", "BUFFER_TEXT_AMBIGUOUS",
            "BUFFER_RICHEDIT_LARGE_UNSUPPORTED", "BUFFER_READ_TIMEOUT", "BUFFER_INCOMPLETE", "BUFFER_CHANGED_DURING_READ",
            "BUFFER_RICHEDIT_UNSTABLE" };
        private static string Detail(int nodes, int scopes, int candidates, int uia)
        {
            return string.Format(CultureInfo.InvariantCulture, "B1|{0}|{1}|{2}|{3}", nodes, scopes, candidates, uia);
        }
        private static string RectSuffix(Rectangle rect)
        {
            return string.Format(CultureInfo.InvariantCulture, "|R|{0}|{1}|{2}|{3}", rect.X, rect.Y, rect.Width, rect.Height);
        }

        // UIA geometry of the one windowless "Output" element, in root client pixels. chain[0] is that element's
        // BoundingRectangle, chain[1..2] its parent and grandparent (the caller stops early at the root window, another
        // process or a tab that is not selected): enough for caption -> dock pane and tab item -> tab strip -> tab control
        // pane. The first element of at least 240x120 decides: a caption bar or tab label is smaller, so its dock or tab
        // pane is chosen; one larger than 90% of the client in either dimension is the main area or the whole window, and
        // one not fully inside the client is not this window's pane. Either refuses (Empty), as do zero or several "Output"
        // elements, nothing large enough within those 3 elements, or an empty client. Never clipped or guessed.
        internal static Rectangle SelectOutputGeometry(int outputElements, IList<Rectangle> chain, Size client)
        {
            if (outputElements != 1 || chain == null || client.Width < 1 || client.Height < 1) return Rectangle.Empty;
            for (int index = 0; index < chain.Count && index <= GeometryAncestors; index++)
            {
                var rect = chain[index];
                if (rect.Width < GeometryMinWidth || rect.Height < GeometryMinHeight) continue;
                if ((long)rect.Width * 10 > (long)client.Width * 9 || (long)rect.Height * 10 > (long)client.Height * 9 ||
                    rect.X < 0 || rect.Y < 0 || (long)rect.X + rect.Width > client.Width || (long)rect.Y + rect.Height > client.Height)
                    return Rectangle.Empty;
                return rect;
            }
            return Rectangle.Empty;
        }
        private const int GeometryMinWidth = 240, GeometryMinHeight = 120, GeometryAncestors = 2;

        // True only for a window shown at 96 DPI without scaling, the one case where UIA's BoundingRectangle (may be physical
        // pixels) and this DPI-unaware process's ScreenToClient (logical) agree. dpi: GetDpiForWindow where available (it
        // reports 96 for a DPI-unaware target at any scale), otherwise the system DPI; 0 when unknown. Because of that the
        // DWM frame bounds (always physical) must also match the logical window rectangle within its invisible borders.
        internal static bool UnscaledWindow(IntPtr root, out int dpi)
        {
            dpi = 0;
            try
            {
                try { dpi = (int)GetDpiForWindow(root); }
                catch (EntryPointNotFoundException)
                {
                    var screen = GetDC(IntPtr.Zero);
                    if (screen == IntPtr.Zero) return false;
                    try { dpi = GetDeviceCaps(screen, 88); } // LOGPIXELSX
                    finally { ReleaseDC(IntPtr.Zero, screen); }
                }
                NativeRect logical, physical;
                if (dpi != 96 || !GetWindowRect(root, out logical) ||
                    DwmGetWindowAttribute(root, 9, out physical, Marshal.SizeOf(typeof(NativeRect))) != 0) return false; // DWMWA_EXTENDED_FRAME_BOUNDS
                long lw = (long)logical.Right - logical.Left, lh = (long)logical.Bottom - logical.Top;
                long pw = (long)physical.Right - physical.Left, ph = (long)physical.Bottom - physical.Top;
                return lw > 0 && lh > 0 && pw > 0 && ph > 0 && Math.Abs(lw - pw) <= 32 && Math.Abs(lh - ph) <= 32;
            }
            catch { return false; }
        }
        // WM_GETTEXT into declared + 2 characters (declared + 1 text characters and the terminator): a copy that fills
        // the buffer (capacity - 1 characters) may have been cut. The marshaler returns the text up to the first NUL.
        private static string ReadText(IntPtr window, int declared, out UIntPtr copied, out int capacity)
        {
            var buffer = new StringBuilder(checked(declared + 2));
            capacity = buffer.Capacity;
            Require(SendText(window, 0x000D, new UIntPtr((uint)buffer.Capacity), buffer,
                0x23, 750, out copied) != IntPtr.Zero, "BUFFER_READ_TIMEOUT");
            return buffer.ToString();
        }
        // "|E|<W20|W50>|<declared>|<copied>|<stable 0/1>": class kind, lengths and a flag only, never text.
        private static string LargeReadSuffix(string className, int declared, int copied, bool stable)
        {
            return string.Format(CultureInfo.InvariantCulture, "|E|{0}|{1}|{2}|{3}",
                string.Equals(className, "RICHEDIT50W", StringComparison.OrdinalIgnoreCase) ? "W50" : "W20",
                declared, copied, stable ? 1 : 0);
        }
        private static int LineBreaks(string text) { return string.IsNullOrEmpty(text) ? 0 : CountLines(text) - 1; }

        // Acceptance of one Unicode RichEdit 2.0+ read above 64K: null accepts, anything else is the failure code. Fail
        // closed: the copy must equal the declared length exactly, or fall short by exactly its own line terminators when
        // it holds no CR+LF pair at all (the documented over-count of CR-only RichEdit text, each paragraph end counted as
        // CR+LF). Any other shortfall, a copy that filled its buffer or one no longer than 64K may be a cut tail; the
        // second read of the same attempt must be identical.
        internal static string LargeRichEditVerdict(int declared, int copied, int capacity, int lineTerminators, bool hasCrLf, bool stable)
        {
            if (declared > MaxCharacters || copied > MaxCharacters) return "BUFFER_TOO_LARGE";
            if (declared <= RichEditClassicLimit || capacity < declared + 2) return "BUFFER_RICHEDIT_LARGE_UNSUPPORTED";
            if (copied < 0 || copied >= capacity - 1 || copied > declared) return "BUFFER_INCOMPLETE";
            if (copied <= RichEditClassicLimit) return "BUFFER_INCOMPLETE";
            int shortfall = declared - copied;
            if (shortfall != 0 && (hasCrLf || lineTerminators < 1 || shortfall != lineTerminators)) return "BUFFER_INCOMPLETE";
            return stable ? null : "BUFFER_RICHEDIT_UNSTABLE";
        }
        // "|R|x|y|w|h" in the root's CLIENT pixels, in this DPI-unaware process's virtualized space like the Slave's other
        // window coordinates. Empty when the scope is gone or maps to an empty rectangle; never throws.
        private static string ScopeRectSuffix(IntPtr root, IntPtr scope)
        {
            try
            {
                NativeRect bounds;
                if (!IsWindow(scope) || !IsChild(root, scope) || !IsWindowVisible(scope) || !GetWindowRect(scope, out bounds)) return "";
                var a = new NativePoint { X = bounds.Left, Y = bounds.Top };
                var b = new NativePoint { X = bounds.Right, Y = bounds.Bottom };
                if (!ScreenToClient(root, ref a) || !ScreenToClient(root, ref b)) return "";
                // A mirrored (RTL) root swaps the horizontal corners; normalize instead of reporting a negative width.
                long x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y), w = Math.Abs((long)b.X - a.X), h = Math.Abs((long)b.Y - a.Y);
                if (w <= 0 || h <= 0 || w > int.MaxValue || h > int.MaxValue) return "";
                return string.Format(CultureInfo.InvariantCulture, "|R|{0}|{1}|{2}|{3}", x, y, w, h);
            }
            catch { return ""; }
        }
        // Parses the "|R|x|y|w|h" suffix of a 10-field B1 Detail, or of a 15-field one whose large-RichEdit block
        // "|E|W20 or W50|declared|copied|0 or 1" precedes it. False for the 5-field shape, any extra/malformed field,
        // non-canonical or signed numbers, zero size, or a rectangle not fully inside clientSize. Never throws.
        internal static bool TryScopeRect(string detail, Size clientSize, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            try
            {
                if (detail == null) return false;
                var p = detail.Split('|');
                bool Number(int index, out int value)
                {
                    return int.TryParse(p[index], NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
                        value.ToString(CultureInfo.InvariantCulture) == p[index];
                }
                int count, x, y, w, h, o = 0;
                if (p.Length == 15 && p[5] == "E")
                {
                    if ((p[6] != "W20" && p[6] != "W50") || !Number(7, out count) || !Number(8, out count) ||
                        (p[9] != "0" && p[9] != "1")) return false;
                    o = 5;
                }
                if (p.Length != 10 + o || p[0] != "B1" || p[5 + o] != "R" || !Number(1, out count) || !Number(2, out count) ||
                    !Number(3, out count) || !Number(4, out count) || !Number(6 + o, out x) || !Number(7 + o, out y) ||
                    !Number(8 + o, out w) || !Number(9 + o, out h) || w == 0 || h == 0 ||
                    (long)x + w > clientSize.Width || (long)y + h > clientSize.Height) return false;
                rect = new Rectangle(x, y, w, h);
                return true;
            }
            catch { rect = Rectangle.Empty; return false; }
        }
        // Whether a B1 Detail carries an Output scope rectangle at all, before the root's client size is known.
        internal static bool HasScopeRect(string detail)
        {
            Rectangle ignored;
            return TryScopeRect(detail, new Size(int.MaxValue, int.MaxValue), out ignored);
        }
        private static void ValidateTarget(IntPtr root, IntPtr scope, Node target, uint expectedPid)
        {
            uint rootPid, scopePid, targetPid;
            var name = new StringBuilder(256);
            Require(IsWindowVisible(root) && !IsIconic(root) && IsWindowVisible(scope) && IsWindowVisible(target.Handle) &&
                scope != root && IsChild(root, scope) && IsChild(scope, target.Handle) &&
                GetWindowThreadProcessId(root, out rootPid) != 0 && rootPid == expectedPid &&
                GetWindowThreadProcessId(scope, out scopePid) != 0 && scopePid == expectedPid &&
                GetWindowThreadProcessId(target.Handle, out targetPid) != 0 && targetPid == expectedPid &&
                GetParent(target.Handle) == target.Parent && GetClassName(target.Handle, name, name.Capacity) > 0 &&
                string.Equals(name.ToString(), target.Class, StringComparison.Ordinal), "BUFFER_TARGET_CHANGED");
            var style = unchecked((uint)GetWindowLong(target.Handle, -16));
            Require((style & Multiline) != 0 && (style & Password) == 0 && (style & Child) != 0,
                "BUFFER_TARGET_CHANGED");
        }
        private static int ReadLength(IntPtr window)
        {
            UIntPtr length;
            Require(SendValue(window, 0x000E, UIntPtr.Zero, IntPtr.Zero, 0x23, 750, out length) != IntPtr.Zero,
                "BUFFER_READ_TIMEOUT");
            Require(length.ToUInt64() <= MaxCharacters, "BUFFER_TOO_LARGE");
            return (int)length.ToUInt64();
        }
        private static void ValidateLength(int length, bool richEdit, bool unicodeRichEdit)
        {
            Require(length >= 0 && length <= MaxCharacters, "BUFFER_TOO_LARGE");
            // RichEdit 1.0 and ANSI RichEdit keep the 64K WM_GETTEXT boundary (the documented >64K alternatives are not
            // marshaled across processes). A Unicode RichEdit 2.0+ above it is accepted only through LargeRichEditVerdict.
            Require(!richEdit || unicodeRichEdit || length <= RichEditClassicLimit, "BUFFER_RICHEDIT_LARGE_UNSUPPORTED");
        }
        private static bool IsRichEdit(string name)
        {
            return string.Equals(name, "RichEdit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "RichEdit20A", StringComparison.OrdinalIgnoreCase) || IsUnicodeRichEdit(name);
        }
        private static bool IsUnicodeRichEdit(string name)
        {
            return string.Equals(name, "RichEdit20W", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "RICHEDIT50W", StringComparison.OrdinalIgnoreCase);
        }
        private static bool IsTextClass(string name)
        { return string.Equals(name, "Edit", StringComparison.OrdinalIgnoreCase) || IsRichEdit(name); }
        private static bool IsInteractiveClass(string name)
        {
            return new[] { "Button", "ComboBox", "ComboBoxEx32", "SysTabControl32", "SysTreeView32", "SysListView32", "ToolbarWindow32", "#32768" }
                .Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        // "Output" elements of the UIA pass: how many, and the one that has no HWND of its own (no handle or only the root's).
        private sealed class UiaMarkers
        {
            internal int Count;
            internal AutomationElement WithoutHandle;
        }

        // Adds the Output scope HWNDs found by name. When none is found and exactly one "Output" element exists without an
        // HWND, returns its UIA geometry in root client pixels (SelectOutputGeometry), otherwise Empty.
        private static Rectangle DiscoverUiaScopes(IntPtr root, uint pid, HashSet<IntPtr> scopes, ref int visited)
        {
            var found = new UiaMarkers();
            try
            {
                var request = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Automation.RawViewCondition,
                    AutomationElementMode = AutomationElementMode.Full };
                request.Add(AutomationElement.NameProperty); request.Add(AutomationElement.ControlTypeProperty);
                request.Add(AutomationElement.ProcessIdProperty); request.Add(AutomationElement.NativeWindowHandleProperty);
                request.Add(AutomationElement.IsOffscreenProperty);
                WalkUia(AutomationElement.FromHandle(root).GetUpdatedCache(request), null, 0, root, pid, scopes, request, ref visited, found);
            }
            catch (ReadFailure) { throw; }
            catch { throw new ReadFailure("BUFFER_UIA_UNAVAILABLE"); }
            return scopes.Count == 0 && found.Count == 1 && found.WithoutHandle != null
                ? OutputGeometry(root, pid, found.WithoutHandle) : Rectangle.Empty;
        }

        // Fail closed: a scaled or unknown DPI, any UIA failure, another process, the root window itself or a tab that is not
        // the selected one ends the chain, and only SelectOutputGeometry decides. Geometry only: no Name or text is read here.
        private static Rectangle OutputGeometry(IntPtr root, uint pid, AutomationElement marker)
        {
            try
            {
                NativeRect client;
                int dpi;
                if (!UnscaledWindow(root, out dpi) || !GetClientRect(root, out client)) return Rectangle.Empty;
                var request = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Automation.RawViewCondition,
                    AutomationElementMode = AutomationElementMode.Full };
                request.Add(AutomationElement.BoundingRectangleProperty); request.Add(AutomationElement.ControlTypeProperty);
                request.Add(AutomationElement.ProcessIdProperty); request.Add(AutomationElement.NativeWindowHandleProperty);
                request.Add(SelectionItemPattern.IsSelectedProperty);
                var walker = TreeWalker.RawViewWalker;
                var chain = new List<Rectangle>();
                var element = marker.GetUpdatedCache(request);
                for (int level = 0; element != null && level <= GeometryAncestors; level++)
                {
                    if ((int)element.GetCachedPropertyValue(AutomationElement.ProcessIdProperty) != (int)pid ||
                        new IntPtr((int)element.GetCachedPropertyValue(AutomationElement.NativeWindowHandleProperty)) == root) break;
                    // The pane above an unselected tab shows another page: never aim there.
                    if (element.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) as ControlType == ControlType.TabItem &&
                        !(element.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty) is bool selected && selected)) break;
                    chain.Add(ToClient(root, (System.Windows.Rect)element.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty)));
                    element = level < GeometryAncestors ? walker.GetParent(element, request) : null;
                }
                return SelectOutputGeometry(1, chain, new Size(client.Right - client.Left, client.Bottom - client.Top));
            }
            catch { return Rectangle.Empty; }
        }

        // Screen to root client pixels exactly like ScopeRectSuffix (ScreenToClient of both corners, mirrored roots normalized).
        private static Rectangle ToClient(IntPtr root, System.Windows.Rect screen)
        {
            if (screen.IsEmpty || double.IsNaN(screen.Left) || double.IsNaN(screen.Top) || double.IsInfinity(screen.Width) ||
                double.IsInfinity(screen.Height) || screen.Width <= 0 || screen.Height <= 0 ||
                Math.Abs(screen.Left) > 1e7 || Math.Abs(screen.Top) > 1e7 || screen.Width > 1e7 || screen.Height > 1e7) return Rectangle.Empty;
            var a = new NativePoint { X = (int)Math.Round(screen.Left), Y = (int)Math.Round(screen.Top) };
            var b = new NativePoint { X = (int)Math.Round(screen.Right), Y = (int)Math.Round(screen.Bottom) };
            if (!ScreenToClient(root, ref a) || !ScreenToClient(root, ref b)) return Rectangle.Empty;
            long x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y), w = Math.Abs((long)b.X - a.X), h = Math.Abs((long)b.Y - a.Y);
            return w <= 0 || h <= 0 || w > int.MaxValue || h > int.MaxValue ? Rectangle.Empty : new Rectangle((int)x, (int)y, (int)w, (int)h);
        }

        private static void WalkUia(AutomationElement element, AutomationElement parent, int depth, IntPtr root, uint pid,
            HashSet<IntPtr> scopes, CacheRequest request, ref int visited, UiaMarkers found)
        {
            Require(depth <= 20 && visited++ < MaxUiaNodes, "BUFFER_UIA_LIMIT");
            Require((int)element.GetCachedPropertyValue(AutomationElement.ProcessIdProperty) == pid, "BUFFER_TARGET_CHANGED");
            string name = element.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "";
            var type = (ControlType)element.GetCachedPropertyValue(AutomationElement.ControlTypeProperty);
            bool offscreen = (bool)element.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty);
            // A TabItem never has an HWND of its own: it only ever leads to the geometry of its (selected) tab pane.
            bool marker = !offscreen && string.Equals(name.Trim(), "Output", StringComparison.OrdinalIgnoreCase) &&
                (type == ControlType.Pane || type == ControlType.Group || type == ControlType.Custom ||
                 type == ControlType.Text || type == ControlType.TitleBar || type == ControlType.Window || type == ControlType.TabItem);
            if (marker)
            {
                found.Count++;
                var scope = type == ControlType.Text || type == ControlType.TitleBar ? parent : element;
                var handle = scope == null ? IntPtr.Zero :
                    new IntPtr((int)scope.GetCachedPropertyValue(AutomationElement.NativeWindowHandleProperty));
                uint owner;
                if (handle != IntPtr.Zero && handle != root && IsChild(root, handle) && IsWindowVisible(handle) &&
                    GetWindowThreadProcessId(handle, out owner) != 0 && owner == pid) scopes.Add(handle);
                else if (handle == IntPtr.Zero || handle == root) found.WithoutHandle = element;
                return;
            }
            // Same bounded dock-discovery pruning as PowerSiObservation; never scan individual net/log rows.
            if (type == ControlType.Tree || type == ControlType.List || type == ControlType.DataGrid || type == ControlType.Table ||
                type == ControlType.ComboBox || type == ControlType.Menu || type == ControlType.MenuBar || type == ControlType.ToolBar ||
                type == ControlType.Document || type == ControlType.Edit) return;
            var walker = TreeWalker.RawViewWalker;
            for (var child = walker.GetFirstChild(element, request); child != null; child = walker.GetNextSibling(child, request))
                WalkUia(child, element, depth + 1, root, pid, scopes, request, ref visited, found);
        }

        private static int CountLines(string text)
        {
            if (text.Length == 0) return 0;
            int count = 1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'))) count++;
            return count;
        }
        private static void Require(bool condition, string code) { if (!condition) throw new ReadFailure(code); }

        internal static void SelfTest()
        {
            Require(Read(IntPtr.Zero).Code == "BUFFER_ROOT_INVALID", "TEST_INVALID_ROOT");
            Require(CountLines("") == 0 && CountLines("one\r\ntwo\nthree\rfour") == 4, "TEST_LINE_COUNT");
            Require(IsTextClass("Edit") && IsRichEdit("RICHEDIT50W") && !IsTextClass("CustomEdit"), "TEST_CLASS_CONTRACT");
            Require(IsUnicodeRichEdit("RICHEDIT50W") && IsUnicodeRichEdit("RichEdit20W") && IsUnicodeRichEdit("richedit20w") &&
                !IsUnicodeRichEdit("RichEdit20A") && !IsUnicodeRichEdit("RichEdit") && IsRichEdit("RichEdit") &&
                IsRichEdit("RichEdit20A"), "TEST_UNICODE_RICHEDIT_CONTRACT");
            try { ValidateLength(MaxCharacters + 1, false, false); throw new InvalidOperationException("Oversize buffer accepted."); }
            catch (ReadFailure ex) { Require(ex.Message == "BUFFER_TOO_LARGE", "TEST_SIZE"); }
            try { ValidateLength(MaxCharacters + 1, true, true); throw new InvalidOperationException("Oversize RichEdit accepted."); }
            catch (ReadFailure ex) { Require(ex.Message == "BUFFER_TOO_LARGE", "TEST_RICHEDIT_CAP"); }
            // RichEdit 1.0 / ANSI RichEdit (not Unicode) keep the 64K refusal; a Unicode RichEdit 2.0+ goes on to the verdict.
            try { ValidateLength(65536, true, false); throw new InvalidOperationException("Large RichEdit accepted."); }
            catch (ReadFailure ex) { Require(ex.Message == "BUFFER_RICHEDIT_LARGE_UNSUPPORTED", "TEST_RICHEDIT_SIZE"); }
            ValidateLength(65535, true, false);
            ValidateLength(65536, true, true);
            ValidateLength(MaxCharacters, true, true);
            LargeReadSelfTest();
            ScopeRectParseSelfTest();
            GeometrySelfTest();
            Console.WriteLine("PASS: buffer RichEdit above 64K acceptance table (declared/copied/capacity/line breaks/stable), B1 scope-rect parser, windowless Output geometry table");
            // Owned offscreen HWND fixture. Never changes another application's focus, input, or clipboard.
            var root = CreateWindowEx(0x08000080, "Static", "Owned buffer self-test", 0x80000000 | Visible,
                -32000, -32000, 420, 280, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Require(root != IntPtr.Zero, "TEST_CREATE_ROOT");
            try
            {
                var scope = CreateWindowEx(0, "Static", "Output", Child | Visible, 0, 0, 400, 250,
                    root, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Require(scope != IntPtr.Zero, "TEST_CREATE_SCOPE");
                string content = string.Concat(Enumerable.Repeat("private-buffer-sentinel 1234567890\r\n", 2400));
                var edit = CreateWindowEx(0, "Edit", "", Child | Visible | Multiline | 0x0800,
                    0, 20, 390, 220, scope, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Require(edit != IntPtr.Zero && SetWindowText(edit, content), "TEST_CREATE_EDIT");
                var beforeFocus = GetForegroundWindow();
                uint beforeClipboard = GetClipboardSequenceNumber();
                var result = Read(root);
                Require(result.Code == "BUFFER_READ" && result.Text == content && result.CharacterCount == content.Length &&
                    result.LineCount == 2401 && result.Detail == "B1|2|1|1|0" && !result.Detail.Contains("private"), "TEST_WHOLE_BUFFER");
                Require(GetForegroundWindow() == beforeFocus && GetClipboardSequenceNumber() == beforeClipboard, "TEST_NO_GLOBAL_MUTATION");
                var second = CreateWindowEx(0, "Edit", "other", Child | Visible | Multiline, 0, 30, 100, 30,
                    scope, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                var ambiguous = Read(root);
                Require(second != IntPtr.Zero && ambiguous.Code == "BUFFER_TEXT_AMBIGUOUS" && ambiguous.Text == null &&
                    ambiguous.Detail == "B1|3|1|2|0|R|0|0|400|250", "TEST_AMBIGUOUS_TEXT");
                DestroyWindow(second);
                var secondScope = CreateWindowEx(0, "Static", "Output", Child | Visible, 0, 0, 100, 100,
                    root, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                var secondEdit = CreateWindowEx(0, "Edit", "other", Child | Visible | Multiline, 0, 0, 50, 50,
                    secondScope, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                var twoScopes = Read(root);
                Rectangle none;
                Require(secondScope != IntPtr.Zero && secondEdit != IntPtr.Zero && twoScopes.Code == "BUFFER_OUTPUT_AMBIGUOUS" &&
                    twoScopes.Detail == "B1|4|2|0|0" && !TryScopeRect(twoScopes.Detail, new Size(420, 280), out none),
                    "TEST_AMBIGUOUS_OUTPUT");
                DestroyWindow(secondScope);
                DestroyWindow(edit);
                foreach (uint style in new[] { Child | Visible | Multiline | Password, Child | Visible, Child | Multiline })
                {
                    var excluded = CreateWindowEx(0, "Edit", "excluded-private-text", style, 0, 20, 100, 50,
                        scope, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    Require(excluded != IntPtr.Zero, "TEST_CREATE_EXCLUDED");
                    var rejected = Read(root);
                    Require(rejected.Code == "BUFFER_STANDARD_TEXT_NOT_FOUND" && rejected.Text == null &&
                        !rejected.Detail.Contains("private"), "TEST_HIDDEN_SINGLELINE_PASSWORD");
                    DestroyWindow(excluded);
                }
                // A non-Edit child keeps one Output scope with no standard text control: its rectangle is reported in
                // root client pixels (geometry and counts only) and parses back exactly.
                DestroyWindow(scope);
                var aimed = CreateWindowEx(0, "Static", "Output", Child | Visible, 12, 8, 300, 200,
                    root, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                var custom = CreateWindowEx(0, "Static", "", Child | Visible, 0, 0, 120, 80,
                    aimed, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Require(aimed != IntPtr.Zero && custom != IntPtr.Zero, "TEST_CREATE_CUSTOM");
                var located = Read(root);
                Rectangle aim;
                Require(located.Code == "BUFFER_STANDARD_TEXT_NOT_FOUND" && located.Text == null &&
                    located.Detail == "B1|2|1|0|0|R|12|8|300|200" && TryScopeRect(located.Detail, new Size(420, 280), out aim) &&
                    aim == new Rectangle(12, 8, 300, 200) && !TryScopeRect(located.Detail, new Size(311, 280), out aim),
                    "TEST_SCOPE_RECT");
                // An owned Unicode RichEdit (RICHEDIT50W) holding 70,000 characters: read whole, twice, and accepted;
                // its Detail carries the class kind, lengths and the stable flag only, never text.
                DestroyWindow(aimed);
                Require(LoadLibrary("Msftedit.dll") != IntPtr.Zero, "TEST_RICHEDIT_LIBRARY");
                var richScope = CreateWindowEx(0, "Static", "Output", Child | Visible, 0, 0, 400, 250,
                    root, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                var rich = CreateWindowEx(0, "RICHEDIT50W", "", Child | Visible | Multiline, 0, 20, 390, 220,
                    richScope, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                UIntPtr limitSet;
                Require(richScope != IntPtr.Zero && rich != IntPtr.Zero &&
                    SendValue(rich, 0x0435, UIntPtr.Zero, new IntPtr(1 << 20), 0x23, 750, out limitSet) != IntPtr.Zero,
                    "TEST_CREATE_RICHEDIT"); // EM_EXLIMITTEXT: the default 32,767-character limit would cut the fixture.
                string large = string.Concat(Enumerable.Repeat("private-richedit-sentinel 0123456789 abcdefghijk\r\n", 1400));
                Require(large.Length == 70000 && SetWindowText(rich, large), "TEST_SET_RICHEDIT");
                var big = Read(root);
                Require(big.Code == "BUFFER_READ" && big.Text != null &&
                    big.Text.Replace("\r\n", "\r") == large.Replace("\r\n", "\r") && big.CharacterCount == big.Text.Length &&
                    big.Detail.StartsWith("B1|", StringComparison.Ordinal) && big.Detail.Contains("|E|W50|") &&
                    big.Detail.EndsWith("|1", StringComparison.Ordinal) && !big.Detail.Contains("private") &&
                    !HasScopeRect(big.Detail), "TEST_RICHEDIT_LARGE");
            }
            finally { DestroyWindow(root); }
        }

        // Synthetic root-client rectangles of a 1920x1009 PowerSI window; no UIA.
        private static void GeometrySelfTest()
        {
            var client = new Size(1920, 1009);
            var caption = new Rectangle(315, 735, 300, 22);
            var dock = new Rectangle(315, 735, 300, 265);
            Rectangle Pick(int names, params Rectangle[] chain) { return SelectOutputGeometry(names, chain, client); }
            Require(Pick(1, caption, dock) == dock && Pick(1, caption, dock, new Rectangle(0, 0, 1920, 1009)) == dock,
                "TEST_GEOMETRY_CAPTION_TO_PANE");
            // Tab item "Output" -> tab strip (wide but 26 px high) -> the tab control's pane; one level more is too far.
            var tabPane = new Rectangle(315, 736, 600, 264);
            Require(Pick(1, new Rectangle(318, 738, 64, 24), new Rectangle(315, 736, 600, 26), tabPane) == tabPane &&
                Pick(1, new Rectangle(320, 740, 60, 20), new Rectangle(318, 738, 64, 24), new Rectangle(315, 736, 600, 26), tabPane).IsEmpty,
                "TEST_GEOMETRY_TAB_LABEL");
            Require(Pick(1, dock, new Rectangle(300, 700, 700, 300)) == dock && Pick(1, new Rectangle(0, 0, 240, 120)) == new Rectangle(0, 0, 240, 120),
                "TEST_GEOMETRY_ALREADY_LARGE");
            // The first large enough element decides: the whole window or main area refuses rather than looking further up.
            Require(Pick(1, caption, new Rectangle(0, 0, 1920, 1009)).IsEmpty && Pick(1, caption, new Rectangle(0, 40, 1729, 600), dock).IsEmpty &&
                Pick(1, caption, new Rectangle(10, 0, 600, 909)).IsEmpty && Pick(1, new Rectangle(0, 0, 1728, 908)) == new Rectangle(0, 0, 1728, 908),
                "TEST_GEOMETRY_TOO_LARGE");
            Require(Pick(2, caption, dock).IsEmpty && Pick(0, caption, dock).IsEmpty, "TEST_GEOMETRY_NAMES");
            Require(Pick(1, caption, new Rectangle(1700, 735, 300, 265)).IsEmpty && Pick(1, caption, new Rectangle(-5, 735, 300, 265)).IsEmpty &&
                Pick(1, caption, new Rectangle(315, 800, 300, 265)).IsEmpty && Pick(1, caption, new Rectangle(315, -1, 300, 265)).IsEmpty,
                "TEST_GEOMETRY_OUTSIDE_CLIENT");
            Require(Pick(1, new Rectangle(0, 0, 239, 400), new Rectangle(0, 0, 400, 119)).IsEmpty && Pick(1, caption).IsEmpty &&
                Pick(1).IsEmpty && SelectOutputGeometry(1, null, client).IsEmpty && SelectOutputGeometry(1, new[] { dock }, Size.Empty).IsEmpty,
                "TEST_GEOMETRY_NONE");
            // At most 3 elements: the "Output" element, its parent and its grandparent.
            var small = Rectangle.Empty;
            Require(Pick(1, caption, small, dock) == dock && Pick(1, caption, small, small, dock).IsEmpty &&
                Pick(1, caption, small, small, small, dock).IsEmpty, "TEST_GEOMETRY_THREE_ELEMENTS");
            // BUFFER_OUTPUT_NO_HWND carries the same "|R|x|y|w|h" root-client shape as the HWND scope codes.
            var detail = Detail(37, 0, 0, 143) + RectSuffix(dock);
            Rectangle parsed;
            Require(detail == "B1|37|0|0|143|R|315|735|300|265" && TryScopeRect(detail, client, out parsed) && parsed == dock &&
                HasScopeRect(detail) && !HasScopeRect(Detail(37, 0, 0, 143)), "TEST_GEOMETRY_SCOPE_RECT");
        }

        private static void LargeReadSelfTest()
        {
            const int Declared = 70000, Capacity = Declared + 2;
            const bool CrLf = true, CrOnly = false, Same = true, Differs = false;
            // declared, copied, capacity, line terminators in the copy, copy holds a CR+LF pair, second read identical
            // -> null (accept) or the failure code.
            var table = new[]
            {
                Tuple.Create(Declared, Declared, Capacity, 0, CrOnly, Same, (string)null),                   // exact whole read
                Tuple.Create(Declared, Declared, Capacity, 1400, CrLf, Same, (string)null),                  // exact, CR+LF text
                Tuple.Create(Declared, Declared - 1400, Capacity, 1400, CrOnly, Same, (string)null),         // CR-only over-count
                Tuple.Create(Declared, Declared - 1399, Capacity, 1400, CrOnly, Same, "BUFFER_INCOMPLETE"),  // line count - 1
                Tuple.Create(Declared, Declared - 1401, Capacity, 1400, CrOnly, Same, "BUFFER_INCOMPLETE"),  // more than the lines
                Tuple.Create(Declared, Declared - 1, Capacity, 1400, CrLf, Same, "BUFFER_INCOMPLETE"),       // CR+LF copy, short by 1
                Tuple.Create(Declared, Declared - 1400, Capacity, 1400, CrLf, Same, "BUFFER_INCOMPLETE"),    // CR+LF copy, short by lines
                Tuple.Create(Declared, Declared - 5, Capacity, 0, CrOnly, Same, "BUFFER_INCOMPLETE"),        // no lines to explain it
                Tuple.Create(Declared, Declared + 1, Capacity, 0, CrOnly, Same, "BUFFER_INCOMPLETE"),        // filled the buffer: maybe cut
                Tuple.Create(Declared, Declared + 1, Capacity + 1, 0, CrOnly, Same, "BUFFER_INCOMPLETE"),    // more than declared
                Tuple.Create(Declared, 65535, Capacity, 4465, CrOnly, Same, "BUFFER_INCOMPLETE"),            // classic 64K cut
                Tuple.Create(Declared, 65536, Capacity, 4464, CrOnly, Same, (string)null),                   // just past 64K, explained
                Tuple.Create(Declared, 0, Capacity, 0, CrOnly, Same, "BUFFER_INCOMPLETE"),
                Tuple.Create(Declared, -1, Capacity, 0, CrOnly, Same, "BUFFER_INCOMPLETE"),
                Tuple.Create(Declared, 69000, Capacity, -1, CrOnly, Same, "BUFFER_INCOMPLETE"),
                Tuple.Create(Declared, Declared, Capacity, 0, CrOnly, Differs, "BUFFER_RICHEDIT_UNSTABLE"),   // second read differed
                Tuple.Create(Declared, Declared - 1400, Capacity, 1400, CrOnly, Differs, "BUFFER_RICHEDIT_UNSTABLE"),
                Tuple.Create(Declared, Declared, Declared + 1, 0, CrOnly, Same, "BUFFER_RICHEDIT_LARGE_UNSUPPORTED"), // buffer too small
                Tuple.Create(65535, 65535, 65537, 0, CrOnly, Same, "BUFFER_RICHEDIT_LARGE_UNSUPPORTED"),     // not a large read
                Tuple.Create(MaxCharacters, MaxCharacters, MaxCharacters + 2, 0, CrOnly, Same, (string)null), // the 8 Mi cap itself
                Tuple.Create(MaxCharacters + 1, MaxCharacters, MaxCharacters + 3, 1, CrOnly, Same, "BUFFER_TOO_LARGE"),
                Tuple.Create(Declared, MaxCharacters + 1, MaxCharacters + 3, 0, CrOnly, Same, "BUFFER_TOO_LARGE"),
            };
            foreach (var row in table)
                Require(LargeRichEditVerdict(row.Item1, row.Item2, row.Item3, row.Item4, row.Item5, row.Item6) == row.Item7, "TEST_RICHEDIT_VERDICT");
            Require(LineBreaks("") == 0 && LineBreaks(null) == 0 && LineBreaks("a") == 0 && LineBreaks("a\r\nb\rc\nd\r") == 4,
                "TEST_RICHEDIT_LINE_BREAKS");
            Require(LargeReadSuffix("RICHEDIT50W", 70000, 68600, true) == "|E|W50|70000|68600|1" &&
                LargeReadSuffix("RichEdit20W", 70000, 70000, false) == "|E|W20|70000|70000|0", "TEST_RICHEDIT_DETAIL");
        }

        private static void ScopeRectParseSelfTest()
        {
            var client = new Size(800, 600);
            Rectangle rect;
            Require(TryScopeRect("B1|5|1|0|0|R|10|20|300|200", client, out rect) && rect == new Rectangle(10, 20, 300, 200),
                "TEST_SCOPE_RECT_PARSE");
            Require(TryScopeRect("B1|5|1|2|0|R|0|0|800|600", client, out rect) && rect == new Rectangle(0, 0, 800, 600),
                "TEST_SCOPE_RECT_EDGE");
            foreach (var bad in new[] { null, "", "NONE", "B1|2|1|1|0", "B1|5|1|0|0|R", "B1|5|1|0|0|R|10|20|300",
                "B1|5|1|0|0|R|10|20|300|200|7", "B1|5|1|0|0|R|10|20|300|200|R|1|1|1|1", "B1|5|1|0|0|R|10|20|300|200|",
                "B1|5|1|0|0|R|-1|20|300|200", "B1|5|1|0|0|R|10|-20|300|200", "B1|5|1|0|0|R|10|20|-300|200",
                "B1|5|1|0|0|R|10|20|300|-200", "B1|5|1|0|0|R|10|20|0|200", "B1|5|1|0|0|R|10|20|300|0",
                "B1|5|1|0|0|R|501|20|300|200", "B1|5|1|0|0|R|10|401|300|200", "B1|5|1|0|0|R|0|0|801|600",
                "B1|5|1|0|0|R|2147483647|0|2147483647|1", "B1|5|1|0|0|R|x|20|300|200", "B1|5|1|0|0|R|10|20|3e2|200",
                "B1|5|1|0|0|R| 10|20|300|200", "B1|5|1|0|0|R|+10|20|300|200", "B1|5|1|0|0|R|010|20|300|200",
                "B1|5|1|0|0|R|10|20|300|99999999999", "B1|5|1|0|0|Q|10|20|300|200", "B2|5|1|0|0|R|10|20|300|200",
                "B1|x|1|0|0|R|10|20|300|200", "B1|5|1|0|-1|R|10|20|300|200", "SOURCE_PID_MATCH|R|10|20|300|200|1|1|1" })
                Require(!TryScopeRect(bad, client, out rect) && rect == Rectangle.Empty, "TEST_SCOPE_RECT_REJECT");
            Require(!TryScopeRect("B1|5|1|0|0|R|0|0|1|1", Size.Empty, out rect) &&
                !TryScopeRect("B1|5|1|0|0|R|0|0|1|1", new Size(-5, 600), out rect), "TEST_SCOPE_RECT_EMPTY_CLIENT");
            // A failed large-RichEdit read keeps its scope rectangle after the "|E|..." block.
            Require(TryScopeRect("B1|9|1|1|0|E|W50|70000|69990|1|R|12|8|300|200", client, out rect) &&
                rect == new Rectangle(12, 8, 300, 200) && TryScopeRect("B1|9|1|1|0|E|W20|70000|65535|0|R|0|0|800|600", client, out rect),
                "TEST_SCOPE_RECT_LARGE_READ");
            foreach (var bad in new[] { "B1|9|1|1|0|E|W50|70000|69990|1", "B1|9|1|1|0|E|W30|70000|69990|1|R|12|8|300|200",
                "B1|9|1|1|0|E|W50|070000|69990|1|R|12|8|300|200", "B1|9|1|1|0|E|W50|70000|-1|1|R|12|8|300|200",
                "B1|9|1|1|0|E|W50|70000|69990|2|R|12|8|300|200", "B1|9|1|1|0|E|W50|70000|69990|1|R|12|8|300",
                "B1|9|1|1|0|R|12|8|300|200|E|W50|70000|69990|1", "B1|9|1|1|0|E|W50|70000|69990|1|Q|12|8|300|200",
                "B1|9|1|1|0|X|W50|70000|69990|1|R|12|8|300|200", "B1|9|1|1|0|E|W50|70000|69990|1|R|12|8|300|200|1" })
                Require(!TryScopeRect(bad, client, out rect) && rect == Rectangle.Empty, "TEST_SCOPE_RECT_LARGE_REJECT");
            Require(HasScopeRect("B1|2|1|0|0|R|12|8|300|200") && HasScopeRect("B1|9|1|1|0|E|W50|70000|69990|1|R|12|8|300|200") &&
                !HasScopeRect("B1|2|1|1|0") && !HasScopeRect("B1|9|1|1|0|E|W50|70000|69990|1") && !HasScopeRect(null) &&
                !HasScopeRect("NONE"), "TEST_HAS_SCOPE_RECT");
        }

        private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr root, WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] private static extern bool IsWindowUnicode(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW")] private static extern bool SetWindowText(IntPtr window, string text);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendText(IntPtr window, uint message, UIntPtr wParam, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendValue(IntPtr window, uint message, UIntPtr wParam, IntPtr value, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width,
            int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryW")] private static extern IntPtr LoadLibrary(string name);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    }
}
