using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RemoteMonitorLink;

namespace RemoteMonitorSlave
{
    // API: the only writing input path in this application. Everything else stays read-only.
    //
    // OutputAnchor
    //   internal sealed class with fields Pid, StartUtcTicks, SessionId, ClientPoint, ClientSize, LearnedUtc, Body.
    //   string Serialize()                      -> "A1|pid|ticks|session|x|y|w|h|learnedTicks" while Body is empty
    //                                              (what the copy worker learns), "A2|<the same nine>|bx|by|bw|bh"
    //                                              once the UI confirmed a body around the point. Uppercase/digits/pipes only.
    //   static bool TryParse(string, out OutputAnchor)   accepts both forms; Matches() still ignores Body.
    //
    // OutputAutoCopy
    //   const string WorkerArgument             = "--powersi-output-auto-copy".
    //   argv of the worker: --powersi-output-auto-copy <sessionId> <pid:startTicks,...> <A2|...>
    //   string LearnAnchor(IntPtr root, ProcessInventory inventory)
    //       Sampled by the existing --powersi-output-copy worker right after TryReadUserCopy succeeded.
    //       Returns "A1|..." or "ANCHOR_NONE|<REASON>" (FOREGROUND, IDENTITY, CURSOR, CLIENT, OUTSIDE, SERIALIZE, SAMPLE).
    //       The copy worker appends it to the OB1 detail as "SOURCE_PID_MATCH|" + this value.
    //   OutputAnchor AnchorOf(OutputBufferResult result)
    //       Reads that suffix back on the UI side (null when the worker learned nothing). The anchor is only a hint;
    //       phase 2 must confirm it with OutputPaneImage.FindBodyAt on the run's frame before storing it.
    //   OutputAnchor AnchorFromVision(ProcessInventory, PowerSiObservation)
    //       Builds a confirmed A2 anchor from a successful local-only LOCATE_ONLY result; no cursor sample or input.
    //   OutputBufferResult RunWorker(string[] args)
    //       Worker body, dispatched from OutputBufferCapture.TryRunWorker. Never throws for known failures; returns
    //       Code=AUTO_COPY_* / Method=NONE / Detail=<metadata or NONE>. Success: Code=AUTO_COPY_READ,
    //       Method=AUTO_CLIPBOARD, Detail="A2|anchorX|anchorY|bodyX|bodyY|bodyW|bodyH|clickScreenX|clickScreenY|B2|...".
    //       clickX/clickY are physical SCREEN coordinates of the injected click; the body rectangle is client-relative.
    //   Task<OutputBufferResult> AutoCopyAsync(ProcessInventory, OutputAnchor, CancellationToken)
    //       UI-side helper. Spawns the worker process (8 s budget) so a hang cannot freeze the UI, and parses OB1.
    //       The anchor MUST carry a confirmed Body (A2); an A1 anchor is refused with AUTO_COPY_REQUEST_INVALID.
    //   byte[] FrameOf(OutputBufferResult result)
    //       The latest worker capture on failure, or the cleared pre-Ctrl+A capture on success. It travels
    //       in the optional 6th OB1 field; OutputBufferCapture.Serialize/Parse use AttachFrame/FrameOf for it.
    //   void SelfTest()                         Called from OutputBufferCapture.SelfTest(); Program.cs needs no change.
    //
    // Worker order: fresh capture/body validation BEFORE any input, then resolve/recheck identity,
    //   geometry, hit test and idle input -> click -> clean capture -> Ctrl+A/C -> guarded deselection click.
    //   Highlighted/ambiguous bodies fail closed; we never click an old rectangle to make validation pass.
    //   Closing the parent's stdin pipe requests cancellation; the host grants cleanup time before termination.
    //
    // Behaviour notes for phase 2:
    //   * The clipboard is NOT restored after an auto copy (the copied Output text is the result we hand to the caller).
    //   * The cursor position is saved before the click and restored best effort at the end of the worker, success or not.
    //   * Two clicks and one Ctrl+A/C; cancellation or input interference skips further input, including cleanup.
    //   * Every stored coordinate is re-verified live (window identity, client size, body search, hit test, foreground).
    //   * The live part of SelfTest() replaces the current clipboard content with its own sample text and does not save
    //     or restore the previous content; it is skipped when no interactive foreground window can be obtained.
    internal sealed class OutputAnchor
    {
        internal const int MaxClientSide = 4096;

        internal int Pid;
        internal long StartUtcTicks;
        internal int SessionId;
        internal Point ClientPoint;
        internal Size ClientSize;
        internal DateTime LearnedUtc;
        // Client-pixel Output body this point was confirmed on. Rectangle.Empty until the UI confirmed it; only an
        // anchor with a body may be used for auto copy, and the worker clicks this body's centre.
        internal Rectangle Body;

        internal bool HasBody { get { return Body.Width > 0 && Body.Height > 0; } }

        internal string Serialize()
        {
            var head = string.Join("|", HasBody ? "A2" : "A1", Text(Pid), Text(StartUtcTicks), Text(SessionId),
                Text(ClientPoint.X), Text(ClientPoint.Y), Text(ClientSize.Width), Text(ClientSize.Height), Text(LearnedUtc.Ticks));
            return HasBody ? head + "|" + string.Join("|", Text(Body.X), Text(Body.Y), Text(Body.Width), Text(Body.Height)) : head;
        }

        internal static bool TryParse(string text, out OutputAnchor anchor)
        {
            anchor = null;
            if (text == null || text.Length > 240) return false;
            var parts = text.Split('|');
            bool withBody = parts.Length == 13 && parts[0] == "A2";
            if (!withBody && (parts.Length != 9 || parts[0] != "A1")) return false;
            int pid, session, x, y, width, height;
            long start, learned;
            if (!Number(parts[1], out pid) || !Number(parts[3], out session) || !Number(parts[4], out x) ||
                !Number(parts[5], out y) || !Number(parts[6], out width) || !Number(parts[7], out height) ||
                !Number(parts[2], out start) || !Number(parts[8], out learned)) return false;
            if (pid < 1 || start < 1 || start > DateTime.MaxValue.Ticks || learned < 1 || learned > DateTime.MaxValue.Ticks ||
                width < 1 || height < 1 || width > MaxClientSide || height > MaxClientSide ||
                x < 0 || y < 0 || x >= width || y >= height) return false;
            var body = Rectangle.Empty;
            if (withBody)
            {
                int bx, by, bw, bh;
                if (!Number(parts[9], out bx) || !Number(parts[10], out by) || !Number(parts[11], out bw) ||
                    !Number(parts[12], out bh)) return false;
                if (bw < 1 || bh < 1 || bx < 0 || by < 0 || bx > width - bw || by > height - bh) return false;
                body = new Rectangle(bx, by, bw, bh);
                if (!body.Contains(new Point(x, y))) return false; // The learned point must lie inside its own body.
            }
            anchor = new OutputAnchor { Pid = pid, StartUtcTicks = start, SessionId = session,
                ClientPoint = new Point(x, y), ClientSize = new Size(width, height),
                LearnedUtc = new DateTime(learned, DateTimeKind.Utc), Body = body };
            return true;
        }

        internal bool Matches(OutputAnchor other)
        {
            return other != null && other.Pid == Pid && other.StartUtcTicks == StartUtcTicks && other.SessionId == SessionId &&
                other.ClientPoint == ClientPoint && other.ClientSize == ClientSize && other.LearnedUtc == LearnedUtc;
        }

        private static string Text(long value) { return value.ToString(CultureInfo.InvariantCulture); }

        private static bool Number(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
                value.ToString(CultureInfo.InvariantCulture) == text;
        }

        private static bool Number(string text, out long value)
        {
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
                value.ToString(CultureInfo.InvariantCulture) == text;
        }
    }

    internal static class OutputAutoCopy
    {
        internal const string WorkerArgument = "--powersi-output-auto-copy";
        internal const int BudgetMilliseconds = 8000;
        private const int ForegroundWaitMilliseconds = 600;
        private const int ChordGapMilliseconds = 80;
        private const int ClipboardWaitMilliseconds = 2000;
        internal const int MaxFramePng = 8 * 1024 * 1024;  // Same bound the capture worker enforces on a frame PNG.
        private const int SettleMilliseconds = 150;
        private const int BodyTolerance = 8;          // Per-edge client-pixel drift still accepted as "the same body".

        // Pre-input capture, kept so a failure can hand it back to the UI (worker process only).
        private static byte[] capturedFrame;
        // The 6th OB1 field belongs to the result object, not to OutputBufferResult (a Link type that must not change).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OutputBufferResult, byte[]> Frames =
            new System.Runtime.CompilerServices.ConditionalWeakTable<OutputBufferResult, byte[]>();

        private const uint InputMouse = 0, InputKeyboard = 1;
        private const uint MouseLeftDown = 0x0002, MouseLeftUp = 0x0004, MouseRightDown = 0x0008, MouseRightUp = 0x0010;
        private const uint KeyScanCode = 0x0008, KeyUp = 0x0002;
        private const ushort VirtualControl = 0x11, VirtualA = 0x41, VirtualC = 0x43;
        private const uint RootAncestor = 2; // GA_ROOT
        private const int SwapButtonMetric = 23; // SM_SWAPBUTTON
        // Physical mouse buttons and every modifier that could turn our click or chord into a different command.
        private static readonly int[] HeldKeys = { 0x01, 0x02, 0x04, 0x05, 0x06, 0x10, 0x11, 0x12, 0x5B, 0x5C };

        // ---------------------------------------------------------------- anchor learning (read-only sampling)

        internal static string LearnAnchor(IntPtr root, ProcessInventory inventory)
        {
            try
            {
                if (root == IntPtr.Zero || inventory == null || inventory.Items == null) return "ANCHOR_NONE|IDENTITY";
                var foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero || (foreground != root && RootOf(foreground) != root)) return "ANCHOR_NONE|FOREGROUND";
                uint pid;
                if (GetWindowThreadProcessId(root, out pid) == 0 || pid == 0 || pid > int.MaxValue) return "ANCHOR_NONE|IDENTITY";
                var identity = inventory.Items.SingleOrDefault(p => p.Pid == (int)pid);
                if (identity == null || !identity.StartUtcTicks.HasValue || identity.StartUtcTicks.Value < 1) return "ANCHOR_NONE|IDENTITY";
                NativePoint cursor;
                if (!GetPhysicalCursorPos(out cursor) || !ScreenToClient(root, ref cursor)) return "ANCHOR_NONE|CURSOR";
                NativeRect client;
                if (!GetClientRect(root, out client)) return "ANCHOR_NONE|CLIENT";
                if (client.Right < 1 || client.Bottom < 1 || client.Right > OutputAnchor.MaxClientSide ||
                    client.Bottom > OutputAnchor.MaxClientSide) return "ANCHOR_NONE|CLIENT";
                if (cursor.X < 0 || cursor.Y < 0 || cursor.X >= client.Right || cursor.Y >= client.Bottom) return "ANCHOR_NONE|OUTSIDE";
                var anchor = new OutputAnchor { Pid = (int)pid, StartUtcTicks = identity.StartUtcTicks.Value,
                    SessionId = inventory.SessionId, ClientPoint = new Point(cursor.X, cursor.Y),
                    ClientSize = new Size(client.Right, client.Bottom), LearnedUtc = DateTime.UtcNow };
                var text = anchor.Serialize();
                OutputAnchor parsed;
                return OutputAnchor.TryParse(text, out parsed) && parsed.Matches(anchor) ? text : "ANCHOR_NONE|SERIALIZE";
            }
            catch { return "ANCHOR_NONE|SAMPLE"; }
        }

        internal static OutputAnchor AnchorOf(OutputBufferResult result)
        {
            if (result == null || result.Detail == null) return null;
            int separator = result.Detail.IndexOf('|');
            if (separator < 0) return null;
            OutputAnchor anchor;
            return OutputAnchor.TryParse(result.Detail.Substring(separator + 1), out anchor) ? anchor : null;
        }

        internal static OutputAnchor AnchorFromVision(ProcessInventory inventory, PowerSiObservation located)
        {
            try
            {
                if (inventory == null || located == null) return null;
                inventory.Validate();
                if (inventory.Items.Length != 1 || inventory.Omitted != 0) return null;
                var identity = inventory.Items[0];
                if (!ProcessInventory.IsPowerSiName(identity.Name) || !identity.StartUtcTicks.HasValue ||
                    identity.StartUtcTicks.Value < 1 || located.Code != "OUTPUT_UNAVAILABLE" || !located.IsVision ||
                    located.LocalVisionMode != "LOCATE_ONLY" || located.LocalFailure != null || !located.CapturedUtc.HasValue ||
                    located.LocalFrame == null || located.LocalFrame.Png == null || located.LocalImage == null ||
                    located.LocalPaneImage == null || !ReferenceEquals(located.LocalFullImage, located.LocalFrame.Png) ||
                    located.LocalFrameSize != located.LocalFrame.PixelSize || located.LocalFrameSize.IsEmpty || located.LocalVisibleEmpty ||
                    located.LocalFrame.CapturedUtc != located.CapturedUtc.Value) return null;
                var body = located.LocalOutputBody;
                var size = located.LocalFrameSize;
                if (body.X < 0 || body.Y < 0 || body.Width < 1 || body.Height < 1 ||
                    body.Width > size.Width || body.Height > size.Height ||
                    body.X > size.Width - body.Width || body.Y > size.Height - body.Height) return null;
                var center = new Point(body.X + body.Width / 2, body.Y + body.Height / 2);
                var anchor = new OutputAnchor { Pid = identity.Pid, StartUtcTicks = identity.StartUtcTicks.Value,
                    SessionId = inventory.SessionId, ClientPoint = center, ClientSize = size,
                    LearnedUtc = located.CapturedUtc.Value, Body = body };
                OutputAnchor parsed;
                return OutputAnchor.TryParse(anchor.Serialize(), out parsed) && parsed.Matches(anchor) && parsed.Body == body
                    ? parsed : null;
            }
            catch { return null; }
        }

        // Worker capture that travels in the optional 6th OB1 field; never a log or network payload.
        internal static void AttachFrame(OutputBufferResult result, byte[] png)
        {
            if (result == null || png == null || png.Length == 0) return;
            try { Frames.Remove(result); Frames.Add(result, png); } catch { }
        }

        internal static byte[] FrameOf(OutputBufferResult result)
        {
            byte[] png;
            return result != null && Frames.TryGetValue(result, out png) ? png : null;
        }

        internal static PowerSiFrame CleanFrameOf(OutputBufferResult result)
        {
            if (result?.Code != "AUTO_COPY_READ" || result.Method != "AUTO_CLIPBOARD" || result.Text == null)
                throw new InvalidDataException("AUTO_COPY_FRAME_INVALID");
            var png = FrameOf(result);
            var parts = (result.Detail ?? "").Split('|');
            if (png == null || parts.Length < 2 || parts[parts.Length - 2] != "CLEAN_FRAME")
                throw new InvalidDataException("AUTO_COPY_FRAME_INVALID");
            return PowerSiScreenCapture.Parse("SC1|" + parts[parts.Length - 1] + "|" + Convert.ToBase64String(png));
        }

        // ---------------------------------------------------------------- UI side

        internal static Task<OutputBufferResult> AutoCopyAsync(ProcessInventory inventory, OutputAnchor anchor,
            CancellationToken cancellation)
        {
            string arguments;
            try
            {
                if (inventory == null || anchor == null) return Task.FromResult(Result("AUTO_COPY_REQUEST_INVALID", null));
                inventory.Validate();
                if (inventory.Items.Length == 0 || inventory.Omitted != 0 ||
                    inventory.Items.Any(p => !ProcessInventory.IsPowerSiName(p.Name) || !p.StartUtcTicks.HasValue))
                    return Task.FromResult(Result("AUTO_COPY_REQUEST_INVALID", null));
                OutputAnchor parsed;
                var serialized = anchor.Serialize();
                // Only a confirmed body may be clicked: an unconfirmed (A1) anchor never reaches the worker.
                if (!anchor.HasBody || !OutputAnchor.TryParse(serialized, out parsed) || !parsed.Matches(anchor) ||
                    parsed.Body != anchor.Body)
                    return Task.FromResult(Result("AUTO_COPY_REQUEST_INVALID", null));
                if (inventory.SessionId != anchor.SessionId ||
                    !inventory.Items.Any(p => p.Pid == anchor.Pid && p.StartUtcTicks.Value == anchor.StartUtcTicks))
                    return Task.FromResult(Result("AUTO_COPY_ANCHOR_STALE", null));
                var identities = string.Join(",", inventory.Items.Select(p => p.Pid.ToString(CultureInfo.InvariantCulture) + ":" +
                    p.StartUtcTicks.Value.ToString(CultureInfo.InvariantCulture)));
                arguments = WorkerArgument + " " + inventory.SessionId.ToString(CultureInfo.InvariantCulture) + " " + identities +
                    " " + serialized;
            }
            catch (OperationCanceledException) { throw; }
            catch { return Task.FromResult(Result("AUTO_COPY_REQUEST_INVALID", null)); }
            return OutputBufferCapture.RunWorkerAsync(arguments, BudgetMilliseconds, "AUTO_COPY_TIMEOUT",
                "AUTO_COPY_WORKER_FAILED", cancellation);
        }

        // ---------------------------------------------------------------- worker

        internal static OutputBufferResult RunWorker(string[] args)
        {
            capturedFrame = null;
            using (var cancellation = new CancellationTokenSource(BudgetMilliseconds))
            try
            {
                // Owned-process cancellation check: this mode cannot resolve a target or inject input.
                if (args != null && args.Length == 2 && args[0] == WorkerArgument && args[1] == "--self-test-cancel")
                {
                    WatchParent(cancellation);
                    Console.WriteLine("READY"); Console.Out.Flush();
                    try { Pause(BudgetMilliseconds, cancellation.Token); }
                    catch (OperationCanceledException) { }
                    finally { capturedFrame = null; }
                    return Result("AUTO_COPY_TEST_CLEANUP", null);
                }
                OutputAnchor anchor;
                if (args == null || args.Length != 4 || args[0] != WorkerArgument ||
                    !OutputAnchor.TryParse(args[3], out anchor) || !anchor.HasBody)
                    throw Failure("AUTO_COPY_REQUEST_INVALID", null);
                WatchParent(cancellation);
                var result = Run(args, anchor, cancellation.Token);
                capturedFrame = null; // Success already attached its clean frame to the result.
                return result;
            }
            catch (AutoCopyException error) { return Failed(error.Message, error.Detail); }
            catch (OperationCanceledException) { return Failed("AUTO_COPY_CANCELLED", null); }
            catch (InvalidDataException error) { return Failed(error.Message, null); }
            catch { return Failed("AUTO_COPY_FAILED", null); }
        }

        internal static void WatchParent(CancellationTokenSource cancellation)
        {
            // An owned stdin pipe is the cancellation channel, not a command/data channel. EOF also covers
            // normal parent shutdown. No injected input is performed by this background reader.
            Task.Run(() =>
            {
                try { Console.In.ReadLine(); } catch { }
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            });
        }

        private static void Pause(int milliseconds, CancellationToken cancellation)
        {
            cancellation.WaitHandle.WaitOne(milliseconds);
            cancellation.ThrowIfCancellationRequested();
        }

        // Every failure after the fresh capture hands that capture back so the UI can keep it for the bundle.
        private static OutputBufferResult Failed(string code, string detail)
        {
            var result = Result(code, detail);
            var frame = capturedFrame;
            capturedFrame = null;
            AttachFrame(result, frame);
            return result;
        }

        private static OutputBufferResult Run(string[] args, OutputAnchor anchor, CancellationToken cancellation)
        {
            var targetArgs = new[] { args[0], args[1], args[2] };
            var inventory = OutputBufferCapture.ParseInventory(args[1], args[2]);
            var root = PowerSiScreenCapture.ResolveWindow(targetArgs); // Fresh HWND plus full PowerSI identity checks.
            CheckTarget(root, inventory, anchor);

            var stored = anchor.Body;
            var clickClient = new Point(stored.X + stored.Width / 2, stored.Y + stored.Height / 2);
            cancellation.ThrowIfCancellationRequested();
            var frame = CaptureFrame(root);
            capturedFrame = frame.Png != null && frame.Png.Length <= MaxFramePng ? frame.Png : null;
            string summary;
            var body = ConfirmBody(frame, anchor, clickClient, cancellation, out summary);
            cancellation.ThrowIfCancellationRequested();
            if (PowerSiScreenCapture.ResolveWindow(targetArgs) != root) throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            CheckTarget(root, inventory, anchor);
            var target = new NativePoint { X = clickClient.X, Y = clickClient.Y };
            if (!ClientToScreen(root, ref target)) throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            CheckOccluder(root, target);
            RequireIdleInput();
            var swapped = GetSystemMetrics(SwapButtonMetric) != 0;

            NativePoint saved;
            var savedCursor = GetPhysicalCursorPos(out saved);
            OutputBufferResult copied;
            PowerSiFrame cleanFrame;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (!SetPhysicalCursorPos(target.X, target.Y)) throw Failure("AUTO_COPY_CURSOR_NOT_SET", null);
                NativePoint current;
                if (!GetPhysicalCursorPos(out current) || current.X != target.X || current.Y != target.Y)
                    throw Failure("AUTO_COPY_CURSOR_NOT_SET", null);
                CheckInputTarget(root, inventory, anchor, clickClient, target, swapped, cancellation);
                ClickOnce(root, swapped);
                var sinceClick = Stopwatch.StartNew();
                Pause(SettleMilliseconds, cancellation);
                WaitForeground(root, cancellation);
                CheckInputTarget(root, inventory, anchor, clickClient, target, swapped, cancellation);
                // OCR must see the deselected state, not the selection we are about to create for copying.
                cleanFrame = CaptureFrame(root);
                capturedFrame = cleanFrame.Png;
                ConfirmBody(cleanFrame, anchor, clickClient, cancellation, out summary);
                CheckInputTarget(root, inventory, anchor, clickClient, target, swapped, cancellation);
                Chord(root, VirtualA);
                Pause(ChordGapMilliseconds, cancellation);
                CheckInputTarget(root, inventory, anchor, clickClient, target, swapped, cancellation);
                // A manual copy earlier in this run must not count as success if this Ctrl+C does nothing.
                uint baseline = OutputBufferCapture.ClipboardSequence;
                Chord(root, VirtualC);
                copied = ReadCopiedText(inventory, baseline, cancellation);
                // Keep this a single click rather than the second half of a double-click (which selects a word).
                Pause(Math.Max(0, System.Windows.Forms.SystemInformation.DoubleClickTime + 1 - (int)sinceClick.ElapsedMilliseconds), cancellation);
                CheckInputTarget(root, inventory, anchor, clickClient, target, swapped, cancellation);
                if (RootOf(GetForegroundWindow()) != root) throw Failure("AUTO_COPY_FOREGROUND_FAILED", null);
                ClickOnce(root, swapped);
                Pause(SettleMilliseconds, cancellation);
            }
            finally
            {
                // Do not undo a real user's movement while stopping; restore only our own last cursor position.
                NativePoint current;
                if (savedCursor && GetPhysicalCursorPos(out current) && current.X == target.X && current.Y == target.Y)
                    try { SetPhysicalCursorPos(saved.X, saved.Y); } catch { }
            }

            if (PowerSiScreenCapture.ResolveWindow(targetArgs) != root) throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            copied.Code = "AUTO_COPY_READ";
            copied.Method = "AUTO_CLIPBOARD";
            copied.Detail = Sanitize(string.Join("|", "A2", Text(anchor.ClientPoint.X), Text(anchor.ClientPoint.Y),
                Text(body.X), Text(body.Y), Text(body.Width), Text(body.Height), Text(target.X), Text(target.Y), summary,
                "CLEAN_FRAME", cleanFrame.CapturedUtc.Ticks.ToString(CultureInfo.InvariantCulture)));
            AttachFrame(copied, cleanFrame.Png);
            return copied;
        }

        private static Rectangle ConfirmBody(PowerSiFrame frame, OutputAnchor anchor, Point click,
            CancellationToken cancellation, out string summary)
        {
            if (frame.PixelSize != anchor.ClientSize) throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            BodySearchDiagnostics diagnostics;
            Rectangle body;
            try { body = OutputPaneImage.FindBodyAt(frame, anchor.ClientPoint, cancellation, out diagnostics); }
            catch (LocalVisionException error) { throw Failure("AUTO_COPY_BODY_UNCONFIRMED", Sanitize(error.Detail)); }
            summary = diagnostics == null ? "NONE" : Sanitize(diagnostics.Summary());
            if (!body.Contains(click)) throw Failure("AUTO_COPY_BODY_UNCONFIRMED", summary);
            var stored = anchor.Body;
            if (Math.Abs(body.X - stored.X) > BodyTolerance || Math.Abs(body.Y - stored.Y) > BodyTolerance ||
                Math.Abs(body.Right - stored.Right) > BodyTolerance || Math.Abs(body.Bottom - stored.Bottom) > BodyTolerance)
                throw Failure("AUTO_COPY_BODY_MOVED", summary);
            return body;
        }

        private static void CheckInputTarget(IntPtr root, ProcessInventory inventory, OutputAnchor anchor,
            Point click, NativePoint expected, bool swapped, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            CheckTarget(root, inventory, anchor);
            var currentTarget = new NativePoint { X = click.X, Y = click.Y };
            if (!ClientToScreen(root, ref currentTarget) || currentTarget.X != expected.X || currentTarget.Y != expected.Y)
                throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            NativePoint current;
            if (!GetPhysicalCursorPos(out current) || current.X != expected.X || current.Y != expected.Y)
                throw Failure("AUTO_COPY_CURSOR_MOVED", null);
            CheckOccluder(root, current);
            RequireIdleInput();
            if ((GetSystemMetrics(SwapButtonMetric) != 0) != swapped) throw Failure("AUTO_COPY_INPUT_BUSY", null);
            cancellation.ThrowIfCancellationRequested();
        }

        private static void CheckTarget(IntPtr root, ProcessInventory inventory, OutputAnchor anchor)
        {
            PowerSiScreenCapture.RequireResponsive(root);
            uint pid;
            if (GetWindowThreadProcessId(root, out pid) == 0 || pid == 0 || (int)pid != anchor.Pid)
                throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            if (inventory.SessionId != anchor.SessionId) throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            var identity = inventory.Items.SingleOrDefault(p => p.Pid == anchor.Pid);
            if (identity == null || !identity.StartUtcTicks.HasValue || identity.StartUtcTicks.Value != anchor.StartUtcTicks)
                throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            if (!IsWindowVisible(root) || IsIconic(root)) throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            NativeRect client;
            if (!GetClientRect(root, out client) || client.Right != anchor.ClientSize.Width ||
                client.Bottom != anchor.ClientSize.Height) throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
        }

        // The screenshot worker owns the capture rules (visibility, blank and size checks); reuse it in this process
        // instead of spawning a second worker. Its SC_* failures become this verb's capture detail.
        private static PowerSiFrame CaptureFrame(IntPtr window)
        {
            PowerSiFrame captured;
            try { captured = PowerSiScreenCapture.CaptureWindow(window); }
            catch (InvalidDataException error) when (error.Message == "SC_PENDING") { throw; }
            catch (InvalidDataException error) { throw Failure("AUTO_COPY_CAPTURE_FAILED", Sanitize(error.Message)); }
            catch { throw Failure("AUTO_COPY_CAPTURE_FAILED", null); }
            if (captured == null || captured.Png == null) throw Failure("AUTO_COPY_CAPTURE_FAILED", null);
            return captured;
        }

        private static OutputBufferResult ReadCopiedText(ProcessInventory inventory, uint baseline, CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            while (OutputBufferCapture.ClipboardSequence == baseline)
            {
                if (clock.ElapsedMilliseconds >= ClipboardWaitMilliseconds) throw Failure("AUTO_COPY_NO_CLIPBOARD", null);
                Pause(50, cancellation);
            }
            // The copying application may still hold the clipboard open for a moment; a few read-only retries only.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                OutputBufferResult result;
                if (OutputBufferCapture.TryReadUserCopy(inventory, baseline, out result) && result != null)
                {
                    if (result.Text == null) throw Failure("AUTO_COPY_CLIPBOARD_SIZE", Sanitize(result.Code));
                    return result;
                }
                Pause(50, cancellation);
            }
            throw Failure("AUTO_COPY_CLIPBOARD_FOREIGN", null);
        }

        // ---------------------------------------------------------------- injection primitives

        // WindowFromPhysicalPoint tells us which window would actually receive the click. On a mismatch the
        // occluding window's process is named so the log can say what was in the way; our own windows are SELF.
        private static void CheckOccluder(IntPtr root, NativePoint point)
        {
            var hit = RootOf(WindowFromPhysicalPoint(point));
            if (hit == root) return;
            throw Failure("AUTO_COPY_OCCLUDED", "OCCLUDER|" + OccluderName(hit));
        }

        private static string OccluderName(IntPtr window)
        {
            try
            {
                uint pid;
                if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out pid) == 0 || pid == 0 ||
                    pid > int.MaxValue) return "NONE";
                using (var occluder = Process.GetProcessById((int)pid))
                using (var self = Process.GetCurrentProcess())
                {
                    // The UI and this worker are the same executable, so a name match means "our own window".
                    if (string.Equals(occluder.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase)) return "SELF";
                    return SafeName(occluder.ProcessName);
                }
            }
            catch { return "NONE"; }
        }

        // A process name reaches the OB1 detail and the diagnostic log, whose character sets are narrower than a
        // Windows process name: keep [A-Za-z0-9_.-] (max 64), then fold to upper case with '_' for '.' and '-'.
        private static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "NONE";
            var builder = new System.Text.StringBuilder(64);
            foreach (var letter in name)
            {
                if (builder.Length >= 64) break;
                if ((letter >= 'A' && letter <= 'Z') || (letter >= 'a' && letter <= 'z') || (letter >= '0' && letter <= '9'))
                    builder.Append(char.ToUpperInvariant(letter));
                else if (letter == '_' || letter == '.' || letter == '-') builder.Append('_');
            }
            return builder.Length == 0 ? "NONE" : builder.ToString();
        }

        private static void RequireIdleInput()
        {
            foreach (var key in HeldKeys)
                if (GetAsyncKeyState(key) < 0) throw Failure("AUTO_COPY_INPUT_BUSY", null); // High bit: physically held.
        }

        private static void WaitForeground(IntPtr root, CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            while (RootOf(GetForegroundWindow()) != root)
            {
                if (clock.ElapsedMilliseconds >= ForegroundWaitMilliseconds) throw Failure("AUTO_COPY_FOREGROUND_FAILED", null);
                Pause(20, cancellation);
            }
        }

        // Submit press/release together: no cancellable hold or inter-packet sleep can leave the button down.
        private static void ClickOnce(IntPtr root, bool swapped)
        {
            PowerSiScreenCapture.RequireResponsive(root);
            var packets = ComposeClick(swapped);
            int inserted = -1;
            try
            {
                inserted = (int)Send(packets);
                if (inserted != packets.Length) throw Failure("AUTO_COPY_CLICK_FAILED", null);
            }
            finally
            {
                if (inserted == 1 || inserted == -1) try { Send(new[] { packets[1] }); } catch { }
            }
        }

        private static Input[] ComposeClick(bool swapped)
        { return new[] { Mouse(swapped ? MouseRightDown : MouseLeftDown), Mouse(swapped ? MouseRightUp : MouseLeftUp) }; }

        private static void Chord(IntPtr root, ushort key)
        {
            if (RootOf(GetForegroundWindow()) != root) throw Failure("AUTO_COPY_FOREGROUND_LOST", null);
            PowerSiScreenCapture.RequireResponsive(root);
            var packets = ComposeChord(key);
            var returned = false;
            var inserted = 0;
            try
            {
                inserted = (int)Send(packets);
                returned = true;
                if (inserted != packets.Length) throw Failure("AUTO_COPY_KEYS_FAILED", null);
            }
            finally
            {
                // packets are [CTRL down, key down, key up, CTRL up]; anything not inserted is still logically held.
                if (!returned || inserted == 2) { try { Send(new[] { Key(key, true) }); } catch { } }
                if (!returned || (inserted >= 1 && inserted <= 3)) { try { Send(new[] { Key(VirtualControl, true) }); } catch { } }
            }
        }

        internal static Input[] ComposeChord(ushort key)
        {
            return new[] { Key(VirtualControl, false), Key(key, false), Key(key, true), Key(VirtualControl, true) };
        }

        private static Input Mouse(uint flags)
        {
            var input = new Input { Type = InputMouse };
            input.Union.Mouse.Flags = flags;
            return input;
        }

        private static Input Key(ushort virtualKey, bool up)
        {
            var scan = (ushort)MapVirtualKey(virtualKey, 0); // MAPVK_VK_TO_VSC
            if (scan == 0) throw Failure("AUTO_COPY_KEYS_UNAVAILABLE", null);
            var input = new Input { Type = InputKeyboard };
            input.Union.Keyboard.Scan = scan; // Scan codes only; wVk is ignored when KEYEVENTF_SCANCODE is set.
            input.Union.Keyboard.Flags = up ? (KeyScanCode | KeyUp) : KeyScanCode;
            return input;
        }

        private static uint Send(Input[] packets)
        {
            var size = Marshal.SizeOf(typeof(Input));
            if (size != (IntPtr.Size == 8 ? 40 : 28)) throw Failure("AUTO_COPY_INPUT_LAYOUT_INVALID", null);
            return SendInput((uint)packets.Length, packets, size);
        }

        private static IntPtr RootOf(IntPtr window)
        {
            return window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(window, RootAncestor);
        }

        // ---------------------------------------------------------------- results

        // InvalidDataException is sealed, so this carries the fixed code in Message and the metadata-only detail.
        // RunWorker maps it, plain InvalidDataException (SC_* window/capture codes) and anything else to a result.
        private sealed class AutoCopyException : Exception
        {
            internal readonly string Detail;
            internal AutoCopyException(string code, string detail) : base(code) { Detail = detail; }
        }

        private static AutoCopyException Failure(string code, string detail) { return new AutoCopyException(code, detail); }

        private static OutputBufferResult Result(string code, string detail)
        {
            return new OutputBufferResult { Code = OutputBufferCapture.SafeCode(code) ? code : "AUTO_COPY_FAILED",
                Method = "NONE", Detail = Sanitize(detail) };
        }

        private static string Sanitize(string detail)
        {
            return detail != null && Regex.IsMatch(detail, @"\A[A-Z0-9_:=,| -]{1,400}\z") ? detail : "NONE";
        }

        private static string Text(int value) { return value.ToString(CultureInfo.InvariantCulture); }

        // ---------------------------------------------------------------- self-test

        internal static void SelfTest()
        {
            AnchorSelfTest();
            VisionAnchorSelfTest();
            DetailSelfTest();
            LayoutSelfTest();
            PreflightSelfTest();
            Console.WriteLine(LiveTest());
        }

        private static void PreflightSelfTest()
        {
            var body = new Rectangle(20, 40, 360, 320);
            var anchor = new OutputAnchor { ClientPoint = new Point(200, 200), ClientSize = new Size(600, 420), Body = body };
            foreach (var scenario in new[] { "SAME", "MOVED", "SELECTED" })
            using (var bitmap = new Bitmap(600, 420))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var png = new MemoryStream())
            {
                graphics.Clear(Color.DarkBlue);
                var current = scenario == "MOVED" ? new Rectangle(40, 40, 340, 320) : body;
                graphics.FillRectangle(scenario == "SELECTED" ? Brushes.DodgerBlue : Brushes.White, current);
                bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                var frame = new PowerSiFrame { Png = png.ToArray(), PixelSize = bitmap.Size };
                string summary;
                try
                {
                    Need(ConfirmBody(frame, anchor, anchor.ClientPoint, CancellationToken.None, out summary) == body && scenario == "SAME",
                        "pre-input body identity");
                }
                catch (AutoCopyException error)
                {
                    Need((scenario == "MOVED" && error.Message == "AUTO_COPY_BODY_MOVED") ||
                        (scenario == "SELECTED" && error.Message == "AUTO_COPY_BODY_UNCONFIRMED"), "changed/selected body rejected before input");
                }
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    try { ConfirmBody(frame, anchor, anchor.ClientPoint, cancelled.Token, out summary); throw new InvalidOperationException("Cancelled preflight ran."); }
                    catch (OperationCanceledException) { }
                }
            }
            Console.WriteLine("PASS: pre-input body guards (unchanged, moved pane, highlighted pane, cancellation)");
        }

        private static void Need(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Output auto copy self-test: " + name);
        }

        private static void AnchorSelfTest()
        {
            var learned = new DateTime(2026, 9, 11, 1, 2, 3, DateTimeKind.Utc);
            var anchor = new OutputAnchor { Pid = 4321, StartUtcTicks = 638000000000000000L, SessionId = 2,
                ClientPoint = new Point(320, 385), ClientSize = new Size(1920, 1040), LearnedUtc = learned };
            var text = anchor.Serialize();
            Need(text == "A1|4321|638000000000000000|2|320|385|1920|1040|" +
                learned.Ticks.ToString(CultureInfo.InvariantCulture), "anchor serialization format");
            OutputAnchor parsed;
            Need(OutputAnchor.TryParse(text, out parsed) && parsed.Matches(anchor) && parsed.Serialize() == text,
                "anchor round trip");
            Need(parsed.LearnedUtc.Kind == DateTimeKind.Utc, "anchor learned time stays UTC");
            foreach (var invalid in new[] { null, "", "A1", "A2|4321|1|2|320|385|1920|1040|1", "A1|4321|1|2|320|385|1920|1040",
                "A1|4321|1|2|320|385|1920|1040|1|9", "A1|0|1|2|320|385|1920|1040|1", "A1|4321|0|2|320|385|1920|1040|1",
                "A1|4321|1|2|1920|385|1920|1040|1", "A1|4321|1|2|320|1040|1920|1040|1", "A1|4321|1|2|-1|385|1920|1040|1",
                "A1|4321|1|-2|320|385|1920|1040|1", "A1|4321|1|2|320|385|4097|1040|1", "A1|4321|1|2|320|385|1920|0|1",
                "A1|4321|1|2|320|385|1920|1040|0", "A1|+4321|1|2|320|385|1920|1040|1", "A1|4321|1|2|320|385|1920|1040| 1",
                "A1|4321|1|2|320|385|1920|1040|01", "a1|4321|1|2|320|385|1920|1040|1", "A1|4321|99999999999999999999|2|320|385|1920|1040|1" })
            {
                OutputAnchor rejected;
                Need(!OutputAnchor.TryParse(invalid, out rejected) && rejected == null, "malformed anchor rejected: " + (invalid ?? "null"));
            }
            Need(Sanitize(text) == text, "anchor text survives the OB1 detail character set");
            Need(!anchor.HasBody && OutputAnchor.TryParse(text, out parsed) && parsed.Body == Rectangle.Empty,
                "an unconfirmed anchor stays A1 and carries no body");

            // The confirmed form the UI hands to the worker: the same nine fields plus the body it was confirmed on.
            var confirmed = new OutputAnchor { Pid = 4321, StartUtcTicks = 638000000000000000L, SessionId = 2,
                ClientPoint = new Point(609, 673), ClientSize = new Size(1920, 1009), LearnedUtc = learned,
                Body = new Rectangle(317, 393, 585, 560) };
            var confirmedText = confirmed.Serialize();
            Need(confirmedText == "A2|4321|638000000000000000|2|609|673|1920|1009|" +
                learned.Ticks.ToString(CultureInfo.InvariantCulture) + "|317|393|585|560", "confirmed anchor serialization format");
            OutputAnchor withBody;
            Need(OutputAnchor.TryParse(confirmedText, out withBody) && withBody.Matches(confirmed) && withBody.HasBody &&
                withBody.Body == confirmed.Body && withBody.Serialize() == confirmedText, "confirmed anchor round trip");
            foreach (var invalid in new[] { "A2|4321|1|2|609|673|1920|1009|1|317|393|585",
                "A2|4321|1|2|609|673|1920|1009|1|317|393|585|560|1", "A1|4321|1|2|609|673|1920|1009|1|317|393|585|560",
                "A2|4321|1|2|609|673|1920|1009|1|317|393|0|560", "A2|4321|1|2|609|673|1920|1009|1|317|393|1605|560",
                "A2|4321|1|2|609|673|1920|1009|1|317|393|585|617", "A2|4321|1|2|10|673|1920|1009|1|317|393|585|560",
                "A2|4321|1|2|609|673|1920|1009|1|-1|393|585|560", "A3|4321|1|2|609|673|1920|1009|1|317|393|585|560" })
            {
                OutputAnchor rejected;
                Need(!OutputAnchor.TryParse(invalid, out rejected) && rejected == null, "malformed confirmed anchor rejected: " + invalid);
            }
            Need(Sanitize(confirmedText) == confirmedText, "confirmed anchor survives the OB1 detail character set");
        }

        private static void VisionAnchorSelfTest()
        {
            var captured = new DateTime(2026, 9, 14, 1, 2, 3, DateTimeKind.Utc);
            var size = new Size(1920, 1009);
            var body = new Rectangle(317, 393, 585, 560);
            ProcessInventory Inventory(params ProcessState[] items)
            {
                return new ProcessInventory { SessionId = 2, Items = items };
            }
            PowerSiObservation Located(Rectangle outputBody, Size frameSize)
            {
                var png = new byte[] { 1, 2, 3 };
                var result = PowerSiObservation.VisionLogExcerpt(null, captured);
                result.LocalFrame = new PowerSiFrame { Png = png, PixelSize = size, CapturedUtc = captured };
                result.LocalFullImage = png;
                result.LocalImage = new byte[] { 4 };
                result.LocalPaneImage = new byte[] { 5 };
                result.LocalFrameSize = frameSize;
                result.LocalOutputBody = outputBody;
                result.LocalVisionMode = "LOCATE_ONLY";
                return result;
            }
            var identity = new ProcessState { Pid = 4321, Name = "powersi", StartUtcTicks = 638000000000000000L };
            var inventory = Inventory(identity);
            var located = Located(body, size);
            var anchor = AnchorFromVision(inventory, located);
            Need(anchor != null && anchor.HasBody && anchor.Pid == identity.Pid &&
                anchor.StartUtcTicks == identity.StartUtcTicks && anchor.SessionId == inventory.SessionId &&
                anchor.ClientSize == size && anchor.Body == body && anchor.ClientPoint == new Point(609, 673) &&
                anchor.LearnedUtc == captured && anchor.Serialize().StartsWith("A2|", StringComparison.Ordinal),
                "locator result becomes a validated A2 centre anchor");
            located.LocalVisibleEmpty = true;
            Need(AnchorFromVision(inventory, located) == null, "visibly empty pane never authorizes input");
            located.LocalVisibleEmpty = false;

            Need(AnchorFromVision(null, located) == null && AnchorFromVision(inventory, null) == null &&
                AnchorFromVision(inventory, Located(Rectangle.Empty, size)) == null,
                "missing locator anchor evidence rejected");
            var failed = Located(body, size); failed.LocalFailure = "LOCATE_OUTPUT_INVALID";
            Need(AnchorFromVision(inventory, failed) == null &&
                AnchorFromVision(inventory, Located(new Rectangle(1800, 900, 200, 200), size)) == null &&
                AnchorFromVision(inventory, Located(body, new Size(1919, 1009))) == null,
                "failed, outside, and size-mismatched locator geometry rejected");
            Need(AnchorFromVision(Inventory(
                    identity, new ProcessState { Pid = 4322, Name = "pwrsi", StartUtcTicks = 638000000000000001L }), located) == null &&
                AnchorFromVision(Inventory(new ProcessState { Pid = 4321, Name = "powersi" }), located) == null &&
                AnchorFromVision(Inventory(new ProcessState { Pid = 4321, Name = "other", StartUtcTicks = identity.StartUtcTicks }), located) == null,
                "non-singleton or incomplete PowerSI identity rejected");
        }

        private static void DetailSelfTest()
        {
            var anchor = new OutputAnchor { Pid = 7, StartUtcTicks = 123456789, SessionId = 1,
                ClientPoint = new Point(10, 20), ClientSize = new Size(800, 600), LearnedUtc = DateTime.UtcNow };
            var carrier = new OutputBufferResult { Code = "USER_COPY_READ", Method = "USER_CLIPBOARD",
                Detail = "SOURCE_PID_MATCH|" + anchor.Serialize() };
            var read = AnchorOf(carrier);
            Need(read != null && read.Pid == 7 && read.ClientPoint == new Point(10, 20) && read.ClientSize == new Size(800, 600),
                "anchor read back from the OB1 detail");
            Need(carrier.Detail.Split('|')[0] == "SOURCE_PID_MATCH", "legacy detail prefix preserved");
            Need(AnchorOf(new OutputBufferResult { Detail = "SOURCE_PID_MATCH" }) == null, "detail without anchor");
            Need(AnchorOf(new OutputBufferResult { Detail = "SOURCE_PID_MATCH|ANCHOR_NONE|CURSOR" }) == null, "anchor-none detail");
            Need(AnchorOf(new OutputBufferResult { Detail = "SOURCE_PID_MATCH|A1|7|123456789|1|10|20|800" }) == null, "truncated anchor detail");
            Need(AnchorOf(null) == null && AnchorOf(new OutputBufferResult()) == null, "missing detail");
            foreach (var reason in new[] { "FOREGROUND", "IDENTITY", "CURSOR", "CLIENT", "OUTSIDE", "SERIALIZE", "SAMPLE" })
                Need(Sanitize("SOURCE_PID_MATCH|ANCHOR_NONE|" + reason) != "NONE", "anchor-none reason is log safe: " + reason);
            Need(LearnAnchor(IntPtr.Zero, null) == "ANCHOR_NONE|IDENTITY", "anchor sampling without a window");
            var summary = "A2|320|385|20|40|360|320|1000|900|B2|1920|1040|12|1|3|4|1|2|1";
            Need(Sanitize(summary) == summary, "success detail is log safe");
            Need(Result("AUTO_COPY_BODY_UNCONFIRMED", null).Detail == "NONE" &&
                Result("auto_copy", null).Code == "AUTO_COPY_FAILED" &&
                Result("AUTO_COPY_BODY_UNCONFIRMED", "b2|1").Detail == "NONE", "failure results stay inside the wire contract");

            // Occluding process names: folded into the narrower log/wire character set, our own windows become SELF.
            Need(SafeName("RemoteMonitorSlave") == "REMOTEMONITORSLAVE" && SafeName("power-si.v2") == "POWER_SI_V2" &&
                SafeName("") == "NONE" && SafeName(null) == "NONE" && SafeName("한글") == "NONE" &&
                SafeName(new string('x', 200)).Length == 64, "occluding process name folding");
            foreach (var name in new[] { "SELF", "NONE", "EXPLORER", SafeName("PowerSI-64.exe") })
                Need(Result("AUTO_COPY_OCCLUDED", "OCCLUDER|" + name).Detail == "OCCLUDER|" + name,
                    "occluder detail stays on the wire: " + name);

            // The worker capture rides beside the result object, never inside OutputBufferResult itself.
            var aborted = new OutputBufferResult { Code = "AUTO_COPY_BODY_MOVED", Method = "NONE", Detail = "NONE" };
            Need(FrameOf(aborted) == null && FrameOf(null) == null, "no worker frame by default");
            AttachFrame(aborted, new byte[] { 1, 2, 3 });
            AttachFrame(aborted, null);
            var kept = FrameOf(aborted);
            Need(kept != null && kept.Length == 3, "the worker frame stays attached to its own result");
            AttachFrame(aborted, new byte[] { 4, 5 });
            Need(FrameOf(aborted).Length == 2, "a second attach replaces the frame instead of throwing");
        }

        private static void LayoutSelfTest()
        {
            var size = Marshal.SizeOf(typeof(Input));
            Need(size == (IntPtr.Size == 8 ? 40 : 28) && Marshal.OffsetOf(typeof(Input), "Union").ToInt32() == (IntPtr.Size == 8 ? 8 : 4) &&
                Marshal.SizeOf(typeof(InputUnion)) == (IntPtr.Size == 8 ? 32 : 24) &&
                Marshal.SizeOf(typeof(MouseInput)) == (IntPtr.Size == 8 ? 32 : 24) &&
                Marshal.SizeOf(typeof(KeyboardInput)) == (IntPtr.Size == 8 ? 24 : 16), "INPUT union layout");
            foreach (var key in new[] { VirtualA, VirtualC })
            {
                var packets = ComposeChord(key);
                var control = (ushort)MapVirtualKey(VirtualControl, 0);
                var scan = (ushort)MapVirtualKey(key, 0);
                Need(packets.Length == 4 && control != 0 && scan != 0, "chord packet count");
                Need(packets[0].Type == InputKeyboard && packets[1].Type == InputKeyboard &&
                    packets[2].Type == InputKeyboard && packets[3].Type == InputKeyboard, "chord uses keyboard input only");
                Need(packets[0].Union.Keyboard.Scan == control && packets[3].Union.Keyboard.Scan == control &&
                    packets[1].Union.Keyboard.Scan == scan && packets[2].Union.Keyboard.Scan == scan, "chord scan codes");
                Need(packets[0].Union.Keyboard.Flags == KeyScanCode && packets[1].Union.Keyboard.Flags == KeyScanCode &&
                    packets[2].Union.Keyboard.Flags == (KeyScanCode | KeyUp) && packets[3].Union.Keyboard.Flags == (KeyScanCode | KeyUp),
                    "chord press/release order");
                foreach (var packet in packets)
                    Need(packet.Union.Keyboard.Vk == 0 && packet.Union.Keyboard.Time == 0 &&
                        packet.Union.Keyboard.ExtraInfo == UIntPtr.Zero, "chord packets carry no injected metadata");
            }
            foreach (var swapped in new[] { false, true })
            {
                var packets = ComposeClick(swapped);
                Need(packets.Length == 2, "press/release submitted in one batch");
                var down = packets[0];
                var up = packets[1];
                Need(down.Type == InputMouse && up.Type == InputMouse &&
                    down.Union.Mouse.Flags == (swapped ? MouseRightDown : MouseLeftDown) &&
                    up.Union.Mouse.Flags == (swapped ? MouseRightUp : MouseLeftUp) &&
                    down.Union.Mouse.X == 0 && down.Union.Mouse.Y == 0 && down.Union.Mouse.Data == 0 &&
                    down.Union.Mouse.Time == 0 && down.Union.Mouse.ExtraInfo == UIntPtr.Zero,
                    "click packets carry no movement or wheel data");
            }
        }

        // Live injection against our own window. It replaces the clipboard with the sample text below and never saves
        // or restores the previous clipboard content. Skipped whenever no interactive foreground window is available.
        private static string LiveTest()
        {
            const string Skip = "SKIP: live input test (no interactive foreground)";
            const string Sample = "AUTO COPY LIVE 1\r\nAUTO COPY LIVE 2";
            System.Windows.Forms.Form form = null;
            System.Windows.Forms.TextBox box = null;
            try
            {
                // Only the window setup may fail silently (a service session has no interactive desktop at all).
                // Once the first input packet is injected every check below is a hard assertion.
                try
                {
                    form = new System.Windows.Forms.Form
                    {
                        Text = "Remote Monitor self-test",
                        Width = 420,
                        Height = 240,
                        ShowInTaskbar = false,
                        TopMost = true,
                        StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen
                    };
                    box = new System.Windows.Forms.TextBox
                    {
                        Multiline = true,
                        Dock = System.Windows.Forms.DockStyle.Fill,
                        Text = Sample
                    };
                    form.Controls.Add(box);
                    form.Show();
                    form.Activate();
                }
                catch { return Skip; }
                if (box == null || !Pump(1000, () => GetForegroundWindow() == form.Handle)) return Skip;
                var target = new NativePoint { X = box.ClientSize.Width / 2, Y = box.ClientSize.Height / 2 };
                if (box.ClientSize.Width < 8 || box.ClientSize.Height < 8 || !ClientToScreen(box.Handle, ref target)) return Skip;
                NativePoint saved;
                var savedCursor = GetPhysicalCursorPos(out saved);
                try
                {
                    foreach (var key in HeldKeys) if (GetAsyncKeyState(key) < 0) return Skip;
                    if (!SetPhysicalCursorPos(target.X, target.Y)) return Skip;
                    NativePoint current;
                    if (!GetPhysicalCursorPos(out current) || current.X != target.X || current.Y != target.Y) return Skip;
                    if (RootOf(WindowFromPhysicalPoint(current)) != form.Handle) return Skip;
                    ClickOnce(form.Handle, GetSystemMetrics(SwapButtonMetric) != 0);
                    Pump(ForegroundWaitMilliseconds, () => false);
                    Need(GetForegroundWindow() == form.Handle, "live test window stayed foreground");
                    Chord(form.Handle, VirtualA);
                    Pump(ChordGapMilliseconds, () => false);
                    System.Windows.Forms.Clipboard.SetText("earlier copy must not count as automatic success");
                    var baseline = OutputBufferCapture.ClipboardSequence;
                    try { ReadCopiedText(null, baseline, CancellationToken.None); throw new InvalidOperationException("Stale clipboard accepted without Ctrl+C."); }
                    catch (AutoCopyException error) { Need(error.Message == "AUTO_COPY_NO_CLIPBOARD", "only post-copy sequence changes count"); }
                    baseline = OutputBufferCapture.ClipboardSequence;
                    Chord(form.Handle, VirtualC);
                    Need(Pump(ClipboardWaitMilliseconds, () => OutputBufferCapture.ClipboardSequence != baseline),
                        "live Ctrl+C changed the clipboard sequence");
                    string copied = null;
                    Pump(1000, () =>
                    {
                        try { copied = System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null; }
                        catch { copied = null; }
                        return copied != null;
                    });
                    Need(copied == Sample, "live Ctrl+A/Ctrl+C copied the exact control text");
                    for (int repeat = 0; repeat < 2; repeat++)
                    {
                        Pump(System.Windows.Forms.SystemInformation.DoubleClickTime + 1, () => false);
                        Need(GetForegroundWindow() == form.Handle && RootOf(WindowFromPhysicalPoint(target)) == form.Handle,
                            "live cleanup still targets the owned text box");
                        ClickOnce(form.Handle, GetSystemMetrics(SwapButtonMetric) != 0);
                        Need(Pump(SettleMilliseconds, () => box.SelectionLength == 0), "cleanup click removed selection");
                        Need(box.Text == Sample && System.Windows.Forms.Clipboard.GetText() == Sample,
                            "cleanup changed neither source text nor clipboard");
                        if (repeat == 0)
                        {
                            baseline = OutputBufferCapture.ClipboardSequence;
                            Chord(form.Handle, VirtualA);
                            Pump(ChordGapMilliseconds, () => false);
                            Chord(form.Handle, VirtualC);
                            Need(Pump(ClipboardWaitMilliseconds, () => OutputBufferCapture.ClipboardSequence != baseline) &&
                                System.Windows.Forms.Clipboard.GetText() == Sample, "second copy after cleanup remains exact");
                        }
                    }
                }
                finally { if (savedCursor) { try { SetPhysicalCursorPos(saved.X, saved.Y); } catch { } } }
                return "PASS: live input test (click + Ctrl+A + Ctrl+C + deselect; repeated copy exact)";
            }
            finally
            {
                if (form != null) { try { form.Dispose(); } catch { } }
                try { System.Windows.Forms.Application.DoEvents(); } catch { }
            }
        }

        // The self-test owns the only message loop these controls get, so every wait has to pump it.
        private static bool Pump(int milliseconds, Func<bool> until)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                try { System.Windows.Forms.Application.DoEvents(); } catch { }
                if (until()) return true;
                if (clock.ElapsedMilliseconds >= milliseconds) return false;
                Thread.Sleep(20);
            }
        }

        // ---------------------------------------------------------------- native layer

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            internal uint Type;
            internal InputUnion Union;
        }

        // The union must be as large as its largest member: MOUSEINPUT, 32 bytes on x64 and 24 on x86
        // (KEYBDINPUT is 24/16). HARDWAREINPUT is never used and is therefore not declared.
        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)] internal MouseInput Mouse;
            [FieldOffset(0)] internal KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            internal int X, Y;
            internal uint Data, Flags, Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            internal ushort Vk, Scan;
            internal uint Flags, Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { internal int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr WindowFromPhysicalPoint(NativePoint point);
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setphysicalcursorpos (Vista+)
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetPhysicalCursorPos(int x, int y);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicalCursorPos(out NativePoint point);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "MapVirtualKeyW")]
        private static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    }
}
