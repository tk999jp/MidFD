# 使い方ガイド

MidFDは、FDライクな操作感を参考にしつつ、現在のWindows環境向けに設計した軽量ファイラーです。

この文書では、初回セットアップ、Browser操作、Mark／MarkSlot、ファイル操作、圧縮、Viewer、設定管理を説明します。詳しいキー一覧は [KEYBINDINGS.md](KEYBINDINGS.md) を参照してください。

## 初回起動

初めて起動すると「MidFD 初回セットアップ」を表示します。

### 機能範囲

| 選択 | 主な内容 |
|---|---|
| 基本機能のみ | 閲覧、コピー、移動、名前変更、Mark、標準キー操作、内蔵Viewer |
| 便利機能まで使う（推奨） | 基本機能＋前回状態復元、mouse gesture、Functionバー説明、メディアEnter外部再生、パンくず表示 |
| すべての機能を使う | 便利機能＋MidFD管理ゴミ箱、Drag ZIP、manifest、clipboard text貼り付け |

各項目は個別に変更できます。既知の組み合わせと一致しない場合は「個別設定」と表示されます。

### 操作方式・配色

操作方式は「MidFD標準」または「FD／WinFD互換」から選びます。

操作方式を実際に変更した場合だけ、対応する標準配色へ連動します。画面を開いただけでは保存済み配色を変更しません。連動後も別のbuilt-in／user presetへ変更できます。

### 外部アプリ

7-Zip、動画tool folder、外部editorを指定できます。未設定でも基本操作は可能で、自動探索または既存fallbackを使用します。

### 後から開き直す

設定画面の「起動・ログ」から「MidFD 基本セットアップ」を再表示できます。

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
| 開く／表示 | Enter |
| OSの関連付けで開く | Z |
| 親directory | Backspace / Alt+↑ |
| 履歴を戻る／進む | Alt+← / Alt+→ |
| QuickAccess | Q |
| Logdsk | L |
| パス入力 | Ctrl+L |
| 再読込 | Ctrl+R / Shift+R |
| 設定 | O |
| Command Palette | Ctrl+Shift+P |

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

- slotを選ぶ
- 現在Markを保存
- slotから復元

保存または復元後は、keyboard中心でBrowserへ戻れるよう画面遷移を整理しています。

### 管理画面

通常画面から管理画面を開くと、slot一覧と内容を編集できます。選択中slotと現在Markは別領域で確認できます。

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

MidFD管理ゴミ箱を有効にすると、通常削除した項目を削除元と同じvolume／shareの `.midfd-trash` へ移します。

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

### Markdown・SQLite

MarkdownとSQLiteはread-only previewです。編集する場合は外部editorまたはMidEditor等を使用してください。

## 動画・音声

動画静止画previewには `ffmpeg.exe` が必要です。`ffprobe.exe` が利用できる場合は動画長やcodec情報を取得します。

メディアEnter外部再生がOFFの場合:

| キー | 動作 |
|---|---|
| Enter / V | 動画静止画preview |
| Ctrl+Enter | 外部再生 |

ONの場合:

| キー | 動作 |
|---|---|
| Enter | 外部再生 |
| Ctrl+Enter / V | 動画静止画preview |

音声は設定に関係なく外部再生します。`ffplay.exe` が見つからない場合はWindowsの関連付けで開きます。

## タブ・カテゴリ

- `Ctrl+T`: 新規tab
- `Ctrl+W`: 現在tabを閉じる
- `Ctrl+Tab` / `Ctrl+Shift+Tab`: 次／前のtab
- `Ctrl+Shift+Right` / `Ctrl+Shift+Left`: 次／前のcategory
- `Ctrl+Alt+Right` / `Ctrl+Alt+Left`: categoryを移動
- `Ctrl+Shift+N`: category追加

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
