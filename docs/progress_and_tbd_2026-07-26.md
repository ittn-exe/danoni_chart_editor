# 進捗・残りTBD・将来実装候補まとめ(2026-07-26時点)

「プレイテスト設定拡張・キーパターン対応・BGM再生アーキテクチャ刷新」実装スレッドの進捗と、
現時点で残っているTBD・将来実装候補を整理する。

## 1. 今回のスレッドで完了した実装

### 1-1. プレイテスト設定の細部調整

- 環境設定 > プレイテストに「起動時待機時間(ms)」を追加(`AppSettings.PlaytestStartupWaitMs`、
  `PlaytestWindow`起動時に`Task.Delay`で反映)
- BackSpaceキーの役割を「中断キー」から「開始フレームからの再スタート」へ変更
  - 中断キー選択欄からBackSpaceを削除
  - `PlaytestEngine.Reset()`を追加(コンボ・フリーズ状態・判定結果を全クリア)
  - `PlaytestWindow.RestartFromStartFrame()`で再生位置・判定状態を初期化

### 1-2. キーパターン(danoniplus本家の「キーパターン」概念)対応

danoniplus本家では同じキー種でも複数の物理キー配置・色分け・回転・位置パターンを持つ場合がある
(`danoni_constants.js`の`keyCtrl{keyType}_{n}`等)。今回、当エディタのテンプレート形式・UIを拡張し、
プレイテストでの利用を可能にした。

- **スコープ確定(ユーザー決定)**: エディタ内(プレイテストのみ)の認識・切り替えに限定。
  dos.txtへのパターン別ヘッダー書き出しは対象外(3-1「将来実装候補」へ)
- **データ格納方式(ユーザー決定)**: キー種ごとに1ファイル(`temp_{keyTypeId}.json`内の
  `extraPatterns`配列)にまとめる。パターン単位でのファイル分割はしない
- **Core**: `KeyTemplate`に`ExtraPatterns`/`PatternCount`/`WithPattern(int)`を追加。
  `KeyPattern`(Name/Blank/DivideCnt/PosMax/LaneOverrides)・`LanePatternOverride`
  (KeyAssign/ColorGroup/PosIndex/ScrollDirection/NoteGraphic/RotationAngle)を新設
- **App(編集UI)**: `TemplateEditorWindow`にパターン切替コンボ・追加/削除ボタンを実装
  (ユーザー要望「データ構造が固まったら編集UIも一緒に」に対応、テンプレ自作者も追加パターンを
  GUIで作成可能)
- **App(設定UI)**: 環境設定 > プレイテストに、追加パターンを持つキー種のみ動的に選択欄を表示
  (`AppSettings.PlaytestPatternByKeyType`)。**この選択欄はハードコードではなくテンプレート
  (`KeyTemplate.PatternCount`/`ExtraPatterns`)を読んで動的生成しており、新しいキー種に
  `extraPatterns`を追加するだけでコード変更なしに選択欄が現れる**(今後もテンプレ絡みは
  この「テンプレート駆動」方針を徹底する)
- **App(適用)**: `PlaytestWindow`が選択中パターンを`WithPattern()`で適用してから起動
- **実データ投入**: `danoni_constants.js`を参照し、5/7/8/9A/9i/11/12/12i/13/14/17の
  11キー種分の`extraPatterns`を投入・回帰テストで検証済み

### 1-3. 副次的に発覚・修正したバグ(本スレッド外・既存不具合)

- `KeyLabelMapper`に`Shift`・`Tab`・`F1`〜`F12`のラベル対応が無く、これらをキー割当に使う
  テンプレート(既存出荷の`temp_11j.json`、新規投入した`temp_12i.json`等)がプレイテストで
  一切操作不能になっていた。特にF1〜F12は**本セッション以前から`temp_12i.json`のパターン0
  (出荷済み)が丸ごと操作不能だった**もので、ユーザーからの「12ikeyはファンクションキーで
  遊べる状態か」という質問がきっかけで発覚・修正した

### 1-4. BGM再生アーキテクチャの全面刷新(ハンドクラップ遅延対策)

ユーザーから「テスト中のクラップがまだラグる」との指摘を受け、原因を調査したところ、
旧構成(WPF `MediaPlayer`をUIスレッドで16ms間隔ポーリング+クラップ専用の別`WasapiOut`デバイス)
には3段階の遅延要因が積み重なっていることが判明。波形表示パイプラインには影響しないことを
確認した上で、ユーザーの明確な許可(「作り直した方がより良いものになるのであれば」)を得て
全面書き換えを実施:

- `HandClapPlayer`: 出力機能を廃し、PCMデコード保持専用クラスへ単純化
- `NAudioBgmPlayer`(新規): BGM再生とクラップ発音判定・PCM重ね合わせを**同一の音声レンダー
  コールバック内**で行う統一エンジン。可変速度(SpeedRatio)対応の線形補間再生、
  `Open()`直後の同期的`Position`代入に対応する「pending seek」処理、`lock`によるスレッド安全性を実装
- `PlaytestWindow`・`MainWindow`の全呼び出し箇所を新APIへ移行
  (`SetClapSchedule`/`SetClapVolume`等)
- ビルド0W/0E、テスト450/450件成功を確認

これにより、クラップの発音タイミング決定がBGMの音声クロックと同期した「音声スレッド同期」
方式になり、UIスレッドポーリング・別デバイスバッファという2つの遅延要因が解消された
(BMS等の外部キー音再生方式に近い構造)。

## 2. 残っているTBD

### 2-1. 複合ヘッダーの専用UI未整備(Raw入力のみ)

`ExtraHeaderDefs.cs`のコメントに明記されている暫定対応。仕様書6.4.4で個別UIが想定されているが、
現状は生文字列入力(Raw型)のまま:

- `dummyId`(難易度タブのチェックボックスリスト化)
- `difColor`(境界値+カラーコードの行追加・D&D並び替えUI)
- `unStockCategory`(word/back/mask固定チェックボックス)
- `stockForceDel`(種別ごとのパターン入力欄)
- `displayChainOFF`
- `keyGroupOrder`(難易度タブごとのキーグループ名D&D並び替えUI)
- `resultFormat`
- `resultValsView`(タグ追加形式のリスト入力)
- `preloadImages`(連番ファイル対応の行追加UI)
- `imgType`
- `titleAnimation` / `titleArrowName` / `skinType`

(`customGauge`は専用UI化済みのため対象外。`colorDataType`は廃止機能のため意図的に対象外)

### 2-2. FUJIエディタ形式インポートの残り未検証事項

- `W`(24分マイナス方向)単体での実例による検証がまだ無い(現状の式は`S`との対称性からの推定)
- レーン拡張マーカーとフリーズ終点の数字系fine文字(`1`〜`9`,`A`〜`I`)の組み合わせ実例が無く未検証
- FUJIエディタ自体のdos.txt書き出しダイアログの出力打ち切り挙動が未解明
  (当エディタ側の問題ではない可能性が高い)

### 2-3. ncolor_dataの範囲/グループ記法

`docs/NColorData_RangeGroupNotation_FutureWork.md`参照。範囲・グループ単位でのncolor_data記法
対応は未着手(現状は1件ずつの個別指定のみ)。

### 2-4. 歌詞表示機能(word_data)未対応

仕様: [dos-e0003-wordData](https://github.com/cwtickle/danoniplus/wiki/dos-e0003-wordData)。
任意の「歌詞入力レーン」(既定OFF・トグル表示)を設ける方針は確定しているが未着手。
データ名バリエーション(言語別/Scroll別/別キーモード別)まで全対応するかは実装時要検討。

### 2-5. カレント難易度タブのみをdosエクスポート(合作用)

複数人で1曲を分担制作する「合作」用途での単一タブ出力機能。エクスポート時に何を含めるか
(サフィックス採番・その他ヘッダーの扱い)をチェックボックスで選べるようにすべきかが未確定。

### 2-6. キーパターンのdos.txtエクスポート未対応(今回のスレッドで対象外と確定)

キーパターン機能はエディタ内(プレイテスト用途)のみに限定して実装した。パターン別ヘッダーの
dos.txt書き出しは今回完全に対象外とし、3-1「将来実装候補」に記録するのみとした。

### 2-7. おにスター・ITTNアナライザーの隠し機能解禁条件UI配線

前スレッド(`docs/progress_and_tbd_2026-07-25.md`参照)で洗い出し済みだが未確認の項目。
別スレッドで既に着手・完了している可能性があるため、次回作業時に現状確認が必要。

### 2-8. difData内ゲージ設定の専用入力欄が無い

`DifficultyTab.DifDataExtra`(dos.txtの各難易度行4列目以降に直接埋め込まれる
`border,recovery,damage,initLife%`形式の生値)を、エディタUIから直接編集する入力欄が無い
(現状はインポート時の値保持のみ)。詳細は`docs/progress_and_tbd_2026-07-25.md`2-3参照。

## 3. 将来実装候補(TBDとは別枠、今は着手しないことが確定している事項)

1. **FUJIエディタへのエクスポート機能**: 現段階では実装予定なし(現状インポートのみ対応)
2. **SKBエディタへのエクスポート機能**: 同上
3. **ラベル密集時の省略表示**: speed/boost/BPM等のイベントタグ密集時の省略表示。需要が
   今のところ無く、必須機能とはしない
4. **テンプレートエディタによる本体用カスタムキーテンプレート書き出し**: `TemplateEditorWindow`
   で作成したテンプレートを、danoniplus本体(CW Edition)の「カスタムキーテンプレート」として
   そのまま使える形で書き出す機能。現時点では必要性が低いため未着手
5. **キーパターンのdos.txtエクスポート対応(今回追加)**: パターン別ヘッダー(dos.txt書き出し)
   への反映。本家wikiでは「danoni_settings.js等の共通設定ファイルへの記述を推奨」との記載もあり、
   dos.txtに書くべき情報かどうか自体が微妙なため、需要が明確になってから検討する

## 4. テスト状況

`DanoniEditor.Core.Tests`は450件全てパス。App層はビルド成功を都度確認
(WPFのためLinux環境ではUIテスト実行不可、ビルド確認のみ)。

## 5. 参考ドキュメント

- `docs/progress_and_tbd_2026-07-22.md` — β版判断材料としての全体進捗・TBDまとめ(配布形態の
  確認結果を含む)
- `docs/progress_and_tbd_2026-07-25.md` — アナライザー・おにスター・クラッシュ復旧スレッドの進捗
- `docs/remaining_tbd_2026-07-22.md` — その他ヘッダーUI未整備項目など
