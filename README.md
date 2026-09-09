# ViDrive

WPF로 조작하고 Unity로 렌더링하는 논크로마키 방식 버추얼 아바타 관리 툴

> 게임 엔진의 복잡한 인터페이스 없이, 누구나 쉬운 한글 UI만으로 3D 아바타를 불러오고 조작해서 방송에 내보낼 수 있는 프로그램입니다.

---

## 목차

- [프로젝트 소개](#프로젝트-소개)
- [핵심 기능](#핵심-기능)
- [아키텍처](#아키텍처)
- [기술 스택](#기술-스택)
- [설정 저장 방식](#설정-저장-방식)
- [폴더 구조](#폴더-구조)
- [시작하기](#시작하기)
- [팀 구성](#팀-구성)
- [개발 일정](#개발-일정)
- [브랜치 전략](#브랜치-전략)
- [참고 자료](#참고-자료)

---

## 프로젝트 소개

ViDrive는 3D 캐릭터(VRM 아바타)를 쉽게 불러오고, 움직이고, 방송 화면에 띄울 수 있게 도와주는 프로그램입니다.

기존 버추얼 아바타 툴 대부분은 게임 엔진 UI를 그대로 노출하고 영어·일본어 위주로 제공되어 비전문 사용자에게 진입 장벽이 있었습니다. ViDrive는 **3D 렌더링(Unity)**과 **사용자 조작 화면(WPF)**을 완전히 분리해, 사용자는 게임 엔진의 복잡한 구조를 몰라도 직관적인 한글 UI만으로 아바타를 다룰 수 있습니다.

또한 크로마키 합성 대신 **Spout**를 이용해 배경이 제거된 화면을 방송 프로그램에 직접 전달하여, 색상 경계 품질 저하나 화면 캡처 방식의 불안정성 문제를 해결합니다.

## 핵심 기능

- Unity 프로세스 실행 및 WPF 창 내부 임베딩 (`HwndHost`)
- WPF ↔ Unity IPC 통신 채널 구축 (Named Pipe + JSON)
- VRM 아바타 로드
- 아바타 Transform 제어 (위치 / 회전 / 방향)
- 배경 투명 처리(알파 채널 렌더링) 및 누끼 형태 송출
- Spout를 통한 외부 방송 프로그램(OBS 등) 연동 송출

## 아키텍처

```
┌─────────────────────┐        Named Pipe (JSON)        ┌──────────────────────┐        Spout        ┌─────┐
│      WPF (.NET 8)   │ ───────────────────────────────▶│   Unity 6.3 LTS (URP) │ ──────────────────▶ │ OBS │
│  UI 조작 (버튼/슬라이더)│                                  │  VRM 렌더링 / Transform │                      │     │
└─────────────────────┘◀─── SetParent() 창 임베딩(HwndHost) ─┘                       └─────────────────────┘
```

- **WPF**: 모든 사용자 인터랙션(버튼, 슬라이더 등)을 담당. UI 로직은 전적으로 WPF에 위치합니다.
- **Unity**: 3D 렌더링 전용. WPF로부터 명령을 받아 Transform 값을 갱신하고, Spout로 알파 채널 영상을 송출합니다.
- **통합**: 별도 프로세스로 실행된 Unity를 Win32 `SetParent()`로 WPF 창에 임베딩합니다.

## 기술 스택

| 영역 | 기술 |
|---|---|
| UI 프레임워크 | WPF (.NET 8, LTS) |
| 3D 렌더링 엔진 | Unity 6.3 LTS (URP) |
| 창 임베딩 | Win32 `SetParent()` + WPF `HwndHost` |
| 프로세스 간 통신(IPC) | Named Pipe (`System.IO.Pipes`) + JSON |
| 아바타 포맷 | VRM 0.x / 1.0 (UniVRM) |
| 영상 송출 | Spout (KlakSpout) → OBS |
| 그래픽 API | DirectX 11 / 12 |
| 설정/데이터 저장 | JSON 파일 (파일 시스템 기반, RDBMS 미사용) |
| OS | Windows 10 이상 |

## 폴더 구조

```
ViDrive/
├── UnityProject/     # Unity 6.3 LTS 프로젝트 — 렌더링, VRM 로드, Transform
├── WpfApp/           # WPF .NET 8 솔루션 — UI, IPC 송신부
├── docs/             # 기획서, WBS, 기술 문서
└── README.md
```

## 시작하기

### 필요 환경

- Visual Studio 2022 (17.x 이상), `.NET desktop development` 워크로드
- (권장) Visual Studio Tools for Unity 확장
- Unity 6.3 LTS (6000.3), URP 템플릿
- Windows 10 이상

### 클론

```bash
git clone https://github.com/<org-or-user>/ViDrive.git
```

- Unity 작업: `UnityProject/` 를 Unity Hub에서 프로젝트로 추가하여 엽니다.
- WPF 작업: `WpfApp/` 안의 `.sln` 파일을 Visual Studio로 엽니다.

## 팀 구성

| 팀원 | 역할 |
|---|---|
| 한종수 (팀장) | Unity 프론트/아바타 담당 |
| 김기범 | Unity 로직/연동 담당 |
| 김성환 | WPF 담당 |

## 개발 일정

**전체 기간:** 2026-08-25 ~ 2026-12-04 (약 3개월)

상세 WBS는 [`docs/`](./docs) 폴더의 간트표 파일을 참고하세요.

## 브랜치 전략

- `main` — 항상 빌드/시연 가능한 상태 유지, 룰셋으로 보호 (PR + 리뷰 1인 필수)
- `develop` — 통합 브랜치
- `feature/{영역}-{설명}` — 예: `feature/unity-front-vrm-load`, `feature/unity-logic-ipc`, `feature/wpf-transform-ui`

## 참고 자료

- [KlakSpout 공식 GitHub 저장소](https://github.com/keijiro/KlakSpout)
- [UniVRM 공식 GitHub 저장소](https://github.com/vrm-c/UniVRM)

---

한국 폴리텍대학 인천캠퍼스 컴퓨터 공학과 하이테크 과정 디지털융합 팀프로젝트
