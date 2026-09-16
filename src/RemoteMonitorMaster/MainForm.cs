using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteMonitorMaster
{
    internal sealed class MainForm : Form
    {
        private static readonly string marker = "D" + Protocol.CreateDiagnosticDigits();
        private readonly AuditLog log;
        private readonly Timer probeTimer = new Timer();
        private readonly Label stateLabel = new Label();
        private readonly TextBox codeBox = new TextBox();
        private readonly Button testButton = new Button();
        private readonly CheckBox confirmSendCheckBox = new CheckBox();
        private readonly Button stopButton = new Button();
        private readonly TextBox detailsBox = new TextBox();
        private SupervisedSendTest.Consent activeConsent;
        private volatile bool stopRequested = true;
        private bool busy, closeRequested, testStarted, auditFailed, messageMayHaveBeenSent, inputMayHaveChanged;
        private int generation;

        public MainForm(AuditLog log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            Text = AppInfo.Title;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ClientSize = new Size(860, 620);
            MinimumSize = Size;
            BuildUi();
            probeTimer.Interval = 5000;
            probeTimer.Tick += ProbeTimerTick;
            FormClosing += MainFormClosing;
            try
            {
                log.Write("INFO", "SUPERVISED_TEST_READY", AuditLog.Field("maximum_send_attempts", 1),
                    AuditLog.Field("send_method", "TIMED_MOUSE_CLICK"), AuditLog.Field("entry_method", "UIA_SETVALUE"));
                SetStatus("READY - NO MANUAL HOVER / ONE SEND MAX");
            }
            catch (Exception ex)
            {
                auditFailed = true;
                FailProbe("AUDIT_UNAVAILABLE", ex);
            }
            UpdateButtons();
        }

        private void BuildUi()
        {
            Controls.Add(new Label
            {
                Text = AppInfo.Title + " - NO MANUAL HOVER / ONE SEND MAX",
                AutoSize = true, Font = new Font(Font, FontStyle.Bold), Location = new Point(18, 16)
            });
            stateLabel.SetBounds(18, 47, 824, 25);
            stateLabel.Font = new Font(Font, FontStyle.Bold);
            Controls.Add(stateLabel);
            Controls.Add(new Label
            {
                Text = "Save any important draft elsewhere, then leave your separate self-chat input completely EMPTY. Do not type or paste a code.\r\n" +
                    "Confirm below and click Test once. Within 5 seconds, activate the chat by its title bar. Leave the pointer there.\r\n" +
                    "The program finds Send, moves the pointer, enters this code and clicks once. Then keep the computer untouched.",
                Location = new Point(18, 78), Size = new Size(824, 60)
            });
            codeBox.SetBounds(18, 146, 174, 44);
            codeBox.Text = marker;
            codeBox.ReadOnly = true;
            codeBox.Font = new Font("Consolas", 22F, FontStyle.Bold, GraphicsUnit.Point);
            codeBox.TextAlign = HorizontalAlignment.Center;
            codeBox.AccessibleName = "This session's test code, D followed by six digits, no spaces";
            Controls.Add(new Label
            {
                Text = "This code will be entered automatically into the EMPTY input.\r\nNo copying, pasting or typing is needed; the clipboard is not changed.",
                Location = new Point(205, 148), Size = new Size(637, 44)
            });
            confirmSendCheckBox.Text =
                "My separate self-chat input is EMPTY. I authorize moving the pointer to its automatically located Send icon once.\r\n" +
                "I authorize automatic code entry once and one actual mouse click (120 ms press), sending at most ONE real message.";
            confirmSendCheckBox.SetBounds(18, 204, 824, 48);
            confirmSendCheckBox.CheckedChanged += delegate { UpdateButtons(); };
            testButton.Text = "Test once (5s)";
            testButton.SetBounds(18, 266, 220, 36);
            testButton.AccessibleDescription =
                "After five seconds, locate Send, move the pointer once, enter the code and click once. No forced focus, clear or retry.";
            testButton.Click += delegate { BeginProbe(); };
            stopButton.Text = "Stop";
            stopButton.SetBounds(737, 266, 105, 36);
            stopButton.Click += delegate { StopProbe("STOP_REQUESTED"); };
            Controls.Add(new Label { Text = "Automatic entry + one click result (delivery is not automatically verified):", AutoSize = true, Location = new Point(18, 314) });
            detailsBox.SetBounds(18, 337, 824, 162);
            detailsBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            detailsBox.Multiline = true;
            detailsBox.ReadOnly = true;
            detailsBox.ScrollBars = ScrollBars.Vertical;
            detailsBox.Text =
                "1. Save any important draft elsewhere, then leave your separate self-chat input EMPTY. No manual code entry is needed.\r\n" +
                "2. Confirm above and click Test once. Activate the chat by its title bar within 5 seconds. No Send hover is needed.\r\n" +
                "3. Leave the computer untouched. After the result, only check whether a new code bubble appeared or the draft remained.\r\n" +
                "Starting, cancelling or rejecting the test permanently locks this session. No clipboard changes, clearing or retries.";
            Controls.Add(new Label
            {
                Text = "Log file:", AutoSize = true, Location = new Point(18, 512), Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            });
            var logPathBox = new TextBox
            {
                Text = log.FilePath, ReadOnly = true, Multiline = true, WordWrap = false,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            logPathBox.SetBounds(18, 534, 679, 24);
            var openLogButton = new Button { Text = "Open Log Folder", Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            openLogButton.SetBounds(707, 529, 135, 32);
            openLogButton.Click += OpenLogFolder;
            Controls.Add(new Label
            {
                Text = "Privacy: the test code and UI text are not logged. Process paths and control metadata may remain.",
                AutoSize = true, Location = new Point(18, 586), Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            });
            Controls.AddRange(new Control[]
                { codeBox, testButton, confirmSendCheckBox, stopButton, detailsBox, logPathBox, openLogButton });
        }

        private void BeginProbe()
        {
            if (testStarted || auditFailed || busy || probeTimer.Enabled || closeRequested || !confirmSendCheckBox.Checked) return;
            testStarted = true; // A started countdown consumes this app session, including cancellation or rejection.
            generation++;
            stopRequested = false;
            confirmSendCheckBox.Checked = false;
            try
            {
                activeConsent = new SupervisedSendTest.Consent(marker, true);
                log.Write("INFO", "SUPERVISED_SEND_COUNTDOWN", AuditLog.Field("maximum_send_attempts", 1));
                detailsBox.Text = "Within 5 seconds, activate your separate self-chat by its title bar. Leave the pointer on the title bar.\r\n" +
                    "Do not click Send, type, paste or press Enter. Release all keys/buttons and leave the computer untouched.\r\n" +
                    "The program locates Send and moves the pointer itself, then enters the code and may send it once; no retry.";
                SetStatus("5 SECONDS - activate separate self-chat; no manual Send hover needed");
                probeTimer.Start();
                UpdateButtons();
            }
            catch (Exception ex) { FailProbe("SEND_TEST_START_FAILED", ex); }
        }

        private async void ProbeTimerTick(object sender, EventArgs e)
        {
            if (!probeTimer.Enabled || !testStarted || activeConsent == null || busy || stopRequested || closeRequested)
            { probeTimer.Stop(); return; }
            probeTimer.Stop();
            var currentGeneration = generation;
            var consent = activeConsent;
            busy = true;
            UpdateButtons();
            try
            {
                var window = NativeMethods.GetForegroundWindow();
                if (window == IntPtr.Zero || window == Handle)
                    throw new MonitorException("SEND_TEST_NO_TARGET", "Select the separate self-chat during the countdown.");
                NativeMethods.ScreenPoint position;
                if (!NativeMethods.GetPhysicalCursorPos(out position))
                    throw new MonitorException("SEND_TEST_POINTER_UNAVAILABLE", "The physical pointer could not be captured.");
                var pointer = new System.Windows.Point(position.X, position.Y);
                log.Write("INFO", "SUPERVISED_SEND_TARGET_CAPTURED",
                    AuditLog.Field("hwnd", "0x" + window.ToInt64().ToString("X")), AuditLog.Field("physical_pointer_captured", true));
                SetStatus("TEST RUNNING - leave the KI chat, draft and computer untouched");
                var result = await Task.Run(() => SupervisedSendTest.Run(window, log, marker, pointer, consent, () => stopRequested));
                messageMayHaveBeenSent |= consent.Attempted;
                inputMayHaveChanged |= consent.WriteAttempted;
                ShowResult(result, currentGeneration);
            }
            catch (Exception ex)
            {
                messageMayHaveBeenSent |= consent.Attempted;
                inputMayHaveChanged |= consent.WriteAttempted;
                if (generation == currentGeneration)
                    FailProbe((ex as MonitorException)?.ReasonCode ?? "SEND_TEST_FAILED", ex);
            }
            finally
            {
                consent.Cancel();
                messageMayHaveBeenSent |= consent.Attempted;
                inputMayHaveChanged |= consent.WriteAttempted;
                stopRequested = true;
                busy = false;
                confirmSendCheckBox.Checked = false;
                UpdateButtons();
                if (closeRequested) BeginInvoke(new Action(Close));
            }
        }

        private void ShowResult(string result, int currentGeneration)
        {
            // This fixed warning is generated by our backend, never by provider text. Keep it visible even after Stop/Close.
            if (result.Contains(SupervisedSendTest.MouseReleaseWarning) || (inputMayHaveChanged && !messageMayHaveBeenSent)) closeRequested = false;
            detailsBox.Text = result + Environment.NewLine + OutcomeAdvice();
            SetStatus((generation != currentGeneration || stopRequested ? "STOPPED / RESULT READY" : "RESULT READY") +
                " - session locked; inspect the visible result and collect the log");
        }

        private string OutcomeAdvice()
        {
            return messageMayHaveBeenSent
                ? "One message may have been sent or may still be pending.\r\n" +
                    "Leave the KI chat and draft untouched. Do not retry.\r\n" +
                    "Only check whether one new code bubble appeared or the code remained in the input; report what you saw."
                : inputMayHaveChanged
                    ? "No mouse click was attempted, but code entry may have completed or may still be pending.\r\n" +
                        "Leave the KI input untouched. Do not send, clear or retry; collect the log."
                    : "No text entry or Send click was attempted. Cursor positioning may have occurred; it is not restored.\r\n" +
                        "Collect the log. A started test cannot be repeated in this app session.";
        }

        private void StopProbe(string reason)
        {
            stopRequested = true;
            generation++;
            probeTimer.Stop();
            activeConsent?.Cancel();
            messageMayHaveBeenSent |= activeConsent?.Attempted == true;
            inputMayHaveChanged |= activeConsent?.WriteAttempted == true;
            confirmSendCheckBox.Checked = false;
            SetStatus(reason + (busy ? " - waiting for the current accessibility call; it cannot be aborted" : " - stopped"));
            detailsBox.Text = (busy ? "Waiting for the current call to return. Stop cannot undo an in-flight write or Send.\r\n" : "") + OutcomeAdvice();
            try { log.Write("INFO", "SUPERVISED_SEND_STOP", AuditLog.Field("reason", reason), AuditLog.Field("attempted", messageMayHaveBeenSent),
                AuditLog.Field("write_attempted", inputMayHaveChanged)); }
            catch { }
            UpdateButtons();
        }

        private void FailProbe(string reason, Exception exception)
        {
            StopProbe(reason);
            // Provider exception messages can contain private drafts; never display or log their text.
            detailsBox.Text = reason + "\r\n" + detailsBox.Text + "\r\nCollect the log using Open Log Folder.\r\n" + log.FilePath;
            try
            {
                if (exception == null) log.Write("ERROR", "SUPERVISED_SEND_FAILED", AuditLog.Field("reason", reason));
                else log.WriteException("SUPERVISED_SEND_FAILED", exception, AuditLog.Field("reason", reason));
            }
            catch { }
        }

        private void SetStatus(string message) { stateLabel.Text = "State: " + message; }

        private void UpdateButtons()
        {
            var ready = !testStarted && !auditFailed && !busy && !probeTimer.Enabled && !closeRequested;
            confirmSendCheckBox.Enabled = ready;
            testButton.Enabled = ready && confirmSendCheckBox.Checked;
            stopButton.Enabled = busy || probeTimer.Enabled;
        }

        private void MainFormClosing(object sender, FormClosingEventArgs e)
        {
            closeRequested = true;
            StopProbe("APP_CLOSING");
            if (busy) e.Cancel = true; // Keep the audit sink alive until an in-flight call returns; never abort it.
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stopRequested = true;
                activeConsent?.Cancel();
                probeTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void OpenLogFolder(object sender, EventArgs e)
        {
            try { Process.Start("explorer.exe", log.FolderPath); log.Write("INFO", "LOG_FOLDER_OPENED"); }
            catch (Exception ex)
            {
                try { log.WriteException("LOG_FOLDER_OPEN_FAILED", ex); } catch { }
                MessageBox.Show("Could not open the log folder.\r\n" + log.FolderPath, AppInfo.Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
