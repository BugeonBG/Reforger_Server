@echo off
setlocal
cd /d "%~dp0"
title 리포저 서버 관리자 빌드

echo ============================================================
echo  리포저 서버 관리자 빌드
echo ============================================================
echo.

rem ---- .NET SDK 확인 -------------------------------------------
where dotnet >nul 2>&1
if errorlevel 1 goto :nodotnet

dotnet --list-sdks > "%TEMP%\rsm_sdks.txt" 2>&1
findstr /r "^8\. ^9\. ^1[0-9]\." "%TEMP%\rsm_sdks.txt" >nul
if errorlevel 1 goto :nosdk

echo [1/2] 테스트 실행 중...
echo.
dotnet run --project tests\RSM.Core.Tests -c Release
if errorlevel 1 goto :fail

echo.
echo [2/2] RSM.exe 만드는 중... (처음에는 패키지를 내려받느라 몇 분 걸립니다)
echo.
rem self-contained: 받는 사람이 .NET을 따로 설치하지 않아도 실행됩니다
dotnet publish src\RSM.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
if errorlevel 1 goto :fail

echo.
echo ============================================================
echo  완료: %CD%\publish\RSM.exe
echo ============================================================
start "" explorer "%CD%\publish"
goto :end

:nodotnet
echo [오류] dotnet 명령을 찾을 수 없습니다. .NET 8 SDK가 설치되어 있지 않습니다.
echo.
echo  1. https://dotnet.microsoft.com/download/dotnet/8.0 에 들어가서
echo  2. 왼쪽 "SDK 8.0.x" 아래 Windows "x64" 설치 파일을 받아 설치하고
echo  3. 이 창을 닫은 뒤 build.bat을 다시 실행하세요.
echo.
echo  (Runtime이 아니라 SDK를 받아야 합니다)
goto :end

:nosdk
echo [오류] .NET은 있지만 8 이상 버전의 SDK가 없습니다. 지금 설치된 SDK:
echo.
type "%TEMP%\rsm_sdks.txt"
echo.
echo  https://dotnet.microsoft.com/download/dotnet/8.0 에서 "SDK 8.0.x" Windows x64를 설치한 뒤
echo  build.bat을 다시 실행하세요.
goto :end

:fail
echo.
echo ============================================================
echo  [실패] 위에 나온 빨간 에러 메시지(error ...)를 캡처해서 보내주세요.
echo ============================================================

:end
echo.
pause
