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

    // ponytail: standard HWND text contract only; custom controls/large RichEdit need a separately verified copy path.
    // No input, activation, selection changes or clipboard access. Caller isolates UIA/provider hangs in its worker.
    internal static class PowerSiOutputBuffer
    {
        internal const int MaxCharacters = 8 * 1024 * 1024;
        private const int MaxNodes = 1536;
        private const int MaxUiaNodes = 512;
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
                if (scopes.Count == 0)
                    DiscoverUiaScopes(root, pid, scopes, ref uiaVisited);
                var outerScopes = scopes.Where(s => !scopes.Any(other => other != s && IsChild(other, s))).ToArray();
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
                int length = ReadLength(selected.Handle);
                ValidateLength(length, IsRichEdit(selected.Class));
                var buffer = new StringBuilder(checked(length + 2));
                UIntPtr copied;
                Require(SendText(selected.Handle, 0x000D, new UIntPtr((uint)buffer.Capacity), buffer,
                    0x23, 750, out copied) != IntPtr.Zero, "BUFFER_READ_TIMEOUT");
                Require(copied.ToUInt64() <= MaxCharacters, "BUFFER_TOO_LARGE");
                string text = buffer.ToString();
                Require(copied.ToUInt64() == (ulong)text.Length, "BUFFER_INCOMPLETE");
                int after = ReadLength(selected.Handle);
                ValidateLength(after, IsRichEdit(selected.Class));
                Require(length == after, "BUFFER_CHANGED_DURING_READ");
                Require(text.Length < buffer.Capacity - 1 && (length == 0 || text.Length > 0), "BUFFER_INCOMPLETE");
                // ANSI conversion can overestimate WM_GETTEXTLENGTH; Unicode controls must agree exactly.
                Require(!IsWindowUnicode(selected.Handle) || text.Length == length, "BUFFER_INCOMPLETE");
                ValidateTarget(root, output, selected, pid);
                Require(text.IndexOf('\0') < 0, "BUFFER_TEXT_INVALID");
                return new OutputBufferResult { Code = text.Length == 0 ? "BUFFER_EMPTY" : "BUFFER_READ",
                    Method = "NATIVE_WM_GETTEXT", Text = text, CharacterCount = text.Length, LineCount = CountLines(text),
                    Detail = Detail(nodes.Count, scopes.Count, textCandidates, uiaVisited) };
            }
            catch (ReadFailure ex) { code = ex.Message; }
            catch { code = "BUFFER_FAILED"; }
            // One identified Output HWND whose text could not be read whole: add its rectangle (geometry only) so a
            // separately guarded copy fallback can aim without vision. Every other result keeps the 5-field B1 shape.
            var detail = Detail(nodes.Count, scopes.Count, textCandidates, uiaVisited);
            if (output != IntPtr.Zero && ScopeRectCodes.Contains(code, StringComparer.Ordinal)) detail += ScopeRectSuffix(root, output);
            return new OutputBufferResult { Code = code, Method = "NATIVE_WM_GETTEXT", Detail = detail };
        }

        private static readonly string[] ScopeRectCodes = { "BUFFER_STANDARD_TEXT_NOT_FOUND", "BUFFER_TEXT_AMBIGUOUS",
            "BUFFER_RICHEDIT_LARGE_UNSUPPORTED", "BUFFER_READ_TIMEOUT", "BUFFER_INCOMPLETE", "BUFFER_CHANGED_DURING_READ" };
        private static string Detail(int nodes, int scopes, int candidates, int uia)
        {
            return string.Format(CultureInfo.InvariantCulture, "B1|{0}|{1}|{2}|{3}", nodes, scopes, candidates, uia);
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
        // Parses the "|R|x|y|w|h" suffix of a 10-field B1 Detail. False for the 5-field shape, any extra/malformed field,
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
                int count, x, y, w, h;
                if (p.Length != 10 || p[0] != "B1" || p[5] != "R" || !Number(1, out count) || !Number(2, out count) ||
                    !Number(3, out count) || !Number(4, out count) || !Number(6, out x) || !Number(7, out y) ||
                    !Number(8, out w) || !Number(9, out h) || w == 0 || h == 0 ||
                    (long)x + w > clientSize.Width || (long)y + h > clientSize.Height) return false;
                rect = new Rectangle(x, y, w, h);
                return true;
            }
            catch { rect = Rectangle.Empty; return false; }
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
        private static void ValidateLength(int length, bool richEdit)
        {
            Require(length >= 0 && length <= MaxCharacters, "BUFFER_TOO_LARGE");
            // WM_GETTEXT's documented RichEdit >64K alternative requires a separately reviewed cross-process path.
            Require(!richEdit || length <= 65535, "BUFFER_RICHEDIT_LARGE_UNSUPPORTED");
        }
        private static bool IsRichEdit(string name)
        {
            return string.Equals(name, "RichEdit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "RichEdit20A", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "RichEdit20W", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "RICHEDIT50W", StringComparison.OrdinalIgnoreCase);
        }
        private static bool IsTextClass(string name)
        { return string.Equals(name, "Edit", StringComparison.OrdinalIgnoreCase) || IsRichEdit(name); }
        private static bool IsInteractiveClass(string name)
        {
            return new[] { "Button", "ComboBox", "ComboBoxEx32", "SysTabControl32", "SysTreeView32", "SysListView32", "ToolbarWindow32", "#32768" }
                .Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        private static void DiscoverUiaScopes(IntPtr root, uint pid, HashSet<IntPtr> scopes, ref int visited)
        {
            try
            {
                var request = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Automation.RawViewCondition,
                    AutomationElementMode = AutomationElementMode.Full };
                request.Add(AutomationElement.NameProperty); request.Add(AutomationElement.ControlTypeProperty);
                request.Add(AutomationElement.ProcessIdProperty); request.Add(AutomationElement.NativeWindowHandleProperty);
                request.Add(AutomationElement.IsOffscreenProperty);
                WalkUia(AutomationElement.FromHandle(root).GetUpdatedCache(request), null, 0, root, pid, scopes, request, ref visited);
            }
            catch (ReadFailure) { throw; }
            catch { throw new ReadFailure("BUFFER_UIA_UNAVAILABLE"); }
        }
        private static void WalkUia(AutomationElement element, AutomationElement parent, int depth, IntPtr root, uint pid,
            HashSet<IntPtr> scopes, CacheRequest request, ref int visited)
        {
            Require(depth <= 20 && visited++ < MaxUiaNodes, "BUFFER_UIA_LIMIT");
            Require((int)element.GetCachedPropertyValue(AutomationElement.ProcessIdProperty) == pid, "BUFFER_TARGET_CHANGED");
            string name = element.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "";
            var type = (ControlType)element.GetCachedPropertyValue(AutomationElement.ControlTypeProperty);
            bool offscreen = (bool)element.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty);
            bool marker = !offscreen && string.Equals(name.Trim(), "Output", StringComparison.OrdinalIgnoreCase) &&
                (type == ControlType.Pane || type == ControlType.Group || type == ControlType.Custom ||
                 type == ControlType.Text || type == ControlType.TitleBar || type == ControlType.Window);
            if (marker)
            {
                var scope = type == ControlType.Text || type == ControlType.TitleBar ? parent : element;
                if (scope != null)
                {
                    var handle = new IntPtr((int)scope.GetCachedPropertyValue(AutomationElement.NativeWindowHandleProperty));
                    uint owner;
                    if (handle != IntPtr.Zero && handle != root && IsChild(root, handle) && IsWindowVisible(handle) &&
                        GetWindowThreadProcessId(handle, out owner) != 0 && owner == pid) scopes.Add(handle);
                }
                return;
            }
            // Same bounded dock-discovery pruning as PowerSiObservation; never scan individual net/log rows.
            if (type == ControlType.Tree || type == ControlType.List || type == ControlType.DataGrid || type == ControlType.Table ||
                type == ControlType.ComboBox || type == ControlType.Menu || type == ControlType.MenuBar || type == ControlType.ToolBar ||
                type == ControlType.Document || type == ControlType.Edit) return;
            var walker = TreeWalker.RawViewWalker;
            for (var child = walker.GetFirstChild(element, request); child != null; child = walker.GetNextSibling(child, request))
                WalkUia(child, element, depth + 1, root, pid, scopes, request, ref visited);
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
            try { ValidateLength(MaxCharacters + 1, false); throw new InvalidOperationException("Oversize buffer accepted."); }
            catch (ReadFailure ex) { Require(ex.Message == "BUFFER_TOO_LARGE", "TEST_SIZE"); }
            try { ValidateLength(65536, true); throw new InvalidOperationException("Large RichEdit accepted."); }
            catch (ReadFailure ex) { Require(ex.Message == "BUFFER_RICHEDIT_LARGE_UNSUPPORTED", "TEST_RICHEDIT_SIZE"); }
            ScopeRectParseSelfTest();
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
            }
            finally { DestroyWindow(root); }
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
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    }
}
