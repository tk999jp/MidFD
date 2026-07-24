# 更新履歴

利用者向けの主な変更概要です。内部の開発ログやtest追加は原則として記載しません。

## v2026.07.24 — ファイル操作・Mark・初回セットアップの改善

### 初回／基本セットアップ

- 初回セットアップを「基本機能のみ」「便利機能まで使う（推奨）」「すべての機能を使う」の3段階へ再構成しました。
- 各段階に含まれる機能を常時表示し、個別変更時は「個別設定」として扱うようにしました。
- 操作方式、配色、7-Zip、動画ツール、外部エディタを同じ画面から確認・変更できるようにしました。
- 操作方式を実際に変更した場合のみ、MidFD標準／FD・WinFD互換に対応する標準配色へ連動するようにしました。画面を開いただけでは保存済み配色を上書きしません。
- 初回起動時は設定を直接保存し、設定画面から再表示した場合は［適用］または［OK］で保存する契約へ整理しました。

### 表示・パス移動

- ファイルサイズの `Bytes` 表示で、実byte数を正しく表示するよう修正しました。
- ヘッダーへパンくず表示を追加し、直接パス入力と切り替えられるようにしました。
- `Ctrl+L`または直接入力欄で、`%TEMP%`などの環境変数を含むpathへ移動できるようにしました。
- 属性／日時変更画面をcompact化し、日時の数値segment入力、自動field移動、calendar選択に対応しました。
- UTF-16 LE／BEのpreviewとbinary判定を補正しました。
- Mark数とMark sizeの表示を操作直後にも更新し、Mark解除やdirectory移動後の残留表示を補正しました。
- UNCパスのUsed／Free情報を、画面操作を止めない遅延取得へ変更しました。
- 大量項目や更新頻度の高いフォルダで、一覧更新・selection・Mark操作が固まりにくいよう調整しました。

### Mark・MarkSlot

- 大量フォルダの一括Markで、現在表示中のページだけでなく読み込み済みデータ全体を対象にするよう修正しました。
- コピー、移動、圧縮、解凍などの後も、実体が残っているMarkを維持するようにしました。削除・移動済みの項目は結果に応じて除外します。
- MarkSlot通常画面を保存・復元中心に簡略化し、詳細表示と管理操作を別画面へ分離しました。
- MarkSlot管理画面で、選択中slotと現在Markを分けて確認できるようにしました。

### Drag ZIP・圧縮

- Browserの項目上だけでなく、空白部分から現在MarkをDrag ZIPとして外部アプリへ渡せるようにしました。
- directoryを含むDrag ZIPで配下のfileを再帰的に収集し、同じfileが重複entryにならないよう修正しました。
- 通常の圧縮画面を「自動」「7-Zip標準Dialog」「MidFD簡易Dialog」から選択できるようにしました。
- 自動モードでは `7zG.exe` の標準Dialogを優先し、利用できない対象や未導入環境ではMidFD簡易Dialogへfallbackします。
- 7-Zip標準Dialogを明示選択した場合は、利用できない理由を表示して処理を停止します。

### ファイル操作

- symlink／junctionのコピー・移動で、リンク先の実体へ再帰せず、リンク自体を同種のリンクとして再作成するよう改善しました。
- リンク作成に権限が必要な場合だけ起動する専用helperを追加しました。
- 異なるドライブ間のMoveを、copy完了後にsourceを削除する正式経路として整理しました。
- 同名directoryへのMove／Copyで、既存directoryとのmerge契約を維持するよう補正しました。
- 親directoryとその配下を同時Markした場合の削除対象重複を正規化しました。
- 現在directory外のMarkを含む削除では、確認Dialogで `Alt+Y` を必要とするようにしました。
- 個別operationの失敗やcancelが、無関係なBrowser操作を停止させないよう処理を局所化しました。

### MidFD管理ゴミ箱

- MidFD管理ゴミ箱を通常削除の選択肢として統合しました。
- ファイルメニューと設定画面から、退避中項目を確認・管理できるDialogを開けるようにしました。
- 一覧から選択復元、選択完全削除、すべて空にする、欠損record掃除を実行できるようにしました。
- 削除元と同じvolume／shareの `.midfd-trash` を使用し、記録されたpathと実体の整合を確認してから復元・削除するよう補正しました。
- 起動時に既存のゴミ箱実体を自動移動せず、現在記録されている場所をそのまま扱うようにしました。

### 設定

- SQLite設定DBの異常読込、backup復旧、既定値起動を整理しました。
- standalone backupを最大5世代保持するようにしました。
- 読込不能な新しい `PayloadVersion` を既定値で上書きせず、元payloadを保持するよう修正しました。
- 設定のimport後、再起動せず現在の画面へ反映するよう改善しました。
- import／export時のbackup状態と一部失敗を結果Dialogへ表示するようにしました。

### 配布

- リンク作成用 `MidFD.FileOperationHelper.exe` をbuild／publish対象へ追加しました。
- 配布ZIP内の `README_FIRST.txt` をversion非依存の起動案内へ変更しました。
- 公開文書を現在の実装sourceに合わせて再整理しました。

## v2026.07.04 — .NET 10移行とQuickAccess／メディア再生改善

### 配布・実行環境

- 公開buildを .NET 10／Windows x64へ移行しました。
- Framework-dependent ZIPを基本配布とし、.NET 10.0 Desktop Runtimeの案内を同梱しました。
- 7-Zip、ffmpeg、ffprobe、ffplayは同梱しない構成です。

### QuickAccess・設定

- QuickAccessへカテゴリ列、カテゴリfilter、カテゴリ見出し、並べ替えを追加しました。
- 数字を含む検索語と `1`〜`9` の直接移動を両立しました。
- アプリ設定の正本をSQLite (`Data\Settings\settings.db`) へ移行しました。
- Portable既定を維持しつつ、Installed profileの明示opt-inを追加しました。

### 操作・表示

- Command Paletteを機能・設定検索へ拡張しました。
- Explorerへのdrag-outとDrag ZIPの互換性を改善しました。
- Markdown／SQLiteのread-only previewを追加しました。
- 動画のEnter外部再生設定と音声外部再生を整理しました。
- 一覧、タブ、ステータス表示のfont・幅・配色設定を拡張しました。

## v2026.06.14

### 追加・改善

- カテゴリのdrag並べ替えと、カテゴリ別タブ一覧を追加しました。
- QuickAccess名、タブ状態、メニュー状態の同期を改善しました。
- Managed Trash保持期限処理とDrag ZIP一時file整理を修正しました。

## v2026.06.07

### 操作カスタマイズ

- Browserキーバインド、Functionバー4layer、マウスジェスチャーを設定可能にしました。
- HelpメニューへCommand一覧を追加しました。
- FD／WinFD互換shortcutと一覧表示modeを整理しました。

### Viewer・clipboard

- 画像の矩形選択copy、回転・反転、画像情報表示を追加しました。
- 設定ON時のclipboard text file化と、貼り付け作成fileのUndoを追加しました。
- 外部ツール起動時のpath検証とargument受け渡しを改善しました。

## v2026.05.20

- 初回セットアップ画面を追加しました。
- 動画tool folder、7-Zip、外部editorの設定導線を追加しました。
- メディアファイルのEnter動作を選択できるようにしました。
