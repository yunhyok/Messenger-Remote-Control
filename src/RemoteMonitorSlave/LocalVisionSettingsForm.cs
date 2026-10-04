using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
            object autoCopy, blindCopy;
            result.AutoCopyEnabled = !values.TryGetValue("auto_copy", out autoCopy) || !(autoCopy is bool) || (bool)autoCopy;
            // Opt-in only: a missing key (every file before 0.3.11) or any other value keeps the LLM-free copy off.
            result.BlindCopyEnabled = values.TryGetValue("blind_copy", out blindCopy) && OptedIn(blindCopy);
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
                { "blind_copy", settings.BlindCopyEnabled }, { "protected_token", encrypted } });
            // This is a small per-user setting file. Incomplete settings fail disabled on the next launch.
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        // JSON true, the number 1 or the string "1" only; everything else (false, 0, "true", "yes", null, objects) is off.
        internal static bool OptedIn(object value)
        {
            return value is bool flag ? flag : value is int number ? number == 1 : value is string text && text == "1";
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
            // The LLM-free copy is opt-in: off by default, after a reset, without the key and for anything but an explicit 1/true.
            if (new LocalVisionSettings().BlindCopyEnabled || new LocalVisionSettings().Clone().BlindCopyEnabled ||
                Load(path).BlindCopyEnabled || Load(Path.Combine(directory, "missing-vision-settings.json")).BlindCopyEnabled)
                throw new InvalidOperationException("The LLM-free copy was enabled without an explicit opt-in.");
            foreach (var value in new object[] { true, 1, "1" })
                if (!OptedIn(value)) throw new InvalidOperationException("An explicit LLM-free copy opt-in was not read.");
            foreach (var value in new object[] { false, 0, 2, -1, "0", "true", "yes", "", null, 1.0m, new Dictionary<string, object>() })
                if (OptedIn(value)) throw new InvalidOperationException("A non-explicit value enabled the LLM-free copy.");
            settings.BlindCopyEnabled = true;
            Save(path, settings);
            var optedIn = Load(path);
            if (!optedIn.BlindCopyEnabled || !optedIn.Clone().BlindCopyEnabled) throw new InvalidOperationException("The LLM-free copy opt-in was not persisted.");
            var hand = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            foreach (var value in new object[] { 1, "1", false, "true" })
            {
                hand["blind_copy"] = value;
                File.WriteAllText(path, new JavaScriptSerializer().Serialize(hand), new UTF8Encoding(false));
                if (Load(path).BlindCopyEnabled != OptedIn(value)) throw new InvalidOperationException("blind_copy parsing changed on reload.");
            }
            hand.Remove("blind_copy");
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(hand), new UTF8Encoding(false));
            if (Load(path).BlindCopyEnabled) throw new InvalidOperationException("A settings file without blind_copy enabled the LLM-free copy.");
            settings.BlindCopyEnabled = false;
            Save(path, settings);
            if (Load(path).BlindCopyEnabled) throw new InvalidOperationException("Turning the LLM-free copy off was not persisted.");
        }
    }

    internal sealed class LocalVisionSettingsForm : Form
    {
        private readonly CheckBox enabled = new CheckBox { Text = "PowerSI 화면의 로컬 LLM 판독 사용", AutoSize = true };
        private readonly CheckBox autoCopy = new CheckBox { Text = "모든 PowerSI Output 자동 탐색·복사 (클릭·Ctrl+A/C)", AutoSize = true };
        private readonly CheckBox blindCopy = new CheckBox { Text = "LLM을 쓰지 않을 때도 대체 복사 허용 (저장 위치·Output 창 구조로 Ctrl+A/C)", AutoSize = true };
        private readonly NumericUpDown port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 1234 };
        private readonly NumericUpDown timeout = new NumericUpDown { Minimum = 15, Maximum = 90, Value = 60 };
        private readonly ComboBox model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        private readonly TextBox token = new TextBox { UseSystemPasswordChar = true };
        private readonly Label message = new Label();
        private readonly Font dialogFont = new Font("Segoe UI", 9F);
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        internal LocalVisionSettings Result { get; private set; }

        internal LocalVisionSettingsForm(LocalVisionSettings settings)
        {
            RemoteMonitorLink.AppIcon.Apply(this);
            Text = Program.Title + " — LM Studio 설정"; Font = dialogFont;
            ClientSize = new Size(640, 568); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            Controls.Add(new Label { Text = "같은 PC의 127.0.0.1에만 연결합니다. 화면/판독 원문은 Master에 보내지 않습니다.\r\nLM Studio에서 이미지 지원 모델을 로드하고 Developer → Start server를 켜세요.",
                Bounds = new Rectangle(18, 16, 604, 46) });
            enabled.SetBounds(18, 70, 500, 24); enabled.Checked = settings.Enabled;
            autoCopy.SetBounds(18, 100, 604, 24); autoCopy.Checked = settings.AutoCopyEnabled;
            autoCopy.AccessibleName = "PowerSI 크기 변경 없이 전경 전환, Output 탐색·검증 후 자동 복사. 최초 수동 복사 없음";
            // Explicit opt-in, only meaningful with auto copy on; its value is only ever true when the operator ticks it.
            blindCopy.SetBounds(18, 130, 604, 24); blindCopy.Checked = settings.BlindCopyEnabled; blindCopy.Enabled = autoCopy.Checked;
            const string blindNote = "켜면 LLM을 쓰지 않을 때도 PowerSI 창을 앞으로 가져와 클릭·Ctrl+A/C 입력을 보냅니다 (클립보드가 바뀝니다).";
            blindCopy.AccessibleDescription = blindNote;
            autoCopy.CheckedChanged += delegate { blindCopy.Enabled = autoCopy.Checked; };
            Controls.Add(new Label { Text = blindNote, Bounds = new Rectangle(38, 156, 584, 22), AccessibleName = "LLM 없이 대체 복사 입력 안내" });
            Controls.Add(new Label { Text = "LM Studio 포트", Bounds = new Rectangle(18, 190, 140, 24) });
            port.SetBounds(170, 186, 100, 28); port.Value = settings.Port;
            Controls.Add(new Label { Text = "단계별 제한 (초)", Bounds = new Rectangle(320, 190, 135, 24) });
            timeout.SetBounds(472, 186, 100, 28); timeout.Value = settings.TimeoutSeconds;
            Controls.Add(new Label { Text = "모델 ID (빈칸: 이미지 지원 모델이 하나일 때 자동 선택)", Bounds = new Rectangle(18, 230, 604, 24) });
            model.SetBounds(18, 258, 440, 28); model.Text = settings.ModelId;
            var discover = new Button { Text = "로드된 모델 확인", Bounds = new Rectangle(470, 255, 152, 34) };
            Controls.Add(new Label { Text = "API token (LM Studio에 설정한 경우만; 이 Windows 사용자용으로 암호화 저장)", Bounds = new Rectangle(18, 304, 604, 24) });
            token.SetBounds(18, 332, 604, 28); token.Text = settings.ApiToken;
            Controls.Add(new Label { Text = "thinking OFF를 요청하지만 실제 적용 여부는 확인할 수 없습니다.\r\n모델별 비교 전에 LM Studio에서도 thinking을 끄고 저장한 같은 화면을 재판독하세요.",
                Bounds = new Rectangle(18, 370, 604, 42), AccessibleName = "thinking 비활성화 요청과 실제 적용 여부 구분" });
            message.SetBounds(18, 416, 604, 88);
            message.Text = "모델은 자동 다운로드/교체하지 않습니다. 선택한 모델이 없거나 이미지 입력을 지원하지 않으면 확인 불가로 표시합니다.\r\n" +
                "자동 복사는 창 크기를 바꾸지 않고 각 Output을 찾아 검증합니다. 응답 없는 창은 건너뛰며 클립보드는 바뀝니다.\r\n" +
                "자동 복사 중에는 Slave 창이 잠시 최소화됐다가 원래 상태로 돌아옵니다.";
            var save = new Button { Text = "저장", Bounds = new Rectangle(404, 517, 100, 34) };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(516, 517, 106, 34) };
            Controls.AddRange(new Control[] { enabled, autoCopy, blindCopy, port, timeout, model, discover, token, message, save, cancel });
            CancelButton = cancel; AcceptButton = save;
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
                var candidate = Current();
                var problem = Problem(candidate);
                if (problem != null) { message.Text = problem; return; }
                try { Result = candidate; Result.Validate(); DialogResult = DialogResult.OK; Close(); }
                catch { message.Text = "포트·모델 ID·토큰·시간 제한을 확인하세요."; }
            };
            FormClosing += delegate { closing.Cancel(); };
        }
        // Internal for the Slave UI self-test (round trip of the dialog's values, including the blind_copy opt-in).
        internal LocalVisionSettings Current()
        {
            return new LocalVisionSettings { Enabled = enabled.Checked, Port = (int)port.Value, ModelId = model.Text.Trim(),
                ApiToken = token.Text.Trim(), TimeoutSeconds = (int)timeout.Value, AutoCopyEnabled = autoCopy.Checked,
                BlindCopyEnabled = blindCopy.Checked };
        }

        // Same rules as LocalVisionSettings.Validate, only to name the field that has to change. Anything this
        // does not recognize still falls back to the generic sentence above.
        private static string Problem(LocalVisionSettings candidate)
        {
            if (candidate.Port < 1 || candidate.Port > 65535) return "LM Studio 포트를 1~65535 범위로 입력하세요.";
            if (candidate.TimeoutSeconds < 15 || candidate.TimeoutSeconds > 90) return "단계별 제한을 15~90초로 입력하세요.";
            if (candidate.ModelId == null || candidate.ModelId.Length > 256 || candidate.ModelId.Any(char.IsControl))
                return "모델 ID를 확인하세요 — 256자 이하, 제어문자 없이 입력하거나 로드된 모델 확인 목록에서 선택하세요.";
            if (candidate.ApiToken == null || candidate.ApiToken.Length > 4096 || candidate.ApiToken.Any(c => c < 0x21 || c > 0x7e))
                return "API token을 확인하세요 — 공백·한글 없이 LM Studio에 설정한 값(4096자 이하)만 붙여넣으세요.";
            return null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Cancel before disposing: a model list still in flight checks IsCancellationRequested, not Token.
                try { closing.Cancel(); } catch (ObjectDisposedException) { }
                closing.Dispose();
            }
            base.Dispose(disposing);
            if (disposing) dialogFont.Dispose(); // Only after the controls that draw with it are gone.
        }
    }
}
