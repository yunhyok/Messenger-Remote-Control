# 설치·업그레이드·운영 안내 — v0.2.1

이번 수정은 Master만 교체해도 됩니다. 기존 Slave v0.2.0과 연결파일은 그대로 사용할 수 있습니다. 새 Slave 설치 파일은 신규 설치 또는 같은 표시 버전이 필요한 경우에 사용합니다.

운용 Start 후 선택한 대화창 검증이 끝나면 `Master Ready`가 메신저로 전송됩니다. `pwrsi`/`total status` 처리 전에 추가 명령을 기다리라는 안내가 오고, 결과 회신 뒤 다시 `Master Ready`가 옵니다. 프로그램 실행 직후 대화창이 선택되기 전에는 메시지를 보내지 않습니다.

이전 실패로 입력창에 초안이 남았다면 내용을 확인하고 직접 비운 뒤 새 세션을 시작하세요. 프로그램은 초안을 덮어쓰거나 불확실한 전송을 재시도하지 않습니다. 전송 경로 자체가 실패하면 추가 안내 전송도 중단하며 Master 화면·로그에 사유를 남깁니다.

## 받을 파일

| 파일 | 용도 |
|---|---|
| `Messenger-Remote-Control-Master-Setup-0.2.1.exe` | Windows 7 SP1 이상 Desktop Master 설치 |
| `Messenger-Remote-Control-Slave-Setup-0.2.1.exe` | Windows 11 Workstation Slave 설치 |
| `Messenger-Remote-Control-v0.2.1-win7-win11-net48.zip` | 기존 ZIP 실행 방식, 두 역할 포함 |
| `Messenger-Remote-Control-Slave-v0.2.1-win11-net48.zip` | 오프라인 Slave용 ZIP 실행 방식 |
| `SHA256SUMS.txt` | 위 네 파일의 SHA256 |

[공개 Releases](https://github.com/yunhyok/Messenger-Remote-Control/releases)에서 다운로드합니다. 오프라인 Slave에는 Slave 설치 파일 하나를 옮기면 됩니다. ZIP은 압축을 풀어 해당 역할 EXE를 실행합니다.

## 설치

1. 해당 역할의 기존 Remote Monitor 프로그램만 종료합니다. PowerSI와 HFSS를 종료할 필요는 없습니다.
2. 설치 파일을 실행하고 설치 위치를 확인합니다. 기본 위치는 현재 사용자의 `%LOCALAPPDATA%\Programs\Messenger Remote Control\Master` 또는 `Slave`입니다. 프로그램 설치는 일반 사용자로 진행하며, Master에 필요한 .NET 런타임이 없는 경우에만 Microsoft 런타임 설치에 관리자 승인이 필요합니다.
3. 시작 메뉴의 **Messenger Remote Control Master** 또는 **Messenger Remote Control Slave**를 실행합니다. 창 제목에서 **v0.2.1**을 확인합니다.

Master 설치 파일에는 Microsoft 공식 .NET Framework **4.8** 오프라인 설치본이 들어 있습니다. 런타임이 이미 있으면 설치를 건너뛰며, 없으면 포함된 설치본을 실행합니다. 재부팅이 필요하면 안내한 뒤 사용자가 재부팅하고 Master 설치를 다시 실행합니다. 강제로 재부팅하지 않습니다.

Slave는 Windows에 설치된 .NET Framework 4.8 이상을 확인합니다. 이 런타임이 없는 환경에서는 설치를 중단하고 런타임 설치를 안내합니다. 설치 중에는 인터넷에서 파일을 다운로드하지 않습니다. LM Studio와 모델은 이 설치 파일에 포함되지 않습니다.

## 업그레이드와 제거

같은 Windows 사용자로 같은 역할의 새 설치 파일을 실행하면 기존 설치 위치와 등록을 사용해 갱신합니다. Master와 Slave는 각각 별도의 프로그램으로 등록됩니다. 제어판의 프로그램 이름·버전과 시작 메뉴 바로가기로 구분합니다.

사용자 설정, 연결정보, Slave의 로컬 LM Studio 설정은 기존 저장 위치를 사용합니다. 설치·업그레이드·제거 시 이를 지우지 않습니다. 프로그램 제거는 제어판에서 역할을 선택합니다. 보존된 연결파일에는 인증정보가 있으므로 공개해서는 안 됩니다.

## 처음 연결

Slave에서 통신 IP를 선택하고 Start한 뒤 연결파일을 내보냅니다. 사내 Master에서 이 파일을 열고 KI-Messenger 나와의 대화를 연결합니다. 사용자가 요청한 `pwrsi`만 한 번 수집하며, 다른 창으로 전환되거나 전송 확인이 불명확하면 회신을 중단합니다.

원문 수집으로 상태를 알 수 있으면 그 결과를 사용합니다. 화면 판독이 필요한 경우 Slave의 LM Studio 로컬 서버를 실행하고 이미지 모델을 로드해 두세요. 앱은 서버 실행·모델 로드·클라우드 대체를 자동으로 하지 않습니다.

## 빌드와 배포

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-package.ps1
powershell -ExecutionPolicy Bypass -File scripts/build-installers.ps1
powershell -ExecutionPolicy Bypass -File scripts/test-installers.ps1 -Role Both
powershell -ExecutionPolicy Bypass -File scripts/check-release-assets.ps1
```

빌드 PC는 .NET SDK와 Windows 빌드 환경이 필요합니다. Windows 10 개발 PC에서 설치 검사를 실행할 때는 `test-installers.ps1 -Role Master`로 Master만 검사합니다. Slave의 Windows 11 설치 조건을 우회하지 않습니다. 인스톨러 빌드는 Inno Setup 7.1.0과 Microsoft 런타임 배포본을 공식 경로에서 받아 SHA256과 서명을 확인해 캐시합니다. 다운로드만 준비하려면 `build-installers.ps1 -DependenciesOnly`를 사용합니다. 이후 설치 파일 자체는 오프라인에서 동작합니다.

CI의 `workflow_dispatch`에 `release_tag=v0.2.1-rc1`을 지정하면 일치하는 소스 버전의 새 태그와 pre-release를 만듭니다. 이미 존재하는 태그를 덮어쓰지 않습니다. CI는 Windows Server 2025에서 두 역할을 검사하며, 실제 Windows 7 SP1·Windows 11 또는 현장 PowerSI/메신저 확인을 대신하지 않습니다. 실제 검증 결과는 [HANDOFF.md](HANDOFF.md)에 기록합니다.

참조: [Inno Setup 공식 지원 환경](https://jrsoftware.org/ishelp/topic_whatisinnosetup.htm), [Microsoft .NET Framework 배포 지침](https://learn.microsoft.com/en-us/dotnet/framework/deployment/deployment-guide-for-developers).
