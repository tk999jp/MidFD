======================================================================
MidFDを起動する前に (README FIRST)
======================================================================

MidFD.exeを起動するには、お使いのPCに次のruntimeが必要です。

  .NET 10.0 Desktop Runtime (Windows x64)

GitHubの「Code > Download ZIP」や「Source code (zip)」はsource codeです。
通常利用する場合は、GitHub Releasesの次の実行用packageを使用してください。

  MidFD-win-x64.zip

----------------------------------------------------------------------
起動できない場合の症状例
----------------------------------------------------------------------

- MidFD.exeをdouble clickしても何も起きない
- 「.NETをinstallしてください」と表示される
- 「Microsoft.WindowsDesktop.App」が見つからないと表示される
- 必要なruntimeが見つからない旨のerrorが表示される

----------------------------------------------------------------------
導入方法1: Microsoft公式Web site (推奨)
----------------------------------------------------------------------

1. 次のMicrosoft公式pageを開きます。

   https://dotnet.microsoft.com/download/dotnet/10.0

2. 「.NET Desktop Runtime 10.0.x」を探します。

3. Windows x64版installerをdownloadします。

   例: windowsdesktop-runtime-10.0.x-win-x64.exe

4. installerを実行し、画面の指示に従ってinstallします。

重要:
- 必ず「.NET Desktop Runtime」のx64版を選んでください。
- 「.NET Runtime」「ASP.NET Core Runtime」だけではWindows Forms appを起動できません。
- .NET SDKが既に正しく導入されている環境では、追加installが不要な場合があります。

----------------------------------------------------------------------
導入方法2: winget (上級者向け)
----------------------------------------------------------------------

Command PromptまたはPowerShellで次を実行します。

  winget install Microsoft.DotNet.DesktopRuntime.10

----------------------------------------------------------------------
外部tool
----------------------------------------------------------------------

MidFDには次の外部toolを同梱していません。

- 7-Zip
- ffmpeg
- ffprobe
- ffplay
- 外部editor

これらは必要な機能だけ別途用意できます。

- 7-Zip未設定時も、対応可能な圧縮・解凍は既存fallbackを使用します。
- ffplay未設定時の外部再生は、Windowsの関連付けを使用します。
- 動画静止画previewにはffmpeg.exeが必要です。

----------------------------------------------------------------------
配布物の確認
----------------------------------------------------------------------

通常の配布ZIPには、少なくとも次が含まれます。

- MidFD.exe
- MidFD.FileOperationHelper.exe
- README_FIRST.txt
- README.md
- CHANGELOG.md
- LICENSE
- UserDocs folder

MidFD.FileOperationHelper.exeは、symlink／junctionの作成に権限が必要な場合だけ使用する補助programです。MidFD.exeと同じ配布folderから削除しないでください。

----------------------------------------------------------------------
動作要件
----------------------------------------------------------------------

- Windows 10 / 11 64bit
- .NET 10.0 Desktop Runtime x64

従来の.NET 8／9だけが導入された環境では起動できません。.NET 10.0 Desktop Runtimeを追加してください。
======================================================================
