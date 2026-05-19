# Open Questions / Runtime Verification

## Pending
(None)

## Resolved / Verified
- **VideoStill ImageViewerForm closeout**:
  - 動画静止画プレビューの正本表示先を ImageViewerForm に確定。
  - 0秒開始、位置バー、左右キー移動、現在位置からの外部再生を確認。
  - statusStrip非表示によりVideoStill時の下部黒帯を解消。
  - MainForm内VideoStill専用Viewerは主導線から外した。
- **公開リポジトリ範囲の最終判断（.codex / Docs / artifacts）**:
  - AI駆動開発の基幹情報である `.codex/` および `Docs/` は削除・圧縮せずに完全な状態で残す方針を確定。
  - 検証用の一時画像しか含まれず、ドキュメントからも参照されていない `artifacts/` を `git rm --cached` により Git 追跡対象から除外完了（ローカル実体は維持）。
  - `.gitignore` に `artifacts/` などの設定が既に存在することを確認。
- **Logdsk directory tab completion corrective**:
  - `LogdiskDialog` 等のディレクトリ入力欄において、Tab キー（正順）および Shift+Tab（逆順）による候補の巡回補完が動作することを確認。
  - Enter でのパス確定、Esc での補完キャンセル（またはダイアログキャンセル）が機能することを確認。
  - 既存のインクリメンタル補完ポップアップとの共存に問題がないことを確認。
  - Browser 本体の Tab マーク操作に影響や回帰がないことを確認。
- **file metadata attribute timestamp and grouped sort closeout**:
  - 単一ファイル/単一フォルダで属性変更（R/H/S/A）が反映されることを確認。
  - 作成日時/更新日時/最終アクセス日時が変更でき、チェックON項目のみ反映されることを確認。
  - `サブディレクトリ以下も処理する` で配下へ一括適用できることを確認。
  - ReadOnlyタブで属性/日時変更がブロックされることを確認。
  - 日時ソート（更新/作成/最終アクセス）が切替可能で並び順が変化することを確認。
  - WinFD互換属性色（System=マゼンタ / Hidden=ブルー / ReadOnly=グリーン）を確認。
  - 複数属性時の色優先順位が `System > Hidden > ReadOnly` であることを確認。
  - ソート時に `..` / ディレクトリ群 / ファイル群が混在せず、各群内で `System > Hidden > ReadOnly > Normal/Archive` の属性グループ順とグループ内ソートが成立することを確認。
- **dialog cancel key contract corrective**:
  - `QuickAccessDialog` で Esc / `キャンセル` / `×` が Cancel として終了することを確認。
  - `QuickAccessDialog` の `閉じる` ボタンが SaveOnly（反映終了）として従来どおり動作することを確認。
  - `SettingsForm`, `PackDialog`, `MarkSlotDialog`, `ArchiveListDialog`, `CommandPaletteDialog` の Esc終了契約に回帰がないことを確認。
  - `FileOperationProgressFallbackForm` および `canCancel:false` 進捗ダイアログ契約に回帰がないことを確認。
- **Batch rename apply progress visibility corrective**:
  - 数千件規模の一括リネームにおいて、進捗ダイアログが正しく表示され、件数と処理中ファイル名が更新されることを実機で確認。
  - 処理中の「応答なし」状態が解消され、キャンセル不可方針が運用上問題ないことを確認。
- **Rename collision and locked-tab root navigation corrective**:
  - `$2N$E` などのパターン指定時に衝突しないケースが OK と判定されることを確認。
  - 固定タブにおいて、サブディレクトリから別タブへ遷移して戻った後でも `\` キーで正しく固定ルートへ復帰することを確認。
- **Tar fallback extract destination collision corrective**:
  - `ExCSS.dll.7z` を解凍する際、同名のファイル `ExCSS.dll` が存在しても、`ExCSS.dll_extracted` 等の別フォルダへ正常に展開されることを確認。
  - すでに `ExCSS.dll_extracted` も存在する場合に `_1`, `_2` と連番が振られることを確認。
  - 通常の一括解凍と、プレビュー画面からの個別解凍の両方でこの挙動が機能することを確認。
  - ログにリダイレクトされた旨が記録されることを確認。
- **Tar fallback extract destination corrective**:
  - 7-Zip なし環境で、`ExCSS.dll.7z` のようなファイル名の 7z 展開が成功することを確認。
  - 展開時に `ExCSS.dll` フォルダが自動作成され、その中へ展開されることを確認。
  - 展開引数から `-a` が消え、bsdtar の警告が出なくなったことを確認。
  - 展開先ディレクトリが作成不能な場合（権限不足等）に、適切な日本語エラーメッセージが表示されることを確認。
- **Tar fallback service implementation**:
  - 7-Zip なし環境で、PackDialog に `zip / 7z / tar` が表示され、それぞれ正しく作成されることを確認。
  - 7-Zip なし環境で、7z/tar/rar の解凍が TarFallbackService 経由で成功することを確認。
  - プレビュー画面 (Archive Contents) からの「すべて解凍」「選択解凍」が、7-Zip なしでも正常に動作することを確認。
  - ReadOnly タブでの解凍ガードが、新設された fallback 経路でも機能していることを確認。
- **archive fallback capability and preview extract guard corrective**:
  - ReadOnly タブのプレビュー画面（Archive Contents）で、解凍ボタンが無効化されていることを確認。
  - 7-Zip が見つからない環境で、PackDialog の形式が zip/7z/tar（tar.exeあり時）に適切に制限されていることを確認。
  - 安定版において、gzip / bzip2 / xz / wim などの高度な形式が PackDialog から消えていることを確認。
- **bulk move hotpath corrective**:
  - 1,860件および1,537件のMove（同一ボリューム）において、I/O以外のオーバーヘッド（UI同期等）が最小化されていることを実機確認。
  - キャンセル時に成功済み分のみが正しくUnmarkされ、Move中にUIがフリーズしないことを確認。
  - 1860件Moveでの計測ログ: `loopMs=2917 fileMoveCallMsTotal=2069 unmarkApplyMs=1 progressReportMs=0`。
- **bulk file operation hotpath and cancelability balance corrective**:
  - 大件数 recycle-bin 削除において、chunk（64件）単位の実行によりスループットが改善し、chunk境界でのキャンセルが成立することを確認。
  - MidFD Managed Trash で 454件のバッチ処理（100%成功）を確認。
  - 進捗表示の間引きにより、大件数操作時のUI応答性が向上していることを確認。
- **first launch profile dialog cancel and tooltip cleanup**:
  - Profile未設定時に「MidFD 利用モードの選択」ダイアログが表示されることを確認。
  - 「実用安定版（推奨）」と「高度機能α版」の表示を確認。
  - `キャンセル/×` の挙動を確認。
  - 実用安定版/高度機能α版の選択導線を確認。
  - SettingsFormで「実用安定版（推奨）」「高度機能α版」の表示を確認。
  - 設定画面上にメイン画面由来のパスToolTip/フロートが残らないことを確認。
- **practical stable profile and feature gate integration**:
  - PracticalStable profile で起動できることを確認。
  - ツールメニューで Workspace スナップショット導線が表示されないことを確認。
  - 外部Diff導線が無効化されていることを確認。
  - MarkSlot基本導線が残っていることを確認。
  - ImageViewerでSVG表示は可能で、減色 / SVGコピー導線が閉じていることを確認。
  - Command Paletteの候補がPracticalStable向けに絞られていることを確認。
- **LargeText character selection autoscroll edge guard corrective**:
  - `UpdateCharacterSelectionAutoScroll` の過剰な上方向スクロール発動（1行目ドラッグ時の暴走）を抑制し、実機にて長大1行JSONの途中選択が行頭へ引きずられないことを確認。
  - GDIに基づく正確なヒットテスト（`MeasureTextWidth` ベースの二分探索）と組み合わせることで、選択位置ズレや先頭選択問題を完全に解消した。
- **7-Zip archive workflow enhancement / corrective**:
  - PackDialog の対象サマリーから冗長な「対象:」プレフィックスを削除し、UIの一貫性を確保。
  - 右クリックメニューの「圧縮...」「解凍...」「フォルダごとに個別圧縮...」を 7-Zip サブメニュー内、またはブラウザメニュー直下の自然な位置へ統合。
  - ReadOnlyタブの右クリックメニューにおいて、圧縮・解凍系操作が正しくグレーアウト（無効化）されることを確認。
  - `ExecuteUnpack` に ReadOnly ガードを追加し、いかなる経路からも ReadOnly タブへのアーカイブ展開が阻止されることを確認。
  - 複数フォルダ選択時に「フォルダごとに個別圧縮」が有効になり、意図した名称でアーカイブが生成されることを確認。
  - 7zG.exe (GUI) / 7z.exe (CUI) の自動判別と fallback 実行を確認。
- **ReadOnly tab write operation guard coverage corrective**:
  - ReadOnlyタブにおいて、切り取り(Cut)、属性変更(ATTR)、Drag-in、圧縮(Pack)、外部エディタ(Edit)など、書き込みを伴う操作が確実にガードされ、実行できないことを確認。
- **editor launch / E key restore and shift enter viewer cleanup**:
  - テキストファイルでは E キーで外部エディタが起動することを確認。
  - バイナリファイル（画像含む）で E キーを押した際、外部エディタではなく内蔵 Viewer が表示されることを確認。
- **workspace snapshot / export import and command palette integration**:
  - Snapshot 管理ダイアログに export/import 導線が表示され、正常に動作することを確認。
- **workspace snapshot / restore foundation**:
  - WorkspaceSnapshotManager による保存・復元・一覧取得が正常に動作することを確認。
- **mouse gesture / browser navigation foundation**:
  - 右クリックドラッグによる戻る/進むが Browser モードで動作することを確認。
- **scalable list presentation polish**:
  - アコーディオン開閉アニメーションと検索フォーカスがコマンドパレットで正常に動作することを確認。
- **external tool quick access foundation**:
  - コマンドパレットから外部ツール（PowerShell 等）を引数付きで起動できることを確認。
- **filter lock / working tab filter foundation**:
  - フィルタロックダイアログの `Ctrl+Shift+L` ショートカットおよび日付時刻ピッカーの UI 改善を確認。


## Watchlist
- **Browser network-drive cursor move regressions**
  - ネットワークドライブ上の連続カーソル移動は継続観測とする。
  - 再発時は巻き戻しではなく、画像自動追従debounce / skipログ / FileSystemWatcher Error 経路を別Phaseで切る。
- LargeText wrap mode (design investigation deferred) / Deferred / 仕様・判断保留

- **CRC/SHA 大容量ファイル時の待ち時間・キャンセル性**
  - CRC/SHA計算は非同期化済みだが、数GB級ファイルや複数大容量ファイルでの体感待ち時間、キャンセル導線の必要性は未確認。
  - 再観測時に `hash progress / cancellation polish` として検討する。

- **CRC/SHA `すべて` 実行時のハッシュ値コピー整形**
  - 単一アルゴリズムのハッシュ値コピーは実用確認済み。
  - `すべて` 実行時に、アルゴリズム名付きのコピー結果が十分見やすいかは継続観察。

- **7-Zip未設定・不正パス時のUX**
  - 通常環境での7z.exe連携は成立。
  - 7-Zip未設定、不正フォルダ、7z.exe削除時のエラー表示の分かりやすさは、必要時に確認する。

- **Archive系の大容量処理中表示**
  - 7zG.exe / 7z.exe fallback は実装済み。
  - 大きな圧縮・解凍で、進捗表示や待ち状態が十分かは継続観察。
  - 不足が見えた場合は `archive operation result and progress polish` として扱う。

- **右クリックメニュー全体の棚卸し**
  - 直近で7-Zip、個別圧縮、CRC/SHA、ReadOnly制御を追加したため、メニュー構成の肥大化や配置違和感は継続観察。
  - ただし現時点では独立した確認フェーズにはしない。

- **Pack / CRC/SHA のMark対象解決**
  - Markあり時はMark対象、Markなし時は選択対象という方針で進めている。
  - ファイル/フォルダ混在、Mark多数、対象0件などの境界は、実使用中に違和感があれば個別correctiveにする。

- **動画内蔵プレビュー方式の再選定（deferred）**
  - 既存WinForms構成で大型依存を増やさずに安定再生を成立させる方式が未確定。
  - 本correctiveは画像Viewer実用性補正に限定し、動画は後続で再設計方針を決める。

- **動画外部再生およびプレビューの拡張（deferred）**:
  - ffplay専用パス設定
  - 複数動画連続再生
  - サムネイルタイムライン
  - ffplay位置の厳密同期

- **Path completion latency on network drives**:
  - ネットワークパス等の応答が遅い環境において、候補列挙で UI が一時的に重くなる可能性。必要に応じて非同期化やタイムアウトを検討する。
- **Batch rename apply cancelability**:

  - 大量一括リネームはキャンセル不可方針としたが、実運用で強い要望が出た場合は `RenameApplyCoordinator` の途中中断機構と Undo 整合性を別途設計する。
- **Windows native archive fallback (tar.exe)**:
  - RAR 展開の実機確認（libarchive のバージョンと対応状況の最終確認）。
  - 長大件数時の ArgumentList 限界挙動（必要に応じて listfile 方式への切り替え検討）。
  - 24H2 未満の Windows 環境における `tar.exe` の能力不足時のエラーハンドリングの継続観測。
