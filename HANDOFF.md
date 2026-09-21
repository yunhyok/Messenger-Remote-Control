# Messenger Remote Control — 점검 인계

갱신: 2026-09-21. Claude를 포함한 다음 검토자가 현재 구현과 검증 범위를 파악하기 위한 진입 문서입니다. 과거 계획보다 이 문서와 실제 소스, 사용자의 최신 지시를 우선합니다.

## 1. 현재 상태와 읽는 순서

| 항목 | 기준 |
|---|---|
| 저장소 | `yunhyok/Messenger-Remote-Control`, Public, 기본 브랜치 `main` |
| 앱·설치파일 | **v0.3.4**, Master와 Slave 모두 동일 버전. 2026-09-21 독립 검토의 수정분 |
| 정식 배포 소스·태그 | `v0.3.4` → `932be45f293910ba4244c3c00a58e44343bdd704` (PR #1 병합 커밋). 설치파일은 §9 |
| 이전 정식 배포 | v0.3.3 → `e31a67c68ca33986016be00cf4990d8d81f9d8ec` |
| 통신 규약 | **0.3.0** — 앱 버전과 별개, 0.3.4에서도 변경 없음 |
| 호환성 | Master 0.3.4 + Slave 0.3.0/0.3.1/0.3.2/0.3.3/0.3.4. 0.2.x에서는 두 역할 모두 갱신 |
| 실행 환경 | Windows 7 SP1 Master / Windows 11 Slave, .NET Framework 4.8 |
| 이번 인계 범위 | v0.3.3 독립 소스 검토 결과의 반영과 v0.3.4 정식 배포. 검토 기록은 [docs/REVIEW-2026-09-21-v0.3.3.md](docs/REVIEW-2026-09-21-v0.3.3.md) |

먼저 [AGENTS.md](AGENTS.md)의 프로젝트 제약을 읽고 이 문서의 소스 지도와 점검 항목을 따라갑니다. 사용법은 [README.md](README.md), 설치는 [INSTALL.md](INSTALL.md), 최소 현장 확인은 [WIN7-TEST.md](WIN7-TEST.md)와 [SLAVE-TEST.md](SLAVE-TEST.md)에 있습니다.

[과거 인계 기록](docs/history/HANDOFF-through-v0.3.3.md)은 별도 보존했습니다. 그 안의 5줄/600자 발췌, pre-release, 창 복원 금지 등은 당시 정책이며 현재 요구가 아닙니다. 기존 `Remote-Control-App` 저장소와 HFSS 작업은 별개입니다. 이 저장소의 출발점은 `Remote-Control-App@1365e2c249d00b2a73634c77cd7bc82247f82405`(PowerSI v0.1.58)입니다.

## 2. 실제 운영 흐름

1. Master 운용 Start 후 5초 안에 KI-Messenger의 나와의 대화창을 한 번 선택합니다. 화면에 남은 초가 표시되고, 선택 직후 선택한 창의 프로세스 이름·PID가 참고로 표시됩니다(판정은 작업자 스레드의 `PROBE_NOT_KI_MESSENGER`가 담당). 해당 세션의 HWND, 프로세스 시작 시각, UIA root, 창 위치·크기를 고정합니다.
2. 대화창을 확인한 뒤 `Master Ready. help, total status, pwrsi 중 하나를 보내세요. [회차 번호]`를 보냅니다. 선두 토큰 `Master Ready`는 유지됩니다. Ready 이후 **새 메시지의 전체 본문**이 허용 명령과 일치해야 접수합니다. 첫 명령 행이 화면 밖이거나 비활성이면 그 행에 고정된 채 접수하지 않으며, 화면에는 `COMMAND_NOT_VISIBLE` 안내(스크롤로 보이게 하라는 문구)가 표시됩니다. 다른 행이 대신 접수되지는 않습니다.
3. `pwrsi`/`total status`는 처리 안내(`Processing pwrsi. …`)를 한 번 보낸 뒤 Slave에 요청합니다. 처리 중 다른 메시지는 큐에 넣지 않고 무시합니다. `help`는 세 줄(머리글·본문·사용법)로 회신하며 느린 조회 안내가 없습니다.
4. Slave는 PowerSI별 수집 자료·출처·시각·상태를 반환합니다. Master가 전체/추가 Output을 판별하고 최대 1,400자씩 고정된 답장을 준비합니다.
5. 각 부분 전송 전에 같은 대화와 접수 명령을 다시 확인합니다. 모든 부분의 보호된 UI 전송이 완료되어야 Output 이력을 저장하고 다음 Ready로 돌아갑니다.

허용 명령: `help`, `help help`, `help total status`, `help pwrsi`, `total status`, `pwrsi`. 임의 명령 실행, 자동 감시, PowerSI 실행·종료는 제공하지 않습니다.

일반 창 뒤에서도 수신 검사를 계속합니다. 최소화된 선택 창은 읽기 전에 복원합니다. Ready·처리 안내·각 답장 입력 전에 PC 입력이 1초 이상 멈추고 누른 키·메뉴·끌기가 없는지 확인한 다음 선택 창만 앞으로 가져옵니다. 마우스를 계속 올려둘 필요는 없습니다. 다른 HWND/프로세스를 찾아 대체하지 않습니다.

Windows 활성화 거부, 대상 변경, 실제 입력 중 간섭 또는 불확실한 전송은 사유를 남기고 중단합니다. 0.3.4부터 운용 창은 중단 사유 코드 옆에 한국어 설명과 다음 행동을 함께 표시하고, 새 세션은 창을 닫고 허브 버튼을 다시 누르면 시작한다는 안내를 붙입니다. 잠금·사용자 세션 전환·절전 후 자동 재개하지 않습니다. 상시 idle 종료 제한은 없지만, 모든 환경 변화에서 세션이 유지된다는 보장은 아닙니다.

## 3. 소스 지도

모든 경로는 저장소 루트 기준입니다. 별도의 solution/test 프로젝트 없이 두 `.csproj`와 실제 EXE의 `--self-test`를 사용합니다. `RemoteMonitorLink/*.cs`는 양쪽 프로젝트에 공유 소스로 포함됩니다.

| 검토 대상 | 진입점·주요 파일 |
|---|---|
| Master 시작·화면 | `src/RemoteMonitorMaster/Program.cs` → `MasterHubForm.cs` → `ReceiveForm.cs` (`Explain`: 사유 코드 → 한국어 안내) |
| 운영 세션·Ready·다음 요청 | `src/RemoteMonitorMaster/StatusSession.cs` |
| 처음 선택한 창 기억·복원·활성화 | `src/RemoteMonitorMaster/OperationalTarget.cs` |
| 명령 접수·Ready 경계·재확인 | `src/RemoteMonitorMaster/ReceiveProbe.cs`, `ReceiveMetadata.cs` |
| 최신 UIA 읽기·캐시 | `src/RemoteMonitorMaster/ReadOnlyProbe.cs`, `ProbeElementCache.cs` |
| 요청부터 분할 발송·최종 이력 저장 | `src/RemoteMonitorMaster/RoundTripTest.cs`, `SupervisedSendTest.cs` |
| 실제 입력·클릭·대상 확인 | `src/RemoteMonitorMaster/SupervisedSendTest.cs`, `MouseClickInput.cs`, `UiaPointProbe.cs` |
| 명령 처리·보고서 구성 | `src/RemoteMonitorMaster/ReadOnlyCommands.cs` (`FormatPowerSi`) |
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
- **최신 증거:** 각 답장에 서로 다른 최신 관찰 두 개, 첫 관찰 시각, 정확한 본문에 묶인 일회성 전송을 유지합니다. 오래된 접수 증거는 식별 기준일 뿐 현재 화면의 증거가 아닙니다. 불명확한 전송을 재시도하거나 남은 초안을 자동으로 지우지 않습니다.
- **이력 저장:** 모든 부분이 성공한 뒤에만 길이·SHA256을 저장합니다. 부분 전송 실패 다음 조회에서 앞부분이 반복될 수 있지만 미송신 본문을 누락하면 안 됩니다. UI 전송/입력창 비워짐 확인은 인증된 모바일 수신 확인이 아닙니다.
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
| **v0.3.4 검토 반영 (Slave·Link)** | D1 자동 복사 논리→물리 좌표 변환과 LiveTest 강화, D2 Master 요청이 로컬 새로고침을 취소, D3 `total status` 두 번째 스냅샷 1회(자체 검사에 5초 상한), D4 수집 콜백/응답 실패를 연결 단위로 격리(`COLLECT_FAILED`/`RESPONSE_FAILED`, UI 문구 구분, loopback 회귀 검사), D5/F12 연결파일 저장 경고, D6~D10, D11, E2 캡처 워커 부모 한도 8/6/4초, E10 stderr 드레인, E13 설정 검증 매핑(+순수 자체 검사), E14 서로게이트 보호, F9~F11·F13~F15·F23 Slave UI, E4/E5 스크립트, E6 `.gitignore`, D13/E8 매니페스트 0.3.4.0 |

**v0.3.4 검증 수준(2026-09-21):**

- Linux에서 net48 참조 어셈블리로 두 프로젝트 컴파일: 경고 0, 오류 0. Mono로 클래스별 자체 검사를 개별 실행해 Windows API가 필요 없는 단위(Master 14개, Slave/Link 8개)가 모두 통과했고, 변경 전과 통과 목록이 동일합니다(신규 검사 추가분 제외).
- [CI run 35558060129](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35558060129)(Windows Server 2025, 커밋 `fc28435`)에서 두 Release 빌드, 두 실제 EXE `--self-test`(새 Windows 전용 검사 포함), 두 설치파일 생성·자산 검사, 두 역할의 설치/동일 버전 재설치/제거/설정 보존 검사가 통과했습니다. 앞선 두 실행은 `test-installers.ps1`의 `$version:` 파싱 오류로 설치 검사 단계에서 실패했고 같은 PR에서 수정했습니다.
- 고대비(High Contrast) 모드가 켜진 Windows에서 `--self-test`를 실행하면 `ReceiveForm`의 색상 단언(`DarkGreen`/`LightYellow`)과 F25의 시스템 색 대체가 충돌할 수 있습니다. 일반 테마(CI 포함)에서는 영향이 없습니다.

**v0.3.4 정식 배포(2026-09-21):** `main` 병합 커밋 `932be45`에서 [CI run 35561827064](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35561827064)(`workflow_dispatch release_tag=v0.3.4`)가 두 Release 빌드·두 실제 EXE `--self-test`·설치파일 생성·자산 검사·두 역할 설치/재설치/제거/설정 보존 검사를 통과한 뒤 태그와 [정식 Release](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.4)를 발행했습니다. 공개 자산 5개를 익명으로 내려받아 SHA256이 SHA256SUMS.txt 및 GitHub digest와 일치함, ZIP 항목이 허용 목록과 같음, 두 ZIP의 Slave 바이너리가 동일함을 확인했습니다(§9). CI 통과는 실제 Win7/Win11 설치·운용 확인이 아닙니다.

**미확인:** 실제 Win7 KI-Messenger에서 0.3.4의 한국어 Ready 안내·카운트다운·`COMMAND_NOT_VISIBLE`·Ready 대기 한도의 동작, Win11 Slave의 실제 디스플레이 배율에서 자동 복사(D1)와 캡처, .NET 4.8이 없는 깨끗한 오프라인 PC의 설치 경로. 2026-09-21 검토에는 새로운 현장 성공/실패 결과가 포함되지 않았습니다.

## 6. 다음 점검 우선순위

검토에서 코드 변경 없이 남긴 항목입니다(ID는 [검토 기록](docs/REVIEW-2026-09-21-v0.3.3.md) 기준).

1. **현장 로그로만 결정할 수 있는 가설:** B2(Text 자식이 없는 메시지 행이 `RECEIVE_HISTORY_NOT_UNIQUE`로 세션 종료), B3(대기 중 시계 모호 행), B4(2048 노드 상한과 계속 자라는 대화), A12(부분마다 반복되는 Authenticode 신원 확인 비용). 각각 해당 로그 코드가 현장에서 관측되면 그때 최소 변경을 검토합니다.
2. **DPI:** D1 수정은 배율 100%에서 항등이라 회귀 위험이 없지만, 배율 125/150%에서의 실제 동작은 Slave `--self-test`의 `LiveTest`가 `PASS:`를 내는지와 `PowerSI 전체 수집` 결과 코드로 확인해야 합니다. 캡처(`PrintWindow`) 자체는 가상화된 크기이며 DPI 인식 선언은 하지 않았습니다.
3. **검사 공백:** E15(설치 검사의 설정 보존 단언은 실질 검증이 아님), 외부 창 실제 캡처 자체 검사 없음, 깨끗한 PC 설치 경로 미검증.
4. **문서화만 한 개선:** B5 잔여 항목(노드당 UIA 왕복), B6/B7, C3(120초 예산 분배), C5(결합 문자 경계), C7(32비트 메모리), E9(`useLegacyV2RuntimeActivationPolicy` 제거), E11(TokenStore 해시 소금), E12(`PW_RENDERFULLCONTENT`).

검토 결과에는 중요도(P1/P2/P3), 파일·행/함수, 재현 조건, 영향, 최소 수정안, 필요한 회귀 검사를 적습니다. 근거가 부족하면 가설로 표시합니다. 코드를 수정한다면 실제 진입점을 추적하고 기존 공용 함수를 먼저 재사용합니다.

## 7. 검증 명령과 배포

저장소 루트에서 Windows PowerShell과 .NET SDK 8 계열을 사용합니다(CI는 8.0.x). 앱 대상 런타임은 계속 .NET Framework 4.8입니다.

```powershell
git status --short --branch
git diff v0.3.3 -- src scripts installer .github/workflows/ci.yml
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-package.ps1
```

`build-package.ps1`은 두 프로젝트 restore/build, 실제 EXE `--self-test`, ZIP 생성을 실행합니다. 결과는 `dist/verification-v0.3.4/`입니다. Master `--self-test`는 0.3.4부터 공유 Link 파싱 검사(`TestMalformedParsing`, `PowerSiReport`)도 실행하며, Slave `--self-test`는 `TestCollectFailureIsolation`과 `PowerSiVision.SelfTest`가 추가되었습니다.

Windows가 없는 환경에서는 `Microsoft.NETFramework.ReferenceAssemblies`를 참조하는 별도 SDK 프로젝트로 컴파일할 수 있고, Mono에서 클래스별 `RunSelfTest`/`SelfTest`를 리플렉션으로 개별 호출하면 Windows API가 필요 없는 단위를 회귀 검사로 쓸 수 있습니다. 이는 실제 EXE `--self-test`를 대체하지 않습니다.

설치 관련 변경을 검증할 때만 아래를 실행합니다. 실제 설치/제거를 수행하므로 전용 검증 PC에서 사용합니다. `test-installers.ps1`은 0.3.4부터 설치파일 종료 코드 3010(재부팅 필요)을 실패가 아니라 "재부팅 후 다시 실행"으로 보고하고 업그레이드/제거 검사를 건너뜁니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-installers.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-installers.ps1 -Role Master
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-release-assets.ps1
```

정식 배포는 `.github/workflows/ci.yml`의 `workflow_dispatch release_tag`로 수행합니다. **v0.3.3과 v0.3.4는 이미 존재하므로 다시 지정하지 않습니다.** 향후 코드 수정 배포는 새 앱 버전과 일치하는 미사용 `v<version>` 태그를 사용하고 기존 태그·자산을 교체하지 않습니다.

## 8. 공개 파일과 로컬 자료

| 위치 | 취급 |
|---|---|
| `src/`, `scripts/`, `installer/`, `.github/`, 루트 문서 | 공개 소스·검사·빌드·운영 안내 |
| `docs/REVIEW-*.md` | 날짜가 있는 검토 기록. 지적의 처리 상태를 포함 |
| `docs/history/` | 날짜가 있는 과거 공개 인계 기록. 현재 지시와 분리 |
| `work/` | 무시된 로컬 검증 자료·시험 코드·다운로드. 새 clone에는 없음 |
| `dist/`, `.cache/`, `bin/`, `obj/` | 무시된 빌드·설치 의존성·결과물 |
| 사용자 로그·스크린샷(`*.png` 등)·진단 ZIP·연결파일·설정 | 비공개. `.gitignore`가 이미지·압축 확장자도 제외합니다. 외부 검토에 첨부하거나 커밋하지 않음 |

Master Output 이력은 `%LOCALAPPDATA%\RemoteMonitorMaster\state\powersi-output-history-v1.txt`에 길이·SHA256만 저장합니다. Slave 로컬 설정/인증정보와 `.rmpair` 연결파일을 보존합니다. 사용자의 Downloads나 기존 PowerSI/HFSS 시험 자료를 저장소 정리 명목으로 삭제하거나 복사하지 않습니다.

## 9. 정식 설치파일

- [Master Setup 0.3.4](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.4/Messenger-Remote-Control-Master-Setup-0.3.4.exe) — SHA256 `496A8F90A31D8B7C99266CA8E9E0BAA559265807216743B68A7618BAE55157AB`
- [Slave Setup 0.3.4](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.4/Messenger-Remote-Control-Slave-Setup-0.3.4.exe) — SHA256 `A56AFF65EDB3FA9F6D2EAED5C9316B1291ABC51403A7250D64A6A80E1A170F56`
- 선택: [전체 ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.4/Messenger-Remote-Control-v0.3.4-win7-win11-net48.zip) `EEBD17812A260E3915529BD242DFE5526E5D56569BC091196E20F935B6DE507E`, [Slave ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.4/Messenger-Remote-Control-Slave-v0.3.4-win11-net48.zip) `4A6969C85EE1FB79699E3A8D4E72CEDF3E4586C48AA7226A52EE72FE90ED51EB`
- [정식 Release v0.3.4](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.4) · [SHA256SUMS.txt](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.4/SHA256SUMS.txt)

2026-09-21에 위 다섯 파일을 익명으로 내려받아 해시를 재확인했습니다. 이전 배포 v0.3.3:

- [Master Setup 0.3.3](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.3/Messenger-Remote-Control-Master-Setup-0.3.3.exe) — SHA256 `37A6B14333D86D63A26C609B5A8F0AA214082EF5F30E8CB2B860AF97222278CF`
- [Slave Setup 0.3.3](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.3/Messenger-Remote-Control-Slave-Setup-0.3.3.exe) — SHA256 `AE8C1577CE7A0326250ECC9B11A002410FAB769A7872660A2E60F48B434AEB45`
- [정식 Release](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.3) · [SHA256SUMS.txt](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.3/SHA256SUMS.txt)
