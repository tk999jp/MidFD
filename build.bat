@echo off
rem このスクリプトは開発時の動作確認用ビルドです。
rem 配布用のリリースZIPを作成する場合は、.\scripts\publish-release.ps1 を使用してください。
dotnet build .\MidFD.csproj -c Release