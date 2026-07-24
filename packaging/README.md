# MidFD Packaging Spike - v2026.06.28

本ドキュメントは、MidFD v2026.06.28 の配布方式を従来の Runtime 同梱 ZIP からインストーラ方式へ移行するための検証結果と設計を記録したものである。

## 1. 配布方式の比較

| 方式 | メリット | デメリット | 採用可否 |
| :--- | :--- | :--- | :--- |
| **通常ZIP (Framework-Dependent)** | 軽量、DLLが散らからずクリーン | 実行環境に .NET Runtime 10.0 の導入が必要 | **併用（維持）** |
| **Runtime同梱ZIP (Self-Contained)** | Runtime未導入環境でも動作する | 解凍時に大量の runtime DLL がルートに散らばり乱雑 | **廃止推奨** |
| **App-Relative Private Runtime** | `runtime/` フォルダ配下に DLL を隠蔽可能 | 設定が複雑で保守コスト高、サイズ大 | **見送り** |
| **Inno Setup インストーラ** | 適切なフォルダへの DLL 隠蔽、自動アンインストーラ、ショートカット作成 | ビルド時に Inno Setup (ISCC.exe) が必要 | **新規採用（最有力）** |

## 2. インストーラ生成ツールの比較

- **Inno Setup (採用)**
  - 理由: 最もシンプルで保守しやすく、無償で利用可能。スクリプト（`.iss`）ベースでバージョン埋め込みや自動ビルドが容易。
- **WiX Toolset (見送り)**
  - 理由: MSI パッケージが作成できるが、XMLの構造が極めて複雑で、初期のSpike検証には学習コスト・記述コストが高すぎる。
- **MSIX (見送り)**
  - 理由: Windows App SDKやUWP向けに設計されており、クラシックデスクトップアプリ（WinForms）では証明書署名や制約が多く不向き。

## 3. インストーラビルド手順

Inno Setup がインストールされている環境で、以下のスクリプトを実行する。

```powershell
powershell -ExecutionPolicy Bypass -File .\packaging\build-installer.ps1 -ReleaseTag v2026.06.28
```

- `ISCC.exe` が見つかった場合: 自動的に `artifacts\release\MidFD-2026.06.28-setup.exe` が生成される。
- `ISCC.exe` が見つからない場合: テンポラリ出力ディレクトリ `artifacts\temp-publish` に配置用ファイルがビルドされた段階で停止し、手動コンパイル手順が提示される。

## 4. 動作検証 (Smoke Test) 計画

インストーラが正常に生成された場合、以下の検証手順を実行する。

1. **インストール検証**:
   - `MidFD-2026.06.28-setup.exe` を実行。
   - インストール先ディレクトリ（デフォルト: `C:\Program Files\MidFD`）にファイルが正しく配置され、不要な一時ファイルが混入していないか確認。
2. **起動検証**:
   - スタートメニューまたはデスクトップのショートカットから `MidFD.exe` を起動し、正常動作（UI描画、設定読み込み）を確認。
3. **アンインストール検証**:
   - Windows の「設定 > アプリと機能」からアンインストールを実行。
   - インストールディレクトリおよびファイルが完全にクリーンアップされていることを確認。
