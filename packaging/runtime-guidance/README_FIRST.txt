======================================================================
MidFDを起動する前に (README FIRST)
======================================================================

MidFD.exeを起動するには、お使いのPCに次のruntimeが必要です。

  .NET 10.0 Desktop Runtime (Windows x64)

32-bit Susie SPI pluginを使用する場合は、追加で次も必要です。

  .NET 10.0 Desktop Runtime (Windows x86)

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

3. MidFD本体用にWindows x64版installerをdownloadします。

   例: windowsdesktop-runtime-10.0.x-win-x64.exe

4. installerを実行し、画面の指示に従ってinstallします。

重要:
- MidFD本体用に「.NET Desktop Runtime」のx64版を選んでください。
- 32-bit Susie SPI pluginを使う場合は、同じページから「.NET Desktop Runtime」のx86版もinstallしてください。
- 「.NET Runtime」「ASP.NET Core Runtime」だけではWindows Forms appを起動できません。
- .NET SDKが既に正しく導入されている環境では、追加installが不要な場合があります。

----------------------------------------------------------------------
導入方法2: winget (上級者向け)
----------------------------------------------------------------------

Command PromptまたはPowerShellで次を実行します。

  winget install Microsoft.DotNet.DesktopRuntime.10

32-bit Susie SPI pluginを使用する場合は、上記の公式download pageからx86版も追加installしてください。

----------------------------------------------------------------------
外部tool
----------------------------------------------------------------------

内容検索用ripgrep 15.2.0 (rg.exe) は同梱済みで、別途導入は不要です。
rg.exeとlicenses/ripgrep folderをMidFD.exeと同じ配布folderに保持してください。

MidFDには次の外部toolを同梱していません。

- 7-Zip
- ffplay
- 外部editor

これらは必要な機能だけ別途用意できます。

- 7-Zip未設定時も、対応可能な圧縮・解凍は既存fallbackを使用します。
- Browserで動画をEnterすると、MidFD内のMidPlayer Lite DLLで再生します。ViewerはMidFDと同じprocess内で動作し、MidPlayer.exeやNamed Pipeを使いません。対応音声のEnterとZによるWindows関連付け起動は別の外部再生経路です。
- 対応音声のEnter／Ctrl+Enterはffplayで外部再生します。ffplay未設定時はWindowsの関連付けを使用します。
- Browser動画はEnter／Ctrl+Enterで同梱のMidPlayer Lite DLLにより再生します。Vは内容probeによるPreviewで、通常動画はBinary表示となりMidPlayerは起動しません。ZはWindowsの関連付けを使用します。

----------------------------------------------------------------------
配布物の確認
----------------------------------------------------------------------

MidFD配布ZIPには、少なくとも次が含まれます。

- MidFD.exe
- MidFD.dll、MidFD.deps.json、MidFD.runtimeconfig.json
- MidPlayer.Lite.dll
- MidFD.FileOperationHelper.exe
- MidFD.FileOperationHelper.dll、MidFD.FileOperationHelper.deps.json、MidFD.FileOperationHelper.runtimeconfig.json
- SusieHost32/SusieHost32.exe と同名の .dll、.deps.json、.runtimeconfig.json
- SusieHost64/SusieHost64.exe と同名の .dll、.deps.json、.runtimeconfig.json

この配布ZIPにはMidPlayer.Lite.dllを含みます。MidFDとは別に起動するMidPlayer.exeとそのruntime sidecarは含みません。
- rg.exe
- licenses/ripgrep/LICENSE-MIT
- licenses/ripgrep/UNLICENSE
- licenses/ripgrep/COPYING
- licenses/ripgrep/manifest.json
- README_FIRST.txt
- README.md
- CHANGELOG.md
- LICENSE
- UserDocs folder

MidFD.FileOperationHelper.exeは、symlink／junctionの作成に権限が必要な場合だけ使用する補助programです。MidFD.exeと同じ配布folderから削除しないでください。

画像／動画ViewerはMidFD内のMidPlayer.Lite.dllを同一processで使用し、Named PipeやMidPlayer.exeを介しません。動画再生はWindows Media Foundationのみです。LibVLC backendは採用せず、LibVLC／SkiaSharp payloadと外部字幕を含みません。SusieHost32／SusieHost64は別processで動作するため、MidFD.exeと同じ配布folderに保持してください。

----------------------------------------------------------------------
動作要件
----------------------------------------------------------------------

- Windows 10 / 11 64bit
- .NET 10.0 Desktop Runtime x64
- 32-bit Susie SPI plugin使用時: .NET 10.0 Desktop Runtime x86

従来の.NET 8／9だけが導入された環境では起動できません。.NET 10.0 Desktop Runtimeを追加してください。
======================================================================
