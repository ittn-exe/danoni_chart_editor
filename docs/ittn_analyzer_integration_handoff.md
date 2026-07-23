# 「難易度分析(ITTNアナライザー式)＋推定star値」エディタ統合 引き継ぎ資料

作成経緯: `analyzer_and_viewer`プロジェクト(analyze.js、通称ITTNアナライザー)を難易度表データで
大規模検証した一連の作業(2026-07-23〜)の成果を、エディタ(danoni_editor)側の新機能として
実装するにあたっての設計方針・現状の資産棚卸し・未解決事項をまとめたもの。
**このドキュメント自体はコード実装を含まない(引き継ぎ・設計メモ)。**

## 1. 用語定義

- **star値**: 難易度表(dodl4.g3.xrea.com等)が付与する表記難易度(☆1〜12=通常難易度、
  ★1〜n=発狂難易度・青天井)をまとめて指す、本ドキュメント内での呼称。
- **統一スケール変換**: star値を1本の数直線として扱うための変換式。
  `tableLevel`(収集データ上の生の値、符号: 負=☆・正=★)から
  `score = tableLevel < 0 ? |tableLevel| : 12 + tableLevel` で算出する
  (☆12の次が★1=score13、以降★は青天井)。逆変換は
  `score <= 12 → "☆" + score`、`score > 12 → "★" + (score - 12)`。
  - 例外: 9A/9BキーはSection 5参照(★が存在しない特殊ケース)。

## 2. 目的

エディタで編集中の譜面(1難易度タブ)に対して、以下2つの値をボタン等から算出・表示できるようにする。

1. **難易度分析(ITTNアナライザー式)**: `analyze.js`が出す各種指標
   (STREAM/VOLTAGE/CHORD/SOFLAN/FREEZE/ONIGIRIのレーダー6軸、JACK/ALT/MOVの特殊要素値、
   baseRating/totalRating/toolScaleRating)をそのままエディタ上に表示する。
2. **推定star値**: 1のtotalRatingから、実測データに基づく回帰式で
   「だいたい☆いくつ/★いくつ相当か」を逆算して表示する(あくまで目安、Section 6参照)。

## 3. 現状の資産棚卸し

### 3.1 エディタ側(danoni_editor)に既にあるもの

- `DanoniEditor.Core/Analysis/DifficultyLevelCalculator.cs`: danoniplus本体の「ツール値」
  (`calcLevel`)をC#へ手動移植した既存の前例。**入力は「レーンごとのフレーム値配列」**
  (tick単位ではない)。ITTNアナライザーの実装でも同じ入力形状に合わせるのが自然。
- `DanoniEditor.Core/Analysis/GaugeCalculator.cs`: 同じくAnalysisフォルダにある既存の
  計算専用staticクラス。新機能もここに追加するのが構成上自然
  (例: `DanoniEditor.Core/Analysis/IttnAnalyzer.cs`)。
- `DanoniEditor.Core/Export/DosExporter.cs`: `Export(ChartProject project, bool includeEditorMetadata)`
  で、プロジェクト全体(全難易度タブ込み)を本家dos.txt形式のテキストとして出力できる
  (既存・動作確認済み)。ITTNアナライザーへの入力として**そのまま使える**。
- `DanoniEditor.Core/Models/ChartProject.cs`: `DifficultyTab.Lanes`(`LaneNotes.Notes`=tick位置の
  通常ノート、`LaneNotes.Freezes`=フリーズ始点終点tickペア)、`ChartProject.CreateTimingEngine()`で
  tick→frame変換が可能。

### 3.2 `analyzer_and_viewer`側(参照元ロジック)にあるもの

- `analyze.js`(`C:\Docmenttn\danoni\analyzer_and_viewer\analyze.js`、約1831行): 本体ロジック。
  主要関数: `ScoreToTimeline`(fullData→タイムライン化)、`getGaugeSettings`、
  `finalizeRadarValue`、`calcStream`/`calcVoltage`/`calcChord`/`calcSoflan`/`calcFreeze`/
  `calcOnigiri`/`calcJack`(レーダー6軸)、`calculateAltLevel`(約280行)、
  `calculateMovLevel`(約520行、**全関数中最大**)、`calcBaseRating`、`satBonus`、
  `calcTotalRating`、`calculateFinal`(最終エントリポイント)。
- `render.js`の`doAnalysis(content, fileName)`: 生dosテキスト→fullData構築→全難易度タブ解析、
  の標準的な呼び出しフロー。**パース時は`|`と`&`の両方をデリミタとして扱う必要がある**
  (実データに`&name=value&`形式が実在することが判明済み、`|`のみ対応のrender.js/
  DosParamParser.cs双方に共通する既知の穴)。
- `collector-app/AnalyzeEngine.cs`(`C:\Docmenttn\danoni\analyzer_and_viewer\collector-app\`):
  **analyze.jsを改造せずJint(NuGetパッケージ、純C#製JavaScriptエンジン)上でそのまま実行する**
  C#ブリッジの実装例。`AnalyzeRawDos(rawText, maxL, fallbackTitle)`という形でC#から
  呼び出せる状態まで作り込み済み。Node.js版と数値が完全一致することを実データで検証済み
  (riseup Total=23.04/51.11、tokei Total=68.28等)。
- `log_viewer.html`(`C:\Docmenttn\danoni\analyzer_and_viewer\log_viewer.html`)+
  `analysis_log.csv`(同フォルダ、難易度表収集データ約1700件): 実データでの検証結果を
  ブラウザだけで確認できるビューア。「キー種別ランク相関」パネルでkeyTypeごとの
  Spearman順位相関係数(ρ)を表示できる。

## 4. アーキテクチャ方針: 2つの選択肢

### 選択肢A: analyze.jsをC#へ手動移植する

`DifficultyLevelCalculator.cs`/`GaugeCalculator.cs`と同じ流儀(JS側ソースを読んで
1関数ずつC#に書き写し、コメントで移植元を明記)。

- 長所: 既存コードとの統一感、外部依存(Jint)が増えない。
- 短所: `calculateMovLevel`だけで約520行あり、`analyze.js`全体では1800行超。
  手動移植は工数・バグ混入リスクが大きい。CONFIG係数を調整するたびに
  JS版とC#版を二重メンテする必要が生じる(過去にNode.js版collectorで
  「C#へロジックを二重実装すると係数を変えるたびに両方直す羽目になる」という
  判断からJint方式を採用した経緯があり、同じ懸念が当てはまる)。

### 選択肢B: DosExporter + Jint(collector-appの方式を移植)

エディタ内で`DosExporter.Export(project, includeEditorMetadata: false)`を呼んで
dos.txtテキストを生成→`collector-app/AnalyzeEngine.cs`とほぼ同じJintブリッジに渡す→
対象タブのインデックス(scoreId相当)を指定して結果を取得。

- 長所: **analyze.js本体を一切改造せず実行**するため、collector-app側で既に
  Node.js版との数値一致を検証済みのロジックをそのまま再利用できる。CONFIG係数を
  更新した際もanalyze.jsを差し替えるだけで済む(移植し直し不要)。DosExporterも
  既存・動作確認済みの資産をそのまま使える。
- 短所: Jint(NuGet)への依存が`DanoniEditor.Core`(または新規プロジェクト)に増える。
  `System.Text.Encoding.CodePages`も同様(Shift-JIS対応、ただしエディタ内で完結する
  分には元々UTF-8前提のはずなので実際に必要かは要確認)。エクスポート→パースの
  往復コストが発生する(ただし1譜面分の解析なので体感上は問題にならない見込み)。

**推奨: 選択肢B。** 理由は上記の通り、ロジックの二重実装リスクを避けられることと、
既にcollector-appで実装・検証済みの`AnalyzeEngine.cs`をほぼそのまま移植できるため、
実装コストも実質的に選択肢Aより低いと考えられるため。

## 5. 実装ステップ(選択肢B採用時の想定)

1. `DanoniEditor.Core`(または新規`DanoniEditor.Core.Analysis`的な別プロジェクト)に
   `Jint`のNuGet参照を追加。
2. `collector-app/AnalyzeEngine.cs`を参考に、`analyzer/analyze.js`
   (エディタ配布物にも同梱する形にするか、`analyzer_and_viewer`側のファイルを
   ビルド時にコピーする形にするかは要検討)を読み込むブリッジを実装。
   `AnalyzeRawDos(rawText, maxL, fallbackTitle)`相当のAPIをそのまま踏襲できるはず。
3. `IttnAnalyzer`(仮称、`DanoniEditor.Core/Analysis/`配下)を新設し、
   `ChartProject`+対象タブIndexを受け取り、内部で
   `DosExporter.Export(project, false)`→Jintブリッジ呼び出し→結果を
   エディタ向けの結果レコード(`record IttnAnalysisResult(double Stream, double Voltage, ...
   double BaseRating, double TotalRating, double ToolScaleRating, ...)`)に詰め替えて返す
   薄いラッパーとして実装。
4. UI側(`MainWindow`または専用ダイアログ)に「難易度分析」ボタンを追加し、
   現在選択中の難易度タブに対して3を実行→結果表示。
5. 4の結果(TotalRating)を使い、Section 6の回帰式で推定star値を算出・併記。

## 6. 推定star値のキャリブレーション情報(2026-07-23クロール、n≈1700件時点)

`log_viewer.html`でのランク相関検証時に、keyTypeごとに
`TotalRating ≈ a × score + b`(scoreはSection 1の統一スケール)の線形回帰を実施した結果。
**逆算する場合は `score推定 = (TotalRating - b) / a`**、その後Section 1の逆変換でstar値ラベル化する。

| keyType | n   | a (傾き) | b (切片) | R²   | 備考 |
|---------|-----|---------|---------|------|------|
| 7i      | 144 | 6.84    | 2.68    | 0.93 | 最も当てはまり良い |
| 7       | 293 | 8.31    | -3.37   | 0.86 | |
| 12      | 208 | 6.72    | -10.28  | 0.83 | |
| 9A      | 91  | 8.65    | -4.65   | 0.81 | 発狂表なし(Section 1例外、後述) |
| 9B      | 78  | 10.93   | -16.77  | 0.81 | 同上 |
| 11L     | 171 | 6.14    | 2.79    | 0.78 | |
| 11      | 234 | 6.14    | 17.38   | 0.62 | 11key系はBase値が伸び悩みがちという既知の懸念と符合する可能性 |
| 5       | 491 | 6.18    | 23.20   | 0.45 | 最もばらつき大。片手/両手2ラベルが同一譜面に付く仕様上のノイズが主因と推測 |

**R²が低いkeyType(特に5key)は点推定の信頼性が低い**ため、UI表示上は「推定star値: ☆n付近
(誤差大)」のような、幅を持たせた見せ方を推奨する。単一の回帰直線だけでなく、
`log_viewer.html`側で算出した「difScoreごとの実測中央値・レンジ」の参照テーブルを
UI内に埋め込む(またはCSVとして同梱する)方式も検討の余地あり。

## 7. 未解決事項・注意点

- **9A/9Bキーには★(発狂)自体が存在しない特殊仕様**(2026-07-24判明): 譜面数が少なく
  発狂難易度表が独立して作られていないため、1〜12(通常)+12+n(易/普/難/超難の4段階、
  n=1〜4)の合計16段階という単一スケールで運用されている。Section 1の統一スケール変換式は
  この特殊ケースでもそのまま機能する(9A/9Bは常にtableLevelが負の値しか取らないため)が、
  UI上で「★」ラベルを出さない(常に☆表記、13〜16も☆として扱う)等の個別対応が必要になる
  可能性がある。
- **star値は最終的に人間の投票で決まるものであり、絶対的な正解ではない**
  (2026-07-24ユーザー所見)。推定star値はあくまで「アナライザーの値がこのくらいなら、
  過去の実績上はこのあたりのstar値と評価されることが多い」という目安であり、
  エディタ上でも「推定値」であることを明示し、断定的な表現("この譜面は☆10です"等)は
  避けるべき。
- Section 6のキャリブレーション係数はn≈1700件・2026-07-23時点のクロール結果に基づく
  暫定値。今後`collector-app`で追加収集した際は再フィットが必要
  (`log_viewer.html`に読み込ませれば同じ手順で再計算できる)。
- `analyze.js`側のCONFIG係数(ALT/MOV閾値等)が将来調整された場合、Section 6の回帰係数も
  連動して見直しが必要になる(TotalRatingの値自体が変わるため)。
- dos.txtの`|`/`&`混在デリミタ問題(Section 3.2参照)は、エディタが**内部的に生成する**
  dos.txt(DosExporter出力)には影響しない(エディタ自身は常に`|`形式で出力するため)。
  この問題は「外部サイトからスクレイピングしてきた既存譜面」を解析する場合にのみ関係する
  (collector-app/collectorの領分であり、エディタ本体の実装には無関係)。

## 8. 参照ファイル一覧

- `C:\Docmenttn\danoni\analyzer_and_viewer\analyze.js` — ITTNアナライザー本体ロジック
- `C:\Docmenttn\danoni\analyzer_and_viewer\render.js` — `doAnalysis`(呼び出しフロー参考)
- `C:\Docmenttn\danoni\analyzer_and_viewer\collector-app\AnalyzeEngine.cs` — Jintブリッジ実装例(移植元)
- `C:\Docmenttn\danoni\analyzer_and_viewer\collector-app\CollectorApp.csproj` — Jint/CodePagesのNuGet参照例
- `C:\Docmenttn\danoni\analyzer_and_viewer\log_viewer.html` + `analysis_log.csv` — 検証データ・ランク相関ビューア
- `C:\Docmenttn\danoni\danoni_editor\src\DanoniEditor.Core\Analysis\DifficultyLevelCalculator.cs` — 既存の類似実装(移植方針の前例)
- `C:\Docmenttn\danoni\danoni_editor\src\DanoniEditor.Core\Analysis\GaugeCalculator.cs` — 同上
- `C:\Docmenttn\danoni\danoni_editor\src\DanoniEditor.Core\Export\DosExporter.cs` — dos.txt出力(選択肢Bの入力元)
- `C:\Docmenttn\danoni\danoni_editor\src\DanoniEditor.Core\Models\ChartProject.cs` — プロジェクトモデル
