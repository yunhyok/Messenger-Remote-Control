using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using RemoteMonitorLink;

namespace RemoteMonitorMaster
{
    // ponytail: fixed, case-sensitive phrases for the supervised test; no natural-language or shell parser.
    internal static class ReadOnlyCommands
    {
        private sealed class OutputTooLargeException : IOException { }
        private static readonly string[] Commands = { "help", "help help", "help total status", "help pwrsi", "total status", "pwrsi" };
        private static readonly string[] Hashes = Commands.Select(TokenStore.Hash).ToArray();
        internal const int MaxReportParts = 9999999;

        internal static bool IsCommand(string command) { return Commands.Contains(command, StringComparer.Ordinal); }

        internal static bool TryMatchHash(string hash, out string command)
        {
            var index = Array.IndexOf(Hashes, hash);
            command = index < 0 ? null : Commands[index];
            return index >= 0;
        }

        internal static void ObserveName(ProbeNode node, string name)
        {
            node.PlainCommand = null;
            node.PlainCommandSourceHash = null;
            node.ReadyNoticeHash = node.ReadyNoticeSourceHash = null;
            node.CommandNameFormat = "UNAVAILABLE";
            if (name == null || node.Identity == null || name.Length != node.Identity.NameLength ||
                TokenStore.Hash(name) != node.Identity.NameHash) return;
            node.CommandNameFormat = "UNSUPPORTED";
            // Use the same bounded outer-space allowance for our Ready echo; raw identity stays strict afterward.
            if (name.Length <= 128)
            {
                node.ReadyNoticeHash = TokenStore.Hash(name.Trim(' ', '\u00a0'));
                node.ReadyNoticeSourceHash = node.Identity.NameHash;
            }
            if (name.Length > 64) { node.CommandNameFormat = "LONG_NAME"; return; }
            // ponytail: only outer space/NBSP is optional. Keep case, internal spacing, and raw identity strict.
            var trimmed = name.Trim(' ', '\u00a0');
            if (IsCommand(trimmed))
            {
                node.PlainCommand = trimmed;
                node.PlainCommandSourceHash = node.Identity.NameHash;
                node.CommandNameFormat = name == trimmed ? "EXACT" : "OUTER_SPACES";
            }
            else if (IsCommand(trimmed.ToLowerInvariant())) node.CommandNameFormat = "CASE_MISMATCH";
            else if (name != trimmed) node.CommandNameFormat = "UNSUPPORTED_EDGE_SPACES";
        }

        internal static bool TryMatchNode(ProbeNode node, out string command)
        {
            command = node == null ? null : node.PlainCommand;
            return IsCommand(command) && node.Identity != null && node.PlainCommandSourceHash == node.Identity.NameHash;
        }

        internal static string Capture(string command, string nonce, SlaveEndpoint endpoint, CancellationToken cancellation)
        {
            var replies = CaptureReplies(command, nonce, endpoint, cancellation);
            Need(replies.Length == 1);
            return replies[0];
        }

        internal static string[] CaptureReplies(string command, string nonce, SlaveEndpoint endpoint, CancellationToken cancellation)
        {
            PreparedPowerSiOutput ignored;
            return CaptureReplies(command, nonce, endpoint, cancellation, out ignored);
        }

        internal static string[] CaptureReplies(string command, string nonce, SlaveEndpoint endpoint, CancellationToken cancellation,
            out PreparedPowerSiOutput preparedOutput)
        {
            preparedOutput = null;
            Need(IsCommand(command) && Protocol.IsDiagnosticMarker("DRAFT", nonce) && endpoint != null);
            var clock = Stopwatch.StartNew();
            cancellation.ThrowIfCancellationRequested();
            if (command.StartsWith("help", StringComparison.Ordinal)) return new[] { Help(command, nonce) };
            MachineStatus state;
            try
            {
                state = StatusClient.QueryAsync(endpoint, cancellation, command == "pwrsi").GetAwaiter().GetResult();
                cancellation.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (!cancellation.IsCancellationRequested && IsExpectedQueryFailure(ex))
            {
                return FormatQueryFailure(command, nonce, ex);
            }
            if (command != "pwrsi") return new[] { FormatStatus(command, nonce, state) };
            void CheckPreparation()
            {
                cancellation.ThrowIfCancellationRequested();
                if (clock.Elapsed >= TimeSpan.FromSeconds(120)) throw new TimeoutException();
            }
            PreparedPowerSiOutput checkpoint;
            string[] replies;
            try
            {
                CheckPreparation();
                checkpoint = PowerSiOutputHistory.Shared.Prepare(endpoint.Pin, state.PowerSiReport, CheckPreparation);
                CheckPreparation();
                replies = FormatPowerSi(nonce, state, checkpoint, CheckPreparation);
                CheckPreparation();
            }
            catch (OutputTooLargeException ex) { return FormatQueryFailure(command, nonce, ex); }
            catch (TimeoutException ex) { return FormatQueryFailure(command, nonce, ex); }
            preparedOutput = checkpoint;
            return replies;
        }

        private static bool IsExpectedQueryFailure(Exception exception)
        {
            return exception is TimeoutException || exception is InvalidDataException || exception is IOException ||
                exception is SocketException || exception is AuthenticationException;
        }

        private static string Help(string command, string nonce)
        {
            string body;
            switch (command)
            {
                case "help": body = "허용 명령: help | help help | help total status | help pwrsi | total status | pwrsi"; break;
                case "help help": body = "help는 허용 명령을 표시합니다. help 다음에 명령을 쓰면 해당 설명을 표시합니다."; break;
                case "help total status": body = "total status는 Slave 시각, 가동 시간, RAM, 버전과 최대 8개 프로세스의 이름·PID·CPU·RAM·경과 시간을 표시합니다. CPU는 시뮬레이션 진행률이 아닙니다."; break;
                case "help pwrsi": body = "pwrsi는 모든 PowerSI를 한 번 수집합니다. 처음에는 수집된 Output 전체, 이후에는 마지막 전송 이후 추가분을 보냅니다. 변동이 없으면 알립니다. 조회·답장 준비는 최대 120초이며, 긴 답장은 여러 메시지로 나뉩니다. 다음 Master Ready까지 기다리세요."; break;
                default: throw new MonitorException("COMMAND_INVALID", "Unknown read-only command.");
            }
            return "HELP " + nonce + " | " + body + " | 명령은 표시된 소문자로 입력하세요. pwrsi에는 공백이 없고 total status의 단어 사이는 한 칸입니다. 바깥 공백은 허용하며 답장을 확인한 뒤 다음 명령을 보내세요.";
        }

        internal static string FormatStatus(string command, string nonce, MachineStatus state)
        {
            Need(command == "total status" && Protocol.IsDiagnosticMarker("DRAFT", nonce) && state != null);
            state.Validate();
            Need(state.Processes != null);
            state.Processes.Validate();
            var maximum = Math.Min(PcStatusReport.MaxPhoneProcesses, state.Processes.Items.Length);
            string text = null;
            for (var shown = maximum; shown >= 0; shown--)
            {
                text = BuildStatus(nonce, state, shown);
                if (text.Length <= PcStatusReport.MaxPhoneLength) break;
            }
            Need(IsReply(text, command, nonce));
            return text;
        }

        private static string BuildStatus(string nonce, MachineStatus state, int shown)
        {
            var total = state.Processes.Items.Length + state.Processes.Omitted;
            var text = new StringBuilder()
                .Append("TOTAL STATUS ").Append(nonce).Append("\r\n")
                .Append("Slave 시각: ").Append(state.LocalTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("가동: ").Append(state.UptimeMinutes.ToString(CultureInfo.InvariantCulture)).Append("분 | RAM ")
                .Append(state.AvailableMiB.ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(state.TotalMiB.ToString(CultureInfo.InvariantCulture)).Append(" MiB | protocol v").Append(state.Version).Append("\r\n")
                .Append("프로세스 ").Append(shown.ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(total.ToString(CultureInfo.InvariantCulture)).Append(" | 생략 ")
                .Append((total - shown).ToString(CultureInfo.InvariantCulture)).Append(" | 읽기 실패 ")
                .Append(state.Processes.Unreadable.ToString(CultureInfo.InvariantCulture));
            foreach (var process in state.Processes.Items.Take(shown))
            {
                var fullName = process.FullName ?? process.Name;
                Need(!string.IsNullOrEmpty(fullName));
                text.Append("\r\n").Append(fullName).Append(" (PID ")
                    .Append(process.Pid.ToString(CultureInfo.InvariantCulture)).Append(") | CPU ")
                    .Append(process.CpuPermille.HasValue ? (process.CpuPermille.Value / 10m).ToString("0.0", CultureInfo.InvariantCulture) : "?")
                    .Append("% | RAM ").Append(Number(process.WorkingSetMiB)).Append(" MiB | 경과 ")
                    .Append(Number(process.AgeSeconds.HasValue ? process.AgeSeconds.Value / 60 : (long?)null)).Append("분");
            }
            return text.Append("\r\n진행률: 제공 안 함 (PROGRESS N/A)").ToString();
        }

        private static string Number(long? value)
        { return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "?"; }

        private sealed class ReportBlock
        {
            internal readonly string FirstHeader, RepeatHeader;
            internal readonly IEnumerable<string> Lines;
            internal ReportBlock(string firstHeader, string repeatHeader, IEnumerable<string> lines)
            { FirstHeader = firstHeader; RepeatHeader = repeatHeader; Lines = lines; }
        }

        internal static string[] FormatPowerSi(string nonce, MachineStatus state, PreparedPowerSiOutput prepared = null,
            Action checkPreparation = null)
        {
            Need(Protocol.IsDiagnosticMarker("DRAFT", nonce) && state != null);
            state.Validate();
            var report = state.PowerSiReport;
            Need(report != null && report.Targets != null);
            var intro = new List<string>
            {
                "수집 UTC: " + Utc(report.CapturedUtc),
                string.Format(CultureInfo.InvariantCulture, "세션 {0} | 대상 {1} | 생략 {2} | 읽기 실패 {3}",
                    report.SessionId, report.Targets.Length, report.Omitted, report.Unreadable),
                "보고서: " + BatchExplanation(report.Code, report.Targets.Length) +
                    (report.Partial ? " 일부 결과만 포함되었습니다." : string.Empty) + " [code " + report.Code + "]"
            };
            var blocks = new List<ReportBlock> { new ReportBlock(string.Empty, string.Empty, intro) };
            foreach (var target in report.Targets)
            {
                Need(target != null && target.Pid > 0 && !string.IsNullOrEmpty(target.ProcessName));
                var full = target.ProcessName + " (PID " + target.Pid.ToString(CultureInfo.InvariantCulture) + ")";
                blocks.Add(new ReportBlock(target.State == "PENDING" ? full + " — Pending" :
                    full + " — " + StateName(target.State), string.Empty, new string[0]));
            }
            for (var index = 0; index < report.Targets.Length; index++)
            {
                var target = report.Targets[index];
                if (target.State == "PENDING") continue;
                var repeat = Abbreviate(target.ProcessName) + " (PID " + target.Pid.ToString(CultureInfo.InvariantCulture) + ")";

                var lines = new List<string>
                {
                    "상태: " + StateName(target.State) + " | 출처: " + SourceName(target.Source),
                    "대상 수집 UTC: " + (target.CapturedUtc.HasValue ? Utc(target.CapturedUtc.Value) : "확인 불가"),
                    "설명: " + StateExplanation(target.State, target.Source) + " [code " + target.Code + "]"
                };
                IEnumerable<string> details = lines;
                if (target.State == "READ" || target.State == "VISIBLE_EMPTY")
                {
                    details = details.Concat(OutputLines(prepared?.Primary[index], target.OutputText, target.Source == "OCR"));
                    if (!string.IsNullOrEmpty(target.OcrText))
                    {
                        details = details.Concat(new[] { "별도 로컬 OCR | 수집 UTC: " + Utc(target.OcrCapturedUtc.Value) })
                            .Concat(OutputLines(prepared?.Secondary[index], target.OcrText, true));
                    }
                }
                else lines.Add(RecoveryAdvice(target.Code));
                if (!string.IsNullOrEmpty(target.VisionCode) && target.VisionCode.StartsWith("VISION_", StringComparison.Ordinal))
                    lines.Add("로컬 LLM: " + RecoveryAdvice(target.VisionCode) + " [code " + target.VisionCode + "]");
                blocks.Add(new ReportBlock("증거: " + repeat, "증거 계속: " + repeat, details));
            }
            return PackReport(nonce, blocks, checkPreparation);
        }

        private static IEnumerable<string> OutputLines(PowerSiOutputDelta delta, string fullText, bool ocr)
        {
            if (ocr) yield return "LLM 전사본: 화면에 보인 내용이며 문자·숫자 정확도는 미검증입니다.";
            var kind = delta?.Kind ?? "FIRST";
            if (kind == "UNCHANGED")
            {
                yield return "추가된 Output이 없어 보고할 변동이 없습니다. 수집된 내용 기준이며 실제 계산 정지를 뜻하지 않습니다.";
                yield break;
            }
            var text = delta?.Text ?? fullText ?? string.Empty;
            yield return kind == "APPENDED" ? "이전 전송 이후 추가된 Output:" :
                kind == "REPLACED" ? "Output이 교체·초기화되었거나 기존 내용이 변경되어 현재 수집 내용 전체를 보냅니다:" :
                "수집된 Output 전체:";
            if (text.Length == 0) { yield return "(Output 내용 없음)"; yield break; }
            // Messenger text cannot carry control characters. Spell them out, preserving their exact code points.
            // Enumerate bounded lines instead of allocating millions of strings for a large newline-only buffer.
            var line = new StringBuilder(1024);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    yield return "  " + line; line.Clear();
                    continue;
                }
                if (line.Length >= 1000) { yield return "  " + line; line.Clear(); }
                if (char.IsControl(c) && c != '\t') line.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                else
                {
                    line.Append(c);
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length) line.Append(text[++i]);
                }
            }
            yield return "  " + line;
        }

        internal static string[] FormatQueryFailure(string command, string nonce, Exception exception)
        {
            Need((command == "total status" || command == "pwrsi") && Protocol.IsDiagnosticMarker("DRAFT", nonce));
            var reason = exception is OutputTooLargeException ? "전체 답장이 안전한 처리 한도(32 Mi 문자)를 넘었습니다. 내용을 잘라 보내거나 전송 이력을 갱신하지 않았습니다." :
                exception is LinkVersionMismatchException ? "Master/Slave 버전이 맞지 않습니다. 두 프로그램을 v" + AppInfo.Version + "으로 맞추세요." :
                exception is TimeoutException ? "Slave 응답 시간이 초과되었습니다. 자동 재시도하지 않습니다." :
                exception is InvalidDataException ? "Slave 응답 형식 또는 Master/Slave 버전이 맞지 않습니다." :
                exception is AuthenticationException ? "Slave 연결 인증을 확인하지 못했습니다." :
                exception is IOException || exception is SocketException ? "Slave 연결에서 응답을 받지 못했습니다." :
                "Slave 상태 조회에 실패했습니다.";
            if (command == "pwrsi")
                return PackReport(nonce, new[] { new ReportBlock(string.Empty, string.Empty,
                    new[] { "PowerSI 보고서를 받지 못했습니다.", reason, "메신저 대상이 그대로일 때 새 요청으로 다시 확인할 수 있습니다." }) });
            var text = "STATUS ERROR " + nonce + " | " + reason;
            Need(IsReply(text, command, nonce));
            return new[] { text };
        }

        private static string[] PackReport(string nonce, IEnumerable<ReportBlock> blocks, Action checkPreparation = null)
        {
            const int maximumParts = MaxReportParts;
            var prefixLength = ReportPrefix(nonce, maximumParts, maximumParts).Length + 2;
            var capacity = PcStatusReport.MaxPhoneLength - prefixLength;
            IEnumerable<string> Chunks()
            {
                foreach (var block in blocks)
                {
                    var current = block.FirstHeader;
                    foreach (var line in block.Lines.SelectMany(value => WrapLine(value, capacity - block.RepeatHeader.Length - 2)))
                    {
                        Need(line != null && line.Length + block.RepeatHeader.Length + 2 <= capacity);
                        var combined = current.Length == 0 ? line : current + "\r\n" + line;
                        if (combined.Length <= capacity) current = combined;
                        else
                        {
                            Need(current.Length > 0);
                            yield return current;
                            current = block.RepeatHeader.Length == 0 ? line : block.RepeatHeader + "\r\n" + line;
                        }
                    }
                    if (current.Length > 0) yield return current;
                }
            }
            var bodies = new List<string>();
            var body = string.Empty;
            long preparedCharacters = 0;
            foreach (var chunk in Chunks())
            {
                checkPreparation?.Invoke();
                Need(chunk.Length <= capacity);
                preparedCharacters += chunk.Length + prefixLength + 4;
                if (preparedCharacters > 32 * 1024 * 1024) throw new OutputTooLargeException();
                var combined = body.Length == 0 ? chunk : body + "\r\n\r\n" + chunk;
                if (combined.Length <= capacity) body = combined;
                else { bodies.Add(body); body = chunk; }
            }
            if (body.Length > 0) bodies.Add(body);
            Need(bodies.Count > 0 && bodies.Count <= maximumParts);
            var result = new string[bodies.Count];
            for (var i = 0; i < result.Length; i++)
            {
                checkPreparation?.Invoke();
                result[i] = ReportPrefix(nonce, i + 1, result.Length) + "\r\n" + bodies[i];
                Need(IsReportPart(result[i], nonce, i + 1, result.Length));
            }
            return result;
        }

        private static IEnumerable<string> WrapLine(string line, int capacity)
        {
            Need(line != null && capacity > 2);
            if (line.Length == 0) { yield return string.Empty; yield break; }
            for (var offset = 0; offset < line.Length;)
            {
                var count = Math.Min(capacity, line.Length - offset);
                if (offset + count < line.Length && char.IsHighSurrogate(line[offset + count - 1])) count--;
                yield return line.Substring(offset, count);
                offset += count;
            }
        }

        private static string ReportPrefix(string nonce, int index, int count)
        {
            return string.Format(CultureInfo.InvariantCulture, "PWRSI REPORT {0} | PART {1:000}/{2:000}", nonce, index, count);
        }

        internal static bool IsReportPart(string text, string nonce, int expectedIndex = 0, int expectedCount = 0)
        {
            if (!Protocol.IsDiagnosticMarker("DRAFT", nonce) || !SafeReplyText(text) || text.Length > PcStatusReport.MaxPhoneLength) return false;
            var match = Regex.Match(text, @"\APWRSI REPORT " + Regex.Escape(nonce) +
                @" \| PART (?<index>[0-9]{3,7})/(?<count>[0-9]{3,7})\r\n(?<body>[\s\S]+)\z", RegexOptions.CultureInvariant);
            int index, count;
            return match.Success && int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out index) &&
                int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out count) &&
                index >= 1 && count >= 1 && count <= MaxReportParts && index <= count &&
                text.StartsWith(ReportPrefix(nonce, index, count) + "\r\n", StringComparison.Ordinal) && (expectedIndex == 0 || index == expectedIndex) &&
                (expectedCount == 0 || count == expectedCount);
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
                case "BUFFER_TOO_LARGE": return "Output이 수집 한도(8 Mi 문자)를 넘었습니다. 일부만 잘라 보내지 않았습니다.";
                case "OUTPUT_INVALID_TEXT": return "수집한 문자의 형식을 안전하게 전달할 수 없습니다. Slave의 로컬 수집 결과를 확인하세요.";
                case "REPORT_TOO_LARGE": return "수집된 전체 자료가 보고서 처리 한도(32 Mi 문자)를 넘었습니다. 원문을 자르지 않았으며 전송 이력을 갱신하지 않습니다.";
                case "NOT_ATTEMPTED": return "전체 수집 시간 제한으로 미확인";
                case "BUSY": return "다른 수집이 진행 중입니다; 완료 후 다시 요청하세요";
                case "OUTPUT_REGION_UNCONFIRMED": case "AUTO_COPY_REGION_UNCONFIRMED": return "Output 영역을 확인하지 못해 자동 입력을 생략했습니다";
                default: return "수집 미확인; Slave의 대상 창과 로컬 설정을 확인하세요";
            }
        }

        private static string BatchExplanation(string code, int targets)
        {
            if (code == "OK") return targets == 0 ? "현재 Slave 세션에서 PowerSI 대상을 찾지 못했습니다." : "요청 시점의 대상별 증거입니다.";
            if (code == "BUSY") return "Slave가 다른 PowerSI 보고서를 수집 중이어서 이번 수집을 시작하지 못했습니다.";
            if (code == "CAPTURE_FAILED") return "Slave가 PowerSI 보고서를 수집하지 못했습니다.";
            if (code == "REPORT_TOO_LARGE") return "전체 자료가 보고서 처리 한도(32 Mi 문자)를 넘어 이름·상태와 크기 오류만 반환했습니다.";
            return "Slave가 보고서 오류를 반환했습니다.";
        }

        private static string StateName(string state)
        {
            switch (state)
            {
                case "READ": return "읽음";
                case "VISIBLE_EMPTY": return "Output 내용 없음";
                case "UNAVAILABLE": return "확인 불가";
                case "TIMEOUT": return "시간 제한";
                case "NOT_ATTEMPTED": return "시도하지 않음";
                default: throw new MonitorException("COMMAND_INVALID", "Unknown PowerSI target state.");
            }
        }

        private static string StateExplanation(string state, string source)
        {
            switch (state)
            {
                case "READ": return "Output 증거를 읽었습니다. 완료 여부나 진행률은 판정하지 않습니다.";
                case "VISIBLE_EMPTY": return source == "BUFFER" ? "직접 읽은 Output 버퍼가 비어 있었습니다." :
                    source == "AUTO_COPY" ? "자동 복사로 읽은 Output 내용이 비어 있었습니다." :
                    "화면에서 보이는 Output 영역이 비어 있었습니다. 전체 버퍼 상태를 추정하지 않습니다.";
                case "UNAVAILABLE": return "이번 요청에서 Output 증거를 확인하지 못했습니다.";
                case "TIMEOUT": return "제한 시간 안에 Output 증거를 받지 못했습니다.";
                case "NOT_ATTEMPTED": return "이번 요청에서는 이 대상 수집을 시도하지 않았습니다.";
                default: throw new MonitorException("COMMAND_INVALID", "Unknown PowerSI target state.");
            }
        }

        private static string SourceName(string source)
        {
            switch (source)
            {
                case "BUFFER": return "버퍼";
                case "AUTO_COPY": return "자동 복사";
                case "OCR": return "로컬 OCR";
                case "NONE": return "없음";
                default: throw new MonitorException("COMMAND_INVALID", "Unknown PowerSI source.");
            }
        }

        private static string Utc(DateTime value)
        {
            Need(value.Kind == DateTimeKind.Utc);
            return value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        }

        private static string Abbreviate(string value)
        {
            var starts = StringInfo.ParseCombiningCharacters(value);
            if (starts.Length <= 32) return value;
            var headEnd = starts[16];
            var tailStart = starts[starts.Length - 8];
            return value.Substring(0, headEnd) + "…" + value.Substring(tailStart);
        }

        private static bool SafeReplyText(string text)
        {
            if (text == null) return false;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[++i])) return false;
                    continue;
                }
                if (char.IsLowSurrogate(text[i])) return false;
                if (!char.IsControl(text[i])) continue;
                if (text[i] == '\t') continue;
                if (text[i] == '\r' && i + 1 < text.Length && text[++i] == '\n') continue;
                if (text[i] == '\n') continue;
                return false;
            }
            return true;
        }

        // Exact prepared-payload comparison in Consent is authoritative; this checks final rendering and reply kind.
        internal static bool IsReply(string text, string command, string nonce)
        {
            if (!IsCommand(command) || !Protocol.IsDiagnosticMarker("DRAFT", nonce) || text == null ||
                text.Length > PcStatusReport.MaxPhoneLength || !SafeReplyText(text)) return false;
            if (command.StartsWith("help", StringComparison.Ordinal)) return text == Help(command, nonce);
            if (command == "pwrsi") return IsReportPart(text, nonce);
            if (text.StartsWith("STATUS ERROR " + nonce + " | ", StringComparison.Ordinal)) return text.IndexOf('\r') < 0 && text.IndexOf('\n') < 0;
            return text.StartsWith("TOTAL STATUS " + nonce + "\r\n", StringComparison.Ordinal) &&
                text.EndsWith("\r\n진행률: 제공 안 함 (PROGRESS N/A)", StringComparison.Ordinal);
        }

        internal static bool IsReplyPart(string text, string command, string nonce, int index, int count)
        {
            return command == "pwrsi" ? IsReportPart(text, nonce, index, count) :
                index == 1 && count == 1 && IsReply(text, command, nonce);
        }

        internal static void RunSelfTest(string directory)
        {
            const string nonce = "D234567";
            int Occurrences(string text, string value)
            {
                var count = 0;
                for (var offset = 0; (offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0; offset += value.Length)
                    count++;
                return count;
            }
            foreach (var command in Commands)
            {
                string matched;
                Need(TryMatchHash(TokenStore.Hash(command), out matched) && matched == command);
                Need(!TryMatchHash(TokenStore.Hash(command.ToUpperInvariant()), out matched));
            }
            foreach (var invalid in new[] { null, "Help", "help ", " total status", "total  status", "total\tstatus", "pwrsi.exe", "help unknown", "run calc", "help\n" })
                Need(!IsCommand(invalid));
            var totalName = "PowerSI MixedCase 123 한글";
            var state = new MachineStatus { Version = LinkVersion.Value, LocalTime = new DateTime(2026, 9, 10, 12, 0, 0),
                UptimeMinutes = 1, AvailableMiB = 1024, TotalMiB = 2048,
                Processes = new ProcessInventory { Items = new[] { new ProcessState {
                    Name = ProcessInventory.NormalizeName(totalName), FullName = totalName, Pid = 1 },
                    new ProcessState { Name = "m345678", Pid = 2 } } } };
            var total = FormatStatus("total status", nonce, state);
            Need(IsReply(total, "total status", nonce) && total.Contains(totalName + " (PID 1)") &&
                !total.Contains(totalName.ToUpperInvariant()) && !total.Contains("M345678") && total.Contains("CPU ?%"));
            state.Processes.Items = Enumerable.Range(1, 8).Select(i =>
            {
                var full = "PowerSI MixedCase " + i.ToString(CultureInfo.InvariantCulture) + " " + new string('界', 220);
                return new ProcessState { Name = ProcessInventory.NormalizeName(full), FullName = full, Pid = i };
            }).ToArray();
            var boundedTotal = FormatStatus("total status", nonce, state);
            var shown = Regex.Matches(boundedTotal, @"\(PID [1-9][0-9]*\)").Count;
            Need(boundedTotal.Length <= PcStatusReport.MaxPhoneLength && shown > 0 && shown < 8 &&
                boundedTotal.Contains(state.Processes.Items[0].FullName + " (PID 1)") &&
                boundedTotal.Contains("프로세스 " + shown.ToString(CultureInfo.InvariantCulture) + "/8 | 생략 " +
                    (8 - shown).ToString(CultureInfo.InvariantCulture)));
            var captured = new DateTime(2026, 9, 16, 1, 2, 3, DateTimeKind.Utc);
            var longName = "PowerSI MixedCase 123 한글 e\u0301 😀 " + new string('界', 205);
            var excerpt = string.Join("\n", Enumerable.Range(1, 12).Select(i =>
                "line" + i.ToString(CultureInfo.InvariantCulture) + " " + new string((char)('a' + i), 108)));
            state.Processes.Items = new ProcessState[0];
            state.PowerSiReport = new PowerSiReport
            {
                CapturedUtc = captured, SessionId = 7, Omitted = 2, Unreadable = 1, Partial = true,
                Targets = new[]
                {
                    new PowerSiTargetReport
                    {
                        Pid = 31, StartUtcTicks = captured.AddHours(-2).Ticks, ProcessName = longName,
                        CapturedUtc = captured, State = "READ", Source = "AUTO_COPY", Code = "AUTO_COPY_READ",
                        OutputText = excerpt
                    },
                    new PowerSiTargetReport
                    {
                        Pid = 32, StartUtcTicks = captured.AddHours(-1).Ticks, ProcessName = "PowerSI Pending MixedCase",
                        State = "PENDING", Source = "NONE", Code = "SC_PENDING"
                    },
                    new PowerSiTargetReport
                    {
                        Pid = 33, StartUtcTicks = captured.AddMinutes(-30).Ticks, ProcessName = "PowerSI Visible Empty",
                        CapturedUtc = captured, State = "VISIBLE_EMPTY", Source = "OCR", Code = "OUTPUT_VISIBLE_EMPTY"
                    }
                }
            };
            var parts = FormatPowerSi(nonce, state);
            var joined = string.Join("\n", parts);
            var abbreviated = Abbreviate(longName);
            Need(parts.Length > 1 && parts.Select((part, index) => part.Length <= PcStatusReport.MaxPhoneLength &&
                IsReportPart(part, nonce, index + 1, parts.Length)).All(valid => valid) && joined.Contains(longName + " (PID 31)") &&
                StringInfo.ParseCombiningCharacters(abbreviated).Length == 25 &&
                joined.Contains("PowerSI Pending MixedCase (PID 32) — Pending") && !joined.Contains("SC_PENDING") &&
                joined.Contains("line1 ") && joined.Contains("line12 ") && joined.Contains("로컬 OCR") && !joined.Contains("CPU") && !joined.Contains("RAM"));
            var firstExcerpt = joined.IndexOf("수집된 Output 전체", StringComparison.Ordinal);
            Need(firstExcerpt > 0 && Occurrences(joined, longName) == 1);
            foreach (var target in state.PowerSiReport.Targets)
            {
                var identityAndState = target.ProcessName + " (PID " + target.Pid.ToString(CultureInfo.InvariantCulture) + ") — " +
                    (target.State == "PENDING" ? "Pending" : StateName(target.State));
                var summaryIndex = joined.IndexOf(identityAndState, StringComparison.Ordinal);
                Need(summaryIndex >= 0 && summaryIndex < firstExcerpt);
            }
            var repeatedReport = string.Join("\n", FormatPowerSi(nonce, state));
            Need(Occurrences(repeatedReport, longName) == 1 && repeatedReport.Contains(longName + " (PID 31)"));
            var history = new PowerSiOutputHistory(Path.Combine(directory, "command-output-history.txt"));
            var pin = new string('0', 64);
            var initial = history.Prepare(pin, state.PowerSiReport);
            Need(string.Join("\n", FormatPowerSi(nonce, state, initial)).Contains("line1 ") && initial.Commit());
            var unchanged = string.Join("\n", FormatPowerSi(nonce, state, history.Prepare(pin, state.PowerSiReport)));
            Need(unchanged.Contains("추가된 Output이 없어") && !unchanged.Contains("line1 "));
            state.PowerSiReport.Targets[0].OutputText += "\n새 결과 123😀";
            var staged = history.Prepare(pin, state.PowerSiReport);
            var addedReplies = FormatPowerSi(nonce, state, staged);
            var added = string.Join("\n", addedReplies);
            Need(added.Contains("새 결과 123😀") && !added.Contains("line1 ") && added.Contains("추가된 Output"));
            var testEndpoint = new SlaveEndpoint(System.Net.IPAddress.Loopback, 1, pin, Convert.ToBase64String(new byte[32]));
            var interrupted = new SupervisedSendTest.Consent(nonce, true, true, testEndpoint, null, true);
            Need(interrupted.TryClaimRoundTrip()); interrupted.BindCommand("pwrsi");
            interrupted.BindPreparedReplies(addedReplies, staged); interrupted.Cancel();
            Need(history.Prepare(pin, state.PowerSiReport).Primary[0].Kind == PowerSiOutputDelta.Appended);
            var delivered = new SupervisedSendTest.Consent(nonce, true, true, testEndpoint, null, true);
            Need(delivered.TryClaimRoundTrip()); delivered.BindCommand("pwrsi");
            delivered.BindPreparedReplies(addedReplies, history.Prepare(pin, state.PowerSiReport));
            for (var i = 0; i < addedReplies.Length; i++)
            {
                var part = delivered.GetPreparedPart(i);
                Need(part.TryConsume(addedReplies[i]) && part.TryCommitMove() && part.TryCommitWrite() && part.TryCommit());
                delivered.RecordPreparedPartOutcome(i, new SupervisedSendTest.Outcome("SENT", "NONE", "self-test", true));
            }
            Need(delivered.CommitPreparedOutput() && history.Prepare(pin, state.PowerSiReport).Primary[0].Kind == PowerSiOutputDelta.Unchanged);
            File.Delete(Path.Combine(directory, "command-output-history.txt"));
            var continuation = string.Join("\n", PackReport(nonce, new[] { new ReportBlock("대상: " + longName + " (PID 31)",
                "대상 계속: " + abbreviated + " (PID 31)", Enumerable.Repeat(new string('x', 500), 4)) }));
            Need(continuation.Contains("대상 계속: " + abbreviated + " (PID 31)") &&
                continuation.Contains(longName + " (PID 31)"));
            var unbroken = string.Concat(Enumerable.Repeat("한글123😀e\u0301", 500));
            Need(string.Concat(WrapLine(unbroken, 1137)) == unbroken && WrapLine(unbroken, 1137).All(SafeReplyText));
            var large = PackReport(nonce, new[] { new ReportBlock(string.Empty, string.Empty,
                new[] { new string('x', 1400000) }) });
            Need(large.Length > 999 && large.Sum(p => p.Count(c => c == 'x')) == 1400000 &&
                large.Select((p, i) => IsReportPart(p, nonce, i + 1, large.Length)).All(x => x));
            try
            {
                PackReport(nonce, new[] { new ReportBlock("", "", Enumerable.Repeat(new string('y', 1300), 30000)) });
                throw new InvalidOperationException("Oversized prepared output accepted.");
            }
            catch (OutputTooLargeException) { }
            var preparationChecks = 0;
            try
            {
                PackReport(nonce, new[] { new ReportBlock("", "", Enumerable.Repeat(new string('y', 1300), 100)) },
                    () => { if (++preparationChecks == 3) throw new OperationCanceledException(); });
                throw new InvalidOperationException("Canceled preparation continued.");
            }
            catch (OperationCanceledException) { Need(preparationChecks == 3); }
            var literal = OutputLines(null, "first\n\nlast\u0001\n", false).ToList();
            Need(literal.Contains("  first") && literal.Contains("  last\\u0001") && literal.Count(s => s == "  ") == 2);
            Need(StateExplanation("VISIBLE_EMPTY", "BUFFER").Contains("버퍼") &&
                StateExplanation("VISIBLE_EMPTY", "AUTO_COPY").Contains("자동 복사") &&
                StateExplanation("VISIBLE_EMPTY", "OCR").Contains("화면"));

            state.PowerSiReport = new PowerSiReport
            {
                CapturedUtc = captured, SessionId = 7,
                Targets = new[] { new PowerSiTargetReport { Pid = 32, StartUtcTicks = captured.Ticks,
                    ProcessName = "PowerSI Pending MixedCase", State = "PENDING", Source = "NONE", Code = "SC_PENDING" } }
            };
            var pendingOnly = string.Join("\n", FormatPowerSi(nonce, state));
            Need(pendingOnly.Contains("PowerSI Pending MixedCase (PID 32) — Pending") &&
                !pendingOnly.Contains("출처:") && !pendingOnly.Contains("설명:") && !pendingOnly.Contains("Output 전체") &&
                !pendingOnly.Contains("SC_PENDING") && !pendingOnly.Contains("CPU") && !pendingOnly.Contains("RAM"));

            state.PowerSiReport = new PowerSiReport { CapturedUtc = captured, SessionId = 7, Partial = true, Code = "BUSY" };
            var busy = string.Join("\n", FormatPowerSi(nonce, state));
            Need(busy.Contains("다른 PowerSI 보고서를 수집 중") && !busy.Contains("대상을 찾지 못"));
            state.PowerSiReport = new PowerSiReport { CapturedUtc = captured, SessionId = 7 };
            Need(string.Join("\n", FormatPowerSi(nonce, state)).Contains("PowerSI 대상을 찾지 못"));

            var mismatch = FormatQueryFailure("pwrsi", nonce, new LinkVersionMismatchException("0.1.58"));
            Need(mismatch.Length == 1 && IsReportPart(mismatch[0], nonce, 1, 1) && mismatch[0].Contains("v" + AppInfo.Version) &&
                !mismatch[0].Contains("0.1.58"));
            var timeout = FormatQueryFailure("pwrsi", nonce, new TimeoutException());
            Need(timeout.Length == 1 && timeout[0].Contains("자동 재시도하지 않습니다"));
            Need(IsExpectedQueryFailure(new InvalidDataException()) && IsExpectedQueryFailure(new SocketException()) &&
                !IsExpectedQueryFailure(new MonitorException("TEST", "formatter")) &&
                !IsExpectedQueryFailure(new InvalidOperationException()));

            // Help never connects to the endpoint. It still needs an exact, locally approved one-use send consent.
            var endpoint = new SlaveEndpoint(System.Net.IPAddress.Loopback, 1, new string('0', 64), Convert.ToBase64String(new byte[32]));
            foreach (var command in Commands.Where(c => c.StartsWith("help", StringComparison.Ordinal)))
            {
                var consent = new SupervisedSendTest.Consent(nonce, true, true, endpoint, null, true);
                Need(consent.TryClaimRoundTrip()); consent.BindCommand(command);
                var reply = consent.PrepareReply();
                Need(IsReply(reply, command, nonce) && consent.IsAuthorizedReply(reply) &&
                    !consent.IsAuthorizedReply(reply + " ") && consent.TryConsume(reply) && !consent.TryConsume(reply));
                consent.Cancel(); Need(!consent.TryCommitMove());
            }
            var cancelled = new SupervisedSendTest.Consent(nonce, true, true, endpoint, null, true);
            Need(cancelled.TryClaimRoundTrip()); cancelled.BindCommand("help"); cancelled.Cancel();
            try { cancelled.PrepareReply(); throw new InvalidOperationException("Cancelled command prepared a reply."); }
            catch (MonitorException) { }
        }

        private static void Need(bool condition)
        { if (!condition) throw new MonitorException("COMMAND_INVALID", "Read-only command data rejected."); }
    }
}
