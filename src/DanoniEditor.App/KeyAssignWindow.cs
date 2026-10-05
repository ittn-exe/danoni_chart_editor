using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.App;

/// <summary>
/// プレーテストのキーアサイン変更ウィンドウ(2026-09-27要望対応)。環境設定「テスト再生」カテゴリの
/// 「キーアサイン」ボタンから開く。キー種をドロップダウンで選び、そのキー種のレーン一覧と現在の
/// 割当(テンプレート既定、またはPlaytestKeyAssignOverridesでの上書き)を表示する。レーンの「割当」
/// ボタンをクリックすると入力待機状態になり、次に押されたキーをそのレーンの上書きへ追加する
/// (TemplateEditorWindowのキャプチャUIと同じ流儀: ボタン文言変化・Window.PreviewKeyDownで横取り・
/// KeyLabelMapper.LabelForKeyで変換・対応外キーは警告・Escで中止)。
///
/// 2026-09-27b「複数キーの割り当て対応」要望により、上書きは1レーン1キーの丸ごと差し替えではなく、
/// テンプレート本来のKeyAssign同様「1レーンに複数の物理キー」を割り当てられるようにした。
/// 「割当」ボタンを複数回押して都度キーを追加でき(既に含まれているキーは重複追加しない)、
/// 各レーンの「リセット」ボタンでその上書きを取り除いてテンプレート既定へ戻せる。
///
/// テンプレート本来のKeyAssign(dos.txt出力・本体エンジンに影響する本来のキー配置)は変更しない。
///
/// 2026-09-29不具合修正: 以前は呼び出し元(PreferencesWindow)から渡された「作業コピー」(環境設定
/// ウィンドウ全体のOK/キャンセルに従う_work)へ直接書き込むだけで、このウィンドウ自身はsettings.json
/// への保存を行わない実装だった。しかし「割当」ボタンを押した時点で画面上には「(変更済)」と表示され、
/// あたかもその場で確定したかのように見えるため、環境設定ウィンドウ側をOKで確定せずに閉じてしまうと
/// キーアサインの変更ごと破棄されてしまう不具合報告があった。これを受け、このウィンドウは呼び出し元
/// から現行(実際に使われている)のAppSettingsを直接渡してもらい、割当・リセットのたびに即座に反映・
/// 即座にsettings.jsonへ保存する独立した挙動に変更した(環境設定ウィンドウのOK/キャンセルとは無関係に
/// 確定する)。呼び出し元(PreferencesWindow)側は、このウィンドウを閉じた直後に自身の作業コピーへも
/// 同じ内容を同期させ、後でOKを押した際に古い作業コピーで上書き保存してしまわないようにしている。
/// </summary>
internal sealed class KeyAssignWindow : Window
{
    private readonly AppSettings _settings;
    private readonly TemplateRepository? _templates;
    private readonly ComboBox _keyTypeCombo = new() { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _laneList = new();
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

    private bool _capturing;
    private Button? _captureButton;
    private string? _captureLaneId;
    private KeyTemplate? _currentTemplate;

    public KeyAssignWindow(AppSettings settings, TemplateRepository? templates)
    {
        _settings = settings;
        _templates = templates;

        Title = "プレーテスト キーアサイン";
        Width = 480;
        Height = 480;
        MinWidth = 380;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        WindowStyle = WindowStyle.ToolWindow;

        var root = new DockPanel { Margin = new Thickness(12) };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(new TextBlock { Text = "キー種:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        top.Children.Add(_keyTypeCombo);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var closeBtn = new Button { Content = "閉じる", Width = 80, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(closeBtn);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        var scroll = new ScrollViewer { Content = _laneList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        Content = root;

        closeBtn.Click += (_, _) => Close();

        if (_templates is not null)
        {
            foreach (var id in _templates.ListKeyTypeIds()) _keyTypeCombo.Items.Add(id);
        }
        _keyTypeCombo.SelectionChanged += (_, _) => RefreshLaneList();
        if (_keyTypeCombo.Items.Count > 0) _keyTypeCombo.SelectedIndex = 0;
        else _status.Text = "(テンプレート一覧を取得できませんでした)";

        PreviewKeyDown += Window_PreviewKeyDown_KeyCapture;
        Closed += (_, _) => EndCapture();
    }

    private void RefreshLaneList()
    {
        EndCapture();
        _laneList.Children.Clear();
        _currentTemplate = null;
        if (_templates is null || _keyTypeCombo.SelectedItem is not string keyTypeId) return;

        try
        {
            _currentTemplate = _templates.Get(keyTypeId);
        }
        catch (Exception ex)
        {
            _laneList.Children.Add(new TextBlock
            {
                Text = $"読み込み失敗: {ex.Message}",
                Foreground = Brushes.Red,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var lane in _currentTemplate.Lanes.OrderBy(l => l.DisplayOrder))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock { Text = lane.LaneId, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(nameText, 0);
            row.Children.Add(nameText);

            var btn = new Button { Width = 170, Margin = new Thickness(4, 0, 0, 0) };
            btn.Content = CurrentLabelText(keyTypeId, lane);
            string laneId = lane.LaneId;
            btn.Click += (_, _) => BeginCapture(btn, laneId);
            Grid.SetColumn(btn, 1);
            row.Children.Add(btn);

            var resetBtn = new Button { Content = "リセット", Width = 60, Margin = new Thickness(4, 0, 0, 0) };
            resetBtn.Click += (_, _) => ResetLane(btn, laneId);
            Grid.SetColumn(resetBtn, 2);
            row.Children.Add(resetBtn);

            _laneList.Children.Add(row);
        }
    }

    /// <summary>レーンの表示テキスト。上書き済みなら上書き後のキー名(複数可、"/"区切り)+「(変更済)」、
    /// 未上書きならテンプレート既定のKeyAssignをそのまま表示する。</summary>
    private string CurrentLabelText(string keyTypeId, LaneDef lane)
    {
        if (_settings.PlaytestKeyAssignOverrides.TryGetValue(keyTypeId, out var overrides)
            && overrides.TryGetValue(lane.LaneId, out var labels) && labels.Count > 0)
            return $"{string.Join("/", labels)}(変更済)";
        return string.Join("/", lane.KeyAssign);
    }

    /// <summary>指定レーンの上書きを取り除き、テンプレート既定へ戻す(2026-09-27b要望対応:
    /// 追記専用のキャプチャだけでは割り当てを減らす手段がないため設けた明示的なクリア操作)。
    /// 2026-09-29不具合修正: 即座に設定ファイルへ保存する(クラス冒頭コメント参照)。</summary>
    private void ResetLane(Button button, string laneId)
    {
        if (_capturing && _captureLaneId == laneId) EndCapture();
        bool changed = false;
        if (_keyTypeCombo.SelectedItem is string keyTypeId
            && _settings.PlaytestKeyAssignOverrides.TryGetValue(keyTypeId, out var overrides))
        {
            changed = overrides.Remove(laneId);
            if (overrides.Count == 0) _settings.PlaytestKeyAssignOverrides.Remove(keyTypeId);
        }
        if (changed) _settings.Save(AppPaths.SettingsFilePath);
        if (_currentTemplate is not null && _keyTypeCombo.SelectedItem is string kt)
        {
            var lane = _currentTemplate.Lanes.FirstOrDefault(l => l.LaneId == laneId);
            if (lane is not null) button.Content = CurrentLabelText(kt, lane);
        }
    }

    private void BeginCapture(Button button, string laneId)
    {
        if (_capturing) EndCapture();
        _capturing = true;
        _captureButton = button;
        _captureLaneId = laneId;
        button.Content = "キー入力待ち...(Esc)";
        button.IsEnabled = false;
        _status.Text = $"「{laneId}」レーンに追加するキーを押してください(Escでキャンセル、複数キーを割り当てる場合はこの操作を繰り返してください)";
        Keyboard.Focus(this);
    }

    private void EndCapture()
    {
        if (_captureButton is not null && _currentTemplate is not null && _keyTypeCombo.SelectedItem is string keyTypeId)
        {
            var lane = _currentTemplate.Lanes.FirstOrDefault(l => l.LaneId == _captureLaneId);
            if (lane is not null) _captureButton.Content = CurrentLabelText(keyTypeId, lane);
            _captureButton.IsEnabled = true;
        }
        _capturing = false;
        _captureButton = null;
        _captureLaneId = null;
        _status.Text = "";
    }

    private void Window_PreviewKeyDown_KeyCapture(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            EndCapture();
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var label = KeyLabelMapper.LabelForKey(key);
        if (label is null)
        {
            MessageBox.Show(this, $"「{key}」はキーアサインに使用できません。", "キーアサイン",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            EndCapture();
            return;
        }

        if (_keyTypeCombo.SelectedItem is string keyTypeId && _captureLaneId is not null)
        {
            if (!_settings.PlaytestKeyAssignOverrides.TryGetValue(keyTypeId, out var overrides))
            {
                overrides = [];
                _settings.PlaytestKeyAssignOverrides[keyTypeId] = overrides;
            }
            if (!overrides.TryGetValue(_captureLaneId, out var labels))
            {
                labels = [];
                overrides[_captureLaneId] = labels;
            }
            // 2026-09-27b: 既に含まれているキーは重複追加しない(TemplateEditorWindowのキャプチャUIと同じ流儀)。
            if (!labels.Contains(label))
            {
                labels.Add(label);
                // 2026-09-29不具合修正: 即座に設定ファイルへ保存する(クラス冒頭コメント参照)。
                _settings.Save(AppPaths.SettingsFilePath);
            }
        }
        EndCapture();
    }
}
