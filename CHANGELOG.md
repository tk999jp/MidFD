# 更新履歴

利用者向けの主な変更概要です。内部の開発ログやtest追加は原則として記載しません。

## v2026.08.11 — 縦型タブ・Browser操作・Viewerの改善

### Browser・ファイル操作

- 一覧の上下・左右・page移動、tab／categoryを使った作業場所の切り替え、tabの固定・close、QuickAccess、breadcrumb、directoryを新しいtabで開く導線、tab追加位置の設定を整理しました。
- 同名directoryのcopy／move／pasteを既存内容を保持するmerge操作へ統一しました。symlink／junctionはリンク先へ再帰せず、リンク自体として扱います。
- BrowserからのDrag & Dropで、同名directoryのcopy／moveをmergeできるようにしました。fileの型不一致やcollisionのCancelは従来どおり安全に停止します。
- Browserの外部入力に含まれる画像URLについて、public addressの検証、DNS／redirect再検証、size／timeout、temporary file確定を行うよう安全化し、UNC／大容量directoryを含む一覧更新と操作の応答性を改善しました。
- マップされたネットワークドライブのUsed／Free情報を非同期で取得し、容量取得中も一覧移動や操作を妨げにくくしました。

### Mark・MarkSlot

- MarkSlot管理画面と通常画面の役割を整理しました。
- Drag ZIP、clipboard、ファイル操作後のMark保持と、リンクを含むcopy／moveの結果判定を整理しました。

### 入力・Viewer

- 主要なBrowser操作をCommand IDとdispatcherへ接続し、Menu／ContextMenu／Command Palette／double-clickの実行先を整合させました。
- 「開く」はEnterと同じ対象別動作として統合し、`..`では親directoryへ移動、directoryはMidFD内、fileは対象別openを行います。「既定アプリで開く」はfileをWindows関連付け、directoryをExplorerで開く別操作です。
- Command Paletteに「開く」を追加し、Functionバーの短縮表示を`open`へ整合させました。
- Functionバーのprofile／modifier／slot別割り当てで、明示的な未割り当て、選択slotの既定復帰、現在profile全体の既定復帰を整合させました。
- V／FD互換Shift+F9の明示Previewは、内容を判定してText／LargeText／Binary／Markdown／CSV・TSV／SQLite等のMidFD内Viewerへ表示します。file double-clickはOS既定openの別契約として保持します。
- X／FD互換F2はコマンド実行Dialogとして統合し、Enter／V／Z／Xの役割を分離しました。
- Markdown Rendered表示の同一文書内リンクはViewer内で移動し、`http`／`https`以外のschemeや相対pathの外部起動を拒否します。表示modeは設定とViewer下部StatusStrip右端から切り替えられ、Rendered右クリックでは選択部分を含む元Markdown block、link／画像の記述をコピーできます。fileと同じdirectory配下の相対PNG／JPEG／GIF画像だけをinline表示し、ブラウザ由来の標準右クリックmenuと別window起動は表示しません。
- ZIP fallbackは展開root配下の既存junction等のreparse pathを経由した書込みと、既存reparse destinationへの上書きを拒否します。unsafe entryは書込み前に検出し、通常のnested directory展開と既存file overwriteは保持します。
- tab、category、menu、Command Paletteの操作を安定化し、入力割り当てのprofile／modifier／slot表示を整理しました。

### 初回設定

- 初回セットアップ、設定復旧、表示色、外部tool設定の導線を整理しました。既存設定形式は変更していません。
- 初回／基本セットアップから縦型／横型のタブ表示を選択できるようにしました。新規の初回セットアップでは縦型（推奨）を表示し、既存の保存済みタブ表示設定は維持します。

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
