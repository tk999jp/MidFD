@echo off
setlocal

cd /d "%~dp0"

set "TARGET_EXE=%~dp0bin\Debug\net8.0-windows\MidFD.exe"

echo [MidFD] Killing debug process if running...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Get-Process MidFD -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq '%TARGET_EXE%' } | Stop-Process -Force"

timeout /t 1 /nobreak >nul

echo [MidFD] Debug build start...
dotnet build ".\MidFD.csproj" -c Debug
if errorlevel 1 (
    echo [MidFD] Build failed.
    pause
    exit /b %errorlevel%
)

echo [MidFD] Launching debug build...
start "" "%TARGET_EXE%"

exit /b 0
