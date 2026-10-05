# 使い方ガイド

MidFDは、FDライクな操作感を参考にしつつ、現在のWindows環境向けに設計した軽量ファイラーです。

この文書では、初回セットアップ、Browser操作、Mark／MarkSlot、ファイル操作、圧縮、Viewer、設定管理を説明します。詳しいキー一覧は [KEYBINDINGS.md](KEYBINDINGS.md) を参照してください。

## 初回起動

初めて起動すると「MidFD 初回セットアップ」を表示します。

「表示」ではタブ表示を「縦型（推奨）」または「横型」から選べます。縦型は左側にcategory／tabを並べ、横型は従来どおり上部にcategory／tabを表示します。どちらのmodeでもtab／category機能を利用できます。

### 機能範囲

| 選択 | 主な内容 |
|---|---|
| 基本機能のみ | 閲覧、コピー、移動、名前変更、Mark、標準キー操作、内蔵Viewer |
| 便利機能まで使う（推奨） | 基本機能＋前回状態復元、mouse gesture、Functionバー説明、メディアEnter外部再生、パンくず表示 |
| すべての機能を使う | 便利機能＋Workspace Snapshot（Fullで既定有効、個別override可）、MarkSlot集合演算／Backup Transfer、Image Quantization、SVG Clipboard、Command PaletteのFavorite／Recent利用情報保存、MidFD管理ゴミ箱、Drag ZIP／manifest、clipboard text貼り付け |

各項目は個別に変更できます。既知の組み合わせと一致しない場合は「個別設定」と表示されます。

Command Paletteの基本検索・実行はどの機能範囲でも利用できます。機能範囲で切り替わるのはFavorite／Recent利用情報の保存です。

### 操作方式・配色

操作方式は「MidFD標準」または「FD／WinFD互換」から選びます。

配色が「既定色」の場合は、操作方式に応じた既定配色を使用します。「MidFD標準」「FD／WinFD互換」「Green」などの配色プリセットを明示選択した場合は、操作方式を変更しても選択した配色を維持します。

### 外部アプリ

7-Zip、動画tool folder、外部editorを指定できます。未設定でも基本操作は可能で、自動探索または既存fallbackを使用します。

### 後から開き直す

設定画面の「起動・ログ」から「MidFD 基本セットアップ」を再表示できます。

- 再表示時は保存済みのタブ表示を含む現在値を表示します。
- ［設定画面へ反映］: 外側の設定UIへ値を反映
- ［適用］／［OK］: 永続保存
- ［キャンセル］: 外側UIと保存済み設定を変更しない

詳しくは [PROFILES.md](PROFILES.md) を参照してください。

## Browser画面

Browser画面では、ファイルやフォルダを選択し、Mark、コピー、移動、削除、圧縮などを実行します。

### 基本操作

| 操作 | キー |
|---|---|
| 選択移動 | ↑ / ↓ / ← / → |
| ページ移動 | PageUp / PageDown |
| 開く（対象別open） | Enter |
| 明示Preview | V |
| OSの関連付け／Explorerで開く | Z |
| コマンド実行Dialog | X |
| コマンド実行Dialog（FD／WinFD互換） | F2 |
| 親directory | Backspace / Alt+↑ |
| ディレクトリ履歴を戻る／進む | Alt+← / Alt+→ |
| タブ履歴を戻る／進む | Alt+Shift+Left / Alt+Shift+Right |
| QuickAccess | Q |
| Logdsk | L |
| パス入力 | Ctrl+L |
| 再読込 | Ctrl+R / Shift+R |
| 設定 | O |
| Command Palette | Ctrl+Shift+P |

Enterは`..`で親directoryへ移動し、directoryはMidFD内で開き、fileは対象別openを行います。Vは明示Preview、XはCommand・Arguments・Working Directoryを指定するコマンド実行Dialogです。Working Directoryを空欄にすると現在のBrowser directoryを使います。ZはfileをOSの関連付けで、directoryをExplorerで開きます。ファイル一覧のdouble-clickはfileをOS既定openしますが、directoryはMidFD内で開くため、Zとはdirectoryの結果が異なります。

単一fileのContextMenuから「プログラムから開く...」を選ぶと、Windowsのアプリ選択画面で起動先を選べます。

ContextMenuの空白部分にある「新規作成」には、設定の「ファイル操作」→「新規作成の拡張子...」で追加・並べ替えた拡張子が表示されます。項目を選ぶと空のfileを作成します。file内容のtemplateを設定する機能ではありません。

「移動 -> タブ移動履歴...」では、タブ履歴のBack／現在／Forwardを一覧で確認できます。BackまたはForwardの項目をEnter／DoubleClickで選ぶと、その履歴位置へ直接移動します。これはAlt+Shift+Left／Rightと同じruntime-onlyの履歴を使い、カテゴリをまたいで移動できます。表示されるpathは過去訪問時のsnapshotではなく、対象Tabが現在保持しているstateです。

## パス表示と移動

### パンくず表示

設定でパンくず表示を有効にすると、現在pathを階層ごとのbuttonとして表示します。各segmentを選択して上位directoryへ移動できます。

直接入力へ切り替えた場合は、path文字列を編集して移動できます。

### 環境変数

パス入力では、Windowsの環境変数を展開します。

```text
%TEMP%
%USERPROFILE%\Downloads
```

引用符やbacktickで囲まれたpathも取り込めます。

### UNCパス

UNC pathではUsed／Free情報を同期取得せず、操作が落ち着いてからbackgroundで取得します。取得中または取得不能時はplaceholderのままになる場合があります。

## 表示

設定の「表示」では、一覧表示mode、date形式、size形式、fontなどを変更できます。

一覧表示modeは「ファイル名のみ」「サイズ」「サイズ・更新日時」「拡張子整列」から選べます。「拡張子整列」は同じ表示列のfileで拡張子の開始位置を揃える表示専用modeです。file名、sort、selection、Mark、file operationの対象は変わりません。Ctrl+4またはCtrl+NumPad4でも切り替えられます。「拡張子を表示」をOFFにすると拡張子は表示されません。

### size形式

- HumanReadable
- KB／MB
- Bytes

`Bytes` は実byte数を表示します。桁数が多い場合は3桁区切りを使用します。

テキストpreviewはUTF-16 LE／BEを含む文字コードを扱い、binaryと誤判定されるケースを補正します。

## Mark

MidFDでは、複数項目をMarkしてまとめて操作できます。

| 操作 | キー |
|---|---|
| Mark切り替え | Space / Insert |
| fileのみ全選択／解除 | Home |
| directoryを含む全選択／解除 | End / Ctrl+A |
| mouseで切り替え | Ctrl+左click |
| 範囲Mark | Shift+左click |
| MarkSlot | Ctrl+M |

大量folderでは一覧がページ分割される場合がありますが、全選択は現在表示ページだけでなく、読み込み済みの現在dataset全体を対象にします。

コピー、移動、圧縮、解凍などの後も、実体が残っているMarkは維持されます。削除または移動済みで存在しない項目は結果に応じて除外されます。cancel／error時は元のMarkを維持します。

Mark数と合計sizeは操作直後に更新され、Mark解除やdirectory移動後に残留表示が出ないよう補正されます。

## MarkSlot

MarkSlotは、現在Markを名前付きslotへ保存し、後から復元・管理する機能です。

### 通常画面

`Ctrl+M`で開きます。

- slotを選び、現在タブのMarkを保存できます。
- 選択slotから現在のMarkへ復元できます。
- 「管理...」の「保存範囲」から、現在カテゴリ全タブまたはWorkspace全体のMarkを選択slotへ保存できます。これらのscope保存は通常機能です。

保存または復元後は、keyboard中心でBrowserへ戻れるよう画面遷移を整理しています。

### 管理画面

管理画面ではslot一覧と内容を確認し、slotのrename／deleteや保存済みmark項目の管理ができます。現在カテゴリ／Workspaceのscope保存も利用できます。

### Full限定機能

機能範囲が「すべての機能を使う (Full)」の場合、以下を利用できます。
- **集合演算 (Set Operations)**: OR（和集合）、AND（積集合）、A-B、B-A、XORを選択し、プレビュー後に結果を現在タブへ適用するか指定スロットへ保存
- **Backup Transfer**: 選択slotまたは全slotをJSONへimport／export

## フィルタ / 検索

Fは現在の一覧を絞り込むFilter、Ctrl+FまたはF7は現在のBrowser tabをrootとして配下を再帰検索するSearchを開きます。FilterとSearchは別pageで、条件のdraftを共有しません。Ctrl+Gは既定未割当です。

### 使い分け

| やりたいこと | 開き方 | 対象 | Search content |
|---|---|---|---|
| 今見えている一覧を絞る | `F` | 現在の一覧 | - |
| 配下からfile／folder名を探す | `Ctrl+F` / `F7` | 現在tab配下を再帰 | 空 |
| 配下fileの中身を探す | `Ctrl+F` / `F7` | 現在tab配下のfileを再帰 | 検索文字列を入力 |

Filterはfilesystemを再帰検索しません。Searchの検索rootは現在のBrowser tabです。内容検索では、必要に応じてfilename条件も併用できます。

### 基本的な検索手順

1. `Ctrl+F`または`F7`でSearchを開きます。
2. 名前検索ではfilename条件を指定し、content欄を空にします。
3. 内容検索ではcontent欄へ検索文字列を入力します。必要ならfilename条件も指定します。
4. 必要に応じて拡張子、更新日時、Git条件などを指定します。
5. `Enter`で検索を開始します。
6. 結果から対象を確認またはPreviewします。内容検索ではfile、ヒット数、行、列、内容を一覧で確認できます。

内容検索用ripgrep 15.2.0は配布packageに同梱されているため、通常は別途導入する必要はありません。実際に使用したengineとversionは検索結果画面に表示されます。

Search pageはfilenameに初期focusし、`Tab`でcontentへ移動します。`Alt+F`はFilter filename、`Alt+S`はSearch filename、`Alt+C`はSearch content、`Alt+N`はactive pageのfilename、`Alt+E`はactive pageの対象拡張子欄へfocusします。`Enter`でactive pageを実行し、`Esc`でdialogを閉じます。

設定の「入力割り当て」には「機能別」「キー別」「ファンクションキー/バー」「マウスジェスチャー」の各viewがあります。キー別で「キーを追加」を選ぶと、先に追加する通常キーを押し、続けて割り当てる機能を選択します。機能選択は機能別viewと同じカテゴリ・表示順でグループ化されます。追加中のDelete／Backspaceはキー入力として扱い、Escでキャンセルします。選択keyの機能変更・解除・既定復元もでき、変更は機能別viewへすぐ反映されます。他のアプリが使うグローバルショートカットと競合する場合があり、競合時はMidFDまたは他アプリ側のキー設定を変更してください。

### モードと条件
- **フィルタ**: 現在タブの一覧を名前・詳細条件で絞り込みます。名前の正規表現にも対応します。通常Browser状態のEscで名前・詳細条件を解除します。
- **検索**: 名前欄と詳細条件で配下のfile／folderを再帰検索します。既定は大文字小文字を区別せず、必要に応じて区別できます。部分一致、wildcard、正規表現に対応します。
- Search pageのcontent欄が空なら名前検索、文字列があれば配下fileの内容検索になります。Search Regex checkboxは1つで、入力済みfilename/contentの両方へ適用します。filenameとcontentの大文字小文字指定は独立しています。
- 内容検索の文字コードはAuto／UTF-8／Shift_JIS／UTF-16 LE／UTF-16 BEから選びます。AutoはUTF-8とBOM付きUTF-16を扱い、Shift_JISの自動推定はしません。再検索時も選択を引き継ぎます。正規表現はPCRE2を使用せず、先読み・後読み・後方参照には対応しません。
- 同梱ripgrep、PATH上のripgrep、内蔵検索の順に使用します。
- 拡張子、更新日時、Git条件はフィルタと検索pageごとに入力できます。対象拡張子は`cs, md, txt`のカンマ区切りを推奨します。既存のカンマ、セミコロン、空白、tab、改行区切りも受け付けます。

### 検索結果の確認
- 検索開始直後にMainForm内へ一時検索surfaceを開き、走査件数、結果／ファイル数、ヒット数、スキップ数、現在のpathを更新します。全体件数の事前走査は行いません。内容検索は検索中から結果を順次表示し、名前検索は完了後に結果一覧を表示します。結果の到着順は保証しません。
- 進捗はcompact ringとcountを表示し、current pathは別行でellipsisします。割合・ETAは表示しません。検索が停止／完了／失敗した場合と結果surfaceが非表示の場合はring animationを停止します。
- 一時検索surfaceはfilesystem Browser tabではなく、workspace／session restore／closed-tab historyへ保存しません。縦型では検索元Browser tabのchild、横型では検索元tabの直後へ表示します。検索sessionは同時に1つで、新しい検索は既存sessionを置き換えます。
- 名前検索は1項目1行、内容検索は1ファイル1行です。結果数／ファイル数・ヒット数とスキップ数を表示します。
- ↑/↓で結果を選び、PageUp/PageDown、Home/Endで移動します。0件でも検索結果surfaceに該当なしの文と0件数を表示し、Browserへ戻らず、Enter/Ctrl+Enterは何も開きません。Ctrl+FまたはF7ですぐ再検索でき、criteriaと検索元rootを引き継ぎます。検索結果childを選ぶと選択行・内容hit・一覧位置を保ったまま結果へ戻ります。結果一覧のEscで検索sessionを閉じ、検索元Browser tabへ戻ります。
- 名前検索結果のEnter「ブラウザ表示」はsession内の確認用Browser tabを1個だけ再利用し、対象fileの親folderを表示して選択します。Ctrl+Enterは結果ごとに新しい通常Browser tabを追加します。folder結果は親folderを表示してfolder自体を選択します。確認後にEscを押すと同じ検索結果childへ戻り、選択行・filterを保ちます。検索元Browser tabと他tabのpath・selection・mark・navigation historyは変わりません。
- 内容検索のEnter「プレビュー」は再利用する確認tab、Ctrl+Enterは新しい通常tabをbacking BrowserとしてPreviewを開き、該当行へ移動します。LargeTextはfull line indexとscroll rangeを反映してからexact hit行へ移動します。ViewerのEnter／Escで検索結果childへ戻り、選択file・hit・filterを維持します。通常TextとLargeTextは検索位置を示し、MarkdownはRaw source表示へ切り替えて行を示します。binary／行Preview非対応形式は既存のPreview表示を維持します。検索sessionを閉じても確認tabと追加tabは通常Browser tabとして残ります。
- 通常BrowserからVでPreviewした場合、ViewerのEnter／Escは従来どおりBrowserへ戻ります。対象消失や起動失敗は検索結果surfaceのstatusへ表示します。
- 内容検索は←/→で同じfile内のhitを移動します。行・列・内容とhit位置を確認でき、別fileへ移動すると先頭hitに戻ります。
- 非0件の完了後はCtrl+Fで結果filterへfocusします。0件ならCtrl+FでSearch dialogを再表示します。F7はいつでもSearch dialogをcriteria引継ぎで再表示します。名前検索はrelative path/name、内容検索はrelative path/nameまたはhit行の文字列をcase-insensitive部分一致で絞ります。path一致fileはそのfileの全hitを表示し、hit行一致では一致hitだけを表示します。filterは完了snapshot内だけで処理し、filesystemを再検索しません。clearすると選択位置を可能な範囲で復元し、filter中も表示件数と選択位置を示します。filter欄のEnterは一覧へ戻り、Escはfilterをclearして一覧へ戻ります。結果一覧のEscは検索tabを閉じます。
- MainForm Function Barの**Expo**または**E 一覧出力**を実行した時だけfilter前の全結果をUTF-8 BOMなしの一時fileへ出力し、設定済み外部Editorで開きます。未設定時は既存方針でnotepadを使用します。名前検索はfull path、内容検索は `fullPath:line:column:lineText` 形式です。
- 内容検索中も到着済みhitをEnter／Ctrl+EnterでPreviewでき、検索は継続します。ViewerのEnter／Escで選択fileとhitを保った結果一覧へ戻ります。結果FilterとExpo／Eによる一覧出力は検索完了後だけ利用できます。
- 大量結果はvirtual一覧で表示します。結果件数の打切りやpaginationはありません。

## 一括リネーム (Batch Rename)

複数項目を選択またはMarkした状態で R（名前変更）を押すと、一括リネームダイアログが起動します（単一項目の場合は通常リネーム）。

### リネーム方式
- **1件ずつ確認**: 項目ごとに新しい名前を入力しながら順次変更
- **テンプレート一括リネーム**:
  - $F: 元のファイル名（拡張子なし）
  - $E: 拡張子
  - $D: 親フォルダ名
  - `$N`: 連番（ダイアログで設定した桁数を使用）
  - `$<digits>N`: 桁数を指定した連番（例: `$3N` は3桁でゼロ埋め）
- **正規表現置換**:
  - 検索パターンと置換パターンを指定
  - 大文字小文字の区別 (IgnoreCase)、複数行 (Multiline)、全置換 (Global) を制御可能

### プレビューとUndo
- 変更前後のファイル名一覧がリアルタイムにプレビュー表示されます。
- プレビューを確認し、［OK］で確定すると一括で名前が変更されます。
- 一括リネームが正常に完了したバッチは、直後であれば Ctrl+Z（元に戻す）で一括復元できます。

## コピー・移動・削除

### 対象

Markがある場合はMark対象を優先し、Markがない場合は現在選択項目を使用します。

### Copy／Move

- 同名directoryがある場合は、既存契約に従ってmergeします。
- 異なるdrive間のMoveは、destination側へのcopyが成立した後にsourceを削除します。
- source削除に失敗した場合は完全成功扱いにせず、結果を表示します。

### 属性／日時変更

属性／日時変更画面では、日時を年・月・日・時・分・秒の数値segmentで入力できます。入力後は次のfieldへ移動でき、calendarから日付を選択することもできます。

### symlink／junction

symlinkやjunctionをコピー／移動する場合、リンク先の実体へ再帰せず、リンク自体を同種のリンクとして再作成します。

リンク作成に権限が必要な場合だけ、専用helperのUAC確認を表示します。MidFD本体を常時管理者実行する必要はありません。

### 削除確認

現在directory外のMarkを含む削除では、確認Dialogに対象範囲の警告を表示し、通常のYではなく `Alt+Y` を要求します。

完全削除で複数項目を対象にする場合も、強い確認として `Alt+Y` を使用します。これは確認Dialog内の操作であり、Browser上のRedo shortcutとは別contextです。

## MidFD管理ゴミ箱

通常削除の既定はWindows標準ごみ箱です。UseMidFdManagedTrash=trueに設定し、MidFD管理ゴミ箱が利用可能な場合だけ、削除した項目を削除元と同じvolume／shareの `.midfd-trash` へ退避します。Windows標準ごみ箱とは別に管理されます。
MidFD管理ゴミ箱を使った削除はMidFDの削除Undo/Redo対象です。Windows標準ごみ箱を使う通常削除では、MidFD独自の削除Undo/Redoを保証しません。

### 管理画面

ファイルメニューまたは設定画面から「MidFD管理ゴミ箱の確認・管理」を開けます。

主な操作:

- 選択復元
- 選択完全削除
- すべて空にする
- 欠損record掃除
- 一覧更新

一覧には元path、退避path、削除日時、期限、残り日数、size、利用可否を表示します。

起動時に既存のゴミ箱実体を別の場所へ自動移動しません。recordのpathと実体を確認し、対象が `.midfd-trash` 内にある場合だけ復元・削除します。

## 圧縮・解凍

### 通常の圧縮画面

設定の「外部連携」で次から選べます。

| mode | 動作 |
|---|---|
| 自動 | 7-Zip標準Dialogを優先し、利用できない場合はMidFD簡易Dialogへfallback |
| 7-Zip標準Dialog | `7zG.exe` を使用。利用不能時は理由を表示して停止 |
| MidFD簡易Dialog | MidFD従来の圧縮画面を使用 |

7-Zip標準Dialogは、`7zG.exe` が存在し、対象pathとcommand line長が利用可能な場合に使用します。

### Drag ZIP

設定で有効にすると、ShiftまたはCtrlを押しながらdragして、現在Markを `MidFD-drag-{hash}.zip` へまとめて外部アプリへ渡せます。

- 項目上からdrag: 通常の対象判定を使用
- Browserの空白部分からdrag: 現在Markを使用
- manifest有効時: `_midfd_drag_manifest.txt` をZIP rootへ追加
- manifestにはlocal pathを含む場合があるため、外部共有前に確認

folderを含む場合は配下のfileを再帰的に収集し、同じfileが重複entryにならないよう整理します。

## clipboard貼り付け

設定を有効にすると、clipboardのtextを `.txt` fileとして作成できます。この機能は誤作成防止のため通常OFFを推奨します。

画像貼り付け、text貼り付け、file copy貼り付けで新規作成したfileは、条件を満たす場合に `Ctrl+Z` で取り消せます。Redoによる再作成は対象外です。

## Viewer

### text

- `Esc`: 閉じる
- `Ctrl+A`: 全選択
- `Ctrl+C`: copy

LargeText Viewerでは大容量file向けの表示経路を使用します。通常選択、Shift+click範囲選択、Ctrl+A、Ctrl+Cに対応します。

### 画像

- dragで矩形選択
- `Ctrl+C` で選択範囲copy。選択なしの場合は全画像copy
- 右／左90度回転
- 左右／上下反転
- 画像情報表示

変換は表示用であり、元fileを書き換えません。

### Markdown・CSV／TSV・SQLite

Markdown、CSV／TSV、SQLiteはread-only previewです。Markdownは設定の「表示」→「ビューア」とViewer下部StatusStrip右端の`Rendered`／`Raw`から表示を切り替えられます。編集する場合は外部editorまたはMidEditor等を使用してください。

MarkdownのRendered表示で同一文書内の見出しリンクを選ぶと、Viewer内で該当位置へ移動します。`http`／`https`リンクだけは確認後にOSの既定ブラウザで開きます。相対path、`file:`、`javascript:`、その他のschemeは開きません。

Rendered表示の右クリックmenuはMidFDの項目だけを表示します。選択範囲がある場合は選択textを優先してコピーでき、選択部分を含むsource-mapped Markdown blockを元sourceのままコピーできます。選択がない場合はblock、link、画像に記述したpathと元Markdownをコピーできます。Markdown fileと同じdirectory配下のPNG／JPEG／GIF相対画像だけをinline表示し、remote URL、absolute path、`file:`、`data:`などは自動取得しません。

## 動画・音声

動画静止画previewには `ffmpeg.exe` が必要です。`ffprobe.exe` が利用できる場合は動画長やcodec情報を取得します。

メディアEnter外部再生がOFFの場合:

| キー | 動作 |
|---|---|
| Enter | 動画静止画preview |
| Ctrl+Enter | 外部再生 |

ONの場合:

| キー | 動作 |
|---|---|
| Enter | 外部再生 |
| Ctrl+Enter | 動画静止画preview |

音声は設定に関係なく外部再生します。`ffplay.exe` が見つからない場合はWindowsの関連付けで開きます。

## タブ・カテゴリ

タブ表示は「縦型」と「横型」から選べます。縦型では左側にcategory／tabをまとめ、横型では上部にcategory／tabを表示します。初回セットアップでは縦型（推奨）が選択され、後から「基本セットアップ」で変更できます。

- `Ctrl+T`: 新規tab
- `Ctrl+W`: 現在tabを閉じる
- `Ctrl+Tab` / `Ctrl+Shift+Tab`: 次／前のtab
- `Ctrl+Shift+Right` / `Ctrl+Shift+Left`: 次／前のcategory
- `Ctrl+Alt+Right` / `Ctrl+Alt+Left`: categoryを移動
- `Ctrl+Shift+N`: category追加

縦型navigationでは通常Browser tabを右クリックし、「グループを作成...」「グループへ移動」「グループから外す」を選べます。「グループへ移動」から現在とは別のGroupを選択できます。新しいGroupには入力した名前が付き、tabの表示だけをまとめます。tab行をGroupの見出しへdropすると末尾へ移動し、Group member行の上半分／下半分へdropするとその前／後へ挿入します。Category行へdropするとGroupから外れてcategory直下へ戻ります。Group nodeのEnter／Spaceまたは開閉アイコンで折りたたみ／展開できます。Group名はGroup nodeのContext Menuから変更します。tabの右クリックContext Menuは `Shift+F10` またはContext Menuキーでも開けます。Command Paletteと設定の「入力割り当て」にもGroup操作があり、既定のshortcutはありません。

Group配下のtabは通常Browser tabのままなので、path、選択、Mark、history、Preview、closeは従来どおりです。タブグループは同一category内に限られ、tabは一度に1つのGroupへ所属します。別Groupへ追加すると所属先が移り、最後のmemberを外すとGroupも消えます。Group自体を閉じる操作や入れ子Groupはありません。横型tab stripではmember tabsを通常tab順に連続表示し、Group名をprefixに付けます。

Tab GroupはMidFDの正常終了後も復元されます。Group ID、名前、所属tab、開閉状態を、通常のBrowser tab順を保って復元します。Search結果の一時階層とSearch lineageは復元しません。Searchから開いたnormal Browser tabは通常tabとして復元され、明示的に作成したGroupへの所属があればその所属も復元します。active Search session中のresult tabはGroupへ追加できません。Searchを閉じると明示的に追加でき、追加後はSource tabとのSearch-derived階層から外れます。Source tabをGroupへ入れても、その配下にあるSearch childとresult tabsは維持されます。

ContextMenuのdirectory項目では「新しいtabで開く」を選べます。複数directoryを選択またはMarkしている場合は、それぞれを新しいtabで一括して開けます。新しいtabの追加位置は設定から、現在tabの隣またはtab列末尾を選択できます。

成功した一括tab openはUndo／Redoの1操作として記録され、対応するfile操作と共通の時系列履歴から `Ctrl+Z`／`Ctrl+Y` で戻す・やり直すことができます。

Browser一覧focus時に`@`を押してから1文字入力すると、その文字で始まる最初の項目へ移動します。入力は1文字で完了し、複数文字で名前を探す場合は`Ctrl+F` / `F7`のSearchを使います。これはFilterではなく、表示中のFilter・並び順・Markを変えません。文字入力前のEnterはジャンプ待機だけを終了し、Escではジャンプだけを終了します。

### Workspace Snapshot

設定の「表示」または「基本セットアップ」でWorkspace Snapshotを有効にすると（Fullプロファイルの既定、または個別Override）、Workspace全体の状態を名前付きスナップショットとして保存・復元できます。

- **管理ダイアログ**: メニュー「ツール」→「Workspace スナップショット...(&W)」から開きます。
  - スナップショット一覧の確認
  - 選択スナップショットの復元（全カテゴリ・タブ状態のトランザクション復元）
  - スナップショットの名前変更、削除
  - JSON形式でのエクスポート／インポート
  - 現在のWorkspaceの新規保存
- **タブ右クリックからの保存**: タブの右クリックメニューに「現在のWorkspaceをスナップショット保存...」が表示されます。
  - 選ぶと、右クリックしたタブへアクティブを切り替えることなく、現在のWorkspace全体（全カテゴリ・全タブ）をそのまま保存します。右クリックしたタブ単体を保存する機能ではありません。

前回状態復元を有効にすると、category、tab、path、cursor位置等を起動時に復元します。詳細は設定の「起動・ログ」で変更できます。

## QuickAccess・Command Palette

QuickAccessは、よく使う場所をcategory付きで管理します。検索欄では数字を含む文字列も検索でき、一覧focus時は `1`〜`9` で表示候補へ直接移動できます。

Command Paletteでは、機能、設定、外部toolを検索して実行できます。

## 設定の保存・移行

設定の正本はSQLiteです。

```text
Data\Settings\settings.db
```

従来の `settings.json` は初回import元として参照する場合がありますが、通常保存では再生成しません。

### export／import

設定画面からJSON形式でexport／importできます。

- import前に内容とversionを確認する
- import成功後はruntime設定へ即時反映する
- backupや一部警告は結果Dialogへ表示する
- 未対応の新しいpayload versionは既定値で上書きしない

### 復旧

設定DB読込に失敗した場合はbackup復旧を試みます。復旧できない場合は既定値で起動し、状況を通知します。standalone backupは最大5世代保持します。

## 外部ツール

外部toolには信頼できる実行fileを指定してください。pathに空白、日本語、shell記号が含まれる場合も、引数を文字列連結せず個別に渡す経路を使用します。

## 注意・免責

MidFDは実fileを変更するアプリケーションです。重要なfileを扱う前に必要なbackupを用意してください。本ソフトウェアは無保証で提供されます。
