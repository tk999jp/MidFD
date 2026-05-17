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

## 初回起動

初回起動時は、利用モードの選択ダイアログが表示されます。

選択肢は次の2つです。

| 利用モード     | 内容             |
| --------- | -------------- |
| 実用安定版（推奨） | 通常利用向けの安定モード   |
| 高度機能α版    | 開発中の高度機能を含むモード |

通常は「実用安定版（推奨）」を選んでください。

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
* 高度機能α版の変更を実用安定版へ不用意に影響させない
* 外部ツールや7-Zip連携は環境依存になりやすいことを意識する

## 関連ドキュメント

* [USER_GUIDE.md](USER_GUIDE.md): 基本的な使い方
* [PROFILES.md](PROFILES.md): 利用モードとキー操作体系
* [KEYBINDINGS.md](KEYBINDINGS.md): キーバインド一覧
* [SUPPORT.md](SUPPORT.md): 不具合報告・サポート方針
