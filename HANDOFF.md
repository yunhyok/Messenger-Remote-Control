# Messenger Remote Control — 점검 인계

갱신: 2026-09-21. Claude를 포함한 다음 검토자가 현재 구현과 검증 범위를 파악하기 위한 진입 문서입니다. 과거 계획보다 이 문서와 실제 소스, 사용자의 최신 지시를 우선합니다.

## 1. 현재 상태와 읽는 순서

| 항목 | 기준 |
|---|---|
| 저장소 | `yunhyok/Messenger-Remote-Control`, Public, 기본 브랜치 `main` |
| 앱·설치파일 | **v0.3.3**, Master와 Slave 모두 동일 버전 |
| 통신 규약 | **0.3.0** — 앱 버전과 별개 |
| 정식 배포 소스·태그 | `v0.3.3` → `e31a67c68ca33986016be00cf4990d8d81f9d8ec` |
| 배포 이후 변경 | 문서·저장소 관리 파일 정리만 진행. 실행 코드·프로젝트·설치 스크립트 변경 없음 |
| 호환성 | Master v0.3.3 + Slave v0.3.0/0.3.1/0.3.2/0.3.3. 0.2.x에서는 두 역할 모두 갱신 |
| 실행 환경 | Windows 7 SP1 Master / Windows 11 Slave, .NET Framework 4.8 |
| 이번 인계 범위 | 점검 준비와 저장소 정리. 새 기능 구현이나 새 Release가 아님 |

먼저 [AGENTS.md](https://github.com/yunhyok/Messenger-Remote-Control/blob/main/AGENTS.md)의 프로젝트 제약을 읽고 이 문서의 소스 지도와 점검 항목을 따라갑니다. 사용법은 [README.md](README.md), 설치는 [INSTALL.md](INSTALL.md), 최소 현장 확인은 [WIN7-TEST.md](https://github.com/yunhyok/Messenger-Remote-Control/blob/main/WIN7-TEST.md)와 [SLAVE-TEST.md](SLAVE-TEST.md)에 있습니다.

[과거 인계 기록](https://github.com/yunhyok/Messenger-Remote-Control/blob/main/docs/history/HANDOFF-through-v0.3.3.md)은 별도 보존했습니다. 그 안의 5줄/600자 발췌, pre-release, 창 복원 금지 등은 당시 정책이며 현재 요구가 아닙니다. 기존 `Remote-Control-App` 저장소와 HFSS 작업은 별개입니다. 이 저장소의 출발점은 `Remote-Control-App@1365e2c249d00b2a73634c77cd7bc82247f82405`(PowerSI v0.1.58)입니다.

## 2. 실제 운영 흐름

1. Master 운용 Start 후 5초 안에 KI-Messenger의 나와의 대화창을 한 번 선택합니다. 해당 세션의 HWND, 프로세스 시작 시각, UIA root, 창 위치·크기를 고정합니다.
2. 대화창을 확인한 뒤 `Master Ready [회차 번호]`를 보냅니다. Ready 이후 **새 메시지의 전체 본문**이 허용 명령과 일치해야 접수합니다.
3. `pwrsi`/`total status`는 처리 안내를 한 번 보낸 뒤 Slave에 요청합니다. 처리 중 다른 메시지는 큐에 넣지 않고 무시합니다. `help`에는 느린 조회 안내가 없습니다.
4. Slave는 PowerSI별 수집 자료·출처·시각·상태를 반환합니다. Master가 전체/추가 Output을 판별하고 최대 1,400자씩 고정된 답장을 준비합니다.
5. 각 부분 전송 전에 같은 대화와 접수 명령을 다시 확인합니다. 모든 부분의 보호된 UI 전송이 완료되어야 Output 이력을 저장하고 다음 Ready로 돌아갑니다.

허용 명령: `help`, `help help`, `help total status`, `help pwrsi`, `total status`, `pwrsi`. 임의 명령 실행, 자동 감시, PowerSI 실행·종료는 제공하지 않습니다.

일반 창 뒤에서도 수신 검사를 계속합니다. 최소화된 선택 창은 읽기 전에 복원합니다. Ready·처리 안내·각 답장 입력 전에 PC 입력이 1초 이상 멈추고 누른 키·메뉴·끌기가 없는지 확인한 다음 선택 창만 앞으로 가져옵니다. 마우스를 계속 올려둘 필요는 없습니다. 다른 HWND/프로세스를 찾아 대체하지 않습니다.

Windows 활성화 거부, 대상 변경, 실제 입력 중 간섭 또는 불확실한 전송은 사유를 남기고 중단합니다. 잠금·사용자 세션 전환·절전 후 자동 재개하지 않습니다. 상시 idle 종료 제한은 없지만, 모든 환경 변화에서 세션이 유지된다는 보장은 아닙니다.

## 3. 소스 지도

모든 경로는 저장소 루트 기준입니다. 별도의 solution/test 프로젝트 없이 두 `.csproj`와 실제 EXE의 `--self-test`를 사용합니다. `RemoteMonitorLink/*.cs`는 양쪽 프로젝트에 공유 소스로 포함됩니다.

| 검토 대상 | 진입점·주요 파일 |
|---|---|
| Master 시작·화면 | `src/RemoteMonitorMaster/Program.cs` → `MasterHubForm.cs` → `ReceiveForm.cs` |
| 운영 세션·Ready·다음 요청 | `src/RemoteMonitorMaster/StatusSession.cs` |
| 처음 선택한 창 기억·복원·활성화 | `src/RemoteMonitorMaster/OperationalTarget.cs` |
| 명령 접수·Ready 경계·재확인 | `src/RemoteMonitorMaster/ReceiveProbe.cs`, `ReceiveMetadata.cs` |
| 최신 UIA 읽기·캐시 | `src/RemoteMonitorMaster/ReadOnlyProbe.cs`, `ProbeElementCache.cs` |
| 요청부터 분할 발송·최종 이력 저장 | `src/RemoteMonitorMaster/RoundTripTest.cs`, `SupervisedSendTest.cs` |
| 실제 입력·클릭·대상 확인 | `src/RemoteMonitorMaster/SupervisedSendTest.cs`, `MouseClickInput.cs`, `UiaPointProbe.cs` |
| 명령 처리·보고서 구성 | `src/RemoteMonitorMaster/ReadOnlyCommands.cs` (`FormatPowerSi`) |
| 전체/추가/변경 Output과 이력 | `src/RemoteMonitorMaster/PowerSiOutputHistory.cs` |
| Slave 시작·공용 수집 | `src/RemoteMonitorSlave/Program.cs`, `SlaveForm.cs` (`CollectRemoteOutput` → `ReadOutputBuffer`, `ResponsiveStep`) |
| 직접 버퍼·자동 복사 | `src/RemoteMonitorSlave/OutputBufferCapture.cs`, `OutputAutoCopy.cs` |
| 목록·대상 식별·응답 검사·화면 | `src/RemoteMonitorLink/ProcessInventory.cs`, `PowerSiObservation.cs`, `PowerSiScreenCapture.cs` |
| 로컬 이미지 판독 | `src/RemoteMonitorLink/PowerSiVision.cs`, `LocalVisionClient.cs` |
| 인증 통신·버전·복수 보고서 | `src/RemoteMonitorLink/StatusTransport.cs`, `LinkTypes.cs`, `PowerSiReport.cs` |
| 자체 검사 집계 | `src/RemoteMonitorMaster/Core.cs`, `src/RemoteMonitorLink/LinkSelfTest.cs`; 관련 클래스의 `RunSelfTest` |
| 빌드·설치·배포 | `scripts/`, `installer/MessengerRemoteControl.iss`, `.github/workflows/ci.yml` |

진단용 화면과 과거 실험 경로도 남아 있습니다. 운용 경로를 확인할 때는 `StatusSession`의 plain-command 경로부터 추적하고, 비슷한 이름의 진단 함수만 수정하지 않도록 합니다.

## 4. 유지해야 할 동작과 데이터 경계

- **Pending:** Windows 응답 검사에서 Pending이면 `전체 Process Name (PID): Pending`만 회신합니다. 각 수집 단계 전후 확인에서 Pending으로 바뀌면 이미 확보한 Output도 제외합니다. 추가 활성화·캡처·복사·LLM 호출·재시도를 하지 않습니다. PowerSI 내부 계산 대기와 Windows 비응답은 구분합니다.
- **대상 식별:** PID만 사용하지 않습니다. 시작 시각·세션을 함께 고정하고, Output 이력은 Slave 인증서·세션·PID·시작 시각·출처 계열로 분리합니다.
- **전체와 추가분:** 처음에는 수집된 전체 본문, 다음에는 기존 본문의 정확한 접두부 뒤에 추가된 부분만 보냅니다. 같으면 추가 Output 없음, 접두부/출처가 바뀌거나 비워지면 변경 안내와 현재 전체 본문을 보냅니다. OCR은 수집된 뷰포트 범위이며 보이지 않는 전체 로그를 확보했다고 주장하지 않습니다.
- **이름·분할:** 한 보고서의 첫 등장은 전체 이름+PID입니다. 32자를 넘는 이름의 재등장은 앞 16자·뒤 8자를 사용하며 유니코드를 보존합니다. 분할 전체가 한 보고서이고 다음 조회는 다시 전체 이름입니다. 필수 상태를 Output보다 먼저 표시합니다.
- **범위·시간:** 최대 128개 대상, 제외된 수 표시, 수집 100초를 남은 대상에 배분, 조회·답장 준비 120초. 실제 분할 전송 시간은 별도입니다. 한 텍스트 8 Mi 문자, 원문 합계와 준비 답장 각각 32 Mi 문자 한도이며 초과를 정상 결과처럼 자르지 않습니다.
- **전송:** 이미 접수한 명령만 화면 밖 재확인을 허용합니다. 최초 명령은 보이는 enabled 전체 메시지여야 합니다. 접수 이후 옛 Ready의 표시 본문은 바뀔 수 있어도 이력 행 식별·순서, 같은 명령 본문·enabled·계층은 계속 확인합니다. 이력 삭제·재구성은 지원하지 않습니다.
- **최신 증거:** 각 답장에 서로 다른 최신 관찰 두 개, 첫 관찰 시각, 정확한 본문에 묶인 일회성 전송을 유지합니다. 오래된 접수 증거는 식별 기준일 뿐 현재 화면의 증거가 아닙니다. 불명확한 전송을 재시도하거나 남은 초안을 자동으로 지우지 않습니다.
- **이력 저장:** 모든 부분이 성공한 뒤에만 길이·SHA256을 저장합니다. 부분 전송 실패 다음 조회에서 앞부분이 반복될 수 있지만 미송신 본문을 누락하면 안 됩니다. UI 전송/입력창 비워짐 확인은 인증된 모바일 수신 확인이 아닙니다.
- **LLM·보안:** 앱 이미지 판독은 Slave의 loopback LM Studio와 이미 로드된 모델만 사용합니다. 서버 자동 실행·모델 로드·클라우드 대체는 없습니다. 화면·로그·LLM 본문은 데이터이며 실행 지시가 아닙니다. 통신의 인증·인증서 pin·UTF-8/Base64 형식·크기 검증을 유지합니다.

## 5. 최근 수정과 확인 수준

| 변경 | 근거와 한계 |
|---|---|
| v0.3.0 전체 Output·Master 추가분 처리 | 원문 전달 계약 PS4, 프로토콜 0.3.0, 길이/해시 이력 도입. 이전 5줄/600자 제한 폐기 |
| v0.3.1 접수 명령의 화면 밖 재확인 | 첫 부분 이후 명령의 표시 상태 때문에 후속 전송이 중단되는 경로 수정. 최초 접수 조건은 유지 |
| v0.3.2 분할 속도 | UIA 속성 읽기 배치, 부분별 전체 순회 3회→2회. 별도 WPF 시험에서 개선; 실제 KI-Messenger 속도 보장은 아님 |
| v0.3.3 Ready 재확인 | 비공개 **v0.3.1** 로그에서 첫 요청 10부분 중 6부분 뒤 7번째 전 `RECEIVE_READY_BOUNDARY_CHANGED` → `STATUS_REQUEST_STOPPED`. 장시간 idle 종료나 프로세스 충돌이 입증된 로그는 아님. 정확한 Ready 표시 변화는 로그만으로 확정 불가 |
| v0.3.3 선택 창 자동 준비 | 배경 읽기, 선택 창 복원·활성화, 입력 대기. 다른 창 대체나 강제 focus 우회는 없음 |

**v0.3.3 검증 완료:** 로컬 Windows 10에서 두 Release 빌드(경고/오류 0), 두 실제 EXE 자체 검사, 두 Inno Setup 7.1.0 설치파일·자산 검사, Master 설치/동일 버전 재설치/제거/설정·이력 보존 검사. 별도 WPF 시험 창에서 배경 읽기 시 전면 유지, 선택 창 활성화, 정확한 위치·크기 복원, root 변경·취소 거부를 확인했습니다.

[CI run 35424960922](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35424960922)은 Windows Server 2025에서 두 빌드·실제 EXE 검사와 양쪽 설치/동일 버전 재설치/제거/설정 보존을 통과했습니다. 공개 다운로드 5개는 익명 다운로드, GitHub digest·길이·SHA256SUMS, ZIP 파일 허용 목록, 설치파일/EXE 버전, 내장 소스 커밋, 두 ZIP의 Slave 바이너리 일치를 확인했습니다.

**미확인:** 실제 Win7 KI-Messenger에서 v0.3.3의 배경 읽기·복원·활성화와 다음 요청까지의 운영, 이번 버전의 실제 Win7/Win11 설치, .NET 4.8이 없는 깨끗한 오프라인 PC의 선행 런타임/UAC/재부팅 경로. 동일 버전 재설치 결과를 모든 이전 버전 업그레이드의 증거로 보지 않습니다. 2026-09-21 인계 준비 요청에는 새로운 현장 성공/실패 결과가 포함되지 않았습니다.

## 6. Claude 점검 우선순위

아래 질문은 이미 확인된 버그 목록이 아닙니다.

1. **세션 수명:** Ready 직후 명령, 접수 후 추가 메시지, 분할 후 다음 Ready에서 정상 대기가 유지되는가? 안전 중단과 앱 종료를 로그 코드로 구분할 수 있는가?
2. **창과 입력:** 배경 읽기에 불필요한 foreground 요구가 남았는가? 활성화/복원 전후 취소·PID 재사용·root 변경·입력 간섭을 막는가? 실제 입력 전 최신 검사가 유지되는가?
3. **수신 증거:** 최초 접수와 접수 후 재확인의 차이가 모든 호출부에 일관적인가? Ready 본문 완화가 명령 변경·삭제·교체·재실행을 허용하지 않는가?
4. **본문과 이력:** 전체/추가/빈/변경 본문, 긴 유니코드 이름·줄, 분할 실패, 이력 기록 실패에서 누락이나 조기 체크포인트가 없는가?
5. **Slave와 통신:** 수집 중 Pending 전환, LLM 장애, 취소·시간 초과, PID 재사용이 다른 대상 결과를 지우지 않는가? 크기·형식·인증 검사가 양쪽에서 일치하는가?
6. **검사 공백:** 실제 호출 경로를 검증하는 검사인지, 합성 시험만 확인한 부분을 현장 성공으로 오인하지 않는지 구분한다.

인계 준비 중 확인한 **메타데이터 불일치**: 두 `src/RemoteMonitorMaster/app.manifest`, `src/RemoteMonitorSlave/app.manifest`의 `assemblyIdentity`는 `0.2.1.0`이고 `.csproj`의 앱/파일 버전은 `0.3.3/0.3.3.0`입니다. 실제 UI·설치파일 버전 검사는 0.3.3으로 통과했습니다. 매니페스트 버전의 의도와 영향은 검토가 필요하며, 이를 실행 장애 원인으로 단정하지 않습니다. 이번 문서 정리에서 배포 코드나 매니페스트를 바꾸지는 않았습니다.

검토 결과에는 중요도(P1/P2/P3), 파일·행/함수, 재현 조건, 영향, 최소 수정안, 필요한 회귀 검사를 적습니다. 근거가 부족하면 가설로 표시합니다. 코드를 수정한다면 실제 진입점을 추적하고 기존 공용 함수를 먼저 재사용합니다.

이전 네 스킬의 의도도 이어갑니다: integrated-agent-flow는 독립 검토와 근거 확인, ponytail은 최소 변경과 기존 도구 재사용, visualize는 필요한 경우만 도식화, i-have-adhd는 명확한 다음 행동과 적은 수동 단계입니다. 해당 플러그인이 없어도 이 문서와 AGENTS.md만으로 점검할 수 있습니다.

## 7. 검증 명령과 배포

저장소 루트에서 Windows PowerShell과 .NET SDK 8 계열을 사용합니다(CI는 8.0.x). 앱 대상 런타임은 계속 .NET Framework 4.8입니다.

```powershell
git status --short --branch
git diff v0.3.3 -- src scripts installer .github/workflows/ci.yml
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-package.ps1
```

`build-package.ps1`은 두 프로젝트 restore/build, 실제 EXE `--self-test`, ZIP 생성을 실행합니다. 결과는 `dist/verification-v0.3.3/`입니다. 이번 문서 정리만을 위해 앱 검사를 반복하거나 운영 중인 프로그램을 실행할 필요는 없습니다. 코드 수정 시 변경 범위에 맞춰 사용합니다.

설치 관련 변경을 검증할 때만 아래를 실행합니다. 실제 설치/제거를 수행하므로 전용 검증 PC에서 사용합니다. 이미 설치된 역할에 대한 스크립트의 충돌 방지 검사를 따릅니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-installers.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-installers.ps1 -Role Master
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-release-assets.ps1
```

Win11 이상 설치 조건을 만족하는 검증 호스트에서는 `-Role Both`를 사용할 수 있습니다. Win10/Server 결과가 실제 Win7 검증을 대체하지 않습니다. Inno Setup과 공식 .NET 오프라인 설치본의 다운로드·검증은 빌드 단계에서 하며 설치파일 실행 중 다운로드는 없습니다. 자세한 방법은 INSTALL.md에 있습니다.

정식 배포는 `.github/workflows/ci.yml`의 `workflow_dispatch release_tag`로 수행합니다. **v0.3.3은 이미 존재하므로 다시 지정하지 않습니다.** 향후 코드 수정 배포는 새 앱 버전과 일치하는 미사용 `v<version>` 태그를 사용하고 기존 태그·자산을 교체하지 않습니다. 이번 문서 정리는 버전 갱신·재배포 없이 커밋합니다.

## 8. 공개 파일과 로컬 자료

| 위치 | 취급 |
|---|---|
| `src/`, `scripts/`, `installer/`, `.github/`, 루트 문서 | 공개 소스·검사·빌드·운영 안내 |
| `docs/history/` | 날짜가 있는 과거 공개 인계 기록. 현재 지시와 분리 |
| `work/` | 무시된 로컬 검증 자료·시험 코드·다운로드. 새 clone에는 없음 |
| `dist/`, `.cache/`, `bin/`, `obj/` | 무시된 빌드·설치 의존성·결과물. 공개 자료는 검증된 Release에서 받음 |
| 사용자 로그·스크린샷·진단 ZIP·연결파일·설정 | 비공개. 외부 검토에 첨부하거나 커밋하지 않음 |

Master Output 이력은 `%LOCALAPPDATA%\RemoteMonitorMaster\state\powersi-output-history-v1.txt`에 길이·SHA256만 저장합니다. Slave 로컬 설정/인증정보와 `.rmpair` 연결파일을 보존합니다. 사용자의 Downloads나 기존 PowerSI/HFSS 시험 자료를 저장소 정리 명목으로 삭제하거나 복사하지 않습니다. `work/`에는 검증 근거가 있으므로 이번 정리에서 삭제하지 않았습니다.

## 9. 정식 설치파일

- [Master Setup 0.3.3](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.3/Messenger-Remote-Control-Master-Setup-0.3.3.exe) — SHA256 `37A6B14333D86D63A26C609B5A8F0AA214082EF5F30E8CB2B860AF97222278CF`
- [Slave Setup 0.3.3](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.3/Messenger-Remote-Control-Slave-Setup-0.3.3.exe) — SHA256 `AE8C1577CE7A0326250ECC9B11A002410FAB769A7872660A2E60F48B434AEB45`
- [정식 Release](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.3.3) · [SHA256SUMS.txt](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.3.3/SHA256SUMS.txt)

2026-09-21에 Public 저장소의 최신 정식 태그와 소스 참조가 그대로임을 재확인했습니다. 바이너리 다운로드·설치 검증의 실행일은 2026-09-19이며 문서 수정으로 기존 배포물을 교체하지 않았습니다.
