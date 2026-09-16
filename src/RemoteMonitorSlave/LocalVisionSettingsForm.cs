using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using RemoteMonitorLink;

namespace RemoteMonitorSlave
{
    internal static class VisionSettingsStore
    {
        internal static LocalVisionSettings Load(string path)
        {
            if (!File.Exists(path)) return new LocalVisionSettings();
            if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Local vision settings too large.");
            var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            var result = new LocalVisionSettings { Enabled = (bool)values["enabled"], Port = (int)values["port"],
                ModelId = (string)values["model"], TimeoutSeconds = (int)values["timeout_seconds"] };
            // Older settings files have no auto-copy key; a missing or non-boolean value keeps the default.
            object autoCopy;
            result.AutoCopyEnabled = !values.TryGetValue("auto_copy", out autoCopy) || !(autoCopy is bool) || (bool)autoCopy;
            var encrypted = (string)values["protected_token"];
            if (encrypted.Length != 0)
            {
                var clear = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
                try { result.ApiToken = Encoding.UTF8.GetString(clear); }
                finally { Array.Clear(clear, 0, clear.Length); }
            }
            result.Validate(); return result;
        }
        internal static void Save(string path, LocalVisionSettings settings)
        {
            settings.Validate();
            var clear = Encoding.UTF8.GetBytes(settings.ApiToken ?? "");
            string encrypted;
            try { encrypted = clear.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(clear, 0, clear.Length); }
            var text = new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "enabled", settings.Enabled }, { "port", settings.Port }, { "model", settings.ModelId },
                { "timeout_seconds", settings.TimeoutSeconds }, { "auto_copy", settings.AutoCopyEnabled },
                { "protected_token", encrypted } });
            // This is a small per-user setting file. Incomplete settings fail disabled on the next launch.
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        internal static void SelfTest(string directory)
        {
            var path = Path.Combine(directory, "vision-settings-check.json");
            var settings = new LocalVisionSettings { Enabled = true, ModelId = "replaceable-vision-model", ApiToken = "private-test-token" };
            Save(path, settings); var read = Load(path);
            if (read.ModelId != settings.ModelId || read.ApiToken != settings.ApiToken || !read.Enabled || !read.AutoCopyEnabled ||
                File.ReadAllText(path).Contains(settings.ApiToken)) throw new InvalidOperationException("Local vision settings persistence failed.");
            settings.AutoCopyEnabled = false;
            Save(path, settings);
            if (Load(path).AutoCopyEnabled) throw new InvalidOperationException("Disabled Output auto copy was not persisted.");
            // A settings file written before the auto-copy option existed has no such key and must load with the default.
            var legacy = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            legacy.Remove("auto_copy");
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(legacy), new UTF8Encoding(false));
            if (!Load(path).AutoCopyEnabled) throw new InvalidOperationException("Settings without the auto copy key lost its default.");
        }
    }

    internal sealed class LocalVisionSettingsForm : Form
    {
        private readonly CheckBox enabled = new CheckBox { Text = "PowerSI 화면의 로컬 LLM 판독 사용", AutoSize = true };
        private readonly CheckBox autoCopy = new CheckBox { Text = "모든 PowerSI Output 자동 탐색·복사 (클릭·Ctrl+A/C)", AutoSize = true };
        private readonly NumericUpDown port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 1234 };
        private readonly NumericUpDown timeout = new NumericUpDown { Minimum = 15, Maximum = 90, Value = 60 };
        private readonly ComboBox model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        private readonly TextBox token = new TextBox { UseSystemPasswordChar = true };
        private readonly Label message = new Label();
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        internal LocalVisionSettings Result { get; private set; }

        internal LocalVisionSettingsForm(LocalVisionSettings settings)
        {
            Text = Program.Title + " — LM Studio 설정"; Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(640, 490); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            Controls.Add(new Label { Text = "같은 PC의 127.0.0.1에만 연결합니다. 화면/판독 원문은 Master에 보내지 않습니다.\r\nLM Studio에서 이미지 지원 모델을 로드하고 Developer → Start server를 켜세요.",
                Bounds = new Rectangle(18, 16, 604, 46) });
            enabled.SetBounds(18, 70, 500, 24); enabled.Checked = settings.Enabled;
            autoCopy.SetBounds(18, 100, 604, 24); autoCopy.Checked = settings.AutoCopyEnabled;
            autoCopy.AccessibleName = "PowerSI 크기 변경 없이 전경 전환, Output 탐색·검증 후 자동 복사. 최초 수동 복사 없음";
            Controls.Add(new Label { Text = "LM Studio 포트", Bounds = new Rectangle(18, 138, 140, 24) });
            port.SetBounds(170, 134, 100, 28); port.Value = settings.Port;
            Controls.Add(new Label { Text = "단계별 제한 (초)", Bounds = new Rectangle(320, 138, 135, 24) });
            timeout.SetBounds(472, 134, 100, 28); timeout.Value = settings.TimeoutSeconds;
            Controls.Add(new Label { Text = "모델 ID (빈칸: 이미지 지원 모델이 하나일 때 자동 선택)", Bounds = new Rectangle(18, 178, 604, 24) });
            model.SetBounds(18, 206, 440, 28); model.Text = settings.ModelId;
            var discover = new Button { Text = "로드된 모델 확인", Bounds = new Rectangle(470, 203, 152, 34) };
            Controls.Add(new Label { Text = "API token (LM Studio에 설정한 경우만; 이 Windows 사용자용으로 암호화 저장)", Bounds = new Rectangle(18, 252, 604, 24) });
            token.SetBounds(18, 280, 604, 28); token.Text = settings.ApiToken;
            Controls.Add(new Label { Text = "thinking OFF를 요청하지만 실제 적용 여부는 확인할 수 없습니다.\r\n모델별 비교 전에 LM Studio에서도 thinking을 끄고 저장한 같은 화면을 재판독하세요.",
                Bounds = new Rectangle(18, 318, 604, 42), AccessibleName = "thinking 비활성화 요청과 실제 적용 여부 구분" });
            message.SetBounds(18, 364, 604, 62);
            message.Text = "모델은 자동 다운로드/교체하지 않습니다. 선택한 모델이 없거나 이미지 입력을 지원하지 않으면 확인 불가로 표시합니다.\r\n" +
                "자동 복사는 창 크기를 바꾸지 않고 각 Output을 찾아 검증합니다. 응답 없는 창은 건너뛰며 클립보드는 바뀝니다.";
            var save = new Button { Text = "저장", Bounds = new Rectangle(404, 439, 100, 34) };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(516, 439, 106, 34) };
            Controls.AddRange(new Control[] { enabled, autoCopy, port, timeout, model, discover, token, message, save, cancel });
            CancelButton = cancel;
            discover.Click += async delegate
            {
                discover.Enabled = false; message.Text = "127.0.0.1의 로드된 이미지 모델 목록을 확인 중…";
                try
                {
                    var found = await LocalVisionClient.ListModelsAsync(Current(), closing.Token);
                    if (closing.IsCancellationRequested) return;
                    var selected = model.Text; model.Items.Clear();
                    foreach (var item in found) model.Items.Add(item.Id);
                    model.Text = selected.Length == 0 && found.Length == 1 ? found[0].Id : selected;
                    message.Text = "로드된 이미지 모델 " + found.Length + "개. 모델을 선택하고 판독 사용을 체크한 뒤 저장하세요.";
                }
                catch (OperationCanceledException) { }
                catch (LocalVisionException ex) { if (!closing.IsCancellationRequested) message.Text = "모델 확인 실패: " + ex.Code + " — 서버·버전·토큰·이미지 모델 로드를 확인하세요."; }
                catch { if (!closing.IsCancellationRequested) message.Text = "설정을 확인하세요."; }
                finally { if (!closing.IsCancellationRequested) discover.Enabled = true; }
            };
            save.Click += delegate
            {
                try { Result = Current(); Result.Validate(); DialogResult = DialogResult.OK; Close(); }
                catch { message.Text = "포트·모델 ID·토큰·시간 제한을 확인하세요."; }
            };
            FormClosing += delegate { closing.Cancel(); };
        }
        private LocalVisionSettings Current()
        {
            return new LocalVisionSettings { Enabled = enabled.Checked, Port = (int)port.Value, ModelId = model.Text.Trim(),
                ApiToken = token.Text.Trim(), TimeoutSeconds = (int)timeout.Value, AutoCopyEnabled = autoCopy.Checked };
        }
    }
}
