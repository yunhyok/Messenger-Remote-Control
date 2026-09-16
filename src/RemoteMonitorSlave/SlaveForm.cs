using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RemoteMonitorLink;

namespace RemoteMonitorSlave
{
    internal sealed class SlaveForm : Form
    {
        private readonly ComboBox address = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button start = new Button { Text = "Slave 시작" };
        private readonly Button stop = new Button { Text = "Stop" };
        private readonly Button export = new Button { Text = "연결파일 저장" };
        private readonly Button refresh = new Button { Text = "현재 목록 새로고침" };
        private readonly Button powerSiCheck = new Button { Text = "PowerSI 확인" };
        private readonly Button outputAll = new Button { Text = "PowerSI 전체 수집" };
        private readonly Button replayVision = new Button { Text = "저장 화면 재판독" };
        private readonly Button visionSetup = new Button { Text = "LM Studio 설정" };
        private readonly Button visionPreview = new Button { Text = "캡처 / 판독 원문 보기" };
        private readonly ComboBox outputTargets = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        // One batch, separate evidence per process. Never compare one instance's OCR with another's buffer.
        private sealed class OutputSample
        {
            internal ProcessState Process;
            internal int SessionId;
            internal OutputBufferResult Buffer;
            internal DateTime ReceivedUtc;
            internal PowerSiObservation Vision;
            internal readonly List<PowerSiObservation> Runs = new List<PowerSiObservation>();
            internal byte[] FailureFrame;
            internal string Failure;
            public override string ToString() { return "PID " + Process.Pid + " / " + (Buffer?.Code ?? "대기") + " / OCR " +
                (Vision?.LocalVisibleEmpty == true ? "보이는 내용 없음" : Vision?.LocalFailure != null ? "실패 (상세 확인)" : Vision?.Code ?? "없음"); }
        }
        private readonly List<OutputSample> outputSamples = new List<OutputSample>();
        private OutputSample selectedSample;
        private bool batchInProgress;
        private LocalVisionSettings visionSettings = new LocalVisionSettings();
        private PowerSiObservation lastObservation;
        private readonly List<PowerSiObservation> comparisonRuns = new List<PowerSiObservation>();
        private OutputBufferResult lastBuffer;
        private DateTime lastBufferReceivedUtc;
        private string lastAutoCopyCode;      // Outcome code of the most recent auto-copy attempt, for the status line.
        // The worker capture of the most recent FAILED auto copy (memory only, last one kept) and its code/detail.
        // It is what explains an abort, so it stays available for the diagnostic bundle until a newer failure.
        private byte[] autoCopyFrame;
        private string autoCopyFailure;
        private readonly Dictionary<PowerSiObservation, string> comparisonReports = new Dictionary<PowerSiObservation, string>();
        private readonly System.Windows.Forms.Timer remoteProgress = new System.Windows.Forms.Timer { Interval = 1000 };
        private Stopwatch remoteClock;
        private readonly Label state = new Label();
        private readonly Label anchorState = new Label();
        private readonly Label snapshot = new Label();
        private readonly TextBox powerSi = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, TabStop = false };
        private readonly DataGridView processes = new DataGridView
        {
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeColumns = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            MultiSelect = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            ScrollBars = ScrollBars.Vertical,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            TabStop = false
        };
        private readonly TextBox pairing = new TextBox { ReadOnly = true, UseSystemPasswordChar = true };
        private readonly string dataPath;
        private readonly SlaveLog log;
        private SlaveIdentity identity;
        private StatusServer server;
        private Action<string> serverActivity;
        private Action<MachineStatus> serverSnapshot;
        private CancellationTokenSource snapshotCancellation;
        private bool busy, closing, replayInProgress;
        private string pairingText;
        private int statusReplies;
        private const string MasterCompletionNotice = "메신저 운용 시에는 Master LOG READY까지 마스터 마우스·키보드를 건드리지 마세요.";

        internal SlaveForm(string testDirectory = null)
        {
            Text = Program.Title + " - READ ONLY";
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(840, 812);
            AutoScroll = true;
            AutoScrollMinSize = ClientSize;
            MinimumSize = new Size(640, 480);
            StartPosition = FormStartPosition.CenterScreen;
            dataPath = testDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteMonitorSlave");
            log = new SlaveLog(Path.Combine(dataPath, "logs"));
            try { visionSettings = VisionSettingsStore.Load(Path.Combine(dataPath, "local-vision.json")); }
            catch { log.Write("VISION_SETTINGS_INVALID"); }
            Controls.Add(new Label { Text = Text, Font = new Font(Font, FontStyle.Bold), Bounds = new Rectangle(18, 16, 804, 28) });
            Controls.Add(new Label { Text = "Slave를 시작하면 Master의 pwrsi 요청마다 모든 PowerSI를 한 번 수집합니다.\r\n" +
                "Pending은 추가 수집 없이 회신합니다. 로컬 전체 수집과 저장한 화면 비교도 사용할 수 있습니다.",
                Bounds = new Rectangle(18, 52, 804, 56) });
            address.SetBounds(18, 120, 246, 28);
            foreach (var ip in NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses).Select(item => item.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)).Distinct())
                address.Items.Add(ip.ToString());
            address.Items.Add("127.0.0.1");
            address.SelectedIndex = -1;
            address.AccessibleName = "Slave 수신용 이 PC의 IPv4 주소";
            start.SetBounds(276, 117, 130, 34);
            stop.SetBounds(418, 117, 100, 34);
            export.SetBounds(530, 117, 152, 34);
            powerSiCheck.SetBounds(694, 117, 128, 34);
            powerSiCheck.AccessibleName = "PowerSI 화면을 같은 PC의 LM Studio로 판독";
            state.SetBounds(18, 164, 804, 48);
            ShowActivity("STOPPED — 수신 IP를 선택하고 Slave를 시작하세요. 로컬 PowerSI 수집은 별도로 실행할 수 있습니다.");
            outputAll.SetBounds(18, 218, 150, 32);
            outputAll.AccessibleName = "새 PowerSI 화면 캡처와 Output 전체 텍스트 수집을 함께 실행하여 비교";
            replayVision.SetBounds(180, 218, 150, 32);
            pairing.SetBounds(342, 222, 480, 26);
            pairing.AccessibleName = "인증 연결 코드 (숨김)";
            snapshot.SetBounds(180, 254, 642, 56);
            snapshot.Text = "현재 세션 프로그램 목록은 아직 읽지 않았습니다. 새로고침은 이 PC에서 읽기만 하며 수신을 시작하지 않습니다.";
            refresh.SetBounds(18, 267, 150, 32);
            processes.SetBounds(18, 318, 804, 196);
            processes.AccessibleName = "현재 세션 프로그램 개요";
            processes.Columns.Add("pid", "PID");
            processes.Columns.Add("name", "프로그램");
            processes.Columns.Add("window", "창");
            processes.Columns.Add("cpu", "CPU (%)");
            processes.Columns.Add("ram", "RAM (MiB)");
            processes.Columns.Add("age", "프로세스 경과");
            processes.Columns[0].Width = 70;
            processes.Columns[1].Width = 280;
            processes.Columns[2].Width = 60;
            processes.Columns[3].Width = 100;
            processes.Columns[4].Width = 110;
            processes.Columns[5].Width = 160;
            foreach (DataGridViewColumn column in processes.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            processes.SelectionChanged += delegate { if (processes.SelectedRows.Count != 0) processes.ClearSelection(); };
            outputTargets.SetBounds(18, 522, 400, 28);
            outputTargets.AccessibleName = "PowerSI 인스턴스별 Output 결과 선택 (PID)";
            outputTargets.SelectedIndexChanged += delegate { SelectOutputSample(outputTargets.SelectedItem as OutputSample); };
            visionSetup.SetBounds(426, 517, 156, 32);
            visionPreview.SetBounds(594, 517, 228, 32); visionPreview.Enabled = false;
            powerSi.SetBounds(18, 554, 804, 114);
            powerSi.AccessibleName = "PowerSI Output 최근 로그 전사본";
            var path = new TextBox { Text = log.Path, ReadOnly = true, Bounds = new Rectangle(18, 682, 650, 25) };
            var folder = new Button { Text = "Open Log Folder", Bounds = new Rectangle(680, 678, 142, 32) };
            anchorState.SetBounds(18, 764, 804, 40);
            anchorState.AccessibleName = "자동 Output 탐색·복사 및 Windows 응답 상태";
            Controls.AddRange(new Control[] { address, start, stop, export, powerSiCheck, outputAll, replayVision, refresh, state, pairing, snapshot, processes, outputTargets, powerSi, path, folder, visionSetup, visionPreview, anchorState });
            Controls.Add(new Label { Text = "화면·전체 원문은 Slave에 보관하고 최근 Output 발췌만 사내 Master와 메신저로 전달합니다. 수집은 한 번씩, 최대100초이며 Stop으로 취소할 수 있습니다. 이미지 판독은 이 PC의 LM Studio만 사용합니다.",
                Bounds = new Rectangle(18, 720, 804, 42) });
            start.Click += async delegate { await StartServer(); };
            address.SelectedIndexChanged += delegate { UpdateButtons(); };
            stop.Click += delegate { StopServer(); };
            export.Click += delegate { ExportPairing(); };
            refresh.Click += async delegate { await RefreshSnapshot(); };
            powerSiCheck.Click += async delegate { await ReadOutputBuffer(); };
            outputAll.Click += async delegate { await ReadOutputBuffer(); };
            replayVision.Click += async delegate { await ReplayVision(); };
            visionSetup.Click += delegate { ConfigureVision(); };
            visionPreview.Click += delegate { ShowVisionPreview(); };
            folder.Click += delegate { try { Process.Start("explorer.exe", Path.GetDirectoryName(log.Path)); } catch { } };
            FormClosing += delegate { closing = true; CancelSnapshotRefresh(); StopServer(); };
            remoteProgress.Tick += delegate
            {
                if (server != null && remoteClock != null)
                    ShowActivity("Master 요청 PowerSI 판독 중 — " + (int)remoteClock.Elapsed.TotalSeconds + "초 경과 / 최대100초; Stop 가능");
            };
            UpdateAnchorState();
            UpdateButtons();
        }

        private async Task StartServer()
        {
            if (busy || batchInProgress || closing || server != null || snapshotCancellation != null || address.SelectedIndex < 0) return;
            var bind = IPAddress.Parse((string)address.SelectedItem);
            busy = true;
            UpdateButtons();
            ClearPowerSi();
            ShowActivity("Slave 시작 준비 중…");
            SlaveIdentity prepared = null;
            try
            {
                prepared = await Task.Run(() => SlaveIdentity.LoadOrCreate(Path.Combine(dataPath, "identity.dat")));
                if (closing) { prepared.Dispose(); return; }
                identity = prepared;
                var running = new StatusServer(identity, bind, 45831, visionSettings, CollectRemoteOutput);
                Action<string> activity = code => ServerActivity(running, code);
                Action<MachineStatus> captured = status => ServerSnapshot(running, status);
                statusReplies = 0;
                server = running;
                serverActivity = activity;
                serverSnapshot = captured;
                running.Activity += activity;
                running.StatusCaptured += captured;
                running.Start();
                pairingText = identity.CreatePairing(bind, running.Port);
                pairing.Text = pairingText;
                log.Write("LISTEN_STARTED");
                _ = WatchServer(running);
            }
            catch (Exception ex)
            {
                if (!closing)
                {
                    StopServer();
                    ShowActivity("시작 실패: " + ex.GetType().Name + " — 선택한 IP, 포트 사용 여부와 로그를 확인하세요.");
                    try { log.Write("LISTEN_FAILED"); } catch { }
                }
            }
            finally { busy = false; if (!closing) UpdateButtons(); }
        }

        private async Task WatchServer(StatusServer running)
        {
            try { await running.Completion; }
            catch { try { log.Write("LISTENER_FAILED"); } catch { } }
            if (!closing && ReferenceEquals(server, running))
            {
                StopServer();
            }
        }

        private void StopServer()
        {
            remoteProgress.Stop(); remoteClock = null;
            bool preserveComparison = batchInProgress || (replayInProgress && lastBuffer != null && lastObservation?.LocalFrame != null);
            CancelSnapshotRefresh();
            replayInProgress = false;
            var oldServer = server;
            var oldActivity = serverActivity;
            var oldSnapshot = serverSnapshot;
            server = null;
            serverActivity = null;
            serverSnapshot = null;
            if (oldServer != null && oldActivity != null) oldServer.Activity -= oldActivity;
            if (oldServer != null && oldSnapshot != null) oldServer.StatusCaptured -= oldSnapshot;
            oldServer?.Dispose();
            identity?.Dispose();
            identity = null;
            pairingText = null;
            pairing.Clear();
            if (!preserveComparison) ClearPowerSi();
            ShowActivity(batchInProgress ? "STOPPING — 입력 작업 정리 중. 완료된 PID 결과는 유지합니다." : preserveComparison ? "STOPPED — 재판독 취소. 마지막 완료 결과를 유지했습니다. 같은 화면으로 다시 판독할 수 있습니다." :
                "STOPPED — 수신을 중지했습니다. 로그를 확인하거나 첨부할 수 있습니다.");
            try { log.Write(oldServer == null ? "UI_STOP" : "LISTEN_STOPPED"); } catch { }
            UpdateButtons();
        }

        private void ServerActivity(StatusServer source, string code)
        {
            try { log.Write(code); } catch { }
            DispatchServerUpdate(source, () => ApplyActivity(source, code));
        }

        private void ServerSnapshot(StatusServer source, MachineStatus captured)
        {
            if (ReferenceEquals(server, source) && captured != null && captured.PowerSi != null)
                try { log.WritePowerSi(captured.PowerSi); } catch { }
            DispatchServerUpdate(source, () => ApplySnapshot(source, captured));
        }

        private void DispatchServerUpdate(StatusServer source, Action update)
        {
            if (closing || IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke(update);
            }
            catch (InvalidOperationException) { }
        }

        private void ApplyActivity(StatusServer source, string code)
        {
            if (closing || IsDisposed || Disposing || !ReferenceEquals(server, source)) return;
            switch (code)
            {
                case "LISTENING":
                    ShowActivity("LISTENING — " + address.SelectedItem + ":" + source.Port + " 수신 중");
                    break;
                case "CLIENT_CONNECTED":
                    ShowActivity("연결 요청 수신 — 인증 확인 중…");
                    break;
                case "STATUS_CAPTURING":
                    ClearPowerSi();
                    ShowActivity("프로그램 상태 수집 중 (CPU 표본 약0.5초)");
                    break;
                case "PWRSI_CAPTURING":
                    ClearPowerSi();
                    remoteClock = Stopwatch.StartNew(); remoteProgress.Start();
                    ShowActivity("PowerSI 상태 수집 중 — Pending 확인 후 Output 수집 (최대100초; Stop 가능)");
                    break;
                case "STATUS_SENT":
                    remoteProgress.Stop(); remoteClock = null;
                    ShowActivity("마스터로 상태 응답 완료 (" + (++statusReplies) + "회, " + DateTime.Now.ToString("HH:mm:ss") + ")");
                    break;
                case "CLIENT_REJECTED":
                    remoteProgress.Stop(); remoteClock = null;
                    ShowActivity("연결 요청 거부됨 — 인증/형식을 확인하세요. 계속 수신 대기 중");
                    break;
                case "STOPPED":
                    remoteProgress.Stop(); remoteClock = null;
                    ClearPowerSi();
                    ShowActivity("STOPPED — 수신 대기가 종료됐습니다.");
                    break;
            }
        }

        private Task RefreshSnapshot()
        {
            return RefreshSnapshot(false);
        }

        private async Task RefreshSnapshot(bool powerSiOnly)
        {
            if (closing || busy || snapshotCancellation != null) return;
            var cancellation = new CancellationTokenSource();
            snapshotCancellation = cancellation;
            ClearPowerSi();
            snapshot.Text = powerSiOnly ? "로컬 PowerSI 화면 → 이 PC의 LM Studio. Slave 수신은 시작하지 않습니다." :
                "현재 세션 프로그램 목록을 읽는 중입니다. 수신은 시작하지 않습니다.";
            if (powerSiOnly)
            {
                try { log.Write("LOCAL_PWRSI_BEGIN"); } catch { }
                ShowActivity("로컬 PowerSI 확인 중 — 캡처 후 LM Studio 판독 (최대100초; Stop 가능)");
            }
            var elapsed = Stopwatch.StartNew();
            var progress = new System.Windows.Forms.Timer { Interval = 1000 };
            progress.Tick += delegate
            {
                if (powerSiOnly && ReferenceEquals(snapshotCancellation, cancellation))
                    ShowActivity("로컬 PowerSI 확인 중 — " + (int)elapsed.Elapsed.TotalSeconds + "초 경과 / 최대100초; Stop 가능");
            };
            if (powerSiOnly) progress.Start();
            UpdateButtons();
            try
            {
                var result = await Task.Run(async () =>
                {
                    var sampledAt = DateTime.Now;
                    var inventory = await ProcessInventory.CaptureAsync(cancellation.Token, powerSiOnly).ConfigureAwait(false);
                    var observation = powerSiOnly
                        ? await PowerSiVision.CaptureAsync(inventory, visionSettings, cancellation.Token).ConfigureAwait(false)
                        : null;
                    return Tuple.Create(inventory, observation, sampledAt);
                });
                if (!RenderLocalSnapshot(cancellation, result.Item1, result.Item2, powerSiOnly, result.Item3))
                {
                    if (powerSiOnly)
                    {
                        try { log.Write("LOCAL_PWRSI_FAILED"); } catch { }
                    }
                    return;
                }
                if (powerSiOnly)
                {
                    try
                    {
                        log.WritePowerSi(result.Item2);
                        log.Write("LOCAL_PWRSI_COMPLETE");
                    }
                    catch { try { log.Write("LOCAL_PWRSI_FAILED"); } catch { } }
                    ShowActivity("로컬 PowerSI 확인 완료 — " + result.Item2.Code + " / " + elapsed.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "초. 로그 첨부 가능");
                }
            }
            catch (OperationCanceledException)
            {
                if (powerSiOnly)
                {
                    try { log.Write("LOCAL_PWRSI_FAILED"); } catch { }
                }
            }
            catch (Exception)
            {
                if (powerSiOnly)
                {
                    try { log.Write("LOCAL_PWRSI_FAILED"); } catch { }
                }
                if (!closing && ReferenceEquals(snapshotCancellation, cancellation))
                {
                    processes.Rows.Clear();
                    ClearPowerSi();
                    snapshot.Text = powerSiOnly ? "로컬 PowerSI 확인을 표시하지 못했습니다. 미제공 — 실행 중은 완료 여부가 아닙니다." :
                        "현재 세션 프로그램 목록을 표시하지 못했습니다. 미제공 — 실행 중은 완료 여부가 아닙니다.";
                    if (powerSiOnly) ShowActivity("로컬 PowerSI 확인 실패 — 로그를 확인하세요.");
                }
            }
            finally
            {
                progress.Dispose();
                bool current = ReferenceEquals(snapshotCancellation, cancellation);
                if (current) snapshotCancellation = null;
                cancellation.Dispose();
                if (current && !closing && !IsDisposed) UpdateButtons();
            }
        }

        private bool RenderLocalSnapshot(CancellationTokenSource cancellation, ProcessInventory inventory,
            PowerSiObservation observation, bool powerSiOnly, DateTime? sampledAt = null)
        {
            if (closing || !ReferenceEquals(snapshotCancellation, cancellation)) return false;
            if (inventory == null) throw new InvalidDataException("Process snapshot is missing.");
            inventory.Validate();
            if (powerSiOnly && observation == null) throw new InvalidDataException("PowerSI observation is missing.");
            RenderSnapshot(inventory, sampledAt ?? DateTime.Now, powerSiOnly ? "로컬 PowerSI 확인" : "로컬 새로고침");
            RenderPowerSi(powerSiOnly ? observation : null);
            return true;
        }

        private void ApplySnapshot(StatusServer source, MachineStatus captured)
        {
            if (closing || IsDisposed || Disposing || !ReferenceEquals(server, source)) return;
            CancelSnapshotRefresh(); // A current Master snapshot wins over a pending local read.
            UpdateButtons();
            var inventory = captured == null ? null : captured.Processes;
            if (inventory == null)
            {
                processes.Rows.Clear();
                ClearPowerSi();
                snapshot.Text = "인증된 Master 요청 " + DateTime.Now.ToString("HH:mm:ss") +
                    " — 프로그램 목록 미제공. 미제공 — 실행 중은 완료 여부가 아닙니다.";
                return;
            }
            try
            {
                inventory.Validate();
                RenderSnapshot(inventory, captured.LocalTime, captured.PowerSiOnly
                    ? "Master 요청 — POWERSI/PWRSI만" : "Master 요청 — 현재 세션 전체");
                if (captured.PowerSiReport != null)
                {
                    captured.PowerSiReport.Validate();
                    snapshot.Text += "\r\n보고 대상 " + captured.PowerSiReport.Targets.Length + "개 / Pending " +
                        captured.PowerSiReport.Targets.Count(item => item.State == "PENDING") +
                        (captured.PowerSiReport.Partial ? " / 일부 미확인" : " / 수집 종료");
                    foreach (DataGridViewRow row in processes.Rows)
                        if (captured.PowerSiReport.Targets.Any(item => item.Pid == (int)row.Cells[0].Value && item.State == "PENDING"))
                        {
                            row.Cells[2].Value = "Pending";
                            for (var column = 3; column < 6; column++) row.Cells[column].Value = "—";
                        }
                }
                else RenderPowerSi(captured.PowerSiOnly ? captured.PowerSi : null);
            }
            catch (Exception)
            {
                processes.Rows.Clear();
                ClearPowerSi();
                snapshot.Text = "인증된 Master 요청의 프로그램 목록을 표시하지 못했습니다. 미제공 — 실행 중은 완료 여부가 아닙니다.";
            }
        }

        private void RenderSnapshot(ProcessInventory inventory, DateTime sampledAt, string source)
        {
            processes.Rows.Clear();
            foreach (var item in inventory.Items)
                processes.Rows.Add(item.Pid, item.FullName ?? item.Name ?? "?", item.HasWindow ? "있음" : "없음",
                    FormatCpu(item.CpuPermille), FormatMiB(item.WorkingSetMiB), FormatAge(item.AgeSeconds));
            processes.ClearSelection();
            snapshot.Text = string.Format(CultureInfo.InvariantCulture,
                "{0} {1:HH:mm:ss} — 표시 {2}개 / 더 있음 {3}개 / 읽지 못함 {4}개\r\n" +
                "CPU는 전체 CPU 기준, 0.1% 단위. 경과는 프로세스 시작 후. 미제공 — 실행 중은 완료 여부가 아닙니다.",
                source, sampledAt, inventory.Items.Length, inventory.Omitted, inventory.Unreadable);
        }

        // ---------------------------------------------------------------- learned Output position (auto copy)

        private void UpdateAnchorState()
        {
            anchorState.Text = "자동 복사: 매번 Output 영역 확인 / 최근 결과: " + (lastAutoCopyCode ?? "없음") +
                " / 자동 복사 설정: " + (visionSettings.AutoCopyEnabled ? "사용" : "꺼짐") + Environment.NewLine +
                "PowerSI 크기 변경 없음. 응답 없음(PENDING)은 입력 없이 건너뜁니다. 내부 시뮬레이션 pending 판별은 미구현입니다.";
        }

        // Called only AFTER the exact PowerSI window has been validated and activated. Never expose an
        // unrelated background app merely because preparation failed. Only our own forms change state.
        private List<KeyValuePair<Form, FormWindowState>> MinimizeForAutoCopy()
        {
            var hidden = new List<KeyValuePair<Form, FormWindowState>>();
            foreach (var owned in OwnedForms)
            {
                if (owned == null || owned.IsDisposed || !owned.Visible) continue;
                hidden.Add(new KeyValuePair<Form, FormWindowState>(owned, owned.WindowState));
                try { owned.WindowState = FormWindowState.Minimized; } catch { }
            }
            hidden.Add(new KeyValuePair<Form, FormWindowState>(this, WindowState));
            try { WindowState = FormWindowState.Minimized; } catch { }
            return hidden;
        }

        private void RestoreAfterAutoCopy(List<KeyValuePair<Form, FormWindowState>> hidden)
        {
            if (hidden == null) return;
            for (int index = hidden.Count - 1; index >= 0; index--)
            {
                var form = hidden[index].Key;
                if (form == null || form.IsDisposed) continue;
                try { form.WindowState = hidden[index].Value; } catch { }
            }
        }

        // Log values stay fixed codes, integers and pipe-separated metadata; anything else becomes NONE.
        private static string LogValue(string text)
        {
            return text != null && System.Text.RegularExpressions.Regex.IsMatch(text, @"\A[A-Za-z0-9_|]{1,256}\z") ? text : "NONE";
        }

        private void RenderPowerSi(PowerSiObservation observation)
        {
            comparisonReports.Clear();
            comparisonRuns.Clear();
            lastBuffer = null;
            visionPreview.Text = "캡처 / 판독 원문 보기";
            lastObservation = observation;
            powerSi.Text = observation == null ? string.Empty :
                (observation.IsVision && observation.LocalFailure == null && observation.LocalEvidence != null ? observation.LocalEvidence : observation.Summary);
            if (!string.IsNullOrEmpty(observation?.LocalModel)) powerSi.AppendText(Environment.NewLine + "Local model: " + observation.LocalModel);
            if (!string.IsNullOrEmpty(observation?.LocalFailure)) powerSi.AppendText(Environment.NewLine + "Local detail: " + observation.LocalFailure);
            visionPreview.Enabled = observation?.LocalImage != null || observation?.LocalFullImage != null;
            UpdateButtons();
        }

        private void ClearPowerSi()
        {
            selectedSample = null;
            outputSamples.Clear();
            outputTargets.Items.Clear();
            comparisonReports.Clear();
            comparisonRuns.Clear();
            lastObservation = null;
            lastBuffer = null;
            visionPreview.Text = "캡처 / 판독 원문 보기";
            visionPreview.Enabled = false;
            powerSi.Clear();
            UpdateButtons();
        }

        private void SaveSelectedSample()
        {
            if (selectedSample == null) return;
            selectedSample.Buffer = lastBuffer;
            selectedSample.ReceivedUtc = lastBufferReceivedUtc;
            selectedSample.Vision = lastObservation;
            selectedSample.Runs.Clear();
            selectedSample.Runs.AddRange(comparisonRuns);
            selectedSample.FailureFrame = autoCopyFrame;
            selectedSample.Failure = autoCopyFailure;
        }

        private void SelectOutputSample(OutputSample sample)
        {
            if (sample == null || ReferenceEquals(sample, selectedSample)) return;
            SaveSelectedSample();
            selectedSample = sample;
            RenderOutputBuffer(sample.Buffer, sample.Vision, sample.ReceivedUtc);
            comparisonRuns.Clear();
            comparisonRuns.AddRange(sample.Runs);
            autoCopyFrame = sample.FailureFrame;
            autoCopyFailure = sample.Failure;
            lastAutoCopyCode = sample.Buffer?.Code;
            UpdateAnchorState();
        }

        private static void RecordTargetFailure(OutputSample sample, string code)
        {
            if (IsPending(code))
            {
                sample.Buffer = new OutputBufferResult { Code = "SC_PENDING", Method = "NONE", Detail = "NONE" };
                sample.Vision = PowerSiObservation.VisionUnavailable("VISION_CAPTURE_FAILED");
                sample.Vision.LocalFailure = "SC_PENDING";
                return;
            }
            if (sample.Buffer?.Code == "OUTPUT_VISIBLE_EMPTY" && sample.Vision?.LocalVisibleEmpty == true) return;
            // OCR failure is not a failed copy: keep the independently collected full buffer and its UTC.
            if (sample.Buffer?.Text == null)
                sample.Buffer = new OutputBufferResult { Code = code, Method = "NONE", Detail = "NONE" };
            sample.Vision = sample.Vision ?? PowerSiObservation.VisionUnavailable("VISION_FAILED");
            sample.Vision.LocalFailure = code;
        }

        private static bool RecordVisibleEmpty(OutputSample sample, PowerSiObservation observation)
        {
            if (observation?.LocalVisibleEmpty != true) return false;
            sample.Vision = observation;
            sample.Buffer = new OutputBufferResult { Code = "OUTPUT_VISIBLE_EMPTY", Method = "SCREEN",
                Detail = "FULL_BUFFER_UNCONFIRMED" };
            return true;
        }

        private async Task<PowerSiReport> ReadOutputBuffer(ProcessInventory capturedInventory = null,
            bool remote = false, CancellationToken externalCancellation = default(CancellationToken))
        {
            if (closing || busy || batchInProgress || (!remote && server != null) || snapshotCancellation != null)
                return ReportFailure(capturedInventory, "BUSY");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
            snapshotCancellation = cancellation;
            ClearPowerSi();
            batchInProgress = true;
            UpdateButtons();
            var clock = Stopwatch.StartNew();
            var inventory = capturedInventory;
            try
            {
                log.Write("OUTPUT_BATCH_BEGIN");
                if (inventory == null) inventory = await ProcessInventory.CaptureAsync(cancellation.Token, true);
                cancellation.Token.ThrowIfCancellationRequested();
                inventory.Validate();
                RenderSnapshot(inventory, DateTime.Now, "PowerSI 전체 인스턴스 수집");
                foreach (var process in inventory.Items.OrderBy(item => item.Pid))
                    outputSamples.Add(new OutputSample { Process = process, SessionId = inventory.SessionId,
                        Buffer = new OutputBufferResult { Code = "NOT_ATTEMPTED", Method = "NONE", Detail = "NONE" },
                        ReceivedUtc = DateTime.UtcNow, Vision = PowerSiObservation.VisionUnavailable("OUTPUT_UNAVAILABLE") });
                for (int index = 0; index < outputSamples.Count; index++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var allowance = TargetAllowance(clock.ElapsedMilliseconds, outputSamples.Count - index);
                    if (allowance == 0) break;
                    var sample = outputSamples[index];
                    string prefix = (index + 1) + "/" + outputSamples.Count + " · PID " + sample.Process.Pid;
                    var target = new ProcessInventory { SessionId = sample.SessionId, Items = new[] { sample.Process } };
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                    {
                        // Each remaining target gets a share of the remaining 100-second collection budget.
                        deadline.CancelAfter(allowance);
                        var progress = new Progress<string>(stage =>
                        {
                            if (closing || cancellation.IsCancellationRequested || !ReferenceEquals(snapshotCancellation, cancellation)) return;
                            ShowActivity(prefix + " / " + stage + " / 전체 수집 최대100초, Stop 가능");
                        });
                        var targetClock = Stopwatch.StartNew();
                        try
                        {
                            sample.Vision = null;
                            log.Write("OUTPUT_TARGET_BEGIN", "pid=" + sample.Process.Pid + " start=" + (sample.Process.StartUtcTicks ?? 0));
                            ShowActivity(prefix + " / 창 확인 → Output 위치 찾기 → 자동 복사 → OCR");
                            PowerSiFrame frame = null;
                            PowerSiObservation located = null;
                            bool autoCopy = visionSettings.Enabled && visionSettings.AutoCopyEnabled;
                            List<KeyValuePair<Form, FormWindowState>> hidden = null;
                            try
                            {
                                // The response probe is independent of image inference and always precedes collection.
                                sample.Buffer = await ResponsiveStep(target, deadline.Token,
                                    () => OutputBufferCapture.ReadAsync(target, deadline.Token));
                                sample.ReceivedUtc = DateTime.UtcNow;
                                if (IsPending(sample.Buffer.Code)) throw new InvalidDataException("SC_PENDING");
                                if (!visionSettings.Enabled || (remote && sample.Buffer.Text != null))
                                {
                                    sample.Vision = PowerSiObservation.VisionUnavailable(visionSettings.Enabled ? "OUTPUT_UNAVAILABLE" : "VISION_NOT_CONFIGURED");
                                    continue;
                                }
                                if (new[] { "BUFFER_TIMEOUT", "BUFFER_WORKER_FAILED", "BUFFER_SIZE", "BUFFER_TOO_LARGE" }.Contains(sample.Buffer.Code))
                                    continue;
                                frame = autoCopy
                                    ? await ResponsiveStep(target, deadline.Token, () => PowerSiScreenCapture.PrepareAsync(target, deadline.Token,
                                        detail => log.Write("OUTPUT_PREPARE", "pid=" + sample.Process.Pid + " " + detail)))
                                    : await ResponsiveStep(target, deadline.Token, () => PowerSiScreenCapture.CaptureAsync(target, deadline.Token));
                                if (autoCopy)
                                {
                                    hidden = MinimizeForAutoCopy();
                                    await Task.Delay(300, deadline.Token);
                                }
                                // A timed-out text provider is not permission to try input against the same application.
                                bool copyAllowed = sample.Buffer.Text == null &&
                                    sample.Buffer.Code.StartsWith("BUFFER_", StringComparison.Ordinal) &&
                                    sample.Buffer.Code != "BUFFER_TIMEOUT" && sample.Buffer.Code != "BUFFER_WORKER_FAILED" &&
                                    sample.Buffer.Code != "BUFFER_SIZE" && sample.Buffer.Code != "BUFFER_TOO_LARGE";
                                if (copyAllowed && autoCopy)
                                {
                                    located = await ResponsiveStep(target, deadline.Token,
                                        () => PowerSiVision.CaptureAsync(target, visionSettings, deadline.Token, progress, frame, null, true,
                                            token => PowerSiScreenCapture.CheckResponsiveAsync(target, token)));
                                    if (IsPending(located.LocalFailure)) throw new InvalidDataException("SC_PENDING");
                                    sample.Runs.Add(located);
                                    log.WritePowerSi(located);
                                    sample.Vision = located; // Preserve the locator frame/diagnostics even when no click is allowed.
                                    var anchor = OutputAutoCopy.AnchorFromVision(target, located);
                                    if (RecordVisibleEmpty(sample, located)) { /* No input or OCR for a visibly empty pane. */ }
                                    else if (anchor != null)
                                    {
                                        log.Write("OUTPUT_AUTO_COPY_BEGIN", "pid=" + sample.Process.Pid + " self_hidden=1");
                                        sample.Buffer = await ResponsiveStep(target, deadline.Token,
                                            () => OutputAutoCopy.AutoCopyAsync(target, anchor, deadline.Token));
                                        sample.ReceivedUtc = DateTime.UtcNow;
                                        if (IsPending(sample.Buffer.Code)) throw new InvalidDataException("SC_PENDING");
                                        if (sample.Buffer.Code == "AUTO_COPY_READ" && sample.Buffer.Text != null)
                                        {
                                            frame = OutputAutoCopy.CleanFrameOf(sample.Buffer);
                                            located = PowerSiVision.ReframeOutput(located, frame);
                                            sample.Vision = located;
                                        }
                                        else
                                        {
                                            sample.FailureFrame = OutputAutoCopy.FrameOf(sample.Buffer);
                                            sample.Failure = sample.Buffer.Code + " " + sample.Buffer.Detail;
                                            located = null;
                                        }
                                    }
                                    else sample.Buffer = new OutputBufferResult { Code = "AUTO_COPY_REGION_UNCONFIRMED",
                                        Method = "NONE", Detail = LogValue(located?.LocalFailure) };
                                }
                            }
                            finally { RestoreAfterAutoCopy(hidden); }
                            deadline.Token.ThrowIfCancellationRequested();
                            if (remote && sample.Buffer.Text != null) continue; // A fresh exact copy needs no extra OCR request.
                            // Nonresponsive or failed input targets receive no more operations. Their captured diagnostics remain.
                            if (sample.Buffer.Code == "AUTO_COPY_READ")
                                sample.Vision = await ResponsiveStep(target, deadline.Token,
                                    () => PowerSiVision.CaptureAsync(target, visionSettings, deadline.Token, progress, frame, located, false,
                                        token => PowerSiScreenCapture.CheckResponsiveAsync(target, token)));
                            else if (sample.Buffer.Text != null || sample.Vision == null && !sample.Buffer.Code.StartsWith("SC_", StringComparison.Ordinal))
                                sample.Vision = await ResponsiveStep(target, deadline.Token,
                                    () => PowerSiVision.CaptureAsync(target, visionSettings, deadline.Token, progress, frame, null, false,
                                        token => PowerSiScreenCapture.CheckResponsiveAsync(target, token)));
                            if (IsPending(sample.Vision?.LocalFailure)) throw new InvalidDataException("SC_PENDING");
                            if (sample.Buffer.Text == null) RecordVisibleEmpty(sample, sample.Vision);
                        }
                        catch (OperationCanceledException)
                        {
                            if (cancellation.IsCancellationRequested)
                            {
                                RecordTargetFailure(sample, "TARGET_CANCELLED");
                                throw;
                            }
                            RecordTargetFailure(sample, "TARGET_TIMEOUT");
                        }
                        catch (Exception error) when (IsExpectedCollectionFailure(error))
                        {
                            RecordTargetFailure(sample, error is InvalidDataException ? LogValue(error.Message) : "TARGET_FAILED");
                        }
                        finally
                        {
                            sample.ReceivedUtc = sample.ReceivedUtc == default(DateTime) ? DateTime.UtcNow : sample.ReceivedUtc;
                            if (sample.Vision == null) sample.Vision = PowerSiObservation.VisionUnavailable("OUTPUT_UNAVAILABLE");
                            if (sample.Buffer.Code == "SC_PENDING")
                                sample.Vision.LocalFailure = "PENDING_WINDOWS_NOT_RESPONDING_INPUT_STOPPED";
                            if (!sample.Runs.Contains(sample.Vision)) sample.Runs.Add(sample.Vision);
                            log.WriteOutputBuffer(sample.Buffer);
                            log.WritePowerSi(sample.Vision);
                            log.Write("OUTPUT_TARGET_END", "pid=" + sample.Process.Pid + " code=" + LogValue(sample.Buffer.Code) + " elapsed_ms=" + targetClock.ElapsedMilliseconds);
                        }
                    }
                }
                int copied = outputSamples.Count(item => item.Buffer.Text != null);
                int visibleEmpty = outputSamples.Count(item => item.Vision?.LocalVisibleEmpty == true);
                ShowActivity("전체 완료 — " + outputSamples.Count + "개 중 원문 " + copied + "개 / 보이는 내용 없음 " + visibleEmpty + "개 / " +
                    clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                    "초. PID별 결과 확인 후 진단 ZIP 한 번 저장하세요." +
                    (inventory.Omitted > 0 ? " 목록 한도 밖 " + inventory.Omitted + "개는 미수집입니다." : ""));
                log.Write("OUTPUT_BATCH_COMPLETE", "targets=" + outputSamples.Count + " text=" + copied + " visible_empty=" + visibleEmpty + " omitted=" + inventory.Omitted);
                return BuildReport(inventory);
            }
            catch (OperationCanceledException)
            {
                if (!closing) ShowActivity("전체 수집 중지 — 완료된 결과는 유지하며, 미실행 대상은 NOT_ATTEMPTED입니다.");
                log.Write("OUTPUT_BATCH_CANCELLED");
                externalCancellation.ThrowIfCancellationRequested();
                return BuildReport(inventory, "CANCELLED");
            }
            catch (Exception error) when (IsExpectedCollectionFailure(error))
            {
                if (!closing) ShowActivity("전체 수집 실패 — 완료된 결과와 로그를 확인하세요.");
                log.Write("OUTPUT_BATCH_FAILED");
                return BuildReport(inventory, "CAPTURE_FAILED");
            }
            finally
            {
                cancellation.Cancel();
                if (ReferenceEquals(snapshotCancellation, cancellation)) snapshotCancellation = null;
                cancellation.Dispose();
                batchInProgress = false;
                if (!closing && !IsDisposed)
                {
                    outputTargets.Items.Clear();
                    foreach (var sample in outputSamples) outputTargets.Items.Add(sample);
                    if (outputTargets.Items.Count > 0) outputTargets.SelectedIndex = 0;
                    UpdateButtons();
                }
            }
        }

        private async Task<PowerSiReport> CollectRemoteOutput(ProcessInventory inventory, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (closing || IsDisposed || !IsHandleCreated) return ReportFailure(inventory, "SLAVE_UNAVAILABLE");
            var completion = new TaskCompletionSource<PowerSiReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellation.Register(() => completion.TrySetCanceled()))
            {
                try
                {
                    BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            cancellation.ThrowIfCancellationRequested();
                            completion.TrySetResult(await ReadOutputBuffer(inventory, true, cancellation));
                        }
                        catch (OperationCanceledException) { completion.TrySetCanceled(); }
                        catch (Exception error) { completion.TrySetException(error); }
                    }));
                }
                catch (InvalidOperationException) { completion.TrySetResult(ReportFailure(inventory, "SLAVE_UNAVAILABLE")); }
                return await completion.Task.ConfigureAwait(false);
            }
        }

        private static int TargetAllowance(long elapsedMilliseconds, int remainingTargets)
        {
            if (remainingTargets < 1) return 0;
            return (int)Math.Max(0, PowerSiReport.CollectionBudgetMilliseconds - elapsedMilliseconds) / remainingTargets;
        }

        private static bool IsExpectedCollectionFailure(Exception error)
        {
            return error is InvalidDataException || error is IOException || error is System.ComponentModel.Win32Exception ||
                error is UnauthorizedAccessException || error is TimeoutException;
        }

        private static bool IsPending(string code)
        {
            return code == "SC_PENDING" || code == "SC_FOREGROUND_WAIT_PENDING" ||
                code == "PENDING_WINDOWS_NOT_RESPONDING_INPUT_STOPPED";
        }

        private static async Task<T> ResponsiveStep<T>(ProcessInventory target, CancellationToken cancellation,
            Func<Task<T>> operation, Func<ProcessInventory, CancellationToken, Task> probe = null)
        {
            probe = probe ?? PowerSiScreenCapture.CheckResponsiveAsync;
            cancellation.ThrowIfCancellationRequested();
            await probe(target, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var value = await operation();
            cancellation.ThrowIfCancellationRequested();
            if (value is OutputBufferResult output && (IsPending(output.Code) || IsPending(output.Detail)) ||
                value is PowerSiObservation observation && IsPending(observation.LocalFailure))
                throw new InvalidDataException("SC_PENDING");
            await probe(target, cancellation);
            return value;
        }

        private static PowerSiReport ReportFailure(ProcessInventory inventory, string code)
        {
            return new PowerSiReport { CapturedUtc = DateTime.UtcNow, SessionId = inventory?.SessionId ?? 0,
                Omitted = inventory?.Omitted ?? 0, Unreadable = inventory?.Unreadable ?? 0, Partial = true, Code = code,
                Targets = (inventory?.Items ?? new ProcessState[0]).Select(process => new PowerSiTargetReport {
                    Pid = process.Pid, StartUtcTicks = process.StartUtcTicks, ProcessName = process.FullName ?? process.Name,
                    State = "UNAVAILABLE", Source = "NONE", Code = code, Summary = RecoveryAdvice(code), Excerpt = ""
                }).ToArray() };
        }

        private PowerSiReport BuildReport(ProcessInventory inventory, string code = "OK")
        {
            if (inventory == null) return ReportFailure(null, code == "OK" ? "CAPTURE_FAILED" : code);
            var targets = outputSamples.Select(ProjectReportTarget).ToArray();
            return new PowerSiReport { CapturedUtc = DateTime.UtcNow, SessionId = inventory.SessionId,
                Omitted = inventory.Omitted, Unreadable = inventory.Unreadable, Code = code, Targets = targets,
                Partial = code != "OK" || inventory.Omitted != 0 || inventory.Unreadable != 0 ||
                    targets.Any(target => target.State == "UNAVAILABLE" || target.State == "TIMEOUT" || target.State == "NOT_ATTEMPTED") };
        }

        private static PowerSiTargetReport ProjectReportTarget(OutputSample sample)
        {
            var target = new PowerSiTargetReport { Pid = sample.Process.Pid, StartUtcTicks = sample.Process.StartUtcTicks,
                ProcessName = sample.Process.FullName ?? sample.Process.Name, State = "UNAVAILABLE", Source = "NONE",
                Code = sample.Buffer?.Code ?? "TARGET_FAILED", Summary = "", Excerpt = "" };
            if (IsPending(target.Code) || IsPending(sample.Vision?.LocalFailure))
            {
                target.State = "PENDING"; target.Code = "SC_PENDING";
                return target;
            }
            var text = sample.Buffer?.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                target.State = "READ";
                target.Source = sample.Buffer.Code == "AUTO_COPY_READ" ? "AUTO_COPY" : "BUFFER";
                target.CapturedUtc = sample.ReceivedUtc;
                target.Excerpt = PowerSiReport.LatestExcerpt(text, out target.Truncated);
                target.Summary = DescribeEvidence(target.Excerpt);
                if (sample.Vision?.Code == "VISION_NOT_CONFIGURED") target.Summary += " / LLM 설정 꺼짐";
                return target;
            }
            if (text != null)
            {
                target.State = "VISIBLE_EMPTY"; target.Code = "OUTPUT_EMPTY";
                target.Source = sample.Buffer.Code == "AUTO_COPY_READ" ? "AUTO_COPY" : "BUFFER";
                target.CapturedUtc = sample.ReceivedUtc;
                target.Summary = "직접 읽은 Output에 내용 없음";
                return target;
            }
            if (sample.Vision?.LocalVisibleEmpty == true)
            {
                target.State = "VISIBLE_EMPTY"; target.Source = "OCR"; target.Code = "OUTPUT_VISIBLE_EMPTY";
                target.CapturedUtc = sample.Vision.CapturedUtc;
                target.Summary = "보이는 Output 내용 없음; 전체 버퍼는 미확인";
                return target;
            }
            text = TranscriptOf(sample.Vision);
            if (!string.IsNullOrWhiteSpace(text))
            {
                target.State = "READ"; target.Source = "OCR"; target.Code = "OUTPUT_READ";
                target.CapturedUtc = sample.Vision.CapturedUtc;
                target.Excerpt = PowerSiReport.LatestExcerpt(text, out target.Truncated);
                target.Summary = "LLM 화면 전사본; 숫자 정확도 미검증";
                return target;
            }
            if (target.Code == "NOT_ATTEMPTED") target.State = "NOT_ATTEMPTED";
            else if (sample.Vision?.LocalFailure == "TARGET_TIMEOUT" || sample.Vision?.Code == "VISION_TIMEOUT")
            { target.State = "TIMEOUT"; target.Code = "TARGET_TIMEOUT"; }
            else if (sample.Vision != null && (sample.Vision.Code.StartsWith("VISION_", StringComparison.Ordinal) ||
                sample.Vision.Code == "OUTPUT_REGION_UNCONFIRMED") && !target.Code.StartsWith("SC_", StringComparison.Ordinal) &&
                !target.Code.StartsWith("AUTO_COPY_", StringComparison.Ordinal))
                target.Code = sample.Vision.Code;
            else if (sample.Buffer?.Text != null) target.Code = "OUTPUT_EMPTY";
            target.Summary = RecoveryAdvice(target.Code);
            return target;
        }

        private static string DescribeEvidence(string text)
        {
            // Reuse only the field-checked PowerSI expressions; historical log events are not live completion claims.
            var observed = PowerSiObservation.FromText(text, null);
            var summary = "프로세스 실행 중; 계산 완료 여부 미확인";
            if (observed.OutputEvent == "SUSPENDED") summary = "최근 로그에 일시정지 기록 관측";
            else if (observed.OutputEvent == "RESUMED") summary = "최근 로그에 재개 기록 관측";
            if (observed.OutputFrequency != "?") summary += "; 로그 주파수 " + observed.OutputFrequency.Replace('_', ' ');
            if (observed.MemoryWarningSeen) summary += "; 메모리 경고 기록 있음";
            return summary;
        }

        private static string RecoveryAdvice(string code)
        {
            switch (code)
            {
                case "VISION_NOT_CONFIGURED": return "LM Studio 설정을 켜고 로드된 이미지 모델을 선택하세요";
                case "VISION_SERVER_UNAVAILABLE": return "Slave의 LM Studio 로컬 서버 실행과 포트를 확인하세요";
                case "VISION_MODEL_UNAVAILABLE": return "Slave의 LM Studio에서 이미지 모델을 로드하세요";
                case "VISION_MODEL_AMBIGUOUS": return "Slave 설정에서 사용할 로드 모델을 선택하세요";
                case "VISION_AUTH_REQUIRED": return "Slave의 LM Studio 인증 설정을 확인하세요";
                case "VISION_INVALID_RESPONSE": case "VISION_FAILED": return "LM Studio 모델 오류 또는 응답 형식 불일치; 로드된 이미지 모델과 서버 로그를 확인하세요";
                case "VISION_TIMEOUT": case "TARGET_TIMEOUT": return "제한 시간 내 수집하지 못했습니다";
                case "SC_MINIMIZED": return "PowerSI 최소화 상태; 창을 직접 준비하세요";
                case "SC_DESKTOP_UNAVAILABLE": return "Slave 데스크톱 잠금 또는 연결 상태를 확인하세요";
                case "SC_IDENTITY": case "SC_NOT_RUNNING": case "SC_WINDOW_CHANGED": return "대상 종료 또는 식별 변경; 새로 조회하세요";
                case "AUTO_COPY_WINDOW_CHANGED": return "대상 종료 또는 창 상태 변경; 새로 조회하세요";
                case "AUTO_COPY_CURSOR_MOVED": case "AUTO_COPY_INPUT_BUSY": case "AUTO_COPY_CLIPBOARD_FOREIGN":
                    return "사용자 입력 또는 클립보드 변경이 감지되어 수집을 중단했습니다";
                case "SC_FOREGROUND_WAIT_MISMATCH": case "SC_FOREGROUND_MISMATCH_PRECAPTURE": case "SC_FOREGROUND_MISMATCH_POSTCAPTURE":
                case "AUTO_COPY_FOREGROUND_LOST": return "다른 창으로 전환되어 수집을 중단했습니다";
                case "OUTPUT_EMPTY": return "직접 읽은 Output에 내용 없음";
                case "NOT_ATTEMPTED": return "전체 수집 시간 제한으로 미확인";
                case "BUSY": return "다른 수집이 진행 중입니다; 완료 후 다시 요청하세요";
                case "OUTPUT_REGION_UNCONFIRMED": case "AUTO_COPY_REGION_UNCONFIRMED": return "Output 영역을 확인하지 못해 자동 입력을 생략했습니다";
                default: return "수집 미확인; Slave의 대상 창과 로컬 설정을 확인하세요";
            }
        }

        private void RenderOutputBuffer(OutputBufferResult result, PowerSiObservation vision = null, DateTime? receivedUtc = null)
        {
            if (vision == null || !ReferenceEquals(lastBuffer, result) || !ReferenceEquals(lastObservation?.LocalFrame, vision.LocalFrame))
            {
                comparisonRuns.Clear();
                comparisonReports.Clear();
            }
            if (vision != null && !comparisonRuns.Contains(vision))
            {
                comparisonRuns.Add(vision);
                if (comparisonRuns.Count > 8) comparisonRuns.RemoveAt(0);
            }
            lastObservation = vision;
            lastBuffer = result;
            lastBufferReceivedUtc = receivedUtc ?? DateTime.UtcNow;
            visionPreview.Text = "결과 비교 보기";
            visionPreview.Enabled = vision != null; // The comparison is a snapshot; enable only after both paths finish.
            powerSi.Text = "출처: " + result.Method + " / " + result.Code + " / " + result.CharacterCount.ToString(CultureInfo.InvariantCulture) +
                "자 / " + result.LineCount.ToString(CultureInfo.InvariantCulture) + "줄 / LLM " + (vision?.Code ?? "대기 중") + "\r\n" +
                (result.Code == "OUTPUT_VISIBLE_EMPTY" ? "보이는 Output 내용 없음 — 전체 버퍼 미확인, 클릭·복사·OCR 생략.\r\n" :
                    result.Code == "SC_MINIMIZED" ? "선택된 PowerSI 창을 Windows가 최소화 상태로 보고하여 활성화·입력을 생략했습니다. 창이 열려 있었다면 진단 ZIP으로 식별값을 확인합니다.\r\n" :
                    result.Method == "USER_CLIPBOARD" ? "수동 복사본 — Output에서 Ctrl+A로 선택했는지 확인하세요.\r\n" :
                    result.Method == "AUTO_CLIPBOARD" ? "자동 복사본 — 선택 해제 → 판독용 캡처 → Ctrl+A/C → 선택 해제. 클립보드가 바뀌었습니다.\r\n" :
                    result.Method == "PREVIOUS_CAPTURE" ? "이번 자동 복사 실패 — 이전 수집본을 참고용으로 유지합니다. 현재 화면보다 오래된 내용입니다.\r\n" :
                    "컨트롤이 현재 보유한 텍스트입니다. 과거에 버린 로그까지 복원하는 것은 아닙니다.\r\n") +
                (result.Text == null ? "미수집: " + result.Detail :
                    (result.Text.Length > 4000 ? "[아래는 끝부분 미리보기 — 결과 비교 보기에서 전체 확인]\r\n" + result.Text.Substring(result.Text.Length - 4000) : result.Text));
            UpdateButtons();
        }

        private bool CanReplayVision()
        {
            return !closing && !busy && !batchInProgress && server == null && snapshotCancellation == null && lastBuffer != null &&
                lastObservation?.LocalFrame != null && !lastObservation.LocalVisibleEmpty;
        }

        private async Task ReplayVision()
        {
            if (!CanReplayVision()) return;
            var frame = lastObservation.LocalFrame;
            var savedCrop = lastObservation.LocalImage == null ? null : lastObservation;
            var buffer = lastBuffer;
            var receivedUtc = lastBufferReceivedUtc;
            var settings = visionSettings.Clone();
            var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(PowerSiVision.RequestDeadlineMilliseconds);
            snapshotCancellation = cancellation;
            replayInProgress = true;
            UpdateButtons();
            var clock = Stopwatch.StartNew();
            string visionStage = "저장한 화면 준비";
            var progress = new Progress<string>(stage =>
            {
                if (!ReferenceEquals(snapshotCancellation, cancellation) || closing || cancellation.IsCancellationRequested) return;
                visionStage = stage == "REUSE_CROP" ? "저장한 OCR 입력 재사용 — 영역 찾기 생략" : stage == "LOCATE_OUTPUT" ? "1/2 Output 영역 찾기" :
                    stage == "CROP_OUTPUT" ? "원본 픽셀 크롭" : stage == "READ_CROP" ? "2/2 크롭 문자 그대로 전사" : "저장한 화면 준비";
                if (savedCrop != null && stage == "READ_CROP") visionStage = "OCR만 재판독 (최대 " + settings.TimeoutSeconds + "초)";
                try { log.Write("OUTPUT_REPLAY_" + stage); } catch { }
            });
            Task<PowerSiObservation> visionTask = null;
            try
            {
                log.Write("OUTPUT_REPLAY_BEGIN");
                ShowActivity(savedCrop == null ? "같은 화면 재판독 시작 — 크롭 없음: 영역 찾기부터 실행 / Stop 가능" :
                    "같은 크롭 OCR만 재판독 — 현재 모델 사용 / 영역 찾기·복사 생략 / Stop 가능");
                visionTask = PowerSiVision.CaptureAsync(null, settings, cancellation.Token, progress, frame, savedCrop);
                while (!visionTask.IsCompleted)
                {
                    ShowActivity("같은 화면 재판독 / " + visionStage + " — " + (int)clock.Elapsed.TotalSeconds + "초 / 최대105초; Stop 가능");
                    await Task.WhenAny(visionTask, Task.Delay(1000, cancellation.Token));
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(snapshotCancellation, cancellation) || closing) return;
                }
                var vision = await visionTask;
                if (!RenderReplay(cancellation, buffer, frame, vision, receivedUtc)) return;
                log.WritePowerSi(vision);
                log.Write("OUTPUT_REPLAY_COMPLETE");
                ShowActivity("같은 화면 재판독 완료 — " + VisionRunCaption(vision) + " / " + vision.Code + " / 결과 비교 보기에서 실행 선택");
            }
            catch (OperationCanceledException)
            {
                try { log.Write("OUTPUT_REPLAY_CANCELLED"); } catch { }
                if (!closing && ReferenceEquals(snapshotCancellation, cancellation))
                    ShowActivity("같은 화면 재판독 취소/시간 제한 — 마지막 완료 결과를 유지했습니다.");
            }
            catch
            {
                try { log.Write("OUTPUT_REPLAY_FAILED"); } catch { }
                if (!closing && ReferenceEquals(snapshotCancellation, cancellation))
                    ShowActivity("같은 화면 재판독 실패 — 마지막 완료 결과를 유지했습니다. 로그를 확인하세요.");
            }
            finally
            {
                cancellation.Cancel();
                if (visionTask != null) { try { await visionTask; } catch { } }
                bool current = ReferenceEquals(snapshotCancellation, cancellation);
                if (current) { snapshotCancellation = null; replayInProgress = false; }
                cancellation.Dispose();
                if (current && !closing && !IsDisposed) UpdateButtons();
            }
        }

        private bool RenderReplay(CancellationTokenSource cancellation, OutputBufferResult buffer, PowerSiFrame frame,
            PowerSiObservation vision, DateTime receivedUtc)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (closing || !ReferenceEquals(snapshotCancellation, cancellation) || !ReferenceEquals(lastBuffer, buffer) ||
                !ReferenceEquals(lastObservation?.LocalFrame, frame)) return false;
            if (vision == null) throw new InvalidDataException("Replay observation is missing.");
            // Even a model-selection failure belongs to this saved frame and must remain retryable.
            vision.LocalFrame = frame;
            if (vision.LocalFullImage == null) vision.LocalFullImage = frame.Png;
            if (vision.LocalSampleId == null) vision.LocalSampleId = lastObservation.LocalSampleId;
            if (vision.LocalVisionMode == null && lastObservation.LocalImage != null &&
                (vision.Code == "VISION_NOT_CONFIGURED" || vision.Code == "VISION_BUSY"))
            {
                // Pre-request failures (disabled/busy) must not discard the saved crop needed for retry.
                vision.LocalVisionMode = "OCR_ONLY";
                vision.LocalImage = lastObservation.LocalImage;
                vision.LocalPaneImage = lastObservation.LocalPaneImage;
                vision.LocalSuggestedImage = lastObservation.LocalSuggestedImage;
                vision.LocalOcrSampleId = lastObservation.LocalOcrSampleId;
                vision.LocalRegionInfo = lastObservation.LocalRegionInfo;
                vision.LocalCaptureInfo = lastObservation.LocalCaptureInfo;
                vision.LocalOutputBody = lastObservation.LocalOutputBody;
                vision.LocalFrameSize = lastObservation.LocalFrameSize;
                vision.LocalBodyDiagnostics = lastObservation.LocalBodyDiagnostics;
            }
            RenderOutputBuffer(buffer, vision, receivedUtc);
            SaveSelectedSample();
            return true;
        }

        private void ConfigureVision()
        {
            if (server != null || snapshotCancellation != null || busy) return;
            using (var dialog = new LocalVisionSettingsForm(visionSettings))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    VisionSettingsStore.Save(Path.Combine(dataPath, "local-vision.json"), dialog.Result);
                    visionSettings = dialog.Result;
                    log.Write("VISION_SETTINGS_SAVED");
                    UpdateAnchorState();
                    ShowActivity(CanReplayVision() ? "설정 저장 완료 — " + replayVision.Text + "으로 현재 모델을 비교하세요. 기준 원문과 이미지는 유지됩니다." :
                        "설정 저장 완료 — 새 화면 두 방식 비교로 캡처·모델 판독·전체 텍스트를 함께 확인합니다.");
                }
                catch { ShowActivity("LM Studio 설정 저장 실패. 이전 설정을 유지합니다."); }
            }
        }

        private void ShowVisionPreview()
        {
            if (lastBuffer != null)
            {
                using (var dialog = CreateComparisonDialog()) dialog.ShowDialog(this);
                return;
            }
            var observation = lastObservation;
            if (observation?.LocalImage == null && observation?.LocalFullImage == null) return;
            using (var dialog = new Form { Text = Program.Title + " — Slave 내부 캡처/판독 확인", ClientSize = new Size(1040, 740),
                StartPosition = FormStartPosition.CenterParent, Font = Font })
            {
                var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
                dialog.Controls.Add(split); split.SplitterDistance = 490;
                AddVisionImages(split.Panel1, observation);
                split.Panel2.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both,
                    Text = observation.LocalEvidence ?? observation.LocalFailure ?? observation.Summary ?? "판독 결과 없음" });
                dialog.ShowDialog(this);
            }
        }

        // ---------------------------------------------------------------- transcript comparison and diagnostic bundle

        // The evidence text starts with fixed local headers; only the transcript lines take part in the comparison.
        private static string TranscriptOf(PowerSiObservation run)
        {
            if (run == null || run.Code != "OUTPUT_READ" || !run.OutputExposed || run.LocalFailure != null) return null;
            var evidence = run.LocalEvidence;
            if (string.IsNullOrEmpty(evidence)) return null;
            var lines = evidence.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            // Strip only the successful OCR envelope. Rejected raw responses remain local diagnostic evidence.
            int start = lines.Length >= 2 && lines[0] == "[Output 하단 확대 판독 — LLM 전사본, 숫자 정확도 미검증]" &&
                lines[1].StartsWith("캡처 UTC ", StringComparison.Ordinal) ? 2 : 0;
            if (start >= lines.Length) return null;
            var transcript = string.Join("\r\n", lines, start, lines.Length - start);
            return string.IsNullOrWhiteSpace(transcript) ? null : transcript;
        }

        // Rendered once per run and kept for the bundle. Only the T1 counters reach the log.
        private string ComparisonFor(PowerSiObservation run, OutputBufferResult source = null)
        {
            if (run == null) return null;
            string existing;
            if (comparisonReports.TryGetValue(run, out existing)) return existing;
            var full = (source ?? lastBuffer)?.Text;
            var transcript = TranscriptOf(run);
            if (string.IsNullOrEmpty(full) || transcript == null) return null;
            string rendered;
            try
            {
                var comparison = TranscriptComparison.Compare(full, transcript);
                rendered = comparison.Render();
                try { log.Write("TRANSCRIPT_COMPARE", "summary=" + LogValue(comparison.Summary())); } catch { }
            }
            catch { return null; }
            comparisonReports[run] = rendered;
            return rendered;
        }

        private DiagnosticBundleContent BuildBundleContent(PowerSiObservation[] runs, OutputSample sample = null)
        {
            var buffer = sample == null ? lastBuffer : sample.Buffer;
            var content = new DiagnosticBundleContent
            {
                Version = LinkVersion.Value,
                CreatedUtc = DateTime.UtcNow,
                TargetPid = sample?.Process.Pid,
                TargetStartUtcTicks = sample?.Process.StartUtcTicks,
                TargetSessionId = sample?.SessionId,
                BufferCode = buffer == null ? null : buffer.Code,
                BufferMethod = buffer == null ? null : buffer.Method,
                BufferDetail = buffer == null ? null : buffer.Detail,
                BufferReceivedUtc = buffer == null ? (DateTime?)null : sample?.ReceivedUtc ?? lastBufferReceivedUtc,
                FullText = buffer == null ? null : buffer.Text,
                LogFilePath = log.Path,
                AutoCopyFramePng = sample == null ? autoCopyFrame : sample.FailureFrame,
                AutoCopyLastFailure = sample == null ? autoCopyFailure : sample.Failure,
                Notes = "자동 복사 위치: 매번 자동 탐색" +
                    " / 자동 복사 설정: " +
                    (visionSettings.AutoCopyEnabled ? "사용" : "꺼짐") + " / 전체 텍스트: " +
                    (buffer == null ? "없음" : buffer.Code + " " + buffer.Method + " " + buffer.Detail) +
                    " / 입력 이미지와 마지막 줄을 직접 대조하세요. 한 번의 성공이 연속 무인 운용이나 전체 전사 정확도를 보증하지 않습니다."
            };
            for (int index = 0; runs != null && index < runs.Length; index++)
            {
                var run = runs[index];
                if (run == null) continue;
                var model = run.LocalModelInfo;
                content.Runs.Add(new DiagnosticBundleRun
                {
                    Label = (index + 1).ToString(CultureInfo.InvariantCulture) + "-" +
                        (model?.Key ?? model?.Id ?? run.LocalModel ?? "model-unknown"),
                    ModelInfo = VisionRunCaption(run),
                    Mode = run.LocalVisionMode,
                    RegionInfo = run.LocalRegionInfo,
                    BodyDiagnostics = run.LocalBodyDiagnostics,
                    Result = run.LocalVisibleEmpty ? "OUTPUT_VISIBLE_EMPTY" : run.Code,
                    FailureCode = run.LocalFailure,
                    FullFramePng = run.LocalFullImage,
                    SuggestedPng = run.LocalSuggestedImage,
                    BodyPng = run.LocalPaneImage,
                    OcrInputPng = run.LocalImage,
                    Transcript = TranscriptOf(run),
                    TranscriptValidated = run.Code == "OUTPUT_READ" && run.LocalFailure == null && run.OutputExposed,
                    Comparison = ComparisonFor(run, buffer),
                    Metadata = "VISION_RUN" + SlaveLog.ComparisonMetadata(run)
                });
            }
            return content;
        }

        private DiagnosticBundleContent BuildExportContent(PowerSiObservation[] runs)
        {
            if (outputSamples.Count == 0) return BuildBundleContent(runs);
            SaveSelectedSample();
            var batch = new DiagnosticBundleContent { Version = LinkVersion.Value, CreatedUtc = DateTime.UtcNow,
                LogFilePath = log.Path, Notes = "PID별 독립 수집. PENDING은 Windows 응답 없음이며 내부 시뮬레이션 상태 판정이 아닙니다." };
            foreach (var sample in outputSamples)
            {
                var target = BuildBundleContent(sample.Runs.ToArray(), sample);
                target.LogFilePath = null;
                batch.Targets.Add(target);
            }
            return batch;
        }

        private void SaveDiagnosticBundle(Form owner, PowerSiObservation[] runs)
        {
            if (MessageBox.Show(owner,
                "진단 묶음(ZIP)에는 PowerSI 화면 캡처 이미지와 Output 전체 텍스트, 전사본·대조 결과가 그대로 들어갑니다.\r\n" +
                "Git·공개 저장소·외부 공유 채널에 올리지 말고, 소유자가 필요하다고 판단한 담당자에게만 직접 전달하세요.\r\n\r\n" +
                "지금 저장할까요?", Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            int pid;
            using (var current = Process.GetCurrentProcess()) pid = current.Id;
            using (var dialog = new SaveFileDialog { Filter = "진단 묶음 (*.zip)|*.zip", OverwritePrompt = true,
                FileName = DiagnosticBundle.DefaultFileName(DateTime.UtcNow, pid) })
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return;
                try
                {
                    byte[] bytes;
                    using (var memory = new MemoryStream())
                    {
                        DiagnosticBundle.Write(memory, BuildExportContent(runs));
                        bytes = memory.ToArray();
                    }
                    File.WriteAllBytes(dialog.FileName, bytes);
                    string hash;
                    using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                    try { log.Write("DIAG_BUNDLE_SAVED", "bytes=" + bytes.Length.ToString(CultureInfo.InvariantCulture) + " sha256=" + hash); } catch { }
                    MessageBox.Show(owner, "진단 묶음을 저장했습니다 (" + bytes.Length.ToString(CultureInfo.InvariantCulture) +
                        " 바이트). 내용을 확인한 뒤 필요한 담당자에게만 전달하세요.", Program.Title);
                }
                catch (Exception error)
                {
                    try { log.Write("DIAG_BUNDLE_FAILED", "code=" + LogValue(error.GetType().Name)); } catch { }
                    MessageBox.Show(owner, "진단 묶음 저장 실패: " + error.GetType().Name, Program.Title);
                }
            }
        }

        private Form CreateComparisonDialog()
        {
                var dialog = new Form { Text = Program.Title + (selectedSample == null ? "" : " — PID " + selectedSample.Process.Pid) + " — 전체 텍스트 / LLM 화면 판독 비교 (시각 차이 주의)",
                    ClientSize = new Size(1180, 740), StartPosition = FormStartPosition.CenterParent, Font = Font };
                try
                {
                    var columns = new SplitContainer { Dock = DockStyle.Fill };
                    dialog.Controls.Add(columns); columns.SplitterDistance = 590;
                    columns.Panel1.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
                        ScrollBars = ScrollBars.Both, MaxLength = OutputBufferCapture.MaxCharacters,
                        Text = lastBuffer.Text ?? (lastBuffer.Code == "OUTPUT_VISIBLE_EMPTY" ?
                            "보이는 Output 내용 없음\r\n전체 버퍼는 미확인입니다. 불필요한 클릭·복사·OCR은 생략했습니다." :
                            "미수집: " + lastBuffer.Code + "\r\n" + lastBuffer.Detail) });
                    columns.Panel1.Controls.Add(new Label { Dock = DockStyle.Top, Height = 54,
                        Text = (lastBuffer.Method == "PREVIOUS_CAPTURE" ? "이전 참고 원문 (이번 자동 복사 실패)" : "전체 텍스트") + " / " + lastBuffer.Method + "\r\n수집 완료 UTC " + lastBufferReceivedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                            " (버퍼 원자적 캡처 시각 아님)" });
                    var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
                    columns.Panel2.Controls.Add(right); right.SplitterDistance = 420;
                    var runs = comparisonRuns.Count == 0 ? new[] { lastObservation } : comparisonRuns.ToArray();
                    var runSelector = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList,
                        AccessibleName = "같은 화면 판독 실행 선택 — 모델 ID, 양자화, 소요 시간" };
                    dialog.Controls.Add(runSelector);
                    for (int index = 0; index < runs.Length; index++)
                        runSelector.Items.Add("실행 " + (index + 1) + " / " + VisionRunCaption(runs[index]));
                    runSelector.SelectedIndexChanged += delegate
                    {
                        foreach (var panel in new[] { right.Panel1, right.Panel2 })
                            while (panel.Controls.Count != 0) panel.Controls[0].Dispose();
                        var selected = runs[runSelector.SelectedIndex];
                        var texts = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
                        right.Panel2.Controls.Add(texts);
                        if (texts.Height > 200) texts.SplitterDistance = 150; // Keep the default split on a very short panel.
                        texts.Panel1.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both,
                            Text = selected?.LocalEvidence ?? selected?.LocalFailure ?? selected?.Summary ?? "LLM 판독 대기 중" });
                        texts.Panel2.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both,
                            AccessibleName = "전사본과 전체 텍스트의 줄 단위 대조 결과",
                            Text = ComparisonFor(selected) ?? "원문 대조 없음 — 전체 텍스트 또는 정상 완료된 OCR 전사본이 없습니다. 실패 응답은 진단용입니다." });
                        texts.Panel2.Controls.Add(new Label { Dock = DockStyle.Top, Height = 22,
                            Text = "원문 대조 — 대응 행 검사만 수행 / 전체·최신 줄 전사 여부 미검증" });
                        AddVisionImages(right.Panel1, selected);
                    };
                    runSelector.SelectedIndex = runs.Length - 1;
                    var actions = new Panel { Dock = DockStyle.Bottom, Height = 44 };
                    var saveBundle = new Button { Text = "진단 묶음 저장…", Bounds = new Rectangle(12, 6, 170, 32),
                        AccessibleName = "화면 이미지와 Output 전체 텍스트를 포함한 진단 ZIP 저장" };
                    saveBundle.Enabled = outputSamples.Count > 0 || !string.IsNullOrEmpty(lastBuffer.Text) || runs.Any(run => run != null);
                    saveBundle.Click += delegate { SaveDiagnosticBundle(dialog, runs); };
                    actions.Controls.Add(saveBundle);
                    actions.Controls.Add(new Label { Bounds = new Rectangle(194, 12, 960, 22),
                        Text = "ZIP에는 모든 PID의 원문·이미지·결과가 포함됩니다. 다른 PID는 이 창을 닫고 메인 창의 결과 목록에서 선택하세요." });
                    dialog.Controls.Add(actions);
                    return dialog;
                }
                catch { dialog.Dispose(); throw; }
        }

        private static void AddVisionImages(Control panel, PowerSiObservation observation)
        {
            var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
                AccessibleName = "LLM용으로 준비한 원본 또는 크롭 이미지" };
            var selector = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList,
                AccessibleName = "문자 전사 실제 입력, Output 전체 본문, LLM 제안 영역 또는 PowerSI 전체 원본 선택" };
            panel.Controls.Add(picture);
            panel.Controls.Add(new TextBox { Dock = DockStyle.Top, Height = 100, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                AccessibleName = "모델과 캡처 영역 및 판독 시간",
                Text = VisionRunCaption(observation) + "\r\n표본 " + (observation?.LocalSampleId ?? "없음") +
                    "\r\nOCR 표본 " + (observation?.LocalOcrSampleId ?? "없음") + "\r\n영역 찾기 " + ((observation?.LocalLocateMs ?? 0) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) +
                    "초 / 문자 전사 " + ((observation?.LocalReadMs ?? 0) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "초\r\n" +
                    (observation?.LocalCaptureInfo ?? "이미지 정보 없음") });
            panel.Controls.Add(selector);
            var images = new List<Bitmap>();
            picture.Disposed += delegate { foreach (var image in images) image.Dispose(); };
            void Add(byte[] png, string caption)
            {
                if (png == null) return;
                using (var stream = new MemoryStream(png, false))
                using (var source = Image.FromStream(stream)) images.Add(new Bitmap(source));
                selector.Items.Add(caption);
            }
            Add(observation?.LocalImage, "문자 전사 실제 입력 — 하단 최대 256픽셀 / 이미지 안의 모든 줄 판독");
            Add(observation?.LocalPaneImage, "Output 전체 본문 — 제목줄 제외 (OCR 입력은 하단 일부)");
            Add(observation?.LocalSuggestedImage, observation?.LocalVisionMode == "OCR_ONLY" ? "LLM 제안 영역 — 이전 실행에서 재사용" : "LLM 제안 영역 — 보정 전");
            Add(observation?.LocalFullImage, "PowerSI 전체 원본 — 영역 찾기 입력");
            selector.SelectedIndexChanged += delegate { picture.Image = images[selector.SelectedIndex]; };
            if (images.Count != 0) selector.SelectedIndex = 0;
        }

        private static string VisionRunCaption(PowerSiObservation observation)
        {
            return (observation?.LocalModelInfo?.Id ?? observation?.LocalModel ?? "모델 정보 없음") +
                " / " + (observation?.LocalModelInfo?.Quantization ?? "양자화 미제공") +
                " / " + ((observation?.LocalElapsedMs ?? 0) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "초" +
                (observation?.LocalVisionMode == "OCR_ONLY" ? " / OCR만 (이전 크롭 재사용)" : "") +
                " / thinking OFF 요청·실제 적용 미확인";
        }

        private static string FormatCpu(int? cpuPermille)
        {
            return cpuPermille.HasValue
                ? (cpuPermille.Value / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + "%"
                : "?";
        }

        private static string FormatMiB(long? workingSetMiB)
        {
            return workingSetMiB.HasValue ? workingSetMiB.Value.ToString(CultureInfo.InvariantCulture) + " MiB" : "?";
        }

        private static string FormatAge(long? ageSeconds)
        {
            if (!ageSeconds.HasValue) return "?";
            return (ageSeconds.Value / 60).ToString(CultureInfo.InvariantCulture) + "분";
        }

        private void CancelSnapshotRefresh()
        {
            var cancellation = snapshotCancellation;
            snapshotCancellation = null;
            if (cancellation == null) return;
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void ShowActivity(string message)
        {
            state.Text = message + Environment.NewLine + (server == null ?
                "Slave 시작·연결파일·Master·메신저는 이번 테스트에 필요 없습니다. 화면/원문은 이 PC 안에서만 처리합니다." : MasterCompletionNotice);
        }

        private void ExportPairing()
        {
            if (server == null || pairingText == null) return;
            using (var dialog = new SaveFileDialog { Filter = "Slave 연결파일 (*.rmpair)|*.rmpair", FileName = "Slave-connection.rmpair", OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, pairingText + Environment.NewLine, new System.Text.UTF8Encoding(false)); }
                catch { MessageBox.Show("연결파일 저장 실패", Program.Title); }
            }
        }

        private void UpdateButtons()
        {
            outputTargets.Enabled = !batchInProgress && !busy && !closing && snapshotCancellation == null;
            address.Enabled = !batchInProgress && !busy && !closing && server == null && snapshotCancellation == null;
            start.Enabled = address.Enabled && address.SelectedIndex >= 0;
            stop.Enabled = !closing && (server != null || snapshotCancellation != null);
            export.Enabled = !busy && !closing && server != null;
            refresh.Enabled = !batchInProgress && !busy && !closing && snapshotCancellation == null;
            powerSiCheck.Enabled = !batchInProgress && !busy && !closing && server == null && snapshotCancellation == null;
            outputAll.Enabled = !batchInProgress && !busy && !closing && server == null && snapshotCancellation == null;
            replayVision.Enabled = CanReplayVision();
            replayVision.Text = lastObservation?.LocalImage == null ? "저장 화면 재판독" : "같은 OCR 이미지 재판독";
            replayVision.AccessibleName = lastObservation?.LocalImage == null ?
                "저장한 전체 화면으로 현재 LM Studio 모델의 Output 영역 찾기부터 재판독" :
                "저장한 동일 OCR 이미지 바이트를 현재 LM Studio 모델로 문자 전사만 재판독. 새 캡처와 원문 복사 없음";
            visionSetup.Enabled = !batchInProgress && !busy && !closing && server == null && snapshotCancellation == null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                remoteProgress.Dispose();
                closing = true;
                CancelSnapshotRefresh();
                var oldServer = server;
                var oldActivity = serverActivity;
                var oldSnapshot = serverSnapshot;
                server = null;
                serverActivity = null;
                serverSnapshot = null;
                if (oldServer != null && oldActivity != null) oldServer.Activity -= oldActivity;
                if (oldServer != null && oldSnapshot != null) oldServer.StatusCaptured -= oldSnapshot;
                oldServer?.Dispose();
                identity?.Dispose();
                identity = null;
            }
            base.Dispose(disposing);
        }

        private static void RunReportSelfTests()
        {
            void Need(bool value) { if (!value) throw new InvalidOperationException("PowerSI report collection regression."); }
            var now = DateTime.UtcNow;
            var sample = new OutputSample { Process = new ProcessState { Pid = 202, Name = "powersi",
                FullName = "PowerSI_" + new string('한', 80), StartUtcTicks = now.AddHours(-1).Ticks }, SessionId = 1,
                ReceivedUtc = now, Buffer = new OutputBufferResult { Code = "AUTO_COPY_READ", Method = "AUTO_CLIPBOARD",
                    Text = "Simulation is suspended.\nold1\nold2\nold3\nold4\nAFS Current Frequency ( MHz ) = 38.000" } };
            var read = ProjectReportTarget(sample);
            read.Validate();
            Need(read.ProcessName == sample.Process.FullName && read.Source == "AUTO_COPY" && read.Truncated &&
                read.Excerpt.EndsWith("38.000", StringComparison.Ordinal) && !read.Summary.Contains("일시정지"));
            RecordTargetFailure(sample, "TARGET_TIMEOUT");
            Need(ProjectReportTarget(sample).State == "READ"); // Exact copy survives an independent OCR timeout.
            RecordTargetFailure(sample, "SC_FOREGROUND_WAIT_PENDING");
            var pending = ProjectReportTarget(sample);
            pending.Validate();
            Need(pending.State == "PENDING" && pending.CapturedUtc == null && pending.Summary == "" &&
                pending.Excerpt == "" && pending.Source == "NONE" && !pending.Truncated && sample.Buffer.Text == null);
            sample.Buffer = new OutputBufferResult { Code = "BUFFER_NOT_EXPOSED" };
            sample.Vision = PowerSiObservation.VisionUnavailable("VISION_INVALID_RESPONSE");
            sample.Vision.LocalFailure = "CONTENT_TEXT_CONTROL";
            sample.Vision.LocalEvidence = "private-rejected-response";
            var failed = ProjectReportTarget(sample);
            failed.Validate();
            Need(failed.State == "UNAVAILABLE" && failed.Code == "VISION_INVALID_RESPONSE" && failed.Excerpt == "");
            sample.Vision = PowerSiObservation.VisionLogExcerpt("Line 001\nLine 002", now);
            sample.Vision.LocalEvidence = "Line 001\nLine 002";
            var ocr = ProjectReportTarget(sample);
            ocr.Validate();
            Need(ocr.State == "READ" && ocr.Source == "OCR" && ocr.Excerpt.Contains("002"));
            sample.Vision = PowerSiObservation.VisionLogExcerpt(null, now);
            sample.Vision.LocalVisibleEmpty = true;
            var empty = ProjectReportTarget(sample);
            empty.Validate();
            Need(empty.State == "VISIBLE_EMPTY" && empty.Excerpt == "" && empty.CapturedUtc == now);
            sample.Buffer = new OutputBufferResult { Code = "BUFFER_READ", Text = " \r\n " };
            sample.Vision = PowerSiObservation.VisionUnavailable("VISION_NOT_CONFIGURED");
            var directEmpty = ProjectReportTarget(sample);
            directEmpty.Validate();
            Need(directEmpty.State == "VISIBLE_EMPTY" && directEmpty.Source == "BUFFER" && directEmpty.CapturedUtc == now);
            sample.Buffer = new OutputBufferResult { Code = "AUTO_COPY_INPUT_BUSY" };
            Need(ProjectReportTarget(sample).Code == "AUTO_COPY_INPUT_BUSY" &&
                ProjectReportTarget(sample).Summary.Contains("사용자 입력"));
            Need(IsExpectedCollectionFailure(new InvalidDataException()) && !IsExpectedCollectionFailure(new NullReferenceException()));
            Need(TargetAllowance(0, 2) == 50000 && TargetAllowance(50000, 2) == 25000 &&
                TargetAllowance(100000, 1) == 0 && TargetAllowance(110000, 1) == 0 && TargetAllowance(0, 0) == 0);
            foreach (var failureProbe in new[] { 1, 2 })
            {
                var probes = 0; var firstWork = 0; var nextWork = 0; var rejected = false;
                Func<ProcessInventory, CancellationToken, Task> probe = (ignored, token) =>
                {
                    if (++probes == failureProbe) throw new InvalidDataException("SC_PENDING");
                    return Task.FromResult(0);
                };
                async Task Run()
                {
                    await ResponsiveStep<object>(null, CancellationToken.None, () => { firstWork++; return Task.FromResult<object>(null); }, probe);
                    await ResponsiveStep<object>(null, CancellationToken.None, () => { nextWork++; return Task.FromResult<object>(null); }, probe);
                }
                try { Run().GetAwaiter().GetResult(); }
                catch (InvalidDataException error) { rejected = error.Message == "SC_PENDING"; }
                Need(rejected && firstWork == failureProbe - 1 && nextWork == 0);
            }
            var report = ReportFailure(new ProcessInventory { SessionId = 1, Items = new[] { sample.Process } }, "BUSY");
            var pendingProbes = 0;
            try
            {
                ResponsiveStep(null, CancellationToken.None,
                    () => Task.FromResult(new OutputBufferResult { Code = "SC_PENDING" }),
                    (ignored, token) => { pendingProbes++; return Task.FromResult(0); }).GetAwaiter().GetResult();
                throw new InvalidOperationException("Pending result was accepted.");
            }
            catch (InvalidDataException error) { Need(error.Message == "SC_PENDING" && pendingProbes == 1); }
            Need(PowerSiReport.Parse(report.Serialize()).Targets[0].ProcessName == sample.Process.FullName);
            Console.WriteLine("PASS: Pending stops collection; evidence projection, full names, partial deadlines and report failures");
        }

        internal static void SelfTest()
        {
            RunReportSelfTests();
            // The run column keeps the LLM transcript above and its comparison against the full text below.
            TextBox Transcript(SplitContainer column)
            { return column.Panel2.Controls.OfType<SplitContainer>().Single().Panel1.Controls.OfType<TextBox>().Single(); }
            TextBox Comparison(SplitContainer column)
            { return column.Panel2.Controls.OfType<SplitContainer>().Single().Panel2.Controls.OfType<TextBox>().Single(); }
            var directory = Path.Combine(Path.GetTempPath(), "RemoteMonitorSlave-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                VisionSettingsStore.SelfTest(directory);
                using (var settingsForm = new LocalVisionSettingsForm(new LocalVisionSettings()))
                {
                    var thinkingNote = settingsForm.Controls.OfType<Label>().Single(label => label.AccessibleName == "thinking 비활성화 요청과 실제 적용 여부 구분");
                    if (!thinkingNote.Text.Contains("실제 적용 여부는 확인할 수 없습니다") ||
                        !thinkingNote.Text.Contains("LM Studio에서도 thinking을 끄고") ||
                        settingsForm.Controls.Cast<Control>().Any(control => control.Bottom > settingsForm.ClientSize.Height))
                        throw new InvalidOperationException("Vision settings hide the thinking distinction or clip controls.");
                }
                using (var form = new SlaveForm(directory))
                {
                    if (!form.Text.Contains(LinkVersion.Value) || form.server != null || form.identity != null ||
                        form.stop.Enabled || form.export.Enabled || form.start.Enabled || !form.refresh.Enabled || !form.powerSiCheck.Enabled ||
                        form.powerSiCheck.Text != "PowerSI 확인" || !form.pairing.UseSystemPasswordChar ||
                        form.powerSi.Text.Length != 0 || !form.powerSi.ReadOnly || !form.state.Text.Contains("STOPPED") || !form.outputAll.Enabled || form.replayVision.Enabled)
                        throw new InvalidOperationException("Slave UI started work without local Start or exposed credentials.");
                    var handle = form.Handle;
                    form.busy = true;
                    var remoteBusy = Task.Run(() => form.CollectRemoteOutput(new ProcessInventory(), CancellationToken.None));
                    var callbackClock = Stopwatch.StartNew();
                    while (!remoteBusy.IsCompleted && callbackClock.ElapsedMilliseconds < 5000)
                    { Application.DoEvents(); Thread.Sleep(1); }
                    if (!remoteBusy.IsCompleted || remoteBusy.GetAwaiter().GetResult().Code != "BUSY")
                        throw new InvalidOperationException("Remote collection did not marshal to the shared UI collector.");
                    form.busy = false;
                    form.address.SelectedItem = "127.0.0.1";
                    if (!form.start.Enabled || form.server != null) throw new InvalidOperationException("Explicit address selection failed.");
                    using (var localCancellation = new CancellationTokenSource())
                    {
                        form.snapshotCancellation = localCancellation;
                        form.UpdateButtons();
                        if (form.refresh.Enabled || form.powerSiCheck.Enabled || form.outputAll.Enabled || form.start.Enabled || !form.stop.Enabled)
                            throw new InvalidOperationException("Local PowerSI check controls were not cancellable without a listener.");
                        form.StopServer();
                        if (!localCancellation.IsCancellationRequested || form.snapshotCancellation != null || form.stop.Enabled)
                            throw new InvalidOperationException("Stop did not cancel a local PowerSI check without a listener.");
                    }
                    form.StopServer();
                    using (var testIdentity = SlaveIdentity.Create())
                    using (var currentServer = new StatusServer(testIdentity, IPAddress.Loopback, 0))
                    using (var staleServer = new StatusServer(testIdentity, IPAddress.Loopback, 0))
                    {
                        var inventory = new ProcessInventory
                        {
                            Items = new[]
                            {
                                new ProcessState { Pid = 101, Name = "sample.exe", HasWindow = true, AgeSeconds = 65, WorkingSetMiB = 12, CpuPermille = 123 },
                                new ProcessState { Pid = 102, Name = "headless.exe", HasWindow = false, AgeSeconds = null, WorkingSetMiB = null, CpuPermille = null }
                            },
                            Omitted = 3,
                            Unreadable = 1,
                            SessionId = 1
                        };
                        inventory.Validate();
                        var captured = new MachineStatus
                        {
                            LocalTime = DateTime.SpecifyKind(new DateTime(2026, 9, 10, 12, 34, 56), DateTimeKind.Unspecified),
                            Processes = inventory
                        };
                        var powerSiInventory = new ProcessInventory
                        {
                            Items = new[] { new ProcessState { Pid = 103, Name = "powersi", HasWindow = true, AgeSeconds = 65, WorkingSetMiB = 24, CpuPermille = 321 } },
                            SessionId = 1
                        };
                        powerSiInventory.Validate();
                        var observation = PowerSiObservation.Parse("PS1:OK:RESUMED:38.000_MHZ:1:SIMULATION:38.000_MHZ:1:1");
                        var powerSiCaptured = new MachineStatus
                        {
                            LocalTime = captured.LocalTime,
                            PowerSiOnly = true,
                            Processes = powerSiInventory,
                            PowerSi = observation
                        };
                        form.server = currentServer;
                        form.ApplySnapshot(currentServer, captured);
                        if (form.processes.Rows.Count != 2 || !form.snapshot.Text.Contains("표시 2개") ||
                            !form.snapshot.Text.Contains("더 있음 3개") || !form.snapshot.Text.Contains("읽지 못함 1개") ||
                            !form.snapshot.Text.Contains("미제공 — 실행 중은 완료 여부가 아닙니다") ||
                            Convert.ToString(form.processes.Rows[0].Cells[3].Value) != "12.3%" ||
                            Convert.ToString(form.processes.Rows[1].Cells[3].Value) != "?" ||
                            Convert.ToString(form.processes.Rows[1].Cells[4].Value) != "?" ||
                            Convert.ToString(form.processes.Rows[0].Cells[5].Value) != "1분" ||
                            Convert.ToString(form.processes.Rows[1].Cells[5].Value) != "?" || form.powerSi.Text.Length != 0)
                            throw new InvalidOperationException("Slave process snapshot was not rendered as read-only current-session data.");
                        using (var localCancellation = new CancellationTokenSource())
                        {
                            form.snapshotCancellation = localCancellation;
                            form.UpdateButtons();
                            // Synthetic asynchronous worker result: never reads a PowerSI window on the test host.
                            var localResult = Task.Run(() => Tuple.Create(powerSiInventory, observation)).GetAwaiter().GetResult();
                            if (!form.RenderLocalSnapshot(localCancellation, localResult.Item1, localResult.Item2, true) ||
                                !form.snapshot.Text.Contains("로컬 PowerSI 확인") || form.powerSi.Text != observation.Summary)
                                throw new InvalidOperationException("Local PowerSI result was not rendered without a listener.");
                            form.ApplySnapshot(currentServer, captured);
                            if (!localCancellation.IsCancellationRequested || form.snapshotCancellation != null ||
                                !form.snapshot.Text.Contains("현재 세션 전체") || form.powerSi.Text.Length != 0 ||
                                form.RenderLocalSnapshot(localCancellation, localResult.Item1, localResult.Item2, true))
                                throw new InvalidOperationException("Stale local PowerSI result overwrote a newer Master snapshot.");
                        }
                        form.ApplyActivity(currentServer, "PWRSI_CAPTURING");
                        if (!form.state.Text.Contains("PowerSI") || !form.state.Text.Contains("수집") || form.powerSi.Text.Length != 0)
                            throw new InvalidOperationException("Slave PowerSI output capture activity was not visible.");
                        form.ApplySnapshot(currentServer, powerSiCaptured);
                        if (form.processes.Rows.Count != 1 || !form.snapshot.Text.Contains("POWERSI/PWRSI만") ||
                            form.powerSi.Text != observation.Summary || !form.powerSi.Text.Contains("RESUMED") ||
                            !form.powerSi.Text.Contains("38.000 MHZ"))
                            throw new InvalidOperationException("Slave PowerSI output observation was not rendered.");
                        form.ApplySnapshot(currentServer, captured);
                        if (form.processes.Rows.Count != 2 || !form.snapshot.Text.Contains("현재 세션 전체") || form.powerSi.Text.Length != 0)
                            throw new InvalidOperationException("General Slave snapshot retained a PowerSI output observation.");
                        form.ApplySnapshot(currentServer, new MachineStatus { LocalTime = captured.LocalTime,
                            PowerSiOnly = true, Processes = new ProcessInventory() });
                        if (form.processes.Rows.Count != 0 || !form.snapshot.Text.Contains("POWERSI/PWRSI만") ||
                            !form.snapshot.Text.Contains("표시 0개") || form.powerSi.Text.Length != 0)
                            throw new InvalidOperationException("Empty filtered snapshot looked like all programs disappeared.");
                        form.ApplySnapshot(currentServer, captured);
                        if (form.processes.Rows.Count != 2 || !form.snapshot.Text.Contains("현재 세션 전체"))
                            throw new InvalidOperationException("General snapshot retained a filtered scope label.");
                        form.ApplyActivity(currentServer, "LISTENING");
                        if (!form.state.Text.Contains("LISTENING") || !form.state.Text.Contains("Master LOG READY"))
                            throw new InvalidOperationException("Slave listening activity was not visible.");
                        form.ApplyActivity(currentServer, "CLIENT_CONNECTED");
                        if (!form.state.Text.Contains("인증 확인 중")) throw new InvalidOperationException("Slave authentication activity was not visible.");
                        form.ApplyActivity(currentServer, "STATUS_CAPTURING");
                        if (!form.state.Text.Contains("프로그램 상태 수집 중") || !form.state.Text.Contains("약0.5초"))
                            throw new InvalidOperationException("Slave process capture activity was not visible.");
                        form.ApplyActivity(currentServer, "STATUS_SENT");
                        if (!form.state.Text.Contains("상태 응답 완료") || form.statusReplies != 1)
                            throw new InvalidOperationException("Slave status reply activity was not visible.");
                        form.ApplyActivity(currentServer, "CLIENT_REJECTED");
                        if (!form.state.Text.Contains("거부됨")) throw new InvalidOperationException("Slave rejection activity was not visible.");
                        form.ApplySnapshot(currentServer, powerSiCaptured);
                        if (form.powerSi.Text != observation.Summary) throw new InvalidOperationException("Slave PowerSI output observation did not recover after a new request.");
                        string currentState = form.state.Text;
                        string currentSnapshot = form.snapshot.Text;
                        string currentPowerSi = form.powerSi.Text;
                        form.server = staleServer;
                        form.ApplyActivity(currentServer, "STATUS_SENT");
                        form.ApplySnapshot(currentServer, powerSiCaptured);
                        if (form.state.Text != currentState || form.snapshot.Text != currentSnapshot || form.powerSi.Text != currentPowerSi || form.statusReplies != 1)
                            throw new InvalidOperationException("Stale Slave activity changed a new session.");
                        form.ApplyActivity(staleServer, "STOPPED");
                        if (!form.state.Text.StartsWith("STOPPED") || !form.state.Text.Contains("Master LOG READY"))
                            throw new InvalidOperationException("Slave stopped activity was not visible.");
                        string stoppedState = form.state.Text;
                        string stoppedSnapshot = form.snapshot.Text;
                        string stoppedPowerSi = form.powerSi.Text;
                        form.server = null;
                        form.ApplyActivity(staleServer, "STATUS_SENT");
                        form.ApplySnapshot(staleServer, powerSiCaptured);
                        if (form.state.Text != stoppedState || form.snapshot.Text != stoppedSnapshot || form.powerSi.Text != stoppedPowerSi)
                            throw new InvalidOperationException("Stale Slave activity changed a stopped session.");
                    }
                    var excerptVision = PowerSiObservation.VisionLogExcerpt("Simulation completed.", DateTime.UtcNow);
                    excerptVision.LocalEvidence = "private-ocr-comparison-sentinel";
                    excerptVision.LocalCaptureInfo = "private-crop-info-sentinel";
                    using (var source = new Bitmap(40, 30))
                    using (var output = new MemoryStream())
                    {
                        source.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                        excerptVision.LocalFullImage = output.ToArray();
                        excerptVision.LocalImage = PowerSiScreenCapture.Crop(new PowerSiFrame { Png = output.ToArray(), PixelSize = source.Size },
                            new Rectangle(5, 6, 20, 10));
                    }
                    var fullBuffer = new OutputBufferResult { Code = "BUFFER_READ", Method = "NATIVE_WM_GETTEXT", Detail = "B1|2|1|1|0",
                        Text = "private-full-buffer-sentinel" + new string('x', 8000) + "last line" };
                    fullBuffer.CharacterCount = fullBuffer.Text.Length;
                    fullBuffer.LineCount = OutputBufferCapture.CountLines(fullBuffer.Text);
                    form.RenderOutputBuffer(fullBuffer);
                    if (form.visionPreview.Enabled || form.replayVision.Enabled) throw new InvalidOperationException("Comparison enabled before LLM completion.");
                    form.RenderOutputBuffer(fullBuffer, excerptVision);
                    form.log.WriteOutputBuffer(fullBuffer);
                    if (!form.visionPreview.Enabled || form.lastBuffer.Text != fullBuffer.Text || form.powerSi.Text.Length > 4500 ||
                        !form.powerSi.Text.Contains("last line") || File.ReadAllText(form.log.Path).Contains("private-full-buffer-sentinel"))
                        throw new InvalidOperationException("Full buffer was truncated, not previewable, or logged.");
                    using (var comparison = form.CreateComparisonDialog())
                    {
                        var columns = comparison.Controls.OfType<SplitContainer>().Single();
                        var right = columns.Panel2.Controls.OfType<SplitContainer>().Single();
                        if (columns.Panel1.Controls.OfType<TextBox>().Single().Text != fullBuffer.Text ||
                            Transcript(right).Text != excerptVision.LocalEvidence ||
                            columns.Panel1.Width < 300 || right.Panel2.Height < 100)
                            throw new InvalidOperationException("Comparison lost full text/OCR or clipped panels.");
                        var selector = right.Panel1.Controls.OfType<ComboBox>().Single();
                        var picture = right.Panel1.Controls.OfType<PictureBox>().Single();
                        if (selector.SelectedIndex != 0 || !selector.Text.StartsWith("문자 전사 실제 입력") || picture.Image.Size != new Size(20, 10))
                            throw new InvalidOperationException("Comparison did not default to the actual OCR input.");
                        selector.SelectedIndex = 1;
                        if (!selector.Text.StartsWith("PowerSI 전체") || picture.Image.Size != new Size(40, 30))
                            throw new InvalidOperationException("Full source context unavailable.");
                    }
                    if (form.replayVision.Enabled) throw new InvalidOperationException("Replay enabled without a saved frame.");
                    var savedFrame = new PowerSiFrame { Png = excerptVision.LocalFullImage, PixelSize = new Size(40, 30), CapturedUtc = DateTime.UtcNow };
                    var receivedUtc = savedFrame.CapturedUtc.AddSeconds(1);
                    excerptVision.LocalFrame = savedFrame;
                    excerptVision.LocalSampleId = new string('A', 64);
                    excerptVision.LocalOcrSampleId = new string('B', 64);
                    excerptVision.LocalModelInfo = new LocalVisionModel { Id = "gemma-e4b", Quantization = "Q4_K_M" };
                    excerptVision.LocalElapsedMs = 1000;
                    excerptVision.LocalPaneImage = PowerSiScreenCapture.Crop(savedFrame, new Rectangle(5, 10, 30, 15));
                    excerptVision.LocalSuggestedImage = PowerSiScreenCapture.Crop(savedFrame, new Rectangle(5, 10, 10, 5));
                    form.RenderOutputBuffer(fullBuffer, excerptVision, receivedUtc);
                    if (!form.replayVision.Enabled || form.replayVision.Text != "같은 OCR 이미지 재판독" ||
                        form.outputAll.Text != "PowerSI 전체 수집")
                        throw new InvalidOperationException("Completed capture did not distinguish exact OCR replay from a fresh comparison.");
                    form.busy = true; form.UpdateButtons();
                    if (form.replayVision.Enabled) throw new InvalidOperationException("Replay enabled while busy.");
                    form.busy = false; form.closing = true; form.UpdateButtons();
                    if (form.replayVision.Enabled) throw new InvalidOperationException("Replay enabled while closing.");
                    form.closing = false;
                    using (var replayIdentity = SlaveIdentity.Create())
                    using (var replayServer = new StatusServer(replayIdentity, IPAddress.Loopback, 0))
                    {
                        form.server = replayServer; form.UpdateButtons();
                        if (form.replayVision.Enabled) throw new InvalidOperationException("Replay enabled with a listener.");
                        form.server = null;
                    }
                    var largerModel = PowerSiObservation.VisionLogExcerpt("38.000 MHz / 1234567890", DateTime.UtcNow);
                    largerModel.LocalEvidence = "31B exact digits: 1234567890";
                    largerModel.LocalModelInfo = new LocalVisionModel { Id = "gemma-31b", Quantization = "Q6_K" };
                    largerModel.LocalElapsedMs = 2000;
                    largerModel.LocalPaneImage = PowerSiScreenCapture.Crop(savedFrame, new Rectangle(4, 10, 32, 16));
                    largerModel.LocalImage = excerptVision.LocalImage;
                    largerModel.LocalSuggestedImage = excerptVision.LocalSuggestedImage;
                    largerModel.LocalFullImage = savedFrame.Png;
                    using (var replayCancellation = new CancellationTokenSource())
                    {
                        form.snapshotCancellation = replayCancellation;
                        form.replayInProgress = true;
                        form.UpdateButtons();
                        if (form.replayVision.Enabled || !form.stop.Enabled || form.visionSetup.Enabled)
                            throw new InvalidOperationException("Replay could overlap another request or settings change.");
                        if (!form.RenderReplay(replayCancellation, fullBuffer, savedFrame, largerModel, receivedUtc) ||
                            form.comparisonRuns.Count != 2 || largerModel.LocalFrame != savedFrame ||
                            largerModel.LocalSampleId != excerptVision.LocalSampleId || form.lastBufferReceivedUtc != receivedUtc)
                            throw new InvalidOperationException("Replay lost saved frame, buffer timestamp, or prior model result.");
                        form.snapshotCancellation = null; form.replayInProgress = false;
                    }
                    form.UpdateButtons();
                    using (var comparison = form.CreateComparisonDialog())
                    {
                        var columns = comparison.Controls.OfType<SplitContainer>().Single();
                        var right = columns.Panel2.Controls.OfType<SplitContainer>().Single();
                        var runs = comparison.Controls.OfType<ComboBox>().Single();
                        var newerPicture = right.Panel1.Controls.OfType<PictureBox>().Single();
                        if (runs.SelectedIndex != 1 || !runs.Text.Contains("gemma-31b / Q6_K / 2.00") ||
                            !runs.Text.Contains("thinking OFF 요청·실제 적용 미확인") ||
                            Transcript(right).Text != largerModel.LocalEvidence || newerPicture.Image.Size != new Size(20, 10))
                            throw new InvalidOperationException("Comparison did not select the latest model result.");
                        runs.SelectedIndex = 0;
                        var images = right.Panel1.Controls.OfType<ComboBox>().Single();
                        var picture = right.Panel1.Controls.OfType<PictureBox>().Single();
                        if (!runs.Text.Contains("gemma-e4b / Q4_K_M / 1.00") || !newerPicture.IsDisposed ||
                            columns.Panel1.Controls.OfType<TextBox>().Single().Text != fullBuffer.Text ||
                            Transcript(right).Text != excerptVision.LocalEvidence ||
                            !ReferenceEquals(form.lastObservation, largerModel) || !ReferenceEquals(form.lastBuffer, fullBuffer) ||
                            images.Items.Count != 4 || !images.Text.StartsWith("문자 전사 실제 입력") || picture.Image.Size != new Size(20, 10))
                            throw new InvalidOperationException("Run selection changed saved state, leaked controls, or displayed the wrong OCR/crop.");
                        images.SelectedIndex = 1;
                        if (!images.Text.StartsWith("Output 전체 본문") || picture.Image.Size != new Size(30, 15))
                            throw new InvalidOperationException("The full Output pane was not available separately from the actual OCR input.");
                        images.SelectedIndex = 2;
                        if (!images.Text.StartsWith("LLM 제안") || picture.Image.Size != new Size(10, 5))
                            throw new InvalidOperationException("Raw model-proposed crop was not retained.");
                    }
                    using (var cancelledReplay = new CancellationTokenSource())
                    {
                        form.snapshotCancellation = cancelledReplay; form.replayInProgress = true;
                        form.StopServer();
                        if (!cancelledReplay.IsCancellationRequested || !form.replayVision.Enabled || form.replayInProgress ||
                            form.comparisonRuns.Count != 2 || !ReferenceEquals(form.lastObservation, largerModel) ||
                            !ReferenceEquals(form.lastBuffer, fullBuffer))
                            throw new InvalidOperationException("Cancelling replay discarded the completed comparison or prevented retry.");
                        bool cancelled = false;
                        try { form.RenderReplay(cancelledReplay, fullBuffer, savedFrame, excerptVision, receivedUtc); }
                        catch (OperationCanceledException) { cancelled = true; }
                        if (!cancelled || !ReferenceEquals(form.lastObservation, largerModel))
                            throw new InvalidOperationException("Cancelled replay replaced a completed result.");
                    }
                    using (var staleReplay = new CancellationTokenSource())
                        if (form.RenderReplay(staleReplay, fullBuffer, savedFrame, excerptVision, receivedUtc))
                            throw new InvalidOperationException("Stale replay callback was accepted.");
                    using (var disabledRequest = new CancellationTokenSource())
                    {
                        form.snapshotCancellation = disabledRequest;
                        var disabled = PowerSiObservation.VisionUnavailable("VISION_NOT_CONFIGURED");
                        if (!form.RenderReplay(disabledRequest, fullBuffer, savedFrame, disabled, receivedUtc) ||
                            !ReferenceEquals(disabled.LocalImage, largerModel.LocalImage) || disabled.LocalVisionMode != "OCR_ONLY")
                            throw new InvalidOperationException("Pre-request failure discarded the OCR crop needed for retry.");
                        form.comparisonRuns.Remove(disabled);
                        form.RenderOutputBuffer(fullBuffer, largerModel, receivedUtc);
                        form.snapshotCancellation = null;
                    }
                    for (int index = 0; index < 7; index++)
                    {
                        var additional = PowerSiObservation.VisionUnavailable("VISION_MODEL_UNAVAILABLE");
                        additional.LocalFrame = savedFrame;
                        form.RenderOutputBuffer(fullBuffer, additional, receivedUtc);
                    }
                    if (form.comparisonRuns.Count != 8 || form.comparisonRuns.Contains(excerptVision) || !form.replayVision.Enabled)
                        throw new InvalidOperationException("Same-frame run history exceeded its bound or failed to retain retry data.");
                    form.RenderOutputBuffer(fullBuffer);
                    if (form.comparisonRuns.Count != 0 || form.replayVision.Enabled)
                        throw new InvalidOperationException("New combined capture retained prior run history.");
                    form.ClearPowerSi();
                    if (form.lastBuffer != null || form.visionPreview.Enabled || form.replayVision.Enabled || form.comparisonRuns.Count != 0)
                        throw new InvalidOperationException("Old full buffer remained visible.");
                    excerptVision.LocalEvidence = "[Output 로그] private-excerpt-sentinel\r\nSimulation completed.";
                    excerptVision.LocalVisionMode = "OCR_ONLY";
                    excerptVision.LocalRequestTimeoutSeconds = 90;
                    form.RenderPowerSi(excerptVision);
                    form.log.WritePowerSi(excerptVision);
                    if (!form.powerSi.Text.Contains("Simulation completed.") || form.powerSi.Text.Contains("FREQ") ||
                        File.ReadAllText(form.log.Path).Contains("private-excerpt-sentinel"))
                        throw new InvalidOperationException("Output excerpt not displayed locally or leaked into log.");
                    var rejectedVision = PowerSiObservation.VisionUnavailable("VISION_INVALID_RESPONSE");
                    rejectedVision.LocalFailure = "CONTENT_TEXT_CONTROL";
                    rejectedVision.LocalEvidence = "[UNVALIDATED LM STUDIO RESPONSE — NOT USED AS SIMULATION STATUS]\r\n" +
                        "Local failure: CONTENT_TEXT_CONTROL\r\nSlave 내부 확인용. 원문은 로그/메신저로 보내지 않습니다.\r\n" +
                        "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"private-response-sentinel\\u0000\"}}]}";
                    rejectedVision.LocalModel = "private-model-sentinel";
                    rejectedVision.LocalImage = new byte[] { 1, 2, 3 };
                    form.RenderPowerSi(rejectedVision);
                    form.log.WritePowerSi(rejectedVision);
                    var privateLog = File.ReadAllText(form.log.Path);
                    if (!privateLog.Contains("code=VISION_RUN") || !privateLog.Contains("model_id=gemma-e4b") || !privateLog.Contains("quantization=Q4_K_M") ||
                        !privateLog.Contains(" sample=" + new string('A', 64)) || !privateLog.Contains(" ocr_sample=" + new string('B', 64)) ||
                        !privateLog.Contains("mode=OCR_ONLY crop_reused=1 request_timeout_s=90") ||
                        !privateLog.Contains("transcript_policy=ALL_VISIBLE_V1 thinking_requested=off thinking_effective=UNKNOWN ocr_max_tokens=4096"))
                        throw new InvalidOperationException("Selected model comparison metadata was not written to the log.");
                    if (!form.visionPreview.Enabled || !form.powerSi.Text.Contains("CONTENT_TEXT_CONTROL") ||
                        privateLog.Contains("private-response-sentinel") || privateLog.Contains("private-model-sentinel") ||
                        rejectedVision.Serialize().Contains("private") || !privateLog.Contains("data=CONTENT_TEXT_CONTROL"))
                        throw new InvalidOperationException("Rejected vision response escaped its local-only preview.");
                    if (privateLog.Contains("private-crop-info-sentinel")) throw new InvalidOperationException("Crop UI data was logged.");
                    rejectedVision.LocalImage = null;
                    rejectedVision.LocalFullImage = excerptVision.LocalFullImage;
                    form.RenderOutputBuffer(fullBuffer, rejectedVision);
                    using (var comparison = form.CreateComparisonDialog())
                    {
                        var right = comparison.Controls.OfType<SplitContainer>().Single().Panel2.Controls.OfType<SplitContainer>().Single();
                        var selector = right.Panel1.Controls.OfType<ComboBox>().Single();
                        if (selector.Items.Count != 1 || !selector.Text.StartsWith("PowerSI 전체"))
                            throw new InvalidOperationException("Failed localization displayed whole image as crop.");
                        if (Transcript(right).Text != rejectedVision.LocalEvidence || !Comparison(right).Text.StartsWith("원문 대조 없음") ||
                            TranscriptOf(rejectedVision) != null || form.comparisonReports.ContainsKey(rejectedVision) ||
                            form.replayVision.Text != "저장 화면 재판독")
                            throw new InvalidOperationException("Rejected JSON was compared as OCR text or lost its local diagnostic preview.");
                    }
                    var failedBundle = form.BuildBundleContent(new[] { rejectedVision });
                    if (failedBundle.Runs[0].Transcript != null || failedBundle.Runs[0].Comparison != null || failedBundle.Runs[0].TranscriptValidated)
                        throw new InvalidOperationException("Rejected JSON was included as a transcript or OCR mismatch report.");
                    using (var memory = new MemoryStream())
                    {
                        DiagnosticBundle.Write(memory, failedBundle);
                        memory.Position = 0;
                        using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Read))
                            if (archive.Entries.Any(entry => entry.FullName.EndsWith("transcript.txt", StringComparison.Ordinal) ||
                                entry.FullName.EndsWith("comparison.txt", StringComparison.Ordinal)))
                                throw new InvalidOperationException("Rejected JSON created transcript/comparison files in the diagnostic ZIP.");
                    }
                    // Learned auto-copy position, 원문 대조 and the diagnostic bundle content.
                    PowerSiFrame bodyFrame;
                    using (var bitmap = new Bitmap(600, 420, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    {
                        using (var graphics = Graphics.FromImage(bitmap))
                        using (var background = new SolidBrush(Color.FromArgb(232, 232, 232)))
                        {
                            graphics.Clear(Color.DarkBlue);
                            graphics.FillRectangle(background, new Rectangle(20, 40, 360, 320));
                            graphics.FillRectangle(background, new Rectangle(410, 40, 170, 320));
                            for (int y = 55; y < 350; y += 16) graphics.FillRectangle(Brushes.Black, 30, y, 300, 2);
                        }
                        using (var png = new MemoryStream())
                        {
                            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                            bodyFrame = new PowerSiFrame { Png = png.ToArray(), PixelSize = bitmap.Size, CapturedUtc = DateTime.UtcNow };
                        }
                    }
                    var copiedBuffer = new OutputBufferResult { Code = "USER_COPY_READ", Method = "USER_CLIPBOARD",
                        Detail = "SOURCE_PID_MATCH", Text = "첫 줄\r\nAFS Current Frequency (MHz) = 860.000\r\n마지막 줄" };
                    copiedBuffer.CharacterCount = copiedBuffer.Text.Length;
                    copiedBuffer.LineCount = OutputBufferCapture.CountLines(copiedBuffer.Text);
                    var anchorVision = PowerSiObservation.VisionLogExcerpt("AFS Current Frequency (MHz) = 860.000", DateTime.UtcNow);
                    anchorVision.LocalEvidence = "[Output 하단 확대 판독 — LLM 전사본, 숫자 정확도 미검증]\r\n캡처 UTC 2026-09-11 00:00:00\r\n" +
                        "AFS Current Frequency (MHz) = 860.000\r\n마지막  줄";
                    var bracketed = PowerSiObservation.VisionLogExcerpt("[Warning] 123\r\n[Done] 456", DateTime.UtcNow);
                    bracketed.LocalEvidence =
                        "[Output 하단 확대 판독 — LLM 전사본, 숫자 정확도 미검증]\r\n캡처 UTC 2026-09-11 00:00:00\r\n[Warning] 123\r\n[Done] 456";
                    if (TranscriptOf(bracketed) != "[Warning] 123\r\n[Done] 456")
                        throw new InvalidOperationException("Bracket-prefixed Output text was removed as an evidence header.");
                    var unreadable = PowerSiObservation.VisionLogExcerpt(null, DateTime.UtcNow);
                    unreadable.LocalEvidence = bracketed.LocalEvidence;
                    bracketed.LocalFailure = "READ_CROP_FAILED";
                    if (TranscriptOf(unreadable) != null || TranscriptOf(bracketed) != null)
                        throw new InvalidOperationException("A local evidence envelope alone was accepted as successful OCR.");
                    anchorVision.LocalFrame = bodyFrame;
                    anchorVision.LocalFullImage = bodyFrame.Png;
                    anchorVision.LocalFrameSize = bodyFrame.PixelSize;
                    anchorVision.LocalBodyDiagnostics = "B2|600|420|3|1|1|0|0|1|0";
                    form.RenderOutputBuffer(copiedBuffer, anchorVision, DateTime.UtcNow);
                    using (var comparison = form.CreateComparisonDialog())
                    {
                        var columns = comparison.Controls.OfType<SplitContainer>().Single();
                        var right = columns.Panel2.Controls.OfType<SplitContainer>().Single();
                        var report = Comparison(right).Text;
                        if (!report.StartsWith("전사본 2행 대조") || !report.Contains("일치 1") || !report.Contains("정규화 일치 1") ||
                            !report.Contains("원문 대응 없음 0") || !report.Contains("전체 줄·최신 줄 전사 여부: 미검증") || !report.Contains("860.000") ||
                            Transcript(right).Text != anchorVision.LocalEvidence)
                            throw new InvalidOperationException("원문 대조 did not compare the transcript against the collected full text.");
                        var bundleButton = comparison.Controls.OfType<Panel>().Single().Controls.OfType<Button>().Single();
                        if (!bundleButton.Enabled || !bundleButton.Text.StartsWith("진단 묶음 저장"))
                            throw new InvalidOperationException("Diagnostic bundle save was unavailable in the comparison view.");
                    }
                    // A failed auto copy keeps its own capture and shows its code in the status line.
                    form.lastAutoCopyCode = "AUTO_COPY_OCCLUDED";
                    form.autoCopyFrame = bodyFrame.Png;
                    form.autoCopyFailure = "AUTO_COPY_OCCLUDED OCCLUDER|SELF";
                    form.UpdateAnchorState();
                    if (!form.anchorState.Text.Contains("최근 결과: AUTO_COPY_OCCLUDED"))
                        throw new InvalidOperationException("Status line did not show the last auto copy outcome.");
                    var bundle = form.BuildBundleContent(new[] { anchorVision });
                    if (bundle.FullText != copiedBuffer.Text || bundle.BufferReceivedUtc != form.lastBufferReceivedUtc ||
                        bundle.LogFilePath != form.log.Path || bundle.Runs.Count != 1 ||
                        bundle.Runs[0].Comparison == null || bundle.Runs[0].Transcript == null ||
                        bundle.Runs[0].BodyDiagnostics != anchorVision.LocalBodyDiagnostics ||
                        !ReferenceEquals(bundle.AutoCopyFramePng, bodyFrame.Png) ||
                        bundle.AutoCopyLastFailure != "AUTO_COPY_OCCLUDED OCCLUDER|SELF" ||
                        bundle.Notes.Contains("최근 자동 복사:") ||
                        !bundle.Runs[0].Metadata.StartsWith("VISION_RUN") || !bundle.Runs[0].Metadata.Contains("frame=600x420") ||
                        !bundle.Runs[0].Metadata.Contains("body=B2|600|420") || bundle.Version != LinkVersion.Value)
                        throw new InvalidOperationException("Diagnostic bundle content lost the run, comparison or metadata.");
                    using (var memory = new MemoryStream())
                    {
                        DiagnosticBundle.Write(memory, bundle);
                        memory.Position = 0;
                        using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Read))
                            if (archive.GetEntry("full-text.txt") == null || archive.GetEntry("slave-log.txt") == null ||
                                archive.GetEntry("README.txt") == null || archive.GetEntry("auto-copy-frame.png") == null ||
                                !archive.Entries.Any(entry => entry.FullName.EndsWith("comparison.txt", StringComparison.Ordinal)) ||
                                !archive.Entries.Any(entry => entry.FullName.EndsWith("full-frame.png", StringComparison.Ordinal)))
                                throw new InvalidOperationException("Diagnostic bundle did not contain the exported screen/text entries.");
                    }
                    var comparisonLog = File.ReadAllText(form.log.Path);
                    if (!comparisonLog.Contains("code=TRANSCRIPT_COMPARE summary=T1|2|1|1|0|0|0|1") ||
                        comparisonLog.Contains("860.000") || comparisonLog.Contains("마지막"))
                        throw new InvalidOperationException("Anchor/comparison logging lost its metadata or leaked Output text.");
                    form.log.Write("OUTPUT_PREPARE", "pid=101 stage=selected hwnd=123 iconic=1 x=-32000 y=-32000");
                    form.log.Write("OUTPUT_PREPARE", "title=private/path");
                    var stateLog = File.ReadAllText(form.log.Path);
                    if (!stateLog.Contains("iconic=1 x=-32000 y=-32000") || stateLog.Contains("private/path"))
                        throw new InvalidOperationException("Signed native window metadata was lost or private text accepted.");
                    form.RenderOutputBuffer(new OutputBufferResult { Code = "SC_MINIMIZED", Method = "NONE", Detail = "NONE" });
                    if (!form.powerSi.Text.Contains("Windows가 최소화 상태로 보고") || !form.powerSi.Text.Contains("활성화·입력을 생략"))
                        throw new InvalidOperationException("Minimized window diagnosis was not explained in the UI.");
                    var first = new OutputSample { Process = new ProcessState { Pid = 101, StartUtcTicks = 1001 }, SessionId = 1,
                        Buffer = copiedBuffer, Vision = anchorVision, ReceivedUtc = DateTime.UtcNow };
                    first.Runs.Add(anchorVision);
                    var secondVision = PowerSiObservation.VisionLogExcerpt("SECOND 789", DateTime.UtcNow);
                    secondVision.LocalEvidence = "SECOND 789";
                    var second = new OutputSample { Process = new ProcessState { Pid = 202, StartUtcTicks = 2002 }, SessionId = 1,
                        Buffer = new OutputBufferResult { Code = "AUTO_COPY_READ", Method = "AUTO_CLIPBOARD", Text = "SECOND 789", CharacterCount = 10, LineCount = 1 },
                        Vision = secondVision, ReceivedUtc = DateTime.UtcNow };
                    second.Runs.Add(secondVision);
                    form.outputSamples.AddRange(new[] { first, second });
                    form.SelectOutputSample(first);
                    form.SelectOutputSample(second);
                    if (form.lastBuffer.Text != "SECOND 789" || !form.ComparisonFor(secondVision).Contains("일치 1"))
                        throw new InvalidOperationException("Target selection mixed instance buffers or comparisons.");
                    var batch = form.BuildExportContent(new[] { secondVision });
                    if (batch.Targets.Count != 2 || batch.Targets[0].FullText != copiedBuffer.Text || batch.Targets[1].FullText != "SECOND 789" ||
                        batch.Targets[0].TargetPid != 101 || batch.Targets[1].TargetPid != 202)
                        throw new InvalidOperationException("Batch export mixed per-instance evidence.");
                    using (var memory = new MemoryStream())
                    {
                        DiagnosticBundle.Write(memory, batch); memory.Position = 0;
                        using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Read))
                        {
                            using (var reader = new StreamReader(archive.GetEntry("targets/01-pid101/full-text.txt").Open()))
                                if (reader.ReadToEnd() != copiedBuffer.Text) throw new InvalidOperationException("First buffer lost in batch ZIP.");
                            using (var reader = new StreamReader(archive.GetEntry("targets/02-pid202/full-text.txt").Open()))
                                if (reader.ReadToEnd() != "SECOND 789") throw new InvalidOperationException("Second buffer lost in batch ZIP.");
                            if (archive.GetEntry("full-text.txt") != null) throw new InvalidOperationException("Batch root exposed a misleading single buffer.");
                        }
                    }
                    form.lastBuffer = new OutputBufferResult { Code = "SC_PENDING", Method = "NONE" };
                    form.lastObservation = PowerSiObservation.VisionUnavailable("OUTPUT_UNAVAILABLE");
                    form.lastObservation.LocalFailure = "PENDING_WINDOWS_NOT_RESPONDING_INPUT_STOPPED";
                    form.comparisonRuns.Clear(); form.comparisonRuns.Add(form.lastObservation);
                    form.SaveSelectedSample();
                    form.batchInProgress = true;
                    form.StopServer();
                    if (form.outputAll.Enabled || form.outputTargets.Enabled || form.visionSetup.Enabled || form.refresh.Enabled)
                        throw new InvalidOperationException("A cancelling batch permitted new work before input cleanup.");
                    form.batchInProgress = false;
                    batch = form.BuildExportContent(null);
                    if (batch.Targets.Count != 2 || batch.Targets[0].FullText != copiedBuffer.Text || batch.Targets[1].FullText != null ||
                        batch.Targets[1].BufferCode != "SC_PENDING" || batch.Targets[1].Runs[0].Transcript != null)
                        throw new InvalidOperationException("Pending/Stop lost prior targets or reused another target's text.");
                    var emptyVision = PowerSiObservation.VisionLogExcerpt(null, DateTime.UtcNow);
                    emptyVision.LocalVisibleEmpty = true;
                    emptyVision.LocalFrame = anchorVision.LocalFrame;
                    emptyVision.LocalFullImage = anchorVision.LocalFullImage;
                    emptyVision.LocalImage = anchorVision.LocalImage;
                    if (!RecordVisibleEmpty(second, emptyVision) || RecordVisibleEmpty(first, anchorVision))
                        throw new InvalidOperationException("Visible-empty routing changed a nonempty target.");
                    foreach (var failure in new[] { "TARGET_CANCELLED", "TARGET_TIMEOUT" }) RecordTargetFailure(second, failure);
                    if (second.Buffer.Code != "OUTPUT_VISIBLE_EMPTY" || second.Buffer.Text != null || second.Vision.LocalFailure != null)
                        throw new InvalidOperationException("Stop or timeout discarded a completed visible-empty observation.");
                    second.Runs.Clear(); second.Runs.Add(emptyVision);
                    form.selectedSample = null;
                    form.SelectOutputSample(second);
                    batch = form.BuildExportContent(null);
                    if (form.CanReplayVision() || form.ComparisonFor(emptyVision) != null ||
                        second.Buffer.Text != null || !second.ToString().Contains("보이는 내용 없음") ||
                        batch.Targets[0].FullText != copiedBuffer.Text || batch.Targets[1].FullText != null ||
                        batch.Targets[1].BufferCode != "OUTPUT_VISIBLE_EMPTY" || batch.Targets[1].Runs[0].Result != "OUTPUT_VISIBLE_EMPTY" ||
                        batch.Targets[1].Runs[0].Transcript != null || !SlaveLog.ComparisonMetadata(emptyVision).Contains("visible_empty=1"))
                        throw new InvalidOperationException("Visible-empty evidence became full text, OCR success, or another PID's data.");
                    Console.WriteLine("PASS: visible-empty target stays distinct from full-buffer success and preserves the other PID");
                    form.SelectOutputSample(first);
                    batch = form.BuildExportContent(null);
                    if (!batch.Targets[0].Notes.Contains("USER_COPY_READ") || batch.Targets[0].Notes.Contains("OUTPUT_VISIBLE_EMPTY") ||
                        !batch.Targets[1].Notes.Contains("OUTPUT_VISIBLE_EMPTY") || batch.Targets[1].Notes.Contains("USER_COPY_READ"))
                        throw new InvalidOperationException("Diagnostic notes inherited another selected target's result.");
                    Console.WriteLine("PASS: per-PID diagnostic notes remain independent of the selected target");
                    var copyTime = first.ReceivedUtc;
                    RecordTargetFailure(first, "TARGET_TIMEOUT");
                    if (first.Buffer.Text != copiedBuffer.Text || first.ReceivedUtc != copyTime || first.Vision.LocalFailure != "TARGET_TIMEOUT" ||
                        TranscriptOf(first.Vision) != null)
                        throw new InvalidOperationException("Later OCR failure discarded independently collected full text or kept a valid transcript.");
                    Console.WriteLine("PASS: two-target selection, independent comparisons/ZIP payloads, pending and Stop preservation");
                    form.ClearPowerSi();
                    using (var reader = new FileStream(form.log.Path, FileMode.Open, FileAccess.Read, FileShare.None))
                        if (reader.Length == 0) throw new InvalidOperationException("Slave log unavailable while form alive.");
                    // A shown window is needed for WinForms to lay out and scroll visible child controls.
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    foreach (Control control in form.Controls)
                        if (!form.DisplayRectangle.Contains(control.Bounds))
                            throw new InvalidOperationException("Slave control outside scrollable content: " + control.GetType().Name + " " + control.Bounds);
                    form.ClientSize = new Size(640, 480);
                    form.PerformLayout();
                    if (!form.VerticalScroll.Visible || !form.HorizontalScroll.Visible)
                        throw new InvalidOperationException("Small Slave window does not expose scrollbars.");
                    form.AutoScrollPosition = new Point(0, form.DisplayRectangle.Height);
                    if (form.anchorState.Top < 0 || form.anchorState.Bottom > form.ClientSize.Height)
                        throw new InvalidOperationException("Slave Pending/status line is unreachable in a small window: " +
                            form.anchorState.Bounds + " / " + form.ClientRectangle + " / " + form.AutoScrollPosition);
                    Console.WriteLine("PASS: small Slave window scrolls to the final Pending/status line");
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
