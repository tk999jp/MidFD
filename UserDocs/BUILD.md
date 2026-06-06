# ビルド方法
MidFD は、Windows 向けの .NET アプリケーションです。

このページでは、ソースコードから MidFD をビルドする方法を説明します。

## 対象環境
MidFD は Windows 環境での利用を前提にしています。

| 項目 | 内容 |
|---|---|
| OS | Windows |
| .NET | .NET 8 SDK |
| プロジェクト | MidFD.csproj |
| UI | Windows Forms |

## 必要なもの
ビルドには、次の環境が必要です。

- Windows
- .NET 8 SDK
- Git
- 必要に応じて Visual Studio または Visual Studio Code  

Visual Studio を使う場合は、.NET デスクトップ開発ワークロードを入れてください。

## ソースコードの取得
GitHub からソースコードを取得します。

```powershell
git clone <repository-url>
cd MidFD
```

`<repository-url>` は、公開リポジトリのURLに置き換えてください。

## ビルド

PowerShell でリポジトリのルートに移動し、次のコマンドを実行します。

```powershell
dotnet build .\MidFD.csproj
```

ビルドに成功すると、Debugビルドの出力は通常次の場所に作成されます。

```text
bin\Debug\net8.0-windows\
```

## 実行

ビルド後、次の実行ファイルを起動します。

```text
bin\Debug\net8.0-windows\MidFD.exe
```

PowerShell から直接起動する場合は、次のように実行できます。

```powershell
.\bin\Debug\net8.0-windows\MidFD.exe
```

## 初回起動（初回セットアップ）

初回起動時は、「MidFD 初回セットアップ」画面が表示されます。

この画面では以下の項目をまとめて設定できます。

- **操作プリセット（キー操作体系）**: 標準的なショートカット操作（標準）か、Fキー中心の操作体系（FD互換）かを選択できます。
- **動画ファイルの Enter 動作**: 動画ファイルを選択して Enter キーを押したときの動作（内蔵プレビュー、外部プレイヤー等）を設定できます。
- **初期オプション（高度な使い方）**: 一部の高度な管理機能（Workspace Snapshot 等）や、追加のファイル操作機能の有効/無効をチェックボックスで選択できます。
- **外部連携パス**: 7-Zip や外部テキストエディタ、ターミナル等の実行ファイルパスを自動検出または手動指定できます。

通常は操作プリセットをお好みに合わせて選択し、初期オプションはチェックを外した状態でセットアップを完了することを推奨します。

設定は後から「設定」画面でいつでも個別に変更可能です。また、初回セットアップ画面自体を設定画面の「起動 / 復元」から再表示することもできます。

詳しくは [PROFILES.md](PROFILES.md) を参照してください。

## Releaseビルド

Release構成でビルドする場合は、次のコマンドを使用します。

```powershell
dotnet build .\MidFD.csproj -c Release
```

出力先は通常次の場所です。

```text
bin\Release\net8.0-windows\
```

## publish

配布用に publish する場合は、次のように実行します。

```powershell
dotnet publish .\MidFD.csproj -c Release
```

publish出力は通常次の場所に作成されます。

```text
bin\Release\net8.0-windows\publish\
```

単一ファイル化などの詳細な配布設定は、今後の公開方針に合わせて変更される可能性があります。

## Release ZIP 作成 (scripts/publish-release.ps1)

配布用の正式なリリースZIPを作成する場合は、以下の PowerShell スクリプト（正本経路）を使用します。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-release.ps1 -ReleaseTag v2026.05.20
```

### 主要パラメータと仕様

- `-ReleaseTag` (必須): `vYYYY.MM.DD` 形式でリリースバージョンを指定します。
- `-SelfContained` (オプション): このスイッチを指定すると、.NET 8 Runtime を同梱した `self-contained` 配布パッケージを作成します。指定しない場合、既定では [.NET 8 Windows Desktop Runtime](https://dotnet.microsoft.com/ja-jp/download/dotnet/8.0) のインストールを前提とする `framework-dependent`（ランタイム非同梱）パッケージを作成します。通常配布時はこのスイッチを指定しません。「.NET Desktop Runtime」の Windows x64 版をインストールしてください。
- `-AllowDirty` (オプション): ワーキングツリーに未コミットの変更がある状態での実行を許可します（開発検証用）。
- `-SkipTagCheck` (オプション): 指定されたGitタグの存在チェック、およびHEADコミットとの一致チェックをスキップします（開発検証用）。

### スクリプトの動作概要

1. **バージョン情報の動的生成**: 指定された `ReleaseTag` から、Csproj用の各種バージョン値を動的に算出して注入します（例: `v2026.05.20` から `Version=2026.5.20`, `InformationalVersion=v2026.05.20` を生成）。
2. **安全性の検証**: 実リリース時は、Gitワーキングツリーがクリーンであり、かつ指定したリリースタグが HEAD コミットを指していることを自動確認します。
3. **発行と動的注入**: `/p:Version` や `/p:InformationalVersion` 等のパラメータを付与し、さらに `-SelfContained` の有無に応じた `--self-contained` 値を設定して `dotnet publish` を実行します。これによりアセンブリおよび `Application.ProductVersion` にバージョンとGitコミットハッシュが自動的に埋め込まれます。
4. **ZIP作成と検証**: `artifacts/release/MidFD-win-x64.zip` を作成後、自動で `artifacts/release-test/` に再解凍して以下の検証を行います。
   - `MidFD.exe` の ProductVersion に指定タグおよびコミットハッシュが正常に含まれていることの確認。
   - 既定の framework-dependent ビルド時、解凍したパッケージに `coreclr.dll`, `hostfxr.dll`, `System.Private.CoreLib.dll` などのランタイム関連ファイルが含まれていない（非同梱である）ことの確認。
5. **SHA256ハッシュの出力**: 検証に成功した場合、ZIP のハッシュ値を `artifacts/release/MidFD-win-x64.zip.sha256` に保存します。

## 7-Zip連携

MidFD は 7-Zip の基本連携に対応しています。

7-Zip連携を使う場合は、7-Zip がインストールされている必要があります。

MidFD は設定または自動検出により、7-Zip の実行ファイルを探します。

利用する可能性がある実行ファイル:

```text
7z.exe
7zG.exe
```

7-Zip が見つからない場合、圧縮、解凍、CRC/SHA などの機能が利用できない場合があります。

## 設定ファイル

MidFD は、実行環境に設定ファイルを作成します。

主な設定例:

| ファイル                | 内容      |
| ------------------- | ------- |
| settings.json       | 基本設定    |
| external_tools.json | 外部ツール定義 |

個人環境のパスや外部ツール設定が含まれる場合があるため、公開リポジトリへ誤って含めないよう注意してください。

## Git管理に含めないもの

次のようなファイルやディレクトリは、通常Git管理に含めません。

* bin/
* obj/
* .vs/
* .dotnet/
* 個人用 settings.json
* 個人用 external_tools.json
* ローカルバックアップ
* publish出力
* 一時ファイル
* ログファイル

## ビルド確認用コマンド

公開前や変更後の確認には、次のコマンドを使用します。

```powershell
dotnet build .\MidFD.csproj
git status --short
```

空白や改行の問題を確認する場合は、次も使用できます。

```powershell
git diff --check
```

## 開発時の注意

MidFD はファイル操作を行うアプリケーションです。

開発中の変更では、次の点に注意してください。

* コピー / 移動 / 削除 / リネームの通常経路を壊さない
* ReadOnlyタブなどの事故防止ガードを弱めない
* 設定ファイルや保存形式を不用意に破壊しない
* 拡張機能向けの変更を標準機能へ不用意に影響させない
* 外部ツールや7-Zip連携は環境依存になりやすいことを意識する

## 関連ドキュメント

* [USER_GUIDE.md](USER_GUIDE.md): 基本的な使い方
* [PROFILES.md](PROFILES.md): 初期オプションとキー操作体系
* [KEYBINDINGS.md](KEYBINDINGS.md): キーバインド一覧
* [SUPPORT.md](SUPPORT.md): 不具合報告・サポート方針
