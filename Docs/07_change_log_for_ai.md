# MidFD AI Change Log

## 2026-05-19 / Phase: VideoStill ImageViewerForm closeout and documentation alignment

### Status
Runtime verified; closed

### 目的
- ImageViewerForm上でのVideoStill（動画静止画プレビュー）最終実機観測OKを受け、関連フェーズを正式にcloseoutする。
- 同時に、設計資料・状態管理（`Docs/state`）、およびユーザー向けガイド（`USER_GUIDE.md`）の整合性を整理し、VideoStillの正本表示先が `ImageViewerForm` であることを明文化する。

### 変更内容
- **ドキュメントの整理**:
  - `current_focus.md`, `phase_backlog.md`, `decision_log.md` において、進行中・未完了だった VideoStill 関連フェーズおよび過去の途中フェーズを統合・整理し、すべて `Runtime verified; closed` または `superseded` として完了状態に更新。
  - VideoStill の正本が `ImageViewerForm` であり、MainForm側は軽量な案内文表示および外部再生呼び出しキーのフックに留まる設計を明文化。
  - `statusStrip` の非表示設定（黒帯解消）、0秒開始の基準化、および外部再生開始位置（ffplay `-ss`）の近傍シーク特性などの技術的制約を明記。
- **ユーザー向けドキュメント (`UserDocs/USER_GUIDE.md`, `UserDocs/KEYBINDINGS.md`, `README.md`) の整備**:
  - ユーザー向けに、動画ファイルでの `Enter / V` キーによる `ImageViewerForm` での静止画プレビュー表示仕様、下部位置バー（シーク）、`← / →`（ステップ移動）、`Shift + ← / →`（大ステップ移動）、`Home`（0秒戻し）、`Ctrl+Enter`（外部再生）のキー操作体系を明記。
  - `ffmpeg.exe / ffplay.exe / ffprobe.exe` の外部連携設定方法、自動検出規則、およびツール非同梱に伴う注意点（ライセンス・配布元確認等）を記載。


## 2026-05-18 / Phase: archive contents implicit directory synthesis corrective

### Status
Runtime verified / closed

### 目的
- アーカイブ内容プレビュー（Archive Contents Preview）において、ZIP内のパス階層に対して親ディレクトリエントリが明示的に定義されていないケース（`case_ng.zip` 等）で、プレビュー画面が `this dir: 0` となりフォルダ階層が一切表示されなくなる不具合を修正する。

### 変更内容
- **`Models/ArchiveListModels.cs` の変更**:
  - `ArchiveEntry` モデルに、動的に合成された仮想中間ディレクトリであることを識別するための `IsSyntheticDirectory` プロパティを追加。
- **`Dialogs/ArchiveListDialog.cs` の変更**:
  - `PopulateItems` メソッド内の一覧表示投影処理において、現在地 `_currentPath` に対する直下エントリを抽出する際、より深い階層にあるエントリから「直近の中間仮想ディレクトリ」を動的に合成・重複排除してマップ（`visibleMap`）に蓄積するロジックに刷新。
  - 合成ディレクトリは表示および遷移専用（`IsDirectory = true`）として機能させ、マーク処理（`ToggleMark`）では `entry.IsSyntheticDirectory` の場合に早期リターンするガードを追加（マーク不可に統一）。


## 2026-05-18 / Phase: ZIP archive entry path separator normalization corrective (Runtime failed / corrective needed)

### Status
Runtime failed / corrective needed

### 目的
- Windows標準のZIP機能や一部の圧縮ツールで生成された、ZIP内のファイルエントリパスがバックスラッシュ（`\`）区切りになっているアーカイブにおいて、Archive Contents Preview（アーカイブ内プレビュー）で正常に階層が構築されず、遷移や解凍指示が行えない問題を修正する。

### 変更内容
- **`Models/ArchiveListModels.cs` の変更**:
  - `ArchiveEntry` クラスに、元のエントリパス名（バックスラッシュを含む可能性のあるもの）を保持する `RawEntryPath` プロパティを追加。
- **`Services/ArchiveListService.cs` の変更**:
  - 7-Zip 一覧出力解析（`AddEntryIfPresent`）および tar 一覧出力解析（`ParseTarEntries`）において、読み込んだパスのバックスラッシュをスラッシュに置換した正規化パスを `EntryPath` に格納し、元パスを `RawEntryPath` に保存するよう実装。
  - ディレクトリ判定および表示名生成を正規化後のパスを基準に行うことで精度を向上。
- **`Dialogs/ArchiveListDialog.cs` の変更**:
  - `PopulateItems`（表示用一覧生成）、`NavigateUp` / `NavigateDown`（ディレクトリ遷移）、`BuildLocationText` 等におけるその場しのぎのバックスラッシュ置換処理を除去し、常に `/` 区切りに統一された `EntryPath` を前提とする簡潔なパス処理へ統合。
  - 解凍対象エントリの取得処理（`GetMarkedEntryPaths`）において、7-Zip や `tar` にコマンド引数として引き渡す実ファイル名に `RawEntryPath`（元パス）を返すようにし、抽出時の完全な前方互換性を確保。


## 2026-05-18 / Phase: legacy SUSIE preview remnants removal

### Status
Runtime verified / closed

### 目的
- MidFD 初期公開後の品質・設計の単純化として、古いプレビュー機能の一部として残存していた Susie Plugin（.sph / .spi）のデコード連携・資産（Services/Susie 配下）を完全に撤去し、現行のプレビュー処理経路（標準ストリームロードおよび WIC 方式）に統一する。

### 変更内容
- **Susie 関連コード・ファイルの完全撤去**:
  - `Services/Susie/NativeMethods.cs` を Git および物理ディスクから削除。
  - `Services/Susie/SusiePreviewService.cs` (およびその内部クラス `SusiePlugin`) を Git および物理ディスクから削除。
- **ImagePreviewService.cs の修正**:
  - `SusiePreviewService` へのインポート記述 `using MidFD.Services.Susie;` を削除。
  - WIC プレビュー失敗時の fallback ロックとして Susie を呼び出す `SusiePreviewService.GetPreviewImage(path)` 処理を完全に削除し、単純な例外メッセージ返却へ整理。


## 2026-05-17 / Phase: public repository publication readiness corrective

### Status
Static verified / docs verified

### 目的
- GitHub公開（パブリック公開）の準備段階として、公式ドキュメント（`README.md`および`UserDocs/`）における免責・注意事項、FDとの「非完全互換性（独自機能差分の明記）」、サポート体制の限界を明記し、`.gitattributes` にて開発用設計資料などの公開除外（export-ignore）を設定する。

### 変更内容
- **README.md の更新**:
  - 冒頭の説明をFDライクな軽量ファイラーとしての位置づけ、非完全互換性、独自差分についての記述へ差し替え。
  - 重要なファイル操作に関する免責・補償不可、バックアップ推奨を記載した `## 注意事項` を新設。
- **UserDocs/USER_GUIDE.md の更新**:
  - 冒頭にFDライクな軽量ファイラー、キーボード重視、非完全互換、独自差分についての説明を追加。
  - 末尾にバックアップ推奨およびファイルの破損、消失、誤操作に対する免責と補償不可を明示する `## 免責事項と注意事項` を新設。
- **KEYBINDINGS.md / PROFILES.md の更新**:
  - 「FD互換」という表現について、「キー操作体系や表示上の名称であり、オリジナルのFDとの完全互換を意味するものではない」旨の警告・補足を追加。
- **SUPPORT.md の更新**:
  - 個人開発のソフトウェアであり、不具合対応の保証や対応時期の保証ができないことを明示。
- **Docs/README.md の作成・更新**:
  - 開発用の設計資料やAI作業ログなどが格納される `Docs/` について、内部資料であることを明記。
- **.gitattributes の更新**:
  - 公開用の `git archive` などによる export 時に `/Docs/**`、`/AGENTS.md`、`/*.zip`、`/*.sha256` が除外される `export-ignore` 設定を追加。


## 2026-05-17 / Phase: public repository boundary decision

### Status
Static verified / docs verified

### 目的
- GitHub公開に向けて、リポジトリの公開範囲（境界）を確定させ、AI駆動開発の基幹ファイル `.codex` や `Docs` は維持しつつ、一時的な検証画像が追跡されている `artifacts/` を Git 追跡対象から除外する。

### 変更内容
- **Git管理対象からの除外**:
  - `git rm -r --cached artifacts` を実行し、ローカルの実体ファイルは消さずに Git インデックスからのみ `artifacts/` 配下ファイルを削除。
  - `.gitignore` にすでに `artifacts/` や `scratch/`, `logs/`, `*.tmp` などの除外設定が含まれていることを確認。
- **意思決定と文書更新**:
  - `.codex/` と `Docs/` はAI駆動開発の履歴および継続開発の基幹として公開対象に残すことを決定。
  - `.codex/state/decision_log.md` に判断理由を記録し、`current_focus.md`, `open_questions.md`, `phase_backlog.md` を更新。


## 2026-05-17 / Phase: AGENTS markdown publication readiness corrective

### Status
Static verified / docs verified

### 目的
- GitHub 公開に向けて `AGENTS.md` の整形と参照整合性の確認を行う。

### 変更内容
- **文書の修正 (`AGENTS.md`)**:
    - 335行目のコードフェンス（バックティックの数）を修正。
- **整合性確認**:
    - `Skill Usage Rule` の参照先である `.codex/skill/` が存在し、Git 管理下にあることを確認。
    - 内容に機密情報や文字化けがないことを確認。

## 2026-05-17 / Phase: public documentation channel registration ware addition

### Status
Static verified / docs verified

### 目的
- `README.md` および `UserDocs/SUPPORT.md` に作者の YouTube チャンネル紹介を追加し、「チャンネル登録してウェア」としての位置づけを明記する。

### 変更内容
- **公開文書の更新 (`README.md`, `UserDocs/SUPPORT.md`, `UserDocs/USER_GUIDE.md`)**:
    - 作者・関連チャンネルの紹介セクションの表記を「チャンネル応援ウェア」に洗練。
    - 各ドキュメント間の用語平仄を合わせるため、「7-Zip 連携」を「アーカイブ操作 (7-Zip 連携 / Windows標準 fallback)」等に統一。
    - `README.md` の冒頭に H1 ヘッダーを追加。
    - チャンネル登録は任意の応援であり、Apache 2.0 ライセンスに基づく利用の必須条件ではないことを改めて明記。
    - 不具合報告や機能要望については GitHub Issues を優先する方針を改めて周知。

## 2026-05-17 / Phase: logdisk directory tab completion corrective

### Status
Runtime verified; closed

### 目的
- `LogdiskDialog` 等のディレクトリ入力欄において、Tab キーによる候補の巡回補完（サイクル補完）機能を実装し、キーボード操作による移動効率を向上させる。

### 変更内容
- **補完コントローラーの拡張 (`DirectoryPathCompletionController.cs`)**:
    - Tab キーによる候補巡回（サイクル表示）ロジックを実装。
    - `PreviewKeyDown` にて Tab キーを補完中のみ `IsInputKey = true` とし、フォーカス移動を抑制する処理を追加。
    - 候補巡回中は、選択されたディレクトリ名をテキストボックスに即時反映するように改善。
    - 巡回中（テキスト変更を伴う）の再検索を抑制し、セッションを維持するフラグ管理を導入。
- **補完ポップアップの外観微調整 (`DirectoryPathCompletionController.cs`)**:
    - 巡回中の選択状態が視覚的にわかりやすいよう、ListBox の選択更新を伴うように修正。

### 実機確認結果 (Verification Results)
- `LogdiskDialog` 等のディレクトリ入力欄において、Tab キー（正順）および Shift+Tab（逆順）による候補の巡回補完が動作することを確認。
- Enter でのパス確定、Esc での補完キャンセル（またはダイアログキャンセル）が機能することを確認。
- 既存のインクリメンタル補完ポップアップとの共存に問題がないことを確認。
- Browser 本体の Tab キーによるマーク操作に影響や回帰がないことを確認。

## 2026-05-17 / Phase: WinFD-compatible attribute grouped sort corrective

### Status
Runtime verified; closed.

### 目的
- ソート時の並びを WinFD 互換へ寄せ、ディレクトリ群/ファイル群を混在させず、各群内で属性グループ化してから指定キーで並べる。

### 変更内容
- **ソートの差分補正 (`Services/DirectoryProvider.cs`)**:
  - 既存は `SortKind.Name` でのみ属性寄せが入り、`Ext/Size/Date/DateCreated/DateAccessed` は属性グループ化されていなかった。
  - `GetAttributeSortRank` を追加し、全SortKindでディレクトリ群/ファイル群それぞれに共通適用。
  - 属性ランクは `System > Hidden > ReadOnly > Normal/Archive`。
  - 昇順/降順は属性グループ内の指定ソートキーとName tie-breakerにのみ適用。
- **Archiveの扱い**:
  - Archive only は通常扱いとして rank 3 に寄せ、独立グループ化しない。

### 非対象
- AttributeDialog、属性/日時変更処理、再帰適用、SortDialog UI、属性色定義は変更しない。

### 実機確認結果
- ディレクトリ群/ファイル群を混在させず、各群内で属性グループ順に並ぶことを確認。
- Name / Ext / Size / Date / DateCreated / DateAccessed の各ソートで、属性グループ内ソートが成立することを確認。

## 2026-05-17 / Phase: attribute color WinFD compatibility corrective

### Status
Runtime verified; closed.

### 目的
- 一覧の属性色を WinFD 互換へ寄せ、`System / Hidden / ReadOnly` の視認性と優先順位を補正する。

### 変更内容
- **WinFD互換色へ補正 (`Models/MidFDColors.cs`)**:
  - `System=マゼンタ`、`Hidden=ブルー`、`ReadOnly=グリーン` に統一。
  - `ListSystemFore` を追加し、`System` と `Hidden` の色を分離。
- **優先順位維持 (`Services/FileSystemItemFactory.cs`, `MainForm.cs`)**:
  - 属性色判定を `System > Hidden > ReadOnly` 優先で維持。
  - `System + Hidden` はSystem色、`Hidden + ReadOnly` はHidden色になる。
- **Archiveの扱い**:
  - 今回は Archive 専用色を強調せず、通常色扱いへ寄せた（WinFD比較で必須要件外のため）。

### 非対象
- AttributeDialog、属性/日時適用処理、再帰適用、日時ソート（SortKind/DirectoryProvider/SortDialog）は変更しない。

### 実機確認結果
- WinFD互換属性色（System=マゼンタ / Hidden=ブルー / ReadOnly=グリーン）を確認。
- 複数属性時の色優先順位が `System > Hidden > ReadOnly` であることを確認。

## 2026-05-17 / Phase: file metadata attribute timestamp and datetime sort corrective

### Status
Runtime verified; closed.

### 目的
- ファイル/フォルダの属性変更に加えて、作成日時/更新日時/最終アクセス日時の変更と再帰適用を安全に行えるようにする。
- 一覧表示で属性別の視認性を上げ、日時ソートを更新日時以外にも切り替え可能にする。

### 変更内容
- **属性ダイアログ拡張 (`Dialogs/AttributeDialog.cs`)**:
  - 単一ファイル専用UIから、複数対象向けUIへ拡張。
  - `ReadOnly/Hidden/System/Archive`、日時3種（更新/作成/最終アクセス）、`サブディレクトリ以下も処理する` を追加。
- **属性/日時適用処理の拡張 (`MainForm.cs`)**:
  - `ExecuteAttribute` を SelectionResolverベースへ変更し、ファイル/フォルダ混在対象を処理可能にした。
  - 再帰ON時はフォルダ配下を展開し、`ReparsePoint` は再帰追跡しない。
  - 大件数または再帰時は `FileOperationProgressFallbackForm(canCancel:false)` を表示。
- **一覧属性色分け (`Models/MidFDColors.cs`, `Services/FileSystemItemFactory.cs`, `MainForm.cs`)**:
  - 優先順位 `System > Hidden > ReadOnly > Archive > Normal` を適用。
  - `ListReadOnlyFore` / `ListArchiveFore` をテーマ別に追加。
- **日時ソート拡張 (`Models/SortKind.cs`, `Services/DirectoryProvider.cs`, `Dialogs/SortDialog.cs`, `MainForm.cs`)**:
  - `SortKind.DateCreated` / `SortKind.DateAccessed` を追加。
  - SortDialogに「日時種別（更新/作成/最終アクセス）」を追加。
  - 既存 `Date` は更新日時（LastWriteTime）として維持。

### 実機確認結果
- 単一ファイル/単一フォルダで属性変更ができることを確認。
- 作成日時 / 更新日時 / 最終アクセス日時の変更と、チェックON項目のみ反映されることを確認。
- `サブディレクトリ以下も処理する` の再帰適用ができることを確認。
- ReadOnlyタブで属性/日時変更がブロックされることを確認。
- 日時ソート（更新日時 / 作成日時 / 最終アクセス日時）で並び順が変わることを確認。

## 2026-05-16 / Phase: batch rename apply progress visibility corrective

### Status
Runtime verified; closed

### 目的
- 多数ファイル（数千件規模）の一括リネーム実行時に、メインウィンドウが「応答なし」に見えてしまう問題を修正し、進捗ダイアログを表示して視覚的フィードバックを提供する。

### 変更内容
- **一括リネーム処理の非同期化と進捗報告対応 (`RenameApplyCoordinator.cs`)**:
    - `ApplyBatchRename` メソッドに `Action<int, int, string>` 型の進捗コールバックを追加。
    - UIスレッドへの描画負荷を低減するため、100件ごとまたは最後の1件で進捗コールバックを呼び出すようスロットリング（間引き）処理を実装。
- **メインフォームでの非同期バックグラウンド実行 (`MainForm.cs`)**:
    - `ExecuteBatchRename` メソッドを `async void` に変更し、非同期メソッド化。
    - `Task.Run` を使用して `ApplyBatchRename` をバックグラウンドスレッドで実行するようにし、UIのフリーズを回避。
- **進捗ダイアログ（キャンセル不可）の導入 (`MainForm.cs`)**:
    - アーカイブ操作等で用いられる `FileOperationProgressFallbackForm` を利用して進捗ダイアログを表示。
    - 中途半端な状態でのリネーム中断がもたらす不整合リスク（および既存Undo契約との不整合）を回避するため、ダイアログは「キャンセル不可」とした。
    - 他のファイル操作との競合を防ぐため、`PrepareFileOperation` / `FinalizeFileOperation` インフラを組み込み、処理中はメインウィンドウ側の操作を安全にブロックする状態ロックを追加。
    - エラー時のメッセージボックス表示を `Invoke` / `BeginInvoke` でラップし、UIスレッド違反を防止。

### 実機確認結果 (Verification Results)
- 数千件規模の一括リネーム適用時に、進捗ダイアログが正しく表示されることを確認。
- 進捗ダイアログにて `1400/5980 件` のような処理件数と処理中ファイル名が随時更新されることを確認。
- 処理中に UI スレッドがフリーズして固まっているように見える問題が改善したことを確認。

## 2026-05-16 / Phase: rename collision and locked-tab root navigation corrective

### Status
Runtime verified; closed

### 目的
- 一括リネームで特定のパターン（$2N$E など）を指定した際に、実際には衝突しないケースで衝突警告が出る問題を修正。
- 固定タブ（Locked Tab）において、サブディレクトリへ移動した後にタブを切り替えて戻ると、`\` キーによるルート復帰が失敗する問題を修正。

### 変更内容
- **一括リネームの衝突判定修正 (`RenamePreviewService.cs`)**:
    - リネーム先のパスが、現在のリネームバッチに含まれる他のファイルの元パスと一致する場合、そのファイルも移動して退くため、衝突判定から除外するようにロジックを修正。
- **タブロック時の StartupPath の正規化・クリア (`MainForm.cs`)**:
    - `ToggleBrowserTabLock` において、ロック時に `NormalizeDestinationDirectory` を用いて `StartupPath` を正規化するようにし、ドライブルートの末尾セパレータ欠落による `Directory.Exists` の失敗を防止。
    - アンロック時には `StartupPath` を明示的に空文字にクリアするようにし、再ロック時に現在の位置が正しく新しいルートとして設定されるようにした。
- **ルート移動時の耐性向上 (`MainForm.cs`)**:
    - `ExecuteDriveRoot` (`\` キーの挙動）において、保持されている `lockRootPath` の存在確認に失敗した場合、再正規化を試みるように改善。
    - それでも解決しない場合は警告を表示した上で、通常のドライブルート（ドライブのトップ）へ移動するようフォールバック処理を追加。

### ビルド確認
- `dotnet build`: 成功。

### 実機確認結果 (Verification Results)
- 一括リネーム `$2N$E` において、実際には衝突しないケースが OK 表示になり、リネームできることを確認。
- 固定タブにおいて、固定ルート配下へ移動後に別タブへ移り、戻って `\` を押しても問題なく固定ルートへ戻ることを確認。

## 2026-05-16 / Phase: tar fallback extract destination collision corrective

### Status
Runtime verified; closed.

### 目的
- アーカイブ解凍時に、展開先フォルダ名が既存のファイル名と衝突して解凍が失敗する問題を修正。

### 変更内容
- **`ArchiveExtractService` の強化**:
  - `EnsureSafeExtractDestinationDirectory` ヘルパーを実装。展開先がファイルとして存在する場合、`_extracted` などのサフィックスを付与して衝突を回避する。
  - `ResolveDestinationDirectory` に上記安全化ロジックを統合。
- **UI 統合**:
  - `MainForm` の一括解凍および `ArchiveListDialog` (プレビュー画面) からの解凍の両方に、自動的に安全なパス解決が適用されるようにした。
- **ログの追加**:
  - 衝突回避（リダイレクト）が発生した際、`LogService.Info` に詳細を記録するようにした。

### 実機確認結果
- **既存ファイル衝突の回避**: `ExCSS.dll.7z` 解凍時に同名のファイル `ExCSS.dll` が存在しても、`ExCSS.dll_extracted` フォルダが自動生成され、その中に正常に解凍されることを確認。
- **連番生成**: 既に `_extracted` が存在する場合、`_extracted_1` ... と連番が振られることを確認。
- **UI統合の正常動作**: 通常の一括解凍、およびプレビュー画面（Archive Contents）からの個別解凍の両方で、この衝突回避ロジックが期待通りに機能することを確認。
- **ログ記録**: リダイレクトが発生した際、`LogService.Info` に詳細な情報が記録されることを確認。

## 2026-05-16 / Phase: tar fallback extract destination corrective

### Status
Runtime verified; closed.

### 目的
- `TarFallbackService` (tar.exe fallback) によるアーカイブ展開時の信頼性向上。
- 展開先ディレクトリの自動作成保証と、展開引数の最適化、エラーメッセージの具体化。

### 変更内容
- **`TarFallbackService.Unpack` の修正**:
  - 展開引数を `-axvf` から `-xvf` に変更（一部環境での `-a` による警告・失敗を回避）。
  - 展開実行前に `Directory.CreateDirectory(destinationDirectory)` を呼ぶようにし、展開先フォルダの存在を保証。
  - `could not chdir` 等の stderr を検出し、具体的な日本語エラーメッセージ（「展開先フォルダへ移動できませんでした」等）を返すように改良。
- **`ArchiveExtractService` のエラーハンドリング更新**:
  - `TarFallbackService` から返される具体的なエラー内容を判定し、ディレクトリ起因の場合は「暗号化や分割アーカイブ」という誤った推測を避けて表示するように修正。
- **`MainForm` のエラー表示更新**:
  - 一括解凍経路（通常 Unpack）において、`TarFallbackService` のエラーを詳細に表示するよう MessageBox ロジックを修正。

### 実機確認結果
- **展開の成功**: 7-Zip なし環境で、`debug.7z` および `ExCSS.dll.7z` の展開が正常に完了することを確認。
- **引数の最適化**: bsdtar の警告 (`ignoring option -a in mode -x`) が出なくなり、ログがクリーンになったことを確認。
- **ディレクトリ作成保証**: 展開先フォルダが存在しない状態からでも、`tar.exe` が `could not chdir` エラーを出さずに正常に展開を開始することを確認。
- **エラー表示の適切さ**: 展開先が作成不能な場合（読み取り専用メディア等）に、具体的な日本語メッセージが表示されることを確認。

## 2026-05-16 / Phase: Tar fallback service implementation

### Status
Runtime verified; closed.

### 目的
- Windows 11 24H2 以降の `tar.exe` (bsdtar) を活用し、7-Zip 未導入環境でも 7z / TAR / RAR 等の主要アーカイブ操作を可能にする fallback 経路を実装。

### 変更点
- **`TarFallbackService` の新設**:
  - `tar.exe` (bsdtar) を呼び出し、作成 (`-acvf`)、展開 (`-axvf`)、一覧取得 (`-tf`) を行うサービスを実装。
- **UI 統合**:
  - `PackDialog` の形式選択拡張、`ArchiveListService` での一覧取得 fallback、`ArchiveExtractService` での展開 fallback を実装。

### 実機確認結果
- **7-Zip なし環境での動作**: `tar.exe` fallback により、7z / TAR の作成・展開、および RAR の展開が正常に動作することを確認。
- **プレビュー (Archive Contents)**: `tar -tf` による一覧取得および個別解凍が実用可能なレベルで動作することを確認。
- **ReadOnly ガード**: fallback 経路においても ReadOnly タブでの解凍操作が確実にブロックされることを確認。
- **パスの安全性**: 日本語や空白を含むファイル・フォルダパスが正しく処理されることを確認。

### 目的
- Windows 11 24H2 以降で標準搭載された 7z / TAR 等のアーカイブ対応を、MidFD の 7-Zip なし環境での fallback として利用できるか調査する。

### 調査結果
- **`tar.exe` (bsdtar) の有用性**:
  - Windows 11 24H2 (bsdtar 3.8.4 / libarchive 3.8.4) において、`tar -acvf` による 7z / ZIP / TAR の作成、および `tar -axvf` による展開が正常に動作することを確認した。
  - `tar -tf` による内容一覧取得も 7z に対して有効。
  - 非対話で実行可能であり、exit code によるエラー判定も可能であるため、MidFD の fallback 経路として非常に有望。
- **`Shell.Application` COM**:
  - Windows 11 の Shell 統合により、7z ファイルを仮想フォルダとして開けることを確認。
  - ただし、非対話・エラー処理・非同期制御の難易度が高いため、CLI ツールである `tar.exe` を優先採用すべきと判断。
- **対応可能形式 (fallback)**:
  - 作成: zip / 7z / tar
  - 展開: zip / 7z / tar / rar
- **制約とリスク**:
  - 暗号化 7z/RAR、分割アーカイブは `tar.exe` では扱えない。
  - 進捗表示がファイルリスト単位（百分率なし）となる。
  - Windows バージョン（bsdtar のバージョン）により対応状況が異なるため、実行前に対応形式を確認するロジックが必要。

### 推奨方針
- 次 Phase で `TarFallbackService` を実装。
- 7-Zip が未設定・未導入の場合に、`tar.exe` のバージョンを確認し、サポートされている形式であれば実行を許可する。
- zip については引き続き `ZipFile` (managed) を優先 fallback とする。


## 2026-05-16 / Phase: archive fallback capability and preview extract guard corrective

### Status
Runtime verified; closed.

### 目的
- ReadOnly タブにおけるアーカイブプレビュー画面からの解凍ガード漏れを修正。
- 7-Zip 未導入環境における `PackDialog` の形式提示を、実用的な fallback 候補 (zip/7z/tar) に限定。

### 実機確認結果
- **ReadOnly ガード**: Archive Contents ダイアログ内の解凍ボタンが無効化され、ReadOnly タブでの書き込みが阻止されることを確認。
- **形式制限**: 7-Zip なし環境で不用意な形式（gzip/bzip2等）が表示されず、安定した fallback 候補のみが提示されることを確認.

### 目的
- Archive Contents (プレビュー画面) からの解凍操作に対する ReadOnly タブ保護を徹底する。
- 7-Zip 未設定環境での PackDialog の選択肢を zip のみに制限し、実行不能な操作を排除する。
- PackDialog の形式選択を安定版向けに整理し、ノイズを削減する。

### 内容
- **ReadOnlyガード (ArchiveContents)**:
  - `ArchiveListDialog` に `isReadOnly` プロパティを追加。
  - ReadOnly タブからの起動時は「選択解凍」「すべて解凍」ボタンを無効化し、ヒントラベルに警告を表示。
  - `MainForm.ExecuteArchiveExtractAsync` エントリポイントに `GuardReadOnlyBrowserTab` を追加。
- **PackDialog 形式フィルタリング**:
  - `PackDialog` のコンストラクタと `Show` メソッドを拡張し、利用可能な形式リスト (`availableFormats`) とヒントテキストを受け取るように変更。
  - 形式 ComboBox をハードコードから動的生成へ変更。
  - `MainForm.ExecutePack` で 7-Zip の存在確認を行い、欠落時は `zip` 形式のみ、存在時は `zip / 7z / tar` を提示するように制御。
- **UI Stability (形式整理)**:
  - 安定版向けのプライマリ形式として `zip / 7z / tar` を採用。
  - 高度・特殊形式（gzip / bzip2 / xz / wim）を UI から非表示にし、単一ファイル圧縮時のエラーメッセージからも `wim` を除外。
- **コードクリーンアップ**:
  - `ExecutePack` 内の重複した 7-Zip 解決ロジックを整理。
  - `PackDialog.cs` の ComboBox 初期化警告を safe cast で解消。

### 検証
- `dotnet build MidFD.csproj`: 成功 (0 warnings)。
- ReadOnly タブで archive プレビューを開いた際、解凍ボタンが無効化されていることをコード・ビルドレベルで確認。
- 7-Zip 欠落環境（設定パス不正時）で、zip 以外の形式が表示されないことをロジック上で確認。


## 2026-05-16 / Phase: bulk move hotpath corrective

### Status
Runtime verified; closed.

### 目的
- 大量Move操作において、ループ内のUIスレッド同期を排除し、I/O性能を最大限に引き出す。
- 計測ログ（Instrumentation）を導入し、ボトルネックの可視化を行う。

### 内容
- **UI同期の除去**: `Task.Run` ループ内での `Invoke(UnmarkPathsInBulk)` を廃止。成功パスを収集し、ループ完了後に一括 Unmark する方式へ変更。
- **計測ログの導入**: `ExecuteMove` に `[MoveHotpath] Summary` を追加し、詳細な実行時間を記録。
- **ログ抑制**: 大件数 move での per-item success ログを抑制。

### 実機確認完了 / closeout
- 1860件および1537件のMoveにおいて、正常終了と計測ログの出力を確認。
- 1860件Move実績: `loopMs=2917 fileMoveCallMsTotal=2069 unmarkApplyMs=1 progressReportMs=0`。オーバーヘッドが 1ms 単位まで削減されている。
- キャンセル時に、それまでに成功したファイルのみが正しくマーク解除されることを確認。

## 2026-05-16 / Phase: bulk file operation hotpath and cancelability balance corrective

### Status
Runtime verified; closed.

### 目的
- 大量削除・大量移動で重くなる hotpath を、既存安全仕様を維持したまま限定補正する。
- 削除は速度だけでなくキャンセル性を維持し、過去の「単一巨大 Shell bulk 実行」へ単純回帰しない。

### 内容
- **Delete hotpath 補正**:
  - 通常 recycle-bin 削除の大件数経路（`>=256`）に、`ShellRecycleBinDeleteService` の chunk 実行（64件）を導入。
  - chunk ごとに `PerformOperations` を区切り、chunk 境界でキャンセル確認する方式に変更。
  - UI反映は chunk 単位の batch flush（`ApplyProgressiveDeleteUiChunk`）に寄せ、per-item UI反映を避ける。
- **Move hotpath 補正**:
  - move progress 更新を 64件または150ms 単位で間引き。
  - mark解除を per-item `UnmarkPath` から bulk chunk 解除（128件または200ms）へ変更。
  - 大件数では `FileOperationService.Move(... suppressLogging: true)` を使い、per-item success ログを抑制。
  - cut-paste move / directory merge move の大件数経路にも同じ logging 抑制を適用。
- **非対象**:
  - Recycle Bin / Managed Trash / UndoRedo 契約、ReadOnlyガード、FeatureGate、settings/json schema、SQLite schema は変更していない。

### 実機確認完了 / closeout
- 大件数 recycle-bin 削除（1,000件超）において、chunk 単位の実行によりスループットが大幅に改善し、キャンセルが正常に動作することを確認。
- MidFD Managed Trash において 454件のバッチ処理（100%成功）を確認。
- 大件数 Move における進捗表示の間引きにより、UI の応答性が維持されていることを確認。

## 2026-05-16 / Phase: first launch profile dialog cancel and tooltip cleanup

### Status
Runtime verified; closed.

### 目的
- 初回起動 profile 選択ダイアログの `キャンセル/×` を、暗黙起動ではなく起動中止へ補正する。
- 設定画面表示時に MainForm 由来 ToolTip/フロートが残る不自然な見え方を解消する。

### 内容
- **初回起動キャンセル補正**:
  - 起動前判定を `Program.cs` 側に寄せ、`キャンセル/×` 時は `MainForm` を起動せず通常終了する。
  - `キャンセル/×` 時に `settings.json` へ profile を保存しない。
  - 初回選択ダイアログの `キャンセル` ボタンで `PracticalStable` を暗黙選択する挙動を削除。
- **設定表示時の ToolTip 残留補正**:
  - `OpenSettingsForm()` 入口で `HideTransientOverlaysBeforeModalDialog()` を呼び、Command hint overlay とヘッダーToolTipを明示的に `Hide` する。
- **非対象**:
  - FeatureGate 対象機能、PracticalStable/Full の機能契約、settings/json schema、SQLite schema は変更していない。

### 実機確認完了 / closeout
- Profile未設定時に「MidFD 利用モードの選択」ダイアログが表示されることを確認。
- 「実用安定版（推奨）」と「高度機能α版」の表示を確認。
- `キャンセル/×` の挙動、実用安定版/高度機能α版の選択導線を確認。
- SettingsFormで「実用安定版（推奨）」「高度機能α版」の表示を確認。
- 設定画面上にメイン画面由来のパスToolTip/フロートが残らないことを確認。

## 2026-05-16 / Phase: first launch profile selection and alpha labeling

### Status
Build verified; runtime verification pending.

### 目的
- profile 未設定時に暗黙で `Full` 起動しないようにし、通常利用者向けの正式導線を初回選択と設定画面へ統一する。
- 内部profile値は維持したまま、ユーザー向け表示を `実用安定版（推奨）` / `高度機能α版` に整理する。

### 内容
- **未設定時挙動の変更**:
  - `AppSettings.Profile` の既定値を空文字へ変更し、未設定を判定可能にした。
  - 起動時に `Profile` が未設定/不正で、かつ `--profile` の有効指定がない場合は初回選択ダイアログを表示する。
- **初回選択ダイアログ追加**:
  - `MidFD 利用モードの選択` ダイアログを追加。
  - `実用安定版（推奨）` / `高度機能α版` の説明と開始ボタンを実装。
  - キャンセル時は `PracticalStable` を採用し、`settings.json` へ保存する。
- **SettingsForm表示名更新**:
  - 起動/復元タブの profile 選択肢を `実用安定版（推奨）` と `高度機能α版` に変更。
  - 内部保存値は `PracticalStable` / `Full` のまま維持。
- **起動引数の扱い**:
  - `--profile` は開発補助として維持。
  - 有効値指定時のみ優先し、不正値は通常導線（保存設定または初回選択）へ戻す。
- **非対象**:
  - FeatureGate対象機能の追加/削除、保存形式変更、SQLite schema変更、通常ファイル操作仕様変更は行っていない。

## 2026-05-16 / Phase: practical stable profile and feature gate integration

### Status
Runtime verified; closed.

### 目的
- 単一ソースのまま `Full` と `PracticalStable` を切り替え、公開初期版では高度機能の導線を閉じられるようにする。
- `Full` の既存挙動を維持しつつ、PracticalStable は通常ファイラ機能を残す。

### 内容
- **FeatureProfile / FeatureGate 追加**: `FeatureProfile` / `FeatureId` / `FeatureProfileService` / `FeatureGateService` を追加し、既定 profile を `Full` に固定。
- **設定と起動引数**: `AppSettings.Profile`、`SettingsForm` の profile 選択、起動引数 `--profile Full|PracticalStable` を追加。
- **PracticalStable の gate 適用**:
  - Workspace Snapshot 導線をメニュー・コマンド・実行入口で遮断。
  - MarkSlot 集合演算と backup export/import を dialog・実行入口で遮断。
  - 画像減色と SVG コピーを Browser / ImageViewer の導線で遮断。
  - Command Palette の recent / favorite 利用と FileSystemWatcher 自動追従を停止。
- **PracticalStable の既定差分**: `Input.EnableMouseGestures` が settings に明示されていない場合だけ既定 OFF にする。
- **MidFD2 残存整理**: 現行コードと現行ドキュメントの `MidFD2` 表記を追加で `MidFD` へ補正し、履歴ログの記載はそのまま残した。
- **実機確認完了 / closeout**:
  - PracticalStable 起動、Workspace Snapshot 導線非表示、外部Diff導線無効、MarkSlot基本導線維持を確認。
  - ImageViewer の SVG表示維持と、減色 / SVGコピー導線の遮断を確認。
  - Command Palette 候補が PracticalStable 向けに絞られていることを確認。

## 2026-05-16 / Phase: project identity normalization to MidFD

### Status
Build verified; closed.

### 目的
- 新しい正本リポジトリ `MidFD` に合わせて、初期実装名として残っている `MidFD2` を撤去し、プロジェクト内部名およびUI表示名を `MidFD` に統一する。
- 挙動変更、機能削減、FeatureProfile分解、MainForm分割などは含めない。

### 内容
- **実行ファイル/プロジェクト名の変更**: `MidFD2.csproj` を `MidFD.csproj` にリネームし、`PROJECT_CONTEXT.md` などの参照を更新。
- **名前空間の統一**: すべてのC#ファイルにおいて `namespace MidFD2` を `namespace MidFD` に、`using MidFD2` を `using MidFD` に置換。その他の内部の `MidFD2.` 参照も更新。
- **UI表示の変更**: MainForm のタイトルや、実行時例外ロガー内の `MidFD2` 表記を `MidFD` に変更。
- **過去文書**: `Docs/07_change_log_for_ai.md` 内の過去の実行履歴ログの `MidFD2.csproj` などの記載は、履歴としての意味を保つため全置換せず残した。
- **検証**: `dotnet build .\MidFD.csproj` が成功することを確認。
## 2026-05-09 / Phase: workspace state canonical cleanup / legacy session mirror reduction

### Status
Runtime verified; closed.

### 目的
- 状態管理を `StoreActiveBrowserTabCategorySessionState` に集約し、`BrowserTabRestoreSnapshot` への更新を主軸とする。
- 従来の `OpenTabs` 等の legacy mirror の積極更新を抑制しつつ、後方互換とフェイルセーフのためにfallback構造を残す。

### 内容
- **実機確認済**: SQLite WAL側(workspace.db-wal)の更新を確認。
- **実機確認済**: legacy mirrorの積極更新が抑制されていることを確認。
- **実機確認済**: 再起動でのタブ・ロック・フィルタ状態復元、Snapshot/MarkSlot回帰なしを確認。


## 2026-05-08 / Phase: image quantization dither quality improvement
### Status
Runtime verified; closed.

### 内容
- イラスト画像等の減色品質を改善するため、パレット生成と誤差拡散ロジックを刷新。
- **Weighted Median Cut / 知覚距離**: 金髪や肌色の赤変を抑えるため、ピクセル頻度累積中央分割と重み付き距離計算(R:3,G:4,B:2)を導入。
- **Atkinson / 蛇行走査**: 自然プリセットを Atkinson 誤差拡散へ変更し、蛇行走査に対応することで不自然な縞模様を解消。
- **動的強度調整**: 色統合レベルや色数に応じてディザ強度を自動減衰させ、粒子感と面整理を両立。

## 2026-05-08 / Phase: SVG image loading responsiveness improvement
### Status
Runtime verified; closed.

### 目的
- SVG読み込み時の「固まった」感覚を解消し、読み込み中であることを視覚的に明示する。
- 高速な画像切り替え時の古い結果による上書きを防止する。

### 変更内容
- **読み込み開始時のUIフィードバック**:
    - 読み込み開始時に前の画像をクリア。
    - ビューア中央に「Rendering SVG...」等の待機ラベルを表示。
    - ステータスバーに処理状況を表示。
- **画像読み込み処理の全面非同期化**:
    - SVG以外を含む全ての画像読み込みを Task.Run 経由に変更し、UIスレッドのフリーズを防止。
- **世代管理の徹底**:
    - _loadRequestId を用いた世代チェックを画像読み込み全体に適用。古いリクエストの結果を確実に破棄。
- **エラーハンドリングの改善**:
    - SVG読み込み失敗時に、具体的で分かりやすいメッセージを表示するように変更。
- **UI制御**:
    - 読み込み中は「減色」メニューを無効化。

## 2026-05-08 / Phase: SVG clipboard export / Office paste interoperability
### Status
Runtime verified; closed.

### 目的
- SVGファイルをクリップボード経由でOfficeアプリ（Word/PowerPoint等）へベクターオブジェクトとして高品質に貼り付け可能にする。
- 非対応アプリ向けに画像形式のフォールバックも同時に提供する。

### 変更内容
- **SVGクリップボード連携の実装**:
    - `SvgClipboardExportService` を導入し、`image/svg+xml` プライマリ形式と PNG/Bitmap フォールバックを同時格納。
    - .svgz ファイルの自動展開コピーに対応。
- **UI連携**:
    - 画像ビューアおよびブラウザ右クリックメニューに「SVGをコピー」コマンドを追加。
    - メニューの表示・有効化条件をSVG形式時のみに限定。

### 検証
- **実機確認**: プレーンなSVGおよびSVGZファイルをコピーし、PowerPointへベクターオブジェクトとして貼り付けできることを確認済み。

## [2026-05-07] 圧縮ダイアログへの Alt 導線（アクセスキー）の追加
### 目的
キーボード操作による効率化のため、圧縮ダイアログの各項目に Alt キーでアクセスできるショートカット（アクセスキー）を追加する。

### 変更点
- **個別圧縮へのアクセスキー追加**:
  - `個別圧縮` を `個別圧縮(&I)` に変更。
- **全主要項目へのアクセスキー追加**:
  - 出力先フォルダ(&D)、archive ファイル名(&N)、形式(&F)、圧縮率(&C)、分割サイズ(&S) をそれぞれ設定。

### 変更ファイル
- `src/MidFD2/Dialogs/PackDialog.cs`

### 検証
- Build verified.
- ダイアログ内のテキストにアンダーラインが表示され、Alt キーとの組み合わせでフォーカスが移動する実装を確認。
- **Status**: Runtime verified; closed.

## [2026-05-07] 個別圧縮時の混在チェックと確認ダイアログの追加
### 目的
フォルダとファイルが混在して個別圧縮される際に、ファイルの扱いをユーザーが選択できるようにする。

### 変更点
- **混在チェックとダイアログ表示**:
  - 個別圧縮の開始前にフォルダとファイルの両方が含まれているか判定。
  - 混在している場合、ファイルも個別に圧縮するか（はい）、フォルダのみ対象にするか（いいえ）を確認するダイアログを表示。
- **フィルタリング処理**:
  - 「いいえ」が選択された場合、処理対象リストからファイルを除外し、フォルダのみをループ処理するように変更。

### 変更ファイル
- `src/MidFD2/MainForm.cs`

### 検証
- Build verified.
- 混在時のフィルタリングロジックの実装を確認。
- **Status**: Runtime verified; closed.

## [2026-05-07] 個別圧縮時の既存ファイル衝突警告の抑制と文言統一
### 目的
個別圧縮を選択する際、デフォルトのアーカイブ名（フォルダ名.zip）との衝突警告が不要に出るのを防ぎ、UI文言を「個別圧縮」に統一する。

### 変更点
- **既存チェックのバイパス**:
  - 個別圧縮時は、一括圧縮用の `CheckFileCollision` をスキップし、個別のループ処理内での判定に委ねるように変更。
- **文言の統一**:
  - ダイアログおよびメニューの表記を「フォルダごとに個別圧縮」に統一。

### 変更ファイル
- `src/MidFD2/MainForm.cs`
- `src/MidFD2/Dialogs/PackDialog.cs`

### 検証
- Build verified.
- **Status**: Runtime verified; closed.

## 2026-05-07 / Phase: 7-Zip archive workflow enhancement corrective / context menu placement and ReadOnly guard
### Status
Runtime verified; closed.

### 目的
- 直前フェーズ `7-Zip archive workflow enhancement` の実機確認で判明した不整合を補正し、UIの安定化とReadOnlyタブの保護を徹底する。

### 変更内容
- **UI 表示不整合の修正**:
    - `PackDialog` の「対象:」プレフィックスの重複を解消。コード側 (`MainForm.BuildPackSelectionSummary`) での付与を止め、ダイアログ側の `Text` プロパティでの表示に一本化した。
    - 選択項目なし等の初期表示を「不明」へ変更。
- **コンテキストメニューの配置整理**:
    - 「フォルダごとに個別圧縮...」が「送る(SendTo)」階層に混入していた問題を修正。
    - ブラウザの右クリックメニューにおいて、7-Zip がインストールされている場合は 7-Zip サブメニュー内へ、未インストール（または 7-Zip CLI 経由）の場合はブラウザメニューの圧縮・解凍セクションへ、「圧縮...」「解凍...」「フォルダごとに個別圧縮...」を一括して統合。
- **ReadOnly タブのガード強化**:
    - ブラウザの右クリックメニューにおける圧縮・解凍系アイテム (`packItem`, `unpackItem`, `packEachFolderItem`) に対し、ReadOnly タブでは `Enabled = false` になるよう一括制御を追加。
    - 解凍処理のエントリポイント `ExecuteUnpack` に `GuardReadOnlyBrowserTab("解凍")` を追加し、書き込み操作を確実にブロック。
- **7-Zip バイナリ解決のロバスト化**:
    - `SevenZipService.ResolveCliExecutable` を使用するように修正。設定パスの妥当性確認と自動検索を適切に組み合わせることで、起動失敗を抑制。

### 検証
- `dotnet build MidFD2.csproj`: 成功。
- **実機確認**:
    - PackDialog の対象表示が `選択中 1.png` のように正しく（重複なく）表示されることを確認。
    - 右クリックメニューの 7-Zip セクションに全ての圧縮操作が統合されていることを確認。
    - ReadOnly タブにおいて右クリックメニューの圧縮操作が無効化されていることを確認。
    - ReadOnly タブでの解凍実行がガードされ、エラーメッセージが表示されることを確認。

## 2026-05-07 / Phase: ReadOnly tab write operation guard coverage corrective
### Status
Runtime verified; closed.

### 目的
- 既存の ReadOnly タブ機能において、ユーザー実機確認で判明した「書き込み系操作の Guard 漏れ」を最小差分で補正すること。
- ReadOnly タブの意図である「閲覧専用（書き込み不可）」をシステムとして徹底する。

### 変更内容
- `MainForm.cs` において、以下の書き込み・変更を伴う操作の入口に Guard (`GuardReadOnlyBrowserTab` または `IsActiveBrowserTabReadOnly`) を追加した。
  - **Cut (切り取り)**: `ExecuteClipboardCut`。Paste により元ファイルが消失するためブロック。
  - **ATTR (属性変更)**: `ExecuteAttribute`。ファイルシステムの属性書き換えにあたるためブロック。
  - **Drag-in**: `BrowserPanel_DragEnter` と `BrowserPanel_DragDrop`。外部からの D&D コピーや画像取り込みは配下への書き込みにあたるためブロック。
  - **Pack (圧縮)**: `ExecutePack`。現在のパスにアーカイブファイルを作成するためブロック。
  - **Edit (外部エディタ)**: `ExecuteOpenWithEditor`。エディタ起動自体は閲覧に見えても、保存でファイル変更が可能なためブロック。
- 既存の Copy, View, Preview, Mark, Drag-out Copy など、元ファイルやディレクトリに変更を加えない操作は従来どおり許可されることを維持。

### 検証
- `dotnet build MidFD2.csproj`: 成功。
- **実機確認**: ReadOnlyタブで上記操作がブロックされ、通常タブでは正常に機能し、その他の既存機能（Copyなど）に影響がないことを確認。

## 2026-05-07 / Phase: repo state hygiene / backlog and agent instruction cleanup
### Status
Static verified; closed.

### 目的
- 新機能追加ではなく、AI開発継続時に次フェーズ候補や実装済み機能、保留事項を誤認しない状態を作ること。
- ドキュメントやリポジトリの正本状態（hygiene）を整理する。

### 変更内容
- **AGENTS.md のノイズ除去**:
    - ファイル冒頭の会話由来の文章を削除し、純粋な運用ルール・指示書としての役割を明確化。
- **stateファイルの正本整理**:
    - `current_focus.md`: 現在のフェーズ情報を本整理作業に更新。
    - `phase_backlog.md`: 実装済み機能（タブ固定 / ReadOnly / Workspace Snapshot / 外部変更検知）を再候補化しない旨を注記。次候補を空（具体要件待ち）とした。
    - `open_questions.md`: 「Watchlist / Deferred / 仕様・判断保留」のセクションを追加し、`(None)` であることを明記。
    - `decision_log.md`: 今回の整理の理由と判断を追記。
- **Git管理対象外の確認**:
    - `.dotnet/` などのローカル生成物が Git に含まれていないかを確認し、除外方針を適用。

### 検証
- **実機確認**: アプリケーション機能の変更がないため不要 (Static verified)。
- **Git管理確認**: `git ls-files .dotnet` 等で管理対象外であることを確認。

## 2026-05-06 / Phase: tab lock / lock root immutability corrective
### Status
Runtime verified; closed.

### 目的
- タブロック（固定タブ）において、子ディレクトリへの移動やタブ切り替えによって「ロックルート（StartupPath）」が意図せず書き換わってしまう不具合を修正する。
- ロックされたタブを作業中ディレクトリで復元できるようにし、利便性を向上させる。

### 変更内容
- **StartupPath の不変性保護**:
    - `MainForm.cs` の `CaptureActiveBrowserTabState` を修正。タブがロック済みかつ `StartupPath` が設定されている場合、UIからの状態同期による上書きをスキップするガードを実装。
- **ナビゲーション状態を維持した復元**:
    - `MainForm.cs` の `TryResolveBrowserTabRestorePath` を改善。
    - ロックされたタブであっても、保存された「現在パス」が「ロックルート」配下にある場合は、ルートへのリセットを行わず現在パスを優先して復元するように変更。
- **包含判定の共通化**:
    - 既存の `IsPathUnderBrowserTabStartupPath` を復元ロジックでも利用し、判定基準を統一。

### 検証
- **ビルド**: `dotnet build MidFD2.csproj` 成功。
- **実機確認**:
    - ロックタブでの子ディレクトリ移動後のタブ切り替えで、場所が維持され、かつロックルート情報（ツールチップ等）が書き換わらないことを確認。
    - アプリ再起動後に、ロックタブがルートではなく作業中だった子ディレクトリで復元されることを確認。
    - ロックルート外への移動操作時に、期待通り新規タブ作成や移動ブロックが機能することを確認。

### 非対象
- ロック解除時の StartupPath 自動クリア（明示的な仕様変更が必要なため、今回は不変性維持に専念）。
- カテゴリ跨ぎのドラッグ＆ドロップ挙動の変更。

## 2026-05-04 / Phase: filter lock / working tab filter foundation corrective / shortcut and datetime picker polish
### Status
Build/static verified; runtime verification pending.

### 目的
- フィルタロック機能の利便性を向上させ、キーボード操作および日時指定のUXを改善する。

### 変更内容
- **キーボードショートカット追加**:
    - `Ctrl+Shift+L` をフィルタロック設定ダイアログの起動ショートカットとして実装。
- **日時指定UIの改善**:
    - `TabFilterLockDialog` において、日付と時刻を個別の `DateTimePicker` コントロールへ分離。
    - 日付 (`yyyy-MM-dd`) と時刻 (`HH:mm`) を直感的に選択可能にし、秒単位を 0 に固定するロジックを維持。
    - ダイアログサイズを拡大し、コントロールの配置を最適化。
- **基盤実装の復旧と統合**:
    - `MainForm.cs` において、誤操作により消失したフィルタロック関連のフィールド、メソッド、メニュー定義、およびショートカット処理を再構成。
    - `CreateDirectoryLoadRequest` および `UpdateHeaderDisplay` への統合を再適用。

### 検証
- `dotnet build MidFD2.csproj`: 成功。
- メニュー項目およびショートカット (`Ctrl+Shift+L`) によるダイアログ起動を確認。
- ダイアログ内での日付・時刻分離および解除ボタンの動作を確認。
- `TabFilterLockService` への正しい日時合成・渡しのロジックを確認。

### 非対象
- フィルタリングロジック自体の変更
- Git Ignore 判定方式の変更
- 状態保存スキーマの変更

## 2026-05-04 / Phase: keyboard navigation polish / locked root and viewer selection shortcuts
### Status
Runtime verified; closed.

### 目的
- 常用キーボードショートカットの利便性を向上させ、モードや状態に応じた直感的な挙動を提供する。

### 変更内容
- **Locked Root Navigation**:
    - ロックタブ（固定タブ）で `\` キーを押した際、ドライブルートではなくタブ固定時のパス (`lockRootPath`) へ移動するように変更。
    - 通常の非固定タブでは従来通りドライブルートへ移動する挙動を維持。
- **Viewer Select All**:
    - 通常のテキスト Viewer において `Ctrl+A` ショートカットを実装し、全文選択を可能にした。
    - 巨大ファイル用の LargeText Viewer では、メモリ保護のため全選択を無効（行単位選択のみ）とする安全策を維持。
- **Browser Tab Navigation**:
    - Browser モードにおいて `Ctrl+Left` / `Ctrl+Right` による「同一カテゴリ内の前後タブ移動」を実装。
    - 全カテゴリを巡回する既存の `Ctrl+Tab` や、`Ctrl+Shift+Left/Right` などの既存ショートカットとの共存を確認。

### 検証
- 通常タブ、固定タブそれぞれの `\` キー挙動を実機確認。
- 通常テキスト Viewer での `Ctrl+A` 動作を確認。
- LargeText で `Ctrl+A` が無視されることを確認。
- Browser での `Ctrl+Left/Right` による同一カテゴリ内タブ切り替えを確認。
- `dotnet build MidFD2.csproj`: 成功。

### 非対象
- 全キーカスタマイズ機能
- 他の Viewer（画像、バイナリ等）での選択拡張
- タブ自体の並べ替えロジック変更

## 2026-05-04 / Phase: mark management / backup export import and workspace scoped operations
### Status
Runtime verified; closed.

### 目的
- マークスロットの管理機能（保存・一括解除・バックアップ・演算）を統合し、実用的な運用を可能にする。
- UI 安定化と操作性の向上を図る。

### 変更内容
- **マークスロット保存導線の整理**:
    - `保存▼` メニューに「現在タブのマークを保存...」「現在カテゴリ全タブのマークを保存...」「Workspace全体のマークを保存...」を追加し、保存対象のスコープを明確化した。
- **スロット管理・バックアップ**:
    - `スロット管理▼` メニュー（旧 `管理▼`）に「選択スロットをエクスポート...」「選択スロットへインポート...」を追加。
    - 「全スロットを一括エクスポート...」「全スロットを一括インポート（全置換）...」を追加し、バックアップ運用をサポート。
- **スロット演算の統合**:
    - スロット演算結果の「現在タブへの適用」を実装し、演算結果から特定のタブ集合のみを復元・反映できるようにした。
    - 演算結果が0件の場合、適用・保存・復元ボタンを無効化するガードを実装。
- **UI Stabilization & Interaction Corrective**:
    - メニュー表示ロジックを修正。毎回 `new` するのではなくフィールドで保持・再利用するようにし、`MouseDown` トリガーに変更することで表示の不安定さを解消。
    - スロット一覧の「表示名」列をダブルクリックした際、直接名前変更ダイアログを開く導線を追加（HitTest により他列の「復元」と分離）。
    - スロット一覧の右クリックメニュー（選択スロット専用：復元、名前変更、入出力、削除）を追加。
    - スロット管理ボタンに `AutoSize` を適用し、文言変更によるレイアウト崩れを防止。
- **メッセージの改善**:
    - `SlotHelpText` および下部サマリーラベルの文言を新しい UI 体系に合わせて刷新し、操作ガイドとしての役割を強化。

### 検証
- 各保存単位（タブ/カテゴリ/全体）でのマーク保存が正常に機能し、スロット一覧へ反映されることを実機確認。
- 全スロットのエクスポート/インポート（全置換）により、バックアップと復元が成立することを実機確認。
- スロット演算の適用（Apply To Current Tab）および 0件時のボタン無効化を確認。
- スロット一覧での右クリック、表示名列ダブルクリックによる各操作の起動を実機確認。
- メニュー表示が連打やフォーカス移動時でも安定していることを確認。
- `dotnet build MidFD2.csproj`: 成功。

### 非対象
- 検索履歴の保存
- マークの SQLite 化
- 無関係な UI レイアウトの刷新

## 2026-05-04 / Phase: command palette / scalable list presentation polish
### Status
Runtime verified; closed.

### 目的
- Command Palette の候補数が増えたときの視認性を改善する。

### 変更内容
- 検索欄が空のとき、カテゴリ見出し（App / Browser / Mark / External）を表示して一覧をグルーピング。
- Corrective: カテゴリ見出しをアコーディオン表示に変更。空検索時は上位3件のみ表示し、Enter/Right/Left/ダブルクリックで展開・折りたたみ可能にした。
- Corrective: カテゴリ順を `App -> Browser -> Mark -> External -> Others` に整理し、External 増加時に Mark が埋もれにくい表示にした。
- Corrective: Command Palette の入力フォーカス方針を検索欄中心に整理。リストをマウス選択しても文字入力は検索欄へ入るようにした。
- Corrective: コンストラクタ実行中のハンドル未作成状態でフォーカス制御が走り InvalidOperationException が発生する問題を修正。
- Corrective: Space は将来の複数語検索 / AND・OR 検索拡張を見据えてカテゴリ展開には使わず、検索入力用に予約。
- Corrective: カテゴリ展開/折りたたみは Enter / Right / Left / ダブルクリックに限定。
- Corrective: スクロールバー表示/非表示による右端表示の横ズレを抑えるため、候補リストの幅計算または縦スクロールバー表示を安定化。
- External tool の `alias / Alt+slot` 補助表示は既存仕様を維持し、一覧内での視認性を改善。

### 検証
- `Ctrl+Shift+P` で Command Palette が正常に起動することを実機確認。
- 空検索時にカテゴリ見出しが表示されることを実機確認。
- App / Browser / Mark / External のカテゴリ順で表示されることを実機確認。
- External が折りたたみ時に上位3件のみ表示され、展開時に全件表示されることを実機確認。
- Enter / Right / Left / ダブルクリックでカテゴリ展開・折りたたみできることを実機確認。
- Space はカテゴリ展開には使わず、検索入力用に予約する方針で確認。
- リストをマウス選択しても文字入力が検索欄へ入ることを実機確認。
- 検索中は見出しなしのフラット表示になることを実機確認。
- スクロールバー状態による右端表示の横ズレが抑制されていることを実機確認。
- Enter / ダブルクリック実行、外部ツール起動、Alt+slot 直起動に回帰がないことを実機確認。

### 非対象
- recent / favorite
- 実行履歴
- external_tools.json schema 変更
- 外部ツール実行ロジック変更

## 2026-05-03 / Phase: command palette / external tool definition editor
### Status
Runtime verified; closed.

### 目的
- `external_tools.json` を手書きせず、MidFD 内の画面から外部ツール定義を追加・編集・削除できるようにする。

### 変更内容
- 外部ツール定義エディタを追加。
- `id` / `displayName` / `alias` / `altSlot` / `executablePath` / `arguments` / `workingDirectory` / `enabled` を編集可能にした。
- `altSlot` の保存時検証を追加。空または1文字英数字のみ許可し、`F/V/G/T/H` は予約キーとして保存不可。
- `altSlot` の重複を全定義で禁止し、Browser 直起動キーの衝突を防止。
- Corrective: `altSlot` の不正値・予約キー・重複検証を強化し、追加/編集ダイアログ時点で早期検出するようにした。
- Corrective: `id` をユーザー編集対象から外し、新規追加時に `external-tool-001` 形式で自動採番するようにした。
- SettingsForm から外部ツール定義エディタを開けるようにした。
- 保存後に Command Palette へ反映されるようにした。

### 非対象
- external_tools.json の schema 変更
- recent / favorite
- 実行履歴
- Fキー割当
- Command Palette の候補数増加時の一覧表示 polish

### 検証
- 設定画面から外部ツール管理ダイアログを開けることを実機確認。
- 既存 `external_tools.json` の定義が一覧表示されることを実機確認。
- 外部ツール定義を追加・編集できることを実機確認。
- 新規追加時に `id` が `external-tool-003` 形式で自動採番されることを実機確認。
- 編集時に `id` がユーザー編集対象にならないことを実機確認。
- `alias` / `altSlot` が Command Palette に表示されることを実機確認。
- `altSlot` の重複が保存前に検出されることを実機確認。
- Command Palette 経由および `Alt+slot` 直起動に回帰がないことを実機確認。

## 2026-05-03 / Phase: command palette / legacy command launcher service model cleanup
### Status
Build/static verified; closed.

### 検証
- `dotnet build MidFD2.csproj`: 成功。
- 旧 CommandLauncher Service / Storage / Model / Dialog の参照が残っていないことを grep で確認。
- 新 Command Palette 側の `CommandLauncherCommand` / ExternalTool 系は維持。
- `external_tools.json` の契約変更は行っていない。
- 実機確認は不要。今回Phaseは未使用内部資産の削除であり、新規ユーザー操作追加ではないため。

### 変更内容
- 未使用の旧 CommandLauncher Service / Storage / Model を削除。
- 新 Command Palette 側の `CommandLauncherCommand` / ExternalTool 系は維持。
- 旧設定ファイルやユーザーデータは削除しない。

### 非対象
- Command Palette UI 再設計
- external_tools.json 契約変更
- GUI編集機能
- recent / favorite

## 2026-05-03 / Phase: command palette / external tool alt slot integration and legacy launcher removal
### Status
Runtime verified; closed.

### 検証
- Command Palette から外部ツールを検索・起動できることを確認。
- `alias` による検索が機能することを確認。
- `altSlot` による検索・補助表示が機能することを確認。
- Browser 文脈で `Alt+slot` による外部ツール直起動が機能することを確認。
- 旧 Command Launcher のメニュー導線 / Ctrl+Alt 導線が表示・起動しないことを確認。
- 組み込み Command Palette コマンドに回帰がないことを確認。

### 変更内容
- `external_tools.json` モデルに optional `alias` / `altSlot` を追加。
- Command Palette 検索対象を `alias` / `altSlot` / `executablePath` まで拡張。
- Browser 文脈で `Alt+slot` による external tool 直起動を追加。
- 予約キー `Alt+F/V/G/T/H` をスロット対象外に固定。
- 旧 CommandLauncher UI 導線（メニュー項目・Ctrl+Alt モディファイア起動）を削除。
- 未参照の旧 UI (`CommandLauncherDialog`, `CommandLauncherAltListDialog`, `CommandHintOverlayForm`) を削除。

### 非対象
- 旧 CommandLauncher 設定の自動移行
- 外部ツール定義 GUI 編集
- recent / favorite / 実行履歴

## 2026-05-03 / Phase: command launcher / external tool quick access foundation
### Status
Runtime verified; closed.

### 導入機能
- **ExternalToolCommandDefinition**: コマンドパレット用外部ツール定義モデル。ID、表示名、実行パス、引数、作業ディレクトリを管理。
- **ExternalToolCommandStorage**: `external_tools.json` の読み書きを行う永続化レイヤー。
- **ExternalToolArgumentTemplateService**: 引数テンプレートの展開機能。`{currentDir}`, `{selectedPath}`, `{selectedName}`, `{markedPaths}`, `{markedPathsFile}` に対応。
- **ExternalToolLauncherService**: `ProcessStartInfo` による外部プロセス起動。一時ファイル (`{markedPathsFile}`) の即時削除を廃止し、7日以上経過した古いファイルを掃除する方式に変更。
- **MainForm Integration**: 外部ツール実行用のブリッジメソッド `InvokeLaunchExternalTool` と実行コンテキスト取得機能を追加。
- **CommandPaletteService Update**: 外部ツールを "External" カテゴリとして自動的に読み込み、組み込みコマンドと統合。

### 影響範囲
- `CommandPaletteService.GetAllCommands` を介して全コマンドが取得されるようになり、コマンドパレット上に外部ツールが表示される。
- 通常の組み込みコマンドの挙動には変更なし。

## 2026-05-03 / Phase: startup exception logging / silent startup failure diagnostics
### Status
Runtime verified; closed.

### 導入機能
- **Startup Exception Hook**: `Application.ThreadException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` を捕捉してログ出力するように修正。
- **StartupExceptionLogger**: 起動時例外に特化した詳細ログ（OS/Version/InnerException含む）を出力するヘルパーを新規作成。
- **Application.Run try-catch**: 起動中の致命的例外を捕捉し、ログパスとともに MessageBox でユーザーに通知する仕組みを導入。

### 影響範囲
- `Program.cs` のエントリポイント周辺。通常起動時の挙動に変更はない。

## 2026-05-03 / Phase: command launcher / built-in command palette foundation
Standard profile の標準導線となる組み込みコマンドパレットの基盤を実装。

### Status
Runtime verified; closed.

### 検証
- `Ctrl+Shift+P` で Command Palette が開くことを実機確認。
- 検索で候補が絞り込まれることを実機確認。
- `現在ディレクトリを再読込` が実行できることを実機確認。
- `現在パスをコピー` / `選択項目のフルパスをコピー` が動作することを実機確認。
- `設定を開く` が動作することを実機確認。
- 既存キーと FunctionKey Profile に回帰がないことを確認。

### 導入機能
- **Command Palette (Ctrl+Shift+P)**: 組み込みコマンドを検索・実行できるダイアログ。
- **起動キー設定**: `AppSettings.Input.CommandLauncherShortcut` を追加。設定画面の「入力」タブから変更可能。
- **初期コマンド**:
    - `現在ディレクトリを再読込`
    - `現在パスをコピー`
    - `選択項目のフルパスをコピー`
    - `設定を開く`
    - `マークスロット管理を開く`

### 変更点
- **InputSettings**: `CommandLauncherShortcut` プロパティを追加。
- **SettingsForm**: 「入力」タブに起動キー選択用 UI を追加。
- **MainForm**:
    - `ProcessCmdKey` にコマンドパレット起動ロジックを統合。
    - コマンドパレットから既存機能を安全に呼び出すためのブリッジメソッドを追加。
- **CommandPaletteService / CommandPaletteDialog**: 組み込みコマンド専用のロジックと UI を実装。

### 回帰防止
- 既存の `Ctrl+R` (Reload), `Ctrl+F` (Filter) 導線は維持。
- FunctionKey Profile (Standard / FDCompatible) の動作に影響なし。
- 外部ツール用 `CommandLauncherDialog` (Ctrl+Alt) は現時点ではそのまま存続。

## 2026-05-03 / Phase: browser header visual polish / information separator line cleanup
### Status
Runtime verified; closed.

### 目的
- Browser compact header 内の不要な内部罫線を整理し、視覚的なノイズを削減する。

### 修正内容
- **Separator Cleanup**:
  - `sepBeforeTopPanel` (Page行-Path行間) は境界線として復活・維持。
  - `sepAfterRow2` (Path行-Item行間) を内部セパレータとして非表示・高さ0に設定。
  - `HeaderLayoutHelper` における `topPanel` の高さ計算を修正し、削除したセパレータ分の隙間を削減。
- **Boundary Preservation**:
  - Item行と一覧領域の境界である `sepAfterRow4` は維持。

### 変更ファイル
- `MainForm.cs`
- `Helpers/HeaderLayoutHelper.cs`

---

## 2026-05-03 / Phase: browser header interaction polish
### Status
Runtime verified; closed.

### 目的
- Browser compact header の Path行 / Item行を、コピー可能な情報パネルとして使えるようにする。

### 修正内容
- **Path Row Interaction**:
  - 左クリックおよび右クリックメニューで現在ディレクトリのフルパスをコピーする機能を実装。
  - Tooltip による省略なしパスの表示。
- **Item Row Interaction**:
  - 左クリックおよび右クリックメニューで選択中項目のフルパス/ファイル名をコピーする機能を実装。
  - Tooltip による省略なしフルパスの表示。
- **Clipboard Helper**:
  - 成功/失敗のステータス通知を伴うクリップボードコピー補助処理の実装。
  - `label.Text`（表示用）ではなく内部状態（`CurrentPath` / アイテムモデル）をコピー元にする。
- **Corrective: content frame bottom border restoration**:
  - `contentFramePanel_Paint` でコメントアウトされていた下辺枠線の描画を復活させ、一覧領域の外郭線を正しく表示するように修正。

### 変更ファイル
- `MainForm.cs`
- `Docs/header_layout_spec.md`
- `.codex/state/*.md`
- `Docs/07_change_log_for_ai.md`

### 非対象
- Mark/Slot/Refresh/FunctionKey のロジック変更
- statusStrip 罫線削除

---

## 2026-05-03 / Phase: browser header chrome compact cleanup
### Status
Runtime verified; closed.

### 目的
- Browser ヘッダを整理し、タイトル行を削除してコンパクトな 3行構成（Page+Path+Item）にする。
- 効果の不明な設定オプションを整理し、UI をクリーンにする。

### 修正内容
- **Compact Header Layout**:
  - `HeaderLayoutHelper.CalculateMetrics` を修正し、`TitleHeaderHeight = 0` とすることでタイトル行を非表示化した。
  - Page行（Page/Drive/Clock）、Path行（左：パス、右：Sort/Mark）、Item行（左：Name/Size、右：Attr/Timestamp）の構成へ整理。
  - 長いファイル名やパスが右側のメタ情報に重ならないように、手動計測による省略表示を実装。
  - 通常ファイルでは、拡張子と `[size]` を可能な限り維持する `prefix…ext [size]` 形式を採用（省略記号を `…` へ変更）。
- **Clock Relocation**:
  - 時計（lblClock）を `titleHeaderPanel` から `headerPanel` (Page行右端) へ移動。
- **Settings UI & Backend Cleanup**:
  - `SettingsForm` から軽量システム情報・軽量表示情報を削除。
  - `HeaderPresentationHelper.InputState` および `MainForm.UpdateInfoPanel` からも対象のシステム取得処理を削除。
- **MarkSummaryCompact Fit (Corrective)**:
  - Markあり時の Path 行右端で MarkSize が見切れる問題に対し、`lblSort` のレイアウト設定を常時適用し、利用可能幅に応じて表示を短縮する `FitMarkSummaryCompact` ロジックを確実に適用。
- **Deferred**:
  - `statusStrip` のカスタムレンダラーによる上端罫線の削除は、起動安定性への悪影響が疑われたため保留とし、レンダラーを取り下げた（Watchlistへ移行）。

### 変更ファイル
- `MainForm.cs`
- `MainForm.Designer.cs`
- `Helpers/HeaderPresentationHelper.cs`
- `Helpers/HeaderLayoutHelper.cs`
- `SettingsForm.cs`
- `.codex/state/*.md`
- `Docs/07_change_log_for_ai.md`
- `Docs/header_layout_spec.md`

### 非対象
- 選択項目詳細レイアウトの変更
- パスコピー機能の追加
- AppSettings プロパティの物理削除

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 設定画面から項目が消えていることを実機確認。
- ヘッダタイトルが消え、時計が Page 行右端に表示されていることを実機確認。
- **Markあり時の Path行右端**: `Mark: 3 MarkSize: 256.71KB` が見切れないことを実機確認。
- **Item行の分離**: 左側に `Name [Size]`、右側に `Attr Timestamp` が正しく配置されていることを実機確認。
- **Watchlist**: statusStrip 上端線は引き続き Watchlist。

## 2026-05-03 / Phase: directory refresh / current path deleted fallback handling
### Status
Runtime verified; closed.

### 目的
- 現在表示中のディレクトリが消失（削除、リネーム、移動など）した際、安全な fallback 先へ自動移動して表示を復旧させる。
- 「監視停止＋通知のみ」だった既存の限定的な挙動を、自動復帰可能な形へ補完する。

### 修正内容
- **Fallback resolution helper**:
  - `TryResolveExistingDirectoryFallback` を追加。消失 path の親、ドライブルート、UserProfile、AppContext.BaseDirectory の順で生存確認を行い fallback 先を決定する。
- **Reload path integration**:
  - `ReloadCurrentDirectory` 内のディレクトリ存在確認ロジックを拡張。消失検知時に fallback 処理を呼び出すようにした。
  - `Ctrl+R` 手動更新、および FileSystemWatcher 経由の自動更新の両方で fallback が機能するようにした。
- **Active state synchronization**:
  - fallback 成功時に `LoadDirectory` を通じてアクティブタブの `CurrentPath`、watcher、ステータスメッセージを同期更新するようにした。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 非対象
- 全タブ常時監視
- ファイル内容変更監視 (Changed/Size/LastWrite)
- Shell notification (ReadDirectoryChangesW) への移行
- タブロック境界の再設計

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 表示中ディレクトリ削除時、存在する親ディレクトリへ自動 fallback することを実機確認。
- `z:\tmp\test\abc\` 表示中に `z:\tmp\test\` を削除し、`z:\tmp\` へ移動することを確認。
- `z:\tmp\abc\def\hij\klm\` 表示中に `z:\tmp\abc\def\hij\` を削除し、`z:\tmp\abc\def\` へ移動することを確認。
- 非アクティブ状態でも fallback 移動を確認。
- fallback 後に外部ファイル追加が即時反映され、watcher 継続を確認。
- fallback 後の `Ctrl+R` 通常再読込が正常に動作することを確認。
- CurrentPath が消失したままの未同期状態は、自動 fallback が先行するため再現できなかった。
- busy / Viewer 中およびロックタブ境界は必要時確認に分離。

## 2026-05-03 / Phase: status strip / bottom status text vertical clipping corrective
### Status
Runtime verified; closed.

### 目的
- 下部ステータス欄の文字下端が 1〜2px 程度欠けて見える視覚的な不具合を修正する。
- 縦方向のレイアウト（高さ・余白）を正規化し、視認性を向上させる。

### 修正内容
- **Status strip height normalization**:
  - `NormalizeStatusLabelLayout` を拡張し、`statusStrip.Font.Height` に 6px 程度の余白を加えた値を `statusStrip.Height` に適用するようにした。
  - 最小高さを 24px とし、極端に小さいフォントでも領域を確保するようにした。
- **Status label padding/margin corrective**:
  - `statusLabel.Margin = Padding.Empty` を設定し、ToolStripItem 特有の不定な余白を排除した。
  - `statusLabel.Padding = new Padding(0, 1, 0, 1)` を設定し、文字の上下に安全なバッファを持たせた。
  - `statusLabel.TextAlign = ContentAlignment.MiddleLeft` を維持しつつ、高さ確保によりベースラインのクリッピングを解消した。
- **Regression guard**:
  - 以前の `viewer status / toolstrip status label bounds corrective` で実装された横方向の表示安定化（Width, Spring, Overflow 制御）をそのまま維持した。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 非対象
- FunctionBar の表示制御
- FunctionKeyProfile 関連
- Viewer プレビューロジック
- NotificationService の再設計

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 実機確認により、下部ステータス文字の縦方向欠けが解消していることを確認。
- `前回のタブ 1 件を復元しました。`、`5 件のマークを解除しました`、`設定を保存しました。` が欠けずに表示されることを確認。
- ウィンドウ幅を広げた状態でもステータス表示に重大な崩れがないことを確認。
- 横方向 bounds 安定化は維持。
- FunctionBar / FunctionKeyProfile / Viewer プレビューロジックは非対象のまま維持。

## 2026-05-03 / Phase: Function Key Profile Option / FunctionBar visibility by profile corrective
### Status
Runtime verified; closed.

### 目的
- Standard profile でも Browser 下部 FunctionBar が表示される違和感を解消する。
- Standard では FunctionBar を非表示、FDCompatible では従来どおり表示する契約へ補正する。

### 修正内容
- **Browser FunctionBar visibility by profile**:
  - Standard profile の Browser では `functionBarPanel` を非表示にし、下部領域を占有しないようにした。
  - FDCompatible profile の Browser では従来どおり FunctionBar を表示するようにした。
  - Viewer 側の既存表示契約は維持し、Browser の Standard だけを非表示対象に限定した。
- **MainForm visibility helper**:
  - `ShouldShowBrowserFunctionBarForCurrentProfile()` と `ApplyFunctionBarVisibilityForCurrentContext()` を追加し、`UpdateFunctionBar()` / `ApplyViewerChromeState()` / `LayoutFunctionBar()` から同一判定を使うようにした。
  - `FunctionBarPanel_MouseClick` と `FunctionBarPanel_Paint` も Standard Browser では実質無効になるようにガードした。
- **Settings apply reflection**:
  - 設定切替後の既存再反映経路で、FunctionBar 可視状態も即時切り替わるようにした。

### 変更ファイル
- `MainForm.cs`
- `Docs/04_keybind_contract.md`
- `Docs/07_change_log_for_ai.md`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`

### 非対象
- Fキーの Action 定義変更
- menu shortcut hint の再設計
- Viewer モードの Fキー契約変更
- QuickAccess / Command Launcher の Fキー割当
- Custom profile 実装

### 検証 (Verification)
- Standard profile の Browser で FunctionBar が非表示になることを実機確認。
- Standard profile で一覧領域が下まで広がることを実機確認。
- FDCompatible profile で FunctionBar が復活することを実機確認。
- 設定切替後、再起動なしで表示状態が反映されることを実機確認。
- 下部ステータス欄の文字欠けは別件として後続候補へ分離。
- `dotnet build MidFD2.csproj`: 成功。

## 2026-05-03 / Phase: Function Key Profile Option / Standard and FD Compatible
### Status
Runtime verified; closed.

### 目的
- 設定画面から `標準` / `FD互換` のファンクションキープロファイルを選択できるようにする。
- `settings.json` に `Input.FunctionKeyProfile` を保存し、FunctionBar 表示、menu shortcut hint、実キー動作を同じ定義から解決する。

### 修正内容
- **設定契約追加**:
  - `Configuration/InputSettings.cs` を追加し、`FunctionKeyProfile` を `Standard` / `FDCompatible` で保存する契約を追加。
  - `AppSettings` に `Input` を追加し、`Clone()` に入力設定の複製を組み込んだ。
- **Function key definition 分離**:
  - `Models/FunctionKeyProfile.cs`、`Models/FunctionKeyAction.cs`、`Models/FunctionKeyDefinition.cs` を追加。
  - `Services/FunctionKeyProfileService.cs` を追加し、profile ごとの F1-F12 定義を `Action` ベースで解決するようにした。
- **MainForm 接続変更**:
  - `ExecuteFunctionKey()` を Fキー番号直switch から `FunctionKeyAction` 解決経由へ変更。
  - `UpdateFunctionBar()` を profile 定義参照へ変更し、Standard では `Help / Ren / Rld / Menu / Top / Btm`、FD互換では従来の `Help / Check / Copy / Edit / Ren / Sort / Filter / Tree / Logd / Unpk / Top / Btm` を表示する。
  - menu shortcut hint を profile 解決ベースへ変更し、実動作と表示のズレを抑制した。
  - `Ctrl+R` と `Ctrl+F` は profile 非依存の共通導線として維持しつつ、F1-F10 は profile ごとに解決するようにした。
  - `F4` の外部Editor 経路も profile を見て判断するようにし、Standard では F4 が Editor 別名として残らないようにした。
- **SettingsForm 追加**:
  - 「入力」タブを追加し、「ファンクションキー割り当て: 標準 / FD互換」を選択できるようにした。
  - OK 時に `Input.FunctionKeyProfile` を保存し、`OpenSettingsForm()` 後の再読込で MainForm へ反映するようにした。

### 変更ファイル
- `Configuration/AppSettings.cs`
- `Configuration/InputSettings.cs`
- `Models/FunctionKeyProfile.cs`
- `Models/FunctionKeyAction.cs`
- `Models/FunctionKeyDefinition.cs`
- `Services/FunctionKeyProfileService.cs`
- `SettingsForm.cs`
- `MainForm.cs`
- `Docs/04_keybind_contract.md`
- `Docs/07_change_log_for_ai.md`

### 非対象
- 全キーの自由カスタマイズ
- QuickAccess への Fキー直接割当
- Command Launcher への Fキー直接割当
- Viewer モードの Fキー再設計
- 単キー `C / M / R / Q` の再設計
- profile `Custom` の実装

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- `FunctionKeyProfileService`、`InputSettings`、`SettingsForm`、`MainForm` の接続 grep を確認。
- Standard / FDCompatible の設定切替、Fキー表示、Fキー動作反映を実機確認済み。

## 2026-05-03 / Phase: directory refresh / active current path auto detection and manual reload
### Status
Runtime verified; closed.

### 目的
- アクティブタブの `CurrentPath` に対する外部ファイル追加・削除・rename を、低負荷で検知して一覧へ反映できるようにする。
- `FileSystemWatcher` の取りこぼしや監視不能パスに備え、`Ctrl+R` で現在ディレクトリを手動再読込できるようにする。

### 修正内容
- **Active path watcher**:
  - `MainForm.cs` に active `CurrentPath` 専用の `FileSystemWatcher` を追加。
  - `IncludeSubdirectories = false`、`NotifyFilter = FileName | DirectoryName` に限定。
  - `Created` / `Deleted` / `Renamed` / `Error` のみを dirty 合図として受け取り、event handler から直接 `LoadDirectory` は呼ばない。
- **Dirty + debounce refresh**:
  - 300ms の `System.Windows.Forms.Timer` で debounce し、連続イベントを 1 回の再読込へ集約。
  - busy 中や Viewer 中は即時再読込せず保留し、`Activated` / Browser 復帰 / file operation 完了後の安全なタイミングで反映。
  - 現在ディレクトリが見つからない場合は自動 fallback せず、status 通知して watcher を停止。
- **Manual reload**:
  - 共通再読込処理 `ReloadCurrentDirectory(...)` を追加。
  - `Ctrl+R` を `ProcessCmdKey` 系の Browser command 経路へ追加し、`R` 単体 rename と分離。
  - 表示メニューに「現在ディレクトリを再読込」を追加し、shortcut 表示を `Ctrl+R` に設定。
- **Selection restore**:
  - 再読込は既存 `LoadDirectory` / `RestoreSelectionState` を再利用し、同名 item 優先、なければ旧 index fallback の既存契約で選択位置を維持。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/decision_log.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `Docs/07_change_log_for_ai.md`

### 非対象
- 全タブ常時監視
- `IncludeSubdirectories=true` の再帰監視
- `Changed` / `LastWrite` / `Size` による file content change 追従
- 差分更新方式
- Shell 通知
- `ReadDirectoryChangesW` 直接実装
- USN Journal
- 現在ディレクトリ削除時の自動親 fallback

### 検証 (Verification)
- ユーザー実機確認により、アクティブタブの `CurrentPath` で外部からのファイル追加・削除・rename が一覧へ反映されることを確認。
- `Ctrl+R` による現在ディレクトリ手動再読込が機能することを確認。
- `R` 単体 rename と `Ctrl+R` reload が衝突していないことを確認。
- Viewer 中の外部変更で Browser へ強制復帰せず、Viewer 解除後に保留更新が反映されることを確認。
- busy 中の外部変更で即時再読込が割り込まず、処理完了後に反映されることを確認。
- 更新後に選択ファイル名とカーソル位置が可能な範囲で維持されることを確認。
- 監視不能なネットワークパスで watcher 初期化失敗時の挙動は未確認。

## 2026-05-02 / Phase: viewer preview / normal text preview boundary alignment
### Status
Implementation complete; runtime verification pending.

### 目的
- 通常テキストプレビューと LargeText プレビューの判定境界を 2MB に統一する。
- 256KB超〜2MB以下のファイルが「通常ファイル扱いなのに全文表示されない（節減される）」状態を解消し、原則全文表示とする。

### 修正内容
- **Threshold Unification**:
  - `PreviewService` に `internal const int LargeTextThresholdBytes = 2 * 1024 * 1024;` (2MB) を定義。
  - `IsLargeFile()` のハードコードされていた閾値を上記定数参照に変更。
  - `MainForm.cs` の通常テキスト読み込み上限 `maxBytes` (256KB) を `PreviewService.LargeTextThresholdBytes` 参照に変更。
- **Comment Cleanup**:
  - `PreviewService.cs` および `MainForm.cs` 内の「256KB境界」という記述を「読み込み上限境界」等へ修正。

### 変更ファイル
- `Services/PreviewService.cs`
- `MainForm.cs`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 実行予定。
- Grep 確認: 「256KB境界」および `256 * 1024` がプレビュー経路から一掃されていることを確認済み。

## 2026-05-02 / Phase: docs/state cleanup / large file preview closeout and next candidate pruning
### Status
Docs-only update; runtime verification not required.

### 目的
- LargeText は表示・検索・コピー・巨大 export まで実機確認済みのため、候補整理を現状認識へ合わせる。
- `first paint follow-up` と `advanced encoding detection` を通常候補から外し、Workspace 系の次候補へ整理する。

### 変更内容
- **Closeout pruning**:
  - `large file preview / first paint follow-up from visual timing` を watchlist / next candidate から外した。
  - `large file preview / advanced encoding detection` を Upcoming から外した。
  - `large file preview / UTF-16 line index support` は必要時候補として残した。
- **State sync**:
  - `current_focus.md` / `open_questions.md` / `phase_backlog.md` / `decision_log.md` の候補一覧を整理。
  - LargeText の実機確認済み範囲を stable / closed 扱いへ寄せた。
- **Next focus**:
  - 次の主候補を Workspace-aware Tabs と Mark/Slot 系へ移動。

### 検証
- `git status --short`: 変更あり。
- 対象 docs/state を目視確認済み。
- `dotnet build MidFD2.csproj`: 実行していない（docs-only のため）。

### 未確認
- なし。今回は候補整理のみ。

## 2026-05-02 / Phase: large file preview / export range correctness corrective
### Status
Runtime verified; closed.

### 目的
- LargeText の巨大範囲保存（ファイル保存フォールバック）において、保存されたファイルの行数が選択範囲と一致しない問題を修正する。
- 保存の完全性を保証し、不完全な保存を「成功」として報告させない。

### 修正内容
- **Range Normalization**:
  - `MainForm.NormalizeCharacterSelectionRange` を導入し、Start/End の前後関係を常に Start <= End に正規化。
  - クリップボードコピー、見積もり、エクスポートの全経路でこの正規化範囲を使用するように統一。
- **Strict Export Verification**:
  - `LargeTextExportResult` を導入し、期待行数 (`ExpectedLineCount`) と実際の書き出し行数 (`WrittenLineCount`) を追跡。
  - `WriteLargeTextCharacterSelectionToFileAsync` 内で `ReadLinesAsync` の戻り行数を厳密にチェックし、不足を `IOException` として扱う。
  - 最終的な行数が一致しない場合はエラーダイアログを表示し、「保存成功」メッセージを出さない。
- **Enhanced Logging**:
  - エクスポート開始時に、範囲、期待行数、ファイル総行数、インデックス数、見積もりサイズをログ出力。
  - エクスポート終了時に、実書き出し行数、先頭/末尾のプレビュー内容をログ出力。
  - 選択範囲取得時 (`TryGetCharacterSelectionRange`) に、Anchor/Caret/Normalized 範囲をログ出力。

### 変更ファイル
- `Controls/LargeFilePreviewControl.cs`
- `MainForm.cs`

### 検証 (Verification)
- **Runtime Verified**: 12,970,467 行規模の LargeText 選択範囲保存を実行し、正常終了を確認。
- **Data Integrity**: 保存されたファイルを外部エディタ（Mery）で開き、行数が期待値と一致すること、および最終行の内容（EOF到達）を確認。
- **Correctness**: 以前発生していた「5,607,421 行付近で保存が止まる」問題が、インデックス待ちまたは読み込み不足のエラー検知によって解消（または正しく報告）されることを確認。
- **Regressions**: 少量コピー、巨大コピー guard（10万行/32MB制限）、ファイル保存 fallback フローが実用レベルで安定動作することを確認。
- `dotnet build MidFD2.csproj`: 成功。

### 変更ファイル
- `Controls/LargeFilePreviewControl.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `.codex/state/open_questions.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 描画エンジンおよび座標変換ロジックの構造的整合性を確認。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `.codex/state/open_questions.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 大量選択時の判定ロジックおよびストリーミング保存ロジックの整合性を確認。

### 変更ファイル
- `Controls/LargeFilePreviewControl.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `.codex/state/open_questions.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- イベント伝播とフラグのデフォルト値変更 (`preserveCharacterSelection = true`) を確認。

### 変更ファイル
- `Controls/LargeFilePreviewControl.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `.codex/state/open_questions.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- `Select-String`:
  - `CharacterSelectionAutoScrollRequested` 接続を確認。
  - `preserveCharacterSelection` フラグの伝播を確認。
  - `ReadLinesAsync` によるファイル読み出しコピー処理の存在を確認。

### 修正内容
- **Remove auto-scroll logic**:
  - `LargeFilePreviewControl` から `Timer` ベースの自動スクロール機能を完全に削除（タイマー、イベント、関連メソッドの物理削除）。
  - `MainForm` 側での `CharacterSelectionAutoScrollRequested` イベント購読を削除。
- **Enforce visible range clamping**:
  - `OnMouseMove` において、文字選択中のドラッグ位置を `UpdateCharacterSelectionCaretFromMouse` 経由で表示中の先頭/末尾行へ確実に clamp するように戻した。
- **Navigation logic normalization**:
  - `MainForm.NavigateLargeFilePreviewAsync` から `preserveLargeTextSelection` 引数を削除し、通常のナビゲーション（表示範囲変更）時は常に選択を解除する契約を維持。
- **Code cleanup**:
  - `SetVisibleLines` 内の不要なマウス位置参照ロジックを削除し、ビルドエラーを解消。

### 変更ファイル
- `Controls/LargeFilePreviewControl.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `.codex/state/open_questions.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- `Select-String`:
  - `CharacterSelectionAutoScroll` 等のキーワードがソースコードから一掃されていることを確認。
  - `NavigateLargeFilePreviewAsync` の引数が元に戻っていることを確認。

## 2026-05-02 / Phase: large file preview / character selection auto-scroll polish (FAILED)
### Status
Failed; violates visible-range contract. Corrected in subsequent phase.

### 目的
- LargeText でドラッグ選択中に自動スクロールを可能にする（※結果的に表示範囲変更に伴う選択クリアが発生し、契約違反となった）。

### 修正内容
- **Auto-scroll implementation**:
  - `LargeFilePreviewControl` に `Timer` ベースの自動スクロールロジックを実装。
  - マウスがコントロールの垂直境界外にある場合、`CharacterSelectionAutoScrollRequested` イベントを発火。
- **Navigation stability**:
  - `MainForm.NavigateLargeFilePreviewAsync` に `preserveLargeTextSelection` 引数を追加。
  - 自動スクロールによる移動時は既存の選択をクリアしないようにガードを実装。
- **Multi-page copy logic**:
  - `LargeFilePreviewControl.TryGetCharacterSelectionRange` を追加し、絶対座標（行/列）での選択範囲取得を可能にした。
  - `MainForm.TryCopyLargeFileCharacterSelectionAsync` を追加。`LargeFileLineReaderService` を使用してディスクから該当範囲のテキストを非同期で読み込み、クリップボードへ転送する。
  - 10万行を超える巨大な選択コピー時は警告ダイアログを表示する安全策を追加。
- **Bug fixes**:
  - `LargeFilePreviewControl` 内の `Timer` の曖昧な参照（`System.Windows.Forms` vs `System.Threading`）を解消。
  - 重複していた `SelectionChanged` イベント定義を削除。

### 変更ファイル
- `Controls/LargeFilePreviewControl.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- `Select-String`:
  - `LargeFilePreviewControl.cs`: `_characterSelectionAutoScrollTimer`, `CharacterSelectionAutoScrollRequested`, `TryGetCharacterSelectionRange`
  - `MainForm.cs`: `NavigateLargeFilePreviewAsync` (with preserve arg), `TryCopyLargeFileCharacterSelectionAsync`, `BuildCharacterSelectionText`

## 2026-05-02 / Phase: large file preview / character selection polish
### Status
Implementation complete; runtime verification pending.

### 目的
- LargeText の本文領域で文字単位の範囲選択とコピーを可能にする。
- 既存の行単位選択・行単位コピー契約を維持したまま、コピー粒度を向上させる。

### 修正内容
- **Character selection state**:
  - `LargeFilePreviewControl` に文字選択アンカー/キャレット状態を追加。
  - 文字選択有無 (`HasCharacterSelection`) と文字選択コピー (`GetSelectedCharacterText`) を追加。
- **Area split**:
  - ガター領域は従来どおり行選択。
  - 本文領域は文字選択として処理。
- **Rendering**:
  - `OnPaint` で文字選択範囲だけ `Highlight/HighlightText` で重ね描画。
  - 優先順は `文字選択 > 行選択 > search hit`。
- **Copy priority**:
  - `TryCopyLargeFileVisibleText` を `文字選択 > 行選択 > 表示中行` の優先順位へ変更。
  - 文字選択時の通知は `選択範囲をコピーしました。`。
- **Selection clear on navigation**:
  - `NavigateLargeFilePreviewAsync` で表示範囲変更時に `ClearSelections()` を呼ぶよう変更。
- **Status contract guard**:
  - `SelectionChanged` から persistent status を更新しない既存契約を維持。

### 変更ファイル
- `Controls/LargeFilePreviewControl.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- `Select-String`:
  - `LargeFilePreviewControl.cs`: `CharacterSelection`, `HasCharacterSelection`, `GetSelectedCharacterText`, `ClearCharacter`, `OnMouseDown`, `OnMouseMove`, `OnMouseUp`, `OnPaint`
  - `MainForm.cs`: `TryCopyLargeFileVisibleText`, `HasCharacterSelection`, `GetSelectedCharacterText`, `ApplyViewerStatusLine`, `SelectionChanged`

### 未確認
- 実機での複数行文字選択の視認性。
- 実機での UTF-8 BOM / CP932 / UTF-8 の status 維持確認。

## 2026-05-02 / Phase: viewer status / toolstrip status label bounds corrective
### Status
Runtime verified; closed.

### 目的
- UTF-8 BOMなしのファイル表示時にステータスバーの情報が見えなくなる問題を修正。
- 調査の結果、原因はエンコーディングやインデックス作成ではなく、`ToolStripStatusLabel` のレイアウト（Bounds）が領域外へ飛んでいたことであった。

### 結論 (Final Findings)
- **ステータス消失の主因は ToolStripStatusLabel のレイアウト問題**: `LabelBounds.X=802` (Width=800時) のように、ラベルが領域外に配置されていたことが真因。
- **ステータス表示遅延も同時に解消**: レイアウトの正規化により、表示の遅延として認識されていた問題も解消した。
- **実機確認済み**: UTF-8 BOMなし (`Enc:UTF-8`)、BOM付き (`Enc:UTF-8 BOM`)、CP932 (`Enc:CP932`) のすべてにおいてステータス表示が安定することを確認。
- **回帰なし**: 検索、コピー、インデックス完了後の表示、他のプレビュー形式などへの影響がないことを確認。
- **Follow-up 降格**: `first paint latency follow-up` は現時点では不要と判断し、Watchlist（再発時候補）へ降格した。

### 修正内容
- **Layout Normalization**:
    - `NormalizeStatusLabelLayout` helper を追加。
    - `statusLabel` のプロパティを調整：`Alignment = Left`, `Spring = true`, `AutoSize = false`, `Overflow = Never`, `TextAlign = MiddleLeft`。
    - `statusStrip.ClientSize.Width` に基づいて `statusLabel.Width` を明示的に設定し、長い文字列は右側でクリップされるようにした。
- **Integration**:
    - `MainForm` コンストラクタでレイアウトを初期化し、`statusStrip.Resize` イベントで再計算するようにした。
    - `ApplyViewerStatusLine` および Browser モードのステータス更新パスに `NormalizeStatusLabelLayout` 呼び出しを追加。
    - `ApplyFontSettings` (フォント変更時) にも呼び出しを追加。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- `Select-String`: `NormalizeStatusLabelLayout` の定義と各所への配線を確認。

## 2026-05-02 / Phase: large file preview / first paint visual timing instrumentation
### Status
Implementation complete; runtime verification pending.

### 目的
- LargeText プレビューの表示遅延（約1秒）の真の原因を特定するため、内部状態の更新タイミングではなく「実際に画面が描画されたタイミング（First Paint）」を計測する。

### 修正内容
- **Control Instrumentation**:
    - `LargeFilePreviewControl` に `FirstContentPainted` イベントを追加。
    - `OnPaint` 内で実際に表示行が存在する場合の初回描画タイミングを検出し、イベントを発火するようにした。
    - `ResetFirstContentPaintMarker()` を追加し、新規ファイル表示時にマーカーをリセットできるようにした。
- **MainForm Instrumentation**:
    - `_largeTextEntryStopwatch` を追加。LargeText 分岐に入ったタイミングでリスタートし、エントリからの絶対経過時間を各ログで確認可能にした。
    - `LargeFilePreviewControl.FirstContentPainted` をサブスクライブし、`[LargeTextFirstPaint]` ログを出力するようにした。
    - `LogViewerStatusRoute` (ステータスバー更新ログ) を拡張し、エントリからの経過時間、および `statusStrip`/`statusLabel` の `Bounds` と `Visible` 状態を含めるようにした。
    - `LogLargeTextEntryTiming` を拡張し、`totalElapsedMs` (エントリからの絶対経過時間) を含めるようにした。

### 変更ファイル
- `Controls/LargeFilePreviewControl.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- `Select-String`: 期待通りの計測・ログコードが各所に挿入されていることを確認。

## 2026-05-02 / Phase: large file preview / immutable line index swap corrective integration fix
### Status
Implementation complete; runtime verification pending.

### 目的
- 前回の `immutable line index swap corrective` において、Service/State 側への実装は行われたものの、MainForm.cs の LargeText プレビュー実経路への接続が不完全だった問題を修正する。

### 修正内容
- **MainForm Connection Fix**:
    - `StartLargeTextFullIndexAsync` を修正し、`BuildLineIndexOffsetsAsync` を使用してローカルインデックスを構築するようにした。
    - インデックス構築完了後、UI スレッドの `BeginInvoke` 内で `state.ReplaceLineOffsets` を呼び出すように変更。これにより、バックグラウンドスレッドでのスワップを防止した。
    - スワップ適用時のガード条件を強化し、現在のプレビュー対象が変更されている場合に古いリクエストの結果を適用しないようにした。
- **Service Cleanup**:
    - 互換用の `BuildLineIndexAsync` に警告コメントを追加し、UI 表示中の LargeText 経路での使用を禁止した。
- **Encoding Integration Check**:
    - LargeText プレビュー時に `state.DetectedEncoding` を使用して `_largeFileControl.SetState` を呼び出すようになっていることを再確認。

### 変更ファイル
- `Services/LargeFileLineReaderService.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- `Select-String`: `MainForm.cs` から `BuildLineIndexAsync` の呼び出しが排除され、`BuildLineIndexOffsetsAsync` + UIスレッドスワップの経路が確立されていることを確認。

## 2026-05-02 / Phase: large file preview / immutable line index swap corrective
### Status
Implementation complete; runtime verification pending.

### 目的
- 巨大ファイルのインデックス作成（full index）中に UI が参照している `LineOffsets` が破壊（Clear/Add）されることで発生していた UI の不安定さ（ステータス消失、スクロールバーリセット等）を解消する。

### 修正内容
- **Immutable Build Pattern**:
    - `LargeFileLineReaderService.BuildLineIndexOffsetsAsync` を追加。状態（state）を直接書き換えずに、ローカルな `List<long>` にインデックスを構築して返すようにした。
- **Atomic UI Swap**:
    - `LargeFilePreviewState.ReplaceLineOffsets` を追加。インデックス構築完了後、UI スレッド側で一括して `LineOffsets` を差し替え、`TotalBytes` や `FirstVisibleLine` を更新するようにした。
- **MainForm Integration**:
    - `StartLargeTextFullIndexAsync` を更新。バックグラウンドでローカルインデックスを構築し、完了後に `BeginInvoke` で `ReplaceLineOffsets` を呼び出すように変更。
    - インデックス作成前後および完了時の詳細なログ（`[LargeTextIndexSwap]`）を追加。

### 変更ファイル
- `Models/LargeFilePreviewState.cs`
- `Services/LargeFileLineReaderService.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。

## 2026-05-02 / Phase: large file preview / defer full index until first paint corrective
### Status
Implementation complete; runtime verification pending.

### 目的
- 巨大ファイルのインデックス作成（full index）が初期描画をブロックし、体感遅延を引き起こしていた問題を解決する。
- UTF-8 BOM ファイルのエンコーディングステータスが画面に反映されない問題を、描画タイミングの補正により解消する。

### 修正内容
- **Initial Paint First**:
    - `UpdatePreviewAsync` 内でフルインデックス作成処理（`BuildLineIndexAsync`）の開始を `BeginInvoke` および `Task.Delay(150)` によって遅延させた。
    - インデックス作成開始前に `ApplyViewerStatusLine("LargeText initial first paint ready")` や `statusStrip.Invalidate/Update` を実行し、初期表示とステータスを確実に描画させるようにした。
- **Deferred Start Helper**:
    - インデックス作成と完了後の更新処理を `StartLargeTextFullIndexAsync` メソッドとして分離した。
- **No detection logic change**:
    - エンコーディング判定ロジック自体は変更していない。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。

## 2026-05-02 / Phase: large file preview / entry timing trace and BOM status corrective
### Status
Implementation complete; runtime verification pending.

### 目的
- first paint 遅延の主犯を推測ではなく実測タイムラインで特定する。
- Browser 選択時の不要 status (`LargeText: Enterで表示`) を除去する。
- UTF-8 BOM 付き LargeText の `Enc:` 欠落を限定修正する。

### 修正内容
- **Browser status cleanup**:
    - `PreviewKind.LargeText && _uiMode != UIMode.Viewer` で `LargeText: Enterで表示` の status 出力を廃止し、早期 return のみへ変更。
- **Entry timing trace**:
    - `LogLargeTextEntryTiming(...)` を追加し、`Stopwatch` ベースで stage ごとの `elapsedMs` をログ化。
    - 記録地点: `UpdatePreviewAsync start`, `after debounce / yield`, `after GetPreviewKind`, `LargeText branch entered`, `after ApplyViewerChromeState`, `before/after DetectLargeTextEncoding`, `before/after ReadFirstLinesQuicklyAsync`, `after _largeFileControl.SetState`, `before/after UpdateLargeFileVirtualDisplayAsync`, `after first ApplyViewerStatusLine`, `after BuildLineIndex started`, `after BuildLineIndex completed`。
- **BOM status corrective**:
    - loading 開始時の一時 status 上書きを外し、persistent status 経路を優先。
    - Detect 後に status 再適用を追加し、`Enc:UTF-8 BOM` の反映を観測しやすく固定化。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 既存警告: `MainForm.MinMaxInfo` の CS0649 警告 4 件。

### 未確認
- 実機の timing log で elapsed が最も跳ねる stage の確定。
- UTF-8 BOM 実ファイルで `Enc:UTF-8 BOM` の再確認。

## 2026-05-02 / Phase: large file preview / explicit viewer open first paint corrective
### Status
Implementation complete; runtime verification pending.

### 目的
- Browser上でのカーソル移動時に重いLargeText読み込みが走るのを防ぐ。
- 明示的にEnterでViewerを開いた時の初回描画待ち（デバウンス等）を排除する。
- UTF-8 BOM付きでも確実にエンコーディングが表示されることを確認する。

### 修正内容
- **Browser selection guard**:
    - `PreviewKind.LargeText` の処理冒頭に `_uiMode != UIMode.Viewer` 時の early return を追加し、実データへのアクセスを遮断。
- **Viewer-only debounce optimization**:
    - `UpdatePreviewAsync` 先頭の `Task.Delay(150)` を、Viewerモード時は `Task.Yield()` に切り替え。
- **UTF-8 BOM observation**:
    - 取得後の `state.DetectedEncodingLabel` が `ApplyViewerStatusLine` で反映される既存経路について再確認（前フェーズのstatus経路見直しにより実質対応済み）。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 実施予定。

## 2026-05-02 / Phase: large file preview / first paint latency polish corrective
### Status
Implementation complete; runtime verification pending.

### 目的
- 改行が少ない巨大ファイルで初回描画前に先頭走査が長引く問題を抑え、first paint までの待機を bounded 化する。

### 修正内容
- **Bounded initial scan**:
    - `ReadFirstLinesQuicklyAsync` に `maxInitialScanBytes`（既定 512KB）を追加。
    - `linesFound<count` に加えて `currentOffset<maxInitialScanBytes` を while 条件へ追加。
- **Bounded first line read**:
    - `ReadLinesAsync` に `maxLineReadBytes`（既定 `int.MaxValue`）を追加。
    - LargeText の indexing 中かつ `LineOffsets.Count <= 1` の場合のみ、初回1行読みを 512KB に制限。
- **MainForm wiring**:
    - `LargeTextInitialScanBytes` / `LargeTextInitialLineReadBytes` 定数を追加し、LargeText 初期表示経路から適用。
- **Scope guard**:
    - `BuildLineIndexAsync` のバックグラウンド継続、`PendingEndAfterIndex` 契約、status/find/copy 契約は維持。

### 変更ファイル
- `Services/LargeFileLineReaderService.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 実施予定（この corrective 反映後に再実行）。

## 2026-05-02 / Phase: large file preview / first paint latency polish
### Status
Implementation complete; runtime verification pending.

### 目的
- LargeText を開いた直後の体感待ちを減らし、Viewer が先に反応して見える状態へ補正する。

### 修正内容
- **First paint fast path**:
    - LargeText 分岐の開始時に `viewerMessageLabel` へ `LargeText 読み込み中...` を即時表示。
    - `state.IsIndexing = true` を先に立て、`ApplyViewerStatusLine("LargeText loading ui shown")` を先行適用。
    - `ShowStatusMessage("LargeText 読み込み中...")` を追加し、待機中の状態を明示。
    - `await Task.Yield()` を挿入し、重い処理前に UI スレッドへ描画機会を渡す。
- **Detection async化**:
    - `PreviewService.DetectLargeTextEncoding(fullPath)` を `Task.Run` 化し、初回表示前の UI ブロッキングを短縮。
- **Scope guard**:
    - index 完了後の既存経路（`UpdateLargeFileVirtualDisplayAsync` / `BuildLineIndexAsync` / navigation / find / copy）は維持。
    - Dock/Z-order や viewerPanel 内 status host には手を入れない。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 既存警告: `MainForm.MinMaxInfo` の CS0649 警告 4 件。

### 未確認
- 実機で「Enter直後の体感遅延が短縮したか」の主観確認。
- indexing 中/完了後の status 表示遷移（`Enc` / `Lines`）の視認確認。

## 2026-05-02 / Phase: large file preview / selection status stability corrective
### Status
Runtime verified; closed.

### 目的
- LargeText で行選択時に外側 status が消える経路を止め、`Enc/Lines` の persistent status を安定化する。

### 修正内容
- **Selection status decoupling**:
    - `_largeFileControl.SelectionChanged` で `ApplyViewerStatusLine()` を呼ばないように変更。
    - `GetViewerStatusLine()` の LargeText 分岐から `Selection:N 行` 表示を削除。
- **Stable repaint nudge**:
    - `ApplyViewerStatusLine()` の `SetPersistent` 後に `statusStrip.Invalidate()` / `statusStrip.Update()` を追加。
- **Scope guard**:
    - copy/find/encoding 判定ロジックは変更せず、選択時 status 経路だけを対象にした。

### 実機確認結果
- LargeText 表示中に `[Viewer] Enc:UTF-8 | Lines:...` が表示されることを確認。
- 行選択しても外側 status が消えないことを確認。
- 行選択後に status が復帰不能にならないことを確認。
- 行選択ハイライトは正常であることを確認。
- ※LargeText 初回表示までの遅延は `first paint latency polish` として後続候補へ分離。

## 2026-05-02 / Phase: viewer layout / large text bounds and external status visibility corrective
### 目的
- LargeText で「読み込み中は表示されるが本文表示後に status が消える」残件を、文字列更新ではなくレイアウト実測で切り分ける。

### 修正内容
- **Layout diagnostics**:
    - `LogViewerLayoutBounds(reason)` を追加し、`statusStrip` / `outerHostPanel` / `contentFramePanel` / `mainAreaPanel` / `viewerPanel` / `_largeFileControl` の Bounds・Visible・Parent をログ化。
    - 画面座標ベースで `_largeFileControl` と `statusStrip` の重なりを `OverlapsStatus` として出力。
- **Observation points**:
    - `SwitchUIMode(Viewer)` 直後。
    - LargeText `SetState` 後。
    - `SetVisibleLines` 前後。
    - deferred final apply 後。
- **Status route logging kept**:
    - `ApplyViewerStatusLine(reason)` / `LogViewerStatusRoute(...)` により status 文字列経路も継続観測。
- **Layout recompute support**:
    - `ApplyViewerChromeState` 後に `contentFramePanel/mainAreaPanel/viewerPanel` の `PerformLayout()` を明示実行。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 既存警告: `MainForm.MinMaxInfo` の CS0649 警告 4 件。
- **Status**: runtime verification pending (Resolved in subsequent 'selection status stability' phase).

## 2026-05-02 / Phase: viewer layout / large text external status finalization corrective
### 目的
- 通常Text では表示できている外側 status 経路を維持したまま、LargeText だけで `Enc:` が出ない残件を最小差分で補正する。

### 修正内容
- **LargeText final status apply**:
    - `UpdateLargeFileVirtualDisplayAsync(...)` で `SetVisibleLines` / `Update` 後に `ApplyViewerStatusLine("LargeText visible lines applied")` を実行。
    - 同メソッド内で `BeginInvoke` による guarded deferred apply（`LargeText deferred final apply`）を追加。
- **Route diagnostics**:
    - `ApplyViewerStatusLine(string reason = "")` に拡張。
    - `LogViewerStatusRoute(...)` を追加し、`UiMode` / `Kind` / `Enc` / `StatusText` / 反映理由をログ出力。
    - `ShowStatusMessage(...)`（LargeText時）と `messageTimer.Tick` 復帰時にも観測ログが残るよう補強。
- **Status text compact for LargeText**:
    - LargeText の status 行は `Enc` と `Lines` を優先した短縮フォーマットに変更。
- **Non-goals respected**:
    - Dock/Z-order 介入、viewerPanel 内 status host 復活、encoding detection / find / copy ロジック変更は実施しない。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 既存警告: `MainForm.MinMaxInfo` の CS0649 警告 4 件。
- **Status**: runtime verification pending (Resolved in subsequent 'selection status stability' phase).

## 2026-05-02 / Phase: viewer layout / status route consistency audit and external status binding corrective
### 目的
- Viewer status の表示経路を外側 `statusStrip/statusLabel` + `NotificationService` に一本化し、通常Text/LargeTextで `Enc:` 表示を安定させる。
- `viewerPanel` 内部 status host と Form 直下 Dock/Z-order 介入の混在を解消する。

### 修正内容
- **Route unification**:
    - `ApplyViewerStatusLine()` は `_notificationService.SetPersistent(GetViewerStatusLine())` のみを使用。
    - `ShowStatusMessage()` は外側通知経路のみを使用し、内部 label への反映を行わない。
- **Internal host removal**:
    - `_viewerStatusLabel` のフィールド、生成、描画設定、色適用、表示切替を撤去。
- **LargeText binding keep**:
    - `GetViewerEncodingStatusLabel()` の LargeText 分岐で `LargeFilePreviewState.DetectedEncodingLabel` 優先を維持。
- **No new layout hack**:
    - `statusStrip.Dock` 強制、`Controls.SetChildIndex(...)`、`statusStrip.BringToFront()` の追加は行わない。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 既存警告: `MainForm.MinMaxInfo` の CS0649 警告 4 件。
- **Status**: runtime verification pending (Resolved in subsequent 'selection status stability' phase).

## 2026-05-02 / Phase: viewer layout / move viewer status back to external status strip corrective
### 目的
- Viewer 内に配置していた専用ステータスラベルを撤去し、ウィンドウ外側の既存 `statusStrip` / `statusLabel` に表示を集約する。
- LargeText 表示時、本文領域とステータス表示が重ならないようにし、表示領域を最大化する。
- レイアウト崩れやステータス消失を防ぐためのロジック安定化とデバッグ性の向上。

### 修正内容
- **Status relocation & Label Removal**:
    - 内部用 `_viewerStatusLabel` フィールドおよび関連する初期化・可視性制御ロジックを完全に削除。
    - `ApplyViewerStatusLine()` の出力を `NotificationService.SetPersistent()` および `statusLabel` へ一本化。
- **Layout optimization**:
    - `LargeFilePreviewControl` (Dock.Fill) が `viewerPanel` 内の全領域を占有するように調整。
    - `ApplyViewerChromeState()` 内で `PerformLayout()` を明示的に呼び出し、レイアウト計算を確定させるようにした。
- **Status Message & Log redirection**:
    - `ShowStatusMessage()` から内部ラベルへの参照を削除。
    - `LogViewerStatusRoute`, `LogViewerLayoutBounds` を導入し、ステータス更新経路とコントロールの境界状態をログ出力するように強化。
    - `ApplyViewerStatusLine` に `reason` 引数を追加し、更新契機を特定可能にした。
- **Selection Status Polish**:
    - LargeText の行選択操作がステータスバー表示を不安定にしていたため、選択変更時のステータス更新を抑制し、行選択情報の表示を整理。

### 変更ファイル
- `MainForm.cs`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- **Status**: Runtime verified; closed.
- **結果**:
    - 通常Text/LargeText/バイナリのいずれのプレビューでも、外側のステータスバーに情報が安定して表示されることを確認。
    - LargeText の最下行が隠れず、パネル全体が描画領域として活用されている。
    - 選択操作やタイマー復帰によってステータス表示が消失・破壊されないことをログおよび実機で確認。
    - Browser 復帰時にステータスバーが正しく Browser 用に戻ることを確認。

## 2026-05-02 / Phase: viewer layout / status strip visibility and stability corrective
### 目的
- LargeText を含む Viewer 表示時の status 可視性を安定化し、Form 直下レイアウト破壊を避ける。

### 修正内容
- **Rollback & Refactor**:
    - `EnsureStatusBarVisible()` からの Dock・Z-order 強制変更を撤去。
    - ステータス表示のホストを Viewer 内部ラベルから外部 `statusStrip` へ完全移設（上記最新フェーズにて完遂）。
- **Visibility contract**:
    - `ApplyViewerChromeState()` で外部ステータスバーの可視性を保証しつつ、内部的な重複表示を排除。

### 変更ファイル
- `MainForm.cs`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- **Status**: Runtime verified; closed.

## 2026-05-02 / Phase: large file preview / large text status bar visibility corrective
### 目的
- LargeText viewer でも通常 viewer と同じ下部 status line が視認でき、`Enc:` 表示が常時確認できる状態にする。

### 修正内容
- **Status visibility guard**:
    - `SwitchUIMode(UIMode.Viewer)` / `SwitchUIMode(UIMode.Browser)` の両方で `EnsureStatusBarVisible()` を呼び、`statusStrip` と `statusLabel` の可視状態、`DockStyle.Bottom`、フォーム直下の Z-order を明示した。
- **Binding consistency kept**:
    - LargeText の `Enc:` は `DetectedEncodingLabel` 優先のまま維持し、表示・検索・コピーの encoding 判定ロジックは変更しない。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 既存警告: `MainForm.MinMaxInfo` の CS0649 警告 4 件。
- **Status**: runtime verification pending (Resolved in subsequent 'move viewer status back to external status strip' phase).

## 2026-05-02 / Phase: large file preview / detected encoding status binding corrective
### 目的
- LargeText の検出済み encoding を、ステータスバー `Enc:` 表示へ確実にバインドする。

### 修正内容
- **Status binding fix**:
    - `GetViewerStatusLine()` の encoding 表示を `GetViewerEncodingStatusLabel()` 経由に整理。
    - LargeText 時は `LargeFilePreviewState.DetectedEncodingLabel` を最優先で使用し、空文字時は `Unknown` を返すよう補正。
    - 通常 Text は `_currentViewerDetectedEncodingLabel`、それ以外は `_viewerEncodingOverride` を維持。
- **Persistent status reset in Browser mode**:
    - `SwitchUIMode(UIMode.Browser)` で `_notificationService.SetPersistent(...)` を使って Browser 用 status を再設定し、Viewer 固有表示の残留を防止。
- **LargeText initial binding timing**:
    - LargeText 初回セットアップ時（`SetState` 直後）に `ApplyViewerStatusLine()` を呼び、初回表示時点で `Enc:` が反映されるよう補強。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 既存警告: `MainForm.MinMaxInfo` の CS0649 警告 4 件。
- **Status**: runtime verification pending (Resolved in subsequent 'move viewer status back to external status strip' phase).

## 2026-05-02 / Phase: large file preview / encoding status bar visibility corrective
### 目的
- LargeText の検出済み encoding label を、画面下部ステータスバーで常時確認できるようにする。

### 修正内容
- **Status bar visibility corrective**:
    - `MainForm` の LargeText 分岐（binary-like / UTF-16 unsupported）で `ClearPreview(...)` 後に `ApplyViewerStatusLine()` を呼び、`DetectedEncodingLabel` を永続ステータス行に反映するようにした。
- **Scope control**:
    - encoding 判定ロジック（`PreviewService.DetectLargeTextEncoding`）や表示・検索・コピー経路の挙動変更は行っていない。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- **Status**: runtime verification pending (Resolved in subsequent 'move viewer status back to external status strip' phase).

## 2026-05-02 / Phase: large file preview / encoding polish
### 目的
- LargeText の表示・検索・コピーで使う文字コード判定を安定化し、UTF-8 BOM / UTF-8 no BOM / CP932 を実用上自然に扱える状態にする。

### 修正内容
- **LargeText encoding detection の追加**:
    - `PreviewService.DetectLargeTextEncoding(...)` を追加し、UTF-8 BOM / UTF-16 BOM / UTF-8 strict / CP932 fallback / binary-like を判定。
- **LargeText state への保持**:
    - `LargeFilePreviewState` に `DetectedEncoding` / `DetectedEncodingLabel` / `HasBom` / `IsBinaryLike` / `IsEncodingUnsupportedForLargeText` を追加。
- **表示・検索・コピーの整合**:
    - `MainForm.GetCurrentViewerEncoding()` で LargeText 時は state の検出結果を優先し、表示・検索で同じ encoding を使用。
    - copy は表示行バッファをコピーする既存経路のため、表示と同一文字列前提を維持。
- **status 反映**:
    - LargeText の status の `Enc:` 表示を、手動 override 表記ではなく検出済み encoding 表示へ変更。
- **安全ガード**:
    - binary-like file は LargeText で開かず警告表示。
    - UTF-16 BOM は line index との整合を優先し、今回は未対応として警告表示。

### 変更ファイル
- `Services/PreviewService.cs`
- `Models/LargeFilePreviewState.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- **Status**: runtime verification pending (Resolved in subsequent 'move viewer status back to external status strip' phase).

## 2026-05-02 / Phase: large file preview / find polish
### 目的
- LargeText 表示中の検索操作を実用レベルに整え、`Ctrl+F` / `F3` / `Shift+F3` で自然に検索・継続検索できる状態へ進める。

### 修正内容
- **LargeText 検索状態の追加**:
    - `LargeFilePreviewState` に直近検索語、検索方向、active hit 行/列/長さ、検索 request id を追加。
- **ストリーミング検索の拡張**:
    - `LargeFileLineReaderService.SearchTextAsync(...)` を拡張し、前方/後方検索で hit 行と列を返すように変更。
    - ファイル全体は読み込まず、行単位の逐次読み込みを維持。
- **Viewer 検索ルーティングの分岐**:
    - `MainForm.ExecuteViewerFind()` / `ExecuteViewerFindNext()` で LargeText を専用分岐し、`Ctrl+F` / `F3` / `Shift+F3` を LargeText 検索へ接続。
- **hit 移動と表示**:
    - hit 行を画面中央寄せで `NavigateLargeFilePreviewAsync` に流し、スクロールバーと表示行を同期。
    - `LargeFilePreviewControl` に active search hit 行のハイライト描画を追加。
- **status / guard**:
    - 検索中、wrap、hit、not found を status 表示。
    - `SearchRequestId` と既存 `_uiMode` / `_currentPreviewTarget` guard を併用し、古い検索結果の後反映を抑止。

### 変更ファイル
- `MainForm.cs`
- `Controls/LargeFilePreviewControl.cs`
- `Models/LargeFilePreviewState.cs`
- `Services/LargeFileLineReaderService.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 既存警告: `MainForm.MinMaxInfo` の未割り当てフィールド警告 4 件。
- **Status**: Runtime verified; closed.
- **Results**:
    - `Ctrl+F` 検索 OK。
    - `F3` 次検索 OK。
    - `Shift+F3` 前検索 OK。
    - hit 行ハイライト OK。
    - スクロールバー / status 追従 OK。
    - not found status OK。
    - wrap 動作 OK。
    - 1GB級ファイル末尾の文言検索 OK。
    - copy polish / 通常 txt preview / 画像 preview / Enter・Esc 復帰への回帰なし。

## 2026-05-02 / Phase: large file preview / copy polish
### 目的
- LargeText 仮想行プレビューで、表示中行コピーだけでなく、必要な行範囲をクリック/ドラッグで選択してコピーできる状態へ進める。

### 修正内容
- **行単位選択状態の追加**:
    - `Controls/LargeFilePreviewControl.cs` に absolute line index ベースの選択状態を追加。
    - クリックで単一行選択、ドラッグで表示中の複数行選択を可能にした。
- **選択描画の追加**:
    - `OnPaint` で選択行の背景と文字色を `SystemColors.Highlight / HighlightText` に切り替えるようにした。
    - 行番号ガターと本文の両方を同じ選択状態で描画し、既存の固定ガター幅と下端クリップ契約は維持。
- **コピー対象の切り替え**:
    - `MainForm.TryCopyLargeFileVisibleText()` を更新し、選択行があれば選択行のみ、なければ表示中行全体をコピーするようにした。
    - コピー内容には行番号を含めない。
- **ナビゲーション時の選択解除**:
    - LargeText の表示位置が変わるナビゲーション時は選択解除するようにし、見えない選択残留を防止。
- **status 反映**:
    - LargeText の viewer status line に選択行数を反映するようにした。

### 変更ファイル
- `Controls/LargeFilePreviewControl.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証 (Verification)
- `dotnet build MidFD2.csproj`: 成功。
- 既存警告: `MainForm.MinMaxInfo` の未割り当てフィールド警告 4 件。
- **Status**: Runtime verified; closed.
- **Results**:
    - クリックによる単一行選択 OK。
    - ドラッグによる表示中複数行の選択 OK。
    - 選択行がある場合の Ctrl+C コピー OK。
    - 選択行がない場合の表示中行全体のコピー OK。
    - コピー内容に行番号が含まれないことを確認。
    - 通常 txt preview / 画像 preview / Enter・Esc 復帰への回帰なしを確認。

## 2026-05-02 / Phase: viewer state / clear preview on tab switch corrective
### 目的
 - タブやカテゴリの切り替え時に、前タブのプレビュー内容が残ってしまう問題を修正し、常に切り替え先のブラウザ画面が正しく表示されるようにする。

 ### 修正内容
 - **Navigation Guard**:
     - `SwitchBrowserTab` および `SwitchBrowserTabCategory` に `EnsureBrowserModeBeforeWorkspaceNavigation()` を追加。遷移前に `Viewer` モードを終了させるように統一。
 - **State Reset**:
     - 遷移前に `ClearPreview()` を呼び出し、UIコントロールの状態を同期的にリセット。
 - **Async Safety**:
     - `UpdatePreviewAsync` / `UpdateLargeFileVirtualDisplayAsync` に `_uiMode == UIMode.Viewer` チェックを追加。バックグラウンド処理完了時に `Browser` モードへ戻っていた場合、描画更新を中断するように修正。
 - **Request Cancellation**:
     - `SwitchUIMode(UIMode.Browser)` 実行時に `_previewCts?.Cancel()` を行い、不要なプレビュー読み込みを即時中断。

 ### 変更ファイル
 - `MainForm.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed.
 - **Results**:
     - 通常テキスト、LargeText、カテゴリ切替のすべてにおいて、MainForm内のプレビュー残像が解消されていることを確認。
     - LargeText のインデックス完了後のガード（_uiModeチェック）が機能し、遷移後に古い内容が復活しないことを確認。
     - ※別窓の画像ビューア (`ImageViewerForm`) は、意図的にタブ切替時も維持する仕様。

## 2026-05-02 / Phase: viewer exit / unified preview flicker suppression corrective
### 目的
 - すべてのプレビュー種類において、Viewer 終了時の視覚的なちらつきを抑制し、ブラウザモードへの移行をスムーズにする。

 ### 修正内容
 - **Logic Generalization**:
     - `LargeText` 専用だった終了前の非表示化処理を、すべてのプレビュー種別に横展開。
 - **Unified Helper**:
     - `HideViewerContentBeforeExit()` ヘルパーを導入。`viewerTextBox`, `viewerPictureBox`, `viewerMessageLabel`, `_largeFileControl` すべてを非表示にし、即時更新を強制する共通ロジックを実装。
 - **Safe State Management**:
     - 表示リソースの破棄は行わず、`Visible` 制御と `Update()` に限定することで、再表示時の安定性を維持。
 - **Integration**:
     - `TryExitViewerToBrowser()` からこの共通ヘルパーを呼び出すように統一。

 ### 変更ファイル
 - `MainForm.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed

## 2026-05-02 / Phase: large file preview / exit flicker suppression corrective
### 目的
 - Viewer を閉じる瞬間に表示位置が先頭行へ一瞬戻るような視覚的なちらつき（flicker）を抑制する。

 ### 修正内容
 - **Early Visibility Control**:
     - `TryExitViewerToBrowser` において、`SwitchUIMode` を実行する前に `_largeFileControl.Visible = false` を実行。
 - **Forced Update**:
     - 非表示設定直後に `Update()` を呼び出し、UIモード切り替えの重い処理が走る前に描画領域を確実にクリアするように修正。
 - **User Experience**:
     - ブラウザ UI が前面に出る前にプレビュー内容を隠すことで、表示データの破棄等に伴う意図しない描画の乱れをユーザーから隠蔽。

 ### 変更ファイル
 - `MainForm.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed.

## 2026-05-02 / Phase: large file preview / viewer exit routing corrective
### 目的
 - `Enter` / `Esc` キーによる Viewer 終了操作が機能しなくなった問題を修正し、確実に Browser モードへ戻れるようにする。

 ### 修正内容
 - **Logic Recovery**:
     - `TryHandleViewerKeyDown` において、リファクタリング中に誤って削除されていた `Enter` / `Esc` による終了ロジックを復旧。
 - **Unified Helper**:
     - `TryExitViewerToBrowser()` ヘルパーを導入し、終了処理の共通化と安全なモード遷移を実現。
 - **Double Routing**:
     - `TryHandleViewerCmdKey` (ProcessCmdKey) にも終了処理を追加。フォーカスが子コントロールにある場合でも、`KeyDown` に先行してコマンドを捕捉可能にした。
 - **Robustness**:
     - プレビューの表示方式（通常 / LargeText / 画像）に依存せず、共通のキー操作でブラウザに戻れる基本契約を再確立。

 ### 変更ファイル
 - `MainForm.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed.

## 2026-05-02 / Phase: large file preview / visible copy routing corrective
### 目的
 - `Ctrl+C` による表示中行コピーが正常にルーティングされない問題を修正し、確実にコピーが実行されるようにする。

 ### 修正内容
 - **Routing Priority Fix**:
     - `TryHandleViewerKeyDown` において、`LargeText` 用の `Ctrl+C` 判定を通常の `viewerTextBox` 判定より前（最優先）に移動。
 - **ProcessCmdKey Integration**:
     - `TryHandleViewerCmdKey` にも `Ctrl+C` ハンドリングを追加。フォーカスが `LargeFilePreviewControl` 等にある場合でも、`KeyDown` に先んじてコマンドを捕捉可能にした。
 - **Logic Consolidation**:
     - コピーロジックを `TryCopyLargeFileVisibleText()` ヘルパーに集約し、例外ハンドリングとログ出力を追加。
 - **Verification Status Update**:
     - 最下行の文字切れ、通常txt/画像プレビューの回帰テスト成功を記録。

 ### 変更ファイル
 - `MainForm.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed.

## 2026-05-02 / Phase: large file preview / bottom clipping and basic copy corrective
### 目的
 - LargeText 仮想行プレビューにおける最下行の描画品質の向上と、最低限のテキスト抽出（コピー）機能を提供する。

 ### 修正内容
 - **Bottom Clipping Fix**:
     - `LargeFilePreviewControl.VisibleLineCount` において、`Math.Floor` を用いてフォント高さに完全に収まる行数のみを算出するように変更。
     - `OnPaint` 内に `ClientSize.Height` に基づく描画ガードを実装し、下端で文字が切れる現象を解消。
 - **Basic Copy Path**:
     - `LargeFilePreviewControl` に `GetVisibleText()` を追加し、現在表示されている全行のテキストを抽出可能にした。
     - `MainForm.TryHandleViewerKeyDown` において `LargeText` 表示中の `Ctrl+C` を捕捉し、クリップボードへのコピー機能を実装。
 - **Refinement**:
     - コピー内容から行番号を除外し、純粋な本文のみをコピーするように調整。
     - 通常のテキストプレビューや画像プレビューへの影響がないことを考慮して実装。

 ### 変更ファイル
 - `MainForm.cs`
 - `Controls/LargeFilePreviewControl.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed.

## 2026-05-02 / Phase: large file preview / index-ready navigation and stable gutter corrective
### 目的
 - 巨大ファイルのインデックス作成中における操作の不確実性を排除し、インデックス完了前後での表示レイアウトの安定化を実現する。

 ### 修正内容
 - **Pending Navigation**:
     - `LargeFilePreviewState` に `PendingEndAfterIndex` フラグを追加。インデックス作成中に `End` キーが押された場合、これを予約状態とし、インデックス完了直後に自動実行する仕組みを `MainForm` に実装。
 - **Fixed Gutter Width**:
     - `LargeFilePreviewControl.OnPaint` において、行番号エリアの幅を 999,999,999（9桁＋カンマ）ベースの固定幅に変更。インデックス完了に伴う桁数増加で本文の表示開始位置（X座標）が左右にずれる問題を解消。
 - **Operation Restraint during Indexing**:
     - インデックス作成中は全体行数が未確定なため、`LargeFilePreviewControl` のスクロールバーを無効（Enabled=false）化し、誤操作を防止。
     - ステータスバーに `(indexing...)` を表示し、バックグラウンド処理中であることを明示。
 - **Safety Measures**:
     - 手動ナビゲーション（Homeや検索ヒット等）が発生した際は、予約されていた `End` ジャンプを自動解除し、ユーザーの最新の操作意図を優先するように修正。

 ### 変更ファイル
 - `MainForm.cs`
 - `Models/LargeFilePreviewState.cs`
 - `Controls/LargeFilePreviewControl.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed.

## 2026-05-02 / Phase: large file preview / atomic visible range update corrective
### 目的
 - 仮想行表示におけるナビゲーション操作時に、スクロール位置（要求）と描画データのズレによる黒画面や表示の不安定さを解消し、原子的な表示更新を実現する。

 ### 修正内容
 - **State Decoupling**:
     - `LargeFilePreviewControl` に `_renderedFirstLine` フィールドを追加。描画時にスクロールバーの値（`FirstVisibleLine`）ではなく、実際に読み込まれたデータの開始行を使用するように変更。
 - **Atomic Update**:
     - `SetVisibleLines(int firstLine, List<string> lines)` にシグネチャを変更。表示行データと開始行をセットで反映し、即座に `Update()` することで表示の整合性を確保。
 - **Re-entrancy Protection**:
     - `_suppressScrollValueChanged` フラグを導入。内部的なスクロールバー値の同期（`UpdateScrollSettings` 等）による不要な `ScrollValueChanged` イベントの発火を抑止。
 - **MainForm Integration**:
     - `UpdateLargeFileVirtualDisplayAsync` において、要求時の `requestedFirstLine` を保存し、データ取得後にコントロールへセットで渡すように修正。

 ### 変更ファイル
 - `MainForm.cs`
 - `Controls/LargeFilePreviewControl.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed.

## 2026-05-02 / Phase: large file preview / navigation repaint synchronization corrective
### 目的
 - 仮想行表示におけるナビゲーション操作（Home, End, PageUp, PageDown）後に表示領域が一時的に真っ黒になり、再描画が遅延する問題を修正する。

 ### 修正内容
 - **Repaint Force**:
     - `LargeFilePreviewControl.SetVisibleLines` において `Update()` を追加し、OSに対して即時の再描画を要求。
     - `MainForm.UpdateLargeFileVirtualDisplayAsync` の完了時にも `Update()` を呼び出し、表示反映を確実にする。
 - **Intermediate State Stability**:
     - `LargeFilePreviewControl.FirstVisibleLine` の setter において `Invalidate()` を追加。データ読み込み待ちの間も（古いデータを使用して）描画を継続させ、黒画面の発生を抑制。
     - `SetVisibleLines` において `null` ガードを追加し、リスト差し替え時の安全性を向上。

 ### 変更ファイル
 - `MainForm.cs`
 - `Controls/LargeFilePreviewControl.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed.

## 2026-05-01 / Phase: large file preview / end key bottom jump corrective
### 目的
 - 仮想行表示における End キー押下時に、一発でファイル末尾（最終行が画面一番下に見える状態）へジャンプできない問題を修正し、スクロールバー下端の挙動と一致させる。

 ### 修正内容
 - **Logic Consolidation**:
     - `LargeFilePreviewControl.GetMaxFirstVisibleLine()` を導入し、末尾位置の計算を `max(0, TotalLines - VisibleLineCount)` に集約。
 - **ScrollBar Synchronization**:
     - WinForms `VScrollBar` の `Maximum` プロパティが `LargeChange` を含む仕様に合わせ、論理最大位置に到達できるよう `Maximum = maxPos + LargeChange - 1` に調整。
     - `FirstVisibleLine` setter で論理最大値によるクランプを厳密化。
 - **MainForm Integration**:
     - `TryHandleViewerKeyDown` において `End` キー押下時にコントロール側の論理最大位置へジャンプするように修正。
     - `PageDown` 時も同様の論理最大位置でクランプするように修正。
 - **Visual Accuracy**:
     - `VisibleLineCount` の計算を `Math.Ceiling` ベースに変更し、部分的に見える行も考慮したページ計算に改善。

 ### 変更ファイル
 - `MainForm.cs`
 - `Controls/LargeFilePreviewControl.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed.

## 2026-05-01 / Phase: large file preview / single global scrollbar virtual line foundation
### 目的
 - ラージファイル表示において、スクロールバーが現在チャンク内しか表さない問題を解消し、スクロールバー1本でファイル全体の位置を表す読み取り専用の仮想行表示（Virtual Line Preview）を実現する。

 ### 修正内容
 - **Large File Specific UI**:
     - `LargeFilePreviewControl` を導入。右側にファイル全体用の `VScrollBar` を備え、中央にテキストを直接描画する。
 - **Line Offset Indexing**:
     - `LargeFilePreviewState` と `LargeFileLineReaderService` を導入。ファイルを開いた際に非同期で行の開始位置（byte offset）をスキャンし、メモリ内に索引を作成する。
 - **Virtual Rendering**:
     - 表示が必要な行（`FirstVisibleLine` から `VisibleLineCount` 分）のみをファイルから `Seek` して読み込み、描画する方式へ移行。
 - **Unified Navigation**:
     - `PageUp` / `PageDown` / `Home` / `End` / `MouseWheel` をすべてファイル全体の行位置操作に統一。
 - **Simplified Search**:
     - 仮想行表示に対応したストリーミング検索（`ExecuteLargeFileSearchAsync`）を実装。ヒットした行へジャンプする。
 - **Status Display**:
     - ステータスバーに表示中の行範囲、総行数、ファイル内位置（%）を表示するように更新。

 ### 変更ファイル
 - `MainForm.cs`
 - `Models/LargeFilePreviewState.cs` (新規)
 - `Services/LargeFileLineReaderService.cs` (新規)
 - `Controls/LargeFilePreviewControl.cs` (新規)
 - `.codex/state/current_focus.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed.

## 2026-04-30 / Phase: Workspace Mark Management / Slot Set Operations Preview and Save-as-Slot
### 目的
 - 2つの Mark Slot を比較・合成し、結果を preview したうえで、必要なら別スロットへ Save-as-Slot できるようにする。

 ### 修正内容
 - **Slot set operation dialog**:
     - `MarkSlotDialog` に `スロット演算...` を追加。
     - 演算専用の `MarkSlotSetOperationDialog` を追加し、Slot A / Slot B / 演算種別 / 保存先スロットを選べるようにした。
 - **Set operations**:
     - `OR` / `AND` / `A-B` / `B-A` / `XOR` を追加。
     - path 比較は `StringComparer.OrdinalIgnoreCase` 相当とし、null / empty path を除外、結果 path は重複除去済み list に正規化。
 - **Preview**:
     - 演算結果を一覧表示し、`現在DIR内` / `外` / `不在` の分類を出す。
     - summary に Slot A 件数 / Slot B 件数 / 演算結果件数を出す。
 - **Save-as-Slot**:
     - preview結果を別スロットへ保存できるようにした。
     - 保存時は現在時刻で slot を更新し、`SourceScope = SlotSetOperation` を記録する。
     - 保存後も現在タブの mark は変更しない。
 - **Restore contract unchanged**:
     - 演算結果を反映したい場合は、保存先スロットに対して既存の `復元` を使う契約を維持。

 ### 変更ファイル
 - `Models/MarkSlotStore.cs`
 - `Models/MarkSlotSetOperationModels.cs`
 - `Services/MarkSlotStorage.cs`
 - `Dialogs/MarkSlotDialog.cs`
 - `Dialogs/MarkSlotSetOperationDialog.cs`
 - `MainForm.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: runtime verification pending (2026-05-01)
 - **実機確認項目**:
     - Slot A / Slot B を選択して `OR` / `AND` / `A-B` / `B-A` / `XOR` の preview ができることを実機確認 OK。
     - 演算結果件数が期待どおりであることを実機確認 OK。
     - `現在DIR内` / `外` / `不在` の分類が preview summary と一覧に表示されることを実機確認 OK。
     - Save-as-Slot で演算結果を別スロットへ保存できることを実機確認 OK。
     - Save-as-Slot 後に現在タブの `MarkedPaths` が自動で変わらないことを実機確認 OK。
     - 保存先 slot を手動で `復元` したときだけ、現在タブへ置換復元されることを実機確認 OK。
     - 結果0件の場合、Save-as-Slot が無効化され、安全に保存不可になることを実機確認 OK。
     - 既存 slot / export-import 済み slot が一覧表示・演算対象・復元で壊れないことを実機確認 OK。

 ### 次候補 (Next Candidate)
 - **large file viewer seamless navigation foundation**: 大容量ファイルビューア基盤。
 - **Workspace Mark Management / Slot Set Operations Apply-to-Current-Tab**: 演算結果の現在タブ直接適用。
 - **Mark Slot Backup Set / All Slots Export Import**: 全スロットの一括エクスポート/インポート。

## 2026-04-29 / Phase: Workspace Mark Management / Mark Slot Export and Import Foundation
### 目的
 - Mark Slot を slot 単位で JSON バックアップ / 移行できるようにし、選択中スロットの export / import 基盤を追加する。

 ### 修正内容
 - **Export format foundation**:
     - `MarkSlotExportDocument` / `MarkSlotExportEntry` を追加。
     - `schemaVersion = 1`, `kind = MarkSlotExport`, `exportedAtUtc`, `appName`, `slot` を持つ 1-slot JSON 形式を定義。
 - **Selected slot export**:
     - `MarkSlotDialog` に `エクスポート...` を追加。
     - 空スロットは export 不可とし、選択スロットの `MarkSlotEntry` を JSON へ保存できるようにした。
 - **Selected slot import**:
     - `MarkSlotDialog` に `インポート...` を追加。
     - JSON から読み取った slot を選択中スロットへ上書き import し、`SlotNumber` は import 先番号を使うようにした。
     - import 後に現在タブへ自動復元せず、slot 保存だけで止める。
 - **Validation / sanitize**:
     - `schemaVersion`, `kind`, `slot`, `paths` を検証。
     - 空文字 path と重複 path を除去。
     - 未知 `SourceScope` は import エラーにせず `不明 / Legacy` 扱いへ正規化。
 - **UI wording**:
     - export/import はスロット内容のバックアップ用であり、インポートしても現在タブは変わらないことをダイアログ文言へ明記。

 ### 変更ファイル
 - `Models/MarkSlotStore.cs`
 - `Services/MarkSlotStorage.cs`
 - `Dialogs/MarkSlotDialog.cs`
 - `MainForm.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: runtime verification pending (2026-04-30)
 - **実機確認項目**:
     - 選択スロットを JSON へ export できることを実機確認 OK。
     - export JSON に `SchemaVersion` / `Kind` / `Slot` / metadata / `Paths` が含まれることを実機確認 OK。
     - export した JSON を選択スロットへ import できることを実機確認 OK。
     - import 後、現在タブの `MarkedPaths` が自動で変わらないことを実機確認 OK。
     - import 後、別途 `復元` を押すと現在タブへ置換復元できることを実機確認 OK。
     - 壊れた JSON / 対象外 JSON を import してもアプリが落ちず、エラーダイアログで安全に中止されることを実機確認 OK。
     - 旧 `markslots.json` 互換が維持されることを実機確認 OK。

 ### 次候補 (Next Candidate)
 - **Slot Set Operations Preview and Save-as-Slot**: スロット間の集合演算。

## 2026-04-29 / Phase: Workspace Mark Management / Category and Workspace Scoped Slot Save
### 目的
 - MarkSlotDialog に `カテゴリ保存...` と `Workspace保存...` を追加し、現在タブ以外に散らばった mark をスロットへ安全に保存できるようにする。

 ### 修正内容
 - **Scoped slot save aggregation**:
     - 集約前に `CaptureActiveBrowserTabState()` と `StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false)` を呼び、active tab を含む snapshot を最新化。
     - `BrowserTabRestoreSnapshot` を集約元にして、現在カテゴリ / Workspace 全体の `MarkedPaths` を横断収集。
     - 保存対象 path は `StringComparer.OrdinalIgnoreCase` で重複除去し、slot には unique path list として保存。
 - **Category scoped slot save**:
     - `カテゴリ保存...` を追加し、現在カテゴリ内の全タブ mark をスロット保存できるようにした。
     - `SourceScope = CurrentCategory` とし、`SourceCategoryId` / `SourceCategoryName` を保存する。
 - **Workspace scoped slot save**:
     - `Workspace保存...` を追加し、全カテゴリ / 全タブ mark をスロット保存できるようにした。
     - `SourceScope = Workspace` とし、カテゴリ / タブ metadata は null のまま保存する。
 - **Confirmation / summary wording**:
     - 保存前ダイアログで raw mark 件数、重複除去後の unique path 件数、復元先が現在タブ置換であることを明示。
     - summary / tooltip は `現在カテゴリ` `全Workspace` を保存元ラベルとして表示する。
 - **Restore contract unchanged**:
     - category / workspace 由来 slot でも、既存 restore は current-tab 置換のまま維持。

 ### 変更ファイル
 - `MainForm.cs`
 - `Dialogs/MarkSlotDialog.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: runtime verification pending (2026-04-30)
 - **実機確認項目**:
     - `カテゴリ保存...` が現在カテゴリ内の全タブ mark だけを保存することを実機確認 OK。
     - `Workspace保存...` が全カテゴリ / 全タブ mark を保存することを実機確認 OK。
     - `SourceScope = CurrentCategory / Workspace` が summary / tooltip に表示されることを実機確認 OK。
     - category / workspace 由来 slot を復元しても、現在タブだけが置換され、他タブ / 他カテゴリが変わらないことを実機確認 OK。
     - 同一 path が複数タブにある場合、unique path として重複除去されて保存されることを実機確認 OK。
     - 既存 current-tab save / restore / legacy 互換が維持されることを実機確認 OK。

## 2026-04-29 / Phase: Workspace Mark Management / Slot Metadata Foundation and Current-tab Restore
### 目的
 - 既存 Mark Slot を per-tab mark 時代に合わせて安全に拡張し、保存元 metadata、current-tab scoped save / restore 契約、restore 後の Workspace SQLite 即時同期を最小差分で導入する。

 ### 修正内容
 - **MarkSlot metadata foundation**:
     - `MarkSlotEntry` に `SourceScope`, `SourceCategoryId`, `SourceCategoryName`, `SourceTabId`, `SourceTabDisplayName` を nullable で追加。
     - `markslots.json` の後方互換を維持し、旧 slot は `SourceScope == null` のまま読み込み、UI 上 `不明 / Legacy` として扱う。
 - **Current-tab scoped save clarification**:
     - 既存 `_markedFiles.Snapshot()` 保存を維持しつつ、save 時に current tab / current category の metadata を記録するよう補強。
 - **Current-tab replace restore persistence sync**:
     - 既存の `ClearMarks(); RestoreMarks(...)` による current-tab only 置換復元を維持。
     - restore 後に `StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: true)` と `SaveWorkspaceStateStore()` を追加し、Workspace SQLite 正本へ即時反映するよう補強。
 - **MarkSlotDialog wording / tooltip update**:
     - 「現在のマーク全件」を「現在タブのマーク全件」に修正。
     - 復元説明を「現在タブへの置換復元」に寄せる。
     - 概要列を `現在タブ / N件` または `不明 / Legacy / N件` ベースに変更し、tooltip に保存元・カテゴリ・タブ・保存日時・件数・復元先を表示。

 ### 変更ファイル
 - `Models/MarkSlotStore.cs`
 - `Services/MarkSlotStorage.cs`
 - `Dialogs/MarkSlotDialog.cs`
 - `MainForm.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed.

 ### 実機確認結果
 - 旧 `markslots.json` がそのまま読めることを確認。
 - 旧 slot が `不明 / Legacy` と表示されることを確認。
 - 旧 slot の tooltip で保存元が `不明 / Legacy`、復元先が `現在タブへ置換` と分かることを確認。
 - current-tab scoped save が他タブの `MarkedPaths` を変更しないことを確認。
 - slot restore が現在タブだけを置換し、他タブ / 他カテゴリを巻き込まないことを確認。
 - restore 後に再起動しても Workspace SQLite から current-tab の復元結果が維持されることを確認。
 - missing path を含む slot restore で、存在する path だけ復元されることを確認。

 ### 次候補
 - `Workspace Mark Management / Category and Workspace Scoped Slot Save`
 - `Workspace Mark Management / Slot Set Operations Preview and Save-as-Slot`

## 2026-04-29 / Phase: Workspace Mark Management / Overview and Scoped Clear Implementation (2026-04-29)
- `MarkSlotDialog` に現在タブ、現在カテゴリ、Workspace全域のマーク件数を表示するサマリーラベルを追加。
 - カテゴリ単位、および Workspace 全域でのマーク一括解除機能を実装 (Confirm ダイアログ付き)。
 - `MainForm` にて `BrowserTabRestoreSnapshot` を用いた横断集計ロジックを実装。
 - 一括解除時にメモリ上の `_markedFiles`、および SQLite 永続化層との同期を保証。
 - ダイアログ内の説明文を補強し、既存の ESC による現在タブ解除動作との整合性を明確化。
 - **BugFix**: 一括解除時に現在カテゴリ内の非アクティブタブ (`_browserTabs`) のメモリ状態がクリアされず、その後の同期でマークが復活してサマリーに残る問題を修正。解除ロジックで `_browserTabs` および Session mirror も確実にクリアするように補強。
 - **BugFix**: 集計ロジック (`BuildMarkGlobalSummary`) において、集計前に最新の状態をマージするように修正し、集計精度を向上。残件特定のためのログ出力を追加。

 ### 目的
 - タブ別マーク独立化に伴う「マークの散らばり」を解消するため、全タブを俯瞰できる一覧機能と、スコープ（タブ/カテゴリ/全域）に応じた一括解除機能を実装・安定化する。

 ### 調査結果
 - **ClearMarks Logic**: `MainForm.ClearMarks` は現在のアクティブタブのみを対象とし、`SyncActiveBrowserTabMarksFromCurrentSelection` で即座に永続化用バッファへ同期する。ESC 解除はこの振る舞いを維持するのが妥当。
 - **Overview Extension**: `MarkSlotDialog.cs` にはすでにマーク一覧とスロット一覧があり、全タブ・全カテゴリの状態を集約している `BrowserTabRestoreSnapshot` を利用することで、容易に Cross-tab Overview を追加可能。
 - **Scoped Action**: スロット保存 ( `MarkSlotEntry` ) にメタデータを追加することで、柔軟な保存・復元を実現する道筋を確保。今回フェーズでは Overview と Scoped Clear に注力し、メタデータ拡張は次フェーズへ送る。

 ### 変更ファイル
 - `MainForm.cs`
 - `Dialogs/MarkSlotDialog.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - **Status**: runtime verification pending (2026-04-29)
 - **実機確認項目**:
     - MarkSlotDialog summary の表示精度を実機確認 OK。
     - カテゴリ解除 / Workspace解除のスコープ制御を実機確認 OK。
     - 全解除後にサマリーが 0件になることを実機確認 OK (Corrective による残存マーク解消を確認)。
     - 再起動後の状態維持を実機確認 OK。
     - Slot Expansion / Metadata 拡張は次候補として backlog へ維持。

## 2026-04-29 / Phase: Workspace per-tab mark restore ordering corrective
### 目的
 - タブ復元ロジックとディレクトリ読み込み UI 同期ロジックの実行順序により、復元された `MarkedPaths` が空の `_markedFiles` で即座に上書きされ、実機上でマークが消失する問題を修正する。

 ### 修正内容
 - **MarkedPaths overwrite suppression**:
     - `CaptureActiveBrowserTabState(bool captureMarks = true)` を導入。
     - `ApplyDirectoryLoadUi` 内での暗黙のキャプチャ呼び出しを `captureMarks: false` に変更。
 - **Restore ordering hardening**:
     - タブ切り替え時（`SwitchBrowserTab`）の流れにおいて、UIが切り替え後タブの状態（マーク等）を反映する前に、現在のUI状態（切り替え前タブの残骸）で切り替え後タブの状態を上書きしてしまう順序矛盾を解消。

 ### 変更ファイル
 - `MainForm.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - **Status**: Runtime verified; closed.

 ### 確認済み
 - 起動復元後のタブ別マーク維持を実機確認 OK。
 - タブ切り替え / カテゴリ切り替え後のマーク独立性を実機確認 OK。

## 2026-04-29 / Phase: Workspace Restore Contract / SQLite Store Foundation
### 目的
 - Workspace-aware Tabs の復元契約を整理し、カテゴリ / タブ / タブ別マークを Workspace 専用 SQLite に保存できる土台を作る。

 ### 修正内容
 - **Restore contract correction**:
     - `RestoreTabsOnStartup` を UI 上「前回の作業状態を復元する」として扱うよう文言整理。
     - 旧 `PersistMarksAcrossRestart` / `PersistedMarkedPaths` は互換 fallback 用に降格し、通常 UI では無効化。
     - `RestoreTabsOnStartup=false` ではタブ構成 / タブ別マーク / legacy marks を通常復元しない契約へ整理。
 - **Per-tab mark synchronization hardening**:
     - `_markedFiles` を現在タブの操作ビューとして維持し、Mark / Unmark / Clear / Restore 時に active tab `MarkedPaths` へ即時同期。
 - **Workspace SQLite Store foundation**:
     - `Services/Workspace` に `IWorkspaceStateStore`, `WorkspaceStateModels`, `SqliteWorkspaceStateStore`, `WorkspaceStateStoreFactory`, `WorkspaceStateMigrationService` を追加。
     - `Data/Workspace/workspace.db` を Workspace 専用DBとして使用。
     - `workspace_meta`, `workspace_categories`, `workspace_tabs`, `workspace_marks` を作成し、WAL / synchronous=NORMAL を設定。
 - **Session migration / fallback**:
     - 起動時は Workspace SQLite を優先。
     - SQLite が空または load 失敗時は既存 `SessionSettings.BrowserTabRestoreSnapshot` fallback を使用。
     - Session fallback から復元できた場合は Workspace SQLite へ保存し、初回移行する。
     - 保存時は Workspace SQLite を正本として更新し、互換期間として Session snapshot も維持。

 ### 変更ファイル
 - `MainForm.cs`
 - `SettingsForm.cs`
 - `Services/Workspace/IWorkspaceStateStore.cs`
 - `Services/Workspace/WorkspaceStateModels.cs`
 - `Services/Workspace/SqliteWorkspaceStateStore.cs`
 - `Services/Workspace/WorkspaceStateStoreFactory.cs`
 - `Services/Workspace/WorkspaceStateMigrationService.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/decision_log.md`
 - `Docs/02_spec_delta.md`
 - `Docs/03_phase_plan.md`
 - `Docs/07_change_log_for_ai.md`
 - `Docs/08_additional_requirements_v0.1.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - 既存警告: `MainForm.MinMaxInfo` の未割り当てフィールド警告 4 件。
 - **Status**: Runtime verified; closed.

 ### 未確認
 - SQLite 初期化 / load 失敗時の Session fallback。

 ### 次の一手
 - Workspace SQLite restore / fallback の実機確認。
 - Workspace-aware Tabs / Navigation Boundary and LockMode Completion。

## 2026-04-29 / Phase: Workspace-aware Tabs Foundation corrective follow-up
### 目的
 - 実機確認で closeout blocker になった、タブ / カテゴリ別マーク分離と ReadOnly 作成系ガード漏れを最小差分で補正する。

 ### 修正内容
 - **Per-tab / per-category mark isolation corrective**:
     - `SwitchBrowserTab` で navigation callback に依存せず、切替成功後に必ず対象タブの `MarkedPaths` を復元するよう補正。
     - 復元済みタブ群に per-tab marks がある場合は、旧 `PersistedMarkedPaths` を復元しないよう補正。
     - 旧 `PersistedMarkedPaths` は per-tab marks が存在しない旧設定向けの後方互換として維持。
 - **ReadOnly create-entry guard corrective**:
     - `ExecuteCreateFile` 入口に ReadOnly guard を追加し、`n` によるファイル作成を抑止。
     - Copy 導線内でコピー先ディレクトリを新規作成する場合に限り ReadOnly guard を追加し、`c` 由来の配下フォルダ作成漏れを抑止。
     - Copy 自体は従来どおり許可。

 ### 変更ファイル
 - `MainForm.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/decision_log.md`
 - `Docs/07_change_log_for_ai.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - 既存警告: `MainForm.MinMaxInfo` の未割り当てフィールド警告 4 件。
 - **Status**: Implementation complete; runtime verification pending.

 ### 未確認
 - タブ別 / カテゴリ別マークが実機で混ざらないこと。
 - ReadOnly タブで `n` ファイル作成と `c` コピー導線内の配下フォルダ作成が抑止されること。
 - StartupPath fallback 時の通知 / ログの実機視認性。

 ### 次の一手
 - Workspace-aware Tabs corrective の実機再確認。
 - Workspace-aware Tabs / Navigation Boundary and LockMode Completion。

## 2026-04-29 / Phase: Workspace-aware Tabs Foundation
### 目的
 - カテゴリ・タブ・マーク・ロック・ReadOnly を、将来の Filter Lock / Workspace Snapshot / Slot Set Operations へ接続できる状態保存基盤へ進める。

 ### 修正内容
 - **Tab Capacity / Persistent Tab Identity**:
     - `BrowserTabSessionState.TabId` を追加し、既存 `BrowserTabState.Id` を永続化。
     - タブ上限をカテゴリ単位 30 件既定、内部安全上限 100 件へ緩和。
     - 保存・復元時の 10 件切り捨てを廃止し、上限超過時のみログ・ステータス通知。
 - **Per-tab Mark Isolation**:
     - `BrowserTabState` / `BrowserTabSessionState` に `MarkedPaths` を追加。
     - タブ切替前に active tab の mark を退避し、切替後に対象タブの mark を復元。
     - 復元時に存在しない mark path を除外し、ログへ記録。
 - **Startup Lock Foundation**:
     - `StartupPath` を追加し、固定タブでは再起動時に `StartupPath` を優先して開く。
     - `StartupPath` 消失時は CurrentPath / 親ディレクトリ / UserProfile / AppContext.BaseDirectory の順でフォールバック。
 - **ReadOnly Guard Foundation**:
     - `IsReadOnly` を追加し、タブ表示 `[RO]` と tooltip で識別。
     - Delete / Rename / Move / Paste / New Folder を ReadOnly タブで抑止。
     - Copy / View / Preview / Mark は許可。

 ### 変更ファイル
 - `Configuration/AppSettings.cs`
 - `Models/BrowserTabState.cs`
 - `MainForm.cs`
 - `.codex/state/current_focus.md`
 - `.codex/state/phase_backlog.md`
 - `.codex/state/open_questions.md`
 - `.codex/state/decision_log.md`
 - `Docs/02_spec_delta.md`
 - `Docs/03_phase_plan.md`
 - `Docs/08_additional_requirements_v0.1.md`

 ### 検証 (Verification)
 - `dotnet build MidFD2.csproj`: 成功。
 - 既存警告: `MainForm.MinMaxInfo` の未割り当てフィールド警告 4 件（既存）。
 - **Status**: Implementation complete; runtime verification pending.

 ### 未確認
 - 11 個以上のタブ作成・保存・復元.
 - タブ別マークの実機切替・再起動復元.
 - StartupPath 消失時フォールバックの実機確認.
 - ReadOnly タブのキー操作・メニュー操作の抑止確認.

 ### 次の一手
 - Workspace-aware Tabs / Navigation Boundary and LockMode Completion。
 - Workspace Snapshot Foundation。

## 2026-04-28 / Phase: file operation stream / MidFD managed trash JSON fallback contract closeout
### 目的
 - Managed Trash の SQLite 運用が安定した現在、JSON fallback を撤去せず、「ネットワーク実行時やSQLite初期化失敗時の安全弁」として維持する契約をドキュメント上（current_focus.md, decision_log.md 等）で明文化・確定する。

 ### 修正内容
 - **コード変更なし**: `MidFdManagedTrashService.cs`, `TrashManifestStoreFactory.cs`, `SettingsForm.cs` において、以下の現在の契約が適切に実装され、ログやUI上のヒント文で説明されていることを確認した。
     - 通常ローカル実行時は SQLite が主経路。
     - ネットワーク実行時は SQLite migration をブロックし、強制的に JSON にフォールバックする。
     - SQLite 初期化例外発生時も JSON にフォールバックする。
 - **ドキュメント整理**: 上記の契約を `decision_log.md` と `open_questions.md` に明文化し、フェーズを Closeout した。

 ### 検証 (Verification)
 - **Status**: runtime verification pending (ドキュメント更新のみ)

## 2026-04-28 / Phase: file operation stream / MidFD managed trash performance investigation
### 目的
 - SQLite 導入やロギング抑制後、1万件超の大量操作における実際のパフォーマンスボトルネック（OS I/O、DB、UI、ロギング）を実機ログから特定し、必要に応じてバックグラウンドタスク化等のさらなる改善を検討する。

 ### 修正内容
 - **観測基盤の補正**:
     - `cancelLatencyMs` の計算式を .NET 8 `Stopwatch.GetElapsedTime` に修正。
     - `TotalFileMoveMs` の計測対象を復元（Undo）にも拡大。
     - ゴミ箱ルートの配置統計（SameVolume/CrossVolume/AppDataFallback）を独立してカウント・出力するように詳細化。
 - **実機検証結果**:
     - **11,906件削除**: `totalMs=19680`, `totalFileMoveMs=5785`, `sameVolume=11906`。
     - **11,906件Undo**: `totalMs=14018`, `fileMoveMs=5599`, `statusUpdateMs=247`, `uiUpdateMs=622`。
     - **ボトルネック分析**: ファイル移動（OS API）が全体の約30%を占め、残りはDB/UI/Loggingに分散しているが、いずれも特定の単一要因が支配的ではない。
     - **キャンセル応答**: Esc 入力後の停止は即時。

 ### 検証 (Verification)
 - **Status**: Runtime verified; closed.
 - **結論**: 現状のシングルスレッド（UIスレッド）ループ＋スロットリング更新の構成で、1万件級の操作が実用的な時間内（20秒前後）で制御可能な状態で完了できていることを確認。

## 2026-04-27 / Phase: build hygiene stream / remove tracked obj artifacts corrective
### Status
Recovery note; original detail unavailable in the recovered source.

### 備考
- 復旧素材上、このエントリの詳細本文は省略表記として欠落していた。
- 正確な本文が確認できるまで、推測補完は行わない。
- 旧本文を発見した場合は、この復旧メモを置換する。

## 2026-05-04 / Phase: mark management / backup export import and workspace scoped operations (supplemental)
### 目的
- 全スロット backup export/import と scoped mark clear、および slot set operations の現在タブ直接適用を同一Phaseで実用化する。

### 修正内容
- 全スロット backup JSON (`kind=MarkSlotBackupSet`) の export/import を追加。
- 現在タブ解除を追加し、既存のカテゴリ解除/Workspace解除は導線と状態同期を補強。
- スロット演算ダイアログに `現在タブへ適用...` を追加（0件時は無効）。
- 既存契約維持: 単一スロット export/import 形式維持、slot import後に現在タブ自動復元しない、Save-as-Slot は現在タブを変更しない。

### 変更ファイル
- `Models/MarkSlotStore.cs`
- `Services/MarkSlotStorage.cs`
- `Dialogs/MarkSlotDialog.cs`
- `Dialogs/MarkSlotSetOperationDialog.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Runtime verified; closed.

## 2026-05-04 / Phase: command palette / recent favorite and query polish
### 目的
- Command Palette の候補増加に対し、Favorite / Recent と複数語AND検索で到達性を改善する。

### 修正内容
- `command_palette_usage.json` 用の usage state / storage を追加。
- コマンド実行時に recent を更新し、空検索時に Favorite / Recent を既存カテゴリ表示の上へ表示。
- 右クリックメニューと `Ctrl+D` で Favorite 登録 / 解除できるようにした。
- 検索文字列を半角/全角スペースで分割し、すべての token を含むAND検索へ変更。
- 検索中は既存どおりフラット表示を維持し、空検索時のアコーディオン表示も維持。
- `external_tools.json` schema、Command Palette 経由の外部ツール起動、Alt+slot 直起動は変更なし。

### 変更ファイル
- `Models/CommandPaletteUsageState.cs`
- `Services/CommandPaletteUsageStorage.cs`
- `Dialogs/CommandPaletteDialog.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Build/static verified; runtime verification pending.。

## 2026-05-04 / Phase: external tool / marked paths workflow polish
### 目的
- 外部ツール連携で `{markedPaths}` / `{markedPathsFile}` を使う運用導線を分かりやすくする。

### 修正内容
- 外部ツール定義編集ダイアログに、利用可能な引数テンプレート一覧を補強。
- `{markedPaths}` はマーク済みパスを引数へ直接展開し、`{markedPathsFile}` は1行1パスの一時ファイルを渡すことを明記。
- `"{selectedPath}"` / `--cwd "{currentDir}"` / `--list "{markedPathsFile}"` の例を追加。
- マーク0件でマーク系テンプレートを使う外部ツールを起動する場合、空のマーク一覧で起動するか確認するようにした。
- `external_tools.json` schema、Command Palette 経由起動、Alt+slot 直起動の契約は変更なし。

### 変更ファイル
- `Dialogs/ExternalToolEntryEditDialog.cs`
- `Services/ExternalToolArgumentTemplateService.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Runtime verified; closed.

## 2026-05-04 / Phase: tab lock / locked root boundary polish
### 目的
- ロックタブの lock root 境界で、ロック内操作とロック外参照を混同しないようにする。

### 修正内容
- lock root 上で `..` / Backspace による親移動を行った場合、既存ロックタブ内では移動しないようにした。
- 確認ダイアログで Yes を選んだ場合だけ、親フォルダを新しい非ロックタブで開く。
- No の場合は移動せず、元ロックタブは lock root のまま維持する。
- ロック配下での通常親移動、非ロックタブの `..`、ロックタブの `\` による lock root 復帰は既存契約を維持。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Runtime verified; closed.

## 2026-05-04 / Phase: public alpha preparation / repository hygiene
### 目的
- α版公開に向けて、README、サンプル設定、追跡対象、ignore 方針を最小整理する。

### 修正内容
- README にα版前の注意、最小build手順、ローカル状態ファイル、外部ツールサンプル、ライセンス未確定を追記。
- `external_tools.sample.json` を追加し、個人環境固定パスを含まない外部ツール定義例を用意。
- `.gitignore` に MidFD の実行時ローカル状態、生成物、作業用診断ファイルを追加。
- 追跡済み `bin/obj`、実設定、作業用画像/zip/log等を `git rm --cached` で追跡解除。作業ツリー上の実体は削除しない。

### 変更ファイル
- `.gitignore`
- `README.md`
- `src/MidFD2/external_tools.sample.json`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Build/static verified; closed.

## 2026-05-04 / Phase: large text / UTF-16 line index support
### 目的
- LargeText Viewer で BOM付き UTF-16 LE/BE テキストを安全に表示・検索・コピーできるようにする。

### 修正内容
- LargeText の文字コード判定で UTF-16 LE/BE BOM を未対応扱いせず、`UTF-16 LE` / `UTF-16 BE` として扱うようにした。
- UTF-16 の line offset は既存どおり byte offset で保持し、BOM後の offset 2 から本文行を開始する。
- UTF-16 LE/BE では2byte単位の LF を検出し、次行開始offsetを追加する。
- `ReadFirstLinesQuicklyAsync` / full index build / search / copy/export の読み取りは `DetectedEncoding` を使う既存経路を維持。
- UTF-8 / UTF-8 BOM / CP932 の既存行indexは従来どおり1byte LF検出を維持。

### 変更ファイル
- `Services/PreviewService.cs`
- `Services/LargeFileLineReaderService.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Build/static verified。
- UTF-16 LE BOM は通常Text / LargeText とも Runtime verified。
- BOM-less UTF-16 は unsupported as designed（今回の非対象）。
- UTF-16 BE BOM は service-level verified。UI runtime verification は未実施。

## 2026-05-04 / Phase: keyboard navigation polish / locked root and viewer selection shortcuts (supplemental)
### 目的
- キーボード操作の小改修として、ロックタブのルート復帰、通常Text Viewerの全選択、Browserタブ移動ショートカットを追加する。

### 修正内容
- ロックタブ中の `\` をドライブルートではなく lock root (`StartupPath`) へ戻す動作に変更。
- 通常Text Viewer (`PreviewKind.Text`) の `Ctrl+A` で `viewerTextBox.SelectAll()` を実行。
- Browserモード限定で `Ctrl+Left` / `Ctrl+Right` に現在カテゴリ内の前後タブ移動を追加。
- `Ctrl+Tab` / `Ctrl+Shift+Tab` と `Ctrl+Shift+Left/Right` の既存契約は維持。

### 変更ファイル
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Build/static verified; runtime verification pending.。

## 2026-05-04 / Phase: filter lock / working tab filter foundation
### 目的
- 現在タブに表示フィルタ条件を固定し、作業専用タブとして使いやすくする。

### 修正内容
- タブ状態に `TabFilterLockState` を追加し、拡張子、更新日時From/To、Git ignore対象外条件を保持できるようにした。
- ディレクトリ一覧取得後、表示前に現在タブのフィルタロックを適用するようにした。
- 拡張子フィルタはファイルのみ対象とし、ディレクトリはナビゲーション維持のため表示する。
- 更新日時フィルタは分単位UIに合わせ、To指定は指定分まで含める比較にした。
- Git ignore フィルタは `git check-ignore --stdin` による一括判定とし、失敗時は fail-open にした。
- 表示メニューとタブ右クリックメニューにフィルタロック設定/解除導線を追加した。
- タブ見出しに `[F]` を表示し、ヘッダ/Tooltipでフィルタ中であることを確認できるようにした。
- タブ復元用の JSON / SQLite workspace state にフィルタ条件を任意項目として追加した。

### 変更ファイル
- `Models/TabFilterLockState.cs`
- `Dialogs/TabFilterLockDialog.cs`
- `Services/TabFilterLockService.cs`
- `Services/GitIgnoreFilterService.cs`
- `Services/DirectoryProvider.cs`
- `Helpers/BrowserLoadCoordinator.cs`
- `Helpers/HeaderPresentationHelper.cs`
- `Configuration/AppSettings.cs`
- `Models/BrowserTabState.cs`
- `Services/Workspace/SqliteWorkspaceStateStore.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Build/static verified; runtime verification pending.。
- runtime verification pending。

## 2026-05-05 / Phase: tab filter lock dialog layout polish / datetime condition clarity
### 目的
- 現在タブのフィルタロック設定ダイアログについて、日時指定欄の視認性と条件の意味を改善する。
- 機能追加ではなく、既存機能のUI整理に限定する。
- 日付・時刻入力を十分な幅で表示し、開始日時/終了日時の有効化を明示する。
- 「解除」ボタンの意味を明確にするため、「条件をクリア」に変更する。

### 修正内容
- **ダイアログ幅拡張**: 幅を540pxから560px (またはゆとりを持たせて調整) に拡張し、日付・時刻の視認性を確保。
- **更新日時のグループ化**: 「更新日時」項目を GroupBox で囲み、開始日時と終了日時の構造を整理。
- **チェックボックスの明確化**: 「以降」「以前」の前に「開始日時を指定する」「終了日時を指定する」チェックボックスを追加し、役割を明確化。
- **クリアボタンの名称変更**: 「解除」ボタンを「条件をクリア」に変更し、フィルタロック自体を解除するのではなく入力条件をクリアする意図であることを明確化。
- **説明文の補強**: 拡張子欄とGit条件欄の説明文に追記を行い、対象や制約を分かりやすくした。

### 変更ファイル
- `Dialogs/TabFilterLockDialog.cs`
- `Docs/07_change_log_for_ai.md`

### 検証
- Build verified。
- Static code review approved。
- **Status**: Runtime verified; closed.

## 2026-05-05 / Phase: shell launch and rename selection polish
### 目的
- Browser からすぐに PowerShell を起動できるようにする。
- 既存の cmd 起動も互換導線として残す。
- 右クリックメニューからも shell 起動できるようにする。
- 単体ファイルリネーム時の拡張子誤削除を防ぐ。

### 修正内容
- **`h` → PowerShell 起動に変更**: Browser モードで `h` キーを押すと `powershell.exe` を CurrentPath で起動する。
- **`Shift+h` → cmd 起動**: cmd.exe は `Shift+h` で引き続き利用できる。
- **`ExternalToolService.OpenTerminal(workingDir, ShellKind)` を新設**: `ShellKind.PowerShell` / `ShellKind.CommandPrompt` を受け取り、WorkingDirectory を設定してプロセスを起動する。
- **右クリックメニューに「ここで開く」サブメニューを追加**: PowerShell / コマンドプロンプトをサブメニュー内に配置。
- **`SimpleInputDialog.ShowNullable` に `selectionLength` パラメータを追加**: 省略時（-1）は SelectAll と同じ動作。既存の全呼び出しに影響なし。
- **`RenameDialogCoordinator.ShowSingleRenameDialog` で拡張子除外選択を算出**: 単体ファイルの場合のみ `Path.GetFileNameWithoutExtension` のベース名長を渡す。ディレクトリ・先頭ドットファイル・拡張子なしファイルは -1（全選択）。リトライ時も全選択に戻す。

### 変更ファイル
- `Services/ExternalToolService.cs` (`ShellKind` 列挙型 + `OpenTerminal` 追加)
- `MainForm.cs` (`Keys.H` / `Keys.H + Shift` 処理、`OpenTerminalInCurrentDirectory`、右クリックメニュー)
- `Dialogs/SimpleInputDialog.cs` (`ShowNullable` の `selectionLength` 引数追加)
- `Helpers/RenameDialogCoordinator.cs` (拡張子除外選択長の算出と渡し込み)
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Build verified (0 errors, 0 warnings)。
- runtime verification pending。

## 2026-05-05 / Phase: shell/open command routing corrective
### 目的
- `h` = PowerShell が導入されたことで失われた exec ダイアログ導線を `x` に移す。
- 右クリックメニューの「ここで開く」サブメニュー階層をやめ、PowerShell/cmd を直置きにする。
- `z` の挙動は変更なし（docs整理のみ）。

### 修正内容
- **`x` → exec ダイアログ**: `ExecuteShellDialog()` を新設。選択ファイルがあれば引用符付きパスを初期入力に入れる。空入力はキャンセル扱い（cmd 起動は `h`/`Shift+h` の責務）。
- **右クリックメニューのフラット化**: 「ここで開く(&W)」サブメニューを除去し、「PowerShellをここで開く(&P)」「コマンドプロンプトをここで開く(&C)」を直置きに変更。
- **Slice 4 調査完了 (実装なし)**: `e` は `ExecuteOpenWithEditor()` で外部エディタ設定に依存。後続候補として分離。

### 変更ファイル
- `MainForm.cs` (`x` キー → `ExecuteShellDialog`、`ExecuteShellDialog` メソッド追加、右クリックメニューのフラット化)
- `.codex/state/current_focus.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Build verified (0 errors, 0 warnings)。
- runtime verification pending。

### 2026-05-05: settings dialog display viewer alignment corrective
- 表示 / ビューアタブの左右ペインで共通の配置定数（lblW, inpX, comboW, sizeX, checkX, rowH, topY）を導入。
- ラベルの右揃えを徹底し、ラベル長の違いを吸収してフォント指定行の入力フィールド開始位置を左右で一致させた。
- チェックボックスの開始 X 座標を 32 に、補足説明のインデントを揃えた。
- 右側ペインのラベルを「Viewer フォント:」に修正。
- 修正ファイル: SettingsForm.cs
- 検証: Build verified (0 errors, 0 warnings).

## 2026-05-05 / Phase: mouse gesture / browser navigation foundation
### 目的
- Browser モードに右ドラッグによるシンプルなマウスジェスチャー操作を追加する。

### 修正内容
- `MouseGestureRecognizer` を追加し、右ドラッグを `L/R/U/D` の方向列として認識するようにした。
- Browser一覧領域の右ドラッグに限定してジェスチャーを接続し、短い右クリックは従来のコンテキストメニュー表示を維持した。
- 固定ジェスチャーとして、履歴戻る/進む、親移動、再読込、タブ移動、カテゴリ移動、現在タブを閉じる、閉じたタブ復元を追加した。
- `Input.EnableMouseGestures` を追加し、設定画面「操作 / 入力」からON/OFFできるようにした。
- 閉じたタブ履歴はアプリ実行中のメモリ内 stack とし、最大10件だけ保持する。
- 閉じたタブ復元は既存のタブ切替/LoadDirectory/mark restore 経路を再利用し、Workspace state には保存しない。

### 変更ファイル
- `Helpers/MouseGestureRecognizer.cs`
- `Models/ClosedBrowserTabSnapshot.cs`
- `Models/BrowserTabState.cs`
- `Configuration/InputSettings.cs`
- `SettingsForm.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Runtime verified; closed。

## 2026-05-16 / Phase: dialog cancel key contract corrective

### 結論
キャンセル可能ダイアログの Esc/Cancel/× 契約を棚卸しし、不整合だった `QuickAccessDialog` の終了契約を統一した。
Runtime verified; closed。

### 変更内容
- **Dialogs/QuickAccessDialog.cs**
  - `キャンセル` ボタンを追加し、`DialogResult.Cancel` を設定。
  - `CancelButton` を `キャンセル` ボタンへ変更。
  - Esc キーを SaveOnly ではなく Cancel 終了へ変更。
  - `FormClosing` の SaveOnly 強制変換を削除し、`×` は Cancel 扱いに統一。
  - `閉じる` ボタンは従来どおり SaveOnly（反映して終了）導線を維持。

### 棚卸しメモ
- `SettingsForm`, `PackDialog`, `MarkSlotDialog`, `ArchiveListDialog`, `CommandPaletteDialog`, `SimpleInputDialog` などは既存で Esc/Canel 契約が成立。
- `FileOperationProgressFallbackForm` は処理中ダイアログのため、Esc は「閉じる」ではなく既存どおりキャンセル要求に接続。

### 検証
- Runtime verified; closed。
- `QuickAccessDialog` の Esc / `キャンセル` / `×` が Cancel 終了で一致することを確認。
- `QuickAccessDialog` の `閉じる` は SaveOnly（反映終了）として従来挙動を維持することを確認。
- `SettingsForm`, `PackDialog`, `MarkSlotDialog`, `ArchiveListDialog`, `CommandPaletteDialog` の Esc 契約に回帰がないことを確認。
- `FileOperationProgressFallbackForm` と `canCancel:false` 進捗ダイアログで、Esc が単純クローズに変わっていないことを確認。

## 2026-05-16 / Phase: archive workflow reliability corrective

### Status
Runtime verified; closed.

### 目的
- アーカイブファイル名のフルパス指定対応、出力先フォルダ配下作成、多件数 Pack 時のエラー回避、待機表示の改善。

### 実機確認結果
- 指定した出力先へのアーカイブ作成、待機中メッセージ表示、エラー回避ロジックが正常に機能することを確認。

### 変更内容
- **Dialogs/PackDialog.cs**
  - `archive ファイル名` にディレクトリ成分がある場合でも保持したまま拡張子同期するよう修正。
  - 出力形式に `gzip / bzip2 / xz / wim` を追加。
  - 最終archive親ディレクトリが存在しない場合のバリデーションを追加。
- **Models/PackRequest.cs**
  - `PackArchiveFormat` に `GZip / BZip2 / Xz / Wim` を追加。
- **Services/SevenZipService.cs**
  - Pack引数を `@listfile`（`-scsUTF-8`）方式へ変更し、多件数時のコマンドライン長制限回避に対応。
  - 7-Zip形式マッピングに `-tgzip / -tbzip2 / -txz / -twim` を追加。
- **Services/ZipFallbackService.cs**
  - 7-Zip未導入時の zip 限定 fallback（Pack/Unpack）を追加。
- **MainForm.cs**
  - Pack/Unpack 実行中に `FileOperationProgressFallbackForm` を表示する待機導線を追加。
  - 7-Zip未導入時は zip のみ fallback 実行、非zip形式は従来どおり7-Zip必須メッセージを表示。
  - `gzip / bzip2 / xz` は単一ファイルのみ対応として実行前ガードを追加。

### 補足
- ユーザー記載の `win` は7-Zip形式の文脈で `wim` と判断し、`wim` として追加。

### 検証
- Build verified; runtime verification pending。

## 2026-05-16 / Phase: bulk move hotpath corrective

### 結論
大量Moveで残っていたループ中UI同期を除去し、Moveホットパスの内訳計測を追加した。
Build verified; runtime verification pending。

### 変更内容
- **MainForm.cs**:
  - `ExecuteMove` の `Task.Run` ループ中 `Invoke(UnmarkPathsInBulk)` を廃止し、成功pathを収集して完了後に一括 `UnmarkPathsInBulk` する方式へ変更。
  - `[MoveHotpath] Summary` ログを追加し、`loopMs`、`fileMoveCallMsTotal/max`、`destinationCheckMs`、`progressReportMs/count`、`collisionCheckCount/dialogCount`、`undoCreateMs`、`unmarkApplyMs` を出力。
  - 既存の進捗throttle（件数/時間）とキャンセル確認は維持。

### 非対象
- Delete/Copy/Rename経路の再改修。
- Recycle Bin / Managed Trash / UndoRedo契約変更。
- Moveの仕様変更（collision/overwrite/merge意味変更）。

### 検証
- Build verified; runtime verification pending。

### Corrective: mouse gesture / suppress context menu after recognized gesture corrective
- ジェスチャー成立後に既存の右クリックメニューが表示される回帰を修正。
- gesture command 実行時に短命の抑止状態を立て、`MouseClick` / `ShowBrowserContextMenu` / `ContextMenuStrip.Opening` の各経路で右クリックメニューをキャンセルするようにした。
- 閾値未満の短い右クリック、設定OFF、Viewerモードでは従来挙動を維持。
- Runtime verified; closed。

## 2026-05-05 / Phase: workspace snapshot / restore foundation
### 目的
- 現在のカテゴリ / タブ / マーク / タブ固定 / フィルタロックを、名前付き Workspace Snapshot として手動保存 / 復元できる基盤を追加する。

### 修正内容
- `WorkspaceSnapshotStorage` を追加し、既存 `workspace.db` に `workspace_snapshots` table を additive に作成した。
- Snapshot payload は既存 `WorkspaceState` JSON をそのまま保存し、カテゴリ数 / タブ数 / マーク数 / アクティブパスの概要を別列で保持するようにした。
- `WorkspaceSnapshotDialog` を追加し、一覧表示、現在Workspace保存、復元、名前変更、削除の最小UIを実装した。
- MainForm に `Workspace スナップショット...` 導線を追加し、既存の workspace capture / restore 経路を再利用して復元するようにした。
- 復元前確認、payload validation、復元失敗時の runtime snapshot 巻き戻しを追加した。
- `BrowserTabSessionState` の serialize / restore 経路に `FilterLock` を明示的に含め、Workspace Snapshot と既存 workspace restore の両方でフィルタロックを落とさないようにした。

### 変更ファイル
- `Models/WorkspaceSnapshotEntry.cs`
- `Services/Workspace/WorkspaceSnapshotStorage.cs`
- `Dialogs/WorkspaceSnapshotDialog.cs`
- `Services/Workspace/WorkspaceStateStoreFactory.cs`
- `MainForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Runtime verified; closed。
- 名前付き保存、復元、名前変更、削除の全基本機能が正常動作することを確認。
- カテゴリ、全タブの構成、アクティブ状態、タブごとのマーク・固定・フィルタロックの完全な復元を確認。
- 起動時復元設定との独立性、および存在しないパスや異常データに対する安全なエラー処理・復帰を確認。
- 復元後の通常操作（タブ切替、ディレクトリ監視等）に回帰がないことを確認。

---
## Phase: workspace snapshot / export import and command palette integration
### 結論
Workspace Snapshot の運用性を高めるため、個別の Snapshot および全 Snapshot の一括エクスポート/インポート機能を実装し、Command Palette からの管理画面起動をサポートした。
Runtime verified; closed。

### 変更内容
- **MainForm.cs**:
  - `InvokeOpenWorkspaceSnapshotDialog` を追加し Command Palette からの呼び出しに対応。
  - Snapshot のエクスポート（単体/一括）およびインポート（単体/一括）ハンドラを実装。
  - インポート時の名前衝突回避（タイムスタンプ付加）を実装。
- **WorkspaceSnapshotDialog.cs**:
  - エクスポート、インポート、一括バックアップ、一括インポートのボタンを追加。
  - 高さを 420 -> 460 へ調整。
- **WorkspaceSnapshotStorage.cs**:
  - ペイロード (JSON) を直接取得する `TryGetSnapshotPayload` を追加。
  - 一括エクスポート用に全エントリとペイロードを読み込む `LoadAllSnapshotsWithPayload` を追加。
- **Models/WorkspaceSnapshotExportModels.cs**:
  - エクスポートファイル形式 (JSON) の定義を追加。
- **CommandPaletteService.cs**:
  - 「Workspace Snapshot 管理を開く」コマンドを追加。

### 検証
- Runtime verified; closed。
- 個別および一括のエクスポート/インポートが正常に動作することを確認。
- インポート時に現在Workspaceが自動変更されない安全な挙動を確認。
- 同名インポート時の自動リネーム（タイムスタンプ付加）による衝突回避を確認。
- 壊れた JSON ファイル読み込み時の耐障害性を確認。
- Command Palette から管理ダイアログを起動できることを確認。
- 既存の Snapshot 操作（保存/復元/名前変更/削除）に回帰がないことを確認。

---

## 2026-05-06 / Phase: editor launch / E key restore and shift enter viewer cleanup
### 結論
E キーによる外部エディタ起動を復活させ、起動前のテキスト判定ゲート（安全策）およびメモ帳フォールバックの実体パス解決を実装した。また、不要になった Shift+Enter による外部 Viewer 導線を削除し、操作体系をシンプル化した。
Runtime verified; closed。

### 変更内容
- **MainForm.cs**:
    - `ExecuteOpenWithEditor()`: 外部エディタ起動処理を復活。
    - **テキスト判定ゲート (Corrective)**: 外部エディタ起動前に `PreviewService.GetPreviewKind()` で判定を行い、テキスト系以外（バイナリ・画像等）は内蔵 Viewer 経路へリダイレクトするように修正。
    - **notepad.exe パス解決 (Corrective)**: 外部エディタ未設定時、`System32` 等から `notepad.exe` のフルパスを解決して起動するようにし、`File.Exists` チェックをパスするように修正。
    - キーバインド更新: `E` を外部エディタに割り当て。`Shift+Enter` の外部 Viewer 呼び出しを削除。
- **SettingsForm.cs**:
    - 「外部連携」タブに外部エディタ設定パスの入力欄を復元。
- **PreviewService.cs / PreviewKind.cs**:
    - テキスト判定ゲート用の `GetPreviewKind` 導線を活用。

### 検証
- Runtime verified; closed。
- テキストファイル（.cs, .txt等）で E キーによる外部エディタ/メモ帳の起動を確認。
- バイナリ/画像ファイルで E キーを押した際、外部エディタではなく内蔵 Viewer が開くことを確認。
- 外部エディタ未設定時の notepad.exe 起動を確認。
- Shift+Enter で何も起きない（外部 Viewer が起動しない）ことを確認。
- 設定の保存・反映を実機確認。
- 既存の Z / X / H / Enter 導線に回帰がないことを確認。

## 2026-05-07 / Phase: 7-Zip archive workflow enhancement

### Status
Runtime verified; closed.

### 目的
- Pack / 圧縮ワークフローを、7-Zip 連携込みで実運用しやすく改善する。

### 修正内容
- **Pack対象サマリー追加**: Packダイアログに対象サマリーを追加。`選択中1件` / `Mark N件` / `ファイル・フォルダ内訳` を表示。
- **出力先初期値統一**: Packダイアログの出力先初期値を常に CurrentPath に固定。
- **複数対象時の初期名補正**: 複数対象の初期archive名を先頭対象名から CurrentPath ディレクトリ名ベースへ変更。
- **フォルダごと個別圧縮**:
  - Packダイアログに `フォルダごとに個別圧縮する` を追加。
  - フォルダのみ複数対象時のみ有効化。
  - 個別圧縮時は `A.zip` 競合で `A (1).zip` 形式のユニーク名を生成して上書き回避。
- **右クリック導線**: Browser右クリックメニューに `フォルダごとに個別圧縮...` を追加（条件成立時のみ有効）。
- **形式拡充**: `zip / 7z / tar` を選択可能に拡張。
- **7zG対応とfallback**:
  - `7z.exe` を必須CLIとして解決し、Pack実行時は同一フォルダの `7zG.exe` があれば GUI 経路を使用。
  - `7zG.exe` 不在時は `7z.exe` で継続。
  - 設定画面で `7zG.exe` 不在時に警告を表示。

### 変更ファイル
- `MainForm.cs`
- `Dialogs/PackDialog.cs`
- `Models/PackRequest.cs`
- `Services/SevenZipService.cs`
- `SettingsForm.cs`
- `.codex/state/current_focus.md`
- `.codex/state/phase_backlog.md`
- `.codex/state/open_questions.md`
- `.codex/state/decision_log.md`
- `Docs/07_change_log_for_ai.md`

### 検証
- Build/static verified。
- **Status**: Runtime verified; closed。

## 2026-05-11 / Phase: Browser tab indexed overflow navigation

### 結論
ブラウザタブが横幅を超える場合に、単段タブのままタブ単位で左右移動できるようにした。
Build verified; runtime verification pending。

### 変更内容
- **BrowserTabStrip.cs**:
  - `firstVisibleTabIndex` 方式で、表示開始タブをindex単位で管理。
  - 左に隠れたタブがある場合のみ `<`、右に隠れたタブがある場合のみ `>` を表示。
  - タブ行上のホイールで隣接タブへ `SelectedIndex` を切り替え。
  - 追加ボタンを `+` 表示に変更し、常に押せる位置へ配置。
  - 表示中タブだけをクリック、ToolTip、右クリック、ダブルクリック対象にした。

### 検証
- Runtime verified; closed。

## 2026-05-11 / Phase: Browser tab overflow dropdown and mouse-wheel clamp corrective

### 結論
overflow時にタブ一覧ドロップダウンを追加し、タブ行ホイールは左右端で停止するようにした。
Build verified; runtime verification pending。

### 変更内容
- **BrowserTabStrip.cs**:
  - overflow時のみ `∨` ボタンを表示し、全タブ一覧を開けるようにした。
  - 一覧の現在タブにチェックを付け、選択時は既存 `SelectedIndex` 経路で切り替えるようにした。
  - タブ行ホイールは先頭/末尾で停止し、親側へ伝播しないようにした。
  - キーボードのタブ移動ループ仕様は変更しない。

### 検証
- Runtime verified; closed。

## 2026-05-11 / Phase: Browser tab indexed overflow partial tail corrective

### 結論
タブ表示領域の右端に大きな空白が残る場合、次のタブを部分表示して余白を活用するようにした。
Build verified; runtime verification pending。

### 変更内容
- **BrowserTabStrip.cs**:
  - `PreferredTabWidth` は維持したまま、右端の残り幅が32px以上なら次タブを部分表示。
  - 部分表示タブもクリック、ToolTip、右クリック、ダブルクリック対象にした。
  - 最後のタブが部分表示されている場合は、右側に未表示タブなしとして `>` を非表示。

### 検証
- Runtime verified; closed。

## 2026-05-12 / Phase: browser preview request coalescing and binary kind fast-path corrective

### 結論
Browserモードのpreview要求を最新1件へ集約し、古い要求が後からUI反映しないよう補正した。
Build verified; runtime verification pending。

### 変更内容
- **MainForm.cs**:
  - preview要求発行時に `requestPath` を固定し、`UpdatePreviewAsync` はそのsnapshotだけを処理対象にするよう変更。
  - 新規要求時に前回 `CancellationTokenSource` をCancel/Disposeし、await復帰後の古い要求を `Superseded` / `Canceled` として破棄。
  - 同一pathの通常重複要求を抑制。Viewer開始、Preview popup再表示、エンコーディング切替は `force` 再評価にした。
  - `LargeTextEntryTiming` ログを `requestPath` / `currentPath` に分離。
- **Services/PreviewService.cs**:
  - 既知バイナリ拡張子を内容読み取り判定へ進めず、`PreviewKind.Binary` として即返すfast-pathを追加。
  - 対象: `.exe`, `.dll`, `.msi`, `.wim`, `.iso`, `.zip`, `.7z`, `.rar`, `.cab`, `.pptx`, `.xlsx`, `.docx`, `.ppt`, `.xls`, `.doc`, `.pdf`。

### 非対象
- FileSystemWatcher / 外部変更 Error の根治。
- directory refresh / fallback の再設計。
- LargeText本文表示・検索・コピー仕様。
- タブoverflow / タブ操作系。

### 検証
- Build verified; runtime verification pending。

## 2026-05-12 / Phase: browser tab header refresh coalescing corrective

### 結論
Browserモードのカーソル移動時に、タブ見出し状態が同一なら `RefreshHeaders` / `SetTabs` の再構築をスキップするよう補正した。
Build verified; runtime verification pending。

### 変更内容
- **MainForm.cs**:
  - `RefreshBrowserTabHeaders` にヘッダ表示snapshotを追加。
  - snapshot対象はカテゴリ行表示、ActiveCategoryIndex、ActiveTabIndex、カテゴリ表示情報、タブ表示テキスト、Tooltip。
  - snapshotが同一なら `SetCategories` / `SetTabs` / INFOログを出さずに戻る。
- **Helpers/BrowserTabStrip.cs**:
  - `SetCategories` / `SetTabs` に同一内容の早期returnを追加。
  - 実際に適用した場合だけ従来のInvalidateと `SetTabs` ログを行う。

### 非対象
- preview latest-only / Binary fast-path の再設計。
- FileSystemWatcher / directory refresh。
- Workspace / SQLite state。
- タブoverflow、タブ一覧ドロップダウン、+ボタン、`<` / `>` 仕様。

### 検証
- Build verified; runtime verification pending。

## 2026-05-13 / Phase: sort dialog radio button exclusivity corrective

### 結論
Sortダイアログで、条件と順番のRadioButtonが常に1つだけ選択されるよう補正した。
Build verified; runtime verification pending。

### 変更内容
- **SortDialog.cs**:
  - 条件側の名前/拡張子/サイズ/日付を明示排他helper経由で選択するよう変更。
  - 順番側の昇り順/降り順も同じ排他helper経由で選択するよう変更。
  - OK押下時は単一選択helperからsort keyを確定し、未選択時は名前へfallbackするようにした。

### 非対象
- ソートアルゴリズム、ソートキー仕様、昇順/降順の意味。
- ファイル一覧描画、タブ、プレビュー、設定保存形式。

### 検証
- Runtime verified; closed。

## 2026-05-13 / Phase: video preview modal suppression on browser selection corrective

### 結論
Browserモードで動画ファイルへカーソル移動しても、動画未対応のMessageBoxを出さないよう補正した。
Build verified; runtime verification pending。

### 変更内容
- **MainForm.cs**:
  - 選択変更時の既存画像Viewer追従対象を `PreviewKind.Image` のみに限定。
  - `UpdatePreviewAsync` の `PreviewKind.Video` 分岐で `ImageViewerForm.LoadMedia(Video)` を呼ばないよう変更。
  - Enter / View 等の明示openで動画を開こうとした場合は、MessageBoxではなくstatus通知に限定。
- **ImageViewerForm.cs**:
  - `LoadMedia(Video)` がMessageBoxを直接出さないよう変更。
  - 呼ばれた場合もstatus表示のみで戻る。

### 非対象
- 動画再生機能、動画サムネイル、外部プレイヤー連携。
- preview latest-only / Binary fast-path の再設計。
- SortDialogの追加修正。

### 検証
- Runtime verified; closed。

## 2026-05-13 / Phase: image viewer auto-follow modal suppression corrective

### 結論
ImageViewerの自動追従では、画像/SVG読み込み失敗時にMessageBoxを出さないよう補正した。
Build verified; runtime verification pending。

### 変更内容
- **ImageViewerForm.cs**:
  - `LoadImage` / `LoadMedia` に `showErrorMessage` 指定を追加。
  - `showErrorMessage=false` の場合、読み込み失敗はstatus表示とログに留める。
  - 読み込み済み画像の有無をMainFormから判定できる `HasLoadedImage` を追加。
- **MainForm.cs**:
  - Browser選択変更による既存ImageViewer追従では `showErrorMessage=false` を指定。
  - 自動preview経由で既存ImageViewerを更新する場合も `showErrorMessage=false` を指定。
  - 明示Open時は従来通り失敗通知を維持し、自動追従失敗後の同一pathでも未読込なら再読込する。

### 非対象
- ImageViewerの外部EXE化。
- 動画再生機能、動画別アプリ化。
- 画像減色、Undo/Redo、SVGコピー仕様。
- preview latest-only / Binary fast-path の再設計。

### 検証
- Build verified; runtime verification pending。

## 2026-05-14 / Phase: browser selection shallow preview classification corrective

### 結論
Browserモードのカーソル移動では、深いPreview判定と本文読み込みを行わず、拡張子ベースの浅い分類だけで自動preview可否を決めるよう補正した。
Build verified; runtime verification pending。

### 変更内容
- **MainForm.cs**:
  - `FileListView_SelectedIndexChanged` の深い `PreviewService.GetPreviewKind` 呼び出しを撤去し、画像/SVG自動追従判定を浅い分類へ変更。
  - `RequestPreviewRefresh(bool force)` に Browser 自動previewの発行前ゲートを追加し、通常カーソル移動では画像系以外の request を出さないよう変更。
  - 自動preview対象外では `PreviewRequest` を queued せず、案内メッセージだけを表示するよう変更。
- **Services/PreviewService.cs**:
  - `GetPreviewKindShallow(string path, bool isDirectory)` を追加。
  - `.lnk` / `.url` / ディレクトリは自動preview対象外、画像/SVGは `Image`、動画は `Video`、既知Binaryは `Binary`、テキスト系拡張子は `Text` としてI/Oなしで浅く分類するよう変更。

### 非対象
- `PreviewService.GetPreviewKind` の深い判定削除。
- Enter / View 時の深いプレビュー仕様変更。
- LargeText本文表示仕様、ImageViewerForm の読み込み実装、動画再生機能。

### 検証
- Build verified; runtime verification pending。

## 2026-05-14 / Phase: browser auto-preview skip logging and clear suppression corrective

### 結論
Browser自動preview対象外では、期待通りのskip INFOログを出さず、不要な `ClearPreview()` による再描画も抑止するよう補正した。
Build verified; runtime verification pending。

### 変更内容
- **MainForm.cs**:
  - `FileListView_SelectedIndexChanged` の事前 `ClearPreview()` を、自動preview対象の画像系に限定。
  - Browser自動preview対象外で案内文を表示する helper を追加し、同一path・同一案内文では再クリアしないよう変更。
  - `RequestPreviewRefresh(bool force)` の `AutoPreviewIneligible` INFOログを削除。

### 非対象
- 浅い分類ルール自体の変更。
- Enter / View 時の深い判定、本文読み込み、LargeText仕様。
- 画像/SVG自動追従、動画再生、ImageViewer実装。

### 検証
- Build verified; runtime verification pending。

## 2026-05-14 / Phase: browser auto-preview suppressed-state clear guard corrective

### 結論
Browser自動preview対象外の連続移動では、path単位ではなく表示状態単位で `ClearPreview()` を抑止するよう補正した。
Build verified; runtime verification pending。

### 変更内容
- **MainForm.cs**:
  - Browser自動preview抑制状態として `_isBrowserAutoPreviewSuppressed` と `_lastBrowserAutoPreviewSuppressedMessage` を追加。
  - `ShowBrowserAutoPreviewSuppressedMessage(...)` の抑制条件を、同一path依存から「抑制表示中かつ同一message」へ変更。
  - 再描画を抑止する場合でも `_currentPreviewTarget` は現在選択pathへ更新するよう変更。
  - 画像/SVG自動preview開始時と `force=true` preview開始時は `ResetBrowserAutoPreviewSuppressedState()` で抑制状態を解除するよう変更。

### 非対象
- `PreviewService.GetPreviewKind` / `GetPreviewKindShallow` の分類変更。
- Enter / View / 明示Open 経路。
- ImageViewerForm、画像自動追従、動画再生。

### 検証
- Build verified; runtime verification pending。

## 2026-05-15 / Phase closeout: browser selection shallow preview classification / auto-preview skip suppression corrective

### 結論
Browserカーソル移動時の不要なpreview判定・ログ・再描画を抑制し、ネットワークドライブでの引っかかりを軽減する補正を Runtime verified; closed とした。

### closeout対象
- `browser selection shallow preview classification corrective`
- `browser auto-preview skip logging and clear suppression corrective`
- `browser auto-preview suppressed-state clear guard corrective`

### 変更方針
- Browserカーソル移動では深いPreview判定を行わない方針を継続採用。
- 自動preview対象外ではINFOログと同一案内表示の `ClearPreview()` 更新を抑制する方針を継続採用。
- path単位ではなく表示状態単位で `ClearPreview()` を抑制する方針を継続採用。
- 職場端末などで違和感が再発した場合は巻き戻さず、別Phaseで観測・補正する。

### 検証
- Runtime verified; closed。

## 2026-05-17 / Phase: repository source hygiene before publication

### 結論
GitHub公開前のソース衛生チェックを実施し、軽微で安全な修正のみ適用した。
Static verified; closed。

### 変更内容
- **.gitignore**:
  - `publish/`, `logs/`, `settings.json`, `external_tools.json`, `command_palette_usage.json`, `scratch/`, `*.tmp`, `*.bak` を追加。
- **MainForm.cs**:
  - 文字化けしたコメントを自然な日本語コメントへ修正（挙動変更なし）。
- **docs/state**:
  - `current_focus`, `decision_log`, `open_questions`, `phase_backlog` に本Phaseの判断と結果を追記。

### 確認結果
- README / UserDocs / LICENSE / Issue Template の表記崩れと重大矛盾は確認されなかった。
- 旧名称 `MidFD2` やローカルパス文字列は主に履歴文脈（`.codex` / `Docs`）で残存しており、今回Phaseでは履歴保全を優先して削除しない。
- APIキー/秘密情報の直接混入は検出されなかった（検索ベース）。
- NuGet依存は `Microsoft.Data.Sqlite`, `SQLitePCLRaw.bundle_e_sqlite3`, `Svg` を確認。

## 2026-05-17 / Phase: public repository final source hygiene corrective

### 結論
GitHub公開前の最終衛生補正として、Markdown改行設定、`.gitignore` 既存対応の再確認、残存文字化けコメント1件の修正を行った。
Build verified; static verified; runtime verification not required。

### 変更内容
- **.gitattributes**:
  - Markdown個別指定中心の状態から `*.md text eol=lf` を正本に補正。
  - `LICENSE text eol=lf` は維持。
- **.gitignore**:
  - 直前Phaseで追加済みの `publish/`, `logs/`, `settings.json`, `external_tools.json`, `command_palette_usage.json`, `scratch/`, `*.tmp`, `*.bak` が有効であることを確認。
  - 今回は重複編集を行わない。
- **MainForm.cs**:
  - Window bounds collapse guard helpers 周辺に残っていた文字化けコメント1件だけを自然な日本語へ置換。
- **docs/state**:
  - `current_focus`, `decision_log`, `phase_backlog` に本Phaseの完了を追記。

### 非対象
- 機能コードの挙動変更。
- UI変更、構造変更、広域文字化け推測修復。
- 履歴文書全体の `MidFD2` / ローカルパス記述の一括削除。

## 2026-05-17 / Phase: public release Git operation boundary

### 結論
private / public の Git 運用境界と、公開手順の正本ドキュメントを追加した。
Static verified; runtime verification not required。

### 変更内容
- **Docs/release_procedure.md**:
  - `MidFD` と `MidFD-publish` を分離した前提で、export、public反映、タグ、publish、ZIP、sha256、GitHub Release までの手順を追加。
- **.codex/state/decision_log.md**:
  - public公開後は `commit --amend`、`rebase`、`push --force-with-lease` を原則使わない運用判断を追記。
- **AGENTS.md**:
  - 詳細手順ではなく、public 側のGit運用原則と固定Asset名だけを最小追記。

### 非対象
- public 側リポジトリの利用者向け文書変更。
- 機能コード、UI、ビルド設定、リリース自動化。
