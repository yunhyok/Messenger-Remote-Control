using System;
using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteMonitorLink
{
    // ponytail: one on-demand inference at a time; no scheduler, model loading, or cloud fallback.
    internal static class PowerSiVision
    {
        private static readonly SemaphoreSlim Reading = new SemaphoreSlim(1, 1);
        internal const int RequestDeadlineMilliseconds = 105000;

        internal static async Task<PowerSiObservation> CaptureAsync(ProcessInventory inventory, LocalVisionSettings settings,
            CancellationToken cancellation, IProgress<string> progress = null, PowerSiFrame savedFrame = null,
            PowerSiObservation savedCrop = null, bool locateOnly = false, Func<CancellationToken, Task> checkResponsive = null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (savedCrop != null && (locateOnly || savedFrame == null || !ReferenceEquals(savedCrop.LocalFrame, savedFrame) ||
                savedCrop.LocalImage == null))
                throw new InvalidDataException("SAVED_CROP_INVALID");
            if (savedFrame == null) inventory.Validate();
            if (settings == null || !settings.Enabled) return PowerSiObservation.VisionUnavailable("VISION_NOT_CONFIGURED");
            settings = settings.Clone(); settings.Validate();
            if (savedFrame == null && inventory.Items.Length == 0) return PowerSiObservation.VisionUnavailable("NOT_OBSERVED");
            if (!await Reading.WaitAsync(0, cancellation).ConfigureAwait(false)) return PowerSiObservation.VisionUnavailable("VISION_BUSY");
            PowerSiFrame frame = savedFrame;
            byte[] crop = null, pane = null, suggested = null;
            LocalVisionModel modelInfo = null;
            bool joinedLocate = savedCrop != null && savedCrop.LocalVisionMode == "LOCATE_ONLY";
            var elapsed = Stopwatch.StartNew();
            var phaseClock = new Stopwatch();
            long priorElapsedMs = 0, locateMs = 0, readMs = 0;
            string regionInfo = null;
            string bodyDiagnostics = null;
            string captureInfo = null, model = null, stage = "CAPTURE";
            Rectangle outputBody = Rectangle.Empty;
            PowerSiObservation Finish(PowerSiObservation result)
            {
                result.LocalFrame = frame;
                result.LocalFullImage = frame?.Png;
                result.LocalImage = crop;
                result.LocalPaneImage = pane;
                result.LocalSuggestedImage = suggested;
                result.LocalCaptureInfo = captureInfo ?? "Output 전체 본문 경계 미확정 — 모델 제안/원본 확인 필요";
                result.LocalModelInfo = modelInfo;
                result.LocalModel = modelInfo?.Id ?? model;
                result.LocalRegionInfo = regionInfo;
                result.LocalBodyDiagnostics = bodyDiagnostics;
                result.LocalFrameSize = frame == null ? Size.Empty : frame.PixelSize;
                result.LocalOutputBody = outputBody;
                result.LocalVisionMode = locateOnly || result.LocalVisibleEmpty ? "LOCATE_ONLY" : savedCrop == null || joinedLocate ? "LOCATE_OCR" : "OCR_ONLY";
                result.LocalRequestTimeoutSeconds = settings.TimeoutSeconds;
                result.LocalElapsedMs = priorElapsedMs + elapsed.ElapsedMilliseconds;
                result.LocalLocateMs = stage == "LOCATE_OUTPUT" ? phaseClock.ElapsedMilliseconds : locateMs;
                result.LocalReadMs = stage == "READ_CROP" ? phaseClock.ElapsedMilliseconds : readMs;
                if (frame != null)
                    using (var sha = SHA256.Create()) result.LocalSampleId = BitConverter.ToString(sha.ComputeHash(frame.Png)).Replace("-", "");
                if (crop != null)
                    using (var sha = SHA256.Create()) result.LocalOcrSampleId = BitConverter.ToString(sha.ComputeHash(crop)).Replace("-", "");
                return result;
            }
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(100000);
                    if (savedCrop != null)
                    {
                        // ponytail: reuse the exact in-memory OCR bytes; no second locator, recrop, or persisted dataset.
                        crop = savedCrop.LocalImage;
                        pane = savedCrop.LocalPaneImage;
                        suggested = savedCrop.LocalSuggestedImage;
                        regionInfo = savedCrop.LocalRegionInfo;
                        bodyDiagnostics = savedCrop.LocalBodyDiagnostics;
                        captureInfo = savedCrop.LocalCaptureInfo;
                        outputBody = savedCrop.LocalOutputBody;
                        if (joinedLocate)
                        {
                            var locatedModel = savedCrop.LocalModelInfo?.Id ?? savedCrop.LocalModel;
                            if (string.IsNullOrEmpty(locatedModel)) throw new InvalidDataException("SAVED_CROP_INVALID");
                            settings.ModelId = locatedModel;
                            modelInfo = savedCrop.LocalModelInfo;
                            model = locatedModel;
                            locateMs = savedCrop.LocalLocateMs;
                            priorElapsedMs = savedCrop.LocalElapsedMs;
                        }
                        stage = "REUSE_CROP";
                        progress?.Report(stage);
                    }
                    else
                    {
                        progress?.Report(stage);
                        if (frame == null) frame = await PowerSiScreenCapture.CaptureAsync(inventory, deadline.Token).ConfigureAwait(false);
                        stage = "LOCATE_OUTPUT"; progress?.Report(stage);
                        phaseClock.Restart();
                        if (checkResponsive != null) await checkResponsive(deadline.Token).ConfigureAwait(false);
                        var region = await LocalVisionClient.LocateAsync(settings, frame.Png, frame.PixelSize.Width,
                            frame.PixelSize.Height, deadline.Token).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        locateMs = phaseClock.ElapsedMilliseconds;
                        model = region.ModelId;
                        modelInfo = region.ModelInfo;
                        settings.ModelId = model; // Keep the same loaded instance; never silently switch OCR to another model.
                        stage = "CROP_OUTPUT"; progress?.Report(stage);
                        suggested = PowerSiScreenCapture.Crop(frame, region.Bounds);
                        regionInfo = "P2|" + Box(region.Bounds);
                        BodySearchDiagnostics bodySearch;
                        var body = OutputPaneImage.FindBody(frame, region.Bounds, deadline.Token, out bodySearch);
                        outputBody = body;
                        bodyDiagnostics = bodySearch.Summary();
                        pane = PowerSiScreenCapture.Crop(frame, body);
                        crop = OutputPaneImage.OcrInput(frame, body);
                        var ocrSize = new Size(body.Width, Math.Min(256, body.Height));
                        int scale = body.Width <= 2048 ? 2 : 1;
                        regionInfo += "|" + Box(body) + "|" + (ocrSize.Width * scale).ToString(CultureInfo.InvariantCulture) + "|" + (ocrSize.Height * scale).ToString(CultureInfo.InvariantCulture);
                        captureInfo = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "원본 {0}×{1}px / LLM 제안 {2} / 본문 경계 {3}\r\nOCR: 본문 하단 {4}px ×{5} 확대 / 영역·숫자 정확도 미검증",
                            frame.PixelSize.Width, frame.PixelSize.Height, Box(region.Bounds), Box(body), ocrSize.Height, scale);
                        if (OutputPaneImage.IsVisiblyEmpty(frame, body, deadline.Token))
                        {
                            var empty = PowerSiObservation.VisionLogExcerpt(null, frame.CapturedUtc);
                            empty.LocalVisibleEmpty = true;
                            empty.LocalEvidence = "확인된 Output 화면 영역에 보이는 내용이 없습니다.\r\n" +
                                "화면 판정이며 전체 버퍼·과거 로그가 비어 있다는 뜻은 아닙니다. 클릭·복사·OCR은 생략했습니다.";
                            return Finish(empty);
                        }
                        if (locateOnly)
                        {
                            stage = "LOCATE_ONLY"; progress?.Report(stage);
                            return Finish(PowerSiObservation.VisionLogExcerpt(null, frame.CapturedUtc));
                        }
                    }
                    stage = "READ_CROP"; progress?.Report(stage);
                    phaseClock.Restart();
                    if (checkResponsive != null) await checkResponsive(deadline.Token).ConfigureAwait(false);
                    var read = await LocalVisionClient.ReadAsync(settings, crop, deadline.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    readMs = phaseClock.ElapsedMilliseconds;
                    modelInfo = read.ModelInfo;
                    var result = PowerSiObservation.VisionLogExcerpt(read.OutputText, frame.CapturedUtc);
                    result.LocalEvidence = "[Output 하단 확대 판독 — LLM 전사본, 숫자 정확도 미검증]\r\n" +
                        "캡처 UTC " + frame.CapturedUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "\r\n" +
                        (read.OutputText ?? "[Output을 읽지 못했습니다]");
                    return Finish(result);
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellation.IsCancellationRequested) throw;
                return Finish(Failed("VISION_TIMEOUT", stage + "_TIMEOUT"));
            }
            catch (LocalVisionException ex)
            {
                modelInfo = ex.LocalModelInfo ?? modelInfo;
                bodyDiagnostics = ex.Detail ?? bodyDiagnostics; // Metadata-only B2 counters from the failed body search.
                var result = Failed(MapError(ex.Code), stage + "_" + ex.Code);
                if (ex.LocalResponse != null)
                    result.LocalEvidence = "[UNVALIDATED LM STUDIO RESPONSE — NOT USED AS SIMULATION STATUS]\r\n" +
                        "Local failure: " + result.LocalFailure + "\r\nSlave 내부 확인용. 원문은 로그/메신저로 보내지 않습니다.\r\n" + ex.LocalResponse;
                return Finish(result);
            }
            catch (InvalidDataException ex)
            {
                var code = ex.Message.StartsWith("SC_", StringComparison.Ordinal) ? ex.Message : "SC_INVALID";
                return Finish(Failed("VISION_CAPTURE_FAILED", code));
            }
            catch { return Finish(Failed("VISION_FAILED", stage + "_FAILED")); }
            finally { Reading.Release(); }
        }

        // Reuse the located body on the worker's clean pre-Ctrl+A frame. This performs the same fresh body
        // validation again but never calls the model or operates the target application.
        internal static PowerSiObservation ReframeOutput(PowerSiObservation located, PowerSiFrame cleanFrame)
        {
            if (located == null || cleanFrame == null || cleanFrame.Png == null || located.Code != "OUTPUT_UNAVAILABLE" ||
                located.LocalVisionMode != "LOCATE_ONLY" || located.LocalFailure != null || located.LocalVisibleEmpty || !located.CapturedUtc.HasValue ||
                located.LocalFrame == null || located.LocalFrameSize.IsEmpty || located.LocalOutputBody.IsEmpty ||
                located.LocalFullImage == null || located.LocalImage == null || located.LocalPaneImage == null ||
                !ReferenceEquals(located.LocalFullImage, located.LocalFrame.Png) ||
                located.LocalFrame.PixelSize != located.LocalFrameSize || cleanFrame.PixelSize != located.LocalFrameSize ||
                located.LocalFrame.CapturedUtc != located.CapturedUtc.Value || cleanFrame.CapturedUtc.Kind != DateTimeKind.Utc ||
                cleanFrame.CapturedUtc.Year < 2000)
                throw new InvalidDataException("SAVED_CROP_INVALID");

            var stored = located.LocalOutputBody;
            if (stored.X < 0 || stored.Y < 0 || stored.Width < 1 || stored.Height < 1 ||
                stored.Width > cleanFrame.PixelSize.Width || stored.Height > cleanFrame.PixelSize.Height ||
                stored.X > cleanFrame.PixelSize.Width - stored.Width || stored.Y > cleanFrame.PixelSize.Height - stored.Height)
                throw new InvalidDataException("SAVED_CROP_INVALID");
            var center = new Point(stored.X + stored.Width / 2, stored.Y + stored.Height / 2);
            BodySearchDiagnostics bodySearch;
            var body = OutputPaneImage.FindBodyAt(cleanFrame, center, CancellationToken.None, out bodySearch);
            if (body != stored) throw new InvalidDataException("SAVED_CROP_INVALID");

            var result = PowerSiObservation.VisionLogExcerpt(null, cleanFrame.CapturedUtc);
            result.LocalFrame = cleanFrame;
            result.LocalFullImage = cleanFrame.Png;
            result.LocalImage = OutputPaneImage.OcrInput(cleanFrame, body);
            result.LocalPaneImage = PowerSiScreenCapture.Crop(cleanFrame, body);
            result.LocalSuggestedImage = null; // The separately retained locator run owns its pre-copy suggestion image.
            result.LocalCaptureInfo = located.LocalCaptureInfo;
            result.LocalModelInfo = located.LocalModelInfo;
            result.LocalModel = located.LocalModel;
            result.LocalRegionInfo = located.LocalRegionInfo;
            result.LocalFrameSize = cleanFrame.PixelSize;
            result.LocalOutputBody = body;
            result.LocalBodyDiagnostics = bodySearch.Summary();
            result.LocalVisionMode = "LOCATE_ONLY";
            result.LocalRequestTimeoutSeconds = located.LocalRequestTimeoutSeconds;
            result.LocalElapsedMs = located.LocalElapsedMs;
            result.LocalLocateMs = located.LocalLocateMs;
            return result;
        }

        private static string Box(Rectangle rect)
        { return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}", rect.X, rect.Y, rect.Width, rect.Height); }

        private static PowerSiObservation Failed(string code, string detail)
        {
            var result = PowerSiObservation.VisionUnavailable(code);
            result.LocalFailure = detail;
            return result;
        }

        private static string MapError(string code)
        {
            switch (code)
            {
                case "SETTINGS_INVALID": case "DISABLED": return "VISION_NOT_CONFIGURED";
                case "REGION_BOUNDARY_UNCONFIRMED": return "OUTPUT_REGION_UNCONFIRMED";
                case "TIMEOUT": return "VISION_TIMEOUT";
                case "OUTPUT_UNREADABLE": return "OUTPUT_UNAVAILABLE";
                case "AUTH_REQUIRED": return "VISION_AUTH_REQUIRED";
                case "MODEL_SELECTION_REQUIRED": return "VISION_MODEL_AMBIGUOUS";
                case "MODEL_NOT_READY": case "NO_VISION_MODEL": return "VISION_MODEL_UNAVAILABLE";
                case "API_UNAVAILABLE": case "CONNECTION_FAILED": return "VISION_SERVER_UNAVAILABLE";
                default: return "VISION_INVALID_RESPONSE";
            }
        }
    }
}
