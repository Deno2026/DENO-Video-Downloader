# DENO Video Downloader

공개되었거나 다운로드 허락을 받은 영상 링크를 붙여넣어 최고 화질 MP4로 저장하는 Windows용 단일 창 유틸리티입니다.

[최신 포터블 ZIP 다운로드](https://github.com/Deno2026/DENO-Video-Downloader/releases/latest)

## 사용 방법

1. 릴리스 페이지에서 `DENO-Video-Downloader-v1.0.0-win-x64.zip`을 받습니다.
2. ZIP 전체를 원하는 폴더에 압축 해제합니다.
3. `영상 다운로드.exe`를 실행합니다.
4. 공개 영상 링크를 붙여넣고 `다운로드 시작`을 누릅니다.
5. 완성된 MP4는 현재 Windows 사용자의 `다운로드` 폴더에 저장됩니다.

실행 파일만 따로 옮기지 마세요. 같은 폴더의 `tools` 폴더가 함께 있어야 합니다.

## 주요 기능

- YouTube 등 yt-dlp 지원 사이트의 단일 영상 다운로드
- 가능한 최고 화질의 영상과 오디오를 FFmpeg로 MP4 병합
- 실행 시 클립보드 URL 자동 입력
- 실제 다운로드 진행률, 속도, 남은 시간 표시
- 취소와 다운로드 폴더 열기
- 동일한 결과 파일이 있으면 덮어쓰지 않음
- 잘못된 링크, 비공개·로그인 필요 영상, 사이트 제한을 한국어로 안내

재생목록 전체 다운로드, 로그인 우회, 유료·비공개·DRM 콘텐츠 제한 우회 기능은 제공하지 않습니다.

## 지원 환경

- Windows 10/11 x64
- Intel 또는 AMD 64비트 PC
- 관리자 권한과 별도 환경 변수 설정 불필요
- 포터블 ZIP에 .NET 8, yt-dlp, FFmpeg, Node.js 포함

Windows on ARM에서는 정상 실행을 보장하지 않습니다. 앱은 코드 서명이 되어 있지 않아 처음 실행할 때 Windows SmartScreen 안내가 표시될 수 있습니다.

## 이용 범위

본인이 소유한 영상, 저작권자에게 다운로드 허락을 받은 영상, 또는 다운로드와 이용이 허용된 레퍼런스 자료에만 사용하세요. 다운로드한 파일의 재배포·재사용 권리는 별도로 확인해야 합니다.

## 소스 빌드

.NET 8 SDK가 설치된 Windows x64 환경에서 실행합니다.

```powershell
dotnet publish .\DenoVideoDownloader.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true
```

소스 빌드는 앱 실행 파일을 만듭니다. 실제 다운로드를 위해서는 실행 파일 옆 `tools` 폴더에 `yt-dlp.exe`, `node.exe`, FFmpeg shared build를 배치하거나 시스템 `PATH`에서 해당 도구를 찾을 수 있어야 합니다.

도구 탐색 순서는 다음과 같습니다.

1. 실행 파일 옆 `tools` 폴더
2. 시스템 `PATH`
3. 현재 PC의 기존 설치 위치

## 라이선스

앱 소스는 [GPL-3.0-only](LICENSE)로 배포합니다.

포터블 ZIP에 포함된 yt-dlp, FFmpeg, Node.js는 각각의 라이선스를 따릅니다. 자세한 버전과 고지는 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)와 ZIP 내부 `licenses` 폴더에서 확인할 수 있습니다.

