using System;
using System.Threading;
using System.Windows.Forms;

namespace RemoteMonitorMaster
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (RemoteMonitorLink.PowerSiScreenCapture.TryRunWorker(args)) return;
            if (RemoteMonitorLink.PowerSiObservation.TryRunWorker(args)) return;
            if (args.Length == 1 && string.Equals(args[0], "--self-test", StringComparison.Ordinal))
            {
                Environment.ExitCode = CoreSelfTest.Run();
                return;
            }

            bool ownsInstance;
            using (var instance = new Mutex(true, AppInfo.InstanceMutexName, out ownsInstance))
            {
                if (!ownsInstance)
                {
                    MessageBox.Show(AppInfo.Title + " is already running in this Windows session.",
                        AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                AuditLog log;
                try
                {
                    log = new AuditLog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        AppInfo.Title + " stopped safely.\r\n\r\nReason: " + ex.GetType().Name +
                        "\r\nLog unavailable.",
                        AppInfo.Title,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                using (log)
                {
                    try
                    {
                        log.Write("INFO", "APP_START",
                            AuditLog.Field("read_only", false),
                            AuditLog.Field("supervised_send_test", false),
                            AuditLog.Field("supervised_roundtrip", true),
                            AuditLog.Field("operating_status_available", true),
                            AuditLog.Field("plain_readonly_commands_available", true),
                            AuditLog.Field("pc_status", true),
                            AuditLog.Field("pc_status_read_only", true),
                            AuditLog.Field("slave_client_available", true),
                            AuditLog.Field("per_request_replies", "ONE_OR_BOUNDED_REPORT_PARTS"),
                            AuditLog.Field("repeated_requests_require_local_approval", true),
                            AuditLog.Field("receive_diagnostic", true),
                            AuditLog.Field("send_method", "TIMED_MOUSE_CLICK"),
                            AuditLog.Field("entry_method", "UIA_SETVALUE"),
                            AuditLog.Field("automatic_send_allowed", false),
                            AuditLog.Field("os", Environment.OSVersion.VersionString),
                            AuditLog.Field("clr", Environment.Version),
                            AuditLog.Field("framework_release", SystemInfo.FrameworkRelease),
                            AuditLog.Field("process_64bit", Environment.Is64BitProcess),
                            AuditLog.Field("os_64bit", Environment.Is64BitOperatingSystem));
                        Application.Run(new MasterHubForm(log));
                        // An attachment reader may deny writes after the completed log was released.
                        try { log.Write("INFO", "APP_EXIT"); }
                        catch (System.IO.IOException) { } // Optional shutdown entry; do not report a false application failure.
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            log.WriteException("APP_FATAL", ex);
                        }
                        catch
                        {
                            // A logging failure must not hide the safe-stop dialog.
                        }

                        MessageBox.Show(
                            AppInfo.Title + " stopped safely.\r\n\r\nReason: " + ex.GetType().Name +
                            "\r\nLog: " + log.FilePath,
                            AppInfo.Title,
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
            }
        }
    }
}
