namespace DanoniEditor.PluginContracts;

/// <summary>
/// プラグインが本体とやり取りするための唯一の入口(2026-07-26、プラグイン対応の土台)。
/// <see cref="IEditorPlugin.Initialize"/>で渡されるインスタンスを保持しておき、以後はこれを通じて
/// 譜面データの読み取り・書き込み・プラグイン専用データの永続化・状態変化の通知を受け取る。
/// </summary>
public interface IPluginHost
{
    /// <summary>現在アクティブな難易度タブの読み取り専用スナップショット。プロジェクト未作成・
    /// タブ未選択の場合はnull。呼び出しのたびに最新の状態を返す(キャッシュはしない)。</summary>
    PluginChartContext? CurrentChart { get; }

    /// <summary>譜面データを書き換えるための限定APIへの参照(段階2)。プロジェクト未作成時に
    /// 呼び出した場合、各メソッドはfalseを返す。</summary>
    IPluginEditApi Edit { get; }

    /// <summary>プロジェクト・難易度タブの切替や、譜面データが編集されたタイミングで発火する。
    /// パネル/オーバーレイの再描画・再読み込みのトリガーに使う想定。</summary>
    event Action? ChartChanged;

    /// <summary>このプラグイン専用のデータをプロジェクトファイルへ永続化する保存領域から読み出す
    /// (プロジェクトのPluginData辞書を、プラグインIDで自動的に名前空間分けした上で参照する)。
    /// 未保存/プロジェクト未作成の場合はnull。</summary>
    string? GetPluginData(string key);

    /// <summary>同上の保存領域へ書き込む。プロジェクトが開かれていない場合は何もしない。</summary>
    void SetPluginData(string key, string value);
}
