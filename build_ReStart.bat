@echo off
setlocal

cd /d "%~dp0"

taskkill /IM MidFD.exe /F >nul 2>&1

if exist ".\bin\publish" rmdir /S /Q ".\bin\publish"

dotnet publish ".\MidFD.csproj" -c Release -r win-x64 --self-contained false -o ".\bin\publish"
if errorlevel 1 (
  echo dotnet publish failed.
  pause
  exit /b 1
)

robocopy ".\bin\publish" "C:\tools\MidFD" /E /R:0 /W:0
if errorlevel 8 (
  echo robocopy failed.
  pause
  exit /b 1
)

start "" "C:\tools\MidFD\MidFD.exe"
exit /b 0