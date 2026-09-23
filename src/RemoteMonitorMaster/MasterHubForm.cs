using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using RemoteMonitorLink;

namespace RemoteMonitorMaster
{
    internal sealed class MasterHubForm : Form
    {
        private readonly AuditLog log;
        private readonly TextBox pairing = new TextBox { UseSystemPasswordChar = true };
        private readonly Label target = new Label();
        private readonly TextBox result = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, TabStop = false };
        private readonly Button import = new Button { Text = "연결파일 열기" };
        private readonly Button query = new Button { Text = "직접 상태 조회 (선택)" };
        private readonly Button slavePhone = new Button { Text = "PowerSI 보고서 + 명령어 운용" };
        private readonly CheckBox legacyMarker = new CheckBox { Text = "기존 M코드" };
        private readonly Button localPhone = new Button { Text = "이 PC만 연속 운용 (Slave 없이)" };
        // Master-only setting read by the operating session at its start; it is never passed to a form or session here.
        private readonly Label watchdogIntervalLabel = new Label { Text = "watchdog 확인 주기", TextAlign = ContentAlignment.MiddleRight };
        private readonly ComboBox watchdogInterval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private SlaveEndpoint endpoint;
        private CancellationTokenSource pending;
        private bool closing, phoneOpen, restoringInterval;
        private int savedIntervalMinutes;

        internal MasterHubForm(AuditLog log)
        {
            this.log = log;
            Text = AppInfo.Title + " - MASTER / SLAVE";
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(880, 470);
            AutoScroll = true;
            AutoScrollMinSize = ClientSize; // 기본 크기는 그대로 두고 축소·스크롤만 허용합니다.
            MinimumSize = new Size(700, 420);
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(new Label { Text = Text, Font = new Font(Font, FontStyle.Bold), Bounds = new Rectangle(18, 16, 844, 28) });
            Controls.Add(new Label { Text = "1. 다른 PC에서 Slave를 시작하고 연결파일을 저장합니다.\r\n" +
                "2. 이 창에서 연결파일을 엽니다. 기본 통합 확인은 고정 읽기 전용 명령어를 같은 세션에서 권장 순서로 확인합니다.", Bounds = new Rectangle(18, 50, 844, 47) });
            pairing.SetBounds(18, 107, 666, 26);
            pairing.AccessibleName = "Slave 연결 코드 (비밀값, 붙여넣기 또는 연결파일 열기)";
            import.SetBounds(694, 103, 168, 32);
            target.SetBounds(18, 144, 600, 30);
            target.AutoEllipsis = true;
            watchdogIntervalLabel.SetBounds(626, 146, 122, 24);
            watchdogInterval.SetBounds(752, 146, 110, 24);
            watchdogInterval.Items.AddRange(new object[] { "30분", "60분" });
            savedIntervalMinutes = WatchdogSettings.LoadIntervalMinutes(WatchdogSettings.DefaultPath); // Never throws; default 30.
            watchdogInterval.SelectedIndex = savedIntervalMinutes == 60 ? 1 : 0;
            watchdogInterval.AccessibleName = "watchdog 확인 주기 (30분 또는 60분, 다음 운용 시작부터 적용)";
            query.SetBounds(18, 182, 210, 36);
            slavePhone.SetBounds(238, 182, 285, 36);
            legacyMarker.SetBounds(531, 187, 92, 26);
            legacyMarker.AccessibleName = "기존 M코드 연속 운용 선택 (기본은 명령어 통합 확인)";
            localPhone.SetBounds(633, 182, 229, 36);
            result.SetBounds(18, 232, 844, 142);
            result.Text = "v" + LinkVersion.AppValue + ": 메신저의 고정 읽기 전용 명령으로 Slave 상태와 PowerSI 증거를 요청합니다.\r\n" +
                "pwrsi 그대로 입력하세요. total status는 단어 사이 한 칸입니다. 앞뒤 공백은 자동 제거합니다. 기존 M코드는 체크 시에만 사용합니다.\r\n" +
                "pwrsi는 요청 시점에 한 번 수집하고 모든 대상을 표시합니다. 각 답장은 1,400자 이하이며 PART 순서대로 전송합니다.\r\n" +
                "watchdog on [PID]는 위의 확인 주기마다 PowerSI 완료를 확인해 알리고 watchdog off [PID]로 해제합니다. 주기는 다음 운용 시작부터 적용됩니다.\r\n" +
                "Pending 대상은 이름·PID·Pending만 표시합니다. 그 밖의 대상은 출처·수집 시각·설명·수집된 Output 전체 또는 이전 전송 이후 추가분을 표시합니다.\r\n" +
                "보고서 수집은 최대 100초, 전체 조회는 최대 120초입니다. 진행률이나 완료율은 추측하지 않습니다.";
            var path = new TextBox { Text = log.FilePath, ReadOnly = true, TabStop = false, Bounds = new Rectangle(18, 389, 666, 25) };
            var folder = new Button { Text = "로그 폴더 열기", Bounds = new Rectangle(694, 386, 168, 32) };
            Controls.AddRange(new Control[] { pairing, import, target, watchdogIntervalLabel, watchdogInterval, query, slavePhone,
                legacyMarker, localPhone, result, path, folder });
            Controls.Add(new Label { Text = "연결파일에는 인증키가 있습니다. 마스터 PC로만 전달하며, 진단 로그와 함께 첨부하지 마세요.",
                Bounds = new Rectangle(18, 428, 844, 28) });
            pairing.TextChanged += delegate { ParseEndpoint(); };
            import.Click += delegate { ImportPairing(); };
            query.Click += async delegate { await Query(); };
            slavePhone.Click += delegate { if (endpoint != null) OpenPhone(endpoint, !legacyMarker.Checked); };
            localPhone.Click += delegate { OpenPhone(null, false); };
            watchdogInterval.SelectedIndexChanged += delegate { SaveWatchdogInterval(); }; // After the initial selection.
            folder.Click += delegate
            {
                try { using (Process.Start("explorer.exe", "\"" + log.FolderPath + "\"")) { } }
                catch { MessageBox.Show(this, "로그 폴더를 직접 여세요:\r\n" + log.FolderPath, AppInfo.Title); }
            };
            FormClosing += delegate { closing = true; pending?.Cancel(); };
            ParseEndpoint();
        }

        private void SaveWatchdogInterval()
        {
            if (restoringInterval) return;
            var minutes = watchdogInterval.SelectedIndex == 1 ? 60 : 30;
            if (minutes == savedIntervalMinutes) return;
            try
            {
                WatchdogSettings.SaveIntervalMinutes(WatchdogSettings.DefaultPath, minutes);
                savedIntervalMinutes = minutes;
                try { log.Write("INFO", "WATCHDOG_INTERVAL_SAVED", AuditLog.Field("minutes", minutes)); } catch { }
            }
            catch (Exception ex)
            {
                try { log.WriteException("WATCHDOG_INTERVAL_SAVE_FAILED", ex); } catch { }
                restoringInterval = true;
                try { watchdogInterval.SelectedIndex = savedIntervalMinutes == 60 ? 1 : 0; }
                finally { restoringInterval = false; }
                MessageBox.Show(this, "watchdog 확인 주기를 저장하지 못했습니다 (" + ex.GetType().Name + ").\r\n" +
                    "이전 설정 " + savedIntervalMinutes + "분을 그대로 사용합니다. 사용자 설정 폴더(%LOCALAPPDATA%\\RemoteMonitorMaster\\state)의 쓰기 권한을 확인하세요.",
                    AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ParseEndpoint()
        {
            endpoint = null;
            try { endpoint = SlaveEndpoint.Parse(pairing.Text.Trim()); }
            catch { }
            target.Text = endpoint == null ? "Slave 미설정 — 연결파일을 열거나 코드를 붙여넣으세요." :
                "대상 Slave: " + endpoint.Address + ":" + endpoint.Port + " / 인증서 " + endpoint.Pin.Substring(0, 12) + "…";
            UpdateButtons();
        }

        private void ImportPairing()
        {
            using (var dialog = new OpenFileDialog { Filter = "Slave 연결파일 (*.rmpair)|*.rmpair", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (new FileInfo(dialog.FileName).Length > 512) throw new InvalidDataException();
                    var text = File.ReadAllText(dialog.FileName).Trim();
                    SlaveEndpoint.Parse(text);
                    pairing.Text = text;
                }
                catch { result.Text = "연결파일을 읽지 못했습니다. Slave에서 저장한 .rmpair 파일인지 확인하세요."; }
            }
        }

        private async System.Threading.Tasks.Task Query()
        {
            if (pending != null || endpoint == null || closing || phoneOpen) return;
            var selected = endpoint;
            var cancellation = new CancellationTokenSource();
            pending = cancellation;
            UpdateButtons();
            result.Text = "Slave 상태 조회 중 (최대 8초)…";
            try
            {
                log.Write("INFO", "SLAVE_QUERY_BEGIN");
                var state = await StatusClient.QueryAsync(selected, cancellation.Token);
                if (closing) return;
                result.Text = "SLAVE STATUS — 연결·인증·현재 상태 조회 성공\r\n" +
                    "시각: " + state.LocalTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "\r\n부팅 경과: " + state.UptimeMinutes + "분\r\nRAM: " + state.AvailableMiB + " / " + state.TotalMiB +
                    " MiB (사용 가능 / 전체)\r\nSlave 통신 규약: " + state.Version +
                    " (Slave 앱 버전은 Slave 창 제목에서 확인)" + ProcessDetails(state.Processes);
                log.Write("INFO", "SLAVE_QUERY_COMPLETE");
            }
            catch (Exception ex)
            {
                if (closing) return;
                result.Text = "Slave 조회 실패: " + ex.GetType().Name + "\r\n" +
                    "Slave 시작 상태, 연결파일, IP 및 방화벽 연결을 확인하세요. PC를 오래 대기시킬 필요는 없습니다.";
                try { log.WriteException("SLAVE_QUERY_FAILED", ex); } catch { }
            }
            finally
            {
                pending = null;
                cancellation.Dispose();
                if (!closing)
                {
                    try { log.ReleaseFile(); result.AppendText("\r\nLOG READY — 앱을 켜 둔 채 로그를 첨부할 수 있습니다."); }
                    catch { result.AppendText("\r\nLOG CLOSE FAILED"); }
                    UpdateButtons();
                }
            }
        }

        private static string ProcessDetails(ProcessInventory inventory)
        {
            if (inventory == null) return "\r\n프로그램 목록 확인 불가";
            var text = new System.Text.StringBuilder("\r\n현재 세션 프로그램 " + inventory.Items.Length +
                "개 / 추가 생략 " + inventory.Omitted + " / 읽기 실패 " + inventory.Unreadable +
                "\r\nCPU는 전체 논리 CPU 기준 사용률입니다. 실행 시간은 프로세스 기준이며, 계산 진행률·성공 여부는 아직 확인하지 않습니다.");
            foreach (var process in inventory.Items)
                text.AppendFormat(CultureInfo.InvariantCulture, "\r\n{0}  PID {1} | CPU {2}% | RAM {3} MiB | 실행 {4}분",
                    process.Name, process.Pid,
                    process.CpuPermille.HasValue ? (process.CpuPermille.Value / 10m).ToString("0.0", CultureInfo.InvariantCulture) : "?",
                    process.WorkingSetMiB.HasValue ? process.WorkingSetMiB.Value.ToString(CultureInfo.InvariantCulture) : "?",
                    process.AgeSeconds.HasValue ? (process.AgeSeconds.Value / 60).ToString(CultureInfo.InvariantCulture) : "?");
            return text.ToString();
        }

        private void OpenPhone(SlaveEndpoint selected, bool plainCommands)
        {
            if (pending != null || closing || phoneOpen) return;
            phoneOpen = true;
            UpdateButtons();
            try
            {
                using (var form = new ReceiveForm(log, true, true, selected, true, plainCommands)) form.ShowDialog(this);
                if (!closing)
                    result.Text = "연속 운용 창이 종료됐습니다. 창에 LOG READY가 표시된 경우에만 로그를 첨부하세요.\r\n" +
                        (plainCommands ? "새 통합 확인은 버튼을 다시 눌러 새 승인과 READY의 고정 명령어로 시작합니다."
                            : "새 연속 운용은 버튼을 다시 눌러 새 승인과 새 M코드로 시작합니다.");
            }
            finally
            {
                phoneOpen = false;
                if (!closing) UpdateButtons();
            }
        }

        private void UpdateButtons()
        {
            var idle = pending == null && !closing && !phoneOpen;
            pairing.Enabled = import.Enabled = idle;
            query.Enabled = idle && endpoint != null;
            slavePhone.Enabled = idle && endpoint != null;
            legacyMarker.Enabled = idle && endpoint != null;
            localPhone.Enabled = idle;
            watchdogInterval.Enabled = !closing && !phoneOpen; // The running session keeps the interval it started with.
        }

        internal static void RunSelfTest(string directory)
        {
            // Real loopback TLS through the same request-bound reply path; no KI or physical input.
            var audit = new AuditLog(directory);
            try
            {
            using (var identity = SlaveIdentity.Create())
            using (var listener = new StatusServer(identity, System.Net.IPAddress.Loopback, 0, null,
                (inventory, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    var targets = new PowerSiTargetReport[inventory.Items.Length];
                    for (var i = 0; i < targets.Length; i++)
                    {
                        var process = inventory.Items[i];
                        targets[i] = new PowerSiTargetReport { Pid = process.Pid, StartUtcTicks = process.StartUtcTicks,
                            ProcessName = process.FullName ?? process.Name, State = "NOT_ATTEMPTED", Source = "NONE",
                            Code = "SELF_TEST_NOT_ATTEMPTED" };
                    }
                    return System.Threading.Tasks.Task.FromResult(new PowerSiReport { CapturedUtc = DateTime.UtcNow,
                        SessionId = inventory.SessionId, Omitted = inventory.Omitted, Unreadable = inventory.Unreadable,
                        Targets = targets });
                }))
            {
                listener.Start();
                var pairingText = identity.CreatePairing(System.Net.IPAddress.Loopback, listener.Port);
                var endpoint = SlaveEndpoint.Parse(pairingText);
                using (var hub = new MasterHubForm(audit))
                {
                    if (hub.Text != AppInfo.Title + " - MASTER / SLAVE" || hub.endpoint != null || hub.slavePhone.Enabled || hub.query.Enabled ||
                        hub.legacyMarker.Checked || hub.legacyMarker.Enabled || !hub.localPhone.Enabled ||
                        hub.slavePhone.Text != "PowerSI 보고서 + 명령어 운용")
                        throw new InvalidOperationException("Master auto-connected without a pairing.");
                    // Read-only check of the real setting; the self-test never writes the user's settings file.
                    if (hub.watchdogInterval.Items.Count != 2 || (string)hub.watchdogInterval.Items[0] != "30분" ||
                        (string)hub.watchdogInterval.Items[1] != "60분" || !hub.watchdogInterval.Enabled ||
                        hub.watchdogInterval.DropDownStyle != ComboBoxStyle.DropDownList ||
                        hub.watchdogInterval.SelectedIndex != (hub.savedIntervalMinutes == 60 ? 1 : 0) ||
                        !WatchdogState.IsValidIntervalMinutes(hub.savedIntervalMinutes) || !hub.result.Text.Contains("watchdog on"))
                        throw new InvalidOperationException("Watchdog interval control invalid.");
                    hub.pairing.Text = pairingText;
                    if (!hub.slavePhone.Enabled || !hub.query.Enabled || !hub.legacyMarker.Enabled || !hub.pairing.UseSystemPasswordChar)
                        throw new InvalidOperationException("Master pairing controls invalid.");
                    using (var phone = new ReceiveForm(audit, true, true, endpoint, true))
                    {
                        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        var approval = (CheckBox)typeof(ReceiveForm).GetField("confirmation", flags).GetValue(phone);
                        var begin = (Button)typeof(ReceiveForm).GetField("start", flags).GetValue(phone);
                        if (!phone.Text.Contains("SLAVE STATUS") || !phone.Text.Contains(AppInfo.Version) ||
                            !phone.Text.Contains("REPEAT UNTIL STOP") || !approval.Text.Contains("반복") ||
                            !approval.Text.Contains("Stop") || !approval.Text.Contains("프로그램 이름") ||
                            approval.Text.Contains("상태 답장 1회") || begin.Enabled)
                            throw new InvalidOperationException("Slave operating consent/title was not explicit.");
                    }
                    using (var commands = new ReceiveForm(audit, true, true, endpoint, true, true))
                    {
                        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        object Field(string name) { return typeof(ReceiveForm).GetField(name, flags).GetValue(commands); }
                        void Set(string name, object value) { typeof(ReceiveForm).GetField(name, flags).SetValue(commands, value); }
                        void Call(string name, params object[] args) { typeof(ReceiveForm).GetMethod(name, flags).Invoke(commands, args); }
                        var approval = (CheckBox)Field("confirmation");
                        var commandCode = (TextBox)Field("code");
                        var commandStatus = (Label)Field("status");
                        var commandNotice = (Label)Field("replyCode");
                        var commandDetails = (TextBox)Field("details");
                        var marker = (string)Field("marker");
                        var hasCommandGuide = false;
                        if (!commands.Text.Contains("COMMANDS") || !commands.Text.Contains("REPEAT UNTIL STOP") ||
                            !(bool)Field("plainCommands") || !approval.Text.Contains("help pwrsi") ||
                            !approval.Text.Contains("ASCII") || !approval.Text.Contains("대화창 활성화") ||
                            !approval.Text.Contains("최소화 복원") || !approval.Text.Contains("완료·경고 알림") ||
                            !((TextBox)Field("details")).Text.Contains("모든 대상") ||
                            !((TextBox)Field("details")).Text.Contains("PART") || commandCode.Text != "WAIT")
                            throw new InvalidOperationException("Plain command operating consent/title was not explicit.");
                        foreach (Control control in commands.Controls)
                        {
                            if (control.Text.Contains(marker)) throw new InvalidOperationException("Plain command UI exposed an M code.");
                            if (control.Text.Contains("help / help help / help total status / help pwrsi / help watchdog / total status / pwrsi / watchdog on / watchdog off"))
                                hasCommandGuide = true;
                            if (!commands.ClientRectangle.Contains(control.Bounds)) throw new InvalidOperationException("Plain command control clipped.");
                        }
                        if (!hasCommandGuide) throw new InvalidOperationException("Plain command guide was not visible.");
                        approval.Checked = true;
                        Call("Begin");
                        ((System.Windows.Forms.Timer)Field("countdown")).Stop();
                        if (Field("statusSession") == null) throw new InvalidOperationException("Plain command UI selected one-shot backend.");
                        // Model the worker's post-countdown state for synthetic UI progress only; no KI, network, or input action.
                        Set("stopped", false);
                        Set("closing", false);
                        Set("activeRound", 0);
                        Set("busy", true);
                        var generation = (int)Field("generation");
                        Call("ApplyOperatingProgress", 0, "READY_TO_RECEIVE", "M234567", generation);
                        if (commandCode.Text != "READY") throw new InvalidOperationException("Plain command READY code was not visible.");
                        if (!commandStatus.Text.Contains("명령 1회")) throw new InvalidOperationException("Plain command READY count was not visible.");
                        if (!commandNotice.Text.Contains("고정 명령어")) throw new InvalidOperationException("Plain command reply notice was not visible.");
                        if (!commandDetails.Text.Contains("허용 고정 명령어") || !commandDetails.Text.Contains("total status"))
                            throw new InvalidOperationException("Plain command READY guide was not explicit.");
                        foreach (var phase in new[] { "WAITING_FOR_PC_IDLE", "RESTORING_TARGET", "ACTIVATING_TARGET" })
                        {
                            Call("ApplyOperatingProgress", 0, phase, "M234567", generation);
                            if (commandCode.Text != "WAIT" || !commandNotice.Text.Contains("다른 대화창"))
                                throw new InvalidOperationException("Target preparation must remain visibly waiting.");
                        }
                        Call("ApplyOperatingProgress", 0, "READY_TO_RECEIVE", "M234567", generation);
                        if (commandCode.Text != "READY" || !commandNotice.Text.Contains("마우스 오버 불필요"))
                            throw new InvalidOperationException("Background receive did not return to Ready.");
                        Call("ApplyOperatingProgress", 0, "COMMAND_NOT_MATCHED", "M234567", generation);
                        if (commandCode.Text != "READY" || !commandStatus.Text.Contains("지원 명령과 불일치") ||
                            !commandNotice.Text.Contains("앞뒤 공백은 허용"))
                            throw new InvalidOperationException("Unmatched command hint was not visible.");
                        Call("ApplyOperatingProgress", 0, "SLAVE_QUERYING", "M234567", generation);
                        if (commandCode.Text != "WAIT" || !commandStatus.Text.Contains("Slave 상태 조회 중"))
                            throw new InvalidOperationException("Plain command query phase was not visible.");
                        Call("ApplyOperatingProgress", 0, "ROUNDTRIP_SENDING", "M234567", generation);
                        if (!commandStatus.Text.Contains("답장 전송 중")) throw new InvalidOperationException("Plain command send phase was not visible.");
                        Call("ApplyOperatingProgress", 0, "ROUNDTRIP_SENDING:2/3", "M234567", generation);
                        if (!commandStatus.Text.Contains("PART 2/3")) throw new InvalidOperationException("Multipart send progress was not visible.");
                        var summary = (Label)Field("watchdogSummary");
                        var statusBefore = commandStatus.Text;
                        if (summary.Text != "watchdog: 감시 없음" || !commands.Controls.Contains(summary))
                            throw new InvalidOperationException("Watchdog summary line was not visible.");
                        foreach (var round in new[] { 0, 5 })
                        {
                            Call("ApplyOperatingProgress", round, "WATCHDOG:감시 2개 · 다음 확인 21:30", "M234567", generation);
                            if (summary.Text != "watchdog: 감시 2개 · 다음 확인 21:30" || commandStatus.Text != statusBefore ||
                                (int)Field("activeRound") != 0)
                                throw new InvalidOperationException("Watchdog summary replaced the status line or moved the round.");
                        }
                        Call("ApplyOperatingProgress", 0, "WATCHDOG:감시 없음\r\n", "M234567", generation);
                        if (summary.Text != "watchdog: 감시 없음") throw new InvalidOperationException("Watchdog summary was not updated in place.");
                        Call("ApplyOperatingProgress", 0, "WATCHDOG_CHECKING", "M234567", generation);
                        if (commandCode.Text != "WAIT" || !commandStatus.Text.Contains("watchdog 확인 중 (최대 120초)") ||
                            !commandStatus.Text.Contains("확인 후 처리"))
                            throw new InvalidOperationException("Watchdog check phase was not visible.");
                        Call("ApplyOperatingProgress", 0, "NOTICE_WATCHDOG", "M234567", generation);
                        if (!commandStatus.Text.Contains("watchdog 알림을 보내는 중"))
                            throw new InvalidOperationException("Watchdog notice phase was not visible.");
                        Call("ApplyOperatingProgress", 0, "ROUND_COMPLETE", "M234567", generation);
                        if (!commandStatus.Text.Contains("다음 고정 명령어")) throw new InvalidOperationException("Plain command completion implied a separate test.");
                        Call("Stop", "TEST_COMMAND_STOP");
                        Set("busy", false);
                        Call("ShowResult", "STATUS_SESSION_STOPPED — STATUS_WATCHDOG_NOTICE_UNCERTAIN; completed=0. No automatic retry or resume.",
                            (int)Field("generation"));
                        if (!commandDetails.Text.Contains("STATUS_WATCHDOG_NOTICE_UNCERTAIN — watchdog 알림 전송이 불확실") ||
                            summary.Text != "watchdog: 세션 종료 — 감시 없음")
                            throw new InvalidOperationException("Session stop reason was not explained.");
                    }
                    foreach (Control control in hub.Controls)
                        if (!hub.ClientRectangle.Contains(control.Bounds)) throw new InvalidOperationException("Master control clipped.");
                }
                var session = new RoundTripSession("M234567", null, true, true, endpoint);
                var consent = session.GetConsent(0);
                if (session.MaximumReplies != 1 || !consent.IsSlaveStatus || !consent.TryClaimRoundTrip())
                    throw new InvalidOperationException("Slave reply not bound to one local approval.");
                var reply = consent.PrepareReply();
                if (!PcStatusReport.IsSlaveReply(reply, "D234567") || !reply.Contains(" | PROCS ") ||
                    !reply.Contains(" | PROGRESS n/a") || PcStatusReport.IsReply(reply, "D234567") ||
                    consent.IsAuthorizedReply(reply + " ") || !consent.TryConsume(reply) || !consent.TryCommitMove() ||
                    !consent.TryCommitWrite() || !consent.TryCommit() || consent.TryConsume(reply))
                    throw new InvalidOperationException("Slave reply origin/exact/once guard failed.");
                session.Cancel();
                foreach (var pair in new[] { new[] { "M234567", "M345678" }, new[] { "M345678", "M456789" } })
                {
                    var current = pair[0];
                    var next = pair[1];
                    var operating = new SupervisedSendTest.Consent("D" + current.Substring(1), true, true, endpoint, next);
                    if (!operating.TryClaimRoundTrip()) throw new InvalidOperationException("Operating Slave reply was not locally approved.");
                    var operatingReply = operating.PrepareReply(); // Real loopback status only; the commit flags below do not drive UI input.
                    if (!PcStatusReport.IsSlaveReply(operatingReply, "D" + current.Substring(1), next) ||
                        operatingReply.Contains(next) || !operatingReply.Contains("NEXT " + next.Substring(1)) ||
                        !operating.TryConsume(operatingReply) || !operating.TryCommitMove() || !operating.TryCommitWrite() || !operating.TryCommit())
                        throw new InvalidOperationException("Operating Slave NEXT reply was not exact or safely digit-only.");
                    operating.Cancel();
                    if (!operating.Cancelled) throw new InvalidOperationException("Operating Slave consent remained reusable.");
                }
                foreach (var command in new[] { "total status", "pwrsi" })
                {
                    var nonce = command == "total status" ? "D567892" : "D678923";
                    var commandConsent = new SupervisedSendTest.Consent(nonce, true, true, endpoint, null, true);
                    if (!commandConsent.TryClaimRoundTrip()) throw new InvalidOperationException("Plain command was not locally approved.");
                    commandConsent.BindCommand(command);
                    var commandReplies = commandConsent.PrepareReplies(); // One loopback query; no KI or physical send.
                    for (var i = 0; i < commandReplies.Length; i++)
                    {
                        var part = commandConsent.GetPreparedPart(i);
                        var commandReply = commandReplies[i];
                        if (!ReadOnlyCommands.IsReplyPart(commandReply, command, nonce, i + 1, commandReplies.Length) ||
                            !part.IsAuthorizedReply(commandReply) || part.IsAuthorizedReply(commandReply + " ") ||
                            !part.TryConsume(commandReply) || !part.TryCommitMove() || !part.TryCommitWrite() || !part.TryCommit())
                            throw new InvalidOperationException("Plain command reply part was not exact, sequential and request-bound.");
                    }
                    commandConsent.Cancel();
                    if (commandConsent.TryCommitMove()) throw new InvalidOperationException("Cancelled plain command committed input.");
                }
                // watchdog on performs the same loopback STATUS query as total status; watchdog off never connects.
                var watch = new WatchdogState(TimeSpan.FromMinutes(WatchdogState.DefaultIntervalMinutes));
                foreach (var command in new[] { "watchdog on", "watchdog off" })
                {
                    var nonce = command == "watchdog on" ? "D789234" : "D892345";
                    var commandConsent = new SupervisedSendTest.Consent(nonce, true, true, endpoint, null, true);
                    if (!commandConsent.TryClaimRoundTrip()) throw new InvalidOperationException("Watchdog command was not locally approved.");
                    commandConsent.AttachWatchdog(watch);
                    commandConsent.BindCommand(command);
                    string[] commandReplies;
                    using (ReadOnlyCommands.UseCommandLog(audit)) commandReplies = commandConsent.PrepareReplies();
                    var commandReply = commandReplies.Length == 1 ? commandReplies[0] : null;
                    if (commandReply == null || !ReadOnlyCommands.IsReplyPart(commandReply, command, nonce, 1, 1) ||
                        !commandReply.StartsWith("WATCHDOG " + nonce + "\r\n", StringComparison.Ordinal) ||
                        commandReply.Contains("Slave에 연결하지 못해") || !commandConsent.IsAuthorizedReply(commandReply) ||
                        commandConsent.IsAuthorizedReply(commandReply + " ") || !commandConsent.TryConsume(commandReply) ||
                        commandConsent.TryConsume(commandReply))
                        throw new InvalidOperationException("Watchdog reply was not exact, single-part and request-bound.");
                    commandConsent.Cancel();
                }
                if (watch.Count != 0) throw new InvalidOperationException("watchdog off left a watch armed.");
                var stopped = new SupervisedSendTest.Consent("D345678", true, true, endpoint);
                stopped.TryClaimRoundTrip(); stopped.Cancel();
                try { stopped.PrepareReply(); throw new InvalidOperationException("Cancelled Slave query ran."); }
                catch (MonitorException) { }
                if (stopped.TryCommitMove()) throw new InvalidOperationException("Cancelled Slave response committed input.");
                audit.ReleaseFile();
                using (var reader = new FileStream(audit.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    if (reader.Length == 0) throw new InvalidOperationException("Integrated log unavailable while alive.");
                var auditText = File.ReadAllText(audit.FilePath);
                if (auditText.Contains(identity.Token)) throw new InvalidOperationException("Pairing token leaked.");
                if (!auditText.Contains("code=\"WATCHDOG_COMMAND\"\taction=\"ON\"") || auditText.Contains("SLAVE_FAILURE") ||
                    (!auditText.Contains("result=\"CLEARED_ALL\"") && !auditText.Contains("result=\"NOTHING_WATCHED\"")))
                    throw new InvalidOperationException("Watchdog command record missing or failed.");
            }
            }
            finally { audit.Dispose(); File.Delete(audit.FilePath); }
        }
    }
}
