using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteMonitorLink
{
    internal sealed class PowerSiFrame
    {
        internal byte[] Png;
        internal DateTime CapturedUtc;
        internal Size PixelSize;
    }

    internal static class PowerSiScreenCapture
    {
        private const string WorkerArgument = "--powersi-screen";
        private const string PrepareWorkerArgument = "--powersi-screen-prepare";
        private const string ResponsiveWorkerArgument = "--powersi-responsive";
        private const string PrepareGate = "GO";
        private const uint WmNull = 0;
        private const uint SmtoBlock = 0x0001;
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint SmtoErrorOnExit = 0x0020;
        private const uint ResponsivenessMilliseconds = 750;
        private const int ForegroundWaitMilliseconds = 1000;
        // Parent caps must strictly exceed the worker's own bounded waits, or a slow but responsive target is
        // killed as SC_TIMEOUT. Prepare: 4x750 responsiveness + 1000 foreground wait = 4000. Read: 4x750 = 3000.
        // Responsive probe: 1x750.
        private const int PrepareTimeoutMilliseconds = 8000;
        private const int CaptureTimeoutMilliseconds = 6000;
        private const int ResponsiveTimeoutMilliseconds = 4000;
        private const int MaxPngBytes = 8 * 1024 * 1024;
        private const int MaxWireChars = ((MaxPngBytes + 2) / 3) * 4 + 64;
        private static readonly string[] Errors = { "SC_IDENTITY", "SC_NOT_RUNNING", "SC_WINDOW_UNAVAILABLE",
            "SC_AMBIGUOUS_WINDOW", "SC_MINIMIZED", "SC_DESKTOP_UNAVAILABLE", "SC_CAPTURE_FAILED", "SC_BLANK",
            "SC_SIZE", "SC_WINDOW_CHANGED", "SC_PENDING", "SC_FOREGROUND_FAILED", "SC_TIMEOUT", "SC_WORKER_FAILED",
            "SC_INVALID_IMAGE", "SC_GATE_REJECTED", "SC_FOREGROUND_REQUEST_REJECTED", "SC_FOREGROUND_WAIT_PENDING",
            "SC_FOREGROUND_WAIT_MISMATCH", "SC_FOREGROUND_WAIT_TIMEOUT", "SC_FOREGROUND_MISMATCH_PRECAPTURE", "SC_FOREGROUND_MISMATCH_POSTCAPTURE" };

        internal static async Task<PowerSiFrame> CaptureAsync(ProcessInventory inventory, CancellationToken cancellation)
        {
            return await RunWorker(WorkerArgument, WorkerArguments(inventory, false, cancellation), cancellation).ConfigureAwait(false);
        }

        internal static async Task<PowerSiFrame> PrepareAsync(ProcessInventory singleton, CancellationToken cancellation,
            Action<string> diagnostic = null)
        {
            return await RunWorker(PrepareWorkerArgument, WorkerArguments(singleton, true, cancellation), cancellation, diagnostic).ConfigureAwait(false);
        }

        // A killable, passive probe. It never activates a window, captures pixels, or sends input.
        internal static async Task CheckResponsiveAsync(ProcessInventory singleton, CancellationToken cancellation)
        {
            await RunWorker(ResponsiveWorkerArgument, WorkerArguments(singleton, true, cancellation), cancellation).ConfigureAwait(false);
        }

        private static string WorkerArguments(ProcessInventory inventory, bool requireSingleton, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (inventory == null) throw Failure("SC_IDENTITY");
            inventory.Validate();
            if (inventory.Items.Length == 0) throw Failure("SC_NOT_RUNNING");
            if (requireSingleton && inventory.Items.Length != 1) throw Failure("SC_IDENTITY");
            if (inventory.Omitted != 0 || inventory.Items.Any(p => !ProcessInventory.IsPowerSiName(p.Name) ||
                !p.StartUtcTicks.HasValue || p.StartUtcTicks.Value <= 0)) throw Failure("SC_IDENTITY");
            var identities = string.Join(",", inventory.Items.Select(p => p.Pid.ToString(CultureInfo.InvariantCulture) + ":" +
                p.StartUtcTicks.Value.ToString(CultureInfo.InvariantCulture)));
            return inventory.SessionId.ToString(CultureInfo.InvariantCulture) + " " + identities;
        }

        private static async Task<PowerSiFrame> RunWorker(string workerArgument, string arguments, CancellationToken cancellation,
            Action<string> diagnostic = null)
        {
            using (var worker = new Process { StartInfo = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                workerArgument + " " + arguments) { UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
                    RedirectStandardInput = workerArgument == PrepareWorkerArgument } })
            {
                Task<string> diagnostics = null;
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    worker.Start();
                    diagnostics = ReadBounded(worker.StandardError, 4096, true);
                    if (workerArgument == PrepareWorkerArgument)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        // A foreground-capable parent may grant only this short-lived child. The child still verifies
                        // the actual foreground result; a refused grant never triggers a stronger activation method.
                        var granted = AllowSetForegroundWindow((uint)worker.Id);
                        try
                        {
                            if (diagnostic != null) diagnostic("grant=" + (granted ? "true" : "false") +
                                " worker_pid=" + worker.Id.ToString(CultureInfo.InvariantCulture));
                        }
                        catch { } // Diagnostics are best-effort and never decide whether actual activation succeeds.
                        cancellation.ThrowIfCancellationRequested();
                        worker.StandardInput.WriteLine(PrepareGate);
                        worker.StandardInput.Close();
                    }
                    var reading = ReadBounded(worker.StandardOutput);
                    var timeout = workerArgument == PrepareWorkerArgument ? PrepareTimeoutMilliseconds :
                        workerArgument == ResponsiveWorkerArgument ? ResponsiveTimeoutMilliseconds : CaptureTimeoutMilliseconds;
                    var clock = Stopwatch.StartNew();
                    while (!worker.HasExited || !reading.IsCompleted || !diagnostics.IsCompleted)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (reading.IsFaulted) await reading.ConfigureAwait(false);
                        // Observe a faulted stderr drain so it never stays an unobserved task exception. The drain
                        // itself drops excess instead of failing, so the child is not blocked on a full pipe.
                        if (diagnostics.IsFaulted && diagnostics.Exception != null) { }
                        if (clock.ElapsedMilliseconds >= timeout) throw Failure("SC_TIMEOUT");
                        await Task.Delay(25, cancellation).ConfigureAwait(false);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    if (worker.ExitCode != 0) throw Failure("SC_WORKER_FAILED");
                    var response = await reading.ConfigureAwait(false);
                    if (workerArgument == ResponsiveWorkerArgument)
                    {
                        if (response != "RESPONSIVE") throw Failure(Errors.Contains(response) ? response : "SC_WORKER_FAILED");
                        return null;
                    }
                    return Parse(response);
                }
                catch (OperationCanceledException) { throw; }
                catch (InvalidDataException error) when (Errors.Contains(error.Message)) { throw; }
                catch { throw Failure("SC_WORKER_FAILED"); }
                finally
                {
                    // Only this single-use helper is terminated; the observed application is never killed.
                    try { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(200); } } catch { }
                    if (diagnostics != null && diagnostics.Status == TaskStatus.RanToCompletion && diagnostic != null)
                        foreach (var line in diagnostics.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            if (IsWindowDiagnostic(line))
                                try { diagnostic(line); } catch { }
                }
            }
        }

        // dropExcess keeps draining the pipe past the bound instead of failing: an over-long diagnostic stream
        // must never block the child on a full stderr pipe until SC_TIMEOUT. The retained head stays bounded.
        private static async Task<string> ReadBounded(StreamReader reader, int maximum = MaxWireChars, bool dropExcess = false)
        {
            var text = new StringBuilder(); var buffer = new char[8192];
            while (true)
            {
                var count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (count == 0) return text.ToString();
                if (text.Length + count > maximum)
                {
                    if (!dropExcess) throw Failure("SC_SIZE");
                    count = maximum - text.Length;
                }
                if (count > 0) text.Append(buffer, 0, count);
            }
        }

        internal static bool TryRunWorker(string[] args)
        {
            if (args.Length == 0 || (args[0] != WorkerArgument && args[0] != PrepareWorkerArgument && args[0] != ResponsiveWorkerArgument)) return false;
            try
            {
                if (args[0] == ResponsiveWorkerArgument)
                {
                    // Probe the selected HWND before the minimized-window guard: a pending target reports Pending.
                    var window = ResolveWindow(args, RequireResponsive);
                    if (ResolveWindow(args) != window) throw Failure("SC_WINDOW_CHANGED");
                    Console.Out.Write("RESPONSIVE");
                    return true;
                }
                if (args[0] == PrepareWorkerArgument &&
                    (!Console.IsInputRedirected || Console.In.ReadLine() != PrepareGate)) throw Failure("SC_GATE_REJECTED");
                var frame = args[0] == PrepareWorkerArgument ? ReadPrepareWorker(args) : ReadWorker(args);
                Console.Out.Write("SC1|" + frame.CapturedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Convert.ToBase64String(frame.Png));
            }
            catch (InvalidDataException error) when (Errors.Contains(error.Message)) { Console.Out.Write(error.Message); }
            catch { Console.Out.Write("SC_CAPTURE_FAILED"); }
            return true;
        }

        private static PowerSiFrame ReadWorker(string[] args)
        {
            var window = ResolveWindow(args);
            RequireResponsive(window);
            var frame = CaptureWindow(window);
            RequireResponsive(window);
            if (ResolveWindow(args) != window) throw Failure("SC_WINDOW_CHANGED");
            return frame;
        }

        private static PowerSiFrame ReadPrepareWorker(string[] args)
        {
            var window = ResolveWindow(args, candidate => ReportWindow("selected", candidate));
            Rect windowRect, clientRect;
            ReadGeometry(window, out windowRect, out clientRect);
            RequireResponsive(window); // Windows message responsiveness only; application-internal phases may remain busy.
            CheckUnchanged(args, window, windowRect, clientRect);
            var activationClock = Stopwatch.StartNew();
            try { SetExactForeground(window); }
            finally { ReportWindow("activation_end", window, activationClock.ElapsedMilliseconds); }
            CheckUnchanged(args, window, windowRect, clientRect);
            RequireForeground(window, "SC_FOREGROUND_MISMATCH_PRECAPTURE");
            var frame = CaptureWindow(window);
            CheckUnchanged(args, window, windowRect, clientRect);
            RequireForeground(window, "SC_FOREGROUND_MISMATCH_POSTCAPTURE");
            return frame;
        }

        private static void ReadGeometry(IntPtr window, out Rect windowRect, out Rect clientRect)
        {
            if (!GetWindowRect(window, out windowRect) || !GetClientRect(window, out clientRect))
                throw Failure("SC_WINDOW_UNAVAILABLE");
        }

        private static void CheckUnchanged(string[] args, IntPtr window, Rect windowRect, Rect clientRect)
        {
            Rect currentWindow, currentClient;
            if (ResolveWindow(args) != window || !GetWindowRect(window, out currentWindow) ||
                !GetClientRect(window, out currentClient) || !SameRect(windowRect, currentWindow) ||
                !SameRect(clientRect, currentClient)) throw Failure("SC_WINDOW_CHANGED");
        }

        private static bool SameRect(Rect left, Rect right)
        {
            return left.Left == right.Left && left.Top == right.Top && left.Right == right.Right && left.Bottom == right.Bottom;
        }

        private static void SetExactForeground(IntPtr window)
        {
            var previous = GetForegroundWindow();
            if (!SetForegroundWindow(window)) throw Failure("SC_FOREGROUND_REQUEST_REJECTED");
            // The v0.1.56 field trace became foreground after this response but after the old immediate check.
            // A responsive message pump is necessary, not the final foreground observation.
            RequireResponsive(window, "SC_FOREGROUND_WAIT_PENDING");
            WaitForExactForeground(window, previous);
        }

        private static void WaitForExactForeground(IntPtr window, IntPtr previous)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var current = GetForegroundWindow();
                if (current == window) return;
                // Null or the original foreground can be a transition; a third window is interference.
                if (current != IntPtr.Zero && current != previous) throw Failure("SC_FOREGROUND_WAIT_MISMATCH");
                if (clock.ElapsedMilliseconds >= ForegroundWaitMilliseconds) throw Failure("SC_FOREGROUND_WAIT_TIMEOUT");
                Thread.Sleep(25); // Read-only polling in the killable worker, never another activation request or input.
            }
        }

        private static void RequireForeground(IntPtr window, string failureCode)
        {
            if (GetForegroundWindow() != window) throw Failure(failureCode);
        }

        // Passive Windows message-pump probe only; it cannot identify every application-internal pending phase.
        internal static void RequireResponsive(IntPtr root)
        {
            RequireResponsive(root, "SC_PENDING");
        }

        private static void RequireResponsive(IntPtr root, string failureCode)
        {
            UIntPtr ignored;
            if (root == IntPtr.Zero || IsHungAppWindow(root) ||
                SendMessageTimeout(root, WmNull, UIntPtr.Zero, IntPtr.Zero,
                    SmtoBlock | SmtoAbortIfHung | SmtoErrorOnExit, ResponsivenessMilliseconds, out ignored) == IntPtr.Zero)
                throw Failure(failureCode);
        }

        // Shared target identity, independent of screenshot or text collection. Single window is the current field-test scope.
        internal static IntPtr ResolveWindow(string[] args, Action<IntPtr> diagnostic = null)
        {
            int session;
            if (args.Length != 3 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out session) ||
                session < 0 || args[2].Length > 4096) throw Failure("SC_IDENTITY");
            var identities = new Dictionary<int, long>();
            foreach (var item in args[2].Split(','))
            {
                var pair = item.Split(':'); int pid; long ticks;
                if (pair.Length != 2 || !int.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid < 1 ||
                    !long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) || ticks < 1 ||
                    ticks > DateTime.MaxValue.Ticks || identities.ContainsKey(pid)) throw Failure("SC_IDENTITY");
                identities.Add(pid, ticks);
            }
            if (identities.Count == 0 || identities.Count > ProcessInventory.MaxItems) throw Failure("SC_IDENTITY");
            CheckIdentities(identities, session);
            CheckDesktop(session);
            return FindWindow(identities, diagnostic);
        }

        private static void CheckIdentities(Dictionary<int, long> identities, int session)
        {
            using (var self = Process.GetCurrentProcess()) if (self.SessionId != session) throw Failure("SC_IDENTITY");
            foreach (var identity in identities)
            {
                try
                {
                    using (var process = Process.GetProcessById(identity.Key))
                        if (process.HasExited || process.SessionId != session ||
                            !ProcessInventory.IsPowerSiName(ProcessInventory.NormalizeName(process.ProcessName)) ||
                            process.StartTime.ToUniversalTime().Ticks != identity.Value) throw Failure("SC_IDENTITY");
                }
                catch (InvalidDataException) { throw; }
                catch { throw Failure("SC_IDENTITY"); }
            }
        }

        private static IntPtr FindWindow(Dictionary<int, long> identities, Action<IntPtr> diagnostic = null)
        {
            var windows = new List<IntPtr>();
            var enumerated = EnumWindows((window, ignored) =>
            {
                uint pid; GetWindowThreadProcessId(window, out pid);
                return TrackVisibleWindow(identities, windows, window, pid, IsWindowVisible(window));
            }, IntPtr.Zero);
            var selected = RequireSingleWindow(windows, enumerated);
            if (diagnostic != null) diagnostic(selected);
            uint selectedPid;
            GetWindowThreadProcessId(selected, out selectedPid);
            if (selectedPid > int.MaxValue || !identities.ContainsKey((int)selectedPid)) throw Failure("SC_IDENTITY");
            if (IsIconic(selected)) throw Failure("SC_MINIMIZED");
            int cloaked;
            if (DwmGetWindowAttribute(selected, 14, out cloaked, 4) == 0 && cloaked != 0)
                throw Failure("SC_WINDOW_UNAVAILABLE");
            return selected;
        }

        // Numeric native metadata only: no window titles, paths, application text or screenshots in ordinary logs.
        private static void ReportWindow(string stage, IntPtr window, long elapsedMilliseconds = 0)
        {
            try { Console.Error.WriteLine(WindowDiagnostic(stage, window) + " elapsed_ms=" + elapsedMilliseconds.ToString(CultureInfo.InvariantCulture)); }
            catch { } // Best-effort diagnostics must not replace the target's actual result.
        }

        private static string WindowDiagnostic(string stage, IntPtr window)
        {
            uint pid, foregroundPid;
            GetWindowThreadProcessId(window, out pid);
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out foregroundPid);
            Rect rect;
            GetWindowRect(window, out rect);
            return "stage=" + stage + " hwnd=" + window.ToInt64().ToString(CultureInfo.InvariantCulture) +
                " pid=" + pid.ToString(CultureInfo.InvariantCulture) +
                " root=" + GetAncestor(window, 2).ToInt64().ToString(CultureInfo.InvariantCulture) +
                " owner=" + GetWindow(window, 4).ToInt64().ToString(CultureInfo.InvariantCulture) +
                " iconic=" + (IsIconic(window) ? "1" : "0") +
                " style_minimized=" + ((GetWindowLong(window, -16) & 0x20000000) != 0 ? "1" : "0") +
                " visible=" + (IsWindowVisible(window) ? "1" : "0") +
                " x=" + rect.Left.ToString(CultureInfo.InvariantCulture) + " y=" + rect.Top.ToString(CultureInfo.InvariantCulture) +
                " width=" + (rect.Right - rect.Left).ToString(CultureInfo.InvariantCulture) +
                " height=" + (rect.Bottom - rect.Top).ToString(CultureInfo.InvariantCulture) +
                " foreground_hwnd=" + foreground.ToInt64().ToString(CultureInfo.InvariantCulture) +
                " foreground_pid=" + foregroundPid.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsWindowDiagnostic(string line)
        {
            return line != null && line.Length <= 512 && System.Text.RegularExpressions.Regex.IsMatch(line,
                @"\Astage=(selected|activated|activation_end) hwnd=-?\d+ pid=\d+ root=-?\d+ owner=-?\d+ iconic=[01] style_minimized=[01] visible=[01] x=-?\d+ y=-?\d+ width=-?\d+ height=-?\d+ foreground_hwnd=-?\d+ foreground_pid=\d+( elapsed_ms=\d+)?\z");
        }

        private static bool TrackVisibleWindow(Dictionary<int, long> identities, List<IntPtr> windows,
            IntPtr window, uint pid, bool visible)
        {
            if (pid <= int.MaxValue && identities.ContainsKey((int)pid) && visible) windows.Add(window);
            return windows.Count < 2;
        }

        private static IntPtr RequireSingleWindow(List<IntPtr> windows, bool enumerated)
        {
            if (windows.Count > 1) throw Failure("SC_AMBIGUOUS_WINDOW");
            if (!enumerated || windows.Count == 0) throw Failure("SC_WINDOW_UNAVAILABLE");
            return windows[0];
        }

        private static void CheckDesktop(int session)
        {
            IntPtr state; int bytes;
            if (!WTSQuerySessionInformation(IntPtr.Zero, session, 8, out state, out bytes)) throw Failure("SC_DESKTOP_UNAVAILABLE");
            try { if (bytes < 4 || Marshal.ReadInt32(state) != 0) throw Failure("SC_DESKTOP_UNAVAILABLE"); }
            finally { WTSFreeMemory(state); }
            var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS; never switch or unlock a desktop.
            if (desktop == IntPtr.Zero) throw Failure("SC_DESKTOP_UNAVAILABLE");
            try
            {
                var name = new StringBuilder(256); uint needed;
                if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out needed) ||
                    !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase)) throw Failure("SC_DESKTOP_UNAVAILABLE");
                name.Clear();
                if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out needed) ||
                    !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase)) throw Failure("SC_DESKTOP_UNAVAILABLE");
            }
            finally { CloseDesktop(desktop); }
        }

        // Internal so the Slave auto-copy worker can reuse the same visibility/size/blank rules in its own process
        // instead of spawning a second capture worker. Throws InvalidDataException with an SC_* code on failure.
        internal static PowerSiFrame CaptureWindow(IntPtr window)
        {
            RequireResponsive(window);
            Rect rect;
            if (!IsWindowVisible(window) || IsIconic(window) || !GetClientRect(window, out rect)) throw Failure("SC_WINDOW_UNAVAILABLE");
            CheckSize(rect.Right, rect.Bottom);
            using (var bitmap = new Bitmap(rect.Right, rect.Bottom, PixelFormat.Format24bppRgb))
            {
                var captured = DateTime.UtcNow;
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Black);
                    var dc = graphics.GetHdc();
                    try
                    {
                        // ponytail: documented PrintWindow client capture only; no desktop pixels or input-based fallback.
                        if (!PrintWindow(window, dc, 1)) throw Failure("SC_CAPTURE_FAILED");
                    }
                    finally { graphics.ReleaseHdc(dc); }
                }
                Rect after;
                RequireResponsive(window);
                if (!IsWindowVisible(window) || IsIconic(window) || !GetClientRect(window, out after) ||
                    after.Right != rect.Right || after.Bottom != rect.Bottom) throw Failure("SC_WINDOW_CHANGED");
                CheckNotBlank(bitmap);
                using (var memory = new MemoryStream())
                {
                    bitmap.Save(memory, ImageFormat.Png);
                    if (memory.Length > MaxPngBytes) throw Failure("SC_SIZE");
                    return new PowerSiFrame { Png = memory.ToArray(), CapturedUtc = captured, PixelSize = bitmap.Size };
                }
            }
        }

        private static void CheckSize(long width, long height)
        {
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || width * height > 16 * 1024 * 1024)
                throw Failure("SC_SIZE");
        }

        private static void CheckNotBlank(Bitmap bitmap)
        {
            var bits = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[bitmap.Width * 3]; int blue = -1, green = -1, red = -1;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), row, 0, row.Length);
                    if (y == 0) { blue = row[0]; green = row[1]; red = row[2]; }
                    for (var x = 0; x < row.Length; x += 3)
                        if (row[x] != blue || row[x + 1] != green || row[x + 2] != red) return;
                }
                throw Failure("SC_BLANK"); // Nonuniform pixels do not prove that an application rendered every pane correctly.
            }
            finally { bitmap.UnlockBits(bits); }
        }

        internal static PowerSiFrame Parse(string wire)
        {
            if (Errors.Contains(wire)) throw Failure(wire);
            if (wire == null || wire.Length > MaxWireChars) throw Failure("SC_INVALID_IMAGE");
            var parts = wire.Split('|'); long ticks;
            if (parts.Length != 3 || parts[0] != "SC1" || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) ||
                ticks < 1 || ticks > DateTime.MaxValue.Ticks || ticks.ToString(CultureInfo.InvariantCulture) != parts[1]) throw Failure("SC_INVALID_IMAGE");
            byte[] png;
            try { png = Convert.FromBase64String(parts[2]); }
            catch (FormatException) { throw Failure("SC_INVALID_IMAGE"); }
            if (png.Length < 33 || png.Length > MaxPngBytes || Convert.ToBase64String(png) != parts[2] ||
                !png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !png.Skip(8).Take(8).SequenceEqual(new byte[] { 0, 0, 0, 13, 73, 72, 68, 82 })) throw Failure("SC_INVALID_IMAGE");
            long width = 0, height = 0;
            for (var i = 16; i < 20; i++) { width = (width << 8) + png[i]; height = (height << 8) + png[i + 4]; }
            CheckSize(width, height); // Validate compressed-image dimensions before asking GDI+ to decode it.
            try
            {
                using (var memory = new MemoryStream(png, false))
                using (var image = Image.FromStream(memory, false, true))
                    if (image.Width != width || image.Height != height) throw Failure("SC_INVALID_IMAGE");
            }
            catch { throw Failure("SC_INVALID_IMAGE"); }
            return new PowerSiFrame { Png = png, CapturedUtc = new DateTime(ticks, DateTimeKind.Utc), PixelSize = new Size((int)width, (int)height) };
        }

        internal static byte[] Crop(PowerSiFrame frame, Rectangle bounds)
        {
            // Coordinates only select existing pixels. Never recapture, resize, synthesize pixels, or operate PowerSI.
            if (frame == null || bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0 ||
                bounds.Width > frame.PixelSize.Width || bounds.Height > frame.PixelSize.Height ||
                bounds.X > frame.PixelSize.Width - bounds.Width || bounds.Y > frame.PixelSize.Height - bounds.Height ||
                bounds.Size == frame.PixelSize) throw new LocalVisionException("REGION_INVALID");
            using (var stream = new MemoryStream(frame.Png, false))
            using (var source = new Bitmap(stream))
            {
                if (source.Size != frame.PixelSize) throw new LocalVisionException("IMAGE_INVALID");
                using (var crop = source.Clone(bounds, PixelFormat.Format24bppRgb))
                using (var output = new MemoryStream())
                {
                    crop.Save(output, ImageFormat.Png);
                    if (output.Length > MaxPngBytes) throw new LocalVisionException("IMAGE_INVALID");
                    return output.ToArray();
                }
            }
        }

        internal static void SelfTest()
        {
            void Reject(Action action, string code)
            {
                try { action(); }
                catch (InvalidDataException error) when (error.Message == code) { return; }
                throw new InvalidOperationException("PowerSI capture self-test: " + code);
            }
            CheckSize(4096, 4096);
            Reject(() => CheckSize(4097, 1), "SC_SIZE");
            Reject(() => CheckSize(0, 50), "SC_SIZE");
            Reject(() => Parse("SC1|1|AAAA"), "SC_INVALID_IMAGE");
            Reject(() => Parse("SC_TIMEOUT"), "SC_TIMEOUT");
            Reject(() => Parse("SC_PENDING"), "SC_PENDING");
            Reject(() => Parse("SC_FOREGROUND_FAILED"), "SC_FOREGROUND_FAILED");
            foreach (var code in new[] { "SC_GATE_REJECTED", "SC_FOREGROUND_REQUEST_REJECTED",
                "SC_FOREGROUND_WAIT_PENDING", "SC_FOREGROUND_WAIT_MISMATCH", "SC_FOREGROUND_WAIT_TIMEOUT",
                "SC_FOREGROUND_MISMATCH_PRECAPTURE", "SC_FOREGROUND_MISMATCH_POSTCAPTURE" })
                Reject(() => Parse(code), code);
            Reject(() => Parse(new string('X', MaxWireChars + 1)), "SC_INVALID_IMAGE");

            var singleton = new ProcessInventory { SessionId = 7, Items = new[] {
                new ProcessState { Pid = 41, Name = "powersi", StartUtcTicks = 123 } } };
            if (WorkerArguments(singleton, true, CancellationToken.None) != "7 41:123")
                throw new InvalidOperationException("Explicit PowerSI singleton identity changed.");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    PrepareAsync(singleton, cancelled.Token).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Cancelled prepare worker started.");
                }
                catch (OperationCanceledException) { }
            }
            Reject(() => WorkerArguments(new ProcessInventory { SessionId = 7, Items = new[] {
                new ProcessState { Pid = 41, Name = "powersi", StartUtcTicks = 123 },
                new ProcessState { Pid = 42, Name = "pwrsi", StartUtcTicks = 456 } } }, true,
                CancellationToken.None), "SC_IDENTITY");
            using (var current = Process.GetCurrentProcess())
            {
                // Exercise the real read-only child worker against our own non-PowerSI PID: no external window is touched.
                var wrongIdentity = new ProcessInventory { SessionId = current.SessionId, Items = new[] {
                    new ProcessState { Pid = current.Id, Name = "powersi", StartUtcTicks = current.StartTime.ToUniversalTime().Ticks } } };
                Reject(() => CheckResponsiveAsync(wrongIdentity, CancellationToken.None).GetAwaiter().GetResult(), "SC_IDENTITY");
            }

            var identities = new Dictionary<int, long> { { 41, 123 } };
            var windows = new List<IntPtr>();
            // Other application windows can appear anywhere in EnumWindows order, including before either target.
            for (uint otherPid = 100; otherPid < 200; otherPid++)
                if (!TrackVisibleWindow(identities, windows, new IntPtr(otherPid), otherPid, true) || windows.Count != 0)
                    throw new InvalidOperationException("An unrelated application's window was selected.");
            if (!TrackVisibleWindow(identities, windows, new IntPtr(420), 42, true) || windows.Count != 0 ||
                !TrackVisibleWindow(identities, windows, new IntPtr(410), 41, true) ||
                RequireSingleWindow(windows, true) != new IntPtr(410) ||
                TrackVisibleWindow(identities, windows, new IntPtr(411), 41, true))
                throw new InvalidOperationException("Explicit PowerSI window selection changed.");
            Reject(() => RequireSingleWindow(windows, false), "SC_AMBIGUOUS_WINDOW");
            WindowStateSelfTest();
            ResponsivenessSelfTest();

            using (var bitmap = new Bitmap(32, 24, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
                Reject(() => CheckNotBlank(bitmap), "SC_BLANK");
                bitmap.SetPixel(8, 8, Color.Black);
                CheckNotBlank(bitmap);
                using (var memory = new MemoryStream())
                {
                    bitmap.Save(memory, ImageFormat.Png);
                    var png = memory.ToArray(); var now = DateTime.UtcNow;
                    var decoded = Parse("SC1|" + now.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Convert.ToBase64String(png));
                    if (decoded.CapturedUtc != now || !decoded.Png.SequenceEqual(png)) throw new InvalidOperationException("PowerSI capture roundtrip.");
                    var region = new Rectangle(4, 5, 10, 8);
                    using (var cropped = new MemoryStream(Crop(decoded, region), false))
                    using (var image = new Bitmap(cropped))
                    {
                        if (image.Size != region.Size) throw new InvalidOperationException("Crop dimensions changed.");
                        for (int y = 0; y < region.Height; y++)
                            for (int x = 0; x < region.Width; x++)
                                if (image.GetPixel(x, y) != bitmap.GetPixel(x + region.X, y + region.Y))
                                    throw new InvalidOperationException("Crop must preserve exact source pixels.");
                    }
                    foreach (var invalid in new[] { new Rectangle(-1, 0, 10, 8), new Rectangle(0, 0, 0, 8),
                        new Rectangle(0, 0, 32, 24), new Rectangle(31, 23, 2, 2), new Rectangle(int.MaxValue, 0, 8, 8) })
                    {
                        try { Crop(decoded, invalid); throw new InvalidOperationException("Invalid crop accepted."); }
                        catch (LocalVisionException ex) when (ex.Code == "REGION_INVALID") { }
                    }
                    png[16] = 127;
                    Reject(() => Parse("SC1|1|" + Convert.ToBase64String(png)), "SC_SIZE");
                }
            }
        }

        private static void WindowStateSelfTest()
        {
            const uint noActivateTool = 0x08000080;
            const uint popupVisible = 0x90000000;
            using (var self = Process.GetCurrentProcess())
            {
                var identities = new Dictionary<int, long> { { self.Id, self.StartTime.ToUniversalTime().Ticks } };
                foreach (bool minimized in new[] { false, true })
                {
                    var before = GetForegroundWindow();
                    var window = CreateWindowEx(noActivateTool, "Static", "Owned window state check", popupVisible |
                        (minimized ? 0x20000000u : 0), -32000, -32000, 320, 200,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (window == IntPtr.Zero) throw new InvalidOperationException("Window state fixture creation failed.");
                    try
                    {
                        var metadata = WindowDiagnostic("selected", window);
                        if (IsIconic(window) != minimized || !IsWindowDiagnostic(metadata) ||
                            !metadata.Contains(" pid=" + self.Id + " ") ||
                            !metadata.Contains(" iconic=" + (minimized ? "1" : "0") + " ") ||
                            IsWindowDiagnostic(metadata + " title=private") || IsWindowDiagnostic("C:\\private\\text"))
                            throw new InvalidOperationException("Native window state or metadata filtering failed.");
                        if (!IsWindowDiagnostic(WindowDiagnostic("activation_end", window) + " elapsed_ms=1000"))
                            throw new InvalidOperationException("Activation-end metadata was rejected.");
                        var previousError = Console.Error;
                        using (var closed = new StringWriter())
                        {
                            closed.Dispose();
                            try { Console.SetError(closed); ReportWindow("selected", window); }
                            finally { Console.SetError(previousError); }
                        }
                        IntPtr recorded = IntPtr.Zero;
                        try
                        {
                            var selected = FindWindow(identities, candidate => recorded = candidate);
                            if (minimized || selected != window) throw new InvalidOperationException("Wrong window state accepted.");
                        }
                        catch (InvalidDataException error) when (minimized && error.Message == "SC_MINIMIZED") { }
                        if (recorded != window || GetForegroundWindow() != before)
                            throw new InvalidOperationException("Selection failed to record its target or activated another window.");
                    }
                    finally { DestroyWindow(window); }
                }
            }
            Console.WriteLine("PASS: PID-only selection amid unrelated windows; native minimized/non-minimized state, no activation on rejection");
        }

        private static void ForegroundReadbackSelfTest(IntPtr first)
        {
            var second = CreateWindowEx(0x08000080, "Static", "Owned late readback check", 0x90000000,
                -31600, -32000, 320, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (second == IntPtr.Zero) throw new InvalidOperationException("Late readback fixture creation failed.");
            try
            {
                Rect beforeWindow, beforeClient, afterWindow, afterClient;
                ReadGeometry(second, out beforeWindow, out beforeClient);
                // Establish the baseline after creating the second top-level window.
                SetExactForeground(first);
                RequireResponsive(second); // WM_NULL completes while first is still foreground.
                RequireForeground(first, "SC_FOREGROUND_WAIT_MISMATCH");
                using (var started = new ManualResetEvent(false))
                {
                    var waiting = Task.Run(() => { started.Set(); WaitForExactForeground(second, first); });
                    if (!started.WaitOne(1000)) throw new InvalidOperationException("Readback check did not start.");
                    Thread.Sleep(150);
                    if (waiting.IsCompleted)
                    {
                        waiting.GetAwaiter().GetResult();
                        throw new InvalidOperationException("Readback completed before target activation: " + WindowDiagnostic("selected", second));
                    }
                    if (!SetForegroundWindow(second)) throw new InvalidOperationException("Owned readback target was not activated.");
                    if (!waiting.Wait(1500)) throw new InvalidOperationException("Readback did not observe late activation.");
                    waiting.GetAwaiter().GetResult();
                }
                ReadGeometry(second, out afterWindow, out afterClient);
                if (!SameRect(beforeWindow, afterWindow) || !SameRect(beforeClient, afterClient))
                    throw new InvalidOperationException("Readback changed target geometry.");
                var clock = Stopwatch.StartNew();
                try { WaitForExactForeground(first, second); throw new InvalidOperationException("Never-activated target was accepted."); }
                catch (InvalidDataException error) when (error.Message == "SC_FOREGROUND_WAIT_TIMEOUT") { }
                if (clock.ElapsedMilliseconds < ForegroundWaitMilliseconds || clock.ElapsedMilliseconds > 2000)
                    throw new InvalidOperationException("Readback timeout was not bounded.");
                try { WaitForExactForeground(first, IntPtr.Zero); throw new InvalidOperationException("Third-party foreground was accepted."); }
                catch (InvalidDataException error) when (error.Message == "SC_FOREGROUND_WAIT_MISMATCH") { }
                SetExactForeground(first);
                Console.WriteLine("PASS: native late foreground after responsive WM_NULL, bounded timeout and third-window rejection");
            }
            finally { DestroyWindow(second); }
        }

        private static void ResponsivenessSelfTest()
        {
            const uint popupVisible = 0x80000000u | 0x10000000u;
            const uint toolWindow = 0x00000080;
            var first = CreateWindowEx(toolWindow, "Static", "Owned capture self-test 1", popupVisible,
                -32000, -32000, 320, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (first == IntPtr.Zero) throw new InvalidOperationException("Owned capture window was not created.");
            try
            {
                var before = GetForegroundWindow();
                RequireResponsive(first);
                if (GetForegroundWindow() != before)
                    throw new InvalidOperationException("Responsiveness probe changed the foreground window.");

                // Exercise the real cross-input-queue delay when this self-test process has foreground rights.
                if (SetForegroundWindow(first) && GetForegroundWindow() == first)
                {
                    ForegroundReadbackSelfTest(first);
                    IntPtr delayed = IntPtr.Zero;
                    Exception delayedError = null;
                    using (var ready = new ManualResetEvent(false))
                    using (var pump = new ManualResetEvent(false))
                    using (var release = new ManualResetEvent(false))
                    {
                        var thread = new Thread(() =>
                        {
                            try
                            {
                                delayed = CreateWindowEx(toolWindow, "Static", "Owned delayed foreground self-test", popupVisible,
                                    -31600, -32000, 320, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                                ready.Set();
                                if (delayed != IntPtr.Zero && pump.WaitOne(5000))
                                {
                                    System.Windows.Forms.Application.DoEvents();
                                    release.WaitOne(5000);
                                }
                            }
                            catch (Exception error) { delayedError = error; }
                            finally
                            {
                                ready.Set();
                                if (delayed != IntPtr.Zero) DestroyWindow(delayed);
                            }
                        });
                        thread.IsBackground = true;
                        thread.SetApartmentState(ApartmentState.STA);
                        thread.Start();
                        if (!ready.WaitOne(2000) || delayed == IntPtr.Zero || delayedError != null)
                        {
                            pump.Set(); release.Set(); thread.Join(2000);
                            throw new InvalidOperationException("Owned delayed window was not created.", delayedError);
                        }
                        try
                        {
                            uint pid;
                            uint ownerThread = GetWindowThreadProcessId(delayed, out pid);
                            using (var self = Process.GetCurrentProcess())
                                if (ownerThread == GetCurrentThreadId() || pid != (uint)self.Id)
                                throw new InvalidOperationException("Delayed foreground test did not cross a message queue.");
                            Rect windowRect, clientRect, currentWindow, currentClient;
                            ReadGeometry(delayed, out windowRect, out clientRect);
                            var delay = new Thread(() => { Thread.Sleep(150); pump.Set(); });
                            delay.IsBackground = true;
                            delay.Start();
                            var clock = Stopwatch.StartNew();
                            SetExactForeground(delayed);
                            if (!delay.Join(2000) || clock.ElapsedMilliseconds < 75 || clock.ElapsedMilliseconds > 2000 ||
                                GetForegroundWindow() != delayed || !GetWindowRect(delayed, out currentWindow) ||
                                !GetClientRect(delayed, out currentClient) || !SameRect(windowRect, currentWindow) ||
                                !SameRect(clientRect, currentClient))
                                throw new InvalidOperationException("Cross-queue foreground barrier or target geometry changed.");
                            Console.WriteLine("PASS: cross-queue foreground completion barrier (single request, unchanged geometry)");
                        }
                        finally
                        {
                            pump.Set(); release.Set();
                            if (!thread.Join(2000)) throw new InvalidOperationException("Owned delayed window did not close.");
                        }
                    }
                }
                else Console.WriteLine("SKIP: cross-queue foreground barrier (no interactive foreground)");
            }
            finally { DestroyWindow(first); }

            try { SetExactForeground(IntPtr.Zero); throw new InvalidOperationException("Invalid foreground target was accepted."); }
            catch (InvalidDataException error) when (error.Message == "SC_FOREGROUND_REQUEST_REJECTED") { }

            IntPtr blocked = IntPtr.Zero;
            Exception blockedError = null;
            using (var ready = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        blocked = CreateWindowEx(0x08000080, "Static", "Owned pending self-test", popupVisible,
                            -32000, -31600, 320, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch (Exception error) { blockedError = error; }
                    finally { ready.Set(); }
                    if (blocked != IntPtr.Zero)
                    {
                        release.WaitOne(5000);
                        DestroyWindow(blocked);
                    }
                });
                thread.IsBackground = true;
                thread.Start();
                if (!ready.WaitOne(2000) || blocked == IntPtr.Zero || blockedError != null)
                {
                    release.Set(); thread.Join(2000);
                    throw new InvalidOperationException("Owned pending window was not created.", blockedError);
                }
                try
                {
                    var foreground = GetForegroundWindow();
                    Rect windowRect, clientRect, currentWindow, currentClient;
                    ReadGeometry(blocked, out windowRect, out clientRect);
                    var clock = Stopwatch.StartNew();
                    try { RequireResponsive(blocked, "SC_FOREGROUND_WAIT_PENDING"); throw new InvalidOperationException("Pending window was accepted."); }
                    catch (InvalidDataException error) when (error.Message == "SC_FOREGROUND_WAIT_PENDING") { }
                    if (clock.ElapsedMilliseconds > 2000)
                        throw new InvalidOperationException("Pending window check exceeded its bound.");
                    if (GetForegroundWindow() != foreground || !GetWindowRect(blocked, out currentWindow) ||
                        !GetClientRect(blocked, out currentClient) || !SameRect(windowRect, currentWindow) ||
                        !SameRect(clientRect, currentClient))
                        throw new InvalidOperationException("Pending window check changed foreground or target geometry.");
                }
                finally
                {
                    release.Set();
                    if (!thread.Join(2000)) throw new InvalidOperationException("Owned pending window did not close.");
                }
            }
        }

        private static InvalidDataException Failure(string code) { return new InvalidDataException(code); }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
        private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
        [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int size, out uint needed);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("wtsapi32.dll")] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int info, out IntPtr buffer, out int size);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    }
}
