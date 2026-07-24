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
| 開く／表示 | Enter |
| Windowsの関連付けで開く | Z |
| コマンド実行 | X |
| PowerShell | H |
| コマンドプロンプト | Shift+H |
| パス入力 | Ctrl+L |
| QuickAccess | Q |
| Logdsk | L |
| MarkSlot | Ctrl+M |
| 圧縮／解凍 | P / U |
| 設定 | O / Alt+F5 |
| Command Palette | Ctrl+Shift+P |

## Browser移動

| 操作 | キー |
|---|---|
| 上下移動 | ↑ / ↓ |
| 複数列で左右移動 | ← / → |
| ページ移動 | PageUp / PageDown |
| 親directory | Backspace / Alt+↑ |
| drive root | `\`（円記号／backslash） |
| 履歴を戻る | Alt+← / Ctrl+Backspace |
| 履歴を進む | Alt+→ |
| パス入力 | Ctrl+L |
| 再読込 | Ctrl+R / Shift+R |
| filter | F / Ctrl+F / F7 |
| QuickAccess | Q / F8 |
| Logdsk | L / F9 |
| tree | T |

パス入力では `%TEMP%`、`%USERPROFILE%` 等の環境変数を展開します。

## file・folder操作

| 操作 | キー |
|---|---|
| 開く／内蔵Viewer | Enter |
| 関連付けで開く | Z |
| 外部editor | E / F4 |
| copy Dialog | C / F3 |
| move Dialog | M |
| 名前変更 | R / F2 |
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
| Command Palette | Ctrl+Shift+P / F10 |
| Command一覧 | F12 |
| system情報 | I |

## tab・category

| 操作 | キー |
|---|---|
| 次のtab | Ctrl+Tab / Ctrl+Right |
| 前のtab | Ctrl+Shift+Tab / Ctrl+Left |
| 新規tab | Ctrl+T |
| tabを閉じる | Ctrl+W |
| tab固定／解除 | Ctrl+Shift+L |
| 次のcategory | Ctrl+Shift+Right |
| 前のcategory | Ctrl+Shift+Left |
| categoryを右へ移動 | Ctrl+Alt+Right |
| categoryを左へ移動 | Ctrl+Alt+Left |
| 新規category | Ctrl+Shift+N |

## 表示mode

| 操作 | キー |
|---|---|
| file名のみ | Ctrl+1 |
| file名＋size | Ctrl+2 |
| file名＋size＋更新日時 | Ctrl+3 |

## Viewer共通

| 操作 | キー |
|---|---|
| 閉じる | Esc |
| copy | Ctrl+C |
| 全選択 | Ctrl+A |

Browser側でcustomizeしたkeyより、Viewer固有keyが優先されます。

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
| Enter / V | 動画静止画preview |
| Ctrl+Enter | 外部再生 |

ONの場合:

| キー | 動作 |
|---|---|
| Enter | 外部再生 |
| Ctrl+Enter / V | 動画静止画preview |

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
| F7 | Filter |
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
| F2 | Check / Execute |
| F3 | Copy |
| F4 | Edit |
| F5 | Rename |
| F6 | Sort |
| F7 | Filter |
| F8 | Tree |
| F9 | Logdsk |
| F10 | Unpack |
| F11 | Top |
| F12 | Bottom |

### FD／WinFD互換時のShift+F

| キー | 操作 |
|---|---|
| Shift+F1 | 属性／日時変更 |
| Shift+F3 | Move |
| Shift+F5 | 新規folder |
| Shift+F6 | PowerShell |
| Shift+F7 | Reload |
| Shift+F8 / Shift+Enter | 外部editor |
| Shift+F9 | Preview |
| Shift+F10 | Pack |

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

- Browser keybind
- Functionバーの通常／Shift／Ctrl／Alt layer
- mouse gesture

同じkeyを複数Commandへ割り当てた場合は競合として検出し、保存を停止します。Viewer、Archive Contents、各Dialogの固有keyはBrowser customizeの対象外です。
