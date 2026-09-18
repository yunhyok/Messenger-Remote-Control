# Messenger Remote Control v0.2.2

모바일 KI-Messenger의 나와의 대화에 명령을 보내면 Desktop의 **Master**가 Workstation의 **Slave**에 상태를 요청하고, PowerSI별 결과를 같은 대화로 회신합니다.

```mermaid
flowchart LR
  Phone[모바일 메신저] <-->|명령 · 분할 보고서| Master[Master · Windows 7 SP1]
  Master <-->|인증된 사내 통신| Slave[Slave · Windows 11]
  Slave -->|원문 우선 수집| PowerSI[PowerSI 프로세스들]
  Slave -->|필요한 경우 이미지 판독| LLM[동일 PC의 LM Studio]
```

## 설치와 실행

[공개 Releases](https://github.com/yunhyok/Messenger-Remote-Control/releases)에서 역할에 맞는 설치 파일을 받으세요. 오프라인 Slave에는 Slave 설치 파일 또는 Slave ZIP만 옮기면 됩니다. 자세한 설치·업그레이드 방법은 [INSTALL.md](INSTALL.md)에 있습니다.

v0.2.2는 Ready 직후 명령을 보냈을 때 세션이 중단되던 문제를 수정합니다. Ready 뒤의 첫 명령만 처리하고 처리 중 추가 메시지는 다음 Ready까지 무시합니다. 기존 Slave v0.2.0은 그대로 사용할 수 있습니다. 통신 규격은 바뀌지 않았습니다.

1. Workstation에서 Slave를 실행하고 통신 IP를 선택한 뒤 Start를 누릅니다. 화면 판독이 필요한 경우 같은 PC의 LM Studio 서버와 이미지 모델을 사용자가 미리 준비합니다.
2. Slave의 연결파일을 내보내 Desktop Master로 전달합니다. 이 파일은 공개하거나 저장소에 넣지 마세요.
3. Master에서 연결파일을 열고, KI-Messenger의 나와의 대화를 독립 창으로 연 뒤 연결합니다.
4. 메신저에서 `Master Ready`를 확인한 뒤 모바일에서 `pwrsi`를 보냅니다. 처리 시작 안내가 한 번 도착하며, 이후 추가 메시지는 대기열에 넣지 않고 무시합니다. 같은 보고서 번호의 마지막 `N/N`과 다음 `Master Ready`를 받은 뒤 새 명령을 보내세요. Ready 뒤의 대괄호 번호는 각 접수 회차를 구분하며 명령에 입력하지 않습니다.

## 명령

| 전체 메시지 본문 | 동작 |
|---|---|
| `help` | 허용 명령과 사용법 |
| `help help`, `help total status`, `help pwrsi` | 해당 명령 도움말 |
| `total status` | Slave 기본 상태와 프로세스 목록 |
| `pwrsi` | 모든 PowerSI 인스턴스를 한 번 수집해 보고 |

명령은 새 메시지의 본문 전체가 일치해야 합니다. 답장에 포함된 명령 같은 문구는 실행하지 않습니다. 예약 감시, PowerSI 실행·종료와 임의 원격 명령은 제공하지 않습니다.

## 보고서 읽기

- 응답하지 않는 대상은 **전체 Process Name (PID): Pending**만 표시합니다. 해당 대상의 수치·Output은 보내지 않습니다. 이는 Windows 응답 검사 결과이며 PowerSI 내부 계산 대기를 모두 판별하는 기능은 아닙니다.
- 이름은 보고서의 첫 등장에 전체로 표시합니다. 32자를 넘는 이름이 다시 나오면 앞 16자와 뒤 8자를 남깁니다. 다음 조회에서는 다시 전체 이름을 표시합니다.
- 원문 직접 읽기·자동 복사를 우선합니다. 최근 5줄, 최대 600자만 전달하고 생략 여부를 표시합니다. LLM 전사본은 OCR 출처로 구분합니다. 화면과 전체 버퍼는 Slave에 남습니다.
- 각 회신은 1,400자 이내이며 같은 보고서 번호와 순번을 갖습니다. 프로세스 존재, CPU, 실행 시간은 완료나 진행률의 근거로 사용하지 않습니다.
- 수집에는 전체 100초를 배정하고 남은 대상에 시간을 나눕니다. 최대 128개 대상과 목록 밖 누락 수, 미확인·시간 초과를 표시합니다. 조회·답장 준비 한도는 120초이고 실제 분할 전송 시간은 별도입니다.

LM Studio 설정 꺼짐, 서버 미실행, 모델 미로드·오류, 잠금, 최소화, 대상 종료, 사용자 입력 간섭을 가능한 정보와 함께 안내합니다. 서버나 모델을 자동으로 시작하지 않습니다. Master/메신저 자체가 꺼져 있거나 안전한 대화 확인이 실패하면 모바일 회신을 보장할 수 없습니다. 전송 성공 여부가 불명확하면 남은 부분을 중단하고 재전송하지 않습니다.

## 개발과 확인

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-package.ps1
powershell -ExecutionPolicy Bypass -File scripts/build-installers.ps1
```

[HANDOFF.md](HANDOFF.md)는 현재 검증 상태, [SLAVE-TEST.md](SLAVE-TEST.md)와 [WIN7-TEST.md](WIN7-TEST.md)는 새 모바일 보고 경로의 짧은 현장 확인 안내입니다.

출발 소스: [Remote-Control-App v0.1.58](https://github.com/yunhyok/Remote-Control-App/tree/1365e2c249d00b2a73634c77cd7bc82247f82405). 이 저장소는 해당 커밋의 공개 가능한 소스와 빌드 파일을 독립적으로 복사했습니다. 기존 저장소 및 HFSS 작업은 변경하지 않습니다.
