using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RemoteMonitorLink
{
    // Metadata-only account of one body search. Counts and geometry never contain screen text, so
    // Summary() may be logged; the rectangles stay local for the diagnostic bundle.
    internal sealed class BodySearchDiagnostics
    {
        internal int FrameWidth, FrameHeight;
        internal int Components;
        internal int Accepted;
        // RejectedCenter is field 8 of B2: centre/containment rejections -- the candidate neither contains the
        // proposal centre nor lies almost entirely (>=90% of its own area) inside the proposal. The B2 field
        // order and count are unchanged from the previous version; only what the rule counts was widened.
        internal int RejectedSize, RejectedFill, RejectedTooLarge, RejectedCenter, RejectedOverlap;
        internal Rectangle? First, Second;

        internal string Summary()
        {
            var values = new[] { FrameWidth, FrameHeight, Components, Accepted,
                RejectedSize, RejectedFill, RejectedTooLarge, RejectedCenter, RejectedOverlap };
            var text = new StringBuilder("B2");
            for (int i = 0; i < values.Length; i++)
                text.Append('|').Append(values[i].ToString(CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }

    internal static class OutputPaneImage
    {
        // API (phase 2 / Slave auto-copy):
        //   Rectangle FindBody(frame, suggested, cancellation)                       — unchanged behaviour.
        //   Rectangle FindBody(frame, suggested, cancellation, out BodySearchDiagnostics diag)
        //   Rectangle FindBodyAt(frame, anchor, cancellation, out BodySearchDiagnostics diag)
        //       — anchor is a client-area point (same pixel space as the frame); the returned body contains it.
        //   byte[] OcrInput(frame, body)                                             — unchanged.
        // Every failure is LocalVisionException("REGION_BOUNDARY_UNCONFIRMED") with Detail = diag.Summary()
        // (metadata only) whenever a scan actually ran; IMAGE_INVALID keeps its own code and no Detail.
        // A candidate is accepted when it contains the proposal centre OR lies almost entirely inside the
        // proposal (>=90% of the candidate's own area); exactly one accepted candidate is still required.
        //
        // ponytail: temporary PowerSI flat neutral-background heuristic, not semantic pane identification.
        // Textured/colored panes or panes covering >=90% of the frame need verified UIA geometry instead.
        internal static Rectangle FindBody(PowerSiFrame frame, Rectangle suggested, CancellationToken cancellation)
        {
            BodySearchDiagnostics ignored;
            return FindBody(frame, suggested, cancellation, out ignored);
        }

        internal static Rectangle FindBody(PowerSiFrame frame, Rectangle suggested, CancellationToken cancellation,
            out BodySearchDiagnostics diag)
        {
            cancellation.ThrowIfCancellationRequested();
            ValidateFrame(frame);
            int width = frame.PixelSize.Width, height = frame.PixelSize.Height;
            diag = new BodySearchDiagnostics { FrameWidth = width, FrameHeight = height };
            if (!Inside(suggested, frame.PixelSize) || suggested.Size == frame.PixelSize)
                throw Unconfirmed(diag);
            var colors = NeutralColors(frame, cancellation);
            var queue = new int[colors.Length];
            var center = new Point(suggested.X + suggested.Width / 2, suggested.Y + suggested.Height / 2);
            Rectangle? found = null;
            for (int start = 0; start < colors.Length; start++)
            {
                if ((start & 65535) == 0) cancellation.ThrowIfCancellationRequested();
                byte color = colors[start];
                if (color == 0) continue;
                diag.Components++;
                int head = 0, tail = 1, left = start % width, right = left, top = start / width, bottom = top;
                queue[0] = start; colors[start] = 0;
                while (head < tail)
                {
                    if ((head & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    int pixel = queue[head++], x = pixel % width, y = pixel / width;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    if (x > 0) Visit(pixel - 1, color, colors, queue, ref tail);
                    if (x + 1 < width) Visit(pixel + 1, color, colors, queue, ref tail);
                    if (y > 0) Visit(pixel - width, color, colors, queue, ref tail);
                    if (y + 1 < height) Visit(pixel + width, color, colors, queue, ref tail);
                }
                var bounds = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
                long area = (long)bounds.Width * bounds.Height;
                // Same acceptance test as before; the counters only record which rule rejected first.
                if (bounds.Width < 120 || bounds.Height < 80 || tail < 10000) { diag.RejectedSize++; continue; }
                if (tail * 100L < area * 55) { diag.RejectedFill++; continue; }
                if (area * 10 >= (long)width * height * 9) { diag.RejectedTooLarge++; continue; }
                var overlap = Rectangle.Intersect(bounds, suggested);
                long overlapArea = (long)overlap.Width * overlap.Height;
                // A proposal may be far wider than the body (a model that swallowed neighbouring panes), so its
                // centre can fall outside the correct candidate. Accept such a candidate when it is mostly inside
                // the proposal instead. Field case (2026-09-11): body 317|393|585|560, proposal 320|385|1570|574.
                if (!bounds.Contains(center) && overlapArea * 10 < area * 9) { diag.RejectedCenter++; continue; }
                if (overlapArea * 2 < Math.Min(area, (long)suggested.Width * suggested.Height))
                { diag.RejectedOverlap++; continue; }
                diag.Accepted++;
                if (found.HasValue) { diag.Second = bounds; throw Unconfirmed(diag); }
                found = bounds; diag.First = bounds;
            }
            cancellation.ThrowIfCancellationRequested();
            if (!found.HasValue) throw Unconfirmed(diag);
            return found.Value;
        }

        // The learned click point replaces the LLM proposal: a small suggestion box centered on the anchor.
        internal static Rectangle FindBodyAt(PowerSiFrame frame, Point anchor, CancellationToken cancellation,
            out BodySearchDiagnostics diag)
        {
            cancellation.ThrowIfCancellationRequested();
            ValidateFrame(frame);
            var size = frame.PixelSize;
            diag = new BodySearchDiagnostics { FrameWidth = size.Width, FrameHeight = size.Height };
            if (anchor.X < 0 || anchor.Y < 0 || anchor.X >= size.Width || anchor.Y >= size.Height)
                throw Unconfirmed(diag);
            int boxWidth = Math.Min(AnchorBox, size.Width), boxHeight = Math.Min(AnchorBox, size.Height);
            int left = Clamp(anchor.X - boxWidth / 2, 0, size.Width - boxWidth);
            int top = Clamp(anchor.Y - boxHeight / 2, 0, size.Height - boxHeight);
            var body = FindBody(frame, new Rectangle(left, top, boxWidth, boxHeight), cancellation, out diag);
            if (!body.Contains(anchor)) throw Unconfirmed(diag);
            return body;
        }

        private const int AnchorBox = 40;

        private static int Clamp(int value, int low, int high)
        { return value < low ? low : (value > high ? high : value); }

        private static LocalVisionException Unconfirmed(BodySearchDiagnostics diag)
        {
            var failure = new LocalVisionException("REGION_BOUNDARY_UNCONFIRMED");
            if (diag != null) failure.Detail = diag.Summary();
            return failure;
        }

        private static void Visit(int pixel, byte color, byte[] colors, int[] queue, ref int tail)
        {
            if (colors[pixel] != color) return;
            colors[pixel] = 0;
            queue[tail++] = pixel;
        }

        private static byte[] NeutralColors(PowerSiFrame frame, CancellationToken cancellation)
        {
            using (var stream = new MemoryStream(frame.Png, false))
            using (var bitmap = new Bitmap(stream))
            {
                if (bitmap.Size != frame.PixelSize) throw new LocalVisionException("IMAGE_INVALID");
                int width = bitmap.Width, height = bitmap.Height;
                var colors = new byte[width * height];
                var row = new byte[width * 3];
                var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), row, 0, row.Length);
                        for (int x = 0; x < width; x++)
                        {
                            int blue = row[x * 3], green = row[x * 3 + 1], red = row[x * 3 + 2];
                            if (Math.Abs(red - green) <= 3 && Math.Abs(green - blue) <= 3)
                                colors[y * width + x] = (byte)(1 + red / 8);
                        }
                    }
                }
                finally { bitmap.UnlockBits(bits); }
                return colors;
            }
        }

        // ponytail: only an exactly uniform neutral body is visibly empty; a caret, texture or any ink
        // remains unconfirmed. This says nothing about off-screen history or a custom control's buffer.
        internal static bool IsVisiblyEmpty(PowerSiFrame frame, Rectangle body, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ValidateFrame(frame);
            using (var stream = new MemoryStream(PowerSiScreenCapture.Crop(frame, body), false))
            using (var bitmap = new Bitmap(stream))
            {
                var first = bitmap.GetPixel(0, 0);
                if (Math.Abs(first.R - first.G) > 3 || Math.Abs(first.R - first.B) > 3) return false;
                var bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[bitmap.Width * 3];
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), row, 0, row.Length);
                        for (int x = 0; x < row.Length; x += 3)
                            if (row[x] != first.B || row[x + 1] != first.G || row[x + 2] != first.R) return false;
                    }
                    return true;
                }
                finally { bitmap.UnlockBits(bits); }
            }
        }

        internal static byte[] OcrInput(PowerSiFrame frame, Rectangle body)
        {
            ValidateFrame(frame);
            // Reuse the source-pixel crop validation before selecting the bottom visible rows.
            byte[] cropped = PowerSiScreenCapture.Crop(frame, body);
            using (var stream = new MemoryStream(cropped, false))
            using (var source = new Bitmap(stream))
            {
                int height = Math.Min(256, source.Height), scale = source.Width <= 2048 ? 2 : 1;
                using (var output = new Bitmap(source.Width * scale, height * scale, PixelFormat.Format24bppRgb))
                using (var graphics = Graphics.FromImage(output))
                using (var png = new MemoryStream())
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    graphics.DrawImage(source, new Rectangle(0, 0, output.Width, output.Height),
                        new Rectangle(0, source.Height - height, source.Width, height), GraphicsUnit.Pixel);
                    output.Save(png, ImageFormat.Png);
                    return png.ToArray();
                }
            }
        }

        private static bool Inside(Rectangle bounds, Size size)
        {
            return bounds.X >= 0 && bounds.Y >= 0 && bounds.Width > 0 && bounds.Height > 0 &&
                bounds.Width <= size.Width && bounds.Height <= size.Height &&
                bounds.X <= size.Width - bounds.Width && bounds.Y <= size.Height - bounds.Height;
        }

        private static void ValidateFrame(PowerSiFrame frame)
        {
            if (frame == null || frame.Png == null || frame.Png.Length < 33 || frame.Png.Length > 8 * 1024 * 1024)
                throw new LocalVisionException("IMAGE_INVALID");
            byte[] header = { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 };
            for (int i = 0; i < header.Length; i++)
                if (frame.Png[i] != header[i]) throw new LocalVisionException("IMAGE_INVALID");
            long width = 0, height = 0;
            for (int i = 16; i < 20; i++) { width = (width << 8) + frame.Png[i]; height = (height << 8) + frame.Png[i + 4]; }
            if (width < 1 || height < 1 || width > 4096 || height > 4096 || width * height > 16 * 1024 * 1024 ||
                width != frame.PixelSize.Width || height != frame.PixelSize.Height)
                throw new LocalVisionException("IMAGE_INVALID");
        }

        internal static void SelfTest()
        {
            void Check(bool passed, string name)
            { if (!passed) throw new InvalidOperationException("Output pane image self-test: " + name); }
            void Reject(Action action, string code = "REGION_BOUNDARY_UNCONFIRMED")
            {
                try { action(); }
                catch (LocalVisionException ex) when (ex.Code == code) { return; }
                throw new InvalidOperationException("Output pane image accepted invalid input: " + code);
            }
            PowerSiFrame Frame(Bitmap bitmap)
            {
                using (var png = new MemoryStream())
                {
                    bitmap.Save(png, ImageFormat.Png);
                    return new PowerSiFrame { Png = png.ToArray(), PixelSize = bitmap.Size, CapturedUtc = DateTime.UtcNow };
                }
            }
            var body = new Rectangle(20, 40, 360, 320);
            var suggested = new Rectangle(100, 150, 360, 160);
            using (var bitmap = new Bitmap(600, 420, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.FromArgb(40, 40, 40));
                Check(IsVisiblyEmpty(Frame(bitmap), body, CancellationToken.None), "uniform body is visibly empty only");
                bitmap.SetPixel(body.Left, body.Top, Color.White);
                Check(!IsVisiblyEmpty(Frame(bitmap), body, CancellationToken.None), "even first-pixel ink prevents empty classification");
                bitmap.SetPixel(body.Left, body.Top, Color.FromArgb(40, 40, 40));
                bitmap.SetPixel(body.Right - 1, body.Bottom - 1, Color.White);
                Check(!IsVisiblyEmpty(Frame(bitmap), body, CancellationToken.None), "last-pixel ink is not missed");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    try { IsVisiblyEmpty(Frame(bitmap), body, canceled.Token); throw new InvalidOperationException("Canceled empty scan ran."); }
                    catch (OperationCanceledException) { }
                }
            }
            foreach (int grey in new[] { 40, 232 })
            using (var bitmap = new Bitmap(600, 420, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                using (var background = new SolidBrush(Color.FromArgb(grey, grey, grey)))
                {
                    graphics.Clear(Color.DarkBlue);
                    graphics.FillRectangle(background, body);
                    graphics.FillRectangle(background, new Rectangle(410, 40, 170, 320));
                    for (int y = 55; y < 350; y += 16) graphics.FillRectangle(Brushes.Black, 30, y, 300, 2);
                }
                var frame = Frame(bitmap);
                Check(FindBody(frame, suggested, CancellationToken.None) == body, "partial proposal expands to full body without adjacent pane");
                Reject(() => FindBody(frame, new Rectangle(1, 1, 10, 10), CancellationToken.None));
                Reject(() => FindBody(frame, new Rectangle(-1, 40, 360, 320), CancellationToken.None));
                Reject(() => FindBody(frame, new Rectangle(int.MaxValue, 40, 360, 320), CancellationToken.None));
                Reject(() => FindBody(frame, new Rectangle(0, 0, 600, 420), CancellationToken.None));
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    try { FindBody(frame, suggested, canceled.Token); throw new InvalidOperationException("Canceled image scan ran."); }
                    catch (OperationCanceledException) { }
                }
                frame.PixelSize = new Size(4097, 420);
                Reject(() => FindBody(frame, suggested, CancellationToken.None), "IMAGE_INVALID");
            }
            using (var bitmap = new Bitmap(600, 420, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.DarkBlue);
                    graphics.FillRectangle(Brushes.White, 30, 30, 400, 350);
                    graphics.FillRectangle(Brushes.Gray, 130, 125, 160, 140);
                }
                Reject(() => FindBody(Frame(bitmap), new Rectangle(140, 140, 120, 100), CancellationToken.None));
                using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
                Reject(() => FindBody(Frame(bitmap), suggested, CancellationToken.None));
            }
            using (var bitmap = new Bitmap(24, 320, PixelFormat.Format24bppRgb))
            {
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++) bitmap.SetPixel(x, y, Color.FromArgb(x * 9, y % 256, (x + y) % 256));
                var region = new Rectangle(3, 8, 12, 300);
                using (var png = new MemoryStream(OcrInput(Frame(bitmap), region), false))
                using (var output = new Bitmap(png))
                {
                    Check(output.Size == new Size(24, 512), "bottom 256 source rows enlarged exactly twice");
                    for (int y = 0; y < output.Height; y++)
                        for (int x = 0; x < output.Width; x++)
                            Check(output.GetPixel(x, y) == bitmap.GetPixel(region.X + x / 2, region.Bottom - 256 + y / 2),
                                "nearest-neighbor duplicates source pixels including newest bottom row");
                }
            }
            foreach (int width in new[] { 120, 2048, 2049, 4096 })
            using (var bitmap = new Bitmap(width, 80, PixelFormat.Format24bppRgb))
            using (var png = new MemoryStream(OcrInput(Frame(bitmap), new Rectangle(0, 10, width, 60)), false))
            using (var output = new Bitmap(png))
            {
                int scale = width <= 2048 ? 2 : 1;
                Check(output.Size == new Size(width * scale, 60 * scale), "short body and maximum OCR dimensions");
            }
            // Body search diagnostics and the anchor-driven search used by Output auto-copy.
            string RejectDetail(Action action)
            {
                try { action(); }
                catch (LocalVisionException ex) when (ex.Code == "REGION_BOUNDARY_UNCONFIRMED")
                {
                    if (string.IsNullOrEmpty(ex.Detail) || !ex.Detail.StartsWith("B2|", StringComparison.Ordinal))
                        throw new InvalidOperationException("Body search failure lost its diagnostics.");
                    return ex.Detail;
                }
                throw new InvalidOperationException("Body search accepted an unconfirmed boundary.");
            }
            using (var bitmap = new Bitmap(600, 420, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                using (var background = new SolidBrush(Color.FromArgb(232, 232, 232)))
                {
                    graphics.Clear(Color.DarkBlue);
                    graphics.FillRectangle(background, body);
                    graphics.FillRectangle(background, new Rectangle(410, 40, 175, 320));
                    for (int y = 55; y < 350; y += 16) graphics.FillRectangle(Brushes.Black, 30, y, 300, 2);
                }
                var frame = Frame(bitmap);
                BodySearchDiagnostics diag;
                Check(FindBody(frame, suggested, CancellationToken.None, out diag) == body, "diagnostic overload keeps the accepted body");
                Check(diag.Accepted == 1 && diag.First == body && !diag.Second.HasValue, "one flat body is the only accepted candidate");
                Check(diag.FrameWidth == 600 && diag.FrameHeight == 420, "body diagnostics record the frame size");
                Check(diag.RejectedSize > 0 && diag.RejectedCenter > 0 && diag.RejectedFill == 0 && diag.RejectedTooLarge == 0 &&
                    diag.RejectedOverlap == 0, "rejected components are counted by their own rule");
                Check(diag.Components == diag.Accepted + diag.RejectedSize + diag.RejectedFill + diag.RejectedTooLarge +
                    diag.RejectedCenter + diag.RejectedOverlap, "every scanned component is classified exactly once");
                var fields = diag.Summary().Split('|');
                Check(fields.Length == 10 && fields[0] == "B2" && fields[1] == "600" && fields[2] == "420" && fields[4] == "1",
                    "body diagnostics summary keeps the B2 metadata order");
                for (int i = 1; i < fields.Length; i++)
                {
                    int parsed;
                    Check(int.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed >= 0,
                        "body diagnostics summary carries only non-negative integers");
                }
                // Center of the proposal falls between the two panes: every candidate is rejected by the center rule.
                var missed = RejectDetail(() => FindBody(frame, new Rectangle(200, 150, 360, 160), CancellationToken.None)).Split('|');
                Check(missed[4] == "0" && int.Parse(missed[8], CultureInfo.InvariantCulture) > 1,
                    "proposal whose center misses every body is rejected and counted");
                BodySearchDiagnostics anchored;
                Check(FindBodyAt(frame, new Point(200, 200), CancellationToken.None, out anchored) == body &&
                    anchored.Accepted == 1 && anchored.FrameWidth == 600 && anchored.First == body,
                    "anchor inside the body resolves the whole body");
                Check(FindBodyAt(frame, new Point(60, 350), CancellationToken.None, out anchored) == body,
                    "anchor on a text row still resolves the surrounding body");
                // Clamped anchor box near the frame edge may select a neighbouring body: it must not be returned.
                RejectDetail(() => { BodySearchDiagnostics ignored; FindBodyAt(frame, new Point(595, 200), CancellationToken.None, out ignored); });
                RejectDetail(() => { BodySearchDiagnostics ignored; FindBodyAt(frame, new Point(595, 415), CancellationToken.None, out ignored); });
                RejectDetail(() => { BodySearchDiagnostics ignored; FindBodyAt(frame, new Point(-1, 5), CancellationToken.None, out ignored); });
                RejectDetail(() => { BodySearchDiagnostics ignored; FindBodyAt(frame, new Point(0, 420), CancellationToken.None, out ignored); });
            }
            using (var bitmap = new Bitmap(600, 420, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.DarkBlue);
                    graphics.FillRectangle(Brushes.White, 30, 30, 400, 350);
                    graphics.FillRectangle(Brushes.Gray, 130, 125, 160, 140);
                }
                var ambiguous = RejectDetail(() => FindBody(Frame(bitmap), new Rectangle(140, 140, 120, 100), CancellationToken.None)).Split('|');
                Check(ambiguous[4] == "2", "two bodies overlapping the proposal are both reported before the failure");
                // Both bodies are almost entirely inside this huge proposal: containment must not resolve the ambiguity.
                var enclosed = RejectDetail(() => FindBody(Frame(bitmap), new Rectangle(10, 10, 580, 400), CancellationToken.None)).Split('|');
                Check(enclosed[4] == "2", "a proposal enclosing two bodies stays ambiguous");
            }
            // 2026-09-11 field geometry at the real frame size: the proposal keeps the correct left/top/height but is
            // far too wide, so its centre lands on a neighbouring pane area. The body must still be accepted.
            using (var bitmap = new Bitmap(1920, 1009, PixelFormat.Format24bppRgb))
            {
                var fieldBody = new Rectangle(317, 393, 585, 560);
                using (var graphics = Graphics.FromImage(bitmap))
                using (var background = new SolidBrush(Color.FromArgb(240, 240, 240)))
                {
                    graphics.Clear(Color.DarkBlue);
                    graphics.FillRectangle(background, fieldBody);
                    for (int y = 400; y < 945; y += 16) graphics.FillRectangle(Brushes.Black, 325, y, 560, 2);
                }
                var frame = Frame(bitmap);
                BodySearchDiagnostics wide;
                Check(FindBody(frame, new Rectangle(320, 385, 1570, 574), CancellationToken.None, out wide) == fieldBody &&
                    wide.Accepted == 1 && wide.FrameWidth == 1920 && wide.FrameHeight == 1009,
                    "a too-wide proposal that still encloses the body accepts it");
                // The other field proposal covered a different pane entirely: no overlap, no containment, no body.
                RejectDetail(() => FindBody(frame, new Rectangle(1420, 260, 462, 376), CancellationToken.None));
                BodySearchDiagnostics anchoredField;
                Check(FindBodyAt(frame, new Point(609, 673), CancellationToken.None, out anchoredField) == fieldBody,
                    "the learned click point resolves the same field body");
            }
        }
    }
}
