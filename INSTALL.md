# 설치·업그레이드·운영 안내 — 소스 v0.3.4 / 정식 배포 v0.3.3

소스 v0.3.4는 독립 검토 반영판이며 아직 설치파일이 없습니다. 아래 파일 표와 설치 절차는 현재 공개된 정식 배포 v0.3.3 기준입니다. 0.3.4를 배포하면 파일 이름의 버전만 바뀌고 절차는 같습니다. 0.3.4 Master는 기존 Slave v0.3.0~0.3.3과 호환되며(프로토콜 0.3.0), 연결파일·설정·송부 이력을 유지합니다. 0.3.4의 사용자 관점 변화: 운용 창의 중단 사유 설명과 다음 행동 표시, 5초 선택 카운트다운과 선택한 창 표시, 한국어 `Master Ready`/`Processing …` 안내(선두 토큰 유지), 세 줄 `help`, Slave IP 목록 새로고침과 연결파일 저장 경고, 자동 복사 좌표의 디스플레이 배율 보정. Slave 디스플레이 배율은 100%를 권장합니다.

v0.3.3은 분할 회신 중 접수한 명령을 다시 확인할 때 이전 Ready 문구를 과거 기록으로만 처리하는 Master 수정입니다. 새 명령의 처음 접수는 보이는 enabled 전체 메시지로 계속 엄격히 확인하며, 접수 명령·현재 enabled 상태·같은 대화와 요소·이력 행의 식별 또는 순서가 달라지면 회신을 중단합니다. 두 번의 최신 화면 확인, 첫 확인 시각, 한 번만 보내기, 불확실한 전송 중단, 모든 부분 완료 뒤 이력 갱신도 유지합니다. 이력 정리·rebase는 지원하지 않으며 v0.3.2의 화면 조회 속도 개선은 유지합니다. Master만 v0.3.3으로 교체하면 기존 Slave v0.3.0/0.3.1/0.3.2와 호환됩니다. 프로토콜은 0.3.0을 유지하며, 이전 0.2.x에서 업그레이드할 때에는 두 역할을 모두 교체해야 합니다. 기존 연결파일·사용자 설정·송부 이력은 유지됩니다.

운용 Start 후 5초 안에 한 번 선택한 나와의 대화창만 연결합니다. 그 검증이 끝나면 회차 번호가 붙은 `Master Ready`가 메신저로 전송됩니다. 그 뒤의 첫 명령만 접수합니다. `pwrsi`/`total status` 처리 시작 안내는 한 번 보내며, 처리 중 추가 명령·일반 메시지는 대기열에 넣지 않고 무시합니다. 결과 회신 뒤 다음 `Master Ready`가 오면 새 명령을 보내세요. Ready의 대괄호 번호는 명령에 입력하지 않습니다. 프로그램 실행 직후 대화창이 선택되기 전에는 메시지를 보내지 않습니다.

다른 일반 창이 선택한 대화를 덮어도 읽기는 계속합니다. 선택한 대화를 최소화하면 읽기 전에 그 창만 원래 크기로 복원하므로 동작 중에는 최소화 상태로 남지 않습니다. Ready·처리 안내·각 회신 부분 전에는 PC 입력이 1초 이상 멈추고 메뉴·끌기·누른 키가 없어야 합니다. 원래 프로그램·창·대화를 다시 확인한 뒤 회신 입력 직전에만 그 창을 한 번 앞으로 가져옵니다. 다른 창을 움직이거나 크기를 바꾸지 않습니다. Windows가 이를 거부하면 `TARGET_ACTIVATION_REJECTED`로 입력 전 중단하고 자동 재시도하지 않습니다. 잠금·세션 전환·절전도 중단하며 자동으로 다시 시작하지 않습니다.

이전 실패로 입력창에 초안이 남았다면 내용을 확인하고 직접 비운 뒤 새 세션을 시작하세요. 프로그램은 초안을 덮어쓰거나 불확실한 전송을 재시도하지 않습니다. 전송 경로 자체가 실패하면 추가 안내 전송도 중단하며 Master 화면·로그에 사유를 남깁니다.

## 받을 파일

| 파일 | 용도 |
|---|---|
| `Messenger-Remote-Control-Master-Setup-0.3.3.exe` | Windows 7 SP1 이상 Desktop Master 설치 (기본 권장) |
| `Messenger-Remote-Control-Slave-Setup-0.3.3.exe` | Windows 11 Workstation Slave 설치 (기본 권장) |
| `Messenger-Remote-Control-v0.3.3-win7-win11-net48.zip` | 선택 가능한 ZIP 실행 방식, 두 역할 포함 |
| `Messenger-Remote-Control-Slave-v0.3.3-win11-net48.zip` | 선택 가능한 오프라인 Slave용 ZIP 실행 방식 |
| `SHA256SUMS.txt` | 위 네 파일의 SHA256 |

[공개 Releases](https://github.com/yunhyok/Messenger-Remote-Control/releases)에서 다운로드합니다. 오프라인 Slave에는 Slave 설치 파일 하나를 옮기면 됩니다. ZIP은 압축을 풀어 해당 역할 EXE를 실행합니다.

## 설치

1. 해당 역할의 기존 Remote Monitor 프로그램만 종료합니다. PowerSI와 HFSS를 종료할 필요는 없습니다.
2. 설치 파일을 실행하고 설치 위치를 확인합니다. 기본 위치는 현재 사용자의 `%LOCALAPPDATA%\Programs\Messenger Remote Control\Master` 또는 `Slave`입니다. 프로그램 설치는 일반 사용자로 진행하며, Master에 필요한 .NET 런타임이 없는 경우에만 Microsoft 런타임 설치에 관리자 승인이 필요합니다.
3. 시작 메뉴의 **Messenger Remote Control Master** 또는 **Messenger Remote Control Slave**를 실행합니다. 창 제목에서 설치한 버전(**v0.3.3** 또는 배포 후 **v0.3.4**)을 확인합니다. Master 허브 화면의 "Slave 통신 규약"은 앱 버전이 아니라 프로토콜(0.3.0)입니다.

Master 설치 파일에는 Microsoft 공식 .NET Framework **4.8** 오프라인 설치본이 들어 있습니다. 런타임이 이미 있으면 설치를 건너뛰며, 없으면 포함된 설치본을 실행합니다. 재부팅이 필요하면 안내한 뒤 사용자가 재부팅하고 Master 설치를 다시 실행합니다. 강제로 재부팅하지 않습니다.

Slave는 Windows에 설치된 .NET Framework 4.8 이상을 확인합니다. 이 런타임이 없는 환경에서는 설치를 중단하고 런타임 설치를 안내합니다. 설치 중에는 인터넷에서 파일을 다운로드하지 않습니다. LM Studio와 모델은 이 설치 파일에 포함되지 않습니다.

## 업그레이드와 제거

같은 Windows 사용자로 같은 역할의 새 설치 파일을 실행하면 기존 설치 위치와 등록을 사용해 갱신합니다. Master와 Slave는 각각 별도의 프로그램으로 등록됩니다. 제어판의 프로그램 이름·버전과 시작 메뉴 바로가기로 구분합니다.

사용자 설정, 연결정보, Slave의 로컬 LM Studio 설정은 기존 저장 위치를 사용합니다. 설치·업그레이드·제거 시 이를 지우지 않습니다. 프로그램 제거는 제어판에서 역할을 선택합니다. 보존된 연결파일에는 인증정보가 있으므로 공개해서는 안 됩니다.

## 처음 연결

Slave에서 통신 IP를 선택하고 Start한 뒤 연결파일을 내보냅니다. 사내 Master에서 이 파일을 열고 KI-Messenger 나와의 대화를 연결합니다. 사용자가 요청한 `pwrsi`만 한 번 수집합니다. 대기 중 일반 창 뒤에 가려지는 것은 허용하며, 회신 입력 전에 선택한 대화를 앞으로 가져옵니다. 실제 입력 중 다른 창으로 전환되거나 전송 확인이 불명확하면 회신을 중단합니다. 첫 조회는 수집된 전체 Output을 보내고, 다음 조회는 같은 대상 식별자에서 새로 추가된 내용만 보냅니다. Output이 변하지 않았다는 표시는 계산 중단 판정이 아닙니다.

원문 수집으로 상태를 알 수 있으면 그 결과를 사용합니다. 화면 판독이 필요한 경우 Slave의 LM Studio 로컬 서버를 실행하고 이미지 모델을 로드해 두세요. 앱은 서버 실행·모델 로드·클라우드 대체를 자동으로 하지 않습니다.

## 빌드와 배포

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-package.ps1
powershell -ExecutionPolicy Bypass -File scripts/build-installers.ps1
powershell -ExecutionPolicy Bypass -File scripts/test-installers.ps1 -Role Both
powershell -ExecutionPolicy Bypass -File scripts/check-release-assets.ps1
```

빌드 PC는 .NET SDK와 Windows 빌드 환경이 필요합니다. Windows 10 개발 PC에서 설치 검사를 실행할 때는 `test-installers.ps1 -Role Master`로 Master만 검사합니다. Slave의 Windows 11 설치 조건을 우회하지 않습니다. 인스톨러 빌드는 Inno Setup 7.1.0과 Microsoft 런타임 배포본을 공식 경로에서 받아 SHA256과 서명을 확인해 캐시합니다. 다운로드만 준비하려면 `build-installers.ps1 -DependenciesOnly`를 사용합니다. 이후 설치 파일 자체는 오프라인에서 동작합니다.

CI의 `workflow_dispatch`에 새 소스 버전과 일치하는 미사용 `release_tag=v<version>`을 지정하면 새 태그와 정식 Release를 만들고 Latest로 표시합니다. 이미 배포된 `v0.3.3`은 재사용하지 않으며, 0.3.4 소스는 `main` 병합 후 `v0.3.4`로 배포합니다. 설치 검사 스크립트는 설치파일 종료 코드 3010(재부팅 필요)을 "재부팅 후 다시 실행"으로 보고합니다. 설치 EXE가 기본 배포물이고 ZIP은 선택 사항입니다. 기존 태그·자산은 덮어쓰지 않습니다. CI는 Windows Server 2025에서 두 역할을 검사하며, 실제 Windows 7 SP1·Windows 11 또는 현장 PowerSI/메신저 확인을 대신하지 않습니다. 실제 검증 결과는 [HANDOFF.md](HANDOFF.md)에 기록합니다.

참조: [Inno Setup 공식 지원 환경](https://jrsoftware.org/ishelp/topic_whatisinnosetup.htm), [Microsoft .NET Framework 배포 지침](https://learn.microsoft.com/en-us/dotnet/framework/deployment/deployment-guide-for-developers).
