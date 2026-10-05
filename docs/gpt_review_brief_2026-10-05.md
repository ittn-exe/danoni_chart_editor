# GPTレビュー依頼資料（danoni_editor）— 2026-10-05

本ファイルは、外部のレビュー担当（GPT）に渡すための依頼書である。
末尾の「提出ファイル一覧」にあるソースと併せて渡すこと。

---

## 0. 依頼の目的

danoni_editor（danoniplus向け譜面エディタ、C# / .NET 8 / WPF、Windows専用）について、次の2点を中心にレビューしてほしい。

1. **動作安定性**: 操作や入力によってクラッシュ・ハング・データ破損を起こさないか。Windowsに過剰な負担（メモリ、ハンドル、タイマー、I/O、スレッド）をかけていないか。
2. **動作快適性**: アルゴリズムや描画の最適化余地がどこにあり、どこが限界か。

加えて、良さそうな機能の提案があれば歓迎する。

## 1. プロジェクト概要（前提知識）

- ソリューション構成:
  - `DanoniEditor.Core`: モデル、入出力（Import/Export）、タイミング、設定。WPF非依存
  - `DanoniEditor.Editing`: 編集ロジック（EditorDocument、Undo、各Controller）。WPF非依存
  - `DanoniEditor.App`: WPF UI層（MainWindow、ChartCanvas など）
  - `DanoniEditor.Collab`: 共同編集（TCP、独自プロトコル）
  - `DanoniEditor.PluginContracts`: プラグインAPI契約
- 内部tick分解能は `TimingEngine.TicksPerBeat = 1680`。
- 譜面ビューは `ChartCanvas.OnRender` で `DrawingContext` へ直接描画する方式（ノートごとのUIElementは作らない）。
- Undoはコマンド方式（`IEditAction`）。難易度タブごとにスタックを分離。容量は既定30。
- 配布は `PublishSingleFile` + self-contained（ネイティブDLLのみ `lib` へ移動）。
- テストは `tests/DanoniEditor.Core.Tests` のみ。Core層とEditing層が対象。App層（WPF）は自動テスト対象外。
- コメントは非常に厚く、日付プレフィックスと「なぜそうしたか」が書かれている。コメント量の多さは仕様であり、指摘不要。

## 2. レビューの観点

### 2.1 安定性
- 例外の握りつぶし、あるいは未処理例外でアプリ全体が終了する経路
- ファイル保存の原子性、同時書き込み、複数プロセス（複数ウィンドウ）間の競合
- スレッド境界（UIスレッドが所有するデータを別スレッドから読み書きしていないか）
- 外部入力（dos.txt、プロジェクトJSON、共同編集メッセージ、プラグインDLL）に対する検証
- リソースの解放漏れ（イベント購読、`CompositionTarget.Rendering`、タイマー、`Process` オブジェクト、音声出力）
- 無制限に増えるキャッシュ、巨大オブジェクト（LOH）の確保パターン
- 0除算、無限ループ、極端に大きな値（tick、BPM、拍子）での挙動

### 2.2 快適性
- `OnRender` と再生中の毎フレーム処理のコスト
- 計算量（特に `TimingEngine`、小節走査、描画ループ内のLINQ・割り当て）
- 再描画の粒度（全面再描画になっていないか）
- 「これ以上は効果が薄い」と判断できる箇所の見極め

### 2.3 機能提案
- 既存機能（検証・警告、解析、レーン入替マクロ、コピーマネージャー、共同編集等）と相性の良いもの
- 実装コストと効果の見積もり付きだと助かる

## 3. 事前調査の所見（Claudeによる机上レビュー。**未実測・未ビルド、要検証**）

以下は仮説である。誤りや見落としがあれば遠慮なく指摘してほしい。行番号は2026-10-05時点で、ずれる場合がある。

### 3.1 安定性

| 重要度 | 所見 | 該当箇所 |
|---|---|---|
| 高 | 保存が `File.WriteAllText` による直接上書き（非アトミック）。電源断・クラッシュ・ディスク満杯・ロックで本体ファイルが破損し得る。自動保存のスロットとmanifestも同様。manifestの読み→変更→書きが複数プロセス間で競合し、他プロセスのエントリが消え得る | `Core/Persistence/ProjectSerializer.cs`（Save、SaveTabExport）、`Core/Persistence/AutoSaveManager.cs`（WriteSlot、SaveManifest） |
| 高 | 共同編集のスナップショット生成（`SnapshotProvider`）が、受信側のスレッドから `_document.Project` を直接シリアライズしている。UIスレッドの編集と競合し得る。`_document!` がnullや別文書になる可能性もある | `App/Collab/CollabSessionController.cs`（`SnapshotProvider = () => SnapshotSync.CreateSnapshot(...)`）、`Collab/Session/CollabHost.cs`（HandleClientAsync） |
| 高 | 共同編集ホストの受け口: 認証前に最大64MBを確保（`new byte[length]`）。接続数の上限なし。Hello待ちのタイムアウトなし。`IPAddress.Any` で全NIC待受。`HelloMessage.ProtocolVersion` をホスト側で検証していない。`JsonException` / `InvalidDataException` を捕捉しておらず、fire-and-forget のため静かに消える | `Collab/Transport/CollabConnection.cs`（ReceiveAsync）、`Collab/Protocol/CollabMessage.cs`（MaxMessageBytes）、`Collab/Session/CollabHost.cs` |
| 中 | 拍子・BPMの値検証が無い。`TimeSignatureEvent.TicksPerMeasure` は分母0で0除算、分子0でtick幅が0になり `TickToMeasurePosition` が長時間空回りし得る。BPM欠落（tick0なし）や0以下は `TimingEngine` コンストラクタが例外（`OnRender` 内でも生成される）。画面入力は1未満を弾くが、dos.txtの `de_timeSig` / `de_bpm`、プロジェクトJSON、共同編集メッセージには検証が無い。`DosParamParser.ParseNumberList` は `double.Parse`（`TryParse` ではない） | `Core/Timing/TimingEngine.cs`、`Core/Import/DosImporter.cs`（80〜91行付近）、`Core/Import/DosParamParser.cs`、`Core/Persistence/ProjectSerializer.cs`（Deserialize） |
| 中 | UIスレッドの未処理例外は常にアプリ終了（緊急保存の選択肢付き）。`OnRender` 内の例外（過去に `Rect` の高さが負になるクラッシュの実例あり）でも終了する。描画段階ごとに保護して1フレーム飛ばす設計が望ましいか | `App/App.xaml.cs`（OnDispatcherUnhandledException）、`App/ChartCanvas.cs`（OnRender） |
| 中 | BGMを全デコードして常駐（`float[]`）。デコード中は `List<float>` と `ToArray` で一時的に約2倍。5分・44.1kHz・ステレオで常駐約106MB、ピーク約210MB。波形用に同じファイルをもう一度デコードし、monoの `List<float>` は事前確保なし | `App/NAudioBgmPlayer.cs`（DecodeWholeFile）、`App/WaveformDecoder.cs` |
| 中 | `TintCache`（画像×色）が静的で上限なし。ncolor_dataで色が多彩だと増え続ける | `App/ChartCanvas.cs`（TintCache、GetTintedNoteImage） |
| 低 | 自動保存の失敗を無言で握りつぶす。ディスク満杯などで永続的に失敗しても利用者が気づけない | `App/MainWindow.xaml.cs`（AutoSaveTimer_Tick） |
| 低 | クラッシュ検知がPIDのみ。PID再利用で「生存」と誤判定し得る。`Process.GetProcessById` の戻り値を破棄していない。`ClearCrashFlag` の `File.Delete` が終了時に例外を投げ得る | `Core/Persistence/AutoSaveManager.cs` |
| 低 | プラグインは非collectibleなAssemblyLoadContextで、フル権限、信頼確認なし。`RenderOverlay` は例外隔離済みだが、他のライフサイクル呼び出しの保護は未確認 | `App/Plugins/PluginLoader.cs`、`App/Plugins/PluginManager.cs` |

### 3.2 快適性

伸びしろがあると見ているもの（効果が大きい順）:

1. **`TimingEngine.TickToFrame` / `FrameToTick`**: 呼ぶたびに `ComputeSegmentStartFrames()` で配列を再生成し、線形探索している。インスタンスは不変なので、構築時に一度だけ計算して二分探索にできる。呼び出し頻度が高い（波形描画は1px行ごとに `FrameAtTick` ×2。1000pxで1フレーム約2000呼び出し。再生中は毎フレーム）。
2. **`CreateTimingEngine()` の多用**: 約40箇所から呼ばれ、毎フレームの `OnRender` と `PlaybackTimer_Tick`、マウス移動ごとの `SnappedTickAt`（スナップOFF時）などで都度生成される（`OrderBy` + `ToList` を2回）。BPM・拍子・StartNumber変更時のみ無効化するキャッシュが有効かもしれないが、`BpmEvents` が公開のmutableな `List` なので無効化漏れが怖い。内容署名比較などの安全策が必要か。
3. **小節走査**: `TickToMeasurePosition` / `SignatureAt` が小節数に比例。`DrawGridAndMeasureLines` の小節線ループが先頭から全小節を走査し毎回 `SignatureAt` を呼ぶため、実質O(M²)（500小節で約12万ステップ/描画）。拍子は区間ごとに等幅なので除算でO(拍子イベント数)にできる。時間情報レーン側の走査も同様の可能性（未確認）。
4. **描画ごとの割り当て**: グリッド線ごとの `new Pen`（未Freeze）、フリーズ帯ごとの `SolidColorBrush`、`DrawCursorLine` が毎回Brush・Pen・DashStyleを生成。`DrawNotesAndFreezes` がレーンごと・描画ごとに `ColorOverrides.ToDictionary` と `Annotations.Where(...).ToHashSet()` ×2を実行（表示範囲に関係なくレーン全件）。
5. **再描画の粒度**: `OnMouseMove` ごとに `InvalidateVisual()`。波形、グリッド、全ノートを含む全レイヤーが再描画される。カーソルラインや再生ラインなど頻繁に動く軽量レイヤーを別 `DrawingVisual` 等へ分離する案。効果は最大だが工数も大きい。2026-09-29の「目視テストがガクガクする」報告との関連も疑っている（音声側は `SmoothedPosition` で対処済み）。
6. **`DrawSelectionHighlights`**: フリーズ選択ごとに `Freezes.FirstOrDefault` を呼ぶ（O(選択数×レーン内フリーズ数)）。全選択＋大量フリーズ時のみ問題になり得る。

限界に近い（これ以上は効果が薄い）と見ているもの:
- ノート・グリッドの描画はビューポート内にカリング済み。Brush、画像、Penの静的キャッシュも概ね済み。`TickToY` / `YToTick` はO(1)。
- Undoはコマンド方式で軽量。
- `IttnAnalyzer` 系は二分探索とスライディングウィンドウで概ねO(N log N)。手動実行のみでホットパスではない。
- `HitTest` は線形だが対象は1レーン分のみ。

### 3.3 良い点（維持してほしい設計）
- 未処理例外時の緊急保存、PIDを使ったクラッシュ検知、複数ウィンドウ対応
- `CompositionTarget.Rendering` の購読と解除が対になっている
- プラグイン描画の例外隔離
- 「本体（danoniplus エンジン）が正」の原則と、実データ検証重視の開発姿勢

## 4. 回答してほしい形式

1. **指摘一覧**: 重要度（高/中/低）、該当ファイルと箇所、根拠、修正方針（1〜2行）。
2. **事前所見（第3章）への判定**: 各項目について「妥当 / 誤り / 要実測」を明記。誤りの場合は理由も。
3. **事前所見に無い新規の指摘**。
4. **推測と確認済みの事実を区別して書くこと**。実測していない性能の数値は「推定」と明記。
5. **機能提案**（任意）: 効果と実装コストの目安付き。
6. パッチの全文は不要（方針と該当箇所のみで可）。ただし、修正に難所がある場合は要点を示してほしい。

## 5. 提出ファイル一覧（`src/` 基準）

### 5.1 最優先（小さく、核心。合計約170KB）

| ファイル | サイズ目安 | 見てほしい点 |
|---|---|---|
| `DanoniEditor.Core/Timing/TimingEngine.cs` | 13KB | 再計算、線形探索、不正値 |
| `DanoniEditor.Core/Persistence/ProjectSerializer.cs` | 19KB | 保存の原子性、読み込み検証 |
| `DanoniEditor.Core/Persistence/AutoSaveManager.cs` | 10KB | 競合、PID判定 |
| `DanoniEditor.App/App.xaml.cs` | 9KB | 起動、例外処理、終了処理 |
| `DanoniEditor.App/Program.cs` | 6KB | エントリポイント、ネイティブDLL検索パス |
| `DanoniEditor.Editing/ChartLayout.cs` | 18KB | 座標変換、HitTest |
| `DanoniEditor.Editing/EditorDocument.cs` | 18KB | イベント、Undoスタック管理 |
| `DanoniEditor.Editing/UndoStack.cs` | 2KB | Undo |
| `DanoniEditor.App/NAudioBgmPlayer.cs` | 20KB | メモリ、スレッド、ロック |
| `DanoniEditor.App/WaveformDecoder.cs` | 1.4KB | 二重デコード |
| `DanoniEditor.Core/Audio/WaveformPeaks.cs` | 3.5KB | ピーク生成 |
| `DanoniEditor.Collab/Session/CollabHost.cs` | 9.4KB | 受け口、例外 |
| `DanoniEditor.Collab/Session/CollabGuestClient.cs` | 5.4KB | クライアント側 |
| `DanoniEditor.Collab/Transport/CollabConnection.cs` | 3.7KB | 長さ検証、確保 |
| `DanoniEditor.Collab/Protocol/CollabMessage.cs` | 5.5KB | 上限値、バージョン |
| `DanoniEditor.App/Collab/CollabSessionController.cs` | 20KB | スレッド境界 |
| `DanoniEditor.App/Plugins/PluginLoader.cs` | 7.7KB | 読み込みと隔離 |

### 5.2 次点（大きいが重要）

| ファイル | サイズ目安 | 見てほしい点 |
|---|---|---|
| `DanoniEditor.App/ChartCanvas.cs` | 152KB | `OnRender` 全体、各 `Draw*`、キャッシュ、マウス処理 |
| `DanoniEditor.Core/Models/ChartProject.cs` | 36KB | モデル、CreateTimingEngine、公開mutable |
| `DanoniEditor.Core/Import/DosImporter.cs` | 43KB | 数値入力の検証 |
| `DanoniEditor.Editing/SmartToolController.cs` | 86KB | ドラッグ中の処理、一括操作 |
| `DanoniEditor.Editing/EditActions.cs` | 79KB | 一括操作の計算量 |

### 5.3 余力があれば
`DanoniEditor.App/PlaytestWindow.cs`（66KB）、`PlayPreviewSurface.cs`（15KB）、`DanoniEditor.Core/Analysis/IttnAnalyzer*.cs`（計算量は良好のため優先度低）、`DanoniEditor.Core/Import/FujiImporter.cs`（38KB）。

### 5.4 `MainWindow.xaml.cs`（393KB）は行範囲の抜粋で渡す
全体は大きすぎるため、次の範囲のみ抜粋する（行番号は2026-10-05時点）。

| 範囲 | 内容 |
|---|---|
| 1810〜1830付近 | `Document.Changed` の購読と解除 |
| 2420〜2560 | 自動保存タイマー、緊急保存、クラッシュ復旧 |
| 2557〜2565 | `CompositionTarget.Rendering` の購読解除 |
| 2566〜2700 | `PlaybackTimer_Tick`（目視テストの毎フレーム処理） |
| 4931〜4945 | `InvalidateChartViews` |
| 6500〜6510付近 | 目視テスト開始時の購読 |

### 5.5 文脈資料（任意）
`README.md`、`docs/handoff_2026-08-06.md`、`docs/progress_and_tbd_2026-08-08.md`。共同編集の背景は `docs/realtime_collab_edit_design_2026-09-20.md`（80KBあるため、必要な章のみで可）。

### 5.6 渡さないもの
- `bin/`、`obj/`
- `template/`、`tools/reference/`（JSONデータ）
- `tests/`（構成は第1章に記載済み。必要なら個別に依頼）
- `sounds/`、`_to_delete/`、`publishに同梱するファイル/`
- 設定系の大きなUI: `PreferencesWindow.cs`（120KB）、`TemplateEditorWindow.cs`、`GaugeEditorWindow.cs`
- 過去日付のdocs

## 6. 注意事項（レビュー時の前提）

- 本資料の所見はすべて机上のコード読みによる。ビルド、実行、プロファイリングは行っていない。
- 開発者の方針として、「本体（danoniplus エンジン）が正」であり、仕様の曖昧点は実データで確認する。
- 開発者は、仕様変更を伴う指摘については、根拠（エンジンソース、実データ）の提示を重視する。
