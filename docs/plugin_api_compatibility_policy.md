# プラグインAPI互換性ポリシー(2026-07-26制定)

エディタ本体の開発者(=このリポジトリを編集する側)が、既存プラグインを壊さずに
`DanoniEditor.PluginContracts` を発展させるために守るルール。外部制作者向けの保証内容は
`plugin-sdk/README.md` の「互換性の保証」に要約して掲載する。

## 1. 基本原則

**追記専用(append-only)**。一度公開したインターフェース・型・メンバーは、削除・改名・
シグネチャ変更を行わない。機能を増やす場合は「新しいメンバー/型の追加」だけで実現する。

## 2. インターフェース拡張のルール(最重要)

インターフェースは「誰が実装するか」で壊れ方が非対称になるため、以下を厳守する。

### 2-1. プラグインが実装する側(IEditorPlugin / IEditorPanelPlugin / IChartOverlayPlugin 等)

- メンバーを追加する場合は、**必ずデフォルト実装(DIM、C# 8のdefault interface method)付き**で追加する。
  既定実装の無いメンバーを追加すると、旧バージョンでビルドされたプラグインDLLが実装を持たず
  読み込み時に壊れる。
- 新しい機能ディメンション(例: プレイテスト画面へのオーバーレイ)は、既存インターフェースへの
  追加ではなく**新しい派生インターフェースの新設**を優先する(実装するかどうかをプラグイン側が
  選べる形を保つ)。

```csharp
// 良い例: 既定実装付きの追加(旧プラグインは再ビルド不要のまま動く)
public interface IChartOverlayPlugin : IEditorPlugin
{
    void RenderOverlay(DrawingContext dc, PluginChartViewTransform transform, PluginChartContext? chart);
    bool WantsMouseEvents => false; // 新規追加は必ず「=> 既定値」付きで
}

// 悪い例: 既定実装なしの追加(旧プラグインDLLが読み込み時に壊れる)
// void OnMouseMove(Point position);
```

### 2-2. 本体が実装する側(IPluginHost / IPluginEditApi 等)

- メンバーの追加は自由(旧プラグインは新メンバーを呼ばないだけなので壊れない)。
- ただし削除・シグネチャ変更は不可(1章の原則通り)。

## 3. データ型(DTO)のルール

- `PluginLaneInfo` のような**位置指定レコードは、既存パラメータの順序変更・削除・型変更を行わない**。
  追加する場合は末尾へ、かつ既定値付きで行う(旧プラグインのDeconstruct/ToString等への影響を最小化)。
- 新しく公開するDTOは、将来の拡張余地を考えると位置指定レコードよりも
  `PluginChartContext` 型のような init プロパティのクラス形式を優先する(プロパティ追加が
  無条件に安全なため)。
- プロパティの意味(単位・座標系・null条件)を変えない。ドキュメントコメントに書いた挙動も
  契約の一部とみなす。

## 4. アセンブリバージョンのルール

- `DanoniEditor.PluginContracts` の **AssemblyVersion は 1.0.0.0 に固定**し、変更しない
  (csproj内で明示指定済み)。.NETは要求バージョンより低いアセンブリの解決に失敗するため、
  うっかり上げると「新しい本体で旧プラグインが動かない」ではなく「旧本体で新プラグインが
  動かない」事故の原因になる。人間向けの表示バージョンは `Version` / `FileVersion` のみ上げる。
- 破壊的変更がどうしても避けられない場合(想定していないが)は、AssemblyVersionを上げるのでは
  なく `DanoniEditor.PluginContracts.V2` のような**別アセンブリの新設**で対応し、旧契約は
  そのまま残して並走させる。

## 5. 読み込み側(ローダー)が仕組みで保証していること

`src/DanoniEditor.App/Plugins/PluginLoader.cs` 実装済みの防御(変更時はこの性質を維持すること):

- プラグインの依存DLLは、プラグインごとの `AssemblyDependencyResolver`(.deps.json準拠)→
  プラグインと同フォルダ探索、の順で解決する。
- 契約アセンブリ(`DanoniEditor.PluginContracts`)だけは、プラグインの依存解決で常に本体側
  (Default ALC)の読み込み済みインスタンスへ委ねる。plugins フォルダへ誤って契約DLLがコピー
  されていた場合もスキップする。二重読み込みを許すと「同名だが別物の型」となり、エラー無しで
  プラグインが認識されなくなるため。
- プラグイン起因の例外・読み込み失敗は `plugins/plugin_log.txt` へ必ず記録する。

## 6. 変更時のチェックリスト

PluginContracts に変更を入れる際は、以下を確認する:

1. 既存メンバーの削除・改名・シグネチャ変更をしていないか
2. プラグイン実装側インターフェースへの追加は、全て既定実装付きか
3. 位置指定レコードの既存パラメータに触れていないか
4. AssemblyVersion を変えていないか
5. `docs/plugin_api_reference.md` / `.html` を更新したか(plugin-sdkへはビルド時に自動反映)
6. 本文書に新しいルールが必要になった場合は追記したか
