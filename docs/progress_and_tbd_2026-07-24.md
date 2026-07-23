# 進捗・残りTBD・将来実装可能性まとめ(2026-07-24時点)

`docs/progress_and_tbd_2026-07-23.md` + `docs/progress_and_tbd_2026-07-23_add.md`(前回まとめ)以降に
実装した機能を反映した最新版。以降このファイルを最新の参照先とする(前回分は履歴として残置)。

## 0. 本セッションの経緯

前回セッション末の追記(`progress_and_tbd_2026-07-23_add.md`)で提示したTBD一覧のうち、以下の優先順位で
着手した。

1. アイコン設定(旧TBD番号なし、セッション冒頭で対応。詳細は §1-0)
2. 時間情報表示レーン(旧2-1追加分「1-1」)
3. カレント難易度タブ単体エクスポート(旧2-6。方針変更あり、詳細は §1-2)
4. ncolor_dataの範囲/グループ記法(旧2-4)
5. 歌詞表示機能 word_data(旧2-5)
6. 歌詞レーン管理操作のUndo対応(4の実装後に追加要望として発生)

以下、明示的に後回しにした項目:

- 複合ヘッダーの専用UI未整備(旧2-1): 「未実装のままβ公開しても良いくらい」との判断で保留
- FUJIエディタ形式インポートの残り未検証事項(旧2-3): 「誰かからエラー報告出てからで良い」との判断で保留
- UI全体の再考(旧「1-2」): 次のβ版仕上げフェーズの中核テーマとして保留、着手時はゼロから設計方針を相談する方針は変更なし

## 1. 今回実装した機能

### 1-0. アプリケーションアイコンの設定

- ユーザー提供の透過PNG(256px)を元に、16/32/48/64/128/256の各解像度を含む多重解像度`.ico`を作成
  (16pxはタスクバー表示時の潰れを防ぐため、ユーザー提供の手描き調整版`editor_icon_16.png`をそのまま採用)
- 成果物: `src/DanoniEditor.App/Assets/AppIcon.ico`
- `DanoniEditor.App.csproj`に`<ApplicationIcon>`設定を追加(exeアイコン)
- `MainWindow.xaml`の`<Window>`要素に`Icon="Assets/AppIcon.ico"`を追加(ウィンドウ/タスクバーアイコン)
- ビルド後の実機確認により、アイコンが正しく反映されていることをユーザー側で確認済み

### 1-1. 時間情報表示レーン

`progress_and_tbd_2026-07-23_add.md` §1-1で追加要望のあった機能。既存の「マーカーレーンと兼用」案は
ユーザー判断で不採用となり、独立レーンとして実装した。

- `ChartLayout`にレーン種別`ColumnKind.TimeInfo`を追加(マーカーレーンよりさらに左端)
- 表示内容: 通常時は各行に秒数(`0.00s`)・フレーム数(`0f`)・小節番号(`#N`)の3行を表示
- **行が重なる場合の自動省略**: 直前行とのY座標差が必要高さ未満の場合、小節番号(`#N`)のみの
  1行表示に自動的に切り替える(ユーザー確定仕様: 「小節番号くらいなら重なっても良い」)
- マーカーレーンで実装済みの「ダブルクリックで再生開始位置(frame)を指定」機能を時間情報表示レーンにも
  適用(`SmartToolController.DoubleLeft`が`ColumnKind.Marker or ColumnKind.TimeInfo`を受理)
- 既存の測定番号描画(旧`DrawGridAndMeasureLines`内のインライン処理)を時間情報表示レーン側に統合し、
  重複描画を解消

### 1-2. カレント難易度タブ単体エクスポート(ITTNエディタタブファイル形式)

旧TBD 2-6は当初「dos.txt形式での単体タブ出力(合作用)」だったが、検討の結果
**「ITTNエディタ自身のJSON形式でタブ単体をエクスポート/インポートする」方針に変更**した
(同じエディタを使う協力者が、そのタブファイルを自分のプロジェクトへそのままインポートできる想定)。

- `ProjectSerializer`に`TabExportEnvelope`(内部用、`tabExport: true`キーで識別)・
  `TabExportResult(ChartProject Source, DifficultyTab Tab)`・
  `SerializeTabExport`/`DeserializeTabExport`/`SaveTabExport`/`LoadTabExport`を追加
- `ProjectOperations.ApplyImport(ChartProject, TabExportResult)`オーバーロードを追加
  (既存のFUJI/SKBインポートと同じ合流パターン。空プロジェクトへのインポート時はタイミング設定を
  取り込み側が継承し、既存プロジェクトへのインポート時は既存のタイミング設定を維持する)
- `DroppedFileClassifier`に`DroppedFileKind.OwnTabExport`を追加。`tabExport`キーの有無で
  通常のプロジェクトファイル(`OwnProject`)と判別してからD&Dを処理
- メニューに「現在の難易度タブをエクスポート(_T)...」「ITTNエディタのタブファイルをインポート...」を追加

### 1-3. ncolor_dataの範囲/グループ記法

`docs/NColorData_RangeGroupNotation_FutureWork.md`の対応。本家danoniplusのソース
(`danoni_main.js`)を確認し、以下の設計判断を行った。

- **g0〜g9のキーグループ記法**: wiki `dos-h0092-keyGroupOrder`により、「トランスキー以外の場合は一律
  キーグループ0に割り当てる」ことが判明。本エディタが対象とする通常テンプレート(transKey非対応)では
  常にキーグループ0のみが有効なため、`g0`は「全レーン」相当・`g1`〜`g9`は常に空集合として扱う
  (テンプレート側にキーグループ管理フィールドを追加する必要はないと判断、スコープ縮小)
- **対応記法**: 範囲(`0...7`)・スラッシュ複数指定(`1/3/5/7`)・グループ(`g0`/`all`)。
  インポート・エクスポート双方に対応
- `DosExporter`: `nColorEntries`の内部表現をタプル`(Frame, EngineLaneNum, TargetSuffix, ColorCode, AllFlag)`
  に再構成し、`CompressNColorEntries`/`CompressColorNoGroup`で連続レンジ→`...`、全レーン一致→`all`、
  それ以外→`/`区切りへ圧縮するロジックを追加
- `DosImporter`: 単一整数のみだった`numPart`解析を、`g0`〜`g9`・`all`・`...`範囲・`/`スラッシュ・単一数値の
  いずれにも対応する複数レーン展開ロジックに置き換え

### 1-4. 歌詞表示機能(word_data)

`docs/progress_and_tbd_2026-07-23.md` §2-5の対応。実装前に本家ソース(`danoni_main.js`の
`makeWordData`/`getPriorityVal`/`getPriorityHeader`)を確認し、実際には言語(Ja/En)・スクロール種別
(Cross/Split/Flat/Alt)・Reverse・タブ番号・キーパターン別`wordA*`のフルの組み合わせ空間と、
最大8段階の優先順位フォールバックが存在することを確認した上で、ユーザーとスコープを協議し
**`word_data`/`wordRev_data`(タブ番号サフィックスのみ)に限定したMVP**として実装した。

データモデル(`ChartProject.cs`):

```csharp
public List<WordLane> WordLanes { get; set; } = [];   // DifficultyTabに追加

public sealed class WordLane
{
    public string Name { get; set; } = "歌詞";
    public bool IsReverse { get; set; }
    public List<WordEntry> Entries { get; set; } = [];
}
public enum WordEntryKind { Lyrics, Control, Comment }
public sealed record WordEntry(long Tick, int Position, WordEntryKind Kind, string Text, int? FadeFrame = null);
```

設計方針(ユーザー確定): 歌詞レーンは「任意に追加できるリスト」とし、レーンごとに
プロパティ(現状は`IsReverse`のみ)を持たせて出力先データ名を決める。サフィックスは他のデータ型と
同様にタブ番号で管理する(レーン側に個別のサフィックス指定は持たせない)。

- **Core**: `DosExporter.AppendWordData`が同一`IsReverse`のレーンをフレーム順にマージして
  `word{suffix}_data`/`wordRev{suffix}_data`へ出力(歌詞行/`[fadein]`等の制御行/`-`のコメント行に対応)。
  `DosImporter.ImportWordData`が読み込み時に1レーンへ集約する
- **Editing**: `ChartLayout`に可変本数対応の`ColumnKind.Word`を追加(`SyncWordLaneCount`で
  `DifficultyTab.WordLanes.Count`と自動同期)。`PlaceWordEntryAction`/`DeleteWordEntryAction`/
  `EditWordEntryAction`を追加し、既存の`ObjectRef`/`HitTest`/`MoveObjectsAction`/`SmartToolController`の
  汎用機構にそのまま乗せた(可変本数レーンへの一般化パターンとして、今後同種の機能を追加する際の
  テンプレートになる)
- **App**: `WordLaneManagerWindow`(設定メニュー「歌詞レーンの管理」から起動)でレーンの
  追加/改名/Reverse切替/削除が可能。譜面ビューに歌詞レーンを描画(歌詞本文と制御行を色分け)。
  右パネルでPosition/本文/フェードフレーム数を編集可能

**今回のスコープ外(将来のTBDとして§2-8へ記録)**: 多言語(Ja/En)・Cross/Split/Flat/Alt・
別キーモード(`wordA*`)・他データ参照委譲(`|wordX_data=wordY_data|`)。

### 1-5. 歌詞レーン管理操作のUndo対応

1-4の実装直後、「レーン削除もUndo対象にすべきで、削除時に存在していたオブジェクトも全て復元されるべき」
という追加要望があり対応した。

- `AddWordLaneAction`/`RenameWordLaneAction`/`SetWordLaneReverseAction`/`DeleteWordLaneAction`を新設し、
  `WordLaneManagerWindow`の全操作を`EditorDocument.Execute`経由の通常のUndoStackへ載せる形に変更
- `DeleteWordLaneAction`は削除時点のレーン(歌詞エントリを含む全体)をそのまま保持し、Undoで同じindexへ
  丸ごと復元する(=削除操作そのものを取り消す)
- 旧実装にあった「レーン削除時にUndoStack全体をClear()する」という安全策は撤廃
- 後続レーンのindexがずれる関係上、Word系オブジェクトの選択状態(`Selection`)は削除・復元いずれの
  タイミングでもクリアする仕様は維持(データの復元とは独立の問題として割り切り)
- 削除確認ダイアログの文言を「削除すると復元できません」→「削除しますか?(Ctrl+Zで元に戻せます)」に修正

## 2. 残っているTBD

### 2-1. 複合ヘッダーの専用UI未整備(Raw入力のみ)【保留・変更なし】

`ExtraHeaderDefs.cs`のコメントに明記されている暫定対応。現状は生文字列入力(Raw型)のまま:
`dummyId` / `difColor` / `unStockCategory` / `stockForceDel` / `displayChainOFF` / `keyGroupOrder` /
`resultFormat` / `resultValsView` / `preloadImages` / `imgType` / `titleAnimation` / `titleArrowName` /
`skinType`。

(`customGauge`は専用UI化済みのため対象外。`colorDataType`は廃止機能のため意図的にUI対象外)

### 2-2. `$txt`(列上部表示テキスト)のテンプレート対応 【決定済み・対応不要】

`LaneDef`に専用フィールドを追加せず、`keyTypeName`を流用する方針で確定(変更なし)。

### 2-3. FUJIエディタ形式インポートの残り未検証事項【保留・変更なし】

- `W`(24分マイナス方向)単体での実例による検証がまだ無い
- レーン拡張マーカーとフリーズ終点の数字系fine文字(`1`〜`9`,`A`〜`I`)の組み合わせの実例が無く未検証
- FUJIエディタ自体のdos.txt書き出しダイアログの出力打ち切り挙動は未解明(当エディタ側の問題ではない可能性)

### 2-4. ncolor_dataの範囲/グループ記法【完了】

§1-3の通り実装済み。

### 2-5. 歌詞表示機能(word_data)【MVP完了】

§1-4の通り`word_data`/`wordRev_data`のみ実装済み。多言語・スクロール種別等は§2-8へ。

### 2-6. カレント難易度タブのみをdosエクスポート(合作用)【完了・仕様変更】

§1-2の通り、dos.txt形式ではなくITTNエディタ独自のタブファイル形式で実装済み。

### 2-7. その他【変更なし】

仕様書14章「今後追加される項目の反映」は元々オープンエンドな項目だったが、headerDefaults・
colorHistory・macros・undoHistorySizeは全て実装済み。今後新たに項目が増えた場合もこの方針
(AppSettings/専用ファイルへの追加)を継続する想定。

### 2-8. 歌詞表示機能(word_data)の未対応バリエーション【新規】

§1-4のMVP実装時にスコープ外とした項目。需要が出た時点で個別に着手を検討する:

- 言語バリエーション(`wordJa_data`/`wordEn_data`)
- スクロール種別バリエーション(Cross/Split/Flat/Alt、`wordCross_data`等)
- 別キーモード用バリエーション(`wordA*_data`)
- 他データ参照委譲記法(`|wordX_data=wordY_data|`)
- `WordEntryKind.Comment`(`-`行)のGUI上での新規作成手段が無い(インポートしたコメント行は
  右パネルで一度編集すると`[...]`記法の有無からLyrics/Controlへ自動判定されるため、Commentとしては
  維持されない。コメント行を維持したまま編集したい場合の専用UIは未実装)
- `WordLaneManagerWindow`にレーンのドラッグ並び替え機能が無い(削除して追加し直すことでの代替は可能)

### 2-9. UI全体の再考【保留・変更なし】

主要機能がおおむね出揃ってきたため、レイアウト全体を本格的に再検討するタイミングに来ている。
`UNRESOLVED_FEATURES_2026-07-17.md` §2-7「UIデザイン方向性の検討」の方針とあわせて、次のβ版仕上げ
フェーズの中核テーマとして扱う想定。着手時期・具体的な進め方は未定。次回このテーマに触れる際は
ゼロから設計方針を相談すること。

## 3. 将来実装の可能性(TBDとは別枠)

変更なし(`progress_and_tbd_2026-07-23.md` §3を参照)。

1. FUJIエディタへのエクスポート機能: 実装予定なし
2. SKBエディタへのエクスポート機能: 実装予定なし
3. ラベル密集時の省略表示: 必須機能とはしない
4. テンプレートエディタによる(CW Edition本体で使う)"カスタムキーテンプレート作成"機能: 必要性が低いため着手せず

## 4. テスト状況

`DanoniEditor.Core.Tests`は**367件全てパス**(前回326件から、単体タブエクスポート・ncolor_data範囲/
グループ記法・歌詞表示機能・歌詞レーン管理Undo対応の各テストを追加)。App層はビルド成功を都度確認
(WPFのためLinux環境ではUIテスト実行不可、ビルド確認のみ)。ソリューション全体、0エラー・0警告。
