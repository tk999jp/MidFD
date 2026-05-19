# Decision Log

## 2026-05-19: VideoStill ImageViewerForm closeout and documentation alignment
- **結論: VideoStill（動画静止画プレビュー）の正本表示先を `ImageViewerForm`（画像プレビュー画面）とし、動画のタイムラインシークおよび外部再生機能の統合仕様を確定した。**
  - **背景**: 開発過程で MainForm 側のインラインプレビューや専用 FunctionBar の追加、あるいは `ImageViewerForm` での表示などが混在・検討され、最終的な挙動にブレが生じていた。ユーザー実機での検証を経て、最も機能的かつ整合性がある「`ImageViewerForm` での統合プレビュー」を正本と定義し、ドキュメントを一貫した記述に統一した。
  - **仕様詳細**:
    - **正本表示先**: 動画静止画プレビューは `ImageViewerForm` で開く。
    - **MainForm 側の挙動**: 動画ファイル選択時は `Enter / V : 画像プレビューで静止画表示 / Ctrl+Enter : 外部再生` という案内メッセージ（ラベル）をプレビュー領域に表示するだけとし、バックグラウンドでの不要なフレーム生成は行わない。
    - **キーボード＆マウス操作**: `Enter` または `V` で `ImageViewerForm` を開き、下部に表示される細い位置シークバーでのマウスクリックによる位置指定、`← / →`（ステップ移動）、`Shift + ← / →`（大ステップ移動）、`Home`（0秒戻し）をサポート。
    - **外部再生連携**: MainForm 上および `ImageViewerForm` 上での `Ctrl + Enter` ショートカットキーは、現在プレビュー中の秒数位置（または0秒）を引数に渡して `ffplay.exe`（見つからない場合は既定のシステムプレイヤー）を起動して外部再生する。
    - **下部黒帯問題の解消**: `ImageViewerForm` での VideoStill プレビュー表示の際、Windows フォームの `statusStrip` がレイアウト上不要な黒帯を生成していたため、動画プレビュー時は `statusStrip.Visible = false` とし、最下部には細いシークバーのみを残すように実装。通常画像ロード時は `statusStrip.Visible = true` で従来仕様を維持する。
    - **初期表示位置**: `0秒` を初期位置の標準とする。設定ファイル（`VideoSkipSeconds`）に旧既定値10秒などが残っている場合でも、ユーザーが明示的に設定した秒数もしくは `0秒` を基準として動作するよう整理。
    - **非同期・外部連携の制限**:
      - `ffplay` への再生開始位置指定（`-ss`）は高速再生（デコーダー初期化位置等）の仕様に準ずるため、厳密なプレビューフレームとの一致ではなく、その近傍（キーフレーム等）からの再生開始となる。
      - `ffmpeg.exe`, `ffplay.exe`, `ffprobe.exe` は MidFD 自体には同梱せず、外部ツール連携設定を介して利用者が導入する設計とする。


## 2026-05-18: Archive contents implicit directory synthesis corrective
- **結論: ZIP アーカイブ内に明示的な親ディレクトリエントリが存在しないケース（`case_ng.zip` 等）においても、正常にプレビュー上で階層を辿り、フォルダ遷移および中のファイルのマーク解凍が行えるよう、エントリのパス構造から「中間ディレクトリを動的に合成（Synthesis）して表示投影する」ロジックへ刷新した。**
  - **背景**: 前回のパス正規化だけでは、親ディレクトリエントリ自体がZIP構造内に明示的に記録されていない場合、現在階層の直接の子だけを表示する `PopulateItems` のロジックにより、フォルダそのものが一覧に表示されず `this dir: 0`（空一覧）になってしまっていた。
  - **対応方針**:
    - `ArchiveEntry` モデルに `IsSyntheticDirectory` プロパティを追加。
    - `ArchiveListDialog.PopulateItems()` で、現在地 `_currentPath` 基準で相対パス内の直下エントリを蓄積する際、より深い階層にあるエントリについては直近の中間仮想ディレクトリを自動で合成・重複排除してマップ（`visibleMap`）に格納。
    - 合成ディレクトリは表示および階層の遷移専用（`IsDirectory = true`）とし、誤った抽出を防ぐためマークガード（`ToggleMark`）を追加しマーク不可（配下の実ファイルを直接マークして解凍する方式）に統合。

## 2026-05-18: ZIP archive entry path separator normalization corrective (Runtime failed / corrective needed)
- **結論: ZIP アーカイブ内のパスがバックスラッシュ（`\`）で区切られているケースでも、正常に階層表示・選択解凍を行えるようにするため、アーカイブ内エントリモデル `ArchiveEntry` の取得（パース）時点で常にスラッシュ（`/`）へパス正規化するよう統一した。同時に、抽出実行（7-Zip/tar等の外部呼び出し）時には元のパス名が必要であるため、`RawEntryPath` プロパティにオリジナルパスを保持・引き渡す設計へ整理した。**
  - **評価結果**: 実機検証の結果、これだけではトップ階層等の親ディレクトリエントリ自体がZIP構造内に明示的に格納されていないアーカイブ（`case_ng.zip` 等）で表示が空になる問題が解決しなかったため、暗黙のディレクトリ合成（合成ディレクトリエントリの動的構築）を行う追加の corrective フェーズが必要と判断された。
  - **背景**: ZIP アーカイブを作成した環境やツール（Windows標準機能の一部や古い圧縮ソフト）によっては、ZIP内のファイルエントリパスがバックスラッシュ区切りで格納されることがある。従来の MidFD 内部処理は `/` 区切り前提で部分的にパス操作（Trim、StartsWith、IndexOf、Directory判定など）を行っていたため、`\` 区切りのZIPではフォルダ内への遷移や正しいツリー構築、および解凍指示が正常に行えなかった。
  - **対応方針**:
    - `ArchiveEntry` モデルに `RawEntryPath` を追加。
    - `ArchiveListService.cs`（7z / tar）でエントリを読み込む際、`EntryPath` には常に `/` 統一の正規化パスを代入し、`RawEntryPath` に元パスを格納。
    - `ArchiveListDialog.cs`（階層UI）側のバックスラッシュに依存する個別 Replace 処理を綺麗にし、正規化パス前提に統一。
    - ArchiveListDialog.cs の解凍対象抽出（GetMarkedEntryPaths）時にのみ RawEntryPath を呼び出して解凍引数へ渡すようにし、抽出処理側への安全な互換性を確保。従来の MidFD 内部処理は / 区切り前提で部分的にパス操作（Trim、StartsWith、IndexOf、Directory判定など）を行っていたため、\ 区切りのZIPではフォルダ内への遷移や正しいツリー構築、および解凍指示が正常に行えなかった。
  - **対応方針**:
    - `ArchiveEntry` モデルに `RawEntryPath` を追加。
    - `ArchiveListService.cs`（7z / tar）でエントリを読み込む際、`EntryPath` には常に `/` 統一の正規化パスを代入し、`RawEntryPath` に元パスを格納。
    - `ArchiveListDialog.cs`（階層UI）側のバックスラッシュに依存する個別 Replace 処理を綺麗にし、正規化パス前提に統一。
    - `ArchiveListDialog.cs` の解凍対象抽出（`GetMarkedEntryPaths`）時にのみ `RawEntryPath` を呼び出して解凍引数へ渡すようにし、抽出処理側への安全な互換性を確保。

## 2026-05-18: Legacy SUSIE preview remnants removal
- **結論: 初期公開後の品質・設計の単純化として、古いプレビュー機能の一部として残存していた Susie Plugin（.sph / .spi）のデコード連携・資産（Services/Susie 配下）を完全に撤去し、現行のプレビュー処理経路（標準ストリームロードおよび WIC 方式）に統一した。**
  - **背景**: Susie Plugin 連携は、古くから存在する画像フォーマットを扱うための仕組みだが、現代の Windows 環境下では .NET 標準のストリームデコーダーおよび WIC（Windows Imaging Component）デコーダーによってほとんどの画像フォーマットが安全かつ高速に表示可能である。かつ、開発用リポジトリにのみ含まれ、呼び出し側も実質無効（初期化の未実行）となっていたため、不要な複雑性を排除し正本経路を単純化することが妥当と判断した。
  - **撤去対象**:
    - `Services/Susie/NativeMethods.cs`
    - `Services/Susie/SusiePreviewService.cs` (およびその内部クラス `SusiePlugin`)
    - `Services/ImagePreviewService.cs` からの `SusiePreviewService` 呼び出しおよびインポート設定。
  - **影響確認**: プロジェクトの `dotnet build` に警告・エラーなしでパスすることを確認。また、主要プレビュー処理（テキスト、画像、SVG、LargeText、アーカイブ）の健全性に影響がないことを確認した。


## 2026-05-17: Public repository publication readiness corrective
- **結論: GitHub公開（パブリック公開）の準備段階として、公式ドキュメント（`README.md`および`UserDocs/`）における免責・注意事項、FDとの「非完全互換性（独自機能差分の明記）」、サポート体制の限界を明記し、`.gitattributes` にて開発用設計資料などの公開除外（export-ignore）を設定した。**
  - **安全性の確保と免責の徹底**: ファイル操作ソフトウェアの性質上、不測の事態に備えて「バックアップの推奨」および「ファイルの破損、消失、誤操作に対する一切の対応・補償の不可」を `README.md` と `UserDocs/USER_GUIDE.md` の目立つ位置に明記した。
  - **期待値の管理（互換性の説明）**: 「FD互換」という表現が「オリジナルのFDとの完全互換」を意味するものではないこと、およびタブ機能など現代環境向けの独自差分を含むことを `README.md`, `USER_GUIDE.md`, `KEYBINDINGS.md`, `PROFILES.md` に明示し、利用者の誤解を防止した。
  - **サポート保証の排除**: 個人開発であることに伴い、不具合対応の確実性や対応時期の保証ができない旨を `SUPPORT.md` に追加した。
  - **開発専用資料の明記と漏洩防止**: 開発用設計資料やAI作業ログなどが格納される `Docs/` 配下の README.md を新設・整理し、内部資料であることを明記。さらに、`.gitattributes` に `/Docs/** export-ignore` および `/AGENTS.md export-ignore` などの記述を追加し、公開用 export 時のパッケージから完全に除外される仕組みを適用した。


## 2026-05-17: Public repository boundary decision
- **結論: GitHub公開に向けて、AI駆動開発の基幹である `.codex` / `Docs` を公開対象に残しつつ、不要な検証画像・一時成果物が格納される `artifacts/` をGitの追跡対象から外す（untrackする）判断を行った。**
  - **背景**: `artifacts/` 配下の画像は過去の機能検証のために生成されたものであり、公式ドキュメント（`README.md` 等）から一切参照されていないため、公開リポジトリのノイズを低減するために除外が妥当と判断した。
  - **untrack の実施**: `git rm -r --cached artifacts` を実行して Git 管理対象から外した。実体ファイルはローカルに保持したまま、インデックスからのみ削除した。
  - **除外設定の維持**: すでに `.gitignore` には `artifacts/` および各種一時ファイル、環境用個人ファイル等の設定が適切に記載されているため、`.gitignore` の変更は不要であることを確認。
  - **AI基幹文書の保護**: `.codex/`（状態・スキル）および `Docs/`（設計・ログ）はAI駆動開発の重要な正本情報・歴史的判断経緯であるため、一切削除・履歴圧縮を行わずに残すこととした。


## 2026-05-17: AGENTS markdown publication readiness corrective
- **結論: `AGENTS.md` の公開に向けた最小限の整形と整合性確認を実施し、公開可能な状態とする。**
  - **整形**: 335行目のコードフェンスの崩れ（バックティックの数）を修正。
  - **参照確認**: `Skill Usage Rule` で参照している `.codex/skill/` がリポジトリ内に存在し、Git 管理下にあることを確認。参照切れのリスクがないことを確認した。
  - **非対象**: 公開前のため大幅な文言修正や機能変更は行わず、静的な整合性維持に留める。


## 2026-05-17: Public documentation channel registration ware addition
- **結論: `README.md` および `UserDocs/SUPPORT.md` に、作者の関連 YouTube チャンネル紹介と「チャンネル登録してウェア」としての位置づけを追記する。**
  - **背景**: 作者の活動紹介と、応援の手段としてのチャンネル登録を周知するため。
  - **ライセンスとの切り分け**:
    - Apache License 2.0 の利用許諾条件とは明確に切り離し、チャンネル登録は「必須条件」ではなく「任意の応援・努力目標」であることを明記する。
    - 「登録しないと使えない」という誤解を招かない表現を徹底する。
  - **サポート方針の維持**:
    - 開発継続のためのフィードバック（不具合報告・要望）については、YouTube コメントではなく GitHub Issues を正本とする方針を改めて明記する。
  - **用語の統一 (平仄合わせ)**:
    - 各ドキュメント (`README.md`, `USER_GUIDE.md` 等) 間で「7-Zip 連携」と「アーカイブ操作 (7-Zip 連携 / Windows標準 fallback)」が混在していたため、後者に統一し、機能の正確な実態を反映させた。
  - **対象文書**:
    - 公開文書である `README.md` と `UserDocs/SUPPORT.md` に限定し、内部仕様書やコード内には直接的な宣伝を含めない。


## 2026-05-17: Logdsk directory tab completion closeout
- **結論: ユーザー実機確認により、`LogdiskDialog` における Tab キーの巡回補完（サイクル補完）が正常に機能し、既存の契約も維持されていることが確認されたため、本Phaseを `Runtime verified; closed` とする。**
  - **確認項目**:
    - Tab による候補の順次表示、Shift+Tab による逆順巡回が意図通り動作することを確認。
    - 補完中の Enter によるパス確定、および Esc による補完ポップアップ閉鎖（またはダイアログキャンセル）が機能することを確認。
    - 既存のインクリメンタル補完ポップアップとの共存に支障がないことを確認。
    - Browser 本体（メイン画面）の Tab キーによるマーク操作に影響や回帰がないことを確認。
  - **変更範囲の固定**: closeout は docs/state 更新のみに限定し、機能コードは変更しない。


## 2026-05-17: Logdsk directory tab completion corrective
- **結論: `LogdiskDialog` 等で使用されている `DirectoryPathCompletionController` を拡張し、Tab キーによる候補の巡回補完（サイクル補完）をサポートする。**
  - **背景**: ドライブ移動やディレクトリ直接入力時に、既存のポップアップ選択だけでなく、Tab キーで手軽に候補を切り替えたいという要望があった。
  - **実装方針**:
    - `DirectoryPathCompletionController` に Tab 巡回状態（`_isTabCycling`）を導入。
    - Tab 押下時に、ポップアップ内の次候補を選択し、同時にテキストボックスの内容をそのパスで更新する。
    - 候補巡回中のテキスト変更では再検索（候補リストの再生成）を行わず、巡回を維持する。
    - Tab によるフォーカス移動を抑制するため、`PreviewKeyDown` で補完中のみ `IsInputKey = true` を設定する。
    - Browser 本体の Tab マーク操作には影響を与えないよう、`DirectoryPathCompletionController` がアタッチされたコントロール内でのみ捕捉する。
  - **候補列挙の制限**:
    - 性能と安全性を考慮し、指定ディレクトリ直下のディレクトリのみを候補とし、再帰検索やファイル名補完は行わない。
    - 隠し属性 (Hidden) / システム属性 (System) のディレクトリについては、既存の一覧表示方針に合わせ、`Directory.GetDirectories` のデフォルト挙動（通常はこれらも含まれる）を許容する。
  - **既存機能との共存**:
    - Enter での確定、Esc でのキャンセル、マウスによるポップアップ選択といった既存のインクリメンタル検索機能を損なわないように実装する。


## 2026-05-17: file metadata attribute timestamp and grouped sort closeout
- **close判断**: ユーザー実機確認により、`file metadata attribute timestamp and datetime sort corrective`、`attribute color WinFD compatibility corrective`、`WinFD-compatible attribute grouped sort corrective` を `Runtime verified; closed` とする。
- **属性/日時変更の確認**: ファイル/フォルダの属性変更、作成日時/更新日時/最終アクセス日時変更、チェックON項目のみ反映、再帰適用、ReadOnlyタブでのブロックが確認された。
- **WinFD互換属性色の確認**: `System=マゼンタ`、`Hidden=ブルー`、`ReadOnly=グリーン`、複数属性時の優先順位 `System > Hidden > ReadOnly` が確認された。
- **Archiveの扱い**: Archiveのみは通常扱い寄せ（独立グループ化しない）を維持する。
- **WinFD互換グループソートの確認**: ソート大枠 `親ディレクトリ → ディレクトリ群 → ファイル群` を固定し、各群内で `System > Hidden > ReadOnly > Normal/Archive` を適用し、昇順/降順は属性グループ内の指定ソートキーにのみ適用する方針が実機で成立した。
- **変更範囲の固定**: closeoutは docs/state 更新のみに限定し、機能コードは変更しない。

## 2026-05-17: WinFD-compatible attribute grouped sort corrective
- **差分原因**: 既存の `DirectoryProvider` はディレクトリ群/ファイル群の分離は維持していたが、属性ランクは `SortKind.Name` でのみ部分適用、その他 `Ext/Size/Date/DateCreated/DateAccessed` では未適用だったため、WinFDの属性グループ化ソートと差分が出ていた。
- **採用方針**: ソート大枠（親ディレクトリ `..` → ディレクトリ群 → ファイル群）は維持し、`DirectoryProvider` 内でディレクトリ群とファイル群それぞれに同一の属性ランクを一次キーとして適用する。
- **属性ランク**: `System=0, Hidden=1, ReadOnly=2, Normal/Archive=3`。複数属性は上位優先（`System + Hidden` はSystem、`Hidden + ReadOnly` はHidden）。
- **昇順/降順の扱い**: 属性ランク順は固定し、昇順/降順は属性グループ内の指定ソートキー（Name/Ext/Size/Date/DateCreated/DateAccessed）とName tie-breakerにのみ適用する。
- **Archiveの扱い**: Archiveのみは通常扱い（独立グループ化しない）を維持した。
- **非対象の維持**: AttributeDialog、属性/日時変更処理、再帰適用、SortDialog UI、属性色定義は変更しない。

## 2026-05-17: attribute color WinFD compatibility corrective
- **色補正の方針**: WinFD実機観測に合わせ、属性色を `System=マゼンタ`、`Hidden=ブルー`、`ReadOnly=グリーン` に統一した。
- **優先順位の方針**: 複数属性時の判定優先順位は `System > Hidden > ReadOnly` を採用し、`System + Hidden` はSystem色、`Hidden + ReadOnly` はHidden色で表示する。
- **Archive の扱い**: WinFD比較でArchive専用色の要件がないため、今回 corrective ではArchive専用強調色を適用しない（通常色へ寄せる）。視認ノイズ抑制を優先した。
- **非対象の維持**: AttributeDialog、属性/日時適用、再帰適用、日時ソート（SortKind/DirectoryProvider/SortDialog）、ReadOnlyガード契約は変更しない。

## 2026-05-17: file metadata attribute timestamp and datetime sort corrective
- **対象拡張の方針**: 既存の属性変更はファイル単体前提だったため、SelectionResolver結果を起点にファイル/フォルダ混在を処理対象へ統合した。ReadOnlyタブは既存 `GuardReadOnlyBrowserTab("属性変更")` を入口で維持する。
- **日時変更の扱い**: 更新日時/作成日時/最終アクセス日時は、ダイアログで明示チェックONの項目だけを適用する。未チェック項目は変更しない。時刻はローカル時刻の `DateTimePicker` で秒まで指定可能とした。
- **再帰適用の安全策**: `サブディレクトリ以下も処理する` は初期OFF。ON時のみフォルダ配下を走査し、`ReparsePoint` は辿らない方針を採用した。無限再帰と意図しない外部領域更新を避けるため。
- **進捗表示契約**: 大件数または再帰ON時は `FileOperationProgressFallbackForm`（`canCancel:false`）で待機/進捗を表示する。途中キャンセルは部分適用状態を作るため今回非対応とした。
- **属性色分けの優先順位**: `System > Hidden > ReadOnly > Archive > Normal` を採用。ディレクトリ通常色/ファイル通常色は最終フォールバックとして維持した。
- **日時ソート種別の保存方針**: `SortKind` に `DateCreated` / `DateAccessed` を追加し、既存 `Date` は更新日時（LastWriteTime）として維持。既存保存形式は enum 文字列のため schema 変更なしで後方互換を維持する。

## 2026-05-16: rename and batch rename progress closeout
- **結論: ユーザー実機確認により、一括リネームにおける衝突誤判定の解消、固定タブルート復帰、および進捗ダイアログ表示の動作が確認できたため、関連Phaseを `Runtime verified; closed` とする**
  - **実機確認の反映**:
    - 一括リネーム `$2N$E` において、実際には衝突しないケースが OK 表示になり、リネームできることを確認。
    - 固定タブにおいて、固定ルート配下へ移動後に別タブへ移り、戻って `\` を押しても問題なく固定ルートへ戻ることを確認。
    - 数千件規模の一括リネーム適用時に、進捗ダイアログが正しく表示されることを確認。
    - 進捗ダイアログにて `1400/5980 件` のような処理件数と処理中ファイル名が随時更新されることを確認。
    - 処理中に UI スレッドがフリーズして固まっているように見える問題が改善したことを確認。
  - **キャンセル不可方針の維持**:
    - 大量一括リネームの途中キャンセルは、ファイル状態の中途半端な混在リスクが高いため、当初の判断通り「キャンセル不可」の方針を維持し運用する。
  - **変更範囲の固定**:
    - closeoutは docs/state 更新のみに限定し、機能コード変更は行わない。


## 2026-05-16: Batch rename apply progress visibility corrective
- **結論: 大量ファイルの一括リネーム時に `FileOperationProgressFallbackForm` を用いた進捗ダイアログを表示し、UI スレッドのブロックを回避する。キャンセル機能は追加しない。**
  - **問題の特定**:
    - 数千件規模の一括リネームにおいて、`ApplyBatchRename` が UI スレッド上で同期的に `File.Move` 等を実行していたため、処理中にウィンドウが「応答なし」に見え、進捗もわからない状態だった。
  - **対策の実装**:
    - `RenameApplyCoordinator.ApplyBatchRename` を非同期化可能な形に修正し、進捗通知コールバックを受け取れるようにした（UI更新負荷を抑えるため間引き通知を含む）。
    - `MainForm.ExecuteBatchRename` を `async void` 化し、`Task.Run` を用いてバックグラウンドでリネーム処理を実行するようにした。
    - 処理中は `PrepareFileOperation` によるガードを張り、`FileOperationProgressFallbackForm`（モードレス・キャンセル不可）を表示して進捗と安全な状態ロックを提供する。
  - **キャンセル不可とした判断理由**:
    - ファイルシステムの変更を伴うリネーム処理を途中で中断すると、元の名前と新しい名前が混在した中途半端な状態になりやすい。
    - 既存の Undo 契約は「バッチ全体が完了した後に結果を Undo スタックに積む」設計であるため、部分的な中断・巻き戻しを安全に行う仕組みがない。
    - 中途半端な状態でエラーやキャンセルが発生するリスクよりも、最後まで処理を完遂（エラー発生分を除く）させ、既存の Undo 機能を確実に利用できる方が安全であると判断した。
  - **影響範囲**:
    - 一括リネームの適用処理のみ非同期化・進捗表示化した。衝突判定やリネームパターン、既存の Undo 契約、TabLock などの仕様には変更なし。


## 2026-05-16: Tar fallback extract destination collision corrective
- **結論: 展開先フォルダ名が既存ファイル名と衝突する場合、`_extracted` などの代替名を自動生成して解凍を継続する**
  - **問題の特定**:
    - `ExCSS.dll.7z` の解凍時、カレントディレクトリに同名ファイル `ExCSS.dll` が存在すると、フォルダとしての作成に失敗し解凍が中断される。
    - これは `tar.exe` の制約ではなく、その前の `Directory.CreateDirectory` がファイルと衝突した際に例外を投げることによるもの。
  - **対策の実装**:
    - `ArchiveExtractService.EnsureSafeExtractDestinationDirectory` を新設。
    - 指定されたパスが既存ファイルと衝突する場合、`_extracted`, `_extracted_1` ... と空いている名前を探して返すロジックを実装。
    - `ResolveDestinationDirectory` にこの安全化ロジックを統合。これにより、`MainForm` の一括解凍および `ArchiveListDialog` の個別解凍の両方に自動適用される。
  - **判断理由**:
    - ユーザーに衝突エラーを出して中断させるよりも、安全な別名で解凍を完了させるほうが利便性が高い。
    - 既に「ディレクトリ」として存在する場合は、従来どおりその中への展開を許容する（既存仕様の維持）。
    - ログ (`LogService.Info`) にリダイレクト情報を記録することで、追跡可能性を確保。
  - **影響範囲**:
    - 7-Zip パスも含め、`ResolveDestinationDirectory` を利用する全てのアーカイブ展開に適用。衝突時は 7-Zip も失敗していたはずなので、全体的な堅牢性が向上する。

## 2026-05-16: Tar fallback extract destination corrective
- **結論: `tar.exe` による展開時の引数見直しと、展開先ディレクトリの事前作成を徹底し信頼性を向上**
  - **問題の特定**:
    - 展開時に `-axvf` を使用していたが、一部の `tar.exe` 環境において `-a` (auto-detect) が `-x` (extract) モードと併用できない、あるいは警告（`ignoring option -a in mode -x`）の原因となることを確認。
    - `tar -C <dest>` は指定されたディレクトリが存在しない場合に失敗（`could not chdir`）するが、`TarFallbackService` 自体でディレクトリ作成を保証していなかった。
  - **対策の実装**:
    - **引数修正**: 展開時の引数を `-axvf` から `-xvf` に変更。圧縮形式の自動判別は `-x` 自体の機能で賄えるため `-a` を削除。
    - **ディレクトリ作成保証**: `TarFallbackService.Unpack` 内で `Directory.CreateDirectory(destinationDirectory)` を実行するよう変更。
    - **エラーメッセージの改善**: `could not chdir` 等のディレクトリ起因のエラーを検出し、「展開先フォルダへ移動できませんでした」等の具体的な日本語メッセージへ変換。これにより、ディレクトリ問題が「暗号化や分割アーカイブ」と誤認されるのを防ぐ。
    - **複数拡張子対応**: `ExCSS.dll.7z` のようなファイル名でも、`Path.GetFileNameWithoutExtension` により `ExCSS.dll` ディレクトリが正しく解決され、展開先として扱われることを維持。
  - **影響範囲の確認**:
    - `MainForm` の一括解凍経路、および `ArchiveExtractService` (プレビューからの解凍) の両方が `TarFallbackService.Unpack` を経由するため、一律で修正が適用される。
    - ReadOnly タブにおけるガード等の既存安全策に変更がないことを確認。

## 2026-05-16: Archive fallback and TarFallbackService closeout
- **結論: 7-Zip なし環境での Archive 操作 (zip/7z/tar) の fallback 経路を正式採用し closeout とする**
  - **実機確認結果**:
    - 7-Zip 未導入環境においても、Windows 標準 `tar.exe` を利用した 7z / TAR の作成・展開、および ZIP の作成・展開（既存 ZipFallbackService 優先）が正常に動作することを確認。
    - プレビュー画面 (Archive Contents) における一覧表示および個別解凍も、`tar.exe` fallback により実用可能なレベルで動作することを確認。
    - ReadOnly タブにおける解凍ガードが、新設された fallback 経路においても確実に機能していることを確認。
  - **運用方針の確定**:
    - **正本経路**: 7-Zip あり環境。暗号化、分割アーカイブ、CRC/SHA、多種形式対応を含むフル機能を維持。
    - **fallback 経路**: 7-Zip なし + `tar.exe` (Win11 24H2等) あり環境。主要 3 形式 (zip/7z/tar) の標準的な作成・展開をサポート。
    - **最小 fallback**: `tar.exe` もない環境。zip のみの対応。
  - **制限事項**: 暗号化 7z/RAR、分割アーカイブ、特殊形式 (gzip/bzip2等) の作成については引き続き 7-Zip を推奨とし、UI 上でも 7-Zip 不在時はこれらを制限する設計を維持する。

## 2026-05-16: Windows native archive fallback implementation (TarFallbackService)
- **結論: `TarFallbackService` を実装し、7-Zip 未導入環境での 7z/RAR/TAR 対応を完了**
  - Windows 11 24H2+ の `tar.exe` を利用した作成・展開・一覧取得を統合。
  - **実装の要点**:
    - `ZipFallbackService` (managed) を ZIP の優先 fallback とし、`TarFallbackService` を 7z/TAR/RAR の二次 fallback とする。
    - `PackDialog` の形式選択において、7-Zip 不在時でも `tar.exe` があれば `zip / 7z / tar` を提示するよう拡張。
    - `ArchiveListService` に `tar -tf` による一覧取得 fallback を導入し、7-Zip なしでもプレビュー画面を開けるようにした。
    - `ArchiveExtractService` および `MainForm` の各エントリポイント（Pack/Unpack/ExtractSelected）に fallback 処理を統合。
  - **安全性の維持**:
    - `GuardReadOnlyBrowserTab` による書き込み制限を全ての fallback 経路（通常 Unpack およびプレビューからの解凍）で維持。
    - `ProcessStartInfo.ArgumentList` を使用し、空白や日本語を含むパスの安全な受け渡しを確保。
  - **制約の再確認**:
    - 暗号化 7z/RAR や分割アーカイブは非対応。これらが原因で失敗した場合は、ユーザーに 7-Zip の設定を促すメッセージを表示する。
    - `tar.exe` の進捗表示はファイルリスト単位のため、UI 上は「解凍中 (n/total)」の形式で表示。

## 2026-05-16: archive fallback capability and preview extract guard corrective
- **ReadOnlyガードの徹底 (Archive Contents)**: プレビュー画面（ArchiveListDialog）からの解凍操作が ReadOnly タブで実行可能だった問題を修正。
  - UI層: `ArchiveListDialog` に `isReadOnly` 状態を注入し、ReadOnly 時は「選択解凍」「すべて解凍」ボタンを無効化、ヒントラベルに警告を表示。
  - Entry-point層: `MainForm.ExecuteArchiveExtractAsync` に `GuardReadOnlyBrowserTab` を追加。
- **7-Zip欠落環境への適応 (PackDialog)**: 7-Zip が未設定・未インストールの環境で、実行不可能な形式（7z, tar 等）を選択できてしまう問題を解消。
  - UI層: `PackDialog` の形式選択 ComboBox を動的生成に変更。7-Zip 欠落時は `zip` のみに制限。
  - Entry-point層: `MainForm.ExecutePack` で 7-Zip の有無を事前に判定し、利用可能な形式リストと動的ヒントテキストを `PackDialog` へ渡す。
- **UI Stability (形式絞り込み)**: 一般利用におけるノイズ削減のため、安定版（PracticalStableプロファイル相当）のプライマリ形式として `zip / 7z / tar` を維持し、特殊形式（gzip / bzip2 / xz / wim）はUIから非表示とした。
- **非対象の維持**: 7-Zip 自体の配布、SQLite への保存、既存の zip fallback 処理のコアロジック、および Undo/Redo 契約は変更しない。

## 2026-05-16: bulk file operation hotpath and cancelability balance corrective
- **主因候補（Delete）**: `ExecuteDelete controlled recycle-bin loop` の標準経路が `DeleteToRecycleBin` を per-item で呼ぶ構成になっており、大件数で API 呼び出し回数が支配的になっていた。ログ観測（8k件で 18.9s / 415件完了）とも整合する。
- **主因候補（Move）**: `FileOperationService.Move` の per-item success INFO ログ、per-item progress 更新、per-item `UnmarkPath`（同期/状態更新付き）が hotpath になりやすい構造だった。
- **過去履歴確認結果**: `f2cd21f` / `5259f19` / `7860fce` 系で削除UI flushやmanaged trash経路のthrottle最適化は入っている一方、現行の大件数 recycle-bin 標準経路には per-item 削除コストが残っていた。`MidFD_old_20260516` 側にも同系の delete/cancel 補正履歴が存在することを確認した。
- **Deleteの採用方針（速度/キャンセル性バランス）**:
  - 小件数は既存の guarded shell / controlled sequential の契約を維持。
  - 大件数（>=256）は `ShellRecycleBinDeleteService` を chunk（64件）単位で実行し、chunk境界でキャンセル判定する方式を採用。
  - 単一巨大 `PerformOperations` には戻さず、cancelability を chunk 粒度で担保する。
- **Moveの採用方針**:
  - progress 更新を件数・時間で間引き（64件または150ms）。
  - mark解除は per-item `UnmarkPath` から bulk chunk 解除へ変更（128件または200ms）。
  - 大件数 move/cut-paste move/dir-merge move では per-item successログを抑制し、summary中心へ寄せる。
- **非対象の維持**: Recycle Bin / Managed Trash / UndoRedo 契約、ReadOnlyガード、FeatureGate、settings/json schema、SQLite schema は変更しない。
- **Verification Results**: Runtime verified; closed.
  - 大量削除 (Recycle Bin): 旧観測よりスループットが改善し、chunk 単位でのキャンセルが動作することを確認。
  - MidFD Managed Trash: 454件のバッチ処理において、マニフェスト記録・Undo 可能性・UI同期を含め 100% 成功を確認。

## 2026-05-16: first launch profile dialog cancel and tooltip cleanup closeout
- **close判断**: ユーザー実機確認により、本Phaseを `Runtime verified; closed` とする。
- **実機確認の反映**:
  - Profile未設定時の初回選択ダイアログ表示を確認。
  - `実用安定版（推奨）` / `高度機能α版` の表示を確認。
  - `キャンセル/×` 挙動、実用安定版/高度機能α版の選択導線を確認。
  - SettingsForm上の表示名を確認。
  - 設定画面上にメイン画面由来のパスToolTip/フロートが残らないことを確認。
- **変更範囲の固定**: closeoutは docs/state 更新のみに限定し、機能コード変更は行わない。

## 2026-05-16: first launch profile dialog cancel and tooltip cleanup
- **初回選択キャンセルの扱い**: 初回起動の profile 選択ダイアログで `キャンセル/×` が選ばれた場合は、`PracticalStable` への暗黙フォールバックをやめて起動を中止する。設定保存も行わない。
- **実装責務の配置**: 起動中止は `Program.cs` の起動前判定で処理し、`MainForm` を起動しない通常終了に寄せる。エラーダイアログは出さない。
- **Tooltip残留対策**: 設定ダイアログを開く直前に `MainForm` 側でヘッダー由来 ToolTip と一時オーバーレイを明示的に `Hide` する。
- **非対象の維持**: FeatureGate 対象機能、PracticalStable/Full 契約、settings/json schema、SQLite schema、ファイル操作仕様は変更しない。

## 2026-05-16: first launch profile selection and alpha labeling
- **未設定時の暗黙Fullを廃止**: `settings.json` に profile が未設定/空/不正のときは初回選択ダイアログを表示し、暗黙で `Full` に入らないようにした。通常利用の正式導線を `初回選択 + 設定画面` に寄せるため。
- **初回選択の安全側既定**: キャンセル時およびダイアログ表示失敗時は `PracticalStable` を採用し、起動不能を避ける。選択結果は `settings.json` に保存する。
- **表示名の分離**: 内部値は `PracticalStable/Full` を維持し、SettingsForm の user-facing 表示を `実用安定版（推奨）/高度機能α版` に変更した。
- **--profile の扱い**: `--profile` は開発補助として残す。明示指定で有効値のみ優先し、不正値は無視して保存設定/初回選択へ戻す。
- **非対象の固定**: FeatureGate対象、機能契約、保存形式、SQLite schema、通常ファイル操作仕様は今回変更しない。

## 2026-05-16: practical stable profile and feature gate integration closeout
- **close判断**: ユーザー実機確認により PracticalStable の主要導線制御が成立したため、本Phaseを `Runtime verified; closed` とする。
- **実機確認の反映**: PracticalStable 起動、Workspace Snapshot 導線非表示、外部Diff導線無効、MarkSlot基本導線維持、ImageViewerでの減色/SVGコピー導線遮断、Command Palette 候補絞り込みを確認済みと記録する。
- **変更範囲の固定**: closeoutは docs/state 更新のみに限定し、機能コード変更は行わない。

## 2026-05-16: practical stable profile and feature gate integration
- **単一ソース維持**: `Full` と `PracticalStable` はビルド分岐ではなく `FeatureProfile` / `FeatureGate` で切り替える。物理削除や DLL 分離は今回行わない。
- **既定値は Full**: 設定未定義、不正値、未指定起動では `Full` を採用する。現行多機能版の通常経路を壊さないため。
- **PracticalStable の閉じ方**: 高リスク機能はコード削除ではなく UI 非表示・dialog 無効化・実行入口 guard で閉じる。通常ファイル操作、QuickAccess、7-Zip 基本連携、MarkSlot 基本保存/復元は残す。
- **マウスジェスチャ既定OFFの条件**: PracticalStable では `EnableMouseGestures` が settings に明示されていない場合だけ OFF に補正する。ユーザーが明示的に ON にした設定は尊重する。
- **今回は広げない範囲**: MainForm 分割、settings.json の migration、SQLite schema 変更、Undo/Redo 再設計、機能コードの物理撤去は別Phaseへ送る。

## 2026-05-16: project identity normalization to MidFD
- **MidFD2の名称統一**: `MidFD2` は初期実装名であり、今後の正本名を `MidFD` に統一する判断を行った。
- **変更範囲の限定**: 今回は identity cleanup のみを扱い、機能変更、FeatureProfile導入、MainForm分割、設定形式変更は行わない。
- **実行ファイルとプロジェクト**: `MidFD2.csproj` を `MidFD.csproj` にリネーム。C# namespace / using も `MidFD` に統一。
- **既存の文書**: 過去ログの文脈としての MidFD2 表記は無理に全置換せず、履歴の意味を維持した。
## 2026-05-10: LargeText character selection autoscroll edge guard corrective
- **Auto-Scroll の過剰発動を抑止**: `UpdateCharacterSelectionAutoScroll` において、`point.Y < margin` だけで無条件に上スクロールを開始するのではなく、`_renderedFirstLine > 0` (実際に上へスクロール可能か) という条件を追加した。これにより、先頭行（1行目）をドラッグしただけで即座に上スクロール扱いになる暴走を阻止した。
- **キャレットの強制ジャンプを防御**: `ExtendCharacterSelectionToVisibleEdge` において、`direction < 0` かつ `_renderedFirstLine == 0` の場合は、`column = 0` の強制適用をスキップするようにした。これにより、先頭行内でのドラッグ中にキャレットが行頭へ飛んでしまう不整合な挙動を完全に断ち切った。
- 下方向のAuto-Scrollも `margin` による判定を廃し、より安全な `point.Y > ClientSize.Height` （コントロール外）でのみ発動する仕様へ変更した。

- **Verification Results**: Runtime verified; closed.
  - 先頭行での上方向auto-scroll過剰発動を抑止した。
  - `_renderedFirstLine == 0` 時に column 0 へ強制ジャンプしないようにした。
  - 実機確認で、長大1行JSONの途中選択が行頭へ引きずられないことを確認。
  - 長大行表示、選択、ステータス表示に回帰なし。


## 2026-05-10: LargeText character hit-test measurement corrective
- **ヒットテスト基準の完全一致化**: `GetColumnFromX` 内の列判定アルゴリズムを `charWidth` による割り算（線形計算）から、OnPaintと同じ `TextRenderer.MeasureText(... NoPadding | NoPrefix)` を用いた二分探索に変更した。
- **MouseDown / MouseMove のクランプ挙動の分離**:
  - MouseDown時は `clampToVisible = false` とし、文字列領域外（左余白、または `MaxRenderableLineLength` 以上の右側何もない空間）のクリックを厳格に無効（`-1`）とする。
  - MouseMove時は `clampToVisible = true` とし、ドラッグ中にマウスがテキスト外へ出た場合でも適切に `0` または `maxLength` へクランプし、選択範囲を維持・拡張できるようにした。
- **長大行表示範囲の保護**: ヒットテストの探索上限を `MaxRenderableLineLength`（2048）に制限し、省略サフィックスや非表示部分をヒットテスト対象から完全に除外した。

## 2026-05-10: LargeText character selection anchor reset corrective
- **通常クリック契約の厳格化**: `OnMouseDown` にて、Shift修飾子なしの通常左クリックが行われた場合、必ず既存のアンカー(`_characterSelectionAnchor`)をリセットする処理を最優先で実行するようにし、古いアンカーが残存する経路を断ち切った。
- **フォールバックの撤廃**: `GetColumnFromX` において、テキスト開始位置より左側（余白）をクリックした際、以前は `Math.Clamp` によりカラム `0` として扱われるケースがあったが、これを「無効値(-1)」として明確に弾くよう修正した。これにより、意図せず行頭から選択が開始される問題を解消した。

## 2026-05-10: LargeText selection metrics alignment corrective
- **選択ハイライトの Metrics 補正**: `layout.CharWidth` による一律計算から、`TextRenderer.MeasureText` による substring 単位の正確な幅取得へ変更。これにより、等幅フォントであっても生じていた累積的な描画ズレ（空白やはみ出し）を解消した。

## 2026-05-10: LargeText long-line visible slice rendering corrective
- **表示用読込制限の導入**: `IsLongLineDetected` 時、`LargeText` ビューアでの表示用読み取り（`ReadLinesAsync`）を 4KB に制限した。これにより描画失敗を回避した。
- **GDI描画安全ガード**: `TextRenderer.DrawText` に渡す直前に文字列を 2048 文字 (`MaxRenderableLineLength`) で切り詰める二重の安全策を導入した。
## 2026-05-08: SVG clipboard export / Office paste interoperability
- **クリップボード形式の統合**: `image/svg+xml` 形式をプライマリとし、Office 非対応環境向けに PNG/Bitmap fallback を DataObject に一括格納する。
- **SVGZ対応**: .svgz は内部的に GZip 展開し、プレーンな SVG XML としてクリップボードへ渡す。
- **メニュー表示制御**: SVG 形式を保持したコピーが不可能な一般画像ではメニューを非表示または無効化し、ユーザーの誤解を防ぐ。

## 2026-05-08: SVG image loading responsiveness improvement
- **SVG読み込み・レンダリングの非同期化**: UIスレッドのフリーズを避けるため、`Task.Run` を利用してバックグラウンドで Bitmap 化を行う。
- **読み込み世代管理**: 高速な画像切り替え時に古い SVG の結果が反映されないよう、`_loadRequestId` による世代チェックを徹底する。
- **待機状態の可視化**: 読み込み開始時に `pictureBox1.Image` をクリアし、ビューア上に「Rendering SVG...」等の待機ラベルを表示することで、処理中であることをユーザーに明示する。
- **共通の非同期読み込み**: SVG 以外の大容量画像でも同様のフリーズが発生する可能性があるため、画像読み込み処理全体を非同期化の対象とする。

## 2026-05-08: Merge-aware dither attenuation
- **色統合レベルに応じたディザ強度の減衰**: 色統合を強くするとパレット色間の距離が広がり、誤差拡散等のディザ粒子が目立ちやすくなるため、統合レベルに合わせてディザ強度を自動で減衰（弱:0.8, 中:0.5, 強:0.2 倍）させる仕組みを導入。
- **色統合閾値の微調整**: `強` での色潰れと粒子感のバランスを改善するため、閾値を (12, 20, 30) から (10, 16, 24) へ僅かに引き下げ。面整理の特性を維持しつつ、不自然な色の飛びを抑制。

## 2026-05-08: Natural dither mode corrective
- **「自然」プリセットを Atkinson 誤差拡散へ変更**: 従来の RGB 直接加算型 Blue Noise 近似（VoidAndCluster）は、任意パレット減色（Median Cut）において色境界を跨ぎやすく、不自然な色飛びやまだらが発生する原因となっていたため、より安定した Atkinson 誤差拡散に変更。
- **ディザ強度 (strength) のモード別最適化**: `自然`（Atkinson）は 0.45f、`階調優先`（SierraLite）は 0.65f など、プリセットの用途に合わせて拡散強度を個別に設定。イラストの平坦部を汚さず、かつ階調を補うバランスに調整。
- **RGB直接加算の抑制**: Bayer や BlueNoise (VoidAndCluster) 経路の強度をさらに引き下げ、パターンの主張を抑える。

## 2026-05-08: Palette hue preservation corrective
- **Weighted Median Cut への改良**: 単純な bucket 数ではなく、ピクセル頻度（重み）の累積中央値で分割するように変更。これにより、髪色や肌色といった画像内の主要な色相が、少数派の極端な色にパレットを奪われるのを防ぐ。
- **知覚重み付き距離 (3:4:2) の導入**: 最近傍色検索において、単純な RGB 二乗距離ではなく、人間の知覚感度に近い重み（Red:3, Green:4, Blue:2）を採用。金髪・茶髪・肌色が赤い服などの強い色へ吸い込まれる（赤変する）現象を抑制。
- **1-pass K-means refinement**: Median Cut で得たパレットを初期値として、全ピクセル分布に基づいたクラスタ重心への再配置を1回実行。パレット生成時の平均化による色相ズレを実画像の色分布へ引き戻す。
- **6-bit 高精度キャッシュ**: 最近傍検索のキャッシュ粒度を 5-bit から 6-bit へ向上。色境界付近での誤マッチを減らし、階調の連続性を改善。
- **半透明ピクセルの重み減衰**: パレット生成時に `128 <= Alpha < 255` のピクセルの重みを下げることで、透過背景等の色がパレット全体を歪めるのを防止。

## 2026-05-08: Color reduction quality improvement
- **Median Cut パレット生成の導入**: 単純な頻度上位方式から Median Cut 方式へ変更。画像全体の色分布を均等にカバーできるようになり、イラストの影や肌色などの代表色が欠落しにくくなった。
- **誤差拡散の弱化と安定化**: 誤差拡散強度を 0.75 に引き下げ、誤差の蓄積を ±128 にクランプすることで、イラスト等で発生しがちな「黒い粒状ノイズ」を大幅に低減。
- **蛇行走査 (Serpentine scan) の強制適用**: 全ての誤差拡散モードで蛇行走査を適用し、一方向への縞や斜め筋の発生を抑制。
- **透明度の考慮**: パレット生成および誤差拡散において、透明（Alpha < 128）なピクセルを無視するようにし、エッジ部分の汚れを防止。
- **256色の既定値を「なし」へ**: 256色モードではパレットが十分高品質であればディザなしの方がイラストに適するため、初期選択を「なし」に変更。

## 2026-05-08: Color reduction dialog UI refinement
- **ディザリング名称の簡略化**: 内部アルゴリズム名（青色雑音、誤差拡散等）を直接 ComboBox に出さず、「自然」「高品質」「なめらか」といったユーザーが直感的に選びやすい名称に変更。
- **説明文の分離**: 長いアルゴリズムの説明を項目名から切り離し、ComboBox 下部のラベル（DitherHint）に表示することで、UI の視覚的ノイズを低減。
- **色数指定の制御**: 「色数指定」選択時のみ数値入力を有効化し、それ以外では無効化することで誤操作を防止。
## 2026-05-07: CRC/SHA result dialog copy usability corrective
- **コピー導線の分離**: 7-Zip の標準出力全体を保持したいニーズと、ハッシュ値だけを抽出して他のツールや文書に貼り付けたいニーズが混在するため、`結果をコピー`（全体）と `ハッシュ値をコピー`（抽出）の2つのボタンを置く方針とした。
- **抽出ロジックの堅牢性**: 7-Zip のテーブル形式（ハイフン行で区切られたデータ行）をパースすることで、ヘッダやフッタを除去して純粋なデータ行のみを取り出すようにした。
- **ディレクトリ対象の事前ブロック**: ディレクトリを対象にハッシュ計算を実行しようとした際、実行後に警告を出すのではなく、コンテキストメニュー生成時点でディレクトリの存在を検出し、`CRC/SHA` メニュー自体を無効化（グレーアウト）する方針とした。これにより、ユーザーは実行不可能な操作を事前に把握でき、不要なクリックとエラーダイアログの表示を回避できる。
- **単一ファイル時の挙動最適化**: 単一ファイルかつ特定アルゴリズムの場合は、ファイル名を含めずハッシュ値文字列のみをコピーすることで、検証作業の利便性を高めた。複数ファイル時は `Hash <TAB> FileName` 形式で識別性を維持する。
- **UI初期状態の補正**: TextBox 全選択による視覚的ノイズを抑えるため、表示時に選択を解除し、初期フォーカスを「閉じる」ボタンへ移動させることで、キーボード操作（Enter/Escで即終了）と内容確認のしやすさを両立させた。

## 2026-05-07: 7-Zip CRC/SHA context menu integration
- **7-Zipハッシュ機能の統合**: .NET 独自実装や外部ライブラリを増やさず、既存の 7-Zip インフラ（7z.exe h コマンド）を活用することで、実装の最小化と信頼性の確保（7-Zip本体と一致する結果）を両立させた。
- **非同期実行とUI分離**: ハッシュ計算はファイル数によって時間がかかる可能性があるため、`SevenZipService.HashAsync` による非同期実行とし、結果を専用の `HashResultDialog` で表示する分離構造とした。
- **ReadOnlyタブでの許容**: ハッシュ計算はファイル内容の読み取りのみであり、ファイルシステムへの変更を伴わないため、ReadOnly タブにおいても実行を許可する（情報取得機能として扱う）方針とした。ただし、一貫性のために `FileOperationGuard` による共通の操作管理下には置く。

## 2026-05-07: 7-Zip archive workflow enhancement / corrective
- **UIサマリーの冗長性排除**: `PackDialog` の対象表示において、コード側（MainForm）とUI側（Dialog）の両方で「対象:」を付与していたため重複していた。Dialog側を正本とし、MainForm側のプレフィックスを削除した。
- **コンテキストメニューの配置統合**: 「フォルダごとに個別圧縮...」が「送る(SendTo)」相当の階層に混入していた問題を修正。標準の「圧縮...」「解凍...」とともに 7-Zip サブメニュー内、あるいはブラウザメニューの圧縮セクションへ集約し、発見性を高めた。
- **ReadOnlyガードの徹底**: ReadOnlyタブの右クリックメニューにおいて、圧縮・解凍操作が `Enabled = false` になるよう制御を追加。また、`ExecuteUnpack` エントリポイント自体にも `GuardReadOnlyBrowserTab` を追加し、ショートカット等による実行も確実にブロックする方針とした。
- **7-Zipバイナリ解決の改善**: `SevenZipService.ResolveCliExecutable` を介して設定値の検証と自動検索（FindSevenZip）を組み合わせることで、設定が不完全な環境でも可能な限り動作を継続させるようにした。


## 2026-05-07: ReadOnly tab write operation guard coverage corrective
- **ReadOnlyの防御範囲厳格化**: ReadOnlyタブは「閲覧専用」の用途であるため、配下ファイルへの書き込み・削除・移動・属性変更などをブロックする方針とした。
- **Guard漏れの補正対象**: これまで実機で動作してしまっていた以下の操作入口に対し、`GuardReadOnlyBrowserTab("操作名")` や `IsActiveBrowserTabReadOnly()` を追加し、実行をブロックした。
  - **Cut (切り取り)**: `ExecuteClipboardCut`。別ディレクトリへの Paste で元ファイルが消滅するため。
  - **ATTR (属性変更)**: `ExecuteAttribute`。ファイルシステムの属性書き換えに該当するため。
  - **Drag-in**: `BrowserPanel_DragEnter` および `BrowserPanel_DragDrop`。D&Dによる外部からのコピー取り込み・画像保存は現在のタブ配下への書き込みにあたるため。
  - **Pack (圧縮)**: `ExecutePack`。現在のパスにアーカイブファイルを作成するため。
  - **Edit (外部エディタ)**: `ExecuteOpenWithEditor`。外部エディタ起動自体は閲覧に見えるが、エディタ側で保存すればファイルを書き換え可能であり、ReadOnlyの趣旨に反するため（Viewerは許可）。
- **許可対象の維持**: Copy、View、Preview、Mark操作、および Drag-out Copy など、元ファイルやディレクトリ構造に変更を加えない操作は従来どおり許可されることを維持した。

## 2026-05-07: repo state hygiene / backlog and agent instruction cleanup
- **AGENTS.md のノイズ除去**: ファイル冒頭に残っていた会話由来の文章（「jsonからSQLiteへの移行など...」等）を削除し、純粋な AI 向け運用ルール・指示書として正本化した。
- **バックログの明確化**: `phase_backlog.md` において、未実装機能ではなく具体的なユーザー価値Phaseだけを置く方針を再確認した。実装済みの「タブ固定」「ReadOnly」「Workspace Snapshot」「外部変更検知」などは、未実装候補として再掲しない旨を Note として明記した。
- **保留事項の明記**: `open_questions.md` に「Watchlist / Deferred / 仕様・判断保留」セクションを追加し、何もない場合は `(None)` を明記することで、根拠のない保留事項の再生産を防いだ。
- **不要生成物の除外**: `.dotnet` 配下の SDK 広告やローカルビルド生成物などが git 管理対象になっていないか確認し、管理対象外とする方針を確認した（実行ログに基づく）。

## 2026-05-06: tab lock / lock root immutability corrective
- **StartupPath の不変性保護**: `CaptureActiveBrowserTabState` において、タブがロックされており既に `StartupPath` が設定されている場合は、UI同期（`BuildBrowserTabStateFromCurrentUi`）による上書きを行わないガードを追加した。これにより、子ディレクトリへの移動やタブ切り替えによってロックルートが意図せず書き換わる問題を解消した。
- **ナビゲーションを考慮した復元ロジックの改善**: `TryResolveBrowserTabRestorePath` において、ロックされたタブであっても「現在パスがロックルート配下にある」場合は、ルートへリセットせず現在パスを優先して復元するように変更した。これにより、作業中のサブディレクトリを維持したままタブ切り替えやアプリ再起動が可能になった。
- **包含判定の統一**: 既存の `IsPathUnderBrowserTabStartupPath` を復元ロジックでも利用することで、ロックの制約（ルート外への逸脱防止）と利便性（ルート内での場所維持）を同一の判定基準で両立させた。

### Verification Results
- Runtime verified; closed.
- ロックされたタブで子ディレクトリへ移動した後、他のタブへ切り替えて戻っても、場所が維持されていることを確認。
- ツールチップやタブ情報ダイアログにおいて、子ディレクトリにいても「起動元」が本来のロックルートを指し続けていることを確認。
- アプリ再起動後も、ロックされたタブが（ルートではなく）作業中だった子ディレクトリで復元されることを確認。
- ロックルート外への移動時に、正しく新規タブ作成や移動ブロックが機能することを確認（回帰なし）。

## 2026-05-06: editor launch / E key restore and shift enter viewer cleanup
- **E キーによる外部エディタ起動の復活**: 実使用において、スクリプトや設定ファイルなどを明示的に外部エディタで開く頻度が高いため、一度削除した E キーを外部エディタ起動用として復活させた。
- **外部エディタ起動前のテキスト判定ゲート (Corrective)**: E キー（編集）の意図が「テキストとしての編集」であることを考慮し、バイナリファイルを誤ってテキストエディタで開くのを防ぐため、起動前に `PreviewService` による判定を行うようにした。バイナリや画像と判定された場合は、外部エディタではなく内蔵 Viewer（`Enter` 相当）へリダイレクトする仕様とした。
- **notepad.exe への安全なフォールバック (Corrective)**: 外部エディタが未設定の場合、標準の `notepad.exe` をフォールバック先とした。この際、単純な文字列 `"notepad.exe"` ではなく、`System32` や `Windows` ディレクトリからの実体パス解決を優先することで、`File.Exists` チェックを伴うサービス経由での起動を確実にした。
- **手動起動時の拡張子制限撤廃（テキスト判定後）**: ユーザーが明示的に E キーまたは F4（編集）を押した場合、自動プレビュー時の制限とは異なり、対象がテキストファイルであればバイナリ判定（拡張子）をスキップしてエディタで開く仕様とした。
- **Shift+Enter の Viewer 導線削除**: 外部 Viewer の利用頻度が極めて低く、また内蔵 Viewer の強化（LargeText 等）により必要性が低下したため、Shift+Enter による外部 Viewer 導線を削除し、操作体系をシンプル化した。
- **設定UIの最小構成での復元**: 外部エディタのパス設定のみを「外部連携」タブに復元した。外部 Viewer 設定は、今後コマンドパレットや `external_tools.json` での管理へ一本化するため、SettingsForm からは復活させない方針とした。

### Verification Results
- Runtime verified; closed.
- Eキーはテキスト編集導線として実機確認済み。
- notepad.exe fallback は実体パス解決で動作確認済み。
- バイナリ/画像は外部エディタではなく Viewer 経路へ回ることを確認済み。
- Shift+Enter 外部Viewer導線削除を確認済み。
- Enter Viewer、z、x、h、Shift+h に回帰なし。
## 2026-05-05: settings dialog information architecture polish
- **タブ構成を5つに統合**: 7つに細分化されていたタブを「表示 / ビューア」「操作 / 入力」「起動 / 復元」「外部連携」「ログ / 詳細」の5つに整理した。これにより設定項目の発見性を高め、将来の拡張余地（マウスジェスチャー等）を確保した。
- **GroupBox の導入**: 各タブ内で項目を論理的なグループ（一覧表示、ビューア、ファイル操作、キー操作等）に分け、情報密度を自然にした。
- **保存ロジックの維持**: コントロール名やフィールド名を変更せず、`BtnOk_Click` での反映処理を壊さないように配慮した。既存の `AppSettings` スキーマや `settings.json` との互換性も維持している。
- **説明文の現代化**: 外部 Viewer / Editor 廃止に伴う操作体系（Z / X / H / Shift+H）を「外部連携」タブで明確に案内するように更新した。
- **2カラム構成への再編とサイズ圧縮**: 800x600 の大きな固定キャンバスに縦積みする方式では余白が目立つため、左右2カラム構成へ組み替えた。フォームサイズを 800x480 へ縮小し、各 GroupBox の高さを内容量に合わせることで、整理された印象と使いやすさを両立させた。
- **意味単位での分割 (起動 / 復元)**: 「起動 / 復元」タブを「起動時の復元」と「表示状態の復元」に分割した。これにより、フォルダ/タブの復元（起動設定）と、位置/列数/ソートの復元（表示設定）の区別を明確にした。
- **旧互換・移行項目をUIから撤去 (Corrective)**: 「JSONから移行...」ボタンと「旧マーク復元設定」チェックボックス、および「保存形式」ComboBox を削除した。これらは既に標準仕様、または内部での自動判定（SQLite優先）に寄せたため。
- **互換プロパティの維持**: `PersistMarksAcrossRestart` および `ManagedTrashStoreMode` プロパティは settings.json 互換のため残置したが、UIからは編集不可とし、保存時にも上書きしない「互換読み取りのみ」とした。
- **起動 / 復元の説明文整理**: 旧互換項目を消したことに合わせ、現在の「作業状態復元」が何を復元するかを明確にする説明文（推奨文言）に差し替えた。

## 2026-05-05: external editor/viewer setting cleanup and E key contract simplification — Verification Results
- `E` キー アンバインド / メニュー削除 / ヘルプ更新: 全て Runtime verified; OK。
- SettingsForm 旧外部UI撤去: OK。

- **`E` キーをアンバインド**: `h`/`x`/`z` で役割が分担されたことで `E` 外部エディタ導線は冗長。`E` キーを外部エディタ専用導線から撤去し、未割当状態とした。F4+Edit profile（FD互換）の場合のみ F4 → `ExecuteOpenWithEditor()` のルートを維持する。
- **メニュー「外部 Editor(&E)」を削除**: ツールメニューから外部エディタ項目を削除。コメントとして「コマンドパレット / external_tools.json へ移行」を残す。
- **SettingsForm の外部 Viewer / Editor 設定UIを撤去**: 旧設定入力欄・参照ボタン・チェックボックスを削除し、代替導線の説明文に置き換えた（z / x / h / Command Palette）。外部ツール管理ダイアログへの導線は維持する。
- **AppSettings プロパティは互換保持で残置**: `ExternalViewerPath`, `ExternalEditorPath`, `FallbackToShellWhenViewerMissing`, `FallbackToShellWhenEditorMissing` は settings.json 互換のため削除しない。UI から編集不可の「互換読み取りのみ」とする。BtnOk_Click での書き込みも停止（既存値を変更しない）。
- **`ExecuteOpenWithEditor()` メソッドは残置**: F4+Edit profile の参照が残るため削除しない。将来の cleanup フェーズで削除候補となる。
- **ヘルプメッセージを整理**: `E: 外部Editor` を削除し、`H: PowerShell / Shift+H: cmd / X: Exec` に更新。

## 2026-05-05: shell/open command routing corrective — Verification Results
- `h` / `Shift+h` / `x` / `z` / 右クリック PowerShell/cmd: 全て Runtime verified; OK。
- Space / Insert / Tab マーク操作回帰なし: OK。
- 単体リネーム拡張子除外選択: OK。


- **`x` → exec ダイアログに変更**: 旧 `x` は `ExecuteCurrentFile()`（内容確認/実行、ビューア判断あり）だったが、exec ダイアログ（任意コマンド実行）に変更した。`x` の旧挙動 `ExecuteCurrentFile()` は `z` 相当（関連付け実行）と責務が重なっており、ユーザーにとって `x` = eXec = コマンド入力の方が直感的。
- **`ExecuteShellDialog()` を新設**: 旧 `ExecuteShell()`（空入力で cmd 起動）は削除せず、`ExecuteShellDialog()` を x キー専用として追加した。選択ファイルがあれば初期入力に引用符付きパスを入れる。空入力はキャンセル扱い（cmd ターミナル起動は `h`/`Shift+h` の責務であり混ぜない）。
- **右クリックメニューをフラット化**: 「ここで開く > PowerShell / コマンドプロンプト」のサブメニュー階層をやめ、「PowerShellをここで開く(&P)」「コマンドプロンプトをここで開く(&C)」を直置きにした。1クリックで到達できる方が実用的。アクセスキーは既存の「プログラムから開く(&H)」と重複しないよう (P)/(C) を使用。
- **`z` の挙動は変更なし**: `ExecuteZLaunch()` は既にファイル=ShellAssociation実行、ディレクトリ=Explorer で開く、という「関連付けで実行/開く」として機能していた。変更は不要であり、docs 整理のみとした。
- **`E` キー調査 (Slice 4)**: `e` は `ExecuteOpenWithEditor()` で外部エディタ設定に依存している。`z`/`x`/`h` 整理後も、外部エディタが設定されていれば Editor 起動の価値は残る。後続候補 `external editor/viewer setting cleanup and E key contract simplification` として分離する。
- **Check/mark 導線の影響確認**: `Space` / `Insert` / `Tab` がマーク操作の正規導線であり、`x` 変更による影響はない。


- **`h` → PowerShell に変更**: `h` は既存の shell 起動導線として認知済みだが、開発利用では PowerShell が主力であるため、`h` を PowerShell 起動に割り当てた。
- **cmd は `Shift+h` で互換維持**: 既存ユーザーが cmd を必要とするケースを想定し、`Shift+h` で引き続き cmd.exe を起動できるようにした。完全撤去はしない。
- **`OpenTerminal(workingDir, ShellKind)` を新設**: 既存の `ExecuteShell()` は他の場所から呼ばれるコマンド実行メソッドであり、ターミナル起動とは責務が異なるため、`ExternalToolService.OpenTerminal` として独立したメソッドを追加した。
- **右クリックメニューはサブメニュー方式**: 「ここで開く（&W）」サブメニューに PowerShell / コマンドプロンプトをまとめた。既存メニューのフラットな構成を過度に長くしないための判断。
- **起動場所は CurrentPath 固定**: 選択ディレクトリ上の右クリック時にそのディレクトリを起点にする機能は今回スコープ外。CurrentPath 固定で統一する。
- **単体リネームの拡張子除外選択**: リネーム時に拡張子を誤って消すミスを防ぐため、初期選択をベース名だけに変更した。`SimpleInputDialog.ShowNullable` に `selectionLength` optional パラメータを追加し、既存の全呼び出しはデフォルト -1（全選択）を維持する。
- **`.gitignore` / 先頭ドットファイルは全選択**: `Path.GetFileNameWithoutExtension(".gitignore")` が `""` を返す挙動を利用し、baseName が空の場合は全選択（-1）のままとする。ディレクトリも全選択。
- **バリデーションリトライ後は全選択に戻す**: 初回だけ拡張子除外選択を適用し、エラー後の再入力では全選択に戻す。再入力時は自分で修正しやすい方が優先されるため。

## 2026-05-05: filter lock / working tab filter foundation & layout polish
- **フィルタロックの分離**: ReadOnly やタブ固定（ロック）とは異なり、フィルタロックは「表示上の絞り込み」に特化した機能として実装した。
- **Git判定フェイルオープン**: `git check-ignore --stdin` での判定が失敗した場合は、エラーとせずフィルタ対象外（fail-open）にすることで、作業の中断を防ぐ方針とした。
- **レイアウトの構造化**: 日付と時刻の入力欄を分離し、GroupBox でまとめることで、開始/終了日時の意味を明確にしつつ十分な視認幅を確保した。
- **「条件をクリア」表記**: クリアボタンは「解除」ではなく「条件をクリア」と表記し、入力内容のリセットであることを明示した。

### Verification Results
- Runtime verified; closed.
- フィルタロック設定により、拡張子、更新日時、Git対象外項目での絞り込みが機能することを確認。
- ダイアログのレイアウト崩れがなく、視認性が改善されたことを実機で確認。

## 2026-05-04: keyboard navigation polish / locked root and viewer selection shortcuts
- **ロックタブの `\` 挙動**: ロック（固定）タブは特定のプロジェクトやディレクトリを作業基点とするため、`\` キーによるルート移動先を「ドライブルート」ではなく「タブ固定時のパス（lock root）」に変更。これにより、不意に作業領域から逸脱するのを防ぎつつ、基点へ即座に戻れるようにした。
- **Ctrl+A の限定適用**: `Ctrl+A`（全選択）は、編集や全文コピーが一般的な「通常テキスト Viewer」に限定して実装。LargeText Viewer では数百万行の全選択によるフリーズやメモリ枯渇のリスクがあるため、意図的に行単位選択の既存契約を維持した。
- **Browser タブ移動ショートカット**: 同一カテゴリ内での前後タブ移動に `Ctrl+Left / Ctrl+Right` を割り当て。
    - `Ctrl+Tab`（全タブ巡回）は Windows の標準的な挙動として維持。
    - `Ctrl+Shift+Left / Right` は「タブの並び替え（移動）」として既に存在する可能性（または将来の予約）があるため、単純な「選択移動」を `Ctrl+Left / Right` に集約した。

### Verification Results
- Runtime verified; closed.
- 通常タブでの `\` がドライブルートへ移動することを確認。
- ロックタブ配下での `\` が lock root へ戻ることを確認。
- 通常Text Viewerで `Ctrl+A` が全選択になることを確認。
- LargeTextで `Ctrl+A` が全選択を起動せず、意図しない巨大データ処理を回避していることを確認。
- Browserで `Ctrl+Left/Right` が現在カテゴリ内の前後タブ移動になることを確認。

## 2026-05-04: mark management / UI stabilization and enhancements
- **メニューオブジェクトの保持**: メニュー表示ごとに `new` して `Closed` で `Dispose` する方式では、フォーカス移動や再表示時に不安定になるため、フィールドとして保持・再利用する方式へ変更。
- **表示トリガーを MouseDown へ変更**: `Click` イベントではボタンを離した瞬間にメニューが出るが、ドロップダウンボタンの一般的な挙動に合わせ、押し下げた瞬間（`MouseDown`）に表示するように変更。これにより、ボタンクリックの連打やフォーカス喪失時の挙動を安定化。
- **メニュー文言の整理**: 「保存▼」と「スロット管理▼」の対象を明確化。
    - 保存: `現在タブ` / `現在カテゴリ` / `Workspace全体` のマーク保存であることを明示。
    - 管理: 選択行への操作（エクスポート等）と全スロット一括操作を分離し、全スロットインポートには `（全置換）` の注釈を追加。
- **ダブルクリック導線の分離**: スロット一覧のダブルクリック時、列インデックス（HitTest）によって挙動を分ける。
    - 「表示名」列: 名前変更ダイアログを直接開く。
    - その他の列（Slot/件数等）: 従来どおり「復元」を実行。
- **右クリックメニューの追加**: 選択スロット行に対して、復元・名前変更・入出力・削除を即座に実行できる専用のコンテキストメニューを追加。広域操作（全スロット一括）はここには含めない。
- **動的な有効/無効制御**: コンテキストメニュー表示直前にスロットの状態（内容の有無、カスタム名の有無）をチェックし、操作不能な項目をグレーアウトするようにした。

### Verification Results
- Runtime verified; closed.
- `保存▼` / `スロット管理▼` の表示安定化を確認。
- 文言変更による操作対象の明確化を確認。
- スロット一覧の表示名列ダブルクリックでの名前変更起動を確認。
- 右クリックメニューの表示および項目の有効/無効制御、各操作の実行を確認。

## 2026-05-04: command palette / scalable list presentation polish
- **アコーディオン表示採用**: 外部ツール候補が増えた場合に全件表示で縦に伸びすぎるため、空検索時はカテゴリごとの上位3件表示と展開/折りたたみを採用する。
- **検索時はフラット維持**: 検索中は即実行性を優先し、見出し行を出さない。
- **Mark優先表示**: Mark は現在1件でも MidFD 本体機能側のカテゴリであるため、ユーザー追加で増える External より上に配置する。
- **カテゴリ順の整理**: `App -> Browser -> Mark -> External -> Others` の順とし、本体機能のアクセス性を維持する。
- **検索欄中心のフォーカス方針**: Command Palette は検索入力が主導線であるため、リスト選択後も入力フォーカスは検索欄へ戻す。
- **Space予約**: Space は将来の複数語検索、AND / OR 検索、簡易クエリ拡張に使えるよう、カテゴリ展開操作には割り当てない。
- **アコーディオン操作の限定**: カテゴリ展開/折りたたみは Enter / Right / Left / ダブルクリックに限定する。
- **幅安定化**: スクロールバー表示/非表示による右端情報の横ズレを避けるため、リスト幅またはスクロールバー表示を安定化する。
- **機能契約維持**: 検索・実行・external_tools.json schema・Alt+slot 直起動は変更しない。

### Verification Results
- Runtime verified; closed.
- Command Palette のカテゴリ見出し、アコーディオン表示、External の上位3件表示、Enter / Right / Left / ダブルクリックによる展開・折りたたみを実機確認。
- Space はカテゴリ展開に使わず、将来の複数語検索 / AND・OR 検索拡張を見据えて検索入力用に予約する方針で確定。
- リストをマウス選択しても検索欄中心の入力フォーカスが維持されることを確認。
- スクロールバー状態による右端表示の横ズレが抑制されることを確認。
- Command Palette 経由のコマンド実行、外部ツール起動、Browser 中の Alt+slot 直起動に回帰がないことを確認。

## 2026-05-03: command palette / external tool definition editor
- **GUI編集への移行**: `external_tools.json` の手書き運用を減らすため、外部ツール定義エディタを追加する。
- **契約維持**: 今回は既存 `external_tools.json` 契約を GUI から編集できるようにするだけで、schemaVersion 変更や recent/favorite は行わない。
- **SettingsForm分離**: 外部ツール定義は項目数が多いため、SettingsForm 内に直接埋め込まず専用 Dialog で扱う。
- **起動安定性優先**: 保存失敗時はアプリを落とさず MessageBox で通知し、既存定義を保護する。
- **altSlotの厳格化**: `altSlot` は直起動キーであるため、検索補助の `alias` より厳格に扱い、予約キー・不正文字・重複を保存時（およびダイアログ確定時）に拒否する。
- **ID自動採番**: `id` は内部管理用の安定キーであり、ユーザー編集対象にせず、新規追加時に通番（external-tool-NNN）で自動生成する。
- **AltSlot早期検証**: `altSlot` 重複を、外部ツール管理ダイアログの OK を待たず、個別ツールの追加/編集ダイアログ確定時に検出するようにし UX を改善する。

### Verification Results
- Runtime verified; closed.
- 外部ツール管理ダイアログの起動、一覧表示、追加・編集、ID自動採番、`alias` / `altSlot` 表示、`altSlot` 重複検出を実機確認。
- Command Palette 表示および `Alt+slot` 直起動に回帰がないことを確認。

### Follow-up
- Command Palette の候補数が増えた場合の視認性改善は、今回Phaseの残バグではなく `command palette / scalable list presentation polish` として後続候補へ分離する。

## 2026-05-03: command palette / legacy command launcher service model cleanup
- **外部ツール起動の正本化**: 外部ツール起動の正本を Command Palette + external_tools.json とし、旧 CommandLauncher UI 撤去後に残る旧 Service / Storage / Model を未使用確認後に削除する。
- **旧データの保護**: 旧設定ファイルやユーザーデータは念のため削除せず維持する。
- **モデルの維持**: `CommandLauncherCommand` は新 Command Palette 側の command model として現役で使用されているため、名前は旧資産風だが削除対象外とする。
- **範囲の限定**: 今回はクリーンアップに徹し、命名の変更（rename）や機能追加は行わない。

### Verification Results
- Build/static verified; closed.
- `dotnet build MidFD2.csproj` 成功。
- 旧 CommandLauncher Service / Storage / Model / 未使用Dialog の参照が残っていないことを grep で確認。
- 実機確認は不要と判断。今回Phaseはユーザー操作追加ではなく、未使用内部資産の削除であるため。

## 2026-05-03: command palette / external tool alt slot integration and legacy launcher removal
- **外部ツール導線の一本化**: 外部ツール起動の主導線を Command Palette (`Ctrl+Shift+P`) と `external_tools.json` の `altSlot` に寄せ、旧 CommandLauncher UI 起動導線を削除する。
- **後方互換拡張**: `external_tools.json` に optional な `alias` / `altSlot` を追加し、既存定義を壊さず検索性・直起動性を拡張する。
- **Alt 予約キーの保護**: `Alt+F/V/G/T/H` はメニューアクセラレータと衝突するため予約扱いにし、外部ツールスロットとしては無効とする。
- **撤去方針**: 旧 CommandLauncher の未参照 UI（Dialog 群）を先に撤去し、共有依存が残るサービス/モデルは次フェーズで段階的に切り離す。

## 2026-05-03: command launcher / external tool quick access foundation
- **外部ツール定義の分離**: 既存の CommandLauncher (Ctrl+Alt) とは別に、Command Palette 用の外部ツール定義 (`external_tools.json`) を導入。これにより組み込みコマンドと外部ツールを同一の検索UIでシームレスに扱えるようにする。
- **引数テンプレートの採用**: `{currentDir}`, `{selectedPath}`, `{markedPaths}`, `{markedPathsFile}` などのプレースホルダーをサポート。特に `{markedPathsFile}` は、大量のファイルがマークされた際のコマンドライン引数の長さ制限を回避するために重要。
- **安全な起動方式**: `ProcessStartInfo` の `UseShellExecute = false` を基本とし、MidFD 本体の安定性を損なわないよう例外処理と一時ファイルのクリーンアップを徹底する。

## 2026-05-03: startup exception logging / silent startup failure diagnostics
- **起動不能時の観測性**: exe 起動直後の例外でウィンドウすら出ない「無言の失敗」が発生した場合に備え、診断用のログ基盤を導入。
- **最小構成の Logger**: 既存 LogService への依存を避け、OS/Version/InnerException を含めて記録できる `StartupExceptionLogger` を新規作成。
- **例外フックの網羅**: `ThreadException`, `UnhandledException`, `UnobservedTaskException` を捕捉し、漏れなく記録する。
- **通知と継続性**: 例外を握りつぶして無理に継続させるのではなく、ログ保存と MessageBox 通知に留めることで、誤った状態での実行を回避する。

## 2026-05-03: command launcher / built-in command palette foundation
- **Standard profile の補助導線**: FunctionBar を非表示にする代わりに、検索可能な Command Palette を標準的な操作補助導線として構築する。
- **Built-in commands 優先**: 初期フェーズでは安全な組み込みコマンド（reload, copy path等）のみを対象とし、ユーザー定義コマンドや外部ツール連携は将来の拡張とする。
- **起動キーの安全性**: 設定での起動キー変更を自由入力ではなく候補式（ComboBox）にすることで、キー衝突や不正値による動作不良を回避する。
- **名称の整理**: 既存の外部ツール用 `CommandLauncherDialog` との競合を避け、組み込みコマンド用には `CommandPaletteDialog` を使用する。

### Verification Results
- Runtime verified; closed.
- `Ctrl+Shift+P` で Command Palette が開くことを実機確認。
- 検索および Enter/ダブルクリックでの実行が正常に動作することを確認。
- 設定画面での起動キー変更がパレット起動に反映されることを確認。

## 2026-05-03: browser header visual polish / information separator line cleanup

### Decision
- ヘッダ情報欄内の視覚的ノイズを減らすため、Path行/Item行間の内部セパレータ（`sepAfterRow2`）を非表示にする。
- Page行/Path行間の境界線（`sepBeforeTopPanel`）および一覧領域との境界線（`sepAfterRow4`）は、構造の区切りとして維持する。
- `HeaderLayoutHelper` の高さ計算を修正し、不要になったセパレータ分の隙間を詰める。
- `statusStrip` renderer は過剰に禁忌扱いしないが、今回の線の発生源ではないため変更しない。

### Verification Results
- Runtime verified; closed.
- Page行下の実線が復活していることを実機確認。
- Path行/Item行間の薄い内部線が消えていることを実機確認。
- Item行/一覧領域の境界線と一覧領域下辺枠線が維持されていることを実機確認。


## [2026-05-03] browser header interaction polish

### Context
Browser compact header の Path行 / Item行を、表示専用ではなくコピー可能な情報パネルとして使えるようにする。
直前の header compact cleanup により `Path / Item` 表示は安定したため、今回はクリックコピー、右クリックメニュー、Tooltip、Clipboard保護に限定して実装した。
また、実機確認で一覧領域の下辺枠線が起動時から表示されないことを確認したため、`contentFramePanel` の下辺描画だけを復活させた。

### Decision
- Headerコピーでは `label.Text` を使わず、内部の `CurrentPath` / selected item full path を正本にする。
- 省略表示や `[size]` は表示専用で、コピー内容には混ぜない。
- Path行は左クリック / 右クリックメニューで現在ディレクトリのフルパスをコピーする。
- Item行は左クリック / 右クリックメニューで選択項目のフルパス、またはファイル名をコピーする。
- Tooltip により、省略表示されている Path / Item のフル情報を確認可能にする。
- Clipboard 操作は失敗する可能性があるため、try/catch で保護し、status通知で結果を返す。
- Sort / MarkSummary 右端ラベルは今回クリック操作対象外にし、将来の Sort / Filter 拡張余地を残す。
- `contentFramePanel` の下辺枠線は Browser一覧領域の外枠として復活させる。
- `statusStrip top border removal` とは別問題として扱い、起動安定性の観点から `statusStrip` renderer には引き続き触らない。

### Consequences
- 省略表示された長いパスやファイル名でも、コピー内容は省略なしの正しい値になる。
- 左クリックだけでなく右クリックメニューと Tooltip があるため、発見性と確認性が上がる。
- Header の表示責務を維持したまま、情報活用の導線だけを追加できた。
- 一覧領域の下辺枠線が復活し、外枠の見た目が整った。
- `statusStrip` renderer を再導入しないため、前回疑われた起動不能リスクを避けられる。

### Verification Results
- Runtime verified; closed.
- Path行コピー、Item行コピー、右クリックメニュー、Tooltip が実機で正常動作することを確認。
- 省略表示や `[size]` がコピー内容に混ざらないことを確認。
- `contentFramePanel` の下辺枠線が起動直後から表示されることを確認。

## [2026-05-03] Function Key Profile Option / Standard and FD Compatible

### Context
現行の Browser Fキー定義は `MainForm` 内に分散しており、FunctionBar 表示、menu shortcut hint、実キー動作が別々に直書きされていた。このままでは `標準` / `FD互換` の切替を入れたときに表示と実動作のズレが起きやすく、将来の個別割当拡張にも繋がりにくい。

### Decision
- **Profile in settings.json**: `Input.FunctionKeyProfile` を追加し、`Standard` / `FDCompatible` を保存する。
- **Action-based definition**: Fキー番号から直接 `Execute*` を決めるのではなく、`FunctionKeyAction` を正本として `MainForm` が既存 `Execute*` へ接続する。
- **Definition outside MainForm**: profile ごとの F1-F12 定義は `FunctionKeyProfileService` へ切り出し、FunctionBar と menu hint も同じ定義を参照する。
- **No Custom in this phase**: 将来の `QuickAccess` / `CommandLauncher` 割当を見据えて `Action` は予約するが、`Custom` profile は今回入れない。

### Reasoning
- 表示と実動作の単一正本を持たないと、F5 / F10 のような profile 依存キーで回帰しやすい。
- `MainForm` から実処理を外へ逃がすより、外部クラスは「定義返却」に限定した方が既存 `Execute*` 契約を壊さずに済む。
- `Custom` まで同時投入すると、ID整合、無効Action、復旧手段、UI が一気に増え、今回の1フェーズを超える。

### Consequences
- Browser の Fキーだけを profile 切替対象とし、Viewer の Fキー契約は現行維持とする。
- 次フェーズで `CommandLauncher` や `QuickAccess` を Fキーへ割り当てる土台はできるが、今回は profile 選択機能だけで止める。

## [2026-05-03] browser header chrome compact cleanup

### Context
Browser のヘッダ領域において、タイトル `<< MidFD >>` が 1 行を占有しており、情報密度に対して専有面積が大きい。また、効果が不明なシステム情報表示設定が残っているため、これらを整理してコンパクトなヘッダ構成にする。さらに旧仕様の3行（Path/Info/Name）構成から、より凝縮された2行（Path/Item）構成への移行を図る。

### Decision
- **Title Removal**: `<< MidFD >>` はブランド表示として必須ではないため、Browser ヘッダから非表示にする。
- **Clock Relocation**: 時計は実用情報として維持し、Page/Total/Used/Free 行の右端へ移動する。
- **Compact Layout (2 Rows)**:
  - Path行: 左に `CurrentPath`、右に `Sort` または `MarkSummaryCompact` を表示。
  - Item行: 左に `Name [Size]`、右に `Attr Timestamp` を表示。
  - 長いファイル名やパスが右端のメタ情報と被らないように `AutoEllipsis` に加え、手動計測による省略表示を適用。
  - 通常ファイルでは、拡張子と `[size]` を可能な限り維持する `prefix…ext [size]` 形式を採用し、手動でフィットさせる。
  - 拡張子直前の `....ext` を避けるため、拡張子保持付き省略では単一省略記号 `…` を採用。
- **Settings Cleanup**: 効果が見えない `ShowSystemInfo` および `ShowLightweightInfo` の設定 UI とバックエンド処理を撤去する。
- **MarkSummaryCompact Fit (Corrective)**:
  - Mark表示は `display.MarkSummaryCompact` の直接代入ではなく、実表示幅に応じた候補選択へ変更する。
  - `lblSort` の Dock / AutoSize / TextAlign は Parent 変更時だけでなく常時適用する。
  - `HeaderPresentationHelper.DisplayStrings` を拡張し、生データ（MarkCount/SizeText）を MainForm へ渡す契約とした。
- **Border Removal Deferred**: 下部ステータスバー上の境界線削除のためのカスタムレンダラーは、適用時に起動不能が疑われたため、起動安定性を優先して取り下げ（Watchlistへ降格）る。

### Reasoning
- ヘッダの専有面積を減らすことで一覧表示領域を広げ、ユーザビリティを向上させる。
- 関連情報を凝縮することで、視線の移動を減らし一目でファイル状況を把握できるようにする。
- 1pxの視覚修正（境界線削除）よりも、アプリケーションの起動安定性を最優先する。

### Consequences
- Browser ヘッダが2行に凝縮され、モダンで引き締まった印象になる。
- 画面領域が有効活用される。
- カスタムレンダラーの排除により、環境依存の起動クラッシュを回避する。


## [2026-05-03] directory refresh / current path deleted fallback handling

### Context
現在表示中のディレクトリが外部から削除された場合、現行実装では「監視停止＋通知」に留まっていた。これを改善し、親ディレクトリ等へ自動的に fallback 移動することで、ユーザーの操作継続を支援する。

### Decision
- **Fallback Hierarchy**: 消失したディレクトリの親、上位の存在する親、ドライブルート、UserProfile、AppContext.BaseDirectory の順で fallback 先を解決する。
- **Integration into Reload path**: `ReloadCurrentDirectory` および watcher の debounce 後の再読込経路に存在確認と fallback 解決を組み込む。
- **Synchronization**: fallback 成功時は `LoadDirectory` を通じてアクティブタブの `CurrentPath`、watcher、ステータス表示を一貫して更新する。
- **Scope Limitation**: 全タブ監視やファイル内容変更監視には広げず、アクティブタブの「場所の消失」への対応に限定する。

### Reasoning
- ディレクトリ消失は外部操作で頻繁に起こり得るため、自動的な復旧はユーザビリティの向上に直結する。
- 既存の `LoadDirectory` 経路を再利用することで、選択状態の復元や watcher の張り直しなどの複雑なロジックを安全に活用できる。

### Consequences
- ディレクトリが削除されてもアプリが停止せず、安全な場所へ自動復帰する。
- fallback 後も監視が継続されるため、シームレスな操作感が維持される。

### Verification Results
- Runtime verified; closed.
- `z:\tmp\test\abc\` 表示中に上位 `z:\tmp\test\` を削除し、`z:\tmp\` へ fallback することを確認。
- `z:\tmp\abc\def\hij\klm\` 表示中に上位 `z:\tmp\abc\def\hij\` を削除し、`z:\tmp\abc\def\` へ fallback することを確認。
- 非アクティブ状態でも fallback 移動を観測。
- fallback 後の外部ファイル追加が即時反映され、watcher が新 CurrentPath へ同期していることを確認。
- fallback 後の `Ctrl+R` 通常再読込が正常に動作することを確認。
- CurrentPath が消失したままの未同期状態は、自動 fallback が先行するため再現できなかった。
- busy / Viewer 中、ロックタブ / StartupPath 境界は必要時確認の Watchlist とする。

## [2026-05-03] status strip / bottom status text vertical clipping corrective

### Context
MidFD の下部ステータス欄で、日本語メッセージや "Ready." 等の文字下端が 1〜2px 程度欠けて見える事象が実機で観測された。原因は `statusStrip` の高さがフォントに対して不十分、または `statusLabel` の余白・配置が不適切であることと考えられる。

### Decision
- **Dynamic statusStrip Height**: `NormalizeStatusLabelLayout` 内で、現在のフォント高さに基づいて `statusStrip.Height` を動的に設定する（例: `font.Height + 6px`）。
- **Normalization of Padding/Margin**: `statusLabel.Margin` を空にし、`Padding` を適切に設定することで、文字の上下位置を安定させる。
- **Maintain horizontal stability**: 以前導入した横方向の Bounds 対策（Width 制御、Overflow、Spring）の契約は維持する。
- **Minimal scope**: Dock 構造や NotificationService の再設計は行わず、`statusStrip` 周辺のレイアウト補正に留める。

### Reasoning
- フォントサイズや DPI によって最適な高さが変わるため、固定値ではなく動的な計算が必要。
- `statusLabel.TextAlign = MiddleLeft` との相性を考え、上下の Padding を均等に配分することでベースラインの沈み込みを防ぐ。

### Consequences
- 下部ステータス文字が欠けずに表示され、視認性が向上する。
- フォント変更や DPI 環境の違いに対しても堅牢な表示が維持される。

### Verification Results
- Runtime verified; closed.
- `NormalizeStatusLabelLayout` の高さ・余白補正により、下部ステータス文字の縦方向欠けが実機で解消した。
- `設定を保存しました。`、マーク解除メッセージ、起動復元メッセージなどの日本語 status が欠けずに表示されることを確認。
- 横方向 bounds 安定化、Width / Spring / Overflow 制御の契約は維持された。
- Dock / Z-order / NotificationService / Viewer status 経路の広域再設計は不要だった。
- FunctionBar visibility corrective への回帰は見られない。

## [2026-05-03] Function Key Profile Option / FunctionBar visibility by profile corrective

### Context
Function Key Profile Option 実装後、Standard profile でも Browser 下部に FunctionBar が表示され、FD互換の視覚要素が残っていた。実キー動作は Standard へ切り替わっていても、見た目としては標準操作体系に寄り切れていない。

### Decision
- **Hide FunctionBar in Standard Browser**: Standard profile の Browser では FunctionBar を表示しない。
- **Keep actions, hide only visual bar**: Standard の `F2=Rename`、`F5=Reload`、`F10=Menu` などの Action 定義は維持し、非表示にするのは FunctionBar だけに留める。
- **FDCompatible keeps visible bar**: FDCompatible profile では従来どおり FunctionBar を表示し、互換ラベルを維持する。
- **Future standard affordance stays separate**: Standard 向けの将来拡張は FunctionBar ではなく、別フェーズの Launcher Slot Bar / Command Launcher Key Binding で扱う。

### Reasoning
- Standard profile の目的は、FD互換を外した標準寄り操作体系を提供することにあるため、視覚的にも FD風補助表示を引きずらない方が一貫する。
- Action 定義まで削ると menu hint や実キー動作まで壊れるため、今回の corrective は可視性だけに限定する。
- `functionBarPanel.Visible` の制御で解けるなら、Dock 構造を壊さず最小差分で済む。

### Consequences
- Standard Browser では一覧領域を優先し、FunctionBar は表示しない。
- FDCompatible Browser では従来の補助表示を残し、既存ユーザーの操作感を維持する。

### Verification Results
- Runtime verified; closed.
- Standard profile の Browser で FunctionBar 非表示を確認。
- FDCompatible profile の Browser で FunctionBar 表示復帰を確認。
- 設定切替後の即時反映を確認。
- Standard では FD互換の視覚要素を引きずらない方針が妥当であることを確認。
- 下部ステータス欄の文字欠けは別件として後続候補に分離する。

## [2026-05-03] Directory Refresh / Active Current Path Auto Detection and Manual Reload

### Context
現在表示中のディレクトリに対する外部追加・削除・rename を一覧へ反映したいが、全タブ常時監視や `Changed` 監視はイベント量・保守コスト・ネットワークパス不安定時の負荷増を招く。初期フェーズでは既存 `LoadDirectory` を壊さず、低負荷で安全に成立させる必要がある。

### Decision
- **Active tab only watcher**: `FileSystemWatcher` は active tab の `CurrentPath` だけへ張る。
- **Dirty + debounce**: watcher event から直接 `LoadDirectory` せず、`Created` / `Deleted` / `Renamed` / `Error` を dirty 化し、300ms debounce 後に 1 回だけ再読込する。
- **No Changed tracking**: `Changed` / `LastWrite` / `Size` は初期フェーズで対象外とする。
- **Manual reload is Ctrl+R**: 取りこぼしや監視不能パスの保険として `Ctrl+R` と表示メニューの再読込を追加する。
- **No function key reassignment**: 既存の FunctionKey 契約を崩さないため、ファンクションキー割当は変更しない。

### Reasoning
- active path 限定なら全タブ監視より低負荷で、タブ切替時の watcher 張替え責務も明確。
- `Changed` は保存途中の一時状態やイベント多発を拾いやすく、初期フェーズの「一覧更新」要件に対して過剰。
- `Ctrl+R` は既存 `R` rename と衝突せず、監視失敗時の回避導線として分かりやすい。
- watcher event から直接 UI 再構築すると、自己操作中や Viewer 中の割り込みで回帰しやすい。

### Consequences
- 初期フェーズは「現在ディレクトリ一覧の安全な再列挙」に限定される。
- 現在ディレクトリ削除時の fallback 移動や file content change 追従は後続フェーズへ送る。

### Verification Results
- Runtime verified; closed.
- active `CurrentPath` に対する外部追加・削除・rename の一覧反映を確認。
- `Ctrl+R` の手動再読込と `R` 単体 rename の非衝突を確認。
- Viewer 中 / busy 中は即時再読込せず、安全なタイミングで保留反映されることを確認。
- 監視不能なネットワークパスでの watcher 失敗時挙動は未確認のまま残す。

## [2026-05-02] Large File Preview Closeout Candidate Pruning

### Context
LargeText は表示・検索・コピー・巨大 export まで実機確認が進み、first paint follow-up も現状体感では解決済み扱いと判断できる状態になった。一方で、advanced encoding detection は実用上の必要性が低く、誤判定や保守コスト増の懸念が勝る。

### Decision
- **First paint follow-up removed**: `large file preview / first paint follow-up from visual timing` は通常候補から外す。
- **Advanced encoding detection removed**: `large file preview / advanced encoding detection` は現時点で導入しない。
- **UTF-16 remains conditional**: `large file preview / UTF-16 line index support` は必要時候補として残す。
- **Next focus shifts**: 次の主候補は Workspace-aware Tabs と Mark / Slot 管理の整理に寄せる。

### Reasoning
- UTF-8 BOM / UTF-8 / CP932 が実用上成立しているため、高度な自動判定を追加する利益が薄い。
- 高度判定は誤判定・遅延・保守コスト増のリスクがある。
- MidFD の LargeText は文字コード解析ツールではなく、安全な巨大ファイルプレビューである。

### Consequences
- Next Candidate が実務優先の Workspace/Mark 系へ整理される。
- LargeText は stable な基盤として維持し、必要時だけ UTF-16 に戻る。

## [2026-05-02] Large File Preview Export Range Correctness Corrective

### Context
LargeText の巨大範囲保存時、保存されたファイルの行数が選択範囲より少なくなる現象が発生。原因は `ReadLinesAsync` が（インデックス作成途中などで）不足した行数を返した際に、それをエラーとせず書き込めた分だけで成功としていたこと。

### Decision
- **Strict Read Enforcement**: export 経路における `ReadLinesAsync` 呼び出しでは、要求数と取得数が一致しない場合を `IOException` とする。
- **Expected vs Actual Validation**: 保存処理の戻り値に行数統計を含め、`ExpectedLineCount == WrittenLineCount` の場合のみを成功ステータスとする。
- **Normalized Single Source**: 選択範囲の Start/End 前後関係を正規化するロジックを一箇所 (`NormalizeCharacterSelectionRange`) に集約し、UI/Export/Clipboard の整合性を確保。
- **Debugging Telemetry**: export の開始と終了、および選択範囲取得時に詳細なログ（行数、オフセット、プレビュー等）を出力し、実機での切り分けを可能にする。

### Consequences
- 不完全なファイル保存が「成功」として見逃されることがなくなり、ユーザーに正しく異常を通知できる。
- ログ情報の充実により、万が一ズレが再発した際の調査コストが劇的に低下。

### Verification Results (2026-05-02)
- **Massive Export**: 12,970,467 行（約1.3千万行）の文字選択範囲のエクスポートが正常に完了することを確認。
- **Accuracy**: 出力されたファイルを Mery 等で開き、末尾が選択範囲の終了行と一致すること、および EOF に到達していることを確認。
- **Success Criteria**: `ExpectedLineCount == WrittenLineCount` の判定が正しく機能し、以前の不完全な書き出し（560万行程度で止まる現象）が再現しないこと、または発生時にエラー検知されることを確認。

## [2026-05-02] Large File Preview Character Selection Column Mapping Corrective

### Context
LargeText の文字選択において、画面上のハイライト範囲とコピー結果がズレる問題が発生。原因は `Graphics.DrawString` (GDI+) と `TextRenderer.MeasureText` (GDI) の混在による文字幅・パディングの不一致、および座標から列番号を算出する際の二分探索精度の不足。

### Decision
- **Rendering Engine Unification**: 本文描画・選択ハイライト描画のすべてにおいて `TextRenderer.DrawText` (GDI) を使用するように統一し、`Graphics.DrawString` を排除。
- **NoPadding Policy**: `TextFormatFlags.NoPadding` を適用し、フォント由来の不透明な余白を無効化することで、文字境界の予測可能性を高める。
- **Constant-width Layout**: 等幅フォント前提の固定幅レイアウト計算 (`LargeTextLayout`) を導入。列番号を `(x - offset) / charWidth` で算出するシンプルなロジックに変更し、1文字単位の境界判定を `Math.Round` で行う。
- **Three-Segment Drawing**: 選択範囲を持つ行を描画する際、本文を「選択前・選択中・選択後」の3つのテキストセグメントに分割して描き分けることで、ハイライト矩形と文字表示の座標を完璧に一致させる。

### Consequences
- ハイライト位置とコピー範囲が完全に一致し、ユーザーの直感に反しない正確な選択体験を実現。
- レイアウト計算が一元化されたことで、将来的なフォント変更やスケーリングへの対応が容易になった。
- 座標計算のオーバーヘッドが低減。

## [2026-05-02] Large File Preview Large Selection Copy Guard and Export Fallback

### Context
1,000万行を超えるような巨大な文字選択範囲を `Clipboard.SetText` でクリップボードへ送ると、メモリ不足、OS制限、貼り付け先アプリのフリーズなどが発生し、実質的にコピーに失敗する。また、全量を一度に `string` 化することによるメモリ負荷も非常に高い。

### Decision
- **Clipboard Limits**: 直接クリップボードへコピーする範囲に上限（10万行、1,000万文字、または推定32MB）を設ける。
- **Export Fallback**: 上限を超える巨大範囲のコピー要求に対しては、クリップボードへの流し込みを行わず、「ファイルへ保存」を提案する。
- **Streaming Export**: ファイル保存時は `StringBuilder` 等による全量メモリ構築を避け、チャンク単位で読み込みながら `StreamWriter` で直接書き出すストリーミング方式を採用する。
- **Pre-read Estimation**: 実際にデータを読み込む前に `LineOffsets` を利用して選択範囲のバイトサイズを見積もり、早期にガードを発動させる。

### Consequences
- クリップボードコピーの失敗やアプリのフリーズを回避し、システムの安定性を確保。
- 巨大な選択範囲であっても、ファイル保存という代替手段により確実にデータを抽出可能になった。
- メモリ消費が大幅に抑制され、数GBクラスの範囲指定に対しても安全に動作する。

## [2026-05-02] Large File Preview Multi-page Character Selection and Shift-Click Extension

### Context
`offscreen character selection` の要件をさらに具体化し、長距離の選択を容易にするため、スクロール後の `Shift+Click` による範囲拡張と、ナビゲーション操作全般における選択保持が必要となった。

### Decision
- **Selection Persistence across Navigation**: 文字選択（Character Selection）は、スクロールバー操作、ホイール、PageUp/Down などのナビゲーション操作後も**クリアせずに維持**する。一方、行選択（Line Selection）はナビゲーション時にクリアする排他仕様とする。
- **Shift+Click Extension**: 既存の選択 Anchor を維持したまま、Shift を押しながらクリックすることで Caret 位置を更新し、範囲を再確定・拡張できる機能を導入。これにより、ドラッグだけでなくジャンプ移動を組み合わせた広範囲選択が可能になる。
- **Absolute Source of Truth**: 選択状態は常にファイル全体の絶対行・列で管理する。
- **Auto-scroll Caret Follow**: 欄外ドラッグによる自動スクロール中、表示されている範囲の端（先頭または末尾）へ Caret を自動追従させ、選択範囲が途切れないようにする。

### Consequences
- 巨大なファイルにおいて、数万行にわたる選択を「開始点のクリック -> スクロール -> Shift+Click」という一般的なエディタ同様の直感的操作で完遂できるようになった。
- 行選択と文字選択の挙動を分けることで、プレビュー時の利便性と編集ライクな選択体験を両立した。

## [2026-05-02] Large File Preview Offscreen Character Selection Auto-Scroll

### Context
`character selection polish` 以降、文字単位選択は可能になったが、当初は表示範囲内 (Visible Range) に限定されていた。ユーザーの「表示外を含めて範囲指定したい」という要件を満たすため、自動スクロール選択と複数ページコピーの実装が必要となった。

### Decision
- **Absolute Selection State**: 選択状態（Anchor/Caret）を表示行インデックスではなく、ファイル全体の絶対行インデックス (`absoluteLineIndex`) と列位置で保持する。これにより、スクロールしても選択位置が不変に保たれる。
- **Selection-Preserving Auto-Scroll**: 自動スクロールによる表示更新時のみ、選択をクリアしない `preserveCharacterSelection` フラグを導入する。通常のナビゲーションでは既存の選択クリア契約を維持し、不可視部分に選択が残り続ける混乱を避ける。
- **Disk-based Selection Copy**: 複数ページにまたがるコピーは、表示中バッファではなく `LargeFileLineReaderService` を用いてディスクから該当範囲を直接読み込む非同期処理として実装する。
- **Safety Guard**: 巨大なコピー（10万行超）に対しては警告ダイアログを表示し、メモリ圧迫を防止する。

### Consequences
- 長大なファイルにおいても、マウス操作だけで広範囲のテキストを正確に選択・抽出できるようになった。
- メモリ消費を抑えつつ、画面外のデータを含む一貫したコピー体験を提供できる。

## [2026-05-02] Large File Preview Visible Range Selection Clamp Corrective

### Context
`character selection auto-scroll polish` フェーズで導入した自動スクロール機能により、文字選択中に意図せず表示範囲が移動し、結果として選択状態がクリアされるという契約違反が発生した。LargeText の文字選択は「表示範囲内 (Visible Range) に限定する」という当初の方針に立ち返る必要がある。

### Decision
- **Abandon Auto-Scroll**: 文字選択中の自動スクロール機能を完全に廃止する。欄外へのドラッグは表示中の先頭/末尾行への clamp のみに留める。
- **Strict Selection Clear on Navigation**: 表示範囲が変更された場合には選択を解除する既存の契約を再確立する。これにより、見えない位置に選択が残る混乱を防止する。
- **Remove Preservation Logic**: `NavigateLargeFilePreviewAsync` に一時的に導入した選択保持フラグ (`preserveLargeTextSelection`) を削除し、ロジックを単純化する。

### Consequences
- 文字選択操作が安定し、ドラッグ中に選択範囲が勝手に消える問題が解消される。
- UI 状態管理が単純化され、ナビゲーションと選択の責務が明確に分離される。
- 複数ページ選択や自動スクロール選択は、後続の高度なフェーズ（UTF-16 対応後など）での検討事項として再定義される。

## [2026-05-02] Large File Preview Character Selection Auto-Scroll and Multi-Page Copy

### Context
`character selection polish` により文字単位選択は可能になったが、ドラッグ選択が画面内に限定されていたため、広範囲の選択が困難だった。また、画面外のテキストは UI コントロールが保持していないため、従来のコピー方法では複数ページにまたがる抽出ができなかった。

### Decision
- **Timer-based auto-scroll**: `LargeFilePreviewControl` 内に 60ms インターバルのタイマーを配置し、ドラッグ中にマウスが垂直境界外にある場合にスクロールを継続する。
- **Preserve selection on navigation**: 自動スクロールによる表示更新（`NavigateLargeFilePreviewAsync`）において、選択を解除しない `preserveLargeTextSelection` フラグを導入する。
- **Disk-based selection copy**: 複数ページにまたがるコピーは、表示中バッファではなく `LargeFileLineReaderService` を用いてディスクから該当範囲を直接読み込む非同期処理として実装する。
- **Safety Dialog**: 巨大な選択範囲（10万行超）を誤ってコピーしメモリを圧迫するのを防ぐため、実行前にユーザー確認ダイアログを表示する。

### Consequences
- 長大なファイルにおいても、マウス操作だけで広範囲のテキストを正確に選択・抽出できるようになった。
- メモリ消費を抑えつつ、画面外のデータを含む一貫したコピー体験を提供できる。
- 実装ミス（Timer 曖昧さ等）はビルドレベルで修正済み。

## [2026-05-02] Large File Preview Character Selection Polish

### Context
LargeText は行単位選択と行コピーまで実装済みだが、本文の一部文字列だけを抽出したいケースで操作粒度が粗かった。既存の status 安定契約を壊さず、custom draw 内で文字単位選択を追加する必要があった。

### Decision
- **Custom draw character selection**: TextBox 標準選択は使わず、`LargeFilePreviewControl` 内で文字選択アンカー/キャレットを管理する。
- **Area split contract**: ガター領域は行選択、本文領域は文字選択に分離して既存UXを維持する。
- **Copy priority**: `文字単位選択 > 行単位選択 > 表示中行コピー` を採用する。
- **No persistent status mutation**: 選択操作では persistent status を更新せず、コピー時の一時メッセージのみで通知する。
- **Visible range scope**: 今回は visible range 内の文字選択に限定し、自動スクロール選択や複数ページ保持は対象外とする。

### Consequences
- LargeText で必要な文字範囲のみコピーできるようになり、実用性が向上する。
- 行選択で status が消える既存問題を再発させない。

## [2026-05-02] Viewer Status ToolStripStatusLabel Bounds Corrective

### Context
app.log により、UTF-8 BOMなしでも status 文字列自体は生成されていたが、`LabelBounds.X=802` となっており、`statusStrip` の幅 800px の外側へ配置されていることが判明した。このため、視覚的にステータスが表示されない状態となっていた。

### Decision
- `statusLabel` の `Alignment` を `Left`、`Spring` を `true`、`AutoSize` を `false` に設定し、`statusStrip` のクライアント幅に収まるように `Width` を明示的に制御する。
- 長いステータス文字列は右側でクリップされるように `Overflow` を `Never`、`TextAlign` を `MiddleLeft` に設定する。
- これらのレイアウト正規化を行う `NormalizeStatusLabelLayout` メソッドを導入し、初期化、リサイズ、フォント適用、およびステータス更新の各タイミングで呼び出す。

### Consequences
- ステータスラベルが常に表示領域内に配置されるようになり、エンコーディングや行数情報の消失が解消される。
- **[Runtime verified]** UTF-8 BOMなしを含む各エンコーディングでステータスが安定して表示されることを実機確認済み。
- レイアウトの問題が根本原因であったことが証明されたため、今後エンコーディング判定やインデックス作成のロジックに不必要な手を入れる必要がなくなった。
- **[Downgrade]** `first paint follow-up` は status 表示遅延の解消に伴い、優先順位を Watchlist へ降格。真の実描画遅延が再観測されない限り実施しない。

## [2026-05-02] Large File Preview First Paint Visual Timing Instrumentation

### Context
immutable line index swap はログ上実経路に入り、LineOffsets 作成中破壊は解消した。しかし、ユーザー体感では依然として LargeText 表示までに 1秒以上の遅延が報告されている。

### Decision
- 推測に基づく追加の最適化（Task.Delay 調整や描画フラグ変更等）を一旦止め、実描画がいつ発生しているかをログで測定する。
- `LargeFilePreviewControl` に `FirstContentPainted` イベントを導入し、`OnPaint` が実際に実行されたタイミングを記録する。
- `MainForm` 側にエントリ開始からの絶対経過時間を計る `Stopwatch` を導入し、各主要イベント（status反映、インデックス開始、インデックス完了、実描画）の時系列を明確にする。

### Consequences
- 遅延の主犯が「内部状態の構築（インデックス作成等）」なのか「WinForms の描画パイプラインの詰まり（メッセージループの競合等）」なのかを切り分けられるようになる。
- 実測ログに基づき、次に取るべき最小の安全な一手を判断できる。

## [2026-05-02] Large File Preview Immutable Line Index Swap Integration Fix

### Context
前回の実装では Service/State へのメソッド追加は行われていたが、MainForm.cs の実経路において古い `BuildLineIndexAsync` が使用されていたり、接続が不完全（バックグラウンドスレッドでのスワップ等）であったため、実機での改善が見られなかった。

### Decision
- `MainForm.cs` において `BuildLineIndexOffsetsAsync` を直接呼び出し、その戻り値を UI スレッドの `BeginInvoke` 内で `state.ReplaceLineOffsets` に渡すよう徹底する。
- スワップ時のガード条件を強化し、ユーザーが既に別のファイルを選択している場合などの誤適用を防ぐ。
- 互換用の `BuildLineIndexAsync` には警告コメントを追加し、今後の誤用を防ぐ。

### Consequences
- UI スレッドでのアトミックな差し替えが確実に行われ、インデックス作成中の表示安定性が向上する。
- ログにより、期待通りのスワップが実行されていることを容易に確認できる。

## [2026-05-02] Large File Preview Immutable Line Index Swap Corrective

### Context
first paint latency は単なる描画タイミングではなく、full index 作成中に UI 正本の `LineOffsets` を破壊している構造が主犯候補と判断した。

### Decision
- `LineOffsets` は UI 表示状態として維持し、full index は local list で構築してから UI スレッドで一括 swap する。
- これ以上 `Task.Delay` / `statusStrip.Update` / encoding個別分岐で症状を追わない。
- 今回は LargeText index state の安定化であり、character selection / UTF-16 / find / copy には広げない。

### Consequences
- フルインデックス作成中も初期表示内容が安定して維持される。
- UI側での `LineOffsets` 破壊によるステータス消失やスクロールバーリセットが解消される。

## [2026-05-02] Large File Preview Defer Full Index Until First Paint Corrective

### Context
timing log により、エンコーディング判定や先頭行の取得（first line read）は遅延の主因ではなく、BuildLineIndex 完了までの約1.2〜1.3秒が体感遅延の主因であることが判明した。初期表示後すぐにフルインデックス作成が走るため、画面描画が引きずられていた。

### Decision
- **Initial Paint First**: 初期表示（先頭行の表示）をフルインデックス作成より先に確実に描画させる。
- **Deferred Index Start**: フルインデックス作成は、初期描画が完了した後に `BeginInvoke` と `Task.Delay(150)` を用いて遅延開始（deferred start）する。
- **UTF-8 BOM Observation**: UTF-8 BOM の status 欠落は内部の文字列生成の問題ではなく、描画反映タイミングの問題として扱い、判定ロジック自体は変更しない。

### Consequences
- フルインデックス作成によって初期描画がブロックされることがなくなり、体感的なレスポンスが向上する。
- 描画が安定することで、UTF-8 BOM などのエンコーディングステータスも正しく画面に反映されるようになる。

## [2026-05-02] Large File Preview Entry Timing Trace And BOM Status Corrective

### Context
first paint 改善系 corrective 後も実機で体感改善が薄く、推測で最適化を重ねると回帰リスクが高い状態だった。また Browser 選択時の `LargeText: Enterで表示` が status 汚染になっていた。加えて UTF-8 BOM 付きで `Enc:` 表示が欠落する報告があった。

### Decision
- **No speculative performance tweak**: まず `Stopwatch` で LargeText entry route のタイムラインを計測し、主犯箇所を観測する。
- **Browser status cleanup**: Browser 選択時の `LargeText: Enterで表示` は出さずに return する。
- **BOM status contract**: UTF-8 BOM でも `DetectedEncodingLabel -> GetViewerStatusLine -> ApplyViewerStatusLine` の外側 status 経路を維持し、一時メッセージによる上書きを最小化する。
- **Scope lock**: statusStrip/Dock/Z-order、find/copy、encoding判定ロジックの再設計には広げない。

### Consequences
- next step の first paint は計測ログに基づいて限定実装できる。
- Browser 操作中の不要な status ノイズを抑えられる。
- UTF-8 BOM の `Enc:` 欠落を他 encoding と同一契約で追跡・検証できる。

## [2026-05-02] Large File Preview Explicit Viewer Open First Paint Corrective

### Context
Browser上でのカーソル移動だけでLargeTextの初期読み込みが走ってしまい、ファイラの操作感を重くしていた。また、Enterで明示的にViewerを開いたときのレスポンスを改善する必要があった。

### Decision
- **Browser selection guard**: Browserモード時は `PreviewKind.LargeText` での実データ読み込みをブロックし、Viewerモード（Enter押下時）のみ読み込みを行う。
- **Viewer-only Debounce Optimization**: Viewerモード時はデバウンスの `Task.Delay(150)` をスキップし、即時描画（`Task.Yield()`）に切り替える。
- **UTF-8 BOM Display Fix**: UTF-8 BOM付きファイルでも確実にエンコーディングが表示されるよう、非同期取得後でも status の反映経路を整理した（以前の `statusStrip` 表示優先度見直しで包括的に対応）。
- **No functional regression**: 通常テキストプレビュー、画像プレビューの挙動は変更しない。

### Consequences
- Browser上でのカーソル移動が軽快になり、無駄なファイルアクセスが抑制される。
- Enterでの明示的なViewer起動時は遅延なくローディング表示が出るようになる。

## [2026-05-02] Large File Preview First Paint Latency Polish Corrective


### Context
first paint latency polish 後も体感改善が弱く、改行が少ない巨大ファイルで `ReadFirstLinesQuicklyAsync` が初回描画前に読み進みすぎる可能性が高かった。

### Decision
- **Bounded initial scan**: `ReadFirstLinesQuicklyAsync` に `maxInitialScanBytes` を導入し、初回先読みを上限付きにする。
- **Bounded fallback line read**: `IsIndexing && LineOffsets.Count<=1` の間は、初回1行読み取りを 512KB 上限に制限する。
- **Keep index contract**: full index は従来どおりバックグラウンド継続し、`PendingEndAfterIndex` 契約は維持する。
- **No scope expansion**: status route / encoding判定 / find / copy には変更を広げない。

### Consequences
- 改行が少ないファイルでも、初回描画前の待機時間を bounded 化できる。
- index 完了後の通常スクロール・ナビゲーション契約への影響を最小化できる。

## [2026-05-02] Large File Preview First Paint Latency Polish

### Context
LargeText は機能面の安定化は完了していたが、プレビュー開始から初回表示までに体感遅延が残っていた。主因は、Viewer 反映前に encoding 判定や初期読み込み待ちが先行することだった。

### Decision
- **Viewer loading first**: LargeText 分岐の冒頭で Viewer を即時反映し、`LargeText 読み込み中...` を先行表示する。
- **Async detection**: encoding 判定を `Task.Run` 化し、UI スレッドの初回待機を短縮する。
- **Indexing status first**: 起動直後から `IsIndexing=true` を立て、`Enc/Lines/indexing...` の外側 status を先に見せる。
- **No contract expansion**: navigation/find/copy/encoding 判定ロジックの契約は変更しない。

### Consequences
- Enter 直後の無反応時間が短くなり、first paint までの待ち感を低減できる。
- index 完了後の既存契約（scrollbar / End 予約 / 検索 / コピー）に影響を広げない。

## [2026-05-02] Large File Preview Selection Status Stability Corrective (Closed)

### Status
Runtime verified; closed.

### Context
LargeText で外側 status が行選択操作を契機に消える問題を修正し、実機確認で再現しなくなった。

### Decision
- **Persistent status excludes selection count**: `Selection:N 行` は persistent status に載せない。
- **SelectionChanged no longer mutates status**: `_largeFileControl.SelectionChanged` から `ApplyViewerStatusLine()` を外す。
- **Copy feedback stays transient**: 行数の通知はコピー時の一時メッセージに限定する。
- **Latency is separate**: 初回表示までの体感1秒程度の遅延は本フェーズ外として、別フェーズ候補 `large file preview / first paint latency polish` に送る。

### Consequences
- 行選択による status 消失が実機で解消された。
- status と選択ハイライトの責務が分離され、実用上の安定性が上がった。

## [2026-05-02] Large File Preview Selection Status Stability Corrective

### Context
LargeText で外側 status が一度表示された後、行選択操作を契機に消えて復帰しない実機観測が得られた。selection 操作時に persistent status を更新する経路が不安定化要因と判断した。

### Decision
- **No selection text in persistent status**: LargeText の persistent status から `Selection:N 行` を除外する。
- **No status apply on selection change**: `_largeFileControl.SelectionChanged` では `ApplyViewerStatusLine()` を呼ばない。
- **Keep copy feedback only**: 選択行数の通知は `TryCopyLargeFileVisibleText()` の一時メッセージでのみ行う。
- **No layout hacks**: Dock / Z-order / viewerPanel 内 status host は変更しない。

### Consequences
- 行選択操作で persistent status が再生成される経路を止め、status 消失の再発を抑止できる。
- copy/find/encoding の本体ロジックには影響を広げない。

## [2026-05-02] Viewer Layout Large Text Bounds And External Status Visibility Corrective

### Context
動画観測で、LargeText は読み込み中に外側 status が表示される一方、本文表示後に消えることを確認した。`ApplyViewerStatusLine` は呼ばれているため、残件は文字列更新よりも表示境界/被覆の可能性が高い。

### Decision
- **Bounds-first investigation**: status 経路追加ではなく、LargeText 表示前後の Bounds/Visible/Parent を実測ログで追う。
- **Overlap signal**: `_largeFileControl` と `statusStrip` の画面座標重なりを `OverlapsStatus` で判定する。
- **Layout stabilization only**: `ApplyViewerChromeState` 後に `PerformLayout()` を入れて、表示切替直後のレイアウト再計算を補強する。
- **No runtime root hacks**: Form直下 Dock/Z-order 介入は再導入しない。

### Consequences
- 文字列上書き問題と被覆問題を切り分けて、次の実機確認で原因を特定しやすくなる。
- 既存の status ルートや find/copy/encoding ロジックを壊さずに観測精度を上げられる。

## [2026-05-02] Viewer Layout Large Text External Status Finalization Corrective

### Context
通常Text では外側 status に `[Viewer] Enc:...` が表示される一方、LargeText では表示されない実機結果が続いた。外側 status 経路自体は成立しているため、残件は LargeText 固有の最終反映タイミングまたは後続上書きと判断した。

### Decision
- **LargeText Final Apply Focus**: 修正対象を LargeText の final status apply に限定し、表示経路の再設計には広げない。
- **Deferred Guarded Apply**: `SetVisibleLines`/`Update` 後に `BeginInvoke` で guarded な再適用を入れ、LargeText 初回描画後の最終反映を確実化する。
- **Route Diagnostics**: `LogViewerStatusRoute` を追加し、`ApplyViewerStatusLine`/`ShowStatusMessage`/timer 復帰の状態を観測可能にする。
- **No Layout Hack**: Dock / Z-order / 内部 status host の再導入は行わない。

### Consequences
- 通常Text で成立済みの経路を壊さず、LargeText 固有の反映漏れだけを切り分けて補正できる。
- 実機で `Ready.` 固定が残る場合も、上書き経路と発生タイミングをログで追跡できる。

## [2026-05-02] Viewer Status Route Consistency Audit And External Status Binding Corrective

### Context
LargeText で `Enc:` 表示が見えない事象に対して、`statusStrip` の Dock/Z-order を動的変更する案と、`viewerPanel` 内に別 status host を持つ案が混在し、表示経路が分散した。結果として実機では `Ready.` 固定や表示重なりが再発し、経路の正本が曖昧だった。

### Decision
- **Single External Route**: Viewer status は外側 `statusStrip/statusLabel` + `NotificationService.SetPersistent(...)` を正本に一本化する。
- **Disable Internal Host**: `viewerPanel` 内の `_viewerStatusLabel` は表示経路から除外し、関連参照を撤去する。
- **No Form-level Layout Hack**: `statusStrip.Dock` 強制、`Controls.SetChildIndex(...)`、`statusStrip.BringToFront()` など Form 直下の実行時レイアウト介入は行わない。
- **LargeText Label Source**: LargeText の encoding 表示は `LargeFilePreviewState.DetectedEncodingLabel` を優先する既存方針を維持する。

### Consequences
- 通常 Text / LargeText ともに同一の外側 status 経路で `Enc:` を表示でき、表示責務が明確になる。
- レイアウト破壊リスクの高い Dock/Z-order ハックを避け、Browser/Viewer の見た目回帰を抑えられる。

## [2026-05-02] Viewer Layout Status Host Corrective

### Context
`statusStrip` の Dock / Z-order を実行時に強制変更する対応は、LargeText の `Enc:` 可視化には寄与しても、Browser/Viewer 共通の外枠や下部領域のレイアウト崩れを誘発するリスクが高かった。

### Decision
- **Rollback Form-level Z-order Hacks**: `statusStrip` / `outerHostPanel` / `mainMenuStrip` の Form 直下 Dock・Z-order を実行時に書き換える処理は撤去する。
- **Viewer-local Status Host**: Viewer mode の status 表示は `viewerPanel` 内の専用 `viewerStatusLabel` に集約し、Browser mode の `statusStrip` と責務を分離する。
- **No Logic Expansion**: 今回は layout corrective に限定し、encoding detection / find / copy のロジックには広げない。

### Consequences
- LargeText と通常Text が同じ Viewer 下部 status 行を共有でき、表示責務が明確になる。
- Form 全体レイアウトへの副作用を抑えつつ、`Enc:` の可視性問題に対処できる。

## [2026-05-02] LargeText Status Bar Visibility Corrective

### Context
LargeText の encoding 判定と表示文字列は生成できていても、実機では下部 status line 自体が見えないケースがあり、`Enc:` の視認性が不足していた。

### Decision
- **Detected Label Source**: LargeText の encoding status は `LargeFilePreviewState.DetectedEncodingLabel` を正本とする。
- **Visibility Guard**: UIモード切替時に status bar の可視・Dock・Z-order を明示し、LargeFilePreviewControl が見かけ上 status 領域を覆う状態を防ぐ。
- **No Detection Changes**: 今回は status visibility corrective に限定し、encoding detection 自体は変更しない。

### Consequences
- LargeText でも通常 viewer と同じ下部 status line に `Enc:` 情報を表示できる。
- 既存の検索・コピー・表示ロジックへの影響を最小化したまま可視性問題を切り分けて解消できる。

## [2026-05-02] LargeText Detected Encoding Status Binding Corrective

### Context
LargeText の encoding detection は成立していたが、ステータスバー表示が `_viewerEncodingOverride` 由来の文言に引っ張られ、検出済み `DetectedEncodingLabel` が常時見えないケースがあった。

### Decision
- **Detected Label Priority**: LargeText の `Enc:` 表示は `LargeFilePreviewState.DetectedEncodingLabel` を最優先で使う。
- **Binding-only Scope**: 今回は status binding corrective に限定し、encoding detection 自体は変更しない。
- **Browser Persistent Reset**: Browser 復帰時は Browser 用の persistent status を再設定し、Viewer 固有の encoding 表示を残さない。

### Consequences
- `Enc: UTF-8 BOM` / `Enc: UTF-8` / `Enc: CP932` が LargeText 中に安定表示される。
- 検索・コピー・表示ロジックの回帰リスクを増やさず、UI可視性だけを改善できる。

## [2026-05-02] LargeText Encoding Status Bar Visibility Corrective

### Context
LargeText の encoding detection は実装済みだが、ユーザー観点では画面下部ステータスバーで常時確認できることが重要だった。表示中に encoding が明示されないケース（binary-like / unsupported 分岐）では可視性が不足していた。

### Decision
- **Use Existing Source of Truth**: ステータスバー表示は `LargeFilePreviewState.DetectedEncodingLabel` を正本として使う。
- **Visibility-only Corrective**: 今回は UI 可視性補強に限定し、encoding 判定・検索・コピーのロジックには手を入れない。
- **Unsupported/Binary Branch Coverage**: `binary-like` / `UTF-16 unsupported` の分岐でも、永続ステータス行に encoding 情報を反映させる。
- **No Browser Residue Contract**: Browser 復帰後に Viewer 固有表示を残さない既存契約を維持する。

### Consequences
- ユーザーは LargeText 利用中に現在の encoding を常時確認できる。
- 既存の encoding polish の挙動（表示・検索・コピー整合）を保ったまま UX だけ改善できる。

## [2026-05-02] LargeText Encoding Polish

### Context
LargeText は byte offset ベースの行インデックスを使うため、表示・検索・コピーで encoding がずれると文字化けや検索不一致が発生する。従来は LargeText 側が事実上 UTF-8 固定に近く、通常 preview の判定方針と乖離があった。

### Decision
- **Single Encoding Source for LargeText**: LargeText の表示・検索・コピーは `LargeFilePreviewState.DetectedEncoding` を単一の正本として使う。
- **Scope of Detection**: 初回 encoding polish は UTF-8 BOM / UTF-8 no BOM / CP932 を主対象にする。
- **Binary Guard**: binary-like file は LargeText として無理に開かず、安全に警告表示する。
- **UTF-16 Safety**: UTF-16 は line offset indexing との整合が必要なため、局所対応できない今回は未対応扱いとし、安全警告で停止して後続へ送る。
- **Out of Scope**: 高度な自動判定（chardet 相当）は今回対象外。

### Consequences
- 表示と検索とコピーの文字列前提が一致し、LargeText の実用安定性が上がる。
- UTF-16 を半端に受けて行ズレを起こすリスクを避けられる。
- UTF-16 line index support / advanced detection は独立フェーズで扱える。

## [2026-05-02] Large File Preview Find Polish (Closed)

### Status
Runtime verified; closed.

### Final Specification
- **LargeText Find Runtime Verified**: `Ctrl+F` / `F3` / `Shift+F3` の検索操作は実機確認 OK。
- **Large-file Reachability**: 1GB級ファイルの末尾付近の文言検索が成立することを確認。
- **UX Confirmation**: hit 行表示、status 表示、wrap、not found 表示は実用上問題ないことを確認。
- **No Regression**: copy polish、通常 txt preview、画像 preview、Enter / Esc 復帰への回帰なしを確認。
- **Out-of-Scope Kept**: 検索結果一覧、正規表現、全文インデックス、hit 文字列単位強調は本フェーズ外として維持し、必要なら後続フェーズで扱う。

## [2026-05-02] LargeText Find Polish

### Context
LargeText は custom draw / virtual line preview のため、通常 `TextBox` の検索選択機構を直接流用できない。既存実装は LargeText 向け検索経路があっても、行番号だけを返す最小経路に留まり、hit 可視化、F3 継続、古い検索結果の反映抑止が不足していた。

### Decision
- **Streaming Line Search**: LargeText は通常 `TextBox` 検索とは別に、`LargeFileLineReaderService` によるストリーミング行検索を使う。
- **Scoped Find Polish**: 初回 find polish は `Ctrl+F` / `F3` / `Shift+F3`、wrap、active hit 表示、status 反映までに限定する。
- **Navigation Reuse**: 検索ヒット移動は既存の `NavigateLargeFilePreviewAsync` を使い、スクロールバー / status / 表示行の同期を崩さない。
- **Async Guard**: `SearchRequestId` と既存 viewer state guard を併用し、Viewer 終了後やタブ切替後に古い検索結果が反映されないようにする。
- **Hit Rendering Scope**: 初回は hit 文字列単位ではなく hit 行全体のハイライトに留め、選択行がある場合は既存選択表示を優先する。

### Consequences
- 巨大ファイルでもファイル全体を一括展開せずに前方 / 後方検索を継続できる。
- copy polish や通常 txt preview の検索責務と干渉せず、LargeText の検索 UX だけを段階的に引き上げられる。
- 検索結果一覧、正規表現、全文インデックスは別フェーズへ切り出せる。

## [2026-05-02] Large File Preview Copy Polish (Closed)

### Status
Runtime verified; closed.

### Final Specification
- **Line-based Selection**: LargeText においてクリックでの単一行選択およびドラッグでの複数行選択を実装し、実機確認 OK。
- **Copy Routing**: 選択範囲がある場合は選択行のみを、ない場合は従来通り表示中の行全体をコピーする。
- **No Line Numbers**: コピーされたテキストに行番号を含めない契約を維持。
- **Out-of-Scope**: 文字単位の選択および複数ページにまたがる選択は、本フェーズの対象外とし、必要に応じて後続（`character selection polish` 等）で扱う。
- **ImageViewer Integration**: 外部ウィンドウ画像ビューア (`ImageViewerForm`) の独立性は維持し、本フェーズの変更が干渉していないことを確認。


## [2026-05-02] LargeText Copy Polish

### Context
LargeText は custom draw ベースの仮想行プレビューであり、通常 `TextBox` の文字選択機構を流用できない。既存の `Ctrl+C` は「表示中の全行コピー」まで整っていたが、実運用では必要な行範囲だけを抜き出したい要望が強く、かつ通常 txt preview や画像 preview の契約は壊したくない。

### Decision
- **Line Selection First**: 初回 polish は文字単位ではなく、absolute line index ベースの行単位選択だけを `LargeFilePreviewControl` に閉じ込めて実装する。
- **Visible Range Only**: 今回のドラッグ選択は表示中行に限定し、表示範囲外へドラッグされた場合も先頭/末尾行へ clamp する。自動スクロールや複数ページ保持には広げない。
- **Selection-aware Copy Routing**: `Ctrl+C` は LargeText 時のみ `GetSelectedText()` と `GetVisibleText()` を切り替える。コピー内容は本文のみ、行番号は含めない。
- **Navigation Clears Selection**: 複数ページ選択を未導入のまま不可視選択を残すと混乱が大きいため、表示範囲変更時は選択解除を基本とする。
- **Status via Viewer Line**: 一時メッセージを乱発せず、選択中行数は viewer status line に載せて追従させる。

### Consequences
- 最小差分で LargeText に実用的な選択コピー導線を追加できる。
- 通常 txt preview のネイティブ選択コピーと責務が混ざらず、回帰面を局所化できる。
- 文字単位選択や複数ページ選択は別フェーズへ安全に送り出せる。

## [2026-05-02] Clear Preview on Workspace Navigation (Closed)

### Status
Runtime verified; closed.

### Final Specification
- **MainForm Embedded Previews**: タブ/カテゴリ切替時は `EnsureBrowserModeBeforeWorkspaceNavigation()` を通じて同期的にクリア・終了する。これにより残像を完全に排除する。
- **External Image Viewer (`ImageViewerForm`)**: 別ウィンドウとして表示される画像ビューアについては、タブ切替時も閉じずに維持する。これは独立したウィンドウとしての利便性を優先した現時点での仕様とする。
- **Async Safety**: `_uiMode == UIMode.Viewer` チェックによるガードが有効に機能し、バックグラウンド更新による意図しない再表示が抑制されていることを確認。


## [2026-05-02] Clear Preview on Workspace Navigation

### Context
テキストプレビュー等を表示したままタブやカテゴリを切り替えた際、切り替え後のタブに古いプレビューの内容が残ってしまう（残像が発生する）問題が報告された。

### Decision
- **Unified Navigation Guard**: タブ切り替え（`SwitchBrowserTab`）およびカテゴリ切り替え（`SwitchBrowserTabCategory`）の直前に `EnsureBrowserModeBeforeWorkspaceNavigation()` を呼び出し、常に `Browser` モードへ戻してから遷移することを強制する。
- **Explicit Content Clearing**: 遷移前に `ClearPreview()` を呼び出し、Popup や MainForm 内のコントロール状態を同期的にリセットする。
- **Async Callback Guard**: `UpdatePreviewAsync` などの非同期処理の完了時チェックに `_uiMode == UIMode.Viewer` を追加し、バックグラウンドでの読み込みが完了した際に意図せず `Browser` モードの画面上にプレビューが復活するのを防止する。
- **Immediate Cancellation**: `SwitchUIMode(UIMode.Browser)` 時に `_previewCts?.Cancel()` を行い、不要になった読み込み処理を即座に破棄する。

### Consequences
- タブ/カテゴリ間でのプレビュー状態のリークが解消され、常に切り替え先のファイル一覧が優先されるようになる。
- 非同期更新による描画の不安定さが解消され、堅牢な UI モード管理が可能になる。


## [2026-05-02] Unified Viewer Exit Flicker Suppression

### Context
`LargeText` で導入した終了時ちらつき抑制（先行非表示化）が有効であったため、これを通常テキスト、画像、案内ラベルなどのすべてのプレビュー種別へ横展開する要望があった。

### Decision
- **Generalized Pre-emptive Hiding**: `LargeText` 専用だった非表示化ロジックを `HideViewerContentBeforeExit()` ヘルパーへ一般化する。
- **Target Controls**: `viewerPanel` 内の主要コントロール（`viewerTextBox`, `viewerPictureBox`, `viewerMessageLabel`, `_largeFileControl`）すべてを非表示の対象とする。
- **Visual Only**: 表示データ（テキストや画像）の破棄は行わず、あくまで `Visible = false` と `Update()` による視覚的な制御に限定する。

### Consequences
- すべてのプレビューにおいて、ブラウザ画面への切り替えがスムーズになり、モード移行時の残像や描画のズレが抑制される。
- `SwitchUIMode(UIMode.Browser)` 側の責務（パネル全体の非表示化）と相補的に動作し、二重の防御でちらつきを防ぐことができる。


## [2026-05-02] LargeText Exit Flicker Suppression

### Context
`LargeText` プレビューを閉じて `Browser` モードに戻る際、コントロールが破棄または隠される直前に表示位置が一瞬だけ先頭行に戻って見える（ちらつく）現象が観測された。

### Decision
- **Pre-emptive Hiding**: `SwitchUIMode(UIMode.Browser)` を呼び出す直前に、`LargeFilePreviewControl` を明示的に `Visible = false` に設定する。
- **Forced Synchronous Update**: 非表示設定の直後に `Update()` を呼び出し、ウィンドウマネージャに対して即時の描画更新（非表示化）を強制する。

### Consequences
- ブラウザ画面に切り替わる前にプレビュー内容が画面から消えるため、描画のズレがユーザーの目に触れるのを防ぐことができる。
- モード切り替えロジック全体を待たずに非表示化が完了するため、体感的なレスポンスの質が向上する。


## [2026-05-02] LargeText Viewer Exit Routing

### Context
`visible copy routing corrective` の改修中に、Viewer から Browser へ戻るための `Enter` / `Esc` キーハンドリングが誤って消失し、プレビューから抜けられない問題が発生した。

### Decision
- **Unified Exit Logic**: `TryExitViewerToBrowser()` ヘルパーを導入し、UIモードの切り替えを一箇所に集約する。
- **Double Routing (KeyDown & CmdKey)**: `Ctrl+C` と同様、`Enter` / `Esc` についても `KeyDown` と `ProcessCmdKey` ( `TryHandleViewerCmdKey` ) の両方でトラップし、フォーカスが子コントロールにある場合でも確実に機能するようにする。

### Consequences
- 表示方式に依存せず、常に共通の終了操作を提供できる。
- `KeyDown` にイベントが到達しないフォーカス状態でも、`ProcessCmdKey` 段階で終了を捕捉できるようになり、操作の堅牢性が向上する。


## [2026-05-02] LargeText Visible Copy Routing

### Context
`bottom clipping and basic copy corrective` で導入した `Ctrl+C` コピーが、通常のテキストボックス用のコピー処理に遮られて実行されない問題が発生した。

### Decision
- **Prioritize LargeText Copy**: `TryHandleViewerKeyDown` において、`LargeText` かつ `Ctrl+C` の判定を、従来の `viewerTextBox` 判定よりも前に行う。
- **Early Command Interception**: `ProcessCmdKey` ( `TryHandleViewerCmdKey` ) 段階でも `Ctrl+C` を捕捉し、フォーカスが子コントロールにある場合でも確実に表示中行コピーが走るようにする。
- **Helper Integration**: コピー処理を `TryCopyLargeFileVisibleText()` へ抽出し、`KeyDown` と `CmdKey` の両方から同一のロジックを呼び出す。

### Consequences
- 表示方式に依存するコピー処理が正しくルーティングされるようになる。
- フォーカス状態に左右されず、一貫したコピー体験を提供できる。


## [2026-05-02] LargeText Bottom Clipping and Basic Copy

### Context
LargeText 仮想行プレビューにおいて、最下行の文字が下端で切れて見える問題と、custom draw 方式ゆえにコピー操作が全く行えない不便さが指摘された。

### Decision
- **Strict Full-Line Counting**: `VisibleLineCount` の計算に `Math.Floor` を採用し、フォント高さに対して完全に収まる行数のみを「表示行数」として扱う。これにより、部分的に描画されて切れて見える行を排除する。
- **Bottom-Edge Clipping**: `OnPaint` 内で `ClientSize.Height` に基づく描画下端ガードを追加し、端を跨ぐ描画を物理的に抑止する。
- **Basic Copy Path (Visible Lines)**: 文字単位の選択 UI 実装はコストが高いため、まずは `Ctrl+C` により「現在表示されているすべての行」を一括でクリップボードにコピーする最小導線を導入する。
- **Clipboard Format**: コピーされるテキストは `Environment.NewLine` で結合され、行番号は含めないプレーンテキストとする。

### Consequences
- 視覚的に「中途半端に切れた行」がなくなり、表示の品質が向上する。
- 完全に表示されていない行はスクロールしない限り見えないため、情報の欠損感も解消される。
- ユーザーはマウス操作なしで、現在見ている範囲の情報を素早く抽出できるようになる。
- 通常の `TextBox` (PreviewKind.Text) におけるコピー挙動（範囲選択コピー）とは独立して実装されるため、既存機能への回帰リスクが低い。

## 2026-05-04: mark management / backup export import and workspace scoped operations
- **全スロット import は全置換**: merge 仕様は衝突解決が複雑化するため、誤解回避を優先して backup set import は全スロット置換に固定。
- **一括解除はスロット不変**: 現在タブ/カテゴリ/Workspace 解除は実行時マーク状態のみを対象にし、`markslots.json` の保存内容は削除しない。
- **0件演算結果の現在タブ適用を抑止**: 暗黙全解除を避けるため、0件時は「現在タブへ適用」を無効化し、明示的な scoped clear 操作へ責務分離。
- **Command Palette recent/favorite は分離**: 今回は mark management の単一Phaseに限定し、command palette 拡張は混在させない。

## 2026-05-04: keyboard navigation polish / locked root and viewer selection shortcuts
- **ロックタブの `\` は lock root 復帰**: 固定タブ内の `\` はドライブルートではなく、固定時の `StartupPath` へ戻す操作とする。ロック境界を越える通常移動は許可しない。
- **Ctrl+A は通常Text限定**: `PreviewKind.Text` の `viewerTextBox` だけ `SelectAll()` を追加する。LargeText は巨大ファイル全体選択のコストと既存独自選択契約を考慮し、今回対象外にする。
- **Ctrl+Left/Right はBrowserタブ移動**: Browserモード限定で現在カテゴリ内の前後タブ移動に割り当てる。既存互換として `Ctrl+Tab` / `Ctrl+Shift+Tab` は維持する。

## 2026-05-04: command palette / recent favorite and query polish
- **Usage state 分離**: Recent / Favorite は外部ツール定義ではなく Command Palette 専用の `command_palette_usage.json` に保存する。`external_tools.json` は外部ツール定義の正本として維持する。
- **空検索時の上位導線**: Favorite / Recent は空検索時だけ既存カテゴリ表示の上に出す。検索中は従来のフラット表示を維持し、カテゴリ見出しを復活させない。
- **検索 polish は AND 限定**: 初回は Space 区切りの複数語AND検索に限定し、OR / fuzzy / 正規表現は別Phaseへ分ける。
- **Space は検索入力用に維持**: 既存方針どおり、Space をカテゴリ展開には割り当てず、複数語検索の区切りとして使う。
- **Verification Results**: Runtime verified; closed.

## 2026-05-04: external tool / marked paths workflow polish
- **Schema維持**: `{markedPaths}` / `{markedPathsFile}` は既存 `external_tools.json` の引数テンプレート契約として扱い、schema 変更は行わない。
- **説明補強に限定**: 外部ツール定義編集画面へ用途説明と例を追加し、テンプレート挿入UIや外部ツール管理の再設計は今回入れない。
- **0件時は確認**: マーク0件でマーク系テンプレートを使う場合、空の値で起動してよいか確認する。既存の起動契約は維持しつつ、意図しない空実行を避ける。
- **External 起動経路維持**: Command Palette 経由と Alt+slot 直起動は共通の `InvokeLaunchExternalTool` 経由のまま扱う。
- **Verification Results**: Build/static verified; runtime verification pending.

## 2026-05-04: tab lock / locked root boundary polish
- **ロック境界維持**: lock root 上の `..` は既存タブ内で親へ移動させず、固定タブの CurrentPath を lock root 配下に保つ。
- **親参照は新規タブ**: ユーザー確認後、親フォルダは新しい非ロックタブで開く。ロック解除や lock root 変更は行わない。
- **入口限定**: 対象は `..` / Backspace の親移動入口に限定し、通常ディレクトリ移動や `\` の lock root 復帰は既存経路を維持する。
- **Verification Results**: Build/static verified; runtime verification pending.

## 2026-05-04: public alpha preparation / repository hygiene
- **追跡解除は非破壊**: `bin/obj`、個人設定、診断ログ、作業用画像などは `git rm --cached` で追跡解除し、作業ツリー上の実体は削除しない。
- **実設定はローカル状態**: `settings.json` / `external_tools.json` / `command_palette_usage.json` は配布正本ではなく、実行ディレクトリのローカル状態として `.gitignore` 対象にする。
- **サンプルは個人パスなし**: 外部ツール定義は `external_tools.sample.json` としてサンプル化し、個人環境固定パスを含めない。
- **ライセンスは未確定**: ユーザー判断が必要なため勝手に LICENSE を追加せず、README に TODO として残す。
- **Verification Results**: Build/static verified; closed.

## 2026-05-04: large text / UTF-16 line index support
- **BOM付き限定**: UTF-16 は LE/BE の BOM 付きだけ初期対応とし、BOMなし推定や UTF-32 は今回入れない。
- **byte offset モデル維持**: 既存 LargeText の正本である byte offset line index を維持し、UTF-16 ではBOM後の本文offsetから2byte単位で LF を検出する。
- **既存Encoding維持**: UTF-8 / UTF-8 BOM / CP932 の行index経路は従来どおり1byte LF検出を使う。
- **UI再設計なし**: 表示・検索・コピーは既存の `DetectedEncoding` 経路に載せ、LargeText UI の全面再設計は行わない。
- **Verification Results**: Build/static verified; service-level UTF-16 LE/BE sample verified; UI runtime verification pending.

## 2026-05-04: pre-alpha workflow polish / closeout
- **Phase 1 closeout**: `{markedPathsFile}` は「マーク一覧ファイルのパスを渡す」契約であり、エディタで一覧ファイルが開く動作を正として確定。マーク0件時は確認ダイアログ導線を維持。
- **Phase 2 closeout**: lock root 上の `..` は既存ロックタブを範囲外へ移動させず、確認後に親を新規非ロックタブで開く方針を確定。
- **Phase 3 closeout**: 実設定は追跡対象外、sample JSON は個人パスなし、README はα版前の最小構成（ビルド手順・ローカル状態・ライセンスTODO）を維持。
- **Phase 4 closeout**: UTF-16 対応は BOM付き限定を確定。BOM-less UTF-16 推定は誤判定と保守コスト増のため今回非対象。BOM-less UTF-16 LE が NG なのは仕様どおり。
- **Verification Results**: Phase 1 Runtime verified; closed。Phase 2 Runtime verified; closed。Phase 3 Build/static verified; closed。Phase 4 Runtime verified for UTF-16 LE BOM; BOM-less UTF-16 unsupported as designed; UTF-16 BE BOM は service-level verified / UI未確認。

## 2026-05-04: filter lock / working tab filter foundation
- **タブ単位の表示フィルタ**: フィルタロックは現在タブの一覧表示条件として扱い、ReadOnly、タブロック、削除抑止、ファイル操作権限とは分離する。
- **ディレクトリは拡張子フィルタ対象外**: 拡張子指定時もディレクトリは表示し、配下へ移動できる導線を維持する。
- **更新日時は分単位UI**: UIは `yyyy-MM-dd HH:mm` とし、To は `< To+1分` で比較して指定分を含める。
- **Git ignore は git に委譲**: `.gitignore` の自前パースはせず、`git check-ignore --stdin` で一括判定する。
- **Git判定失敗は fail-open**: Git未導入、worktree外、timeout、error 時は誤って項目を隠さず、Git条件を適用しない。
- **後続送り**: Git status modified/untracked、正規表現、プリセット、Workspace/カテゴリ単位フィルタは今回入れない。
- **Verification Results**: Build/static verified; runtime verification pending.


## 2026-05-05: settings dialog two-column compact layout corrective
- **2カラム構成への再編**: 縦積みによる下の大きな空白を埋めるため、各タブを左右 2 カラム構成（各 360px 幅）へ再編。
- **フォームサイズ圧縮**: 800x600 固定（または最小サイズ）を 800x480 固定へサイズを詰め、視認性を高めた。
- **GroupBox の内容量ベース化**: 画面全体を埋めるために GroupBox を縦に引き伸ばさず、項目数に応じた高さに調整。
- **設計の整理**: 「起動設定・保存の設定（カテゴリ/タブ管理）」と「表示状態の設定（位置/件数等）」に明確に分離。
- **ボタン配置調整**: フォームサイズ圧縮に合わせ、OK / Cancel ボタンを右下付近へ再配置。
- **Verification Results**: Build verified; runtime verification pending.

## 2026-05-05: settings dialog display viewer alignment corrective
- **配置定数の統一**: 表示 / ビューアタブの左右ペインで共通の配置定数（lblW, inpX, comboW, sizeX, checkX, rowH, topY）を導入し、グリッドを完全に一致させた。
- **ラベル右揃えの徹底**: ラベル長の違いを吸収し、フォント指定行の入力フィールド開始位置を左右で揃えた。
- **インデントの調整**: チェックボックスの X 座標を 32 に固定し、補足説明（HintLabel）の開始位置を整列させた。
- **文言の修正**: 右側ペインのラベルを「Viewer フォント:」に修正し、対象を明確化した。
- **Verification Results**: Build verified; Runtime verified.

## 2026-05-05: mouse gesture / browser navigation foundation
- **Browser限定**: マウスジェスチャーは Browser 一覧領域の右ドラッグだけで扱い、Viewer / Dialog / TextBox には適用しない。
- **右クリック維持**: 閾値未満の短い右クリックは従来どおりコンテキストメニューを表示し、ジェスチャー成立時だけ次の右クリックメニューを抑止する。
- **固定割当のみ**: 初期実装は固定パターンに限定し、任意割当UI、軌跡描画、編集画面は後続候補に分ける。
- **閉じたタブ復元**: `LR` はメモリ内 stack の直近閉じタブ復元に限定し、Workspace state や起動復元には混ぜない。
- **設定ON/OFF**: 誤爆時の逃げ道として `Input.EnableMouseGestures` を追加し、既定ONとする。
- **Verification Results**: Build/static verified; runtime verification pending.

## 2026-05-05: mouse gesture context menu suppression corrective
- **gesture優先**: 右ドラッグがジェスチャーとして成立した場合は、同じ右ボタン操作を通常右クリックではなく gesture command として消費する。
- **短命抑止**: 抑止は次の Browser context menu 表示1回または短時間に限定し、通常右クリックが永久に出なくなる状態を避ける。
- **複数経路で防御**: `MouseClick` だけでなく `ShowBrowserContextMenu` と `ContextMenuStrip.Opening` でも抑止を確認する。
- **Verification Results**: Build/static verification pending; runtime verification pending.

## 2026-05-05: mouse gesture / browser navigation foundation closeout
- **Verification Results**: Runtime verified; closed.
- ジェスチャー成立後は context menu より gesture command を優先する方針について、実機で再現なく確認済み。
- 短い右クリックは従来どおり context menu を表示し、gesture成立時のみ menu 抑止が動作することを確認済み。
- `L/R/U/UD/RU/LU/UR/UL/DR/LR` の固定ジェスチャー割当、設定OFF/Viewer非発火、既存選択/マーク/ダブルクリック回帰なしを確認済み。

## 2026-05-05: workspace snapshot / restore foundation
- **手動Snapshotを起動時復元と分離**: Workspace Snapshot は起動時自動復元とは別の手動保存 / 手動復元機能として扱う。`RestoreTabsOnStartup` の ON/OFF で手動復元自体はブロックしない。
- **既存JSON payload を再利用**: Snapshot payload は既存の `WorkspaceState` / `BrowserTabRestoreSnapshot` JSON をそのまま保存し、Snapshot専用の状態モデル再設計は行わない。
- **保存先は既存 SQLite に additive 追加**: `workspace.db` に `workspace_snapshots` table を追加し、起動時復元の正本テーブルとは分離したまま同一DBで管理する。
- **復元は確認必須**: 現在のカテゴリ / タブ構成を置き換えるため、復元前に確認ダイアログを必須にする。
- **最小安全策**: 復元失敗時は復元前の runtime snapshot をメモリに退避し、既存 restore 経路で巻き戻す。
- **後続送り**: export / import、差分比較、自動世代管理、特定Snapshotの起動時自動選択は今回入れない。
- **補正**: `BrowserTabSessionState` への `FilterLock` serialize / restore を明示し、Workspace Snapshot でもフィルタロックを落とさないようにした。
- **Verification Results**: Runtime verified; closed.
- 手動 Snapshot 保存 / 復元、同一 SQLite 内の additive table 管理が実機で正常に動作することを確認済み。
- Snapshot 一覧表示（名前、日時、タブ数、マーク数、フィルタロック等）および名前変更・削除を確認済み。
- 復元前の確認ダイアログ表示、および復元後にカテゴリ、タブ構成、アクティブパス、マーク、固定状態、フィルタロックが期待通りに復帰することを確認済み。
- 起動時復元設定から独立して手動復元できること、および復元後の通常操作に回帰がないことを確認済み。

## 2026-05-05: workspace snapshot / export import and command palette integration — Verification Results
- 単体 export/import: Runtime verified; OK。エクスポートされた JSON の妥当性と、インポート後の Snapshot 一覧への反映を確認。
- 一括 export/import: Runtime verified; OK。全スナップショットがバックアップセットとして正しく保存・復元されることを確認。
- インポート時の自動復元なし方針: Runtime verified; OK。インポート操作は一覧への追加のみに留まり、現在作業中の Workspace が勝手に書き換わらない安全な挙動を確認。
- 同名衝突処理: Runtime verified; OK。インポート時に名前が重複した場合、タイムスタンプが付加され別名として保存されることを確認。
- 壊れた JSON 耐性: Runtime verified; OK。不正なファイルや異なる形式の JSON を読み込んでも、エラーメッセージが表示されアプリがクラッシュしないことを確認。
- Command Palette 連携: Runtime verified; OK。「Workspace Snapshot 管理を開く」コマンドからダイアログが正常に起動することを確認。
- 既存機能回帰なし: Runtime verified; OK。Snapshot の保存、復元、名前変更、削除が引き続き正常に動作することを確認。

- **単体 import は現在Workspaceを自動復元しない**: インポートは「外部データの取り込み」であり、現在の作業状態を破壊するリスクを避けるため、一覧に追加するのみとした。復元が必要な場合は、一覧から明示的に選択して「復元」ボタンを押す運用とする。
- **全 import は追加 import (additive)**: 一括インポートにおいても、既存の Snapshot を上書き・削除せず、すべて追加として扱うことでデータの安全性を優先した。
- **Command Palette では管理Dialog起動のみ**: コマンドパレットから直接特定のスナップショットを復元する機能は、誤操作のリスクと「復元前確認」の重要性を鑑み、今回は管理ダイアログの起動（導線提供）に留めた。個別 Snapshot の直復元は、将来の拡張候補（Recent/Favorite等）として扱う。

## 2026-05-07: 7-Zip archive workflow enhancement
- **複数対象の初期archive名**: 先頭対象名ではなく CurrentPath ディレクトリ名を採用した。Mark複数対象時の誤解を避け、作業単位に一致させるため。
- **出力先初期値**: Packダイアログの初期出力先は常に CurrentPath に固定した。過去値や先頭対象親フォルダに引きずられないことを優先。
- **個別圧縮方針**: フォルダのみ複数対象のときに限定して「フォルダごとに個別圧縮」を有効化。個別圧縮時は既存archiveを上書きせず、`GetUniquePathStartingAtOne` でユニーク名を生成。
- **7zG.exe の扱い**: `7z.exe` を必須CLI経路、`7zG.exe` を任意GUI経路として分離。`7zG.exe` 不在は警告のみで、Pack機能自体は `7z.exe` fallback で継続する。
- **形式拡充の範囲**: 二段処理が必要な `tar.gz` などは今回対象外とし、`zip / 7z / tar` の単段作成のみを追加した。
- **Verification Results**: Build/static verified; runtime verification pending.

## 2026-05-08: media preview brushup / image quantization presets and video playback foundation (partial)
- **画像初期表示の正本**: 初期表示は等倍100%を正本とし、`InitialFitLimitWidth/Height` を超える場合のみ縮小表示する。
- **画像Viewerキーの境界**: `F`, `+`, `-`, `[`, `]`, `0/1`, `Ctrl+Wheel` は画像Viewer限定で扱い、BrowserやLargeText契約へ影響を広げない。
- **減色方式の採用**: 添付JSの全機能移植は行わず、C#側の `ImageQuantizationService` を新設し、プリセット方式で開始する。
- **動画再生の扱い**: OS依存内蔵再生は既存WinForms構成で大型依存なしでは成立方式の再検討が必要なため、今回Phaseでは実装停止（拡張子判定のみ先行）。
- **後続分離**: SVG/PowerPoint貼り付け導線は `image vector export / SVG clipboard and file output` に分離する。

## 2026-05-08: image viewer polish corrective / quantization controls, SVG load performance and history
- **色数ベースUIへの移行**: 用途名プリセットより判断しやすい `65536/256/16/2/色数指定` を正本UIとする。
- **65536色の扱い**: 通常パレット減色ではなく RGB565 として扱い、速度と見た目のバランスを優先する。
- **ディザ方式選択化**: `None/Floyd-Steinberg/Atkinson/Ordered` を選択可能とし、画像特性に合わせて調整可能にする。
- **色統合の追加**: SVG化前処理にも有効なため `なし/弱/中/強` を追加し、微小ノイズ色を減らせるようにする。
- **SVG読み込みの待ち感対策**: UIスレッドのブロックを避けるため SVG 読み込みを非同期化し、読み込み中表示と request id ガードを導入する。
- **Undo/Redoの範囲制限**: `Ctrl+Z/Ctrl+Y` はViewer内画像状態のみ対象とし、Browserのファイル操作Undo/Redoとは分離する。
- **Viewer前面化の契約**: MidFD側の明示的な画像更新時は、既存Viewerを復元して前面化し、見失いを減らす。
- **減色UIの再補正**: サブメニュー階層は操作理解を妨げるため、色数・ディザ・色統合を1つのダイアログで指定し、実行ボタンで処理する方式へ変更する。
- **ディザ表示名の判断**: UIにはアルゴリズム名ではなく `自然/高品質/なめらか` を表示し、内部で Void-and-Cluster 相当の青色雑音、Sierra Lite、Blue-noise Error Diffusion へ対応させる。

## 2026-05-09: agent instructions / text encoding and Japanese Markdown safety guardrail

- **AGENTS.md へ文字コード安全ルールを追加**: `Docs/07_change_log_for_ai.md` の mojibake 復旧事故を再発防止するため、日本語Markdown / docs / .codex/state の UTF-8 扱い、PowerShell 曖昧リダイレクトの回避、`git log -p` / `git show` 表示を正本にしない方針を明文化した。
- **自動復旧の停止条件を明文化**: mojibake候補や U+FFFD が見えた場合は、自動修復・行番号指定上書き・推測補完を止め、clean source / candidate file を作って検証してから採用する方針に固定した。
- **Status全件置換禁止を明文化**: `Runtime verification pending` などの履歴Statusを一括置換せず、確認済みの対象だけを限定修正する方針にした。
- **範囲限定**: `07_change_log_for_ai.md` 本文復旧は `ee825ac` で完了済みとして扱い、今回Phaseでは再編集しない。

## 2026-05-09: workspace state canonical path audit
- **Workspace復元の正本候補**: 起動時復元ON時の永続状態は `Data/Workspace/workspace.db` の `workspace_meta/categories/tabs/marks` を優先して読み込む。`settings.json` の `Session.BrowserTabRestoreSnapshot` は互換 fallback として残す判断材料とする。
- **手動Snapshotの分離**: 手動 Workspace Snapshot は同一DB内の `workspace_snapshots.payload_json` を保存単位とし、起動時復元テーブルとは別契約として扱う。
- **settings.jsonの役割**: `RestoreTabsOnStartup`、ウィンドウ位置、表示設定など Workspace 外設定は `settings.json` が正本。`OpenTabs` / `BrowserTabCategories` などの旧 mirror は互換参照用で、通常 save では永続化対象から外される。
- **Mark経路の分離**: タブ内マークは Workspace state / snapshot に含める一方、Mark slot は `markslots.json` が別正本。`PersistedMarkedPaths` は RestoreTabsOnStartup OFF 時の legacy mark persistence として残す判断材料とする。
- **今回の扱い**: audit のみで、SQLite schema、settings schema、migration、旧経路撤去、MainForm分解、文字化け復旧は行わない。

## 2026-05-09: workspace state canonical cleanup / legacy session mirror reduction
- **通常同期は snapshot / SQLite へ寄せる**: 通常操作後の `StoreActiveBrowserTabCategorySessionState` は `BrowserTabRestoreSnapshot` を更新し、legacy `OpenTabs` / `ActiveTabIndex` / `BrowserTabCategories` mirror は更新しない方針へ寄せた。
- **fallbackは残す**: `Session.BrowserTabRestoreSnapshot`、旧settings materialize、workspace store load失敗時 fallback は data protection のため維持する。
- **完全撤去はしない**: `SessionSettings` の legacy mirror プロパティ、settings schema、SQLite schema、Snapshot payload、Mark slot、`PersistedMarkedPaths` は今回変更しない。

- **Verification Results**: Debug版での通常操作後、SQLite WAL側(workspace.db-wal)の更新と `BrowserTabRestoreSnapshot` の更新を確認。legacy mirrorの積極更新が抑制されていること、再起動でのタブ・ロック・フィルタ状態復元、Snapshot/MarkSlotに回帰がないことを実機確認した。

## 2026-05-09: dialog keyboard contract / OK Cancel key binding normalization
- **共通契約**: `DialogKeyboardHelper` を追加し、`Esc=Cancel/Close`、`Y=OK/Yes/Execute`、`N=Cancel/No` を共通化した。
- **入力中の非介入**: `TextBoxBase`、編集可能 `ComboBox`、`NumericUpDown`/`UpDownBase`、`DateTimePicker`、`DataGridView` へフォーカス中は plain `Y/N` を処理しない方針にした。
- **安全契約維持**: `DeleteConfirmDialog` は非対象とし、multi-file permanent delete の `Alt+Y` 必須契約を維持する。
- **適用範囲の限定**: 既存の独自 `ProcessCmdKey` 契約を持つDialogには広げず、OK/Cancel系の標準確認Dialogを中心に適用した。

- **Verification Results**: Runtime verified; closed.
  - 入力なしDialogで `Y=OK/実行`、`N=Cancel/No`、`Esc=Close/Cancel` が動作することを確認。
  - 入力ありDialogで、TextBox等への `y/n` 入力が奪われず、勝手にOK/Cancelにならないことを確認。
  - `DeleteConfirmDialog` の multi-file permanent delete で plain `Y` が通らず、従来どおり `Alt+Y` 必須契約が維持されていることを確認。

## 2026-05-11: Browser tab indexed overflow navigation
- **firstVisibleTabIndex方式を採用**: pixel scroll offset方式は描画座標、クリック判定、ドラッグ挿入位置の同時変更が必要で破綻しやすいため、タブindex単位の表示開始位置管理にした。
- **表示中タブだけを判定対象にする**: 非表示タブのクリック矩形を残さず、`_tabBoundIndexes` で表示矩形から実タブindexへ変換する方針にした。
- **ドラッグ自動スクロールは対象外**: 表示中タブ間の既存ドラッグ維持を優先し、非表示領域をまたぐドラッグ拡張は今回入れない。
- **Verification Results**: Build verified; runtime verification pending.

## 2026-05-11: Browser tab indexed overflow partial tail corrective
- **右端空白の扱い**: タブ幅を伸ばすとタブ位置とクリック判定が可変化するため、`PreferredTabWidth` は維持し、余り幅には次タブを部分表示する方針にした。
- **部分表示の下限**: 誤クリックと崩れた描画を避けるため、32px未満の部分タブは描画・判定対象にしない。
- **`>` 表示条件**: 最後のタブが完全表示または部分表示されていれば、右側に未表示タブなしとして `>` を非表示にする。
- **Verification Results**: Runtime verified; closed.

## 2026-05-11: Browser tab overflow dropdown and mouse-wheel clamp corrective
- **遠方タブアクセス**: overflow時に全タブ一覧ドロップダウンを追加し、遠いタブへ直接切り替えられる導線を補完した。
- **ホイールとキーボードの分離**: マウスホイールは物理操作として左右端で停止させ、キーボード移動の既存ループ仕様とは分離した。
- **通常表示の幅確保**: タブ一覧ボタンはoverflow時のみ表示し、通常時のタブ表示領域を狭めない方針にした。
- **Verification Results**: Runtime verified; closed.

## 2026-05-12: browser preview request coalescing and binary kind fast-path corrective
- **Preview要求の正本**: `UpdatePreviewAsync` 内で現在選択を後読みする方式をやめ、要求発行時の `requestPath` snapshot を正本にした。同一 `reqId` のログと処理対象が途中で別pathに見える状態を避けるため。
- **latest-only化**: 新規preview要求では前回 `CancellationTokenSource` をCancel/Disposeし、await復帰後は `reqId`、`requestPath`、現在選択pathを確認して古い要求をUI反映しない方針にした。
- **同一path重複抑制**: Browser側の通常選択変更では同一pathの重複要求を積まない。Viewer開始、Preview popup再表示、エンコーディング切替は明示再評価として `force` 扱いにした。
- **Binary fast-path**: `.exe/.dll/.msi/.wim/.iso/.zip/.7z/.rar/.cab/.pptx/.xlsx/.docx/.ppt/.xls/.doc/.pdf` は画像・動画判定後、内容読み取り系判定へ進まず `PreviewKind.Binary` を返す。Office文書やinstaller/archiveを本文プレビュー対象にしない現行契約を優先した。
- **今回広げない範囲**: FileSystemWatcher / 外部変更 Error、directory refresh、タブ復元・カテゴリ保存契約、LargeText本文仕様は別Phase扱いとし、同時変更しない。
- **Verification Results**: Build verified; runtime verification pending.

## 2026-05-12: browser tab header refresh coalescing corrective
- **ヘッダ表示snapshot**: `RefreshBrowserTabHeaders` でカテゴリ行表示、ActiveCategoryIndex、ActiveTabIndex、カテゴリ表示情報、タブ表示テキスト、Tooltipからsnapshotを作り、同一なら `SetCategories` / `SetTabs` / INFOログをスキップする。
- **Strip側の二重ガード**: `BrowserTabStrip.SetCategories` / `SetTabs` でも同一リストは早期returnし、呼び出し漏れがあっても不要なInvalidateとログを抑制する。
- **更新される条件**: タブ追加/削除/リネーム、タブ切替、ロック/ReadOnly/CurrentPath/Tooltip変化、カテゴリ追加/削除/リネーム/切替、カテゴリ行表示変化ではsnapshotが変わるため従来通り更新される。
- **今回広げない範囲**: preview latest-only、Binary fast-path、FileSystemWatcher、directory refresh、Workspace state、タブoverflow仕様は変更しない。
- **Verification Results**: Build verified; runtime verification pending.

## 2026-05-13: sort dialog radio button exclusivity corrective
- **明示排他を採用**: SortダイアログのRadioButtonは見た目上GroupBox内にあるが、初期化順や将来の親コンテナ変更に依存しないよう、条件と順番をそれぞれhelper経由で1つだけCheckedにする方針にした。
- **選択値の正本**: OK押下時は複数Checked状態を前提にせず、`GetSelectedSortKey()` のfallback込みで単一のsort keyへ確定する。
- **今回広げない範囲**: ソートアルゴリズム、ソートキー仕様、設定保存形式、ファイル一覧描画、タブ・プレビュー系は変更しない。
- **Verification Results**: Runtime verified; closed.

## 2026-05-13: video preview modal suppression on browser selection corrective
- **自動previewでのVideo扱い**: Browser選択変更および `UpdatePreviewAsync` のVideo分岐では、`ImageViewerForm.LoadMedia(Video)` を呼ばず、MessageBoxを出さない方針にした。
- **明示openでの通知**: Enter / View 等の明示openでVideoを開こうとした場合は、モーダルではなくstatus通知「動画の内蔵再生は未対応です。」に限定する。
- **LoadMediaの安全化**: `ImageViewerForm.LoadMedia(Video)` は呼び出し文脈を判定できないため、MessageBoxを直接出さずstatus表示だけにした。
- **今回広げない範囲**: 動画再生実装、動画サムネイル、外部プレイヤー連携、preview latest-only、Binary fast-path、SortDialog追加修正は行わない。
- **Verification Results**: Runtime verified; closed.

## 2026-05-13: image viewer auto-follow modal suppression corrective
- **自動追従は非モーダル**: Browser選択変更や自動previewから既存ImageViewerを追従更新する場合、画像/SVG読み込み失敗でMessageBoxを出さず、Viewer内statusとログに留める。
- **明示openは通知維持**: Enter / View / ImageViewer内のファイル選択では従来通り失敗通知を許容する。自動追従失敗後に同じpathを明示openした場合は、未読込状態なら再読込して通知できるようにした。
- **古いSVG世代管理維持**: 既存の `_loadRequestId` による世代違い破棄は変更せず、古い読み込み結果や失敗結果を反映しない方針を維持する。
- **今回広げない範囲**: ImageViewer外部EXE化、動画別アプリ化、減色、Undo/Redo、SVGコピー、preview latest-onlyは変更しない。
- **Verification Results**: Build verified; runtime verification pending.

## 2026-05-14: browser selection shallow preview classification corrective
- **Browser選択変更では浅い分類に限定**: カーソル移動時は `PreviewService.GetPreviewKind` を呼ばず、拡張子と一覧上のファイル/ディレクトリ判定だけで自動preview可否を決める。ネットワークや低速環境で本文読み込み、LargeText判定、ショートカット解決へ入らないことを優先するため。
- **自動preview対象を画像系だけに絞る**: 画像/SVGは既存ImageViewer自動追従を維持する。一方でディレクトリ、`.lnk`、`.url`、動画、Binary fast-path拡張子、テキスト系拡張子、不明拡張子では Browser 選択変更だけで preview request を出さない。
- **深い判定は明示操作へ寄せる**: 本文読み込み、LargeText判定、文字コード判定、長大行判定、ショートカット解決は Enter / View / 明示Open など従来の深い経路に残す。通常カーソル移動の軽さを優先し、α版前の正本経路を単純化するため。
- **Verification Results**: Build verified; runtime verification pending.

## 2026-05-14: browser auto-preview skip logging and clear suppression corrective
- **期待通りのskipは記録しない**: Browser自動preview対象外は正常系のため、`AutoPreviewIneligible` のINFOログは出さない方針にした。ネットワーク環境での大量カーソル移動時に、観測価値の低いログを増やさないため。
- **対象外では事前クリアしない**: `FileListView_SelectedIndexChanged` の同期 `ClearPreview()` は自動preview対象に限定し、対象外では不要な空クリアを避ける方針にした。カーソル移動ごとの再描画とちらつき抑制を優先するため。
- **同一案内文の再クリア抑止**: 同じpathで同じ案内文がすでに表示されている場合は `ClearPreview(message)` を再実行しない。ディレクトリやテキスト系の連続選択で同一表示を無駄に描き直さないため。
- **Verification Results**: Build verified; runtime verification pending.

## 2026-05-14: browser auto-preview suppressed-state clear guard corrective
- **抑制条件を表示状態単位へ変更**: Browser自動preview対象外では、path一致ではなく「すでに自動preview抑制表示中で、案内messageが同じか」で `ClearPreview(message)` を抑止する方針にした。`.txt -> .csv -> .url -> フォルダ` のように path が変わっても同じ案内表示なら再描画しないため。
- **path更新は維持する**: 再描画を抑止する場合でも `_currentPreviewTarget` は現在選択pathへ更新する。preview正本の追跡を崩さず、表示だけを据え置くため。
- **古い画像表示の初回消去を維持**: 画像/SVG側へ入る経路、および `force=true` の深いpreview経路へ進む直前では自動preview抑制状態を明示的に解除する。画像表示中から非対象へ移動した初回は必ず `ClearPreview(message)` が走るようにするため。
- **Verification Results**: Build verified; runtime verification pending.

## 2026-05-15: browser preview cursor-move performance closeout
- **深いPreview判定をカーソル移動から外す方針を継続採用**: Browserカーソル移動では深い `PreviewKind` 判定、本文読み込み、LargeText判定、ショートカット解決へ進まない構成を維持する。自宅端末のネットワークドライブ上押しっぱなし移動で体感上問題が再現しなかったため。
- **対象外skipログと同一ClearPreview更新を抑制する方針を継続採用**: 自動preview対象外では `AutoPreviewIneligible` INFOログを出さず、同一案内表示の `ClearPreview` 再実行も抑止する。カーソル移動時の観測ノイズと再描画ノイズを増やさないため。
- **ClearPreview抑制はpath単位ではなく表示状態単位を正本とする**: pathが変わっても同じ抑制表示なら再描画せず、初回の画像消去だけ保証する現在方式を正本とする。
- **再発時は巻き戻さず別Phaseで切る**: 職場端末などで違和感が再発した場合は本closeoutを巻き戻さず、画像自動追従debounce、skipログ、FileSystemWatcher Error 経路を別Phaseで観測・補正する。
- **Verification Results**: Runtime verified; closed.

## 2026-05-16: archive workflow reliability corrective
- **archive出力先解決の正本化**: `archive ファイル名` にディレクトリ成分がある場合は、そのパスを保持したまま拡張子同期する。`z:\foo\bar.zip` のような明示パスを `出力先フォルダ` で上書きしないため。
- **形式追加**: Pack形式に `gzip / bzip2 / xz / wim` を追加した。ユーザー観測の `win` は7-Zip文脈では `wim` と判断し、UI/内部値を `wim` で統一した。
- **多数ファイルPackの長い引数対策**: `SevenZipService.Pack` は `@listfile` + `-scsUTF-8` を使用する方針へ変更。ワイルドカード入力時のみ従来引数追加を維持した。
- **進捗表示**: Pack/Unpack 中は `FileOperationProgressFallbackForm` を表示し、少なくとも待機状態を明示する。キャンセル導線は既存 `PrepareFileOperation` のキャンセルへ接続した。
- **7-Zipなし時fallback**: zip形式限定で `System.IO.Compression.ZipFile` fallback を追加。非zip形式は従来どおり7-Zip必須とする。
- **今回広げない範囲**: tar.gz 等の複合形式、7-Zip以外の外部アーカイバ連携、archive workflow全面再設計は実施しない。
- **Verification Results**: Build verified; runtime verification pending.

## 2026-05-19: VideoStill ImageViewerForm closeout and documentation alignment
- VideoStillの正本表示先は MainForm内ではなく ImageViewerForm とする。
- MainForm側は、動画選択時の軽い案内と Ctrl+Enter 外部再生導線に留める。
- Enter / V で ImageViewerForm を開き、動画から生成した静止画を表示する。
- ImageViewerForm上で位置バー、←/→、Shift+←/→、Home、Ctrl+Enterを扱う。
- VideoStill時の statusStrip は下部黒帯の主因だったため非表示にし、細い位置バーのみ残す。
- 通常画像表示時は statusStrip を従来どおり表示する。
- 初期表示は0秒。
- ffplay の `-ss` は厳密一致ではなく近傍シーク。
- ffmpeg / ffplay / ffprobe は同梱しない。

## 2026-05-16: bulk move hotpath corrective
- **Move loop中のUI同期を除去**: 大量Moveで `Task.Run` ループ内から `Invoke(UnmarkPathsInBulk)` していた経路を廃止し、成功したsource pathを収集してループ完了後に一括Unmarkする方式へ変更。Move本体の待ちを減らし、UIスレッド同期をホットパスから外すため。
- **主因候補の計測を追加**: `ExecuteMove` に `[MoveHotpath] Summary` を追加し、`loopMs` / `fileMoveCallMsTotal` / `fileMoveCallMsMax` / `destinationCheckMs` / `progressReportMs` / `progressReportCount` / `collisionCheckCount` / `collisionDialogCount` / `undoCreateMs` / `unmarkApplyMs` を記録する方針とした。
- **FileOperationService.Moveの扱い**: Move意味（overwrite、cross-volume fallback、例外契約）は変更せず、今回は呼び出し側のホットパス補正と計測に限定した。Copy/Delete/Renameへの波及リスクを避けるため。
- **direct fast pathは見送り**: collision/merge/overwrite/undo契約差分の混入リスクがあるため、今回Phaseでは導入しない。まず計測ログで主因を絞る。
- **Verification Results**: Runtime verified; closed.
  - 大量Move (1860件, 1537件) において I/O 以外のオーバーヘッドが最小化されていることを確認。
  - キャンセル時に成功分のみが正しくUnmarkされることを確認。

## 2026-05-16: dialog cancel key contract corrective
- **棚卸し結果**: `Dialogs/*`, `SettingsForm`, `FileOperationProgressFallbackForm`, `ImageViewerForm` を確認し、通常ダイアログは概ね `CancelButton` または `Esc` 処理を持つことを確認。
- **修正対象の特定**: `QuickAccessDialog` は `CancelButton` が「閉じる（SaveOnly）」に結びつき、EscがCancelではなくSaveOnlyになる契約だったため、キャンセル可能ダイアログとして不整合と判断。
- **採用方針**: `QuickAccessDialog` に明示的な `キャンセル` ボタンを追加し、`CancelButton` と Esc を `DialogResult.Cancel` に統一。`閉じる` は従来どおり SaveOnly 導線として維持。
- **×ボタン契約**: `FormClosing` で SaveOnly に強制変換していた処理を削除し、`×` は Cancel 扱いに統一。Esc / Cancel / × の終了契約を揃えるため。
- **Esc非対応維持**: `FileOperationProgressFallbackForm` は処理中ダイアログとして Esc=「キャンセル要求」に接続する既存契約を維持（閉じる動作にはしない）。
- **Verification Results**: Build verified; runtime verification pending.

## 2026-05-16: dialog cancel key contract closeout
- **Runtime verified; closed**: ユーザー実機確認により `QuickAccessDialog` の Esc / `キャンセル` / `×` が Cancel 終了で一致することを確認。
- **SaveOnly導線維持**: `QuickAccessDialog` の `閉じる` ボタンは SaveOnly（反映終了）として従来挙動を維持することを確認。
- **主要Dialog回帰なし**: `SettingsForm`, `PackDialog`, `MarkSlotDialog`, `ArchiveListDialog`, `CommandPaletteDialog` の Esc 契約に回帰がないことを確認。
- **処理中Dialog契約維持**: `FileOperationProgressFallbackForm` と `canCancel:false` 進捗ダイアログが Esc で単純クローズしない契約を維持することを確認。

## 2026-05-17: repository source hygiene before publication
- **公開前衛生チェックを実施**: 公開文書、Git管理設定、機密文字列、旧名称、文字化け、生成物混入、依存パッケージを横断確認した。公開前の安全確認を優先し、新機能追加は行わない方針とした。
- **軽微修正のみ採用**: `.gitignore` の不足項目（`publish/` `logs/` `settings.json` `external_tools.json` `command_palette_usage.json` `scratch/` `*.tmp` `*.bak`）を追加し、環境依存ファイルと一時ファイルの混入リスクを下げた。
- **コード変更は非挙動差分に限定**: `MainForm.cs` の文字化けコメントを自然な日本語へ置換した。実行経路・機能仕様・設定/DBスキーマは変更していない。
- **見送り判断**: `.codex` / `Docs` / `artifacts` の公開可否は運用方針判断を伴うため、このPhaseでは履歴削除や追跡解除を実施しない。履歴保全を優先し、別判断事項として残す。
- **Verification Results**: Build verified; static hygiene checks passed.

## 2026-05-17: public repository final source hygiene corrective
- **Markdown改行設定を全体化**: `.gitattributes` は `*.md text eol=lf` を正本にし、個別の `README.md` / `UserDocs/*.md` 指定より広く安全に適用する方針へ補正した。GitHub公開前に Markdown の改行差分を増やさないため。
- **`.gitignore` は再修正しない**: 直前Phaseで `publish/`, `logs/`, `settings.json`, `external_tools.json`, `command_palette_usage.json`, `scratch/`, `*.tmp`, `*.bak` が追加済みであり、今回Sliceの目的を満たしているため重複編集を避けた。
- **文字化けコメントは1件のみ補正**: `MainForm.cs` の window bounds collapse guard helpers 周辺に残っていた文字化けコメントだけを自然な日本語へ置換した。ロジック、メソッド名、挙動は変更していない。
- **Verification Results**: Build verified; static verified; runtime verification not required.

## 2026-05-17: Public release Git operation boundary
- **運用分離を採用**: MidFD public公開後のGit運用として、開発用 `MidFD` / `tk999jp/MidFD-dev` と公開用 `MidFD-publish` / `tk999jp/MidFD` を分離する方針を採用した。
- **理由**: public 側の履歴・タグ・Release Assets は利用者が参照済みの可能性があり、通常の実装、検証、試行錯誤は private 側で行う方が安全なため。
- **決定**: 通常開発は `G:\source\repos\MidFD` で行い、public反映は `G:\source\repos\MidFD-publish` に export 後、通常コミットで行う。public公開後は原則として `commit --amend`、`rebase`、`push --force-with-lease` を使わず、公開済みタグは基本的に動かさない。
- **配布手順の正本**: 詳細手順は `Docs/release_procedure.md` に置き、公開用リポジトリ側には内部運用情報を持ち込まない。

## 2026-05-18: archive contents implicit directory synthesis corrective
- **結論: アーカイブ内に明示ディレクトリエントリがなくても、パスから中間ディレクトリを合成して表示できるようにし、さらに明示ディレクトリと合成ディレクトリの重複を完璧に防止した**
  - **対策の実装**:
    - ArchiveListDialog.PopulateItems() において、現在地 _currentPath から見て深い階層にあるエントリについて、最初のパスセグメントを「合成中間ディレクトリ（IsSyntheticDirectory = true）」として動的に構築。
    - 明示的なディレクトリエントリ（IsDirectory = true）を isibleMap に登録する際、末尾スラッシュがない場合はキーの末尾に / を補正して登録（key = entry.EntryPath + "/"）するように統一。
    - これにより、合成ディレクトリ（常に / で終わる）と明示ディレクトリが末尾のスラッシュの有無によって二重に登録されてしまうバグを完全に解決した。
    - 合成されたディレクトリは移動用（IsDirectory = true）として機能させ、マーク処理（ToggleMark）では entry.IsSyntheticDirectory の場合に早期リターンすることで安全にガード。
  - **検証結果**: Build verified; closed. dotnet build に成功し、正常に機能することを確認。
