# プラグインAPIリファレンス(現状仕様まとめ、2026-07-26版)

外部制作者がプラグインを作成する際に参照する、`DanoniEditor.PluginContracts`が公開するAPIと
本体側の読み込み仕様の一覧。本体(Core/Editing/App)の内部実装が変わっても、この文書に記載の形は
安定した窓口として維持される(互換性の保証は10章参照)。

## 1. プラグインとは

- 実体は `DanoniEditor.PluginContracts.dll` の1つ以上のインターフェースを実装した .NET(net8.0-windows、WPF)クラスライブラリ(.dll)。
- 本体の起動時に `./plugins` フォルダ(exeと同じ階層)直下の `*.dll` を全て読み込み、`IEditorPlugin` を実装し、かつ引数無しの公開コンストラクタを持つ型を自動的にインスタンス化する。
- 本体側のプロジェクト(Core/Editing/App)には一切依存せず、`DanoniEditor.PluginContracts` のみを参照して作る。

## 2. 読み込みの仕組み(2026-07-26互換性強化後の現状仕様)

- プラグインDLLはそれぞれ専用の `AssemblyLoadContext` で読み込まれる。
- **依存DLLの解決**: プラグインがNuGetパッケージ等へ依存している場合、①プラグインの`.deps.json`(あれば)→②プラグインと同じフォルダ、の順で自動解決される。依存DLL一式と`.deps.json`を`plugins`フォルダへ一緒に配置すればよい。
- **契約DLLの扱い**: `DanoniEditor.PluginContracts` への参照だけは、常に本体側の読み込み済みインスタンスへ委ねられる。`plugins`フォルダへ契約DLLをコピーする必要は無く、コピーされていた場合もスキップされる(型不一致事故の防止)。
- **診断ログ**: 読み込みの成功/失敗、初期化失敗、パネル生成・オーバーレイ描画中の例外は全て `plugins/plugin_log.txt` に記録される。プラグインが認識されない・動作しない場合はまずこのログを確認する。
- 1つのDLL・1つの型の読み込み失敗は他のプラグインの読み込みを妨げない(エラーは起動時にダイアログでも通知される)。
- ホットリロード・有効/無効切替UIは無い(DLL配置=有効化、削除=無効化。反映には再起動が必要)。

## 3. プラグインプロジェクトの作り方

`plugin-sdk/template/` をコピー&リネームするのが最短。手で作る場合:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="DanoniEditor.PluginContracts">
      <HintPath>(plugin-sdk内の DanoniEditor.PluginContracts.dll へのパス)</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

ビルド後、出力された `.dll`(依存があれば依存DLLと`.deps.json`も)を本体exeと同じ階層の
`./plugins` フォルダへ配置し、エディタを再起動すると読み込まれる。

## 4. 基底インターフェース: `IEditorPlugin`

```csharp
public interface IEditorPlugin
{
    string Id { get; }       // 一意なID(半角英数字推奨、他プラグインと重複させない)
    string Name { get; }     // UI上に表示するプラグイン名
    string Version { get; }  // 表示用のバージョン文字列(本体側は検証しない)
    void Initialize(IPluginHost host);  // 読み込み直後に一度だけ呼ばれる
}
```

これ単体では何もしない。下記の派生インターフェースを1つ以上組み合わせて実装する
(1つのプラグインが複数を同時に実装してもよい)。

## 5. UIパネル差し込み: `IEditorPanelPlugin`

```csharp
public interface IEditorPanelPlugin : IEditorPlugin
{
    string PanelTitle { get; }      // 右パネルのタブ見出し
    UIElement CreatePanel();        // タブの中身(WPF UIElement)を1つ生成する
}
```

- 本体は`CreatePanel()`の戻り値をそのまま右パネルの`TabItem.Content`へ設定する。内部のレイアウト・状態管理は全てプラグイン側の責任。
- 呼ばれるのはアプリ起動時に1回だけ。以後の表示更新はプラグイン自身が`IPluginHost.ChartChanged`イベントを購読して行う。

## 6. 譜面ビューへのオーバーレイ描画: `IChartOverlayPlugin`

```csharp
public interface IChartOverlayPlugin : IEditorPlugin
{
    void RenderOverlay(DrawingContext dc, PluginChartViewTransform transform, PluginChartContext? chart);
}
```

- 譜面ビューの再描画のたびに、本体の描画が完了した後、最前面に呼び出される。
- `chart`はプロジェクト未作成・タブ未選択の場合`null`になりうる。
- 描画中の例外は本体を止めず、`plugins/plugin_log.txt`へ記録された上でその回の描画だけスキップされる。
- **呼び出し頻度**: 固定fps(例: 毎フレーム60fps)での呼び出しではない。WPFの`OnRender`パス、すなわち`InvalidateVisual()`が呼ばれた時にだけ発生する。スクロール・ズーム・ノート編集・選択操作・Undo/Redo・プロジェクト読み込みなど、譜面ビューの見た目が変わりうる操作のたびに`InvalidateVisual()`が呼ばれる仕組みのため、実運用では「何か操作されるたびに数回〜」程度の頻度になる。プレイテスト画面の再生ヘッド移動中の連続呼び出しはこの対象外(プレイテスト画面は別ウィンドウで、オーバーレイAPIは現状譜面ビューのみ対応、11章参照)。重い処理を書く場合はこの前提で計測すること。
- **複数プラグイン間の描画順**: 明示的な優先度指定は無い。読み込み時に`plugins`フォルダ内のDLLファイル名を**英数字順(大文字小文字を区別しない)**に並べ、その順でプラグインを初期化・登録するため、オーバーレイもその順(A→B→C…とファイル名順)で呼び出される。複数プラグインが重なる描画をする場合、ファイル名の先頭に意図した順序を意識した命名(例: 連番プレフィックス)を付けることで見た目の重なり順を制御できる。

### `PluginChartViewTransform`(座標変換)

```csharp
public sealed class PluginChartViewTransform
{
    public Func<long, double> TickToScreenY { get; }   // tick → 譜面ビュー上のY座標(px)
    public Func<int, double> LaneToScreenX { get; }     // レーンindex(0始まり) → レーン列の中心X座標(px)
    public double NoteLaneWidth { get; }                // ノートレーン1本分の幅(px)
    public double ViewportWidth { get; }                // 表示中ビューポート幅(px)
    public double ViewportHeight { get; }               // 表示中ビューポート高さ(px)
    public bool IsReverse { get; }                       // Reverse表示(tick0を下端にする表示)が有効か
}
```

得られた座標がビューポート範囲外なら描画をスキップする(カリング)ことを推奨。

## 7. `IPluginHost`(本体とのやり取りの入口)

```csharp
public interface IPluginHost
{
    PluginChartContext? CurrentChart { get; }   // 最新の読み取り専用スナップショット(未オープン時null)
    IPluginEditApi Edit { get; }                 // 限定的な編集API
    event Action? ChartChanged;                  // プロジェクト/タブ切替・編集発生時に発火
    string? GetPluginData(string key);           // プラグイン専用データの読み出し
    void SetPluginData(string key, string value); // 同・書き込み
}
```

`Initialize(IPluginHost host)`で受け取ったインスタンスを保持し、以後は全てこれ経由でやり取りする。

**`ChartChanged`の発火条件**: 譜面の状態が変わりうる操作のほぼ全てで発火する。具体的には、

- ノート配置・削除等の編集操作(プラグイン自身の`Edit.PlaceNote`/`DeleteNote`経由の変更も含む、Undo/Redoスタックに積まれる全操作)
- Undo・Redo
- 難易度タブの切替・追加・削除・並び替え・複製
- BPM/StartNumber等、Undo対象外のヘッダー値の直接編集
- キー種変更、Reverse表示の切替
- プロジェクトの新規作成・オープン・保存完了・クローズ
- プラグイン自身の`SetPluginData`呼び出し(自分のデータ書き込みでも発火する点に注意)

選択操作・ドラッグ中の一時的な状態変化など「保存対象にならない」軽微な操作でも発火することがあるため、`ChartChanged`ハンドラは呼び出し頻度が高くなりうる前提で、重い処理は避けるか差分検知(前回スナップショットとの比較等)を行うことを推奨する。

## 8. 編集API: `IPluginEditApi`

```csharp
public interface IPluginEditApi
{
    bool PlaceNote(int laneIndex, long tick);   // 通常ノート配置(成功でtrue)
    bool DeleteNote(int laneIndex, long tick);  // 通常ノート削除(成功でtrue)
}
```

- 本体のUndo/Redoスタックへ正しく積まれるため、プラグインによる変更もユーザーが`Ctrl+Z`で取り消せる。
- 配置先に既にノートがある・対象が無い・レーン/tickが不正・プロジェクト未作成、の場合は`false`を返し何もしない。
- 現状は「通常ノートの配置/削除」のみ(段階2の最小構成)。拡張は要望に応じて行う。
- **座標範囲の詳細**:
  - `laneIndex`: `0`以上、`現在の難易度タブのレーン数未満`。固定の最大値ではなく、現在開いている難易度タブのキー種のレーン数に連動する(キー種によって変わる)。範囲外は`false`。
  - `tick`: `PlaceNote`は`tick < 0`のみ弾く(下限チェックのみ)。**上限チェックは無い**ため、曲の長さを超える巨大なtick値も配置自体は成功しうる(表示・エクスポート時の挙動は保証しない)。`DeleteNote`はtickの範囲チェック自体を行わず、対象位置に既存ノートがあるかどうかのみで判定する。

## 9. データ型・永続化

### `PluginChartContext`(現在の難易度タブの読み取り専用スナップショット)

```csharp
public sealed class PluginChartContext
{
    public string KeyTypeId { get; }
    public string DifficultyName { get; }
    public IReadOnlyList<PluginLaneInfo> Lanes { get; }
    public Func<long, double> TickToFrame { get; }
    public Func<double, long> FrameToTick { get; }
}
```

### `PluginLaneInfo`(1レーン分の情報)

```csharp
public sealed record PluginLaneInfo(
    string LaneId,
    string KeyAssign,                              // プレイテスト時の割当キー("/"区切り)
    int ColorGroup,
    IReadOnlyList<long> NoteTicks,
    IReadOnlyList<(long Start, long End)> FreezeTicks);
```

### プラグイン専用データの永続化

`GetPluginData`/`SetPluginData`は、プロジェクトファイル内の自由記述領域を、呼び出し元プラグインの
IDで自動的に名前空間分け(`"{プラグインID}.{key}"`)した上で読み書きする。

- 値は文字列のみ(構造化データはJSON文字列にして保存する)。
- **dos.txtへは出力されない**。エディタのプロジェクトファイル内だけに保存される。
- 本体はこの中身を一切解釈しない。

## 10. 互換性の保証

本体側は追記専用(append-only)ポリシー(`docs/plugin_api_compatibility_policy.md`)を採用しており、
外部制作者に対して以下を約束する:

- 公開済みのインターフェース・型・メンバーの削除・改名・シグネチャ変更は行わない。
- プラグインが実装するインターフェースへのメンバー追加は必ず既定実装付きで行うため、
  **古いSDKでビルドしたプラグインDLLは再ビルド不要のまま新しいエディタでも動き続ける**。
- 契約DLLのアセンブリバージョンは固定されており、バージョン不一致による読み込み失敗は発生しない。

## 11. 制約・注意点

- 全API呼び出しはUIスレッド(WPFのDispatcherスレッド)上で行われる想定。バックグラウンドスレッドを使う場合、UI操作・ホストAPI呼び出しは必ずUIスレッドへディスパッチすること。
- 判定ロジック・ゲージ計算式・マウス操作の基本挙動・ノート自体の描画方法など、エンジン中核の差し替えAPIは提供していない。
- プレイテスト画面へのオーバーレイは未対応(現状は譜面ビューのみ。将来課題)。

## 12. 付属サンプル: `HandMovementSamplePlugin`

`plugin-sdk/sample/` に同梱。`IEditorPanelPlugin`・`IChartOverlayPlugin`の両方を実装し、
右パネルタブ・オーバーレイ描画・ノート配置API・`ChartChanged`購読の一通りを実際に使っている。
実運用プラグインの出発点として参照されたい。

## 13. 参考ドキュメント

- `plugin-sdk/README.md` — セットアップ手順・トラブルシューティング
- `docs/plugin_api_compatibility_policy.md` — 本体開発者向けのAPI進化ルール(保証の裏付け)
- `docs/plugin_architecture_design_2026-07-26.md` — 設計方針・段階分け・将来構想
