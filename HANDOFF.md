# Messenger Remote Control — 점검 인계

갱신: 2026-09-23. Claude를 포함한 다음 검토자가 현재 구현과 검증 범위를 파악하기 위한 진입 문서입니다. 과거 계획보다 이 문서와 실제 소스, 사용자의 최신 지시를 우선합니다.

## 1. 현재 상태와 읽는 순서

| 항목 | 기준 |
|---|---|
| 저장소 | `yunhyok/Messenger-Remote-Control`, Public, 기본 브랜치 `main` |
| 앱·설치파일 | **v0.3.9**, Master와 Slave 모두 동일 버전. 휴대폰 명령 `watchdog on`/`watchdog off`(PID 지정 가능)로 PowerSI 완료를 30분 또는 60분마다 확인해 알리는 Master 기능 추가분이며 현장 미확인. Slave는 버전 표기만 올랐습니다 |
| 정식 배포 소스·태그 | 현재 정식 배포는 `v0.3.8` → `620b38da4969b7c670681f352b39af284cc1d3a1` (PR #9 병합 커밋). 설치파일과 해시는 §9. 0.3.9 설치파일은 태그 `v0.3.9`의 CI 배포 실행이 발행하며, 0.3.8과 같은 절차로 공개 자산 해시를 확인한 뒤 §9에 기록합니다 |
| 이전 정식 배포 | v0.3.7 → `7e4d52a0b84caeeaed2842ef58bc2b4909f524f8`, v0.3.6 → `e5c5389798495af735077f6a644a0eb4a6ccb6cb`, v0.3.5 → `d27c2b8f57bd9b6078e4d8a085bd2bbe295d9f9a`, v0.3.4 → `932be45f293910ba4244c3c00a58e44343bdd704`, v0.3.3 → `e31a67c68ca33986016be00cf4990d8d81f9d8ec` |
| 통신 규약 | **0.3.0** — 앱 버전과 별개, 0.3.9에서도 변경 없음 |
| 호환성 | Master 0.3.9 + Slave 0.3.0~0.3.8. 0.2.x에서는 두 역할 모두 갱신 |
| 실행 환경 | Windows 7 SP1 Master / Windows 11 Slave, .NET Framework 4.8 |
| 이번 인계 범위 | v0.3.3 독립 소스 검토 결과의 반영과 v0.3.4 정식 배포, 첫 Win7 현장 로그(G1)에 대한 0.3.5 수정, v0.3.5 현장 로그의 속도 측정(G2)에 대한 0.3.6 변경, v0.3.6 현장 로그의 시간 분해(G3)에 대한 0.3.7 변경, v0.3.6 야간 로그의 증거 유효기간 초과(G4)에 대한 0.3.8 변경, 0.3.9의 watchdog 명령(PowerSI 완료 감시·알림) 추가. 검토 기록은 [docs/REVIEW-2026-09-21-v0.3.3.md](docs/REVIEW-2026-09-21-v0.3.3.md) |

먼저 [AGENTS.md](AGENTS.md)의 프로젝트 제약을 읽고 이 문서의 소스 지도와 점검 항목을 따라갑니다. 사용법은 [README.md](README.md), 설치는 [INSTALL.md](INSTALL.md), 최소 현장 확인은 [WIN7-TEST.md](WIN7-TEST.md)와 [SLAVE-TEST.md](SLAVE-TEST.md)에 있습니다.

[과거 인계 기록](docs/history/HANDOFF-through-v0.3.3.md)은 별도 보존했습니다. 그 안의 5줄/600자 발췌, pre-release, 창 복원 금지 등은 당시 정책이며 현재 요구가 아닙니다. 기존 `Remote-Control-App` 저장소와 HFSS 작업은 별개입니다. 이 저장소의 출발점은 `Remote-Control-App@1365e2c249d00b2a73634c77cd7bc82247f82405`(PowerSI v0.1.58)입니다.

## 2. 실제 운영 흐름

1. Master 운용 Start 후 5초 안에 KI-Messenger의 나와의 대화창을 한 번 선택합니다. 화면에 남은 초가 표시되고, 선택 직후 선택한 창의 프로세스 이름·PID가 참고로 표시됩니다(판정은 작업자 스레드의 `PROBE_NOT_KI_MESSENGER`가 담당). 해당 세션의 HWND, 프로세스 시작 시각, UIA root, 창 위치·크기를 고정합니다.
2. 대화창을 확인한 뒤 `Master Ready. help, total status, pwrsi, watchdog 중 하나를 보내세요. [회차 번호]`를 보냅니다. 선두 토큰 `Master Ready`는 유지됩니다. Ready 이후 **새 메시지의 전체 본문**이 허용 명령과 일치해야 접수합니다. 첫 명령 행이 화면 밖이거나 비활성이면 그 행에 고정된 채 접수하지 않으며, 화면에는 `COMMAND_NOT_VISIBLE` 안내(스크롤로 보이게 하라는 문구)가 표시됩니다. 다른 행이 대신 접수되지는 않습니다. 0.3.6부터 명령 대기 중에는 250 ms마다 대화 기록 끝부분의 구조(요소 식별자)만 표본으로 확인해 변화가 보이면 곧바로 전체 스냅샷을 시작하고, 변화가 없으면 최대 3초 주기로 시작합니다(`RECEIVE_CHANGE_TRIGGER`). 이 표본은 본문을 읽지 않으며 접수 규칙(서로 다른 전체 스냅샷 두 번에서 같은 전체 본문)은 그대로입니다.
3. `pwrsi`/`total status`는 처리 안내(`Processing pwrsi. …`)를 한 번 보낸 뒤 Slave에 요청합니다. 처리 중 다른 메시지는 큐에 넣지 않고 무시합니다. `help`는 세 줄(머리글·본문·사용법)로 회신하며 느린 조회 안내가 없습니다. `help watchdog`과 `watchdog on`/`off`도 처리 안내 없이 한 부분으로 회신합니다(`watchdog on`은 `total status`와 같은 프로세스 목록 조회만 합니다).
4. Slave는 PowerSI별 수집 자료·출처·시각·상태를 반환합니다. Master가 전체/추가 Output을 판별하고 최대 1,400자씩 고정된 답장을 준비합니다.
5. 각 부분 전송 전에 같은 대화와 접수 명령을 다시 확인합니다. 모든 부분의 보호된 UI 전송이 완료되어야 Output 이력을 저장하고 다음 Ready로 돌아갑니다.

허용 명령: `help`, `help help`, `help total status`, `help pwrsi`, `help watchdog`, `total status`, `pwrsi`, `watchdog on`, `watchdog on <PID>`, `watchdog off`, `watchdog off <PID>`(소문자, 단어 사이 ASCII 공백 한 칸, PID는 앞자리 0 없는 1~10자리 숫자로 2,147,483,647 이하). `watchdog`은 PowerSI의 완료를 주기적으로 확인해 알리는 기능뿐이며, 임의 명령 실행과 PowerSI 실행·종료는 제공하지 않습니다.

**watchdog 흐름(0.3.9, 명령어 운용 세션 전용):** ① `watchdog on`은 `total status`와 같은 STATUS 목록 조회(Output 수집·Slave 쪽 활성화 없음)로 시작 시각을 읽을 수 있는 PowerSI 전체를 그 시점에 대상으로 고정해 기존 목록에 합치고, `watchdog on <PID>`는 목록에 있는 PowerSI 하나만 추가합니다. `watchdog off [PID]`는 Slave 조회 없이 해제합니다. 답장은 머리글 `WATCHDOG <번호>`의 한 부분이며, 등록·해제는 답장 전송이 아니라 답장 준비 시점에 적용됩니다. ② 확인 주기는 Master 시작 화면의 "watchdog 확인 주기"(30분/60분, 기본 30분)이며 세션 시작 때 한 번 읽습니다. 주기가 되면 명령 대기가 idle 상태(Ready 뒤 폴링 1회 이상, 재관찰을 기다리는 후보 없음, 화면 밖에 고정된 명령 행 없음, Ready 행 결합)일 때만 대기를 양보합니다(`WAIT_YIELDED`). ③ Master는 `pwrsi`와 같은 PowerSI 수집을 한 번 실행해 대상별로 완료를 판정하며, Output 이력은 읽지도 갱신하지도 않습니다. ④ 완료·해제·경고가 있으면 새 코드의 `WATCHDOG <코드> | 완료 알림` 또는 `| 경고`를 Ready와 같은 알림 경로로 보내고, 대기 중인 알림을 모두 보낸 뒤 새 코드의 `Master Ready`를 보냅니다. 보낼 것이 없으면 같은 Ready로 대기에 다시 들어가므로 주기마다 Ready가 쌓이지 않습니다. 확인 중 도착한 명령은 확인이 끝난 뒤 알림보다 먼저 처리합니다. ⑤ 감시는 세션과 함께 끝나며(Stop·잠금·절전·세션 중단) Master를 다시 시작해도 이어지지 않습니다.

일반 창 뒤에서도 수신 검사를 계속합니다. 최소화된 선택 창은 읽기 전에 복원합니다. Ready·처리 안내·각 답장 입력 전에 PC 입력이 1초 이상 멈추고 마우스 버튼·Shift/Ctrl/Alt/Win 키가 눌려 있지 않으며 앞 창에 메뉴·끌기·캡처가 없는지 확인한 다음 선택 창만 앞으로 가져옵니다. 0.3.5부터 이 대기가 2초를 넘으면 막고 있는 조건(`INPUT_RECENT`/`KEY_HELD`/`FOREGROUND_BUSY`/`NO_FOREGROUND`)을 화면과 로그(`OPERATIONAL_TARGET_IDLE_WAIT`)에 표시하고, 60초를 넘으면 `TARGET_PC_NOT_IDLE`로 중단합니다. 마우스를 계속 올려둘 필요는 없습니다. 0.3.6부터는 Master 자신의 보호된 클릭이 만든 마지막 입력 시각(tick)과 정확히 같은 값만 자기 입력으로 보아 1초 규칙에서 제외합니다(`own_input`). 다른 모든 tick은 기존 1초 규칙 그대로이고 키 검사·앞 창 검사·60초 한도도 변하지 않습니다. 다른 HWND/프로세스를 찾아 대체하지 않습니다.

Windows 활성화 거부, 대상 변경, 실제 입력 중 간섭 또는 불확실한 전송은 사유를 남기고 중단합니다. 0.3.8부터 실제 입력이 한 번도 시도되지 않은 채 중단된 요청은 세션을 끝내지 않고 새 M코드를 할당해 새 `Master Ready`를 보낸 뒤 다시 수신합니다(로그 `STATUS_REQUEST_ABORTED ... resumed=true`, 운용 창 `REQUEST_RESUMED`). 연속 3회째 중단은 `STATUS_REQUEST_ABORT_LIMIT`로 세션을 끝내고, 입력이 시작된 뒤의 중단은 종전대로 `STATUS_REQUEST_STOPPED`로 끝냅니다. 재개는 재전송이 아니며 중단된 답장은 어느 경우에도 다시 보내지 않습니다. 0.3.4부터 운용 창은 중단 사유 코드 옆에 한국어 설명과 다음 행동을 함께 표시하고, 새 세션은 창을 닫고 허브 버튼을 다시 누르면 시작한다는 안내를 붙입니다. 잠금·사용자 세션 전환·절전 후 자동 재개하지 않습니다. 상시 idle 종료 제한은 없지만, 모든 환경 변화에서 세션이 유지된다는 보장은 아닙니다.

## 3. 소스 지도

모든 경로는 저장소 루트 기준입니다. 별도의 solution/test 프로젝트 없이 두 `.csproj`와 실제 EXE의 `--self-test`를 사용합니다. `RemoteMonitorLink/*.cs`는 양쪽 프로젝트에 공유 소스로 포함됩니다.

| 검토 대상 | 진입점·주요 파일 |
|---|---|
| Master 시작·화면 | `src/RemoteMonitorMaster/Program.cs` → `MasterHubForm.cs` (0.3.9: watchdog 확인 주기 선택) → `ReceiveForm.cs` (`Explain`: 사유 코드 → 한국어 안내, 0.3.9: watchdog 요약 줄과 세션 종료 사유를 꺼내는 `SessionStopReason`) |
| 운영 세션·Ready·다음 요청 | `src/RemoteMonitorMaster/StatusSession.cs` (0.3.9: 양보된 대기의 해제 `TryReleaseYieldedRequest`, `Run` 안의 주기 확인 `RunWatchdogCheck`와 알림 큐 `SendQueuedNotices`, `DecideAfterNotice`, `RequireNoPendingCommand`, `WatchdogNoticeRetryAfter`) |
| 처음 선택한 창 기억·복원·활성화 | `src/RemoteMonitorMaster/OperationalTarget.cs` (0.3.6: `IsInputRecent(now, lastInput, ownInputTick)`) |
| 대상 프로세스 신원·서명 확인 | `src/RemoteMonitorMaster/AutomationTarget.cs`의 `ProcessIdentity.Capture` (0.3.6: 서명 검증 캐시와 `RunSelfTest`) |
| 명령 접수·Ready 경계·재확인 | `src/RemoteMonitorMaster/ReceiveProbe.cs` (0.3.6: 대기 중 표본 `TailPath`/`SameTailChain`/`TriggerReason`, 0.3.9: idle 대기의 양보 규칙 `MayYield`와 `WAIT_YIELDED` 결과), `ReceiveMetadata.cs` |
| 최신 UIA 읽기·캐시 | `src/RemoteMonitorMaster/ReadOnlyProbe.cs`, `ProbeElementCache.cs` (0.3.6: `TryCapture`가 내용 앞 가드, 0.3.7: `CreateRequest`가 만든 요청 아래의 부모당 자식 질의 1회와 `TryFromCached`) |
| 요청부터 분할 발송·최종 이력 저장 | `src/RemoteMonitorMaster/RoundTripTest.cs`, `SupervisedSendTest.cs` |
| 실제 입력·클릭·대상 확인 | `src/RemoteMonitorMaster/SupervisedSendTest.cs`, `MouseClickInput.cs` (0.3.6: `TryGetLastInputTick`, `LastOwnInputTick`), `UiaPointProbe.cs` |
| 명령 처리·보고서 구성 | `src/RemoteMonitorMaster/ReadOnlyCommands.cs` (`FormatPowerSi`, 0.3.9: `PrepareWatchdogReply`와 `WATCHDOG_COMMAND` 기록) |
| watchdog 상태·문구·문법·설정 (0.3.9) | `src/RemoteMonitorMaster/Watchdog.cs` (`WatchdogState` 감시 목록·주기·판정 적용, `WatchdogText` 답장·알림 문구와 분할, `WatchdogCommand` 명령 문법, `WatchdogSettings` 주기 파일), `PowerSiCompletion.cs` (Output의 완료 표시 판정). 알림 동의는 `SupervisedSendTest.Consent.ForWatchdogNotice`, 세션 목록 연결은 `Consent.AttachWatchdog` |
| 전체/추가/변경 Output과 이력 | `src/RemoteMonitorMaster/PowerSiOutputHistory.cs` |
| Slave 시작·공용 수집 | `src/RemoteMonitorSlave/Program.cs`, `SlaveForm.cs` (`CollectRemoteOutput` → `ReadOutputBuffer`, `ResponsiveStep`) |
| 직접 버퍼·자동 복사 | `src/RemoteMonitorSlave/OutputBufferCapture.cs`, `OutputAutoCopy.cs` (논리→물리 좌표 변환) |
| 목록·대상 식별·응답 검사·화면 | `src/RemoteMonitorLink/ProcessInventory.cs`, `PowerSiObservation.cs`, `PowerSiScreenCapture.cs` |
| 로컬 이미지 판독 | `src/RemoteMonitorLink/PowerSiVision.cs`, `LocalVisionClient.cs` |
| 인증 통신·버전·복수 보고서 | `src/RemoteMonitorLink/StatusTransport.cs`, `LinkTypes.cs`, `PowerSiReport.cs` |
| 자체 검사 집계 | `src/RemoteMonitorMaster/Core.cs`, `src/RemoteMonitorLink/LinkSelfTest.cs`; 관련 클래스의 `RunSelfTest`/`SelfTest` |
| 빌드·설치·배포 | `scripts/`, `installer/MessengerRemoteControl.iss`, `.github/workflows/ci.yml` |
| 검토 기록 | `docs/REVIEW-2026-09-21-v0.3.3.md` (지적 ID A/B/C/D/E/F, 심각도, 위치, 처리) |

진단용 화면과 과거 실험 경로도 남아 있습니다. `MainForm.cs`와 `AutomationTarget.Bind/SendPong`은 자체 검사 전용 레거시 경로이며 `Program`/`MasterHubForm`에서 도달하지 않습니다(0.3.4에서 주석으로 표시). 운용 경로를 확인할 때는 `StatusSession`의 plain-command 경로부터 추적하고, 비슷한 이름의 진단 함수만 수정하지 않도록 합니다.

## 4. 유지해야 할 동작과 데이터 경계

- **Pending:** Windows 응답 검사에서 Pending이면 `전체 Process Name (PID): Pending`만 회신합니다. 각 수집 단계 전후 확인에서 Pending으로 바뀌면 이미 확보한 Output도 제외합니다. 추가 활성화·캡처·복사·LLM 호출·재시도를 하지 않습니다. PowerSI 내부 계산 대기와 Windows 비응답은 구분합니다.
- **대상 식별:** PID만 사용하지 않습니다. 시작 시각·세션을 함께 고정하고, Output 이력은 Slave 인증서·세션·PID·시작 시각·출처 계열로 분리합니다.
- **전체와 추가분:** 처음에는 수집된 전체 본문, 다음에는 기존 본문의 정확한 접두부 뒤에 추가된 부분만 보냅니다. 같으면 추가 Output 없음, 접두부/출처가 바뀌거나 비워지면 변경 안내와 현재 전체 본문을 보냅니다. OCR은 수집된 뷰포트 범위이며 보이지 않는 전체 로그를 확보했다고 주장하지 않습니다.
- **이름·분할:** 한 보고서의 첫 등장은 전체 이름+PID입니다. 32자를 넘는 이름의 재등장은 앞 16자·뒤 8자를 사용하며 유니코드를 보존합니다. 분할 전체가 한 보고서이고 다음 조회는 다시 전체 이름입니다. 필수 상태를 Output보다 먼저 표시합니다(0.3.4부터 지연 열거가 아니라 구성 순서로 보장).
- **범위·시간:** 최대 128개 대상, 제외된 수 표시, 수집 100초를 남은 대상에 배분, 조회·답장 준비 120초. 실제 분할 전송 시간은 별도입니다. 한 텍스트 8 Mi 문자, 원문 합계와 준비 답장 각각 32 Mi 문자 한도이며 초과를 정상 결과처럼 자르지 않습니다.
- **전송:** 이미 접수한 명령만 화면 밖 재확인을 허용합니다. 최초 명령은 보이는 enabled 전체 메시지여야 합니다. 접수 이후 옛 Ready의 표시 본문은 바뀔 수 있어도 이력 행 식별·순서, 같은 명령 본문·enabled·계층은 계속 확인합니다. 이력 삭제·재구성은 지원하지 않습니다.
- **최신 증거:** 각 답장에 서로 다른 최신 관찰 두 개, 첫 관찰 시각, 정확한 본문에 묶인 일회성 전송을 유지합니다. 오래된 접수 증거는 식별 기준일 뿐 현재 화면의 증거가 아닙니다. 0.3.8부터 이 증거의 유효기간은 `RoundTripTest.ProofAgeLimit` 35초이며, 스냅샷 1회의 협조적 상한 15초(`RECEIVE_PHASE_TIME_LIMIT`)의 2배에 5초를 더한 값입니다. 증거는 설계상 서로 다른 제한된 스냅샷 2회만큼 오래되므로 상한의 2배보다 작은 한도는 긴 대화에서 충족될 수 없습니다. 첫 관찰 시각을 공유해 다섯 지점에서 검사하는 규칙과 5초 재관찰 빠른 경로는 그대로입니다. 불명확한 전송을 재시도하거나 남은 초안을 자동으로 지우지 않습니다.
- **중단과 재개:** 커서 이동·입력·클릭이 한 번도 시도되지 않은 채 끝난 요청만 0.3.8부터 재개합니다. 재개는 새 M코드와 새 `Master Ready`로 다시 수신하는 것이며 재전송이 아닙니다. 중단된 회차의 M코드·동의·증거는 재사용하지 않고, 준비했던 부분을 다시 보내지 않으며, Output 이력을 갱신하지 않으므로 다음 조회가 보내지 못한 부분을 다시 포함합니다. 연속 3회(`StatusSession.AbortResumeLimit`)째는 `STATUS_REQUEST_ABORT_LIMIT`로 세션을 끝내고, 입력이 시도되었거나 시도 여부를 알 수 없으면 종전대로 `STATUS_REQUEST_STOPPED`로 끝냅니다(불확실한 전달은 항상 우선). 운용자 Stop은 그대로 세션을 끝냅니다.
- **이력 저장:** 모든 부분이 성공한 뒤에만 길이·SHA256을 저장합니다. 부분 전송 실패 다음 조회에서 앞부분이 반복될 수 있지만 미송신 본문을 누락하면 안 됩니다. UI 전송/입력창 비워짐 확인은 인증된 모바일 수신 확인이 아닙니다.
- **watchdog 판정(0.3.9):** 완료는 상태가 `READ`이고 출처가 `BUFFER` 또는 `AUTO_COPY`인 대상의 Output 본문에서만 판정합니다(`PowerSiCompletion`). OCR·LLM·화면 본문은 데이터이며 판정하지 않습니다. 앞뒤 공백을 뺀 줄 전체가 `AFS Current Frequency ( GHz|MHz ) = <수>`, `AFS Finished`, `Total Sampling Points = <정수>`인 줄만 표시로 보고 마지막 표시 블록이 결정합니다: 마지막 `AFS Finished` 뒤에 Total 줄이 있으면 완료, 그 뒤에 주파수 줄이 나오면 실행 중(새 실행), Total 줄이 아직 없으면 완료 대기, 표시가 없으면 판정 불가입니다. CPU·경과 시간·프로세스 존재는 완료 근거가 아닙니다. 대상은 PID와 시작 시각으로 고정하며, 보고서의 시작 시각이 등록 값과 다르거나 완전한 보고서(`OK`, 부분·누락·읽기 불가 없음)에 그 PID가 없을 때만 종료·재시작으로 보고 해제합니다. 판정 불가(Pending·수집 실패·시간 초과·수집 안 함·빈 Output·OCR·표시 없음)와 Slave 조회 실패는 감시를 유지하고 각각 3회 연속에 도달할 때 한 번 경고합니다. 실패한 확인도 그 주기의 시도로 보아 다음 확인은 한 주기 뒤입니다. watchdog 확인은 PowerSI Output 이력을 읽거나 갱신하지 않으므로 다음 `pwrsi`의 추가분 기준은 바뀌지 않습니다.
- **watchdog 알림(0.3.9):** 알림은 Ready와 같은 알림 경로로 보내며, 부분마다 정확한 본문에 묶인 일회성 동의(`ForWatchdogNotice`, 머리글이 그 알림 자신의 코드)와 입력 전 새 스냅샷 검사(같은 프로세스, 연속성, 그 알림 코드의 회신 부재, 현재 Ready 뒤 대기 중인 명령 없음 `WATCHDOG_NOTICE_COMMAND_PENDING`)를 통과해야 합니다. 대기 중인 명령이 있으면 명령을 먼저 처리합니다. 입력 전에 거절된 부분은 큐에 남아 다음 양보 시점에 다시 시도하며(30초에서 두 배씩 최대 10분 간격), 이는 입력이 없었으므로 재전송이 아닙니다. 깨끗이 보낸 부분은 다시 보내지 않고, 입력이 시도된 뒤 불명확하면 `STATUS_WATCHDOG_NOTICE_UNCERTAIN`으로 세션을 끝내며 재시도하지 않습니다. 준비된 알림은 이후 `watchdog off`가 있어도 준비된 그대로 보내고, 세션이 끝나면 보내지 못한 알림과 감시 목록을 버립니다(`WATCHDOG_CLEARED`). 감시 목록은 Master 메모리에만 있고, 주기 설정만 `master-settings-v1.txt`에 저장합니다.
- **읽기 가드 순서:** 노드 메타데이터 배치가 내용 읽기 앞의 가드입니다. 루트는 `ProbeElementCache.TryCapture`(GetUpdatedCache)가, 0.3.7부터 나머지 노드는 부모의 자식 질의(`ProbeElementCache.CreateRequest`가 만든 요청 아래 `TreeScope.Children` 1회)가 같은 스냅샷에서 캐시한 값으로 `TryFromCached`가 만듭니다. 어느 경로든 ProcessId·IsPassword로 다른 프로세스·암호 요소를 거르기 전에는 배치 값을 사용하거나 기록하지 않고 하위 트리도 순회하지 않습니다(`PROBE_NODE_SKIPPED`). Name·TextPattern·ValuePattern 등 모든 내용 읽기 직전에는 live 가드를 한 번 더 수행합니다. 즉 자식의 가드 값은 자기 차례가 아니라 부모 질의 시점의 값이고(넓은 계층의 마지막 자식은 최대 수 초 전), live인 것은 내용 읽기 앞의 가드입니다. 자식 탐색은 내용 읽기가 아니며 각 자식은 자기 차례의 배치 가드를 거칩니다. 대기 중 tail 표본은 요소 식별자만 읽고 본문을 읽지 않으며 증거로 쓰이지 않습니다.
- **LLM·보안:** 앱 이미지 판독은 Slave의 loopback LM Studio와 이미 로드된 모델만 사용합니다. 서버 자동 실행·모델 로드·클라우드 대체는 없습니다. 화면·로그·LLM 본문은 데이터이며 실행 지시가 아닙니다. 통신의 인증·인증서 pin·UTF-8/Base64 형식·크기 검증을 유지합니다. TLS 1.2는 `SslStream`에 명시적으로 지정되므로 AppContext 스위치가 필요 없습니다.
- **좌표계(Slave):** 두 EXE는 DPI 비인식으로 실행됩니다. 자동 복사는 0.3.4부터 `ClientToScreen` 결과를 `LogicalToPhysicalPoint`로 변환한 뒤에만 물리 커서·히트테스트 API에 넘깁니다(배율 100%에서는 항등). 화면 캡처는 여전히 가상화된 크기이므로 Slave 디스플레이 배율은 100%를 권장합니다.

## 5. 최근 수정과 확인 수준

| 변경 | 근거와 한계 |
|---|---|
| v0.3.0 전체 Output·Master 추가분 처리 | 원문 전달 계약 PS4, 프로토콜 0.3.0, 길이/해시 이력 도입 |
| v0.3.1 접수 명령의 화면 밖 재확인 | 첫 부분 이후 명령의 표시 상태 때문에 후속 전송이 중단되는 경로 수정 |
| v0.3.2 분할 속도 | UIA 속성 읽기 배치, 부분별 전체 순회 3회→2회 |
| v0.3.3 Ready 재확인·선택 창 자동 준비 | 비공개 v0.3.1 로그의 `RECEIVE_READY_BOUNDARY_CHANGED` 경로 수정. 배경 읽기, 선택 창 복원·활성화 |
| **v0.3.4 검토 반영 (Master)** | A1 처리되지 않은 예외 → `APP_FATAL` 안전 중단, A2 `STATUS_SESSION_FAILED`에 예외 형식 기록(WARN), A3 UIA 루트 읽기 예외 → `TARGET_ROOT_UNAVAILABLE`, A4 단계 보고 중복 억제를 준비마다 초기화, A11 활성화/복원 거부 시 `OPERATIONAL_TARGET_DENIED`(win32_error), A13 신원 확인 전 foreground 재확인, B1 화면 밖/비활성 첫 명령 행의 `COMMAND_NOT_VISIBLE` 신호(접수 규칙 불변), B8 수신 단계 사유 코드, B11 Ready 행 대기의 재시도별 15초 예산과 60초 총 한도 `RECEIVE_READY_NOT_OBSERVED`, C1 공백 OCR null 가드, C2 해시 1회, C4 상태 줄 구성 순서, F16 Ready/처리 안내 한국어(선두 토큰 유지), F17/F18/F19/F20 답장 문구, E1 Master `--self-test`에 Link 파싱 검사, E7 자체 검사 정리 보호, UI F1~F7·F21~F26·A7·A9·A10 |
| **v0.3.5 Ready 전 PC 입력 대기 (G1)** | Win7 Master v0.3.4 현장 로그: `STATUS_SESSION_BEGIN` 직후 `WAITING_FOR_PC_IDLE`에 들어가 52초 동안 조건이 충족되지 않아 Ready가 발송되지 않았고 사용자가 Stop(`TARGET_CANCELLED`). v0.3.3이 도입한 `OperationalTarget.WaitForPcIdle`는 가상 키 1~254 전체를 훑고 입력 경과 1초를 요구했으며 어느 조건이 막는지 기록하지 않았음. 0.3.5: 키 검사를 현장 검증된 `MouseClickInput.HeldKeys` 10개(마우스 버튼·수정 키)로 통일(타이핑은 1초 경과 조건이 담당), 2초 후·10초마다 `OPERATIONAL_TARGET_IDLE_WAIT`(reason, input_age_ms, held_keys, foreground_pid, gui_*) 기록과 화면 안내, 60초 한도 `TARGET_PC_NOT_IDLE`. 어느 조건이 현장에서 막았는지는 새 로그가 있어야 확정됨 |
| **v0.3.6 속도 개선 (G2)** | Win7 Master v0.3.5 현장 로그(KI-Messenger 3.5.52, UIA/MSAA 프록시를 통한 Chromium 계열 창) 측정: 전체 UIA 스냅샷 1회가 466~496 노드에 5,654~6,842 ms(노드당 약 12.3 ms, read_failures=0, skipped=0), `ProcessIdentity.Capture`가 136 MB 실행 파일의 WinVerifyTrust+X509 때문에 1회 약 0.34초이며 답장 부분마다 약 6회(probe, ReceiveProbe 시작, CheckRoot, `OperationalTarget.VerifyFullIdentity` 등) 반복 → 가설 A12 현장 확정, 각 부분의 보호된 클릭 직후 `PrepareSend → WaitForPcIdle`가 자기 클릭 때문에 `WAITING_FOR_PC_IDLE:INPUT_RECENT`로 약 1.1~1.7초 대기, 부분당 약 17.7초(대기 약 1.7 + 재관찰 스냅샷 약 6.5 + 발신자 스냅샷 약 6.4 + 신원 확인 약 0.7 + 입력·클릭 약 2.3)로 11부분 `pwrsi` 회신이 약 3분, 명령 인식은 순수 폴링이라 같은 전체 본문이 연속 두 전체 스냅샷에 나와야 하므로 로그에서 Ready 뒤 약 24초(polls=4) → 가설 B5(노드당 UIA 왕복)가 지배적 비용임을 확정. 0.3.6 조치: (1) `ReadOnlyProbe.Visit`의 노드당 provider 왕복 12→5(배치가 내용 앞 가드, Name 1회 읽기, 자식 탐색 전 가드 제거)와 `guard_ms/cache_ms/name_ms/content_ms/nav_ms` 기록, (2) `ProcessIdentity`의 서명 검증만 (pid·프로세스 시작 시각·경로·파일 길이·수정 시각) 키로 프로세스 수명 1건 캐시(`signature_cached`), (3) 자기 클릭 tick을 입력 대기에서 제외(`own_input`), (4) 대기 상태에서 250 ms 간격의 내용 없는 tail 구조 표본으로 전체 스냅샷 시작 시점을 결정(`RECEIVE_CHANGE_TRIGGER`). 확인 수준은 아래 **0.3.6 검증 수준**이며, 결과는 **0.3.6 현장 확인**에 기록했습니다: (2)(3)(4)는 확인되었고 (1)은 스냅샷 시간을 줄이지 못했습니다. 당시 추정치였던 부분당 약 10~12초는 실제 약 14.1초였습니다 |
| **v0.3.7 자식 질의 (G3)** | Win7 Master v0.3.6 첫 현장 로그 측정: 전체 UIA 스냅샷 1회가 593~632 노드(대화가 자람)에 6,251~7,696 ms로 0.3.5와 사실상 같았고, 0.3.6이 추가한 항목별 시간이 비용의 위치를 확정했습니다 — `nav_ms` 5,064~5,893(약 80%, `GetFirstChild`/`GetNextSibling` 이동), `cache_ms` 1,059~1,159(약 16%, 노드당 배치), `guard_ms` 약 20, `name_ms` 약 15, `content_ms` 0. 0.3.6의 왕복 축소(가드·Name)는 절감이 거의 없었고, 실제 비용은 MSAA 프록시가 형제 이동마다 부모의 자식을 다시 열거하는 구조(계층당 O(N²))였습니다. 0.3.7 조치: `ReadOnlyProbe.Visit`가 `TreeWalker.RawViewWalker`의 `GetFirstChild`/`GetNextSibling` 대신 방문 노드마다 `ProbeElementCache.CreateRequest(...).Activate()` 아래에서 `element.FindAll(TreeScope.Children, Condition.TrueCondition)` 1회를 실행합니다(TreeFilter를 TrueCondition으로 두어 RAW 뷰를 그대로 열거, `AutomationElementMode.Full`로 자식이 live 참조, 같은 11개 속성·21개 패턴과 포인터 단계의 BoundingRectangle). 자식은 반환 순서대로 같은 깊이 우선 번호로 방문하고, 각 자식의 `ProbeElementCache`는 그 질의가 수 ms 전에 캐시한 값으로 새 `ProbeElementCache.TryFromCached`가 만듭니다(같은 `SecurityRejection` 내용 앞 가드, 같은 `Cached<T>` 기본값, 같은 식별자·`patternNames`). 루트만 `TryCapture`(GetUpdatedCache)를 유지합니다. 약 600노드 스냅샷 기준 provider 왕복 추정 약 1,800 → 약 600이고, 약 50행의 이력 계층은 약 2,550회 자식 마샬링 대신 1회 열거입니다. `READ_ONLY_PROBE_RESULT`에 `children_ms`·`children_queries` 추가(`nav_ms`는 이제 루트 조회만, `cache_ms`는 루트 배치만). MaxNodes 2048·깊이 32·협조적 15초·`PROBE_NODE_SKIPPED` 사유·내용 읽기 직전 live 가드·보관 요소·레이아웃 가드는 불변. 확인 수준은 아래 **0.3.7 검증 수준**이며 기대 효과는 추정입니다 |
| **v0.3.8 증거 유효기간·재개 (G4)** | Win7 Master v0.3.6 야간 로그: 대화가 더 자라 전체 UIA 스냅샷 1회가 632~702 노드에 6.7~8.8초가 된 상태에서 두 세션이 같은 순서로 끝났습니다 — 명령 인식 → 재관찰 스냅샷(8.0~8.2초) → 발신자의 새 스냅샷(7.4~7.8초) → `SUPERVISED_SEND_FAILED property=bound_receive_snapshot`, `SUPERVISED_SEND_RESULT status=REJECTED reason=ROUNDTRIP_PROOF_EXPIRED_OR_WINDOW_CHANGED attempted=False` → `ROUNDTRIP_RESULT PART_UNCERTAIN_ABORTED` → `STATUS_SESSION_END reason=STATUS_REQUEST_STOPPED`. 원인은 `RoundTripTest`의 다섯 지점이 재관찰의 첫 캡처부터 15초를 증거 유효기간으로 쓴 것이며, 제한된 스냅샷 2회가 15.5~16.0초여서 한도를 넘었습니다(같은 날 앞선 실행은 612~632 노드·13.4초로 우연히 통과). 창 이동·프로세스 변경·PC 잠금은 없었고 사용자가 입력하거나 클릭한 것도 없습니다. 두 번째 세션은 1시간 18분(440 폴링) 동안 오류 없이 대기하다 늦게 도착한 명령을 정확히 인식했지만, 이 중단이 곧바로 세션을 끝내 회신이 가지 않았고 아침에 Master가 멈춘 채 발견되었습니다. 0.3.8 조치: (1) `RoundTripTest.ProofAgeLimit` = 2 × `ReceivePhaseTimeLimit`(15초, `ReceiveProbe`의 스냅샷당 협조적 상한 `RECEIVE_PHASE_TIME_LIMIT`를 반영) + 5초 = 35초가 다섯 곳(`AuthorizeHandoffCore` 2, `CompleteRefreshedProof` 2, `SelectRefreshedProof` 1)의 15초 비교를 대체하며 첫 캡처 시각 공유·검사 지점·5초 재관찰 빠른 경로는 불변, (2) 입력이 시도되지 않은 중단은 새 M코드·새 Ready로 재개하고 연속 3회 한도(`StatusSession.AbortResumeLimit`)와 입력 시도 시 종료 규칙을 둠(`StatusSession.DecideAfterAbort`, `SupervisedSendTest.Outcome.InputAttempted`). 확인 수준은 아래 **0.3.8 검증 수준**이며 현장 미확인입니다 |
| **v0.3.9 watchdog 명령** | 현장 로그가 아니라 기능 추가입니다. 휴대폰에서 `watchdog on`, `watchdog on <PID>`, `watchdog off`, `watchdog off <PID>`, `help watchdog`(정확한 전체 본문, 소문자, 단어 사이 한 칸, PID는 앞자리 0 없는 1~10자리, `WatchdogCommand`)을 접수합니다. `watchdog on`은 `total status`와 같은 STATUS 목록 조회로 시작 시각이 있는 PowerSI를 그 시점에 고정해 목록에 합치며(`WatchdogState.Arm`), 답장은 감시 시작·이미 감시 중·실행 중인 PowerSI 없음·PID 없음·Slave 연결 실패(자동 재시도 없음) 중 하나입니다. `watchdog off`는 Slave 조회 없이 모두 해제·하나 해제·감시 대상 없음·해당 PID 없음으로 답합니다. 등록·해제는 답장 준비 시점에 적용됩니다. 주기(30/60분, 허브 화면 설정 `WatchdogSettings`)가 되면 명령 대기의 idle 지점에서만 양보(`ReceiveProbe.MayYield`, `WAIT_YIELDED`)해 `pwrsi`와 같은 수집을 한 번 실행하고 `PowerSiCompletion`으로 판정합니다. 완료·해제는 완료 알림으로, 3회 연속 판정 불가·조회 실패는 경고로 새 코드의 `WATCHDOG` 알림(1,400자 초과 시 PART 분할)을 보내고, 모두 보낸 뒤 새 `Master Ready`를 보냅니다. 운용 창에는 감시 요약 줄(감시 수·다음 확인 시각), `WATCHDOG_CHECKING`·`NOTICE_WATCHDOG` 단계와 세션 종료 사유 설명이 추가되었습니다. 종료 사유 설명은 기존 공백의 수정입니다: 운용 세션의 `STATUS_SESSION_STOPPED — <사유>; ...` 결과가 설명표와 맞지 않아 0.3.8의 `STATUS_REQUEST_STOPPED`/`STATUS_REQUEST_ABORT_LIMIT` 설명이 표시되지 않았습니다. 프로토콜·Slave·접수 규칙·Output 이력 규칙은 불변입니다. 확인 수준은 아래 **0.3.9 검증 수준**이며 현장 미확인입니다 |
| **v0.3.4 검토 반영 (Slave·Link)** | D1 자동 복사 논리→물리 좌표 변환과 LiveTest 강화, D2 Master 요청이 로컬 새로고침을 취소, D3 `total status` 두 번째 스냅샷 1회(자체 검사에 5초 상한), D4 수집 콜백/응답 실패를 연결 단위로 격리(`COLLECT_FAILED`/`RESPONSE_FAILED`, UI 문구 구분, loopback 회귀 검사), D5/F12 연결파일 저장 경고, D6~D10, D11, E2 캡처 워커 부모 한도 8/6/4초, E10 stderr 드레인, E13 설정 검증 매핑(+순수 자체 검사), E14 서로게이트 보호, F9~F11·F13~F15·F23 Slave UI, E4/E5 스크립트, E6 `.gitignore`, D13/E8 매니페스트 0.3.4.0 |

**v0.3.4 검증 수준(2026-09-21):**

- Linux에서 net48 참조 어셈블리로 두 프로젝트 컴파일: 경고 0, 오류 0. Mono로 클래스별 자체 검사를 개별 실행해 Windows API가 필요 없는 단위(Master 14개, Slave/Link 8개)가 모두 통과했고, 변경 전과 통과 목록이 동일합니다(신규 검사 추가분 제외).
- [CI run 35558060129](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35558060129)(Windows Server 2025, 커밋 `fc28435`)에서 두 Release 빌드, 두 실제 EXE `--self-test`(새 Windows 전용 검사 포함), 두 설치파일 생성·자산 검사, 두 역할의 설치/동일 버전 재설치/제거/설정 보존 검사가 통과했습니다. 앞선 두 실행은 `test-installers.ps1`의 `$version:` 파싱 오류로 설치 검사 단계에서 실패했고 같은 PR에서 수정했습니다.
- 고대비(High Contrast) 모드가 켜진 Windows에서 `--self-test`를 실행하면 `ReceiveForm`의 색상 단언(`DarkGreen`/`LightYellow`)과 F25의 시스템 색 대체가 충돌할 수 있습니다. 일반 테마(CI 포함)에서는 영향이 없습니다.

**v0.3.4 정식 배포(2026-09-21):** `main` 병합 커밋 `932be45`에서 [CI run 35561827064](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35561827064)(`workflow_dispatch release_tag=v0.3.4`)가 두 Release 빌드·두 실제 EXE `--self-test`·설치파일 생성·자산 검사·두 역할 설치/재설치/제거/설정 보존 검사를 통과한 뒤 태그와 [정식 Release](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.4)를 발행했습니다. 공개 자산 5개를 익명으로 내려받아 SHA256이 SHA256SUMS.txt 및 GitHub digest와 일치함, ZIP 항목이 허용 목록과 같음, 두 ZIP의 Slave 바이너리가 동일함을 확인했습니다(§9). CI 통과는 실제 Win7/Win11 설치·운용 확인이 아닙니다.

**0.3.5 검증 수준:** Linux 컴파일 경고 0·오류 0, Mono 자체 검사 통과 목록 동일(`OperationalTarget`·`MouseClickInput` 자체 검사에 키 집합·사유 우선순위·입력 경과 규칙 검사 추가). PR #3 CI(run 35565043828)와 `main` 병합 커밋 `d27c2b8`의 [배포 실행 35565471150](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35565471150)이 두 Release 빌드·두 실제 EXE `--self-test`·설치파일 생성·자산 검사·두 역할 설치/재설치/제거/설정 보존 검사를 통과하고 [Release v0.3.5](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.5)를 발행했습니다. 공개 자산 5개를 익명으로 내려받아 SHA256이 SHA256SUMS.txt·GitHub digest와 일치함, 두 ZIP의 Slave 바이너리가 동일함을 확인했습니다(§9). 그 뒤 Win7 현장 로그(G2)에서 0.3.5의 Ready 발송·명령 접수·11부분 분할 회신이 이루어졌고, 같은 로그의 속도 측정이 0.3.6 변경의 근거입니다(§5).

**0.3.6 검증 수준:** Linux에서 net48 참조 어셈블리로 두 프로젝트 컴파일(경고 0, 오류 0). Mono로 클래스별 자체 검사를 개별 실행해 `ReceiveProbe`·`ReadOnlyPair`·`RoundTripTest`·`OperationalTarget`·`MouseClickInput`·`ProcessIdentity`·`ReadOnlyCommands`·`SendMetadataProbe`·`PowerSiOutputHistory`가 통과했고, Windows API·UIA·WinForms가 필요해 Mono에서 실행할 수 없는 실패 12건의 목록은 변경 전과 같습니다. Windows Release 빌드, 두 실제 EXE `--self-test`(`ProcessIdentity.RunSelfTest` 포함), 설치파일 생성·자산 검사는 CI(Windows Server 2025)에서 수행합니다. PR #5 CI([run 35569778571](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35569778571), 커밋 `ddfa6ba`)와 `main` 병합 커밋 `e5c5389`의 [배포 실행 35570250448](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35570250448)(`workflow_dispatch release_tag=v0.3.6`)이 두 Release 빌드·두 실제 EXE `--self-test`·설치파일 생성·자산 검사·두 역할 설치/재설치/제거/설정 보존 검사를 통과하고 [Release v0.3.6](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.6)을 발행했습니다. 공개 자산 5개의 해시·ZIP 항목·Slave 바이너리 동일성은 §9에 기록했습니다. 이후 G3 로그로 네 변경의 결과가 확인되었습니다(아래 **0.3.6 현장 확인**).

**0.3.6 현장 확인(G3, Win7 Master v0.3.6 첫 로그):** 네 변경 중 셋이 확인되었습니다. 서명 검증 캐시는 `PROCESS_IDENTITY signature_cached=True` 18/18, 자기 클릭 idle 규칙은 `OPERATIONAL_TARGET_PHASE`/`WAITING_FOR_PC_IDLE` 기록이 한 건도 없음(0.3.5 로그는 24건), 수신 트리거는 `RECEIVE_CHANGE_TRIGGER`의 `reason`이 `NOT_IDLE` 3·`PERIODIC` 6(`waited_ms` 약 3,070~3,140, `samples=11`, `chain_depth=4`)·`TAIL_CHANGED` 1(`waited_ms=245`, `samples=1`)로 설계대로 동작했습니다. 명령이 스냅샷 도중 도착해 4번째 폴링이 Text 자식 없는 새 행만 보았고(606→609 노드, `new_texts=0`), 그 스냅샷 245 ms 뒤 트리거가 걸려 5번째 폴링(612 노드)이 후보를, 6번째가 확정을 관측했습니다(`CANDIDATE_OBSERVED polls=6`, 도착 후 약 스냅샷 2.5회). 대기 중 전체 스냅샷도 연속 실행이 아니라 약 9.5초(표본 3초 + 스냅샷 약 6.5초)마다 1회가 되었습니다. 반면 스냅샷 자체는 빨라지지 않았고(위 v0.3.7 행의 측정), 답장 부분은 약 17.7초 → 약 14.1초(BASELINE → `SUPERVISED_SEND_RESULT`, 이 실행은 1부분)로 남은 약 13.3초가 스냅샷 2회입니다. WARN·읽기 실패·건너뛴 노드는 없었고, 세션은 운용자 Stop(`RECEIVE_UI_STOP` → `RECEIVE_CANCELLED` → `STATUS_REQUEST_STOPPED`)으로 끝났으므로 ERROR 2건은 그 취소이며 결함이 아닙니다.

**0.3.6 야간 현장 결과(G4, Win7 Master v0.3.6 야간 로그, 2026-09-21 23:23~익일 00:48 KST):** 같은 0.3.6 Master를 밤새 켜 둔 로그입니다. 대화 기록이 더 자라 스냅샷 1회가 632~702 노드·6.7~8.8초였고, 두 세션이 모두 답장 증거의 유효기간 초과로 끝났습니다(순서는 위 v0.3.8 행). 첫 세션은 처리 안내 뒤 회신 1부분에서, 둘째 세션은 처리 안내 자체에서 중단되었습니다. 중단 직전의 제한된 스냅샷 2회는 15.5~16.0초였고, 같은 날 앞선 실행(612~632 노드, 13.4초)은 같은 15초 한도를 우연히 통과했습니다. 창 이동·프로세스 변경·PC 잠금은 없었고 사용자가 입력하거나 클릭한 것도 없습니다. 둘째 세션은 1시간 18분·440 폴링 동안 오류 없이 대기했고(로그 전체의 `RECEIVE_CHANGE_TRIGGER`는 `PERIODIC` 452·`TAIL_CHANGED` 1·`NOT_IDLE` 6) 늦게 도착한 명령도 정확히 인식했으므로 0.3.6의 상시 대기·인식 경로 자체는 밤새 동작했습니다. 실패한 것은 인식 이후의 증거 유효기간이며, 그 중단이 세션을 끝내 회신이 가지 않았고 아침에 멈춘 채 발견되었습니다. 이것이 0.3.8 두 변경의 근거입니다.

**0.3.7 검증 수준:** Linux에서 net48 참조 어셈블리로 두 프로젝트 컴파일(경고 0, 오류 0). Mono로 클래스별 자체 검사를 개별 실행한 통과 목록과 Windows API·UIA·WinForms가 필요해 실행할 수 없는 실패 12건의 목록은 0.3.6과 동일합니다. Windows Release 빌드, 두 실제 EXE `--self-test`(`ProbeElementCache.RunSelfTest`에 추가된 `PROBE_CACHE_CHILDREN_QUERY_EMPTY`, `PROBE_CACHE_FROM_CACHED_REJECTED`/`IDENTITY_MISMATCH`/`METADATA_MISMATCH`/`FOREIGN_ACCEPTED` 포함), 설치파일 생성·자산 검사는 CI(Windows Server 2025)에서 수행하며 PR #7 CI([run 35572781784](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35572781784), 커밋 `89dd292`)와 `main` 병합 커밋 `7e4d52a`의 [배포 실행 35573343568](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35573343568)(`workflow_dispatch release_tag=v0.3.7`)이 두 Release 빌드·두 실제 EXE `--self-test`(새 `ProbeElementCache` 자식 질의 검증 포함)·설치파일 생성·자산 검사·두 역할 설치/재설치/제거/설정 보존 검사를 통과하고 [Release v0.3.7](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.7)을 발행했습니다. 공개 자산 5개의 해시·ZIP 항목·Slave 바이너리 동일성은 §9에 기록했습니다. 0.3.7 변경은 현장 미확인입니다. 스냅샷 약 6.5초 → 약 1.5~2.5초, 답장 부분 약 14초 → 약 4~6초, 인식 지연 도착 후 약 5~7초는 `children_ms`가 0.3.6의 배치 비용(약 1.1초)에 계층별 열거 1회를 더한 수준에 머문다는 가정의 **추정이며 측정값이 아닙니다.** 다음 Win7 로그의 `READ_ONLY_PROBE_RESULT`의 `elapsed_ms`·`children_ms`·`children_queries`·`nodes`로만 확정합니다.

**0.3.8 검증 수준:** Linux에서 net48 참조 어셈블리로 두 프로젝트 컴파일(경고 0, 오류 0). Mono로 클래스별 자체 검사를 개별 실행한 통과 목록과 Windows API·UIA·WinForms가 필요해 실행할 수 없는 실패 12건의 목록은 0.3.7과 동일합니다. 새 순수 검사는 `RoundTripTest`(`ProofAgeLimit`가 스냅샷 상한의 2배 이상, 두 캡처 합계 16초인 증거의 수락, 한도에 도달한 증거의 거절, 기존 16초 만료 사례를 한도 기준으로 재지정)와 `StatusSession`(`DecideAfterAbort` 9가지 조합, 입력 없는 중단만 1회 해제되고 정상 완료·불명 전송·입력 시도는 거절)입니다. Windows Release 빌드, 두 실제 EXE `--self-test`, 설치파일 생성·자산 검사는 CI(Windows Server 2025)에서 수행하며 PR #9 CI([run 35669700042](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35669700042), 커밋 `2411aa3`)와 `main` 병합 커밋 `620b38d`의 [배포 실행 35670256709](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35670256709)(`workflow_dispatch release_tag=v0.3.8`)이 두 Release 빌드·두 실제 EXE `--self-test`(중단 판정표·재개 경로 검사 포함)·설치파일 생성·자산 검사·두 역할 설치/재설치/제거/설정 보존 검사를 통과하고 [Release v0.3.8](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.8)을 발행했습니다. 공개 자산 5개의 해시·ZIP 항목·Slave 바이너리 동일성은 §9에 기록했습니다. 0.3.8 변경은 현장 미확인입니다. 남는 한계: 스냅샷 1회가 약 17.5초(0.3.6 속도의 약 1,500노드)를 넘으면 35초 한도보다 스냅샷당 15초 상한(`RECEIVE_PHASE_TIME_LIMIT`, 이 역시 입력 전 중단)이 먼저 걸리므로 아주 긴 대화에는 새 대화가 여전히 해법이며, 0.3.7의 자식 질의가 스냅샷을 줄여 줄 것으로 보이나 측정되지 않았습니다.

**0.3.9 검증 수준:** Linux에서 net48 참조 어셈블리로 두 프로젝트 컴파일(경고 0, 오류 0). Mono로 클래스별 자체 검사를 개별 실행한 결과는 통과 21건·실패 12건이며, 새로 통과한 6건은 watchdog 순수 검사(`PowerSiCompletion`과 `Watchdog.cs`의 검사 클래스)이고 실패 12건은 종전처럼 Windows API·UIA·WinForms가 필요해 Mono에서 실행할 수 없는 단위입니다. 기존 클래스의 자체 검사에도 watchdog 단언이 추가되었습니다: `ReceiveProbe`(양보 규칙 256조합 중 idle 1조합만 허용, 양보 뒤 같은 Ready 행 재결합, 알림 행 뒤 명령 접수, 화면 밖 명령 행은 양보도 막음), `RoundTripTest`(양보 결과의 조건, watchdog 명령은 처리 안내·PC 상태 표본 없음), `StatusSession`(알림 결정표, 30초→10분 간격, 입력 없는 양보만 해제, `WATCHDOG_NOTICE_COMMAND_PENDING`), `SupervisedSendTest`(알림 동의의 정확한 본문·1회 사용·머리글 코드), `ReadOnlyCommands`(문법·도움말·답장 형식·등록/해제와 `WATCHDOG_COMMAND` 기록), `MasterHubForm`(주기 선택·요약 줄·종료 사유 설명·loopback `watchdog on`/`off`, Windows 전용). Windows Release 빌드, 두 실제 EXE `--self-test`, 설치파일 생성·자산 검사는 CI(Windows Server 2025)에서 수행하며 **PR CI와 배포 실행은 §9 기록 시 갱신합니다.** 0.3.9 변경은 현장 미확인입니다. 남는 위험: 무인 주기 수집이 버퍼 읽기에 실패하면 Slave에서 PowerSI 활성화와 자동 복사(포커스·클립보드)를 쓸 수 있으며 Slave에는 사용자 작업 중 여부를 보는 보호가 없습니다. 확인 중 보낸 명령은 최대 약 2분 기다리고, 명령 행이 화면 밖에 고정된 동안에는 확인과 알림도 미뤄집니다. 각 알림은 실제 Win7 UI 전송이므로 idle 대기를 거치며 불확실하면 세션이 끝납니다. 완료된 대상은 알림 전송 확인 전에 목록에서 빠집니다. 새 실행이 첫 주파수 줄을 출력하기 전에는 직전 실행의 완료 블록이 완료로 보일 수 있습니다. 실제 PowerSI Output 형식은 현장 확인 전입니다. STATUS 목록은 128개로 제한되어 그 밖의 PowerSI는 등록 시점에 없는 것으로 보입니다. 감시는 세션과 함께 끝납니다(Stop·잠금·절전). 상세는 [검토 기록 §14](docs/REVIEW-2026-09-21-v0.3.3.md).

**미확인:** 0.3.9 watchdog의 실제 Win7/Win11 동작(주기 양보·무인 수집·알림 전송과 새 Ready, 무인 수집 중 Slave의 PowerSI 활성화·자동 복사, 실제 PowerSI Output의 완료 표시 형식), 0.3.8의 35초 증거 유효기간이 실제 야간 운용에서 중단을 없애는지와 입력 전 중단의 재개(회신 없는 추가 `Master Ready`, 연속 3회 한도)가 현장에서 어떻게 보이는지, 0.3.7의 자식 질의 전환이 실제 Win7에서 내는 속도 효과와 부작용(자식 순서·노드 번호, 순회 중 사라진 자식의 `read_failures`), 0.3.4의 한국어 Ready 안내·카운트다운·`COMMAND_NOT_VISIBLE`·Ready 대기 한도의 동작, Win11 Slave의 실제 디스플레이 배율에서 자동 복사(D1)와 캡처, .NET 4.8이 없는 깨끗한 오프라인 PC의 설치 경로. 0.3.6의 서명 캐시·자기 클릭 idle 규칙·대기 중 tail 표본은 G3 로그에서 확인되었고(위 **0.3.6 현장 확인**), 노드당 왕복 축소는 효과가 거의 없음이 함께 확인되었습니다. 0.3.6의 상시 대기·명령 인식은 G4 야간 로그에서도 1시간 18분 동안 동작했습니다. v0.3.5 현장 로그(G2)에서 Ready 발송·명령 접수·11부분 분할 회신 자체는 이루어졌으므로 G1 경로도 미확인이 아닙니다.

## 6. 다음 점검 우선순위

검토에서 코드 변경 없이 남긴 항목입니다(ID는 [검토 기록](docs/REVIEW-2026-09-21-v0.3.3.md) 기준).

1. **G4/G3 후속:** Win7 Master를 0.3.8 이상(현재 0.3.9)으로 올린 뒤 새 현장 로그에서 먼저 G4 항목을 확인합니다. `ROUNDTRIP_PROOF_EXPIRED_OR_WINDOW_CHANGED`가 한 건도 없어야 하고, `STATUS_REQUEST_ABORTED`는 없거나 드물게 `resumed=true`여야 하며(있으면 `reason`·`input_attempted`·`consecutive`를 함께 기록), `STATUS_REQUEST_ABORT_LIMIT`가 있으면 연속 중단이 일어난 것입니다. 이어서 G3 항목으로 `READ_ONLY_PROBE_RESULT`의 `elapsed_ms`·`children_ms`·`children_queries`·`nodes`와 `row_prefix_preserved`·`read_failures`·`complete`를 확인합니다. `children_queries`는 방문 노드 수와 같은 수준이어야 하고, `nodes`가 0.3.6 로그와 같은 규모이고 `row_prefix_preserved`가 유지되면 자식 순서·노드 번호가 raw walker와 같다는 뜻입니다. `read_failures`가 늘고 `complete=false`가 되면 순회 중 자식이 사라진 경우입니다. 0.3.6 로그와 같은 항목(스냅샷 1회 시간, 부분당 시간, 인식까지의 polls)을 비교해 개선 여부를 판정합니다. 원문·스크린샷은 올리지 마세요.
2. **watchdog 현장 확인(0.3.9):** 실행 중인 PowerSI가 있을 때 `watchdog on`을 한 번 보내고 로그에서 확인합니다. `WATCHDOG_COMMAND`의 `result`(`ARMED` 등)·`added`·`total`, 대기 중 `STATUS_WAIT_YIELDED`(`check_due=true`)와 `RECEIVE_RESULT status=WAIT_YIELDED`가 설정 주기(30/60분)마다 한 번씩 나오는지, `WATCHDOG_CHECK_RESULT`와 대상별 `WATCHDOG_TARGET_EVALUATED`의 `outcome`·`report_state`·`source`(`BUFFER`/`AUTO_COPY`가 아니면 판정하지 않음)·`consecutive_unjudged`, 알림이 있었다면 `WATCHDOG_NOTICE_SENT` 뒤의 새 `MASTER_NOTICE_SENT stage=READY`, `WATCHDOG_NOTICE_DEFERRED`가 있다면 그 `reason`과 횟수, 그리고 `STATUS_WATCHDOG_NOTICE_UNCERTAIN`이 한 건도 없는지. 시뮬레이션이 실제로 끝난 뒤 `outcome=FINISHED`가 나오는지가 Output 형식 가정의 첫 현장 확인이며, 끝난 대상이 `NO_MARKERS`나 `FINISH_PENDING`에 머물면 형식 가정을 다시 봅니다. `WATCHDOG_CHECK_FAILED`가 있으면 `exception_type`과 `failure_streak`만 기록합니다. 원문·스크린샷은 올리지 마세요.
3. **G1 후속:** 0.3.5 현장 로그(G2)에서 Ready 전 대기는 통과했습니다. 같은 상황이 다시 발생하면 로그의 `OPERATIONAL_TARGET_IDLE_WAIT` 행(reason·input_age_ms·held_keys·gui_*·0.3.6의 own_input)을 확인합니다. `INPUT_RECENT`가 반복되면 마우스 흔들림 방지 프로그램·원격 제어 도구·떨리는 마우스 등 지속 입력원이, `KEY_HELD`면 눌린 마우스 버튼/수정 키가, `FOREGROUND_BUSY`/`NO_FOREGROUND`면 앞 창 상태가 원인입니다. 원문·스크린샷은 올리지 마세요.
4. **현장 로그로만 결정할 수 있는 가설:** B2(Text 자식이 없는 메시지 행이 `RECEIVE_HISTORY_NOT_UNIQUE`로 세션 종료), B3(대기 중 시계 모호 행), B4(2048 노드 상한과 계속 자라는 대화). 각각 해당 로그 코드가 현장에서 관측되면 그때 최소 변경을 검토합니다.
5. **DPI:** D1 수정은 배율 100%에서 항등이라 회귀 위험이 없지만, 배율 125/150%에서의 실제 동작은 Slave `--self-test`의 `LiveTest`가 `PASS:`를 내는지와 `PowerSI 전체 수집` 결과 코드로 확인해야 합니다. 캡처(`PrintWindow`) 자체는 가상화된 크기이며 DPI 인식 선언은 하지 않았습니다.
6. **검사 공백:** E15(설치 검사의 설정 보존 단언은 실질 검증이 아님), 외부 창 실제 캡처 자체 검사 없음, 깨끗한 PC 설치 경로 미검증.
7. **문서화만 한 개선:** B6/B7, C3(120초 예산 분배), C5(결합 문자 경계), C7(32비트 메모리), E9(`useLegacyV2RuntimeActivationPolicy` 제거), E11(TokenStore 해시 소금), E12(`PW_RENDERFULLCONTENT`).

검토 결과에는 중요도(P1/P2/P3), 파일·행/함수, 재현 조건, 영향, 최소 수정안, 필요한 회귀 검사를 적습니다. 근거가 부족하면 가설로 표시합니다. 코드를 수정한다면 실제 진입점을 추적하고 기존 공용 함수를 먼저 재사용합니다.

## 7. 검증 명령과 배포

저장소 루트에서 Windows PowerShell과 .NET SDK 8 계열을 사용합니다(CI는 8.0.x). 앱 대상 런타임은 계속 .NET Framework 4.8입니다.

```powershell
git status --short --branch
git diff v0.3.3 -- src scripts installer .github/workflows/ci.yml
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-package.ps1
```

`build-package.ps1`은 두 프로젝트 restore/build, 실제 EXE `--self-test`, ZIP 생성을 실행합니다. 결과 폴더 이름은 소스 버전을 따르므로 0.3.9에서는 `dist/verification-v0.3.9/`입니다. Master `--self-test`는 0.3.4부터 공유 Link 파싱 검사(`TestMalformedParsing`, `PowerSiReport`)도 실행하며, 0.3.6부터 `ProcessIdentity.RunSelfTest`(서명 캐시 키 규칙)와 `ReceiveProbe`의 변화 표본 검사가, 0.3.7부터 `ProbeElementCache.RunSelfTest`의 자식 질의 단언(`PROBE_CACHE_CHILDREN_QUERY_EMPTY`, `PROBE_CACHE_FROM_CACHED_*`, Windows 전용)이, 0.3.8부터 `RoundTripTest`의 증거 유효기간 단언과 `StatusSession.DecideAfterAbort` 결정표·중단 해제 검사가, 0.3.9부터 `PowerSiCompletion.RunSelfTest`와 `WatchdogSelfTest`(명령 문법·주기 설정 파일·감시 상태·문구), `ReceiveProbe`·`RoundTripTest`·`StatusSession`·`SupervisedSendTest`·`ReadOnlyCommands`·`MasterHubForm`의 watchdog 단언이 추가되었습니다. Slave `--self-test`는 `TestCollectFailureIsolation`과 `PowerSiVision.SelfTest`가 추가되었습니다.

현장 로그를 읽을 때는 기존 항목에 더해 0.3.6·0.3.7이 추가한 필드를 함께 봅니다: `READ_ONLY_PROBE_RESULT`의 `guard_ms`/`cache_ms`/`name_ms`/`content_ms`/`nav_ms`(항목별 provider 시간, 본문 없음)와 0.3.7의 `children_ms`/`children_queries`(부모당 자식 질의의 합계 시간과 횟수. 0.3.7부터 `nav_ms`는 루트 조회, `cache_ms`는 루트 배치만 집계합니다), `PROCESS_IDENTITY`의 `signature_cached`, `OPERATIONAL_TARGET_IDLE_WAIT`의 `own_input`, 폴링 대기마다 1행인 `RECEIVE_CHANGE_TRIGGER`(`reason`, `waited_ms`, `samples`, `chain_depth`). 노드 배치가 알 수 없는 이유로 실패하면 0.3.6부터 `skipped_subtrees`가 아니라 `read_failures`로 집계됩니다(하위 트리는 여전히 순회하지 않고 `complete=false`). 0.3.8부터는 중단된 회차마다 1행인 `STATUS_REQUEST_ABORTED`(`reason`, `input_attempted`, `consecutive`, `resumed`, `round_index`), 연속 한도에서 세션을 끝낸 `STATUS_REQUEST_ABORT_LIMIT`, 운용 창에 표시되는 `REQUEST_RESUMED` 단계도 함께 봅니다. 0.3.9부터는 `APP_START`의 `watchdog_available`, 세션 시작의 `WATCHDOG_INTERVAL`(`minutes`), 명령마다 `WATCHDOG_COMMAND`(`action`, `pid`, `result`, `added`, `already`, `cleared`, `remaining`, `total`)와 `WATCHDOG_COMMAND_READY`, 양보마다 `RECEIVE_RESULT`/`ROUNDTRIP_RESULT status=WAIT_YIELDED`와 `STATUS_WAIT_YIELDED`(`check_due`, `queued_parts`), 확인마다 `WATCHDOG_CHECK_BEGIN`, 대상별 `WATCHDOG_TARGET_EVALUATED`(`pid`, `start_utc_ticks`, `outcome`, `report_state`, `source`, `output_length`, `consecutive_unjudged`), `WATCHDOG_CHECK_RESULT`(`finished`, `missing`, `remaining`, `all_cleared`, `unjudged_streak`, `absent_means_missing`) 또는 `WATCHDOG_CHECK_FAILED`(`exception_type`, `failure_streak`), 알림의 `WATCHDOG_NOTICE_QUEUED`/`WATCHDOG_NOTICE_SENT`/`WATCHDOG_NOTICE_DEFERRED`(`kind`, `part`, `reason`), 불확실한 알림으로 끝난 세션의 `STATUS_SESSION_END reason=STATUS_WATCHDOG_NOTICE_UNCERTAIN`, 세션 종료 시 `WATCHDOG_CLEARED`(`reason=SESSION_END`, `entries`, `unsent_notice_parts`)도 봅니다. 이 기록에는 프로세스 이름과 Output 본문이 없습니다.

Windows가 없는 환경에서는 `Microsoft.NETFramework.ReferenceAssemblies`를 참조하는 별도 SDK 프로젝트로 컴파일할 수 있고, Mono에서 클래스별 `RunSelfTest`/`SelfTest`를 리플렉션으로 개별 호출하면 Windows API가 필요 없는 단위를 회귀 검사로 쓸 수 있습니다. 이는 실제 EXE `--self-test`를 대체하지 않습니다.

설치 관련 변경을 검증할 때만 아래를 실행합니다. 실제 설치/제거를 수행하므로 전용 검증 PC에서 사용합니다. `test-installers.ps1`은 0.3.4부터 설치파일 종료 코드 3010(재부팅 필요)을 실패가 아니라 "재부팅 후 다시 실행"으로 보고하고 업그레이드/제거 검사를 건너뜁니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-installers.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-installers.ps1 -Role Master
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-release-assets.ps1
```

정식 배포는 `.github/workflows/ci.yml`의 `workflow_dispatch release_tag`로 수행합니다. 0.3.9 배포는 아직 없는 `release_tag=v0.3.9`로 실행합니다. **v0.3.3, v0.3.4, v0.3.5, v0.3.6, v0.3.7, v0.3.8은 이미 존재하므로 다시 지정하지 않습니다.** 향후 코드 수정 배포는 새 앱 버전과 일치하는 미사용 `v<version>` 태그를 사용하고 기존 태그·자산을 교체하지 않습니다.

## 8. 공개 파일과 로컬 자료

| 위치 | 취급 |
|---|---|
| `src/`, `scripts/`, `installer/`, `.github/`, 루트 문서 | 공개 소스·검사·빌드·운영 안내 |
| `docs/REVIEW-*.md` | 날짜가 있는 검토 기록. 지적의 처리 상태를 포함 |
| `docs/history/` | 날짜가 있는 과거 공개 인계 기록. 현재 지시와 분리 |
| `work/` | 무시된 로컬 검증 자료·시험 코드·다운로드. 새 clone에는 없음 |
| `dist/`, `.cache/`, `bin/`, `obj/` | 무시된 빌드·설치 의존성·결과물 |
| 사용자 로그·스크린샷(`*.png` 등)·진단 ZIP·연결파일·설정 | 비공개. `.gitignore`가 이미지·압축 확장자도 제외합니다. 외부 검토에 첨부하거나 커밋하지 않음 |

Master Output 이력은 `%LOCALAPPDATA%\RemoteMonitorMaster\state\powersi-output-history-v1.txt`에 길이·SHA256만 저장합니다. 0.3.9부터 같은 폴더의 `master-settings-v1.txt`에 watchdog 확인 주기(`watchdog_interval_minutes=30` 또는 `60`)를 저장하며, 감시 목록과 알림은 저장하지 않습니다. Slave 로컬 설정/인증정보와 `.rmpair` 연결파일을 보존합니다. 사용자의 Downloads나 기존 PowerSI/HFSS 시험 자료를 저장소 정리 명목으로 삭제하거나 복사하지 않습니다.

## 9. 정식 설치파일

- **v0.3.9: 배포 후 기록.** 태그 `v0.3.9`의 CI 배포 실행이 설치파일 2개·ZIP 2개·`SHA256SUMS.txt`를 발행하면, 0.3.8과 같이 익명 다운로드로 SHA256과 GitHub digest 일치, ZIP 항목 허용 목록, 두 ZIP의 Slave 바이너리 동일, 두 EXE의 버전 문자열(0.3.9/규약 0.3.0)을 확인한 뒤 아래 형식으로 행을 추가합니다.

현재 정식 배포 v0.3.8 (2026-09-22, `main` 병합 커밋 `620b38d`, [배포 실행 35670256709](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35670256709)):

- [Master Setup 0.3.8](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.8/Messenger-Remote-Control-Master-Setup-0.3.8.exe) — SHA256 `FEDEF463E250D6EC503EE5CA0B0B448F9D5C29BFE6972E6402AE95ED91616700`
- [Slave Setup 0.3.8](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.8/Messenger-Remote-Control-Slave-Setup-0.3.8.exe) — SHA256 `99526A5E240078C6BA791FBB0AFD5F7E3C727EE19C54EC8A957DC5571942707D`
- 선택: [전체 ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.8/Messenger-Remote-Control-v0.3.8-win7-win11-net48.zip) `2C61A161EAA00B8A3C76C6EC98141A0142F981E7B0F5507D6AC8ED8B9642C54B`, [Slave ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.8/Messenger-Remote-Control-Slave-v0.3.8-win11-net48.zip) `BDABA15A335A47CBDF1CFB263F3DD01F0429504E03E8D704E070CF847689D471`
- [정식 Release v0.3.8](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.8) · [SHA256SUMS.txt](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.8/SHA256SUMS.txt)

2026-09-22에 위 다섯 파일을 익명으로 내려받아 SHA256이 SHA256SUMS.txt·GitHub digest와 일치함, 두 ZIP의 항목이 허용 목록(실행 파일·`.exe.config`·공개 md)과 같음, 두 ZIP의 Slave 바이너리가 동일함, 두 EXE의 버전 문자열이 0.3.8/규약 0.3.0임을 확인했습니다. 이전 배포 v0.3.7 (2026-09-21, `main` 병합 커밋 `7e4d52a`, [배포 실행 35573343568](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35573343568)):

- [Master Setup 0.3.7](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.7/Messenger-Remote-Control-Master-Setup-0.3.7.exe) — SHA256 `635C96E0A6BC64BA65D59879920414C4312E607B6EF2C82524CF15051AE138F9`
- [Slave Setup 0.3.7](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.7/Messenger-Remote-Control-Slave-Setup-0.3.7.exe) — SHA256 `67056895B3EC519EE855AC8FB9ECEDCFD71DB8CBD841BB899AC3D68B16C89A92`
- 선택: [전체 ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.7/Messenger-Remote-Control-v0.3.7-win7-win11-net48.zip) `ED5F21C5D30CBCA3F204CC2BD823E50412412F0ABC173C5403806369F93155D6`, [Slave ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.7/Messenger-Remote-Control-Slave-v0.3.7-win11-net48.zip) `C611652289080D490F77D80E5EF35C7507CC6B656AD980E570BED6D3C7CF8570`
- [정식 Release v0.3.7](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.7) · [SHA256SUMS.txt](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.7/SHA256SUMS.txt)

2026-09-21에 위 다섯 파일을 익명으로 내려받아 SHA256이 SHA256SUMS.txt·GitHub digest와 일치함, 두 ZIP의 항목이 허용 목록(실행 파일·`.exe.config`·공개 md)과 같음, 두 ZIP의 Slave 바이너리가 동일함, 두 EXE의 버전 문자열이 0.3.7/규약 0.3.0임을 확인했습니다. 이전 배포 v0.3.6 (2026-09-21, `main` 병합 커밋 `e5c5389`, [배포 실행 35570250448](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35570250448)):

- [Master Setup 0.3.6](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.6/Messenger-Remote-Control-Master-Setup-0.3.6.exe) — SHA256 `919125E54F99F36654B03374D17FE7FFB2648EDD7FB75480299CCCAEFF612B59`
- [Slave Setup 0.3.6](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.6/Messenger-Remote-Control-Slave-Setup-0.3.6.exe) — SHA256 `5F371C5F99C5A0C1DDFFF9CAA337BA28A61E9D4CC82216FBB0980F2175008660`
- 선택: [전체 ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.6/Messenger-Remote-Control-v0.3.6-win7-win11-net48.zip) `3E5E9628AC2310C4CA579CA20E496F7E420F84DE11FE4BB039999B8ED0F82FB8`, [Slave ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.6/Messenger-Remote-Control-Slave-v0.3.6-win11-net48.zip) `62501F656AE21BAE6E9F441CC5232537B6089E7C81A7F45BDC39F8B0C685E73B`
- [정식 Release v0.3.6](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.6) · [SHA256SUMS.txt](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.6/SHA256SUMS.txt)

2026-09-21에 위 다섯 파일을 익명으로 내려받아 SHA256이 SHA256SUMS.txt·GitHub digest와 일치함, 두 ZIP의 항목이 허용 목록(실행 파일·`.exe.config`·공개 md)과 같음, 두 ZIP의 Slave 바이너리가 동일함, 두 EXE의 버전 문자열이 0.3.6/규약 0.3.0임을 확인했습니다. 이전 배포 v0.3.5:

- [Master Setup 0.3.5](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.5/Messenger-Remote-Control-Master-Setup-0.3.5.exe) — SHA256 `53BB214C705ACFBCB1D030FE4B6EE8BF019903F94BADB365541896B3AA7D506B`
- [Slave Setup 0.3.5](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.5/Messenger-Remote-Control-Slave-Setup-0.3.5.exe) — SHA256 `6C545A57FDF8464A3142CBD43C7EBCA5A185685F044999C677E288E14CC5A834`
- 선택: [전체 ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.5/Messenger-Remote-Control-v0.3.5-win7-win11-net48.zip) `653C66AD34DCBFFFD4785E9203EA656ED3D74B27346C8B114291E696DE63EA7A`, [Slave ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.5/Messenger-Remote-Control-Slave-v0.3.5-win11-net48.zip) `514442533382D614DBADBB6C3D964652C3A8020BC684D593C485C97EBCD41FAF`
- [정식 Release v0.3.5](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.5) · [SHA256SUMS.txt](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.5/SHA256SUMS.txt)

2026-09-21에 위 다섯 파일을 익명으로 내려받아 해시를 재확인했습니다. 이전 배포 v0.3.4:

- [Master Setup 0.3.4](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.4/Messenger-Remote-Control-Master-Setup-0.3.4.exe) — SHA256 `496A8F90A31D8B7C99266CA8E9E0BAA559265807216743B68A7618BAE55157AB`
- [Slave Setup 0.3.4](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.4/Messenger-Remote-Control-Slave-Setup-0.3.4.exe) — SHA256 `A56AFF65EDB3FA9F6D2EAED5C9316B1291ABC51403A7250D64A6A80E1A170F56`
- 선택: [전체 ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.4/Messenger-Remote-Control-v0.3.4-win7-win11-net48.zip) `EEBD17812A260E3915529BD242DFE5526E5D56569BC091196E20F935B6DE507E`, [Slave ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.4/Messenger-Remote-Control-Slave-v0.3.4-win11-net48.zip) `4A6969C85EE1FB79699E3A8D4E72CEDF3E4586C48AA7226A52EE72FE90ED51EB`
- [정식 Release v0.3.4](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.4) · [SHA256SUMS.txt](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.4/SHA256SUMS.txt)

2026-09-21에 위 다섯 파일을 익명으로 내려받아 해시를 재확인했습니다. 이전 배포 v0.3.3:

- [Master Setup 0.3.3](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.3/Messenger-Remote-Control-Master-Setup-0.3.3.exe) — SHA256 `37A6B14333D86D63A26C609B5A8F0AA214082EF5F30E8CB2B860AF97222278CF`
- [Slave Setup 0.3.3](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.3/Messenger-Remote-Control-Slave-Setup-0.3.3.exe) — SHA256 `AE8C1577CE7A0326250ECC9B11A002410FAB769A7872660A2E60F48B434AEB45`
- [정식 Release](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.3) · [SHA256SUMS.txt](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.3/SHA256SUMS.txt)
