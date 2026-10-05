# キーバインド

MidFDの主なキー操作です。Browser画面のkeybindは設定から変更できます。Viewerや各Dialogには、それぞれ固有のkey契約があります。

## 操作方式

| 操作方式 | 内容 |
|---|---|
| MidFD標準 | 現代的なshortcutとMidFD標準Functionバー |
| FD／WinFD互換 | Fキー、Shift+F、数字キーの一部をFD／WinFD寄りにする |

FD／WinFD互換は、利用できる機能範囲を制限する設定ではありません。オリジナルFDとの完全互換でもありません。

## 最初に覚えるキー

| 操作 | キー |
|---|---|
| 開く（対象別open） | Enter |
| 既定アプリ／Explorerで開く | Z |
| コマンド実行 | X |
| コマンド実行Dialog（FD／WinFD互換） | F2 |
| 明示Preview | V |
| PowerShell | H |
| コマンドプロンプト | Shift+H |
| パス入力 | Ctrl+L |
| QuickAccess | Q |
| Logdsk | L |
| MarkSlot | Ctrl+M |
| 圧縮／解凍 | P / U |
| 設定 | O / Alt+F5 |
| Command Palette | Ctrl+Shift+P |

F1〜F12とShift+F1〜F12は操作方式により既定割り当てが異なるため、後半のプロファイル別表を参照してください。

Enterは`..`で親directoryへ移動し、directoryはMidFD内で開き、fileは対象別openを行います。Vは明示Preview、XはCommand・Arguments・Working Directoryを指定するコマンド実行Dialogです。Working Directoryを空欄にすると現在のBrowser directoryを使います。ZはfileをWindowsの関連付けで、directoryをExplorerで開きます。ファイル一覧のdouble-clickはfileをOS既定openしますが、directoryはMidFD内で開くため、Zとはdirectoryの結果が異なります。

### Functionバーの割り当て

Functionバーは、操作方式（profile）・modifier・slotごとに個別変更できます。

- `(無効)`はそのslotの明示的な未割り当てで、profileの既定操作へ自動的には戻りません。
- 「選択項目を既定に戻す」は、選択中のslotだけをそのprofileの既定操作へ戻します。
- 「現在の全割り当てを既定に戻す」は、現在のprofileの割り当て全体を既定操作へ戻します。
- 予約されたslotは通常のCommand候補として選択できず、必要な予約状態と編集保護を維持します。

## Browser移動

| 操作 | キー |
|---|---|
| 上下移動 | ↑ / ↓ |
| 複数列で左右移動 | ← / → |
| ページ移動 | PageUp / PageDown |
| 親directory | Backspace / Alt+↑ |
| drive root | `\`（円記号／backslash） |
| ディレクトリ履歴を戻る | Alt+← / Ctrl+Backspace |
| ディレクトリ履歴を進む | Alt+→ |
| パス入力 | Ctrl+L |
| 再読込 | Ctrl+R / Shift+R |
| フィルタ | F |
| 再帰検索 | Ctrl+F / F7 |
| 現在のフィルタを解除 | Esc（名前・詳細条件。通常Browser状態） |
| QuickAccess | Q |
| Logdsk | L / F9 |
| tree | T |

パス入力では `%TEMP%`、`%USERPROFILE%` 等の環境変数を展開します。

通常Browser状態でUnified Filter（名前または詳細条件）が有効なとき、Escは現在TabのFilter条件を解除します。Markがある場合も最初のEscではMarkを保持し、Filter解除後の次のEscで従来どおりMarkを解除します。Filterが無効なときは既存のMark解除／終了確認へ戻り、Path Entry、file operation、Viewer、DialogのEsc処理を優先します。

## Filter / Search

| 操作 | キー／条件 | 説明 |
|---|---|---|
| 現在一覧Filter | `F` | 現在tabに表示中の一覧だけを絞り込みます。filesystemを再帰検索しません。 |
| Search | `Ctrl+F` / `F7` | 現在のBrowser tabをrootとして配下を再帰検索します。 |
| 名前検索 | Search contentを空にする | filename条件で配下のfile／folderを検索します。 |
| 内容検索 | Search contentへ入力する | 配下fileの内容を検索します。必要に応じてfilename条件も併用できます。 |

FilterとSearchはdialog内の別pageで、draftを共有しません。Searchはfilenameに初期focusし、`Tab`でcontentへ移動します。`Ctrl+G`は既定未割当です。`Alt+F`はFilter filename、`Alt+S`はSearch filename、`Alt+C`はSearch content、`Alt+N`はactive pageのfilename、`Alt+E`はactive pageの拡張子欄へfocusします。`Enter`でactive pageを実行し、`Esc`でdialogを閉じます。

SearchのRegex checkboxは入力済みfilename/contentの両方へ適用します。filenameとcontentのcase-sensitive設定は独立しています。拡張子は`cs, md, txt`のように指定でき、カンマ、セミコロン、空白、tab、改行で区切れます。内容検索の文字コードはAuto／UTF-8／Shift_JIS／UTF-16 LE／UTF-16 BEから選べます。AutoはUTF-8とBOM付きUTF-16を扱い、Shift_JISを自動推定しません。正規表現の詳細は[USER_GUIDE.md](USER_GUIDE.md)を参照してください。

### 検索結果の操作

| 結果画面操作 | 動作 |
|---|---|
| `↑` / `↓`、`PageUp` / `PageDown`、`Home` / `End` | file／resultを移動します。 |
| `←` / `→` | 内容検索で同一file内のhitを移動します。 |
| `Enter` | 名前検索は確認用Browserで開き、内容検索はPreviewします。 |
| `Ctrl+Enter` | 新しい通常tabを使って開く／Previewします。 |
| `Ctrl+F` | 検索完了後、非0件なら結果filterへfocusします。0件ならcriteriaを引き継いだSearch dialogを開きます。 |
| `F7` | criteriaを引き継いだSearch dialogを開きます。 |
| `Esc` | 結果filter欄ではfilterをclearして一覧へ戻り、それ以外では検索結果を閉じて検索元Browserへ戻ります。 |

結果filter欄で`Enter`を押すと一覧へ戻ります。結果filterは検索完了後に利用でき、case-insensitive部分一致でrelative path／nameまたは内容hit行を絞り込みます。path／nameに一致したfileは全hitを表示し、hit行に一致した場合は一致hitだけを表示します。filesystemを再検索せず、完了snapshot内を絞り込みます。clear後は選択位置を可能な範囲で復元します。

検索開始直後にMainForm内の一時検索surfaceを開き、走査件数、結果／file数、hit数、skip数、ellipsis付きcurrent pathを更新します。compact ringとcountで進捗を示し、検索停止／完了時またはsurface非表示時はring animationを停止します。全体件数の事前走査は行いません。内容検索は検索中から到着済み結果を順次表示し、名前検索は完了後に一覧を表示します。内容検索は1file 1rowで、file数／hit数とhitの行／列／内容を確認できます。

一時検索surfaceはfilesystem Browser tabではなく、Browser tab数にも含めず、workspace／session restore／closed-tab historyへ保存しません。縦型では検索元Browser tabのchild、横型では検索元tabの直後へ表示します。検索sessionは同時に1つで、新しい検索は既存sessionを置き換えます。0件でも該当なしと0件数を表示し、`Enter`／`Ctrl+Enter`では何も開きません。結果数の打切りやpaginationはありません。

名前検索の`Enter`はsession内の確認用Browser tabを再利用し、対象fileの親folderを表示して選択します。folder結果は親folderを表示してfolder自体を選択します。確認Browserからは`Esc`で同じ検索結果へ戻ります。内容検索の`Enter`は確認tabでPreviewし、hit行へ移動します。どちらも`Ctrl+Enter`では新しい通常Browser tabを使います。検索元Browser tabのpath・selection・mark・navigation historyは変わらず、追加tabは検索sessionを閉じた後も残ります。

PreviewからViewerの`Enter`／`Esc`で同じ検索結果へ戻り、選択file・hit・filterを維持します。LargeTextではfull line index後にexact hit行へ移動します。通常Text／LargeTextは検索位置を示し、MarkdownはRaw source表示で行を示します。binary／行Preview非対応形式では既存のPreview表示を維持します。通常Browserから`V`でPreviewした場合は従来どおりBrowserへ戻ります。

検索中も到着済み内容hitはPreviewできます。検索完了後はMainForm Function Barの**Expo**または**E 一覧出力**から、filter前の全snapshotをUTF-8 BOMなしの一時fileへ出力し、設定済み外部Editorで開けます。Editor未設定時は既存方針でnotepadを使用します。名前検索はfull path、内容検索は`fullPath:line:column:lineText`形式です。

## file・folder操作

| 操作 | キー |
|---|---|
| 開く／対象別open | Enter |
| 明示Preview | V |
| 関連付けで開く／Explorerで開く | Z |
| コマンド実行Dialog（FD／WinFD互換） | F2 |
| 外部editor | E |
| copy Dialog | C / F3 |
| move Dialog | M |
| 名前変更／複数対象のBatch Rename | R |
| 削除 | D / Delete |
| 新規folder | K |
| 新規file | N |
| 属性／日時変更 | A |
| 圧縮 | P |
| 解凍 | U |
| full path copy | Ctrl+Shift+C |
| clipboardへcopy | Ctrl+C |
| clipboardへcut | Ctrl+X |
| clipboardからpaste | Ctrl+V |
| Undo | Ctrl+Z / Alt+Z |
| Redo | Ctrl+Y / Alt+Y |

複数directoryの「新しいtabで開く」が成功すると、ひとまとまりのUndo／Redo操作として記録され、対応するfile操作と共通の時系列履歴に入ります。

### 削除確認DialogのAlt+Y

次の場合、削除確認Dialog内で強い確認として `Alt+Y` を要求します。

- 現在directory外のMarkを含む削除
- 複数項目の完全削除

この `Alt+Y` は確認Dialogが開いている間だけ有効です。Browser画面上の `Alt+Y` はRedoです。

## Mark

| 操作 | キー |
|---|---|
| Mark切り替え | Space / Insert |
| fileのみ全選択／解除 | Home |
| directoryを含む全選択／解除 | End / Ctrl+A |
| mouseでMark切り替え | Ctrl+左click |
| 範囲Mark | Shift+左click |
| 範囲Mark | Ctrl+Shift+左click |
| Mark関連操作 | Tab |
| MarkSlot | Ctrl+M |

大量folderで一覧がページ分割されていても、全選択は読み込み済みdataset全体を対象にします。

## MarkSlot

| 操作 | キー／操作 |
|---|---|
| MarkSlot画面 | Ctrl+M |
| slot選択 | ↑ / ↓ |
| 決定 | Enter |
| 閉じる | Esc |
| 管理画面 | 画面内の管理button |

## Command・shell

| 操作 | キー |
|---|---|
| コマンド実行Dialog | X |
| PowerShell | H |
| コマンドプロンプト | Shift+H |
| Explorer | Alt+F2 |
| 新規MidFD instance | Alt+F1 |
| コントロールパネル | Alt+F3 |
| 設定 | O / Alt+F5 |
| Command Palette | Ctrl+Shift+P |
| Command一覧 | F12（MidFD標準） |
| system情報 | I |

## tab・category

| 操作 | キー |
|---|---|
| 次のtab | Ctrl+Tab / Ctrl+Right |
| 前のtab | Ctrl+Shift+Tab / Ctrl+Left |
| タブ履歴を戻る | Alt+Shift+Left |
| タブ履歴を進む | Alt+Shift+Right |
| タブ移動履歴一覧 | 移動メニュー -> タブ移動履歴... |
| 新規tab | Ctrl+T |
| tabを閉じる | Ctrl+W |
| tab固定／解除 | Ctrl+Shift+L |
| 次のcategory | Ctrl+Shift+Right |
| 前のcategory | Ctrl+Shift+Left |
| categoryを右へ移動 | Ctrl+Alt+Right |
| categoryを左へ移動 | Ctrl+Alt+Left |
| 新規category | Ctrl+Shift+N |

Tab Groupの作成／追加／解除／名前変更はCommand Paletteと設定の「入力割り当て」から実行できます。既定shortcutはありません。縦型navigation treeのtabまたはGroupを選び、Context Menuキー／`Shift+F10`で操作menuを開けます。Group nodeのEnter／Spaceで折りたたみ／展開します。

ContextMenuのdirectory項目では「新しいtabで開く」を選べます。複数directoryを選択またはMarkしている場合は一括して開けます。新しいtabの追加位置は設定から、現在tabの隣またはtab列末尾を選択できます。

Workspace Snapshotのshortcutはありません。Fullでは既定有効で、設定または基本セットアップから個別に有効／無効を変更できます。有効時はtabの右クリックmenuから「現在のWorkspaceをスナップショット保存...」を選べます。右クリックしたtabへ切り替えず、現在のWorkspace全体を保存します。

## 表示mode

| 操作 | キー |
|---|---|
| 頭文字ジャンプ | `@`の後に1文字入力（Browser一覧focus時） |
| file名のみ | Ctrl+1 |
| file名＋size | Ctrl+2 |
| file名＋size＋更新日時 | Ctrl+3 |
| 拡張子整列 | Ctrl+4 / Ctrl+NumPad4 |

Browser一覧focus時に`@`を押すとone-shot待機を開始します。次に入力した表示可能な1文字で現在一覧の表示順を大文字・小文字を区別せずに検索し、matchした場合だけcursorを移動して、その時点でジャンプ待機を終了します。`..`は対象外です。Filter／Mark／sortは変更しません。文字入力前のEnter／Escはcommandを実行せず、ジャンプ待機だけを終了します。複数文字で名前を探す場合は`Ctrl+F` / `F7`のSearchを使います。

## Viewer共通

| 操作 | キー |
|---|---|
| 閉じる／起点へ戻る | Enter / Esc |
| copy | Ctrl+C |
| 全選択 | Ctrl+A |

通常BrowserからVでPreviewした場合はBrowserへ戻ります。内容検索結果から起動したPreviewは検索結果childへ戻ります。Browser側でcustomizeしたkeyより、Viewer固有keyが優先されます。

## text／LargeText Viewer

| 操作 | キー |
|---|---|
| 検索 | Ctrl+F |
| 次を検索 | F3 |
| 前を検索 | Shift+F3 |
| 全選択 | Ctrl+A |
| copy | Ctrl+C |
| 閉じる | Esc |

LargeText Viewerでは、大容量file向けの表示経路を使います。通常選択、Shift+click範囲選択、Ctrl+A、Ctrl+Cに対応します。

## 画像Viewer

| 操作 | キー／mouse |
|---|---|
| 矩形選択 | mouse drag |
| 選択範囲copy／全画像copy | Ctrl+C |
| 閉じる | Esc |

回転、反転、画像情報はmenuまたは画面内操作から実行します。

## 動画静止画preview

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

preview画面:

| 操作 | キー |
|---|---|
| seek | ← / → |
| 大きくseek | Shift+← / Shift+→ |
| 先頭 | Home |
| 現在位置付近から外部再生 | Ctrl+Enter |
| 閉じる | Esc |

音声fileは設定に関係なく外部再生します。

## 標準Functionキー

| キー | 既定操作 |
|---|---|
| F1 | Help |
| F2 | Rename |
| F3 | Copy |
| F4 | 外部editor |
| F5 | Reload |
| F6 | Sort |
| F7 | Recursive Search |
| F8 | QuickAccess |
| F9 | Logdsk |
| F10 | Command Palette |
| F11 | MarkSlot |
| F12 | Command一覧 |

Shift／Ctrl／Alt＋F1〜F12は、Functionバー割り当てから個別に変更できます。

## FD／WinFD互換Functionキー

| キー | 既定操作 |
|---|---|
| F1 | Help |
| F2 | eXec（Xと同じコマンド実行Dialog） |
| F3 | Copy |
| F4 | Delete |
| F5 | Rename |
| F6 | Sort |
| F7 | Recursive Search |
| F8 | Tree |
| F9 | Logdsk |
| F10 | Unpack |
| F11 | Top |
| F12 | Bottom |

### FD／WinFD互換時のShift+F

| キー | 操作 |
|---|---|
| Shift+F1 | 属性／日時変更 |
| Shift+F2 | system情報 |
| Shift+F3 | Move |
| Shift+F4 | Delete |
| Shift+F5 | 新規folder |
| Shift+F6 | sHell（Command Prompt） |
| Shift+F7 | Reload |
| Shift+F8 | 外部editor |
| Shift+F9 | Preview |
| Shift+F10 | Pack |
| Shift+F11 | QuickAccess |
| Shift+F12 | 未割り当て |

`Shift+Enter` もFD／WinFD互換時の外部editor別名キーです。

## 外部tool Alt slot

| 操作 | キー |
|---|---|
| launcher表示 | Alt |
| slot直接起動 | Alt+英数字 |
| 選択移動 | ↑ / ↓ / Home / End / PageUp / PageDown |
| 起動 | Enter |
| 閉じる | Esc |

Alt+英数字slotとAlt+F1〜F12のFunction layerは別の設定です。

## Drag ZIP

Drag ZIPを有効にしている場合、ShiftまたはCtrlを押しながらdragすると、現在MarkをZIP 1個へまとめて外部アプリへ渡します。

Browserの空白部分からdragした場合も現在Markを使用します。manifest有効時は `_midfd_drag_manifest.txt` を同梱します。

## customize

設定の「入力割り当て」では次を変更できます。

- Browser keybind（機能別／キー別）
- Functionバーの通常／Shift／Ctrl／Alt layer
- mouse gesture

キー別では通常キーから機能を逆引きし、「キーを追加」では先にキーを入力してから機能を選択します。機能選択は機能別viewと同じカテゴリ・表示順でグループ化されます。追加中はDelete／Backspaceもキー入力として扱い、Escでキャンセルします。機能別viewからのcommand-first編集と、選択keyの機能変更・解除も維持します。割当済みkeyを別機能へ移す場合は現在の機能と新しい機能を示す確認を表示し、承認時だけ移動します。他のアプリが使うグローバルショートカットと競合する場合があるため、MidFDまたは他アプリ側のキー設定を変更してください。MidFDはOSや他プロセスのグローバルショートカットを抑制しません。Viewer、Archive Contents、各Dialogの固有keyはBrowser customizeの対象外です。
