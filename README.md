# QuotaTray (v0.1.0)

<div align="center">

**Cross-Platform AI Quota & Rate Limit Monitor System Tray App**  
*OpenAI Codex, Google Antigravity, Claude Code, GitHub Copilot, OpenRouter, Kimi, Z.AI, Synthetic*

[![Release](https://img.shields.io/badge/Release-v0.1.0-818cf8.svg)](https://github.com/nanocode00/QuotaTray/releases)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-38bdf8.svg)](https://github.com/nanocode00/QuotaTray)
[![Framework](https://img.shields.io/badge/.NET-8.0-10b981.svg)](https://dotnet.microsoft.com/)
[![UI Framework](https://img.shields.io/badge/Avalonia-11.1-ec4899.svg)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-MIT-f59e0b.svg)](LICENSE)

[**한국어 문서**](#-한국어-소개) | [**English Documentation**](#-english-overview)

</div>

---

## 🇰🇷 한국어 소개

**QuotaTray**는 시스템 트레이(System Tray / Menu Bar)에 상주하며 여러 주요 AI 서비스의 **남은 쿼터(Quota)와 리셋 시간, 크레딧 잔액**을 실시간으로 직관적이게 모니터링할 수 있는 크로스 플랫폼(Windows, Linux, macOS) 데스크톱 애플리케이션입니다.

외부 Node.js, Python, WSL2 환경에 일절 의존하지 않는 완전한 **Zero-Dependency 단일 실행 파일(Self-contained Single Binary)**로 동작합니다.

---

### ✨ 핵심 기능

* **🚀 8대 주요 AI 프로바이더 완벽 지원**:
  1. **OpenAI Codex**: API 실제 반환 Rate Limit 윈도우(`5h / 7d`) 및 장기 윈도우 대표값 표시 (`codex login` / `~/.codex/auth.json`)
  2. **Google Antigravity**: Gemini 풀 및 Claude/GPT 풀 쿼터 실시간 모니터링 (`agy auth login` / Cloud Code Assist)
  3. **Claude Code**: Anthropic 5h / 7d 사용률 및 리셋 카운트다운 (`claude login` / `API Key`)
  4. **GitHub Copilot**: 플랜별 최적화 (Free 플랜: Code Completions / 유료 플랜: Premium Requests 단독 집중)
  5. **OpenRouter**: 계정 크레딧 실시간 잔액($), 무료 모델 일일 한도(`50/day` or `1,000/day`), API Key 제한 모니터링
  6. **Kimi (Moonshot AI)**: 사용 가능한 캐시/바우처 잔액 및 쿼터
  7. **Z.AI (Zhipu GLM)**: API 계정 상태 및 토큰 쿼터
  8. **Synthetic.new**: 사용량 및 구독 상태
* **🎨 모던 다크 테마 & 공식 브랜드 벡터 에셋**:
  * 각 AI 공식 SVG 및 투명 벡터 아이콘을 프로젝트 내장(`Avalonia.Svg.Skia`)하여 다크 테마 칩 위에 선명하게 렌더링.
* **📱 2가지 뷰 모드 지원 (간단 모드 vs 상세 모드)**:
  * **간단 모드 (Compact Mode)**: 모든 Provider 카드를 2줄 표준 그리드로 통일하여 완벽한 픽셀 정렬(Progress bar 시작점 및 우측 메타 일치)과 대표 쿼터만 표시.
  * **상세 모드 (Detail Mode)**: 개별 모델 풀(5h, Weekly 등)과 서브타이틀, 세부 리셋 시간을 풍부하게 표시.
* **⚙️ 편리한 인앱 인증 및 설정**:
  * 원클릭 CLI 로그인 콘솔 호출 (`codex login`, `agy auth login` 등)
  * 설정 패널에서 API Key 직접 입력 및 원하는 Provider만 선택 On/Off.
  * 자동 갱신 주기(`1분 ~ 60분`), 쿼터 부족 경고 알림(`5% ~ 50%`) 설정.
* **🔔 스마트 트레이 & 실시간 알림**:
  * 트레이 아이콘 동적 게이지 및 색상(초록/주황/빨강) 변화.
  * 쿼터 부족 알림 및 충전 완료(`🎉 100% 충전 완료`) Toast 알림.

### 🔐 Antigravity 인증 동작

Antigravity/agy에서 한 번 정상 로그인해 로컬 OAuth 세션이 생성되면, 이후 QuotaTray는 `agy` 프로세스가 꺼져 있어도 직접 access token을 갱신해 쿼터를 조회할 수 있습니다.

일반 사용자는 Antigravity OAuth 환경변수를 별도로 설정할 필요가 없습니다. QuotaTray는 공개 installed-app OAuth client 메타데이터를 자동으로 확인하고 SHA-256 fingerprint로 검증한 뒤 QuotaTray 전용 credential cache에 저장합니다. Antigravity가 소유한 `gemini:antigravity` credential은 덮어쓰지 않습니다.

문제 진단 시 다음 명령으로 직접 refresh 경로를 검증할 수 있습니다.

```powershell
dotnet run --project .\QuotaTray.TestCli -- refresh-test
```

자세한 흐름은 `docs/antigravity-oauth-runtime.md`를 참고하세요.

---

### 📥 다운로드 및 실행

[GitHub Releases](https://github.com/HonorBear/QuotaTray/releases)에서 OS에 맞는 단일 실행 파일을 다운로드하여 실행하세요:

| 운영체제 | 다운로드 파일 | 실행 방법 |
| :--- | :--- | :--- |
| **Windows (x64)** | `QuotaTray-windows-x64.zip` | 압축 해제 후 `QuotaTray.Desktop.exe` 실행 |
| **Linux (x64)** | `QuotaTray-linux-x64.tar.gz` | `tar -xzf ...` 후 `./QuotaTray.Desktop` 실행 |
| **macOS (Apple Silicon)** | `QuotaTray-macos-arm64.tar.gz` | `tar -xzf ...` 후 `./QuotaTray.Desktop` 실행 |
| **macOS (Intel x64)** | `QuotaTray-macos-x64.tar.gz` | `tar -xzf ...` 후 `./QuotaTray.Desktop` 실행 |

---

## 🇺🇸 English Overview

**QuotaTray** is a sleek, cross-platform system tray application built with .NET 8 and Avalonia UI 11 for real-time tracking of AI quotas, rate limit reset times, and credit balances across top AI developer tools.

### 🌟 Key Features
* **Zero Dependencies**: Single standalone executable without requiring Python, Node.js, or WSL.
* **8 Top AI Providers Supported**: OpenAI Codex, Google Antigravity, Claude Code, GitHub Copilot, OpenRouter, Kimi, Z.AI, Synthetic.
* **Dual View Modes**:
  * **Compact Mode**: Uniform 2-row pixel-aligned cards with representative quota focus.
  * **Detailed Mode**: Comprehensive breakdown across all model pools and quota buckets.
* **Provider-Specific Quota Rules**:
  * **Codex**: Longest rate limit window representative value with 5h detailed breakdown.
  * **Antigravity**: Multi-pool weekly minimum tracking across Gemini and Claude/GPT pools.
  * **GitHub Copilot**: Smart plan detection (Free: Code Completions / Paid: Premium Requests).
  * **OpenRouter**: Real-time account credit balance ($) with `Free` / `PAYG` status and key limit gauges.
* **Official Brand Vector Assets**: High-resolution dark-chip vector icons with `Avalonia.Svg.Skia`.
* **In-App Credentials & Quick Settings**: One-click CLI authentication, custom API key management, dynamic provider toggle, and customizable toast notification thresholds.

---

## 🛠️ 빌드 방법 (Build from Source)

### 사전 요구사항
* [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 빌드 및 배포 명령어

```powershell
# 저장소 클론
git clone https://github.com/nanocode00/QuotaTray.git
cd QuotaTray

# 솔루션 빌드
dotnet build QuotaTray.sln

# Windows x64 단일 실행 파일 배포
dotnet publish QuotaTray.Desktop/QuotaTray.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/windows-x64

# Linux x64 단일 실행 파일 배포
dotnet publish QuotaTray.Desktop/QuotaTray.Desktop.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish/linux-x64

# macOS ARM64 (Apple Silicon) 단일 실행 파일 배포
dotnet publish QuotaTray.Desktop/QuotaTray.Desktop.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o publish/macos-arm64
```

---

## 🏗️ 아키텍처 및 기술 스택

```text
QuotaTray/
├── QuotaTray.Core/              # .NET 8 Core Library
│   ├── Models/                 # QuotaWindow, QuotaGroup, ProviderQuotaResult
│   ├── Providers/              # 8 AI Provider API Integrations & Parsers
│   ├── Security/               # Win32 Credential Manager & File Credential Resolvers
│   └── Utils/                  # TimeFormatter, Formatting Helpers
├── QuotaTray.Desktop/           # Avalonia UI 11 Cross-Platform Desktop App
│   ├── Assets/Providers/       # Official SVG/PNG Brand Assets
│   ├── Services/               # TrayIconService, ProviderIconService, NotificationService
│   ├── ViewModels/             # QuotaViewModel, ProviderItemViewModel, SettingsViewModel
│   └── Views/                  # MainWindow (Compact & Detail), SettingsWindow
├── .github/workflows/          # Automated GitHub Actions Release Workflow
└── publish/                    # Built single-file binaries
```

* **Core**: C# 12 / .NET 8 LTS
* **UI Framework**: Avalonia UI 11.1 (Fluent Theme)
* **Graphics & SVG Engine**: SkiaSharp / Avalonia.Svg.Skia
* **Storage & Encryption**: `System.Security.Cryptography.ProtectedData`, Win32 Credential Manager, JSON Settings

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
