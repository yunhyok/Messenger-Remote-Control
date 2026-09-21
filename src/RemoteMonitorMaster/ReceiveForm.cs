using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using RemoteMonitorLink;
using Threading = System.Threading;

namespace RemoteMonitorMaster
{
    // The default remains read-only; each sending mode requires explicit approval and bounded actions per request.
    internal sealed class ReceiveForm : Form
    {
        private readonly AuditLog log;
        private readonly bool roundTrip;
        private readonly bool pcStatus;
        private readonly SlaveEndpoint slave;
        private readonly bool operating;
        private readonly bool plainCommands;
        private string ReportPrefix { get { return slave == null ? "PC STATUS" : "SLAVE STATUS"; } }
        private string StatusFields { get { return slave == null ? "시각·가동 시간·RAM·버전만 보냅니다." :
            "시각·RAM·버전과 프로그램 이름·PID·CPU·메모리·실행 시간을 보냅니다. 진행률은 미제공입니다."; } }
        private int ReplyCount { get { return pcStatus ? 1 : 2; } }
        private readonly string marker = "M" + Protocol.CreateDiagnosticDigits();
        private readonly string reply;
        private readonly string secondMarker;
        private readonly string secondReply;
        private readonly Timer countdown = new Timer { Interval = 5000 };
        private readonly Timer countdownDisplay = new Timer { Interval = 1000 }; // 남은 초 표시 전용. 네이티브·UIA 호출 없음.
        private readonly Label status = new Label();
        private readonly TextBox code = new TextBox();
        private readonly Label replyCode = new Label();
        private readonly TextBox details = new TextBox();
        private readonly CheckBox confirmation = new CheckBox();
        private readonly Button start = new Button();
        private readonly Button stop = new Button();
        private RoundTripSession session;
        private StatusSession statusSession;
        private volatile bool stopped = true;
        private bool started, busy, closing, auditFailed;
        private int generation;
        private int activeRound;
        private int environmentRevision, approvedEnvironmentRevision, disposed;
        private bool environmentReady;
        private string environmentReason;
        private DateTime countdownStartedUtc;
        private string countdownMessage = "";

        private const string RestartAdvice =
            "다시 시작하려면 이 창을 닫고 Master 시작 화면에서 운용 버튼을 다시 누르세요. 프로그램을 종료할 필요는 없습니다.";

        internal ReceiveForm(AuditLog log, bool roundTrip = false)
            : this(log, roundTrip, false) { }

        internal ReceiveForm(AuditLog log, bool roundTrip, bool pcStatus)
            : this(log, roundTrip, pcStatus, null) { }

        internal ReceiveForm(AuditLog log, bool roundTrip, bool pcStatus, SlaveEndpoint slave)
            : this(log, roundTrip, pcStatus, slave, false) { }

        internal ReceiveForm(AuditLog log, bool roundTrip, bool pcStatus, SlaveEndpoint slave, bool operating = false)
            : this(log, roundTrip, pcStatus, slave, operating, false) { }

        internal ReceiveForm(AuditLog log, bool roundTrip, bool pcStatus, SlaveEndpoint slave, bool operating, bool plainCommands)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            if (pcStatus && !roundTrip) throw new ArgumentException("PC status requires the approved reply mode.", nameof(pcStatus));
            if (slave != null && !pcStatus) throw new ArgumentException("Slave requires status mode.", nameof(slave));
            if (operating && (!pcStatus || !roundTrip)) throw new ArgumentException("Operating status requires the approved reply mode.", nameof(operating));
            if (plainCommands && (!operating || slave == null)) throw new ArgumentException("Plain commands require operating Slave status.", nameof(plainCommands));
            this.roundTrip = roundTrip;
            this.pcStatus = pcStatus;
            this.slave = slave;
            this.operating = operating;
            this.plainCommands = plainCommands;
            reply = "D" + marker.Substring(1);
            secondMarker = "M" + Protocol.CreateDiagnosticDigits();
            if (secondMarker == marker)
                secondMarker = marker.Substring(0, 6) + (marker[6] == '9' ? '2' : (char)(marker[6] + 1));
            secondReply = "D" + secondMarker.Substring(1);
            Text = AppInfo.Title + (operating ? " - " + ReportPrefix + (plainCommands ? " / COMMANDS" : "") + " / REPEAT UNTIL STOP" : pcStatus ? " - " + ReportPrefix + " / ONE REPLY" :
                roundTrip ? " - SESSION / TWO SENDS MAX" : " - RECEIVE / READ ONLY");
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(860, roundTrip ? (plainCommands ? 660 : 630) : 590);
            MinimumSize = new Size(700, 500); // 기본 크기는 그대로 두고 축소·스크롤만 허용합니다.
            Controls.Add(new Label { Text = Text, AutoEllipsis = true, Font = new Font(Font, FontStyle.Bold),
                Location = new Point(18, 16), Size = new Size(824, 30) });
            status.SetBounds(18, 47, 824, 30);
            status.Font = new Font(Font, FontStyle.Bold);
            Controls.Add(status);
            Controls.Add(new Label
            {
                Text = roundTrip
                    ? "1. PC 메신저에는 아무것도 입력하지 않습니다. 개별 자기 대화창의 입력란을 비워 두세요.\r\n" +
                        "2. 확인란 → Start (5초) → 5초 안에 KI 제목 표시줄을 클릭합니다. 초록색 READY를 기다리세요.\r\n" +
                        (operating ? (plainCommands
                            ? "3. READY 뒤 휴대폰에서 명령을 보냅니다. 마우스 오버는 불필요하며 대화창이 뒤에 있어도 대기합니다. 발송할 때 선택한 창을 앞으로 가져옵니다."
                            : "3. 첫 READY의 M을 휴대폰에서 한 번 전송합니다. 이후 답장 NEXT 숫자 앞에 M을 붙여 Stop 전까지 반복하며, LOG READY 후 로그를 첨부합니다.")
                            : pcStatus ? "3. READY에서 휴대폰으로 M코드 한 번 전송 → " + ReportPrefix + " 답장 확인 → LOG READY에서 로그 첨부로 끝납니다."
                            : "3. READY 1/2에서 M 전송 → D 확인 → READY 2/2에서 새 M 전송 → 두 번째 D 확인으로 끝납니다.")
                    : "1. Open your separate KI self-chat, leaving its input empty. Keep both windows visible.\r\n" +
                    "2. Confirm and Start. Within 5 seconds activate the KI chat by its title bar. No Send hover is needed.\r\n" +
                    "3. WAIT for the green READY signal and the M code below; then send that code ONCE from your PHONE.",
                Location = new Point(18, 85), Size = new Size(824, 62)
            });
            code.SetBounds(18, 157, 182, 46);
            code.Text = "WAIT";
            code.ReadOnly = true;
            code.TabStop = false;
            code.Font = new Font("Consolas", 22F, FontStyle.Bold);
            code.TextAlign = HorizontalAlignment.Center;
            code.AccessibleName = plainCommands ? "휴대폰 고정 소문자 읽기 전용 명령어 준비 상태" :
                roundTrip ? "READY 후 휴대폰에서 한 번 보낼 M코드" : "Phone test code, shown only after baseline is ready";
            Controls.Add(code);
            if (roundTrip)
            {
                replyCode.SetBounds(217, 186, 625, 28);
                replyCode.Text = operating ? (plainCommands ? "고정 읽기 전용 답장 — pwrsi 보고서는 1,400자 이하 PART 순차 전송"
                    : "반복 상태 답장 — 첫 M은 READY, 이후 NEXT 숫자 앞에 M")
                    : pcStatus ? "PC 자동 답장: " + ReportPrefix + " 1회 — 수신 후 현재 상태를 조회해 전송합니다."
                    : "PC 자동 답장: ① " + reply + "  ② " + secondReply + " — 직접 입력하지 마세요.";
                replyCode.AccessibleName = "PC 자동 답장 안내. 휴대폰에서 보내는 코드가 아닙니다.";
                Controls.Add(replyCode);
                Controls.Add(new Label
                {
                    Text = operating ? (plainCommands ? "← 휴대폰: pwrsi 그대로 / total status는 단어 사이 한 칸"
                        : "← 첫 M은 READY / 이후 NEXT 숫자 앞에 M")
                        : pcStatus ? "← 휴대폰에서 보낼 M코드 (READY 후 한 번 / 공백 없음)"
                        : "← 현재 회차에 휴대폰에서 보낼 M코드 (READY 후 한 번 / 공백 없음)",
                    Font = new Font(Font, FontStyle.Bold),
                    Location = new Point(217, 155), Size = new Size(625, 28)
                });
            }
            else Controls.Add(new Label
            {
                Text = "Do not send anything before READY. The code is M + six digits, with NO SPACES.\r\n" +
                    "The PC will NOT type, move the mouse, click Send or reply in this receive-only test.",
                Location = new Point(217, 160), Size = new Size(625, 45)
            });
            confirmation.Text = operating
                ? (plainCommands
                    ? "본인의 개별 자기 대화창입니다. Slave " + slave.Address + ":" + slave.Port + "의 고정 읽기 전용 명령어 상태 답장을 Stop 전까지 전송하는 것을 승인합니다.\r\n" +
                        "허용 명령: help / help help / help total status / help pwrsi / total status / pwrsi (소문자, 단어 사이는 ASCII 공백 한 칸).\r\n" +
                        "발송 시 선택한 대화창 활성화·최소화 복원을 허용합니다. 각 답장 부분마다 커서 이동·입력·클릭은 1회이며, 목록 밖 명령은 실행하지 않습니다."
                    : "본인의 개별 자기 대화창입니다. " + (slave == null ? "이 PC" : "Slave " + slave.Address + ":" + slave.Port) + "의 읽기 전용 상태 답장을 Stop 전까지 반복 전송하는 것을 승인합니다.\r\n" +
                        StatusFields + " 커서 이동·입력·클릭은 각 1회입니다.\r\n" +
                        "첫 READY의 M만 보내고 이후에는 답장 NEXT 숫자 앞에 M을 붙여 보냅니다. Stop 후 LOG READY까지 마우스·키보드를 건드리지 않습니다.")
                : pcStatus
                ? "본인의 개별 자기 대화창입니다. " + (slave == null ? "이 PC" : "Slave " + slave.Address + ":" + slave.Port) + " 상태 답장 1회 전송을 승인합니다.\r\n" +
                    StatusFields + " 커서 이동·입력·클릭은 각 1회입니다.\r\n" +
                    "본문과 첨부 이름은 구별되지 않습니다. READY 후 현재 M만 보냅니다. 다른 명령 실행은 하지 않습니다."
                : roundTrip
                ? "본인의 개별 자기 대화창이며 입력란이 비어 있습니다. 위 두 D답장의 전송을 각각 한 번 승인합니다.\r\n" +
                    "커서 이동·D 입력·Send 클릭(120 ms)을 각각 최대 2회 허용합니다. 본문과 첨부 이름은 구별되지 않습니다.\r\n" +
                    "최대 3분의 제한 시험이며 일반 원격 명령·무인 제어가 아닙니다. 휴대폰에서는 현재 M만 보내겠습니다."
                : "This is my separate self-chat, not the combined people-list window. I authorize read-only diagnosis.";
            confirmation.Font = Font; // 측정과 표시에 같은 글꼴을 사용합니다.
            var consentHeight = roundTrip ? (plainCommands ? 82 : 62) : 30;
            var consentExtra = Math.Max(0, TextRenderer.MeasureText(confirmation.Text, confirmation.Font,
                new Size(824 - 24, int.MaxValue), TextFormatFlags.WordBreak).Height + 8 - consentHeight);
            confirmation.SetBounds(18, 222, 824, consentHeight + consentExtra);
            confirmation.CheckedChanged += delegate { UpdateButtons(); };
            Controls.Add(confirmation);
            start.Text = "Start (5초)";
            var offset = (roundTrip ? (plainCommands ? 60 : 40) : 0) + consentExtra;
            start.SetBounds(18, 266 + offset, 210, 36);
            start.Click += delegate { Begin(); };
            stop.Text = "Stop";
            stop.SetBounds(737, 266 + offset, 105, 36);
            stop.Click += delegate { Stop("STOPPED"); };
            Controls.AddRange(new Control[] { start, stop });
            details.SetBounds(18, 322 + offset, 824, 155);
            details.Multiline = true;
            details.ReadOnly = true;
            details.TabStop = false;
            details.ScrollBars = ScrollBars.Vertical;
            details.Text = operating
                ? (plainCommands
                    ? "pwrsi 한 번은 Slave에서 요청 시점의 PowerSI 보고서를 한 번 수집하고, 1,400자 이하 PART 답장을 순서대로 전송합니다.\r\n" +
                        "help help, help total status도 인식하는 고정 읽기 전용 명령어입니다. 목록 밖의 명령은 승인하거나 실행하지 않으며, 메시지 본문 인증을 주장하지 않습니다.\r\n" +
                        "모든 대상이 표시되며 Pending 대상은 이름·PID·Pending만 보냅니다. 나머지는 출처·수집 시각·설명·수집된 Output 전체 또는 이전 전송 이후 추가분을 보내고 진행률·완료를 추측하지 않습니다.\r\n" +
                        "보고서 수집은 최대 100초, 전체 조회는 최대 120초입니다. 한 PART라도 전송 결과가 불확실하면 남은 PART를 보내지 않습니다. Stop 뒤 현재 호출이 끝나야 LOG READY가 표시됩니다."
                    : "반복 통합 확인: 첫 휴대폰 M코드 수신 → " + (slave == null ? "이 PC" : "선택한 Slave PC") + " 읽기 전용 상태 조회 → " + ReportPrefix + " 답장 → 다음 NEXT 숫자 앞에 M을 붙여 반복합니다.\r\n" +
                        "첫 초록 READY의 M만 한 번 보내세요. 이후 답장을 본 뒤 답장 NEXT 숫자 앞에 M을 붙여 바로 휴대폰에서 보낼 수 있습니다.\r\n" +
                        (slave == null ? "" : "휴대폰 목록은 현재 Slave 세션에서 CPU 사용률 우선, 창 있음·RAM 순 최대 8개입니다. PROGRESS n/a는 진행률 미제공입니다.\r\n") +
                        "각 조회·전송 단계의 제한은 유지되지만, 대기 시간 제한은 없습니다. Stop을 누른 뒤 현재 호출이 끝나야 LOG READY가 표시됩니다.\r\n" +
                        "PC 입력란·마우스·키보드는 건드리지 마세요. 휴대폰 실제 수신은 직접 확인하며, 프로그램은 명령을 실행하지 않습니다.")
                : pcStatus
                ? "통합 확인: 휴대폰 M코드 수신 → " + (slave == null ? "이 PC" : "선택한 Slave PC") + " 상태 조회 → " + ReportPrefix + " 답장 1회 → 로그파일 잠금 해제.\r\n" +
                    "답장에는 PC 현지 시각, 부팅 후 경과 시간(분), 사용 가능/전체 RAM(MiB), 프로그램 버전이 포함됩니다.\r\n" +
                    StatusFields + " PC·사용자 이름, 경로·명령줄은 보내지 않습니다.\r\n" +
                    "준비 중 45초간 진척이 없거나 휴대폰 전송 후 90초간 결과가 없으면 Stop을 누르세요.\r\n" +
                    "잠금·세션 변경·절전 알림 시 중단하며 자동 재개하지 않습니다. LOG READY 후 프로그램을 닫을 필요는 없습니다."
                : roundTrip
                ? "READY 1/2: 휴대폰에서 큰 칸의 M코드를 한 번 보냅니다. 첫 D답장 도착을 확인하세요.\r\n" +
                    "프로그램이 자동으로 다음 대기를 준비합니다. Start를 다시 누르거나 PC를 조작하지 않습니다.\r\n" +
                    "READY 2/2: 큰 칸의 새 M코드를 한 번 보냅니다. 두 D답장이 각각 한 번 왔는지 확인하고 로그 하나를 보내세요.\r\n" +
                    "준비 중 45초간 진척이 없거나 휴대폰 전송 후 90초간 결과가 없으면 Stop을 누르세요.\r\n" +
                    "잠금·세션 변경·절전 알림을 받으면 승인을 취소하며 자동 재개하지 않습니다. 이미 수행한 입력·클릭은 되돌리지 않습니다."
                : "One session collects baseline, new-message observations and additional accessibility metadata.\r\n" +
                    "After READY, send the displayed M code as ordinary phone text and leave both PC windows unchanged.\r\n" +
                    "The test ends automatically. Candidate detection is NOT authenticated command recognition; no PONG is sent.\r\n" +
                    "If preparation makes no progress for 45 seconds, or a phone send gives no result for 90 seconds, Stop and collect the log.";
            Controls.Add(details);
            var path = new TextBox { Text = log.FilePath, ReadOnly = true, TabStop = false };
            path.SetBounds(18, 494 + offset, 679, 24);
            var open = new Button { Text = "로그 폴더 열기" };
            open.SetBounds(707, 489 + offset, 135, 32);
            open.Click += delegate
            {
                try { using (Process.Start("explorer.exe", "\"" + log.FolderPath + "\"")) { } }
                catch { MessageBox.Show(this, "로그 폴더를 직접 여세요:\r\n" + log.FolderPath, AppInfo.Title); }
            };
            Controls.AddRange(new Control[] { path, open });
            Controls.Add(new Label
            {
                Text = "개인정보: 메시지 본문·파일명·시험 코드는 기록하지 않습니다. 컨트롤 메타데이터와 프로세스 경로는 남을 수 있습니다.",
                Location = new Point(18, 552 + offset), Size = new Size(824, 30)
            });
            if (consentExtra > 0) ClientSize = new Size(ClientSize.Width, ClientSize.Height + consentExtra);
            AutoScroll = true;
            AutoScrollMinSize = ClientSize;
            countdown.Tick += Tick;
            countdownDisplay.Tick += CountdownDisplayTick;
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                // 사용자가 직접 닫을 때만 확인합니다. 프로그램 Close(CloseReason.None)는 기존 동작 그대로입니다.
                if (busy && args.CloseReason == CloseReason.UserClosing &&
                    MessageBox.Show(this, "회신 전송이 진행 중입니다. 지금 닫으면 남은 PART를 보내지 않고 중단합니다.\r\n\r\n중단하고 닫을까요?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    args.Cancel = true;
                    return;
                }
                closing = true;
                Stop(roundTrip ? "CLOSING - waiting for the current call" : "CLOSING - waiting for the current read");
                if (busy) args.Cancel = true;
            };
            SetStatus(roundTrip ? (operating ? (plainCommands ? "시작 전 — 첫 READY 전에는 휴대폰에서 명령어를 보내지 마세요."
                : "시작 전 — 첫 READY까지 휴대폰에서 보내지 마세요. 이후에는 Stop 전까지 반복합니다.")
                : "시작 전 — 아직 휴대폰에서 아무것도 보내지 마세요.") : "NOT STARTED - do not send a phone message yet", false);
            try { log.Write("INFO", "RECEIVE_UI_READY", AuditLog.Field("read_only", !roundTrip),
                AuditLog.Field("supervised_roundtrip", roundTrip), AuditLog.Field("pc_status", pcStatus),
                AuditLog.Field("slave_status", slave != null),
                AuditLog.Field("operating_status", operating),
                AuditLog.Field("plain_commands", plainCommands),
                AuditLog.Field("maximum_replies", operating ? (object)"UNTIL_STOP" : roundTrip ? ReplyCount : 0), AuditLog.Field("automatic_send_allowed", false)); }
            catch { auditFailed = true; SetStatus("AUDIT UNAVAILABLE - no test can start", false); }
            var uiContext = Threading.SynchronizationContext.Current;
            try
            {
                // SystemEvents captures this context. Revoke in its callback before our asynchronous UI update.
                // OS delivery itself may be delayed; this does not abort a committed native/COM call.
                Threading.SynchronizationContext.SetSynchronizationContext(new Threading.SynchronizationContext());
                SystemEvents.SessionSwitch += OnSessionSwitch;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                SystemEvents.SessionEnding += OnSessionEnding;
                environmentReady = true;
            }
            catch (Exception ex)
            {
                DetachEnvironmentEvents();
                SetStatus("환경 변경 알림을 준비하지 못해 시작할 수 없습니다. 로그를 회수하세요.", false);
                try { log.WriteException("ENVIRONMENT_MONITOR_UNAVAILABLE", ex); } catch { }
            }
            finally { Threading.SynchronizationContext.SetSynchronizationContext(uiContext); }
            UpdateButtons();
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
        { InterruptEnvironment("SESSION_" + args.Reason); }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode == PowerModes.Suspend || args.Mode == PowerModes.Resume)
                InterruptEnvironment("POWER_" + args.Mode);
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs args)
        { InterruptEnvironment("SESSION_ENDING_" + args.Reason); } // Never veto Windows logoff/shutdown.

        private bool EnvironmentChanged(int revision)
        { return Threading.Volatile.Read(ref environmentRevision) != revision; }

        private void InterruptEnvironment(string reason)
        {
            if (Threading.Volatile.Read(ref disposed) != 0) return;
            Threading.Volatile.Write(ref environmentReason, reason);
            var revision = Threading.Interlocked.Increment(ref environmentRevision);
            stopped = true;
            Threading.Volatile.Read(ref session)?.Cancel(); // Revoke before UI dispatch; never wait for a worker/COM call.
            Threading.Volatile.Read(ref statusSession)?.Cancel();
            var run = Threading.Volatile.Read(ref generation);
            if (!IsHandleCreated) return; // Never create a Form handle on the SystemEvents thread.
            try { BeginInvoke(new Action(() => ShowEnvironmentInterruption(run, revision))); }
            catch (InvalidOperationException) { } // Disposing or a disappearing handle cannot restore approval.
        }

        private void ShowEnvironmentInterruption(int run, int revision)
        {
            if (Threading.Volatile.Read(ref disposed) != 0 || !started || generation != run ||
                revision != Threading.Volatile.Read(ref environmentRevision)) return;
            try { log.Write("INFO", "ENVIRONMENT_CHANGED", AuditLog.Field("reason", environmentReason),
                AuditLog.Field("revision", revision),
                AuditLog.Field("approval_revoked", true), AuditLog.Field("automatic_resume", false)); } catch { }
            if (busy || countdown.Enabled) Stop("ENVIRONMENT_CHANGED - " + environmentReason);
            else
            {
                // A late OS notification must not replace a final pending-draft or mouse-release warning.
                details.AppendText("\r\n환경 변경: " + environmentReason + " — 자동 재개하지 않습니다.");
                UpdateButtons();
            }
        }

        private void DetachEnvironmentEvents()
        {
            environmentReady = false;
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            try { SystemEvents.SessionEnding -= OnSessionEnding; } catch { }
        }

        private void Begin()
        {
            if (started || busy || closing || auditFailed || !environmentReady || !confirmation.Checked) return;
            approvedEnvironmentRevision = Threading.Volatile.Read(ref environmentRevision);
            started = true;
            stopped = false;
            generation++;
            confirmation.Checked = false;
            try
            {
                if (operating) Threading.Volatile.Write(ref statusSession, new StatusSession(marker, true, slave, plainCommands));
                else if (roundTrip) Threading.Volatile.Write(ref session, new RoundTripSession(marker, secondMarker, true, pcStatus, slave));
                if (EnvironmentChanged(approvedEnvironmentRevision)) { Stop("ENVIRONMENT_CHANGED - " + environmentReason); return; }
                log.Write("INFO", "RECEIVE_COUNTDOWN", AuditLog.Field("seconds", 5), AuditLog.Field("read_only", !roundTrip),
                    AuditLog.Field("supervised_roundtrip", roundTrip), AuditLog.Field("unclassified_candidate_fixed_reply_confirmed", roundTrip && !pcStatus),
                    AuditLog.Field("unclassified_candidate_pc_status_confirmed", pcStatus),
                    AuditLog.Field("operating_status", operating),
                    AuditLog.Field("plain_commands", plainCommands),
                    AuditLog.Field("maximum_replies", operating ? (object)"UNTIL_STOP" : roundTrip ? ReplyCount : 0));
                countdownMessage = roundTrip ? (operating ? (plainCommands ? "5초 안에 KI 제목 표시줄을 클릭하세요. 첫 READY 전에는 명령어를 보내지 마세요."
                    : "5초 안에 KI 제목 표시줄을 클릭하세요. 첫 READY 전에는 보내지 마세요.")
                    : "5초 안에 KI 제목 표시줄을 클릭하세요. 휴대폰 전송은 아직 하지 마세요.") :
                    "5 SECONDS - activate the KI chat; do not send from the phone yet";
                SetStatus(CountdownText(5), false);
                SetInteractionNotice(plainCommands ? "처음에 대화창을 한 번 선택하세요. READY 뒤 마우스 오버·앞면 유지는 필요 없습니다." :
                    "KI 창 선택 후에는 마스터 LOG READY까지 PC 조작을 멈춰 주세요.");
                if (stopped || EnvironmentChanged(approvedEnvironmentRevision)) { Stop("ENVIRONMENT_CHANGED - " + environmentReason); return; }
                countdown.Start();
                countdownStartedUtc = DateTime.UtcNow;
                countdownDisplay.Start();
            }
            catch (Exception ex)
            {
                try { log.WriteException("RECEIVE_BEGIN_FAILED", ex); } catch { }
                Stop((ex as MonitorException)?.ReasonCode ?? "RECEIVE_BEGIN_FAILED");
            }
            UpdateButtons();
        }

        private string CountdownText(int remaining)
        {
            return remaining + "초 남음 — " + countdownMessage;
        }

        // 라벨만 갱신합니다. 5초 선택 판정은 countdown Timer의 Tick이 그대로 담당합니다.
        private void CountdownDisplayTick(object sender, EventArgs args)
        {
            if (!countdown.Enabled || stopped || busy || closing) { countdownDisplay.Stop(); return; }
            var remaining = 5 - (int)(DateTime.UtcNow - countdownStartedUtc).TotalSeconds;
            if (remaining < 1) { countdownDisplay.Stop(); return; }
            SetStatus(CountdownText(remaining), false);
        }

        private async void Tick(object sender, EventArgs args)
        {
            countdownDisplay.Stop();
            if (!countdown.Enabled || busy || closing) { countdown.Stop(); return; }
            if (EnvironmentChanged(approvedEnvironmentRevision)) { Stop("ENVIRONMENT_CHANGED - " + environmentReason); return; }
            if (stopped) { countdown.Stop(); return; }
            countdown.Stop();
            busy = true;
            var runGeneration = generation;
            var runEnvironmentRevision = approvedEnvironmentRevision;
            var runSession = Threading.Volatile.Read(ref session);
            var runStatusSession = Threading.Volatile.Read(ref statusSession);
            UpdateButtons();
            try
            {
                var window = NativeMethods.GetForegroundWindow();
                if (window == IntPtr.Zero || window == Handle)
                    throw new MonitorException("RECEIVE_NO_TARGET", "Activate the separate KI chat during countdown.");
                ShowSelectedWindowNotice(window);
                SetStatus(roundTrip ? (operating ? (plainCommands ? "명령어 통합 확인 준비 중 — 첫 READY까지 기다리세요."
                    : "반복 상태 확인 준비 중 — 첫 READY까지 기다리세요.")
                    : "기존 대화 확인 중 — 초록색 READY까지 기다리세요. 아직 보내지 마세요.") :
                    "PREPARING BASELINE - do not send from the phone yet", false);
                var result = await Task.Run(() =>
                {
                    if (operating) return runStatusSession.Run(window, log, () => stopped || EnvironmentChanged(runEnvironmentRevision),
                        (round, phase, currentMarker) => PublishOperatingSession(round, phase, currentMarker, runGeneration));
                    if (roundTrip) return runSession.Run(window, log, () => stopped || EnvironmentChanged(runEnvironmentRevision),
                        (round, phase) => PublishSession(round, phase, runGeneration));
                    return ReceiveProbe.Run(window, log, marker, () => stopped || EnvironmentChanged(runEnvironmentRevision),
                        phase => Publish(phase, runGeneration));
                });
                ShowResult(result, runGeneration);
            }
            catch (Exception ex)
            {
                var reason = (ex as MonitorException)?.ReasonCode ?? "RECEIVE_FAILED";
                try { log.WriteException("RECEIVE_UI_FAILED", ex, AuditLog.Field("reason", reason)); } catch { }
                session?.Cancel();
                statusSession?.Cancel();
                ShowResult(reason, runGeneration);
            }
            finally
            {
                stopped = true;
                session?.Cancel();
                statusSession?.Cancel();
                busy = false;
                UpdateButtons();
                if (closing) BeginInvoke(new Action(Close));
            }
        }

        // 참고 표시 전용: 프로세스 이름·PID만 읽습니다. KI-Messenger 판정은 작업자 스레드의 PROBE_NOT_KI_MESSENGER가 담당합니다.
        private void ShowSelectedWindowNotice(IntPtr window)
        {
            uint pid;
            if (NativeMethods.GetWindowThreadProcessId(window, out pid) == 0 || pid == 0) return;
            string name = null;
            try { using (var process = Process.GetProcessById((int)pid)) name = process.ProcessName; }
            catch { return; } // 참고 정보일 뿐이므로 실패해도 세션을 중단하지 않습니다.
            if (string.IsNullOrEmpty(name)) return;
            SetInteractionNotice("선택한 창: " + name + " (PID " + pid + ") — 이 창으로만 주고받습니다.");
            try { log.Write("INFO", "RECEIVE_TARGET_SELECTED", AuditLog.Field("process_name", name), AuditLog.Field("pid", pid)); }
            catch { }
        }

        private void Publish(string phase, int runGeneration)
        {
            if (stopped || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => ApplyProgress(phase, runGeneration))); }
            catch (InvalidOperationException) { }
        }

        private void PublishSession(int round, string phase, int runGeneration)
        {
            if (stopped || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => ApplySessionProgress(round, phase, runGeneration))); }
            catch (InvalidOperationException) { }
        }

        private void PublishOperatingSession(int round, string phase, string currentMarker, int runGeneration)
        {
            if (stopped || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => ApplyOperatingProgress(round, phase, currentMarker, runGeneration))); }
            catch (InvalidOperationException) { }
        }

        private void ApplySessionProgress(int round, string phase, int runGeneration)
        {
            if (stopped || closing || !busy || generation != runGeneration || round < activeRound || round < 0 || round >= ReplyCount) return;
            activeRound = round;
            var count = (round + 1) + "/" + ReplyCount;
            if (phase == "BASELINE")
            {
                code.Text = "WAIT";
                SetStatus(count + " 대기 준비 중 — 아직 휴대폰에서 보내지 마세요.", false);
                SetInteractionNotice("진행 중 — 마스터 LOG READY까지 마우스·키보드를 건드리지 마세요.");
            }
            else if (phase == "READY_TO_RECEIVE")
            {
                code.Text = round == 0 ? marker : secondMarker;
                SetStatus("READY " + count + " — 휴대폰에서 M코드만 한 번 보내세요." +
                    (pcStatus ? " " + ReportPrefix + "를 답장합니다." : " D는 보내지 마세요.") + " (최대 60초)", true);
                SetInteractionNotice("휴대폰에서만 M 전송 — 마스터 LOG READY까지 PC 조작을 멈춰 주세요.");
                details.Text = pcStatus
                    ? "큰 칸의 M코드를 휴대폰에서 한 번 보내세요. PC 메신저 입력란에는 입력하지 않습니다.\r\n" +
                        (slave == null ? "이 PC" : "Slave PC") + "의 현지 시각·부팅 후 경과 분·사용 가능/전체 RAM(MiB)·프로그램 버전을 수신 후 조회합니다.\r\n" +
                        "휴대폰에서 " + ReportPrefix + " 답장 한 건을 확인하세요. 두 번째 M코드 전송은 없습니다.\r\n" +
                        "PC 조작·수동 Send·마우스 오버는 필요 없습니다. LOG READY 후 앱을 켜 둔 채 로그를 첨부하세요.\r\n" +
                        "90초간 진척이 없으면 Stop 후 로그를 보내세요."
                    : (round == 0 ? "첫 번째" : "두 번째") + " 대기입니다. 큰 칸의 현재 M코드를 휴대폰에서 한 번 보내세요.\r\n" +
                    "PC 자동 답장: " + (round == 0 ? reply : secondReply) + " — 휴대폰에서는 도착만 확인합니다.\r\n" +
                    (round == 0 ? "첫 답장 후 프로그램이 다음 대기를 자동 준비합니다. READY 2/2까지 기다리세요.\r\n" :
                        "이전 M코드는 응답 대상이 아닙니다. 이번 답장까지 두 개가 각각 한 번 왔는지 확인하세요.\r\n") +
                    "PC 조작·수동 Send·마우스 오버는 필요 없습니다. 90초간 진척이 없으면 Stop 후 로그를 보내세요.";
            }
            else if (phase == "WAITING_FOR_REPEAT")
                SetStatus(count + " M 표시 재확인 중 — 추가 메시지를 보내지 마세요.", false);
            else if (phase == "SLAVE_QUERYING" || phase == "PC_STATUS_QUERYING")
            {
                code.Text = "WAIT";
                SetStatus(count + (phase == "SLAVE_QUERYING" ? " Slave 조회 중 — 일반 상태 약 8초 / PowerSI 전체 최대 120초, Stop 가능…" : " 이 PC 상태 조회 중…"), false);
                SetInteractionNotice("조회 중 — 상태 조회 완료는 메신저 시험 종료가 아닙니다.");
            }
            else if (phase == "ROUNDTRIP_SENDING" || phase.StartsWith("ROUNDTRIP_SENDING:", StringComparison.Ordinal))
            {
                code.Text = "WAIT";
                var part = phase.Length > "ROUNDTRIP_SENDING:".Length ? phase.Substring("ROUNDTRIP_SENDING:".Length) : null;
                SetStatus(count + (part == null ? (pcStatus ? " PC 상태를 조회해 승인된 답장을 입력·전송합니다." :
                    " PC가 승인된 D답장을 자동 입력·전송합니다.") : " 승인된 답장 PART " + part + " 입력·전송 중입니다.") +
                    " PC를 건드리지 마세요.", false);
                SetInteractionNotice("전송 중 — 마우스·키보드를 건드리지 마세요. 아직 종료되지 않았습니다.");
            }
            else if (phase == "ROUND_COMPLETE")
            {
                code.Text = "WAIT";
                SetStatus(count + " 전송 동작 종료 — " + (round + 1 < ReplyCount ? "다음 READY를 기다리세요." : "휴대폰 수신을 확인하세요."), false);
                SetInteractionNotice("마감 중 — 마스터 LOG READY가 뜰 때까지 기다려 주세요.");
            }
        }

        private void ApplyOperatingProgress(int round, string phase, string currentMarker, int runGeneration)
        {
            if (stopped || closing || !busy || generation != runGeneration || round < activeRound || round < 0) return;
            if (plainCommands)
            {
                ApplyPlainOperatingProgress(round, phase);
                return;
            }
            activeRound = round;
            var completed = CompletedRoundCount;
            var count = "완료 " + completed + "회 / 요청 " + (round + 1) + "회";
            if (phase == "BASELINE")
            {
                code.Text = "WAIT";
                SetStatus(count + " 다음 상태 요청 준비 중 — 아직 휴대폰에서 보내지 마세요.", false);
                SetInteractionNotice(completed == 0 ? "대기 중 — 첫 READY 코드 / 종료하려면 Stop" : "대기 중 — NEXT 숫자 앞에 M / 종료하려면 Stop");
            }
            else if (phase == "READY_TO_RECEIVE")
            {
                if (!Protocol.IsDiagnosticMarker("MESSAGE", currentMarker)) { Stop("OPERATING_CODE_INVALID"); return; }
                code.Text = currentMarker;
                SetStatus("READY — " + count + " / 현재 " + currentMarker + " — 휴대폰에서 이 M 한 번", true);
                SetInteractionNotice(completed == 0 ? "첫 M은 현재 READY 코드 / 종료하려면 Stop" : "NEXT 숫자 앞에 M / 종료하려면 Stop");
                details.Text = "현재 " + currentMarker + "를 휴대폰에서 한 번 보내세요. 완료된 요청: " + completed + "회.\r\n" +
                    "첫 요청 뒤에는 휴대폰 답장 NEXT 숫자 앞에 M을 붙여 바로 보낼 수 있습니다. 프로그램은 다음 대기 기준을 미리 준비합니다.\r\n" +
                    "Stop을 누른 뒤 현재 조회/전송 호출이 끝나야 LOG READY가 표시됩니다. PC 입력란·마우스·키보드는 건드리지 마세요.\r\n" +
                    "상태 조회만 수행하며 휴대폰 실제 수신은 직접 확인합니다.";
            }
            else if (phase == "WAITING_FOR_REPEAT")
                SetStatus(count + " M 표시 재확인 중 — 같은 M을 다시 보내지 마세요.", false);
            else if (phase == "SLAVE_QUERYING" || phase == "PC_STATUS_QUERYING")
            {
                code.Text = "WAIT";
                SetStatus(count + (phase == "SLAVE_QUERYING" ? " Slave 상태 조회 중…" : " 이 PC 상태 조회 중…"), false);
                SetInteractionNotice("조회 중 — 완료 전에는 PC 조작 금지");
            }
            else if (phase == "ROUNDTRIP_SENDING")
            {
                code.Text = "WAIT";
                SetStatus(count + " 상태 답장 PART 순차 전송 중 — 아직 완료되지 않았습니다.", false);
                SetInteractionNotice("전송 중 — 완료 전에는 PC 조작 금지");
            }
            else if (phase == "ROUND_COMPLETE")
            {
                code.Text = "WAIT";
                SetStatus("요청 " + (round + 1) + "회 완료 — 총 " + completed + "회 완료. NEXT 숫자 앞에 M / Stop 종료", false);
                SetInteractionNotice("완료됨 — 연속 운용 중 / 종료하려면 Stop");
            }
        }

        private void ApplyPlainOperatingProgress(int round, string phase)
        {
            activeRound = round;
            var completed = CompletedRoundCount;
            var count = "완료 " + completed + "회 / 명령 " + (round + 1) + "회";
            if (phase.StartsWith("WAITING_FOR_PC_IDLE:", StringComparison.Ordinal))
            {
                // 같은 대기의 원인별 안내입니다. 기본 "WAITING_FOR_PC_IDLE" 문구는 아래 분기에서 그대로 유지합니다.
                code.Text = "WAIT";
                SetStatus(IdleWaitAdvice(phase.Substring("WAITING_FOR_PC_IDLE:".Length)), false);
                SetInteractionNotice("60초 안에 조건이 충족되지 않으면 TARGET_PC_NOT_IDLE로 중단하고 로그에 원인을 남깁니다.");
            }
            else if (phase == "WAITING_FOR_PC_IDLE" || phase == "ACTIVATING_TARGET" || phase == "RESTORING_TARGET")
            {
                code.Text = "WAIT";
                SetStatus(phase == "WAITING_FOR_PC_IDLE" ? "PC 입력이 끝나기를 기다리고 있습니다. 요청은 유지됩니다." :
                    phase == "RESTORING_TARGET" ? "처음 선택한 대화창의 최소화를 복원하고 있습니다." :
                    "답장을 위해 처음 선택한 대화창을 앞으로 가져옵니다.", false);
                SetInteractionNotice("다른 대화창으로 대상을 바꾸지 않습니다. 실제 전송 중에는 잠시 PC 입력을 멈춰 주세요.");
            }
            else if (phase == "NOTICE_READY" || phase == "NOTICE_BUSY")
            {
                code.Text = "WAIT";
                SetStatus(phase == "NOTICE_READY" ? "메신저로 Master Ready 안내를 보내는 중입니다." :
                    "메신저로 처리 중 안내를 보내는 중입니다. 새 명령을 보내지 마세요.", false);
                SetInteractionNotice("메신저 안내 전송 중 — 마우스·키보드를 건드리지 마세요.");
            }
            else if (phase == "REVALIDATING")
            {
                code.Text = "WAIT";
                SetStatus("같은 명령과 대화창을 재확인 중입니다. 새 명령을 보내지 마세요.", false);
            }
            else if (phase == "BASELINE")
            {
                code.Text = "WAIT";
                SetStatus(count + " 고정 명령어 수신 준비 중 — 아직 휴대폰에서 보내지 마세요.", false);
                SetInteractionNotice("대기 중 — READY의 고정 명령어 / 종료하려면 Stop");
            }
            else if (phase == "READY_TO_RECEIVE")
            {
                code.Text = "READY";
                SetStatus("READY — " + count + " / 휴대폰에서 소문자 명령어 하나 (pwrsi에는 공백 불필요)", true);
                SetInteractionNotice("다른 창 뒤에서도 고정 명령어 수신 대기 / 마우스 오버 불필요 / Stop으로 종료");
                details.Text = "허용 고정 명령어: help / help help / help total status / help pwrsi / total status / pwrsi\r\n" +
                    "메신저의 Master Ready 이후 첫 명령 하나를 처리합니다. 처리 중 추가 메시지는 대기열에 넣지 않고 무시하므로 다음 Master Ready 뒤에 새 명령을 보내세요.\r\n" +
                    "처음 선택한 대화창을 기억합니다. 다른 창 뒤에서는 그대로 읽고, 발송할 때 앞으로 가져옵니다. 최소화된 창은 복원합니다. PC 입력 중이면 잠시 기다립니다.\r\n" +
                    "total status는 Slave 프로그램 상태를 표시합니다. pwrsi는 모든 PowerSI 대상의 요청 시점 증거를 1,400자 이하 PART로 순서대로 보냅니다.\r\n" +
                    "Pending은 이름·PID·Pending만 표시하며 진행률·완료를 추측하지 않습니다. Stop 뒤 현재 호출이 끝나야 LOG READY가 표시됩니다.";
            }
            else if (phase == "WAITING_FOR_REPEAT")
                SetStatus(count + " 수신 명령어 재확인 중 — 추가 메시지를 보내지 마세요.", false);
            else if (phase == "COMMAND_NOT_MATCHED")
            {
                code.Text = "READY";
                SetStatus("새 메시지는 읽었지만 지원 명령과 불일치 — 소문자 pwrsi / total status를 확인하세요.", false);
                SetInteractionNotice("앞뒤 공백은 허용 / 철자·대소문자·단어 사이 공백은 구분 / Stop으로 종료");
            }
            else if (phase == "COMMAND_NOT_VISIBLE")
            {
                // 첫 명령 행은 고정되며 다른 행이 대신할 수 없습니다. 보이는 enabled 상태가 될 때까지 접수하지 않습니다.
                code.Text = "READY";
                SetStatus("명령 메시지를 읽었지만 화면 밖이거나 비활성 상태 — 대화 기록을 맨 아래로 스크롤해 명령이 보이게 하세요.", false);
                SetInteractionNotice("첫 명령 행이 보이는 enabled 상태여야 접수 / 이후 보낸 메시지는 대신 접수되지 않음 / Stop으로 종료");
            }
            else if (phase == "SLAVE_QUERYING" || phase == "PC_STATUS_QUERYING")
            {
                code.Text = "WAIT";
                SetStatus(count + " Slave 상태 조회 중 — 고정 명령어 답장 준비", false);
                SetInteractionNotice("조회 중 — 상태 조회는 시험 종료가 아닙니다.");
            }
            else if (phase == "ROUNDTRIP_SENDING" || phase.StartsWith("ROUNDTRIP_SENDING:", StringComparison.Ordinal))
            {
                code.Text = "WAIT";
                var part = phase.Length > "ROUNDTRIP_SENDING:".Length ? phase.Substring("ROUNDTRIP_SENDING:".Length) : null;
                SetStatus(count + (part == null ? " 고정 읽기 전용 명령어 답장 전송 중" :
                    " 답장 PART " + part + " 전송 중") + " — 아직 완료되지 않았습니다.", false);
                SetInteractionNotice("전송 중 — 완료 전에는 PC 조작 금지");
            }
            else if (phase == "ROUND_COMPLETE")
            {
                code.Text = "WAIT";
                SetStatus("명령 " + (round + 1) + "회 완료 — 휴대폰 답장을 확인하고 다음 고정 명령어를 보낼 수 있습니다. Stop으로 종료합니다.", false);
                SetInteractionNotice("완료됨 — 명령어 통합 확인 중 / 종료하려면 Stop");
            }
        }

        // PC 입력 정지 대기를 막고 있는 원인 하나를 그대로 알려 줍니다. 모르는 코드는 기본 문구를 씁니다.
        private static string IdleWaitAdvice(string reason)
        {
            switch (reason)
            {
                case "INPUT_RECENT":
                    return "PC 입력이 계속 감지되어 기다리는 중입니다 (마지막 입력 1초 미만). 마우스·키보드에서 손을 떼세요. 마우스 흔들림 방지 프로그램이나 원격 제어 도구가 있으면 잠시 중지하세요.";
                case "KEY_HELD":
                    return "마우스 버튼 또는 Shift/Ctrl/Alt/Win 키가 눌려 있어 기다리는 중입니다. 키와 버튼에서 손을 떼세요.";
                case "FOREGROUND_BUSY":
                    return "앞에 있는 창이 메뉴·끌기·마우스 캡처 상태여서 기다리는 중입니다. 메뉴를 닫고 끌기를 끝내세요.";
                case "NO_FOREGROUND":
                    return "활성 창이 없어(잠금 화면 또는 전환 중) 기다리는 중입니다.";
            }
            return "PC 입력이 끝나기를 기다리고 있습니다. 요청은 유지됩니다.";
        }

        private void ApplyProgress(string phase, int runGeneration)
        {
            if (stopped || closing || !busy || generation != runGeneration) return;
            if (phase == "READY_TO_RECEIVE")
            {
                code.Text = marker;
                SetStatus("READY - send the M code ONCE from your PHONE now (waiting up to 60 seconds)", true);
            }
            else if (phase == "BASELINE_METADATA") SetStatus("PREPARING BASELINE METADATA - do not send yet", false);
            else if (phase == "AFTER_METADATA") SetStatus("CANDIDATE SEEN - collecting metadata; do not send another message", false);
            else if (phase == "WAITING_FOR_REPEAT") SetStatus("CANDIDATE SEEN - checking again automatically; do not send again", false);
        }

        private int CompletedRoundCount
        {
            get { return operating ? Threading.Volatile.Read(ref statusSession)?.CompletedRounds ?? 0 : session?.CompletedRounds ?? 0; }
        }

        private bool ActivePendingWrite
        {
            get { return operating ? Threading.Volatile.Read(ref statusSession)?.PendingWrite == true : session?.PendingWrite == true; }
        }

        private bool ActiveWriteAttempted
        {
            get { return operating ? Threading.Volatile.Read(ref statusSession)?.WriteAttempted == true : session?.WriteAttempted == true; }
        }

        private bool ActiveSendAttempted
        {
            get { return operating ? Threading.Volatile.Read(ref statusSession)?.SendAttempted == true : session?.SendAttempted == true; }
        }

        private bool ActiveCursorMoveAttempted
        {
            get { return operating ? Threading.Volatile.Read(ref statusSession)?.CursorMoveAttempted == true : session?.CursorMoveAttempted == true; }
        }

        private void ShowResult(string result, int runGeneration)
        {
            if (roundTrip && (result.Contains(SupervisedSendTest.MouseReleaseWarning) ||
                ActivePendingWrite)) closing = false;
            var explanation = Explain(result); // 작업자 결과 본문은 매핑되지 않으므로 사유 코드일 때만 설명이 붙습니다.
            details.Text = (string.IsNullOrEmpty(explanation) ? result : result + " — " + explanation) + "\r\n" + OutcomeAdvice();
            if (EnvironmentChanged(approvedEnvironmentRevision))
                details.AppendText("\r\n환경 변경: " + environmentReason + " — 승인은 취소되었으며 자동 재개하지 않습니다.");
            details.AppendText("\r\n" + RestartAdvice);
            if (roundTrip) code.Text = "WAIT";
            SetStatus((stopped || runGeneration != generation ? "STOPPED / RESULT READY" : "RESULT READY") +
                (operating ? " — 연속 운용 종료 / 완료 " + CompletedRoundCount + "회 / 휴대폰 수신 확인 필요"
                    : roundTrip ? " — 시험 종료. 전송 동작 " + CompletedRoundCount + "/" + ReplyCount + "; 휴대폰 수신 확인 필요"
                    : " - read only; session locked"), false);
            ReleaseLogFile(); // Called after the awaited worker has finished all of its diagnostic writes.
        }

        private void ReleaseLogFile()
        {
            try
            {
                log.ReleaseFile();
                details.AppendText("\r\nLOG READY — 프로그램을 닫지 않고 로그파일을 첨부할 수 있습니다.");
                SetInteractionNotice(operating ? "종료 / LOG READY — 로그 첨부 가능; 휴대폰 수신은 직접 확인"
                    : "종료 / LOG READY — 로그 첨부 가능. 실제 수신은 휴대폰에서 확인하세요.", true);
            }
            catch (Exception ex)
            {
                auditFailed = true;
                closing = false;
                details.AppendText("\r\nLOG CLOSE FAILED — " + ex.GetType().Name);
                SetInteractionNotice("시험 종료 / LOG CLOSE FAILED — 로그파일 잠금 해제 실패", true);
            }
        }

        private string OutcomeAdvice()
        {
            if (!roundTrip) return "No PC input or Send action was performed. Collect the log; do not repeat.";
            if (operating)
            {
                var operatingAdvice = "완료된 요청: " + CompletedRoundCount + "회; 휴대폰 수신은 직접 확인하세요.\r\n" +
                    "채팅창과 초안을 그대로 두세요. 수동 전송·초안 삭제·자동 재개는 하지 않습니다.";
                if (ActivePendingWrite) return "상태 답장을 입력했지만 클릭하지 못했습니다. 초안을 그대로 두세요.\r\n" + operatingAdvice;
                if (ActiveSendAttempted) return "상태 답장의 Send 클릭이 시도됐습니다. 실제 전송 여부는 휴대폰에서 확인하세요.\r\n" + operatingAdvice;
                return "프로그램의 텍스트 입력·Send 클릭은 시도되지 않았습니다.\r\n" +
                    (ActiveCursorMoveAttempted ? "커서 이동은 시도됐으며 자동 복원하지 않습니다.\r\n" : "") + operatingAdvice;
            }
            var advice = (pcStatus ? "휴대폰에서 " + ReportPrefix + " 답장 한 건과 내용을 확인하고, LOG READY 후 앱을 켜 둔 채 로그를 첨부하세요.\r\n"
                : "휴대폰 배달은 프로그램이 확인하지 못합니다. 두 D답장의 수신 횟수와 로그 하나를 보내세요.\r\n") +
                "채팅창과 초안을 그대로 두세요. 수동 전송·초안 삭제·재실행은 하지 않습니다.";
            if (ActivePendingWrite)
                return (pcStatus ? "PC 상태를 입력했지만 클릭하지 못했습니다. 초안을 그대로 두세요.\r\n"
                    : "입력 후 클릭하지 못한 회차가 있습니다. 이전 답장은 이미 전송됐을 수 있습니다.\r\n") + advice;
            if (ActiveSendAttempted) return (pcStatus ? "Send 클릭 1회가 시도됐습니다. 실제 전송 여부는 휴대폰에서 확인하세요.\r\n"
                : "최대 두 번의 클릭 중 일부가 수행됐습니다. 실제 전송 여부는 휴대폰에서 확인하세요.\r\n") + advice;
            return "프로그램의 텍스트 입력·Send 클릭은 시도되지 않았습니다.\r\n" +
                (ActiveCursorMoveAttempted ? "커서 이동은 시도됐으며 자동 복원하지 않습니다.\r\n" : "") + advice;
        }

        private void Stop(string reason)
        {
            stopped = true;
            session?.Cancel();
            statusSession?.Cancel();
            generation++;
            countdown.Stop();
            countdownDisplay.Stop();
            confirmation.Checked = false;
            if (roundTrip) code.Text = "WAIT";
            SetStatus("중단됨 — " + reason, false);
            if (busy) SetInteractionNotice(operating ? "중단 요청 중 — 호출 종료 후 LOG READY" : "중단 요청 중 — 호출이 끝나고 마스터 LOG READY가 뜰 때까지 기다리세요.");
            details.Text = roundTrip
                ? (busy ? "진행 중인 호출의 반환을 기다립니다. Stop은 이미 수행한 입력·클릭을 되돌리지 않습니다.\r\n" : "") + OutcomeAdvice()
                : "READ ONLY: no PC input, mouse action or reply.\r\n" +
                    (busy ? "Waiting for the current accessibility read to return; Stop cannot forcibly abort it.\r\n" : "") +
                    "Collect the log. Do not send another phone message or repeat this session.";
            var explanation = Explain(reason);
            if (!string.IsNullOrEmpty(explanation)) details.AppendText("\r\n" + reason + " — " + explanation);
            details.AppendText("\r\n" + RestartAdvice);
            try { log.Write("INFO", "RECEIVE_UI_STOP", AuditLog.Field("busy", busy), AuditLog.Field("read_only", !roundTrip),
                AuditLog.Field("environment_changed", EnvironmentChanged(approvedEnvironmentRevision)),
                AuditLog.Field("environment_reason", environmentReason),
                AuditLog.Field("operating_status", operating), AuditLog.Field("completed_rounds", CompletedRoundCount),
                AuditLog.Field("plain_commands", plainCommands),
                AuditLog.Field("cursor_move_attempted", ActiveCursorMoveAttempted),
                AuditLog.Field("write_attempted", ActiveWriteAttempted), AuditLog.Field("send_attempted", ActiveSendAttempted)); } catch { }
            if (!busy) ReleaseLogFile(); // Busy Stop still needs the worker's final diagnostic records.
            UpdateButtons();
        }

        // 사유 코드는 그대로 두고 다음 행동만 덧붙입니다. 모르는 코드는 null이며 코드만 표시합니다.
        private static string Explain(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return null;
            switch (reason)
            {
                case "RECEIVE_NO_TARGET":
                    return "5초 안에 KI-Messenger의 나와의 대화 창을 클릭하지 않았습니다. 이 창을 닫고 다시 연 뒤 Start를 누르고 5초 안에 대화 창 제목 표시줄을 한 번 클릭하세요.";
                case "PROBE_NOT_KI_MESSENGER":
                    return "선택한 창이 KI-Messenger가 아닙니다. 새 세션에서 나와의 대화 창을 클릭하세요.";
                case "TARGET_INITIAL_FOREGROUND_REQUIRED":
                    return "선택 직후 다른 창이 앞으로 나와 세션을 시작하지 못했습니다. 알림·팝업을 닫고 새 세션을 시작하세요.";
                case "TARGET_ACTIVATION_REJECTED":
                case "TARGET_RESTORE_REJECTED":
                    return "Windows가 대화창을 앞으로 가져오거나 복원하는 것을 거부했습니다. 전체 화면 프로그램·설치 마법사·UAC 창을 닫고 새 세션을 시작하세요. 자동 재시도는 하지 않습니다.";
                case "TARGET_FOREGROUND_INTERFERED":
                case "TARGET_FOREGROUND_NOT_ACQUIRED":
                case "TARGET_INPUT_CHANGED":
                    return "회신 입력 직전에 다른 창이 앞으로 나오거나 PC 입력이 감지됐습니다. PC 조작을 멈춘 뒤 새 세션을 시작하세요.";
                case "TARGET_PC_NOT_IDLE":
                    return "60초 동안 PC 입력 정지 조건이 충족되지 않았습니다. 로그의 OPERATIONAL_TARGET_IDLE_WAIT 항목(reason·input_age_ms·held_keys·gui_*)이 원인을 가리킵니다. 마우스 흔들림 방지 프로그램·원격 제어 도구·눌린 키를 확인한 뒤 새 세션을 시작하세요.";
                case "TARGET_INPUT_DESKTOP_UNAVAILABLE":
                case "TARGET_INPUT_DESKTOP_INSECURE":
                    return "잠금 화면·보안 데스크톱 상태여서 입력할 수 없습니다. 잠금을 해제한 뒤 새 세션을 시작하세요. 자동 재개는 하지 않습니다.";
                case "RECEIVE_READY_BOUNDARY_CHANGED":
                    return "메신저 화면의 Master Ready 표시가 접수 당시와 달라져 회신을 중단했습니다. 남은 부분은 보내지 않았습니다.";
                case "RECEIVE_READY_NOT_OBSERVED":
                case "RECEIVE_READY_NOT_APPENDED":
                    return "보낸 Master Ready가 대화 기록의 마지막 줄로 확인되지 않았습니다. 메신저 연결 상태를 확인한 뒤 새 세션을 시작하세요.";
                case "RECEIVE_WAIT_TIME_LIMIT":
                    return "60초 안에 새 메시지가 도착하지 않았습니다. READY 뒤 휴대폰에서 한 번 보낸 다음 새 세션을 시작하세요.";
                case "RECEIVE_PHASE_TIME_LIMIT":
                    return "화면 읽기 한 단계가 15초를 넘었습니다. 대화 기록이 매우 길면 새 대화를 사용하세요.";
                case "STATUS_REQUEST_STOPPED":
                    return "이번 요청이 중단되어 다음 요청을 시작하지 않았습니다.";
                case "STATUS_TARGET_CHANGED":
                case "STATUS_WINDOW_MOVED":
                case "STATUS_PROCESS_CHANGED":
                case "TARGET_WINDOW_MOVED":
                case "TARGET_WINDOW_OR_PID_CHANGED":
                case "TARGET_PROCESS_IDENTITY_CHANGED":
                    return "선택한 대화창이 닫히거나 이동·변경되었습니다. 창을 움직이지 말고 새 세션을 시작하세요.";
                case "STATUS_SESSION_FAILED":
                case "RECEIVE_FAILED":
                case "RECEIVE_BEGIN_FAILED":
                    return "예상하지 못한 오류로 중단했습니다. 로그 폴더의 최신 로그를 첨부해 문의하세요.";
            }
            if (reason.StartsWith("TARGET_ROOT_", StringComparison.Ordinal))
                return "선택한 대화창이 닫히거나 이동·변경되었습니다. 창을 움직이지 말고 새 세션을 시작하세요.";
            if (reason.StartsWith("RECEIVE_HISTORY_", StringComparison.Ordinal))
                return "대화 기록의 구조가 바뀌어 안전을 위해 중단했습니다. 메시지 삭제·재정렬이 없었는지 확인하고 새 세션을 시작하세요.";
            if (reason.StartsWith("ENVIRONMENT_CHANGED", StringComparison.Ordinal))
                return "잠금·세션 전환·절전이 감지되어 중단했습니다. 자동으로 다시 시작하지 않습니다.";
            return null;
        }

        private void SetStatus(string message, bool ready)
        {
            status.Text = "상태: " + message;
            status.ForeColor = ready && !SystemInformation.HighContrast ? Color.DarkGreen : SystemColors.ControlText;
        }

        private void SetInteractionNotice(string message, bool finished = false)
        {
            if (!roundTrip) return;
            replyCode.Text = message;
            replyCode.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : finished ? Color.DarkBlue : Color.DarkRed;
            replyCode.BackColor = finished ? SystemColors.Control : SystemInformation.HighContrast ? SystemColors.Window : Color.LightYellow;
        }

        private void UpdateButtons()
        {
            var available = !started && !busy && !closing && !auditFailed && environmentReady;
            confirmation.Enabled = available;
            start.Enabled = available && confirmation.Checked;
            stop.Enabled = busy || countdown.Enabled;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Threading.Interlocked.Exchange(ref disposed, 1);
                stopped = true;
                Threading.Volatile.Read(ref session)?.Cancel();
                Threading.Volatile.Read(ref statusSession)?.Cancel();
                DetachEnvironmentEvents();
                countdown.Dispose();
                countdownDisplay.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
