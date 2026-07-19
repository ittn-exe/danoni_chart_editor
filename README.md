# ダンおに譜面エディタ (danoni_editor_spec v0.43 準拠)

## 構成
- `src/DanoniEditor.Core` — プラットフォーム非依存のコアライブラリ
  - `Timing/TimingEngine` — 拍(tick)⇔フレーム変換(1拍=48tick、ソフラン・変拍子対応。仕様書7.2/7.3/7.5)
  - `Models/KeyTemplate` — テンプレート(4.2/4.2.1)+ ステップゾーンX座標計算(本体式と一致)
  - `Models/ChartProject` — プロジェクト/難易度タブ/ノート(5章、tick管理)
  - `Naming/FrzNameResolver` — frz名導出(本体 g_escapeStr.frzName 完全再現)
  - `Export/DosExporter` — dos.txt汎用組み立てエンジン(3.1、サフィックス採番対応)
- `src/DanoniEditor.App` — WPF UI(骨組みのみ。Windows上で実行)
- `tests/` — xunit 30件(仕様書15.3/15.4の検証済み実数値を使用)
- `template/` — 標準24キー種テンプレート(**折衷版**: ユーザー製キュレーション + 本体g_keyObj値で補正。詳細は下記)
- `tools/gen_templates.mjs` — テンプレート再生成スクリプト(要: 本体ソース)

## ビルド
    dotnet test                      # コアのテスト(Linux/Windows両対応)
    dotnet build src/DanoniEditor.App  # WPF(コンパイルはLinux可、実行はWindows)
    dotnet publish src/DanoniEditor.App -r win-x64 -p:PublishSingleFile=true  # 単独EXE

## 本体ソース裏取りで確定した事項(仕様書のTBD解消)
1. **frz名の正式ルール**: g_escapeStr.frzName の置換テーブル
   (leftdia→frzLdia, rightdia→frzRdia, gor→frzGor, oni→foni 等、
    非該当は frz+Capitalize)。sleft→sfrzLeft のように接頭辞は保存される。
2. **pos/div未指定時のデフォルト補完のコード箇所を特定**(仕様書16章TBD):
   danoni_main.js `setKeyDfVal`(L5639) — pos未指定→0からの連番、
   div未指定→max(pos)+1、divMax未指定→max(pos)+1。
3. **15Bキー等のコピー派生**: g_copyKeyPtn(15B_0→15A_0)で解決。keyCtrlのみ個別定義。

## テンプレートの出自(折衷版)
ユーザー製の人力キュレーション版をベースに、danoniplus本体ソースの値で補正したもの。
- ユーザー版採用: displayOrder(編集画面での並び)、keyAssign(表示ラベル、複数キーは配列)、
  scrollDirection("up"/"down"文字列)、明示的なfrzDataNameOverride
- 本体値で補正: 11jの矢印6レーン・15Aのtレーン3本のrotationAngle(0のまま欠落していたのを補完)、
  13keyのsレーンcolorGroup(1→3、本体color13_0_0準拠)
- 新規生成(ユーザー版の流儀を規則化して適用): 15B / 16i / 17 / 23
  - scrollDirection規則: 折返しあり(pos>divideCntのレーンが存在)なら pos≤divideCnt→"up"、それ以外→"down"
  - displayOrder規則: 矢印キー割当レーンが半数未満のとき末尾へ移動(11/13/15A等のユーザー版と同じ並びになることを確認済み)
- 12i/9Bのsright回転角はユーザー版の-135を維持(本体の225と同値のため)
- 旧・本体ソース直接生成版は tools/reference/template-engine-raw に退避

## フェーズ2(2026-07-15)
- `Persistence/ProjectSerializer` — プロジェクトJSON保存/読込(schemaVersion=1、往復安定性テスト済み)
- `Persistence/ProjectOperations` — MoveTab(6.4.2共通色ルール)、ApplyImport(15.3.1タブ追加)
- `Import/DosParamParser` — |k=v|抽出(JSラッパー・複数行値対応)
- `Import/FujiImporter` — 実データ検証で確定した形式解釈:
  - $frameのB(blank)がヘッダーblankFrameより優先 / mlen=(C/10−blank)/(E−Σskip/16)
  - フリーズは {X}{8}{LL}-{QQQQ}(X=1/16小節スロット、QQQQ≥0x100は−0x60補正)
  - speed={X}400-{VVVV}, boost={X}410-{VVVV}(値=VVVV/1000)
  - barcut→拍子オブジェクト (16−skip)/16(スペックの「直前小節と合体」ではなく無損失な単純表現を採用)
- `Import/SkbImporter` — ★scores[]は難易度ごとではなくページごとの配列(仕様書15.4の記述を訂正)
  - globalTick=ページindex×pageBlockNum×48+tick / フリーズはページ横断平坦化→順次ペアリング
  - timingsのstartNum不連続(再同期ジャンプ)はモデル上表現不能→警告
- 検証: by_node(7key S-EXPERT、ノート717+フリーズ)全数1F以内一致 / a.txt(barcut)スペック検証値と完全一致 /
  skb_test 全数1F以内一致。インポート→dos.txt再出力の往復も一致。

## 小物追加(2026-07-15 その2)
- `Import/DosImporter` — dos.txt単体インポート(仕様書15.2)
  - タイミング復元の優先順: 明示指定 > de_*(自前埋め込み) > es_*(SKB埋め込み) > デフォルトBPM仮定+警告
  - キー種はdifData行(先頭フィールド)から。difData欠落時はes_keyKindへフォールバック
  - tickスナップは「整数フレーム丸め(±0.5F)と矛盾しない最も粗い音楽グリッド(4分→64分)」規約。
    整数フレームからは隣接細グリッドと原理的に区別不能なケースがあるため決定的規約とし、誤差は常に0.5F以内。
    完全なtick保存はプロジェクトファイル側の責務
- `DosExporter.Export(project, includeEditorMetadata: true)` — de_*パラメータ(startNumber/BPM列/拍子列)を
  dos.txtへ埋め込み。本体は未知パラメータを無視するため再生無影響で、dos.txt単体往復が可能になる
- difDataの4フィールド目以降(ゲージ設定等)を`DifficultyTab.DifDataExtra`として保持・再出力
- frzColorの区切りを`$`→`,`に修正(本家dos仕様例準拠)
- 検証: skb_test_dos(es_*)→SkbImporter経由とtick完全一致 / by_node(BPM明示)→FujiImporter経由とtick完全一致 /
  自前プロジェクトのde_*付きdos往復で完全一致

## BPM自動推定(2026-07-16、雑談発)
- `Import/DosTimingEstimator` — 「四分間隔」文化に基づくBPM復元。ノートのフレーム列に対し
  BPM候補(100〜200、0.1刻み)×位相を走査し、16分グリッド±0.55Fへのインライア率で採点。
  上位候補を最小二乗でリファインし(累積ドリフト除去)、キリの良いBPM(0.05刻み)へスナップ。
- 実データ検証: by_node(530ノート)→BPM185.0を一致率100%で検出(次点34.9%と大差)、
  skb_test(539点・位相未知の2次元探索)→BPM176.0を一致率100%で検出。
- `DosImportOptions.AutoEstimateTiming=true` でDosImporterの復元チェーンに組み込み:
  明示指定 > de_* > es_* > 自動推定(一致率0.95以上で採用) > デフォルトBPM仮定。
- 制約: 単一BPM前提(ソフランは区間分割推定が将来課題)。「どこが1拍目か」は原理的に
  決定不能のため、位相は[blank, blank+四分間隔)へ正規化した格子等価代表値+要確認警告。
