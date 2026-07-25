# 進捗・残りTBDまとめ(2026-07-25時点)

「アナライザー・おにスター・クラッシュ復旧」実装スレッドの途中経過。今回のセッションで完了した
実装内容と、現時点で残っているTBDを整理する。

## 1. 今回のセッションで完了した実装

### 1-1. 自動保存・クラッシュ復旧

- `AutoSaveManager`(`DanoniEditor.Core/Persistence/`): クラッシュフラグ・manifest・スロット単位の
  自動保存ファイルを管理。実プロジェクトファイルは一切上書きしない別フォルダ方式(B案)
- 環境設定 > 編集・保存に「自動保存」チェックボックス(デフォルトOFF)と保存間隔(分)入力欄を追加
- `App.xaml.cs`: 起動時に前回のクラッシュフラグを確認 → 復旧候補があればメイン画面表示後に
  1件ずつ「前回の続きから復元しますか?」ダイアログを表示。`OnExit`で正常終了時のみフラグを消す
- 手動保存・タブを閉じる操作でそのセッションの自動保存スロットをクリア
- Core層ユニットテスト9件、ビルド0W/0E・テスト全件成功を確認済み

### 1-2. ITTNアナライザー 忠実移植パート

- `analyzer_and_viewer/analyze.js`(1830行)を`DanoniEditor.Core/Analysis/`配下へ6ファイル構成で
  移植(`IttnAnalyzerConfig`/`IttnAnalyzerKeyMap`/`IttnAnalyzerTimeline`/`IttnAnalyzerCommon`/
  `IttnAnalyzer`/`IttnAnalyzerJack`/`IttnAnalyzerAlt`/`IttnAnalyzerMov`)
- dos.txtの再パースではなく、エディタ内部モデル(`DifficultyTab`/`TimingEngine`)から直接
  タイムラインを構築する方式を採用
- JSブリッジ(`danoni_chart_collector/collector-app/AnalyzeEngine.cs`、Jint経由)との数値照合を
  5鍵/7鍵/11鍵の3パターン×12指標=36件実施し、全て完全一致(誤差0.0000)を確認
- STREAM/VOLTAGE/CHORD/SOFLAN/FREEZE/ONIGIRIの6軸、JACK/ALT/MOVの特殊要素、
  baseRating/totalRating/toolScaleRatingまで実装範囲

### 1-3. ITTNアナライザー BPM強化パート

- 以下4箇所のフレーム固定スライディング窓を、tick/BPM基準(拍・小節基準)へ置き換え
  (`[仮]`値、後日実測データで校正予定)

  | 対象 | 旧(JS版・フレーム固定) | 新(BPM基準) |
  |------|----------------------|-------------|
  | VOLTAGE | 240F(4秒) | 2小節 |
  | ALT `PEAK_WINDOW` | 600F(10秒) | 8小節 |
  | MOV `PEAK_WINDOW` | 600F(10秒) | 8小節 |
  | MOV `SIMUL_WINDOW` | 15F(0.25秒) | 2拍 |
  | SOFLAN `TIME_WINDOW_B` | 120F(2秒) | 1小節 |

- DISCARD(捨て予算解析)は今回も対象外(移植・強化とも見送り)
- BPM感度テストを追加(同一ノーツパターンでBPMを変えると窓が音楽的基準で追従することを確認。
  例: VOLTAGEはBPM倍増でPeakApmもきれいに2倍)
- ビルド0W/0E、テスト425件全て成功(既存421+新規4件)

## 2. 残っているTBD(このスレッド関連)

### 2-1. おにスター(統合回帰+幅表示)実装 — 未着手

難易度表(dodl4)の☆/★スケールと多鍵データベース(ta)のLv1〜10スケールを、`toolScaleRating`を
橋渡しに1本の「おにスター」表記へ統合する回帰式(`score_equivalent ≈ 2.704×Lv − 1.737`)は
既に算出済みだが、C#側での実装(統合回帰テーブル・60%信頼区間の幅表示ロジック)にはまだ着手していない。

### 2-2. 隠し機能の解禁条件・右パネルUI配線 — 未着手

- ITTNアナライザー: 配置オブジェクト10,000個で解禁
- おにスター: 「算出・再算出」ボタン10回押下で解禁
- 右パネルに「分析」タブを新設し、解禁段階に応じて表示内容を追加していく配線

上記3点(1-1〜1-3)はロジック層の実装が完了した段階で、UIへの配線はまだ行っていない。

### 2-3. (新規発見)difData内ゲージ設定(border/recovery/damage/initLife%)の専用入力欄が無い

`DifficultyTab.DifDataExtra`(dos.txtの各難易度行4列目以降に直接埋め込まれる
`border,recovery,damage,initLife%`形式の生値)は、インポート時に文字列としてそのまま保持し
エクスポート時にも書き戻しているが、**この値自体をエディタのUIから編集する入力欄が存在しない**。

現状の`GaugeEditorWindow`は、
- ①難易度タブごとのゲージ名リスト(継承キーワード/明示リスト)
- ②名前付きゲージ(customGauge/gaugeXXX)のパラメータテーブル
- ③dos.txtのゲージ関連ヘッダー行を直接貼り付けるRawモード

の3系統をカバーしているが、いずれも「名前付きゲージ設定」を対象にしたもので、
各難易度行に直接インラインで書かれるborder/recovery/damage/initLife%(名前を介さない生の4値)を
個別に編集する手段が無い。現状は既存のdos.txtをインポートした場合のみ値が保持される
(新規タブでは空のまま)。専用入力欄(タブごとの4項目欄など)の追加を今後のTBDとして記録する。

## 3. 参考ドキュメント

- `docs/ittn_analyzer_integration_handoff.md` — ITTNアナライザー統合の背景・用語整理
- `docs/progress_and_tbd_2026-07-24_add.md` — 忠実移植/BPM強化の2段階方針、おにスター回帰の方針確定
- `docs/progress_and_tbd_2026-07-22.md` — β版判断材料としての全体進捗・TBDまとめ(ゲージ設定
  `GaugeEditorWindow`の実装経緯を含む、本ドキュメント2-3の前提となる現状把握元)
- `docs/remaining_tbd_2026-07-22.md` — その他ヘッダーUI未整備項目など、本スレッド外のTBD一覧
