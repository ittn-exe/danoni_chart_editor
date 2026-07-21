# キー種テンプレート(temp_*.json)の書式

対象: `./template/temp_{keyTypeId}.json`。1ファイル=1キー種のレーン構造定義。
実装: `src/DanoniEditor.Core/Models/KeyTemplate.cs`(`KeyTemplate`/`LaneDef`/`KeyAssignConverter`)、
読み込みは `TemplateRepository.cs`(ファイル名の `temp_` と `.json` を除いた部分が `keyTypeId` として扱われる。
中身の `keyTypeId` フィールドと一致させておくこと)。

バリデーションは一切行われない(壊れた値のまま読み込め、実行時に描画やエクスポートがおかしくなる形で
症状が出る)ため、手編集後は必ず実機で「譜面ビューの矢印位置」「プレイテスト」「dos.txt出力」を
一通り確認すること。

## 1. トップレベルフィールド

| フィールド | 型 | 意味 |
|---|---|---|
| `keyTypeId` | string | キー種ID。例: `"5"`, `"11L"`, `"12i"`, `"23"`。ファイル名 `temp_{keyTypeId}.json` と一致させる |
| `keyTypeName` | string | 表示名。例: `"5key"` |
| `keyCount` | int | レーン数。**`lanes` 配列の要素数と一致させる(自動検証されない)** |
| `comment` | string? | 自由記述。未使用なら `""` |
| `blank` | double | 隣接ステップゾーン間の距離(px)。プレイテスト画面のX座標計算に使用 |
| `divideCnt` | double | 上下折返しの区切り値(本体の `div - 1`) |
| `posMax` | double | 位置インデックスの最大値(本体の `divMax`) |
| `lanes` | array | `LaneDef` の配列。下記2章参照 |

### blank / divideCnt / posMax とステップゾーンX座標の関係

プレイテスト画面のX座標は `KeyTemplate.GetStepX` で以下のように計算される(本家 `danoni_main.js`
の `getArrowSettings` 準拠)。

```
stdPos = posIndex - ((posIndex > divideCnt ? posMax : 0) + divideCnt) / 2
stepX  = blank * stdPos + (playingWidth - 50) / 2
```

`divideCnt` は「`posIndex` がこの値を超えたら折返し後(下段/別グループ)」という閾値として働く。
`posMax` は折返し後グループのオフセット計算に使う値で、`posIndex` の最大値と一致させるのが基本
(23keyの例: `posMax=27`、`posIndex` は `bright` が26で最大)。

## 2. lanes[] の各フィールド(LaneDef)

| フィールド | 型 | 意味 |
|---|---|---|
| `laneId` | string | レーン識別子。エディタ内部でのみ使用(dos.txt出力には出ない) |
| `dataName` | string | dos.txt出力時のベース名(例: `"left"` → `left_data`) |
| `frzDataNameOverride` | string? | frz名が `frz` + `dataName` の規則から外れる場合のみ指定(例: `leftdia` → `frzLdia`)。規則通りなら `null` |
| `displayOrder` | int | エディタ上のレーン表示順。0始まり、レーンごとに一意にする |
| `keyAssign` | string または string[] | 本家の実プレイキー割当(表示ラベル)。単一キーは文字列(`"←"`, `"Space"`)、同時に複数キーへ割り当てる場合は配列(`["3","4"]`)。**5key相当のレーンは矢印キー/Spaceを含むことが多い** |
| `keyboardInputKeys` | string または string[] | キーボードモード(SKB風入力)専用のノート入力キー。`keyAssign` とは別データ。**矢印/Space/BackSpace等はキーボードモードのカーソル移動に予約済みのため、ここに割り当てると操作と衝突する**。未指定(空)のレーンはキーボードモードでの入力を受け付けない |
| `colorGroup` | int | 色グループ番号。`setColor` ヘッダー(`DefaultLaneColors`)のインデックスに対応。同じ色を共有したいレーン同士に同じ値を振る |
| `posIndex` | double | 本体の `pos` 値。9hkey等では小数値(例: `11.75`)を取ることがある |
| `scrollDirection` | string | `"up"` または `"down"`。**up = ステップゾーンが上・ノーツは下から上へスクロール、down = ステップゾーンが下・ノーツは上から下へスクロール**(2026-07-19確定の定義。逆に解釈しないこと)。Reverse表示ON時はこの値が全レーンで反転する |
| `noteGraphic` | string | ノート画像名。`./img/{noteGraphic}.png` を読み込む。既存アセット: `arrow` / `onigiri` / `giko` / `iyo` / `c` / `monar` / `morara`。存在しない名前を指定すると画像なしのベクター矩形にフォールバックする |
| `rotationAngle` | double | ノート画像の回転角(度)。矢印系レーンは方向に合わせて指定(例: `left`=0, `down`=-90, `up`=90, `right`=180) |
| `engineLaneNum` | int | danoniplus本体エンジンの内部レーン番号。`ncolor_data` 等が参照する。**同一キー種内で重複させない・本体の実際のレーン順と一致させる**(ここがズレると色指定(ncolor_data)がレーンを取り違える) |
| `fujiLaneNum` | int? | FUJIエディタの `$dosformat [aNN]` 列番号。本体エンジン順と異なるキー種(23keyなど)でのみ明示指定。省略時は `displayOrder` を使う(`EffectiveFujiLane`) |

`KeyAssign`/`KeyboardInputKeys` は `KeyAssignConverter` により、JSON上は単一なら文字列、複数なら配列の
どちらでも読み書きできる(手編集のしやすさ優先)。

## 3. 最小サンプル(5key、`temp_5.json`)

```json
{
  "keyTypeId": "5",
  "keyTypeName": "5key",
  "keyCount": 5,
  "comment": "",
  "blank": 57.5,
  "divideCnt": 4,
  "posMax": 5,
  "lanes": [
    {
      "laneId": "left",
      "dataName": "left",
      "frzDataNameOverride": null,
      "displayOrder": 0,
      "keyAssign": "←",
      "keyboardInputKeys": "J",
      "colorGroup": 0,
      "posIndex": 0,
      "scrollDirection": "up",
      "noteGraphic": "arrow",
      "rotationAngle": 0,
      "engineLaneNum": 0
    }
    // ... down/up/right/space と続く
  ]
}
```

## 4. 複雑な例で見るポイント(23key、`temp_23.json`より抜粋)

```json
{
  "laneId": "aup",
  "dataName": "aup",
  "frzDataNameOverride": "afrzUp",
  "displayOrder": 2,
  "keyAssign": ["3", "4"],
  "keyboardInputKeys": "3",
  "colorGroup": 3,
  "posIndex": 2,
  "scrollDirection": "up",
  "noteGraphic": "arrow",
  "rotationAngle": 90,
  "engineLaneNum": 2,
  "fujiLaneNum": 2
}
```

- `keyAssign` が配列 → 本家では `3`/`4` どちらのキーでも同じレーンにノーツ入力できる
- `frzDataNameOverride` が規則外の名前(`afrzUp`。規則通りなら `frzAup` 等になるはずが本体側の命名がそうなっていないため明示指定)
- `fujiLaneNum` が `displayOrder` と一致しないレーンが存在する(23keyは本体エンジン順とFUJI列順が異なるキー種の代表例)

## 5. 手編集時に見落としやすいポイント(チェックリスト)

- `keyCount` と `lanes` の要素数が一致しているか(不一致は検出されない)
- `engineLaneNum` がキー種内で重複していないか、本体の実際のレーン順と一致しているか
- `displayOrder` がレーンごとに一意か(表示順が崩れるだけで実害は小さいが要確認)
- `fujiLaneNum` を明示指定すべきキー種(本体エンジン順とFUJI列順が異なるキー種)で指定漏れがないか
- `keyboardInputKeys` に矢印/Space/BackSpace等の予約キーを割り当てていないか
- `noteGraphic` が `./img/` 配下に実在するファイル名(拡張子なし)か
- JSON自体の構文エラー(末尾カンマ、引用符の閉じ忘れ等)。.NETの `System.Text.Json` は末尾カンマを許容しないため注意
- 色関連は `colorGroup` (setColor用、レーンごとに複数グループあり得る)と `frzColor`
  (曲/難易度ごとに常に4スロット固定、`colorGroup` とは無関係)を混同しないこと
  (`src/DanoniEditor.Core/Export/ColorDefaults.cs` 参照)
