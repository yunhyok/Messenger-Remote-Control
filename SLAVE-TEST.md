# Slave 현장 확인

**0.3.10 확인(Slave를 v0.3.10으로 올린 뒤):** 0.3.10은 LLM 위치 확인이 실패했을 때의 대체 복사를 추가합니다. Slave의 "LM Studio 설정"에서 "PowerSI 화면의 로컬 LLM 판독 사용"과 "모든 PowerSI Output 자동 탐색·복사"를 켜 둔 채 진행하고, 수집이 도는 동안 Slave PC를 조작하지 마세요(PowerSI 창을 앞으로 가져오고 클립보드를 덮어씁니다). ① LM Studio 서버와 모델이 정상인 상태에서 "PowerSI 전체 수집"을 한 번 실행해 자동 복사가 성공한 뒤 상태 줄의 "저장 위치 n개"가 1개 이상인지 봅니다. ② LM Studio 서버를 멈추거나 모델을 내린 뒤 다시 "PowerSI 전체 수집"을 실행하거나 휴대폰에서 `pwrsi`를 보냅니다. 기대 동작은 상태 줄의 "최근 대체 복사: 비전 실패(VISION_SERVER_UNAVAILABLE) → … 복사 성공"(모델을 내렸다면 다른 `VISION_*` 코드)과 PID 목록의 "· 대체 복사 ANCHOR"이며, Master 답장에는 `[code AUTO_COPY_ANCHOR_READ]`가 옵니다. ①을 거치지 않은 첫 사용이라면 `AUTO_COPY_SCOPE_READ`(Output 창 영역) 또는 `AUTO_COPY_LAYOUT_READ`(같은 크기 창의 저장 위치)가 오거나, 사용할 위치가 없어 종전처럼 `AUTO_COPY_REGION_UNCONFIRMED`가 될 수 있습니다. 결과 칸에 보이는 본문이 실제 PowerSI Output인지 확인하고, 로그에서는 대상별 `OUTPUT_FALLBACK_RESULT`의 `route`·`code`, `OUTPUT_ANCHOR_DROPPED`가 있으면 그 `reason`, 실패한 대상의 `OUTPUT_BUFFER`의 `detail`(`FB|…`로 시작하는 코드 목록)만 알려 주세요. 원문 로그·스크린샷·Output 본문은 올리지 마세요. 확인이 끝나면 LM Studio를 원래대로 되돌립니다.

**0.3.4 추가 확인(Slave를 v0.3.4로 올린 경우에만):** Slave 시작 전 "IP·프로그램 목록 새로고침"으로 주소 목록이 갱신되는지, 연결파일 저장 시 인증정보 경고가 나오는지, 실제 디스플레이 배율(100%가 아니면 특히)에서 `RemoteMonitorSlave.exe --self-test`의 LiveTest가 `PASS:`인지 `SKIP:`인지, `PowerSI 전체 수집` 결과 코드가 `AUTO_COPY_READ`인지 `AUTO_COPY_OCCLUDED`인지만 기록합니다. Slave를 올리지 않으면 아래 절차만 적용됩니다.

## v0.3.3 기준 절차

이번 수정은 이미 접수한 요청의 Master 재확인과 창 사용 경로입니다. **Slave v0.3.0/0.3.1/0.3.2를 다시 설치하거나 기존 PowerSI 시험을 반복할 필요가 없습니다.** 프로토콜은 0.3.0이며 설정과 연결파일을 유지합니다. v0.3.2의 화면 조회 속도 개선도 유지합니다.

기존에 완료한 PowerSI 시험과 진행 중인 HFSS 시험을 반복하지 않습니다. 이 절차는 새 Master→Slave→메신저 보고 경로만 짧게 확인합니다.

1. Master만 v0.3.3으로 교체하고, 메신저 창을 최소화하지 않은 채 다른 일반 창 뒤에 둔 상태에서 [WIN7-TEST.md](WIN7-TEST.md)의 다음 자연 발생 분할 회신을 확인합니다. 기존 Slave는 유지합니다.
2. 나중에 평소 명령을 한 번 보내 정상 접수·회신을 확인합니다. PowerSI/HFSS를 새로 시작·종료하거나 이력을 지우지 않습니다.

비공개 v0.3.1 현장 기록은 10개 부분 중 여섯 부분 뒤 일곱 번째 전 `RECEIVE_READY_BOUNDARY_CHANGED`를 보였지만, Ready 문구 또는 화면 구조의 정확한 변화와 idle timeout·crash는 확인하지 못했습니다. 회신 누락·오류가 있으면 보고서 번호와 Slave 상태만 기록하고, 원문·스크린샷·로그·연결파일은 공개 이슈나 저장소에 올리지 마세요.

v0.3.3은 로컬 Release 빌드·실제 EXE 자체 검사·설치파일 생성과 Master 설치 검사를 통과했습니다. Windows Server 2025 CI에서 두 역할의 빌드·자체 검사·설치·동일 버전 재설치·제거·설정 보존을 확인했고, 배포 후 공개 Release 다운로드 검증도 별도로 완료했습니다. 실제 Windows 7/11에서 이번 버전의 설치·메신저 동작과, 런타임이 없는 깨끗한 오프라인 PC의 설치는 현장 확인 전입니다. 자세한 근거는 [HANDOFF.md](HANDOFF.md)에 있습니다.
