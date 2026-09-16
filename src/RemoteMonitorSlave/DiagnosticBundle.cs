using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace RemoteMonitorSlave
{
    // API: DiagnosticBundleRun holds one vision run's exported data (screens/text/results); every
    // API:   field is optional -- a null field means "not available for this run" and its entry
    // API:   (image/transcript/comparison) is skipped rather than written empty.
    // API: DiagnosticBundleContent is the whole export: buffer read metadata, the full Output text,
    // API:   an optional path to the current diagnostic log (copied verbatim if it exists and is
    // API:   readable; never written to the archive as a path -- only its content, and only a
    // API:   log_included flag reaches manifest.json), the run list, and optionally the capture the
    // API:   auto-copy worker took when it aborted (AutoCopyFramePng -> auto-copy-frame.png, with its
    // API:   code/detail in manifest.json as auto_copy_last_failure).
    // API: DiagnosticBundle.Write(target, content) builds one ZIP into `target`, an already-open,
    // API:   writable Stream the CALLER owns and disposes -- Write leaves it open (ZipArchiveMode
    // API:   .Create with leaveOpen:true) so a caller can inspect or reuse it afterwards. Entry
    // API:   order is fixed and content-independent: README.txt, manifest.json, full-text.txt,
    // API:   slave-log.txt, auto-copy-frame.png, then for each run in list order runs/NN-<sanitized label>/
    // API:   {full-frame,suggested,body,ocr-input}.png, transcript.txt, comparison.txt, run.json --
    // API:   so identical content always yields identical entry ordering. manifest.json and each
    // API:   run.json carry metadata only (labels/codes/diagnostics/SHA-256 of every image entry);
    // API:   transcript/comparison/full text live only in their own text entries, never duplicated
    // API:   into the JSON, matching the "metadata only in logs" project rule for anything that is
    // API:   not this explicit, user-triggered export.
    // API: DiagnosticBundle.DefaultFileName(utc, pid) proposes a save-dialog file name; nothing in
    // API:   this file writes to disk or triggers automatically -- phase 2 wires an explicit user
    // API:   action (a button) to picking a destination Stream/path and calling Write.
    // API: DiagnosticBundle.SelfTest() builds a synthetic bundle into a MemoryStream, reopens it
    // API:   read-only, and verifies entries/hashes/README wording; returns "PASS: ..." or throws
    // API:   InvalidOperationException. Nothing here registers it with Program.cs -- phase 2 calls
    // API:   DiagnosticBundle.SelfTest() from SlaveForm.SelfTest() (or Program's self-test chain).
    internal sealed class DiagnosticBundleRun
    {
        internal string Label;
        internal string ModelInfo;
        internal string Mode;
        internal string RegionInfo;
        internal string BodyDiagnostics;
        internal string Result;
        internal string FailureCode;
        internal byte[] FullFramePng;
        internal byte[] SuggestedPng;
        internal byte[] BodyPng;
        internal byte[] OcrInputPng;
        internal string Transcript;
        internal bool TranscriptValidated; // Response format only, never OCR correctness.
        internal string Comparison;
        internal string Metadata;
    }

    internal sealed class DiagnosticBundleContent
    {
        internal string Version;
        internal DateTime CreatedUtc;
        internal string BufferCode;
        internal string BufferMethod;
        internal string BufferDetail;
        internal DateTime? BufferReceivedUtc;
        internal string FullText;
        internal string LogFilePath;
        // The auto-copy worker's own capture of a failed attempt, plus its "<CODE> <detail>" summary.
        internal byte[] AutoCopyFramePng;
        internal string AutoCopyLastFailure;
        internal List<DiagnosticBundleRun> Runs = new List<DiagnosticBundleRun>();
        internal string Notes;
        internal int? TargetPid, TargetSessionId;
        internal long? TargetStartUtcTicks;
        internal List<DiagnosticBundleContent> Targets = new List<DiagnosticBundleContent>();
    }

    internal static class DiagnosticBundle
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        private const string ReadmeText =
            "PowerSI 진단 번들 - 발신 전 확인\r\n" +
            "====================================\r\n" +
            "이 압축 파일에는 화면 캡처 이미지와 Output 판독 원문 등 사내 고유 정보가 포함되어 있습니다.\r\n" +
            "Git 등 공개 저장소나 외부 공유 채널에 올리지 말고, 소유자가 필요하다고 판단한 담당자에게만 직접 전달하세요.\r\n" +
            "구성: manifest.json(메타데이터/해시), full-text.txt(Output 전체 텍스트),\r\n" +
            "slave-log.txt(진단 로그 사본, 있는 경우), auto-copy-frame.png(자동 복사 실패 시의 화면, 있는 경우),\r\n" +
            "runs\\NN-이름\\ (화면 이미지, 대조 결과, 실행별 상세).\r\n" +
            "ocr-input.png가 실제 문자 전사 입력이며 body.png 전체가 아니라 하단 최대 256픽셀을 사용합니다.\r\n" +
            "전사 요청은 OCR 입력의 모든 줄 대상입니다. 전체 줄·최신 줄 전사 여부는 자동 검증하지 않습니다.\r\n" +
            "'원문 대응 없음'은 원문에 대응하지 않는 전사 행 수이며, 읽지 않은 이미지 행 수가 아닙니다.\r\n" +
            "thinking OFF는 앱의 요청값입니다. 실제 적용 여부는 미확인이므로 LM Studio 설정도 확인하세요.\r\n" +
            "transcript_format_validated는 응답 형식 검사 결과이며 문자 정확도 검증이 아닙니다.\r\n";

        internal static void Write(Stream target, DiagnosticBundleContent content)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (content == null) throw new ArgumentNullException("content");
            if (content.Targets.Any(item => item == null || item.TargetPid.GetValueOrDefault() < 1 ||
                item.TargetSessionId.GetValueOrDefault(-1) < 0 || item.Targets.Count != 0) ||
                content.Targets.Select(item => item.TargetPid).Distinct().Count() != content.Targets.Count)
                throw new InvalidDataException("Invalid diagnostic target identities.");
            using (var archive = new ZipArchive(target, ZipArchiveMode.Create, true))
            {
                WriteContent(archive, content, "");
                for (int index = 0; index < content.Targets.Count; index++)
                    WriteContent(archive, content.Targets[index], TargetDirectory(index, content.Targets[index]));
            }
        }

        private static string TargetDirectory(int index, DiagnosticBundleContent target)
        { return "targets/" + (index + 1).ToString("D2", CultureInfo.InvariantCulture) + "-pid" + target.TargetPid.Value.ToString(CultureInfo.InvariantCulture) + "/"; }

        private static void WriteContent(ZipArchive archive, DiagnosticBundleContent content, string prefix)
        {
            var runs = content.Runs ?? new List<DiagnosticBundleRun>();

            byte[] logBytes = null;
            if (!string.IsNullOrEmpty(content.LogFilePath))
            {
                try { if (File.Exists(content.LogFilePath)) logBytes = File.ReadAllBytes(content.LogFilePath); }
                catch { logBytes = null; }
            }

            var manifestRuns = new List<object>();
            var runDirs = new string[runs.Count];
            var runJsonTexts = new string[runs.Count];

            for (var i = 0; i < runs.Count; i++)
            {
                var run = runs[i] ?? new DiagnosticBundleRun();
                var dir = prefix + "runs/" + (i + 1).ToString("D2", CultureInfo.InvariantCulture) + "-" + SanitizeLabel(run.Label) + "/";
                runDirs[i] = dir;

                var images = new Dictionary<string, object>();
                AddImageManifest(images, "full_frame", dir + "full-frame.png", run.FullFramePng);
                AddImageManifest(images, "suggested", dir + "suggested.png", run.SuggestedPng);
                AddImageManifest(images, "body", dir + "body.png", run.BodyPng);
                AddImageManifest(images, "ocr_input", dir + "ocr-input.png", run.OcrInputPng);

                var runJson = new Dictionary<string, object> {
                    { "index", i + 1 }, { "label", run.Label }, { "model_info", run.ModelInfo }, { "mode", run.Mode },
                    { "region_info", run.RegionInfo }, { "body_diagnostics", run.BodyDiagnostics }, { "result", run.Result },
                    { "failure_code", run.FailureCode }, { "transcript_format_validated", run.TranscriptValidated },
                    { "transcript_included", run.Transcript != null }, { "comparison_included", run.Comparison != null },
                    { "metadata", run.Metadata }, { "images", images } };
                runJsonTexts[i] = Serialize(runJson);

                manifestRuns.Add(new Dictionary<string, object> {
                    { "index", i + 1 }, { "entry_dir", dir }, { "label", run.Label }, { "model_info", run.ModelInfo },
                    { "mode", run.Mode }, { "region_info", run.RegionInfo }, { "body_diagnostics", run.BodyDiagnostics },
                    { "result", run.Result }, { "failure_code", run.FailureCode },
                    { "transcript_format_validated", run.TranscriptValidated }, { "transcript_included", run.Transcript != null },
                    { "comparison_included", run.Comparison != null }, { "metadata", run.Metadata }, { "images", images } });
            }

            var autoCopyImages = new Dictionary<string, object>();
            AddImageManifest(autoCopyImages, "auto_copy_frame", prefix + "auto-copy-frame.png", content.AutoCopyFramePng);

            var manifest = new Dictionary<string, object> {
                { "version", content.Version }, { "created_utc", content.CreatedUtc.ToString("o", CultureInfo.InvariantCulture) },
                { "target_pid", content.TargetPid }, { "target_start_utc_ticks", content.TargetStartUtcTicks }, { "target_session_id", content.TargetSessionId },
                { "targets", content.Targets.Select((item, index) => new { pid = item.TargetPid, start_utc_ticks = item.TargetStartUtcTicks,
                    session_id = item.TargetSessionId, entry_dir = TargetDirectory(index, item), result = item.BufferCode }).ToArray() },
                { "buffer_code", content.BufferCode }, { "buffer_method", content.BufferMethod }, { "buffer_detail", content.BufferDetail },
                { "buffer_received_utc", content.BufferReceivedUtc?.ToString("o", CultureInfo.InvariantCulture) },
                { "full_text_included", content.FullText != null }, { "log_included", logBytes != null },
                { "auto_copy_last_failure", content.AutoCopyLastFailure }, { "auto_copy_images", autoCopyImages },
                { "notes", content.Notes }, { "runs", manifestRuns } };
            var manifestText = Serialize(manifest);

            {
                if (prefix.Length == 0) WriteTextEntry(archive, "README.txt", ReadmeText + "여러 PowerSI 결과는 targets/NN-pidPID/ 아래 서로 분리되어 있습니다.\r\n", false);
                WriteTextEntry(archive, prefix + "manifest.json", manifestText, false);
                if (content.FullText != null) WriteTextEntry(archive, prefix + "full-text.txt", content.FullText, true);
                if (logBytes != null) WriteBinaryEntry(archive, prefix + "slave-log.txt", logBytes);
                if (content.AutoCopyFramePng != null) WriteBinaryEntry(archive, prefix + "auto-copy-frame.png", content.AutoCopyFramePng);
                for (var i = 0; i < runs.Count; i++)
                {
                    var run = runs[i] ?? new DiagnosticBundleRun();
                    var dir = runDirs[i];
                    if (run.FullFramePng != null) WriteBinaryEntry(archive, dir + "full-frame.png", run.FullFramePng);
                    if (run.SuggestedPng != null) WriteBinaryEntry(archive, dir + "suggested.png", run.SuggestedPng);
                    if (run.BodyPng != null) WriteBinaryEntry(archive, dir + "body.png", run.BodyPng);
                    if (run.OcrInputPng != null) WriteBinaryEntry(archive, dir + "ocr-input.png", run.OcrInputPng);
                    if (run.Transcript != null) WriteTextEntry(archive, dir + "transcript.txt", run.Transcript, false);
                    if (run.Comparison != null) WriteTextEntry(archive, dir + "comparison.txt", run.Comparison, false);
                    WriteTextEntry(archive, dir + "run.json", runJsonTexts[i], false);
                }
            }
        }

        internal static string DefaultFileName(DateTime utc, int pid)
        {
            return "remote-monitor-diag-" + utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                "-pid" + pid.ToString(CultureInfo.InvariantCulture) + ".zip";
        }

        internal static string SelfTest()
        {
            var content = new DiagnosticBundleContent
            {
                Version = "0.1.50-selftest",
                CreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                BufferCode = "BUFFER_READ",
                BufferMethod = "NATIVE_WM_GETTEXT",
                BufferDetail = "NATIVE=1,UIA=0",
                FullText = "완료\r\n끝",
                Notes = "self-test synthetic bundle",
                LogFilePath = null,
                AutoCopyFramePng = new byte[] { 0xA, 0xB, 0xC, 0xD },
                AutoCopyLastFailure = "AUTO_COPY_OCCLUDED OCCLUDER|SELF"
            };
            content.Runs.Add(new DiagnosticBundleRun
            {
                Label = "31B Q4",
                ModelInfo = "qwen2-vl:31b-q4",
                Mode = "CROP_OUTPUT",
                RegionInfo = "P2|1|1424|378|479|493",
                BodyDiagnostics = "B2|1920|1080|3|1|0|1|0|0|1",
                Result = "CROP_OUTPUT_REGION_BOUNDARY_UNCONFIRMED",
                FailureCode = "OUTPUT_REGION_UNCONFIRMED",
                FullFramePng = new byte[] { 0x1, 0x2, 0x3, 0x4, 0x5 },
                SuggestedPng = new byte[] { 0x6, 0x7 },
                Transcript = "AFS Current Frequency (MHz) = 860.000",
                TranscriptValidated = false,
                Comparison = "T1|1|0|0|0|1|0|false",
                Metadata = "VISION_RUN mode=CROP_OUTPUT model=31B_Q4 result=CROP_OUTPUT_REGION_BOUNDARY_UNCONFIRMED"
            });
            content.Runs.Add(new DiagnosticBundleRun
            {
                Label = "E4B",
                Result = "OK",
                TranscriptValidated = true,
                FullFramePng = new byte[] { 0x9, 0x9, 0x9 }
            });

            byte[] zipBytes;
            using (var stream = new MemoryStream())
            {
                Write(stream, content);
                zipBytes = stream.ToArray();
            }

            using (var reopen = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(reopen, ZipArchiveMode.Read))
            {
                var names = archive.Entries.Select(e => e.FullName).ToList();
                if (!names.Contains("README.txt") || !names.Contains("manifest.json") || !names.Contains("full-text.txt"))
                    throw new InvalidOperationException("Diagnostic bundle missing top-level entries.");
                if (names.Contains("slave-log.txt"))
                    throw new InvalidOperationException("Diagnostic bundle wrote a log entry with no log path given.");

                var autoCopyHash = ComputeHashHex(content.AutoCopyFramePng);
                if (!names.Contains("auto-copy-frame.png") ||
                    !ReadEntryBytes(archive, "auto-copy-frame.png").SequenceEqual(content.AutoCopyFramePng))
                    throw new InvalidOperationException("Diagnostic bundle lost the failed auto-copy capture.");

                var readme = ReadEntryText(archive, "README.txt");
                if (!readme.Contains("공개 저장소") || !readme.Contains("고유 정보"))
                    throw new InvalidOperationException("Diagnostic bundle README is missing the Korean sharing warning.");
                if (!readme.Contains("ocr-input.png가 실제 문자 전사 입력") || !readme.Contains("전체 줄·최신 줄 전사 여부는 자동 검증하지 않습니다") ||
                    !readme.Contains("'원문 대응 없음'") || !readme.Contains("thinking OFF는 앱의 요청값"))
                    throw new InvalidOperationException("Diagnostic bundle lost the OCR input, completeness or thinking-status distinction.");

                const string run1Dir = "runs/01-31B_Q4/";
                const string run2Dir = "runs/02-E4B/";
                if (!names.Contains(run1Dir + "full-frame.png") || !names.Contains(run1Dir + "suggested.png") ||
                    names.Contains(run1Dir + "body.png") || names.Contains(run1Dir + "ocr-input.png") ||
                    !names.Contains(run1Dir + "transcript.txt") || !names.Contains(run1Dir + "comparison.txt") ||
                    !names.Contains(run1Dir + "run.json"))
                    throw new InvalidOperationException("Diagnostic bundle run 1 entries do not match its content.");
                if (!names.Contains(run2Dir + "full-frame.png") || !names.Contains(run2Dir + "run.json") ||
                    names.Contains(run2Dir + "suggested.png") || names.Contains(run2Dir + "transcript.txt") ||
                    names.Contains(run2Dir + "comparison.txt"))
                    throw new InvalidOperationException("Diagnostic bundle run 2 wrote entries for null fields.");

                var expectedFrameHash = ComputeHashHex(content.Runs[0].FullFramePng);
                var manifestJson = ReadEntryText(archive, "manifest.json");
                if (!manifestJson.Contains("transcript_format_validated") || manifestJson.Contains("\"transcript_validated\""))
                    throw new InvalidOperationException("Format validation was mislabeled as transcript accuracy.");
                if (!manifestJson.Contains(expectedFrameHash) || manifestJson.Contains(content.Runs[0].Transcript))
                    throw new InvalidOperationException("Diagnostic bundle manifest hash/metadata-only content is wrong.");
                if (!manifestJson.Contains(autoCopyHash) || !manifestJson.Contains("auto_copy_last_failure") ||
                    !manifestJson.Contains("AUTO_COPY_OCCLUDED"))
                    throw new InvalidOperationException("Diagnostic bundle manifest lost the auto-copy failure metadata.");

                var actualFrameBytes = ReadEntryBytes(archive, run1Dir + "full-frame.png");
                if (!actualFrameBytes.SequenceEqual(content.Runs[0].FullFramePng) || ComputeHashHex(actualFrameBytes) != expectedFrameHash)
                    throw new InvalidOperationException("Diagnostic bundle stored image bytes do not round-trip.");

                var fullText = ReadEntryText(archive, "full-text.txt");
                if (fullText != content.FullText)
                    throw new InvalidOperationException("Diagnostic bundle full-text.txt round-trip failed.");

                var run1Json = ReadEntryText(archive, run1Dir + "run.json");
                if (!run1Json.Contains("OUTPUT_REGION_UNCONFIRMED") || run1Json.Contains(content.Runs[0].Transcript))
                    throw new InvalidOperationException("Diagnostic bundle run.json is missing metadata or leaking transcript text.");

                var defaultName = DefaultFileName(content.CreatedUtc, 4242);
                if (defaultName != "remote-monitor-diag-20260102-030405-pid4242.zip")
                    throw new InvalidOperationException("Diagnostic bundle default file name format changed.");

                return "PASS: diagnostic bundle round-trip, " + names.Count.ToString(CultureInfo.InvariantCulture) + " entries";
            }
        }

        private static void AddImageManifest(Dictionary<string, object> images, string key, string entryName, byte[] data)
        {
            if (data == null) return;
            images[key] = new Dictionary<string, object> { { "entry", entryName }, { "sha256", ComputeHashHex(data) } };
        }

        private static string SanitizeLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "run";
            var builder = new StringBuilder(label.Length);
            foreach (var ch in label.Trim())
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') builder.Append(ch);
                else if (char.IsWhiteSpace(ch)) builder.Append('_');
                // Any other character (path separators, quotes, control chars, punctuation) is
                // dropped -- the human-readable label still appears in manifest.json/run.json.
            }
            var sanitized = builder.ToString();
            if (sanitized.Length > 40) sanitized = sanitized.Substring(0, 40);
            sanitized = sanitized.Trim('_');
            return sanitized.Length == 0 ? "run" : sanitized;
        }

        private static string Serialize(object value)
        {
            return new JavaScriptSerializer().Serialize(value);
        }

        private static void WriteTextEntry(ZipArchive archive, string name, string text, bool bom)
        {
            var body = Utf8NoBom.GetBytes(text ?? "");
            byte[] bytes;
            if (bom)
            {
                bytes = new byte[Utf8Bom.Length + body.Length];
                Buffer.BlockCopy(Utf8Bom, 0, bytes, 0, Utf8Bom.Length);
                Buffer.BlockCopy(body, 0, bytes, Utf8Bom.Length, body.Length);
            }
            else bytes = body;
            WriteBinaryEntry(archive, name, bytes);
        }

        private static void WriteBinaryEntry(ZipArchive archive, string name, byte[] data)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (var stream = entry.Open()) stream.Write(data, 0, data.Length);
        }

        private static string ReadEntryText(ZipArchive archive, string name)
        {
            var entry = archive.GetEntry(name);
            if (entry == null) throw new InvalidOperationException("Diagnostic bundle missing entry: " + name);
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true)) return reader.ReadToEnd();
        }

        private static byte[] ReadEntryBytes(ZipArchive archive, string name)
        {
            var entry = archive.GetEntry(name);
            if (entry == null) throw new InvalidOperationException("Diagnostic bundle missing entry: " + name);
            using (var stream = entry.Open())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private static string ComputeHashHex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(data);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }
    }
}
