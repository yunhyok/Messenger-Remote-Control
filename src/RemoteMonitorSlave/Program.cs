using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using RemoteMonitorLink;

namespace RemoteMonitorSlave
{
    internal static class Program
    {
        internal const string Title = "Messenger Remote Control Slave v" + LinkVersion.AppValue;

        [STAThread]
        private static void Main(string[] args)
        {
            if (OutputBufferCapture.TryRunWorker(args)) return;
            if (PowerSiScreenCapture.TryRunWorker(args)) return;
            if (PowerSiObservation.TryRunWorker(args)) return;
            if (args.Length == 1 && args[0] == "--self-test")
            {
                try
                {
                    LinkSelfTest.Run(); PowerSiOutputBuffer.SelfTest(); OutputBufferCapture.SelfTest();
                    Console.WriteLine(DiagnosticBundle.SelfTest());
                    SlaveForm.SelfTest();
                    Console.WriteLine("PASS: Slave status link and UI checks");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                return;
            }
            bool owns;
            using (var mutex = new Mutex(true, @"Local\RemoteMonitorSlave", out owns))
            {
                if (!owns) { MessageBox.Show("Slave가 이미 실행 중입니다.", Title); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // Without this, a UI-thread exception opens the WinForms error dialog and keeps running instead of
                // reaching the catch below, so a broken Slave would look like a working one.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                var shown = false;
                try
                {
                    var form = new SlaveForm();
                    form.Shown += delegate { shown = true; };
                    Application.Run(form);
                }
                catch (Exception ex)
                {
                    MessageBox.Show((shown ? "Slave가 안전하게 중단되었습니다: " : "Slave 시작 실패: ") +
                        ex.GetType().Name + "\r\n로그 폴더의 최신 로그를 확인하세요.", Title);
                }
            }
        }
    }

    internal sealed class SlaveLog
    {
        internal readonly string Path;
        private readonly object gate = new object();
        internal SlaveLog(string directory)
        {
            Directory.CreateDirectory(directory);
            int pid;
            using (var current = System.Diagnostics.Process.GetCurrentProcess()) pid = current.Id;
            // A non-Gregorian user calendar would otherwise rename every log file (and break their ordering).
            Path = System.IO.Path.Combine(directory, "remote-monitor-slave-" +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) +
                "-pid" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".log");
            Write("APP_START");
        }
        internal void Write(string code)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"\A[A-Z0-9_]{1,64}\z")) code = "LINK_EVENT";
            lock (gate)
                File.AppendAllText(Path, DateTime.UtcNow.ToString("O") + " version=" + LinkVersion.AppValue + " code=" + code +
                    Environment.NewLine, new UTF8Encoding(false)); // Each event releases the stream immediately.
        }

        // Fixed code plus bounded key=value metadata (identifiers, integers, hashes, B1/B2/T1/A1/A2 fields).
        // Screen text, transcripts, file paths and tokens never pass this filter: anything else becomes INVALID.
        internal void Write(string code, string detail)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"\A[A-Z0-9_]{1,64}\z")) code = "LINK_EVENT";
            if (detail == null || !System.Text.RegularExpressions.Regex.IsMatch(detail, @"\A[A-Za-z0-9_=| \-]{1,512}\z"))
                detail = "detail=INVALID";
            lock (gate)
                File.AppendAllText(Path, DateTime.UtcNow.ToString("O") + " version=" + LinkVersion.AppValue + " code=" + code +
                    " " + detail + Environment.NewLine, new UTF8Encoding(false));
        }

        internal void WritePowerSi(PowerSiObservation observation)
        {
            var wire = PowerSiObservation.Parse(observation.Serialize()).Serialize();
            var stage = observation.ProbeStage;
            var prefix = DateTime.UtcNow.ToString("O") + " version=" + LinkVersion.AppValue;
            var entry = new StringBuilder(prefix + " code=PWRSI_OBSERVATION data=" + wire + " stage=" + stage + Environment.NewLine);
            if (observation.LocalSampleId != null)
                entry.Append(prefix).Append(" code=VISION_RUN").Append(ComparisonMetadata(observation)).Append(Environment.NewLine);
            if (observation.LocalFailure != null && System.Text.RegularExpressions.Regex.IsMatch(observation.LocalFailure, @"\A[A-Z0-9_]{1,64}\z"))
                entry.Append(prefix).Append(" code=PWRSI_LOCAL_FAILURE data=").Append(observation.LocalFailure).Append(Environment.NewLine);
            foreach (var detail in observation.ProbeDetails)
                entry.Append(prefix).Append(" code=PWRSI_PROBE data=").Append(detail).Append(Environment.NewLine);
            lock (gate)
                File.AppendAllText(Path, entry.ToString(), new UTF8Encoding(false));
        }

        // Also used by the diagnostic bundle so an exported run carries the same VISION_RUN metadata as the log.
        internal static string ComparisonMetadata(PowerSiObservation result)
        {
            string Id(string value) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[A-F0-9]{64}\z") ? value : "UNKNOWN";
            string Label(string value) => string.IsNullOrWhiteSpace(value) || value.Length > 256 ? "UNKNOWN" : Uri.EscapeDataString(value);
            var model = result.LocalModelInfo;
            string geometry = result.LocalRegionInfo != null && System.Text.RegularExpressions.Regex.IsMatch(result.LocalRegionInfo, @"\AP2(\|[0-9]{1,5}){4}((\|[0-9]{1,5}){6})?\z")
                ? result.LocalRegionInfo : "UNKNOWN";
            string frame = result.LocalFrameSize.Width > 0 && result.LocalFrameSize.Height > 0 &&
                result.LocalFrameSize.Width <= 99999 && result.LocalFrameSize.Height <= 99999
                ? result.LocalFrameSize.Width.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" +
                    result.LocalFrameSize.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "UNKNOWN";
            string body = result.LocalBodyDiagnostics != null &&
                System.Text.RegularExpressions.Regex.IsMatch(result.LocalBodyDiagnostics, @"\AB2(\|[0-9]{1,7}){9}\z")
                ? result.LocalBodyDiagnostics : "UNKNOWN";
            return " mode=" + (result.LocalVisionMode == "OCR_ONLY" ? "OCR_ONLY crop_reused=1" :
                result.LocalVisionMode == "LOCATE_ONLY" ? "LOCATE_ONLY crop_reused=0" : "LOCATE_OCR crop_reused=0") +
                " request_timeout_s=" + Math.Max(0, Math.Min(90, result.LocalRequestTimeoutSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " visible_empty=" + (result.LocalVisibleEmpty ? "1" : "0") +
                " transcript_policy=" + LocalVisionClient.TranscriptPolicy +
                " thinking_requested=off thinking_effective=UNKNOWN ocr_max_tokens=" + LocalVisionClient.ReadMaxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " sample=" + Id(result.LocalSampleId) + " ocr_sample=" + Id(result.LocalOcrSampleId) +
                " model_id=" + Label(model?.Id) + " model_name=" + Label(model?.DisplayName) + " model_key=" + Label(model?.Key) +
                " quantization=" + Label(model?.Quantization) + " total_ms=" + Math.Max(0, result.LocalElapsedMs).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " locate_ms=" + Math.Max(0, result.LocalLocateMs).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " read_ms=" + Math.Max(0, result.LocalReadMs).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " geometry=" + geometry + " frame=" + frame + " body=" + body;
        }

        internal void WriteOutputBuffer(OutputBufferResult result)
        {
            var metadata = OutputBufferCapture.LogMetadata(result); // Raw buffer is never part of log metadata.
            lock (gate)
                File.AppendAllText(Path, DateTime.UtcNow.ToString("O") + " version=" + LinkVersion.AppValue +
                    " code=OUTPUT_BUFFER" + metadata + Environment.NewLine, new UTF8Encoding(false));
        }
    }
}
