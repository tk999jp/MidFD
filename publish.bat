@echo off
echo ======================================================================
echo [WARNING] publish.bat は廃止されました。
echo リリースZIPを作成するには、以下の正本スクリプトを使用してください:
echo.
echo   powershell -ExecutionPolicy Bypass -File .\scripts\publish-release.ps1 -ReleaseTag vYYYY.MM.DD
echo.
echo ======================================================================
pause
exit /b 1
