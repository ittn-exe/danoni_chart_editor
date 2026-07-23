using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Settings;
using Microsoft.Win32;

namespace DanoniEditor.App;

/// <summary>
/// 環境設定ウィンドウ(2026-07-19、仕様書14章)。メニューバー「設定 > 環境設定」から開く。
/// 左にカテゴリ一覧、右に設定項目のカテゴリ式レイアウト。従来のDisplaySettingsDialog(表示設定のみの
/// 最小ダイアログ)を置き換え、AppSettingsの全項目をここへ集約する。
/// 今後の設定項目(headerDefaults/colorHistory/macros/undoHistorySize等、仕様書14章のTBD)も
/// カテゴリを足すだけで拡張できる構造にしてある。
/// </summary>
internal sealed class PreferencesWindow : Window
{
    private readonly AppSettings _work; // 作業コピー(OKで確定)

    /// <summary>OK確定後の設定。キャンセル時はnull</summary>
    public AppSettings? Result { get; private set; }

    // --- 表示 ---
    private readonly CheckBox _showImages = new() { Content = "ノート画像を表示する" };
    private readonly CheckBox _showGrid = new() { Content = "強調グリッド(横棒)を表示する" };
    private readonly CheckBox _excludeFreezeEndHighlight = new() { Content = "フリーズアロー終点を強調グリッドの対象から除外する" };
    private readonly TextBox _gridWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _gridColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _gridPreview = MakePreview();
    private readonly TextBox _startLineWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _startLineColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _startLinePreview = MakePreview();
    // --- カーソルライン(マウスモード、2026-07-25) ---
    private readonly TextBox _cursorLineWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _cursorLineColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _cursorLinePreview = MakePreview();
    private readonly TextBox _cursorHighlightWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _cursorHighlightColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _cursorHighlightPreview = MakePreview();

    // --- 目視テスト ---
    private readonly ComboBox _followMode = new() { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };

    // --- プレイテスト ---
    private readonly CheckBox _ptReverse = new() { Content = "Reverse(スクロール反転)" };
    private readonly ComboBox _ptHiSpeed = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _ptOffset = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _ptScale = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _ptQuitDelete = new() { Content = "Delete" };
    private readonly CheckBox _ptQuitBackSpace = new() { Content = "BackSpace" };
    private readonly CheckBox _ptQuitEscape = new() { Content = "Escape" };
    // --- プレイテスト: キー種ごとのReverse既定値(2026-08-02要望対応) ---
    private readonly Dictionary<string, CheckBox> _ptReverseByKeyType = [];

    // --- マーカー表示(表示カテゴリ内) ---
    private readonly RadioButton _markerFull = new() { Content = "全文表示", GroupName = "marker" };
    private readonly RadioButton _markerHead = new() { Content = "先頭数文字のみ", GroupName = "marker" };
    private readonly TextBox _markerHeadChars = new() { Width = 50, HorizontalAlignment = HorizontalAlignment.Left };

    // --- 新規プロジェクト(headerDefaults) ---
    private readonly TextBox _defStartFrame = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _defBlankFrame = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _defTuning = new() { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _defFrzAttempt = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _defBpm = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };

    // --- 編集・保存 ---
    private readonly TextBox _undoSize = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _confirmUnsaved = new() { Content = "未保存の変更がある時、終了前に確認する" };
    private readonly TextBox _colorHistLimit = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _recentFilesLimit = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };

    // --- 譜面ビューReverse(2026-07-22、環境設定のみで切替) ---
    private readonly CheckBox _chartViewReverse = new() { Content = "譜面ビューをReverse表示する(tick0を下端・末尾を上端にする)" };

    // --- SKB操作モード(キーボード操作、2026-07-21) ---
    private readonly TextBox _kbdThreshold = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };

    // --- グリッド分解能ショートカット(Ctrl+1〜9,0,-,^、2026-07-26) ---
    private readonly ComboBox _gridShortcutPreset = new() { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };

    // --- musicURLからの楽曲取得(2026-07-27) ---
    private readonly CheckBox _musicUrlEnabled = new() { Content = "musicURLから楽曲を取得できるようにする" };
    private readonly TextBox _musicUrlFolder = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left, IsReadOnly = true };
    private readonly Button _musicUrlBrowse = new() { Content = "参照...", Width = 70, Margin = new Thickness(4, 0, 0, 0) };

    // --- 全選択(Shift+Ctrl+A)の対象(2026-07-21) ---
    private readonly CheckBox _selAllNote = new() { Content = "ノート" };
    private readonly CheckBox _selAllFreeze = new() { Content = "フリーズアロー" };
    private readonly CheckBox _selAllSpeed = new() { Content = "速度変化(speed_data)" };
    private readonly CheckBox _selAllBoost = new() { Content = "個別加速(boost_data)" };
    private readonly CheckBox _selAllBpm = new() { Content = "BPM変化" };
    private readonly CheckBox _selAllTimeSig = new() { Content = "拍子変化" };
    private readonly CheckBox _selAllMarker = new() { Content = "マーカー" };

    // --- テンプレート(temp_*.json、2026-07-29) ---
    private readonly ListBox _templateList = new() { Margin = new Thickness(0, 0, 0, 8), Height = 260 };
    private readonly Button _templateEditButton = new() { Content = "編集", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _templateNewButton = new() { Content = "新規作成", Width = 90 };
    private readonly TemplateRepository? _templates;

    private readonly TextBlock _error = new() { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    public PreferencesWindow(AppSettings current, int initialCategory = 0, TemplateRepository? templates = null)
    {
        _work = current.Clone();
        _templates = templates;

        Title = "環境設定";
        Width = 560;
        Height = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;

        // --- カテゴリ一覧+パネル切替 ---
        var categories = new ListBox { Margin = new Thickness(8), Width = 120 };
        categories.Items.Add("表示");
        categories.Items.Add("目視テスト");
        categories.Items.Add("プレイテスト");
        categories.Items.Add("新規プロジェクト");
        categories.Items.Add("編集・保存");
        categories.Items.Add("キーボードモード");
        categories.Items.Add("musicURL取得");
        categories.Items.Add("テンプレート");

        var panels = new[] { BuildDisplayPanel(), BuildVisualTestPanel(), BuildPlaytestPanel(), BuildNewProjectPanel(), BuildEditSavePanel(), BuildKeyboardModePanel(), BuildMusicUrlPanel(), BuildTemplatePanel() };
        var content = new ContentControl { Margin = new Thickness(0, 8, 8, 0) };
        categories.SelectionChanged += (_, _) =>
        {
            if (categories.SelectedIndex >= 0) content.Content = panels[categories.SelectedIndex];
        };
        categories.SelectedIndex = Math.Clamp(initialCategory, 0, panels.Length - 1);

        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 80, IsCancel = true };
        ok.Click += (_, _) => { if (TryCommit()) { DialogResult = true; } };
        cancel.Click += (_, _) => DialogResult = false;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var errDock = new Border { Child = _error, Margin = new Thickness(12, 0, 12, 0) };
        DockPanel.SetDock(errDock, Dock.Bottom);
        root.Children.Add(errDock);
        DockPanel.SetDock(categories, Dock.Left);
        root.Children.Add(categories);
        root.Children.Add(content);
        Content = root;

        LoadFrom(_work);
    }

    private static Border MakePreview() => new()
    {
        Height = 16,
        Width = 160,
        HorizontalAlignment = HorizontalAlignment.Left,
        BorderBrush = Brushes.Black,
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 2, 0, 8),
    };

    private static TextBlock Label(string text, bool section = false) => new()
    {
        Text = text,
        FontWeight = section ? FontWeights.Bold : FontWeights.Normal,
        Margin = section ? new Thickness(0, 8, 0, 6) : new Thickness(0, 4, 0, 2),
    };

    /// <summary>色コード入力欄+履歴ピッカーボタンの横並び行を作る(2026-07-23)。</summary>
    private UIElement ColorFieldRow(TextBox colorBox)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var historyBtn = new Button { Content = "履歴", Width = 36, Margin = new Thickness(4, 0, 0, 0) };
        historyBtn.Click += (_, _) => ColorHistoryPicker.Show(_work, historyBtn, hex => colorBox.Text = hex);
        row.Children.Add(colorBox);
        row.Children.Add(historyBtn);
        return row;
    }

    private UIElement BuildDisplayPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("ノート表示", section: true));
        _showImages.Margin = new Thickness(0, 0, 0, 4);
        _showGrid.Margin = new Thickness(0, 0, 0, 4);
        _excludeFreezeEndHighlight.Margin = new Thickness(16, 0, 0, 4); // 強調グリッドの子項目として少し字下げ
        p.Children.Add(_showImages);
        p.Children.Add(_showGrid);
        p.Children.Add(_excludeFreezeEndHighlight);
        p.Children.Add(new TextBlock
        {
            Text = "フリーズが密集した際に終点の横棒が見づらいという指摘への対応ですわ(既定OFF=従来通り表示)。",
            Foreground = Brushes.Gray,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 0, 0, 4),
        });
        p.Children.Add(Label("強調グリッドの太さ(px):"));
        p.Children.Add(_gridWidth);
        p.Children.Add(Label("強調グリッドの色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_gridColor));
        p.Children.Add(_gridPreview);
        _gridColor.TextChanged += (_, _) => _gridPreview.Background = SafeBrush(_gridColor.Text);
        _gridColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _gridColor.Text);

        p.Children.Add(Label("再生開始フレームライン", section: true));
        p.Children.Add(Label("太さ(px):"));
        p.Children.Add(_startLineWidth);
        p.Children.Add(Label("色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_startLineColor));
        p.Children.Add(_startLinePreview);
        _startLineColor.TextChanged += (_, _) => _startLinePreview.Background = SafeBrush(_startLineColor.Text);
        _startLineColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _startLineColor.Text);

        p.Children.Add(Label("カーソルライン(マウスモード)", section: true));
        p.Children.Add(new TextBlock
        {
            Text = "マウスホバー中、今クリックすると実際にどこへスナップされるかを示す線ですわ。" +
                   "全レーン共通の細い線と、カーソルが乗っているレーンだけを強調する太い帯を別々に設定できますの。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });
        p.Children.Add(Label("細い線の太さ(px):"));
        p.Children.Add(_cursorLineWidth);
        p.Children.Add(Label("細い線の色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_cursorLineColor));
        p.Children.Add(_cursorLinePreview);
        _cursorLineColor.TextChanged += (_, _) => _cursorLinePreview.Background = SafeBrush(_cursorLineColor.Text);
        _cursorLineColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _cursorLineColor.Text);

        p.Children.Add(Label("強調帯の太さ(px):"));
        p.Children.Add(_cursorHighlightWidth);
        p.Children.Add(Label("強調帯の色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_cursorHighlightColor));
        p.Children.Add(_cursorHighlightPreview);
        _cursorHighlightColor.TextChanged += (_, _) => _cursorHighlightPreview.Background = SafeBrush(_cursorHighlightColor.Text);
        _cursorHighlightColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _cursorHighlightColor.Text);

        p.Children.Add(Label("マーカーのコメント表示(仕様書7.4)", section: true));
        _markerFull.Margin = new Thickness(0, 0, 0, 2);
        _markerHead.Margin = new Thickness(0, 0, 0, 2);
        p.Children.Add(_markerFull);
        p.Children.Add(_markerHead);
        p.Children.Add(Label("先頭表示の文字数:"));
        p.Children.Add(_markerHeadChars);

        p.Children.Add(Label("譜面ビュー", section: true));
        _chartViewReverse.Margin = new Thickness(0, 0, 0, 2);
        p.Children.Add(_chartViewReverse);
        p.Children.Add(new TextBlock
        {
            Text = "進行方向(カーソル・目視テストの流れ)だけが逆になりますわ。ノート画像等の見た目は反転しませんの。" +
                   "プレイテストの表示には影響しませんわ。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement BuildVisualTestPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("再生位置ラインの追従方式", section: true));
        _followMode.Items.Add("ページ送り(画面外に出たら次の1画面へ)");
        _followMode.Items.Add("スムーズスクロール(ライン位置固定で譜面が流れる)");
        p.Children.Add(_followMode);
        p.Children.Add(new TextBlock
        {
            Text = "目視テスト(Space)中、再生位置ラインが画面外へ出た時の譜面ビューの動き方ですわ。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        return p;
    }

    private UIElement BuildPlaytestPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("プレイテスト(Ctrl+P)", section: true));
        _ptReverse.Margin = new Thickness(0, 0, 0, 4);
        p.Children.Add(_ptReverse);
        p.Children.Add(Label("ハイスピード(x0.25〜x10、0.25刻み):"));
        foreach (var v in Enumerable.Range(1, 40).Select(i => i * 0.25)) _ptHiSpeed.Items.Add(v);
        p.Children.Add(_ptHiSpeed);
        p.Children.Add(Label("タイミング調整オフセット(frame、正=譜面を後ろへ):"));
        p.Children.Add(_ptOffset);
        p.Children.Add(Label("ウィンドウサイズ倍率:"));
        foreach (var v in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 }) _ptScale.Items.Add(v);
        p.Children.Add(_ptScale);
        p.Children.Add(new TextBlock
        {
            Text = "これらは上部パネルの「プレーテスト」欄と同じ設定ですわ(どちらで変えても保存されますの)。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        p.Children.Add(Label("中断キー(2026-07-20)", section: true));
        _ptQuitDelete.Margin = new Thickness(0, 0, 0, 2);
        _ptQuitBackSpace.Margin = new Thickness(0, 0, 0, 2);
        _ptQuitEscape.Margin = new Thickness(0, 0, 0, 2);
        p.Children.Add(_ptQuitDelete);
        p.Children.Add(_ptQuitBackSpace);
        p.Children.Add(_ptQuitEscape);
        p.Children.Add(new TextBlock
        {
            Text = "checkedのキーだけがプレイテストの中断キーとして機能しますわ(最低1つはcheckedが必要ですの)。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        p.Children.Add(Label("キー種ごとのReverse既定値(2026-08-02要望対応)", section: true));
        p.Children.Add(new TextBlock
        {
            Text = "難易度タブを切り替えた際、そのキー種に応じて上部パネルの「プレーテスト:Reverse」の" +
                   "チェック状態を自動的に変更しますの。ここに無いキー種はOFF(通常)扱いですわ。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });
        _ptReverseByKeyType.Clear();
        if (_templates is null)
        {
            p.Children.Add(new TextBlock { Text = "(テンプレート一覧を取得できませんでした)", Foreground = Brushes.Gray, FontStyle = FontStyles.Italic });
        }
        else
        {
            foreach (var keyTypeId in _templates.ListKeyTypeIds())
            {
                var cb = new CheckBox { Content = $"{keyTypeId}k", Margin = new Thickness(0, 0, 0, 2) };
                _ptReverseByKeyType[keyTypeId] = cb;
                p.Children.Add(cb);
            }
        }
        return p;
    }

    private UIElement BuildNewProjectPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("新規プロジェクトのデフォルト値(headerDefaults)", section: true));
        p.Children.Add(new TextBlock
        {
            Text = "新規プロジェクト作成時に適用される初期値ですわ(仕様書6.4.1)。musicURLは対象外(毎回入力)ですの。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        p.Children.Add(Label("startFrame:"));
        p.Children.Add(_defStartFrame);
        p.Children.Add(Label("blankFrame(個人運用では200等):"));
        p.Children.Add(_defBlankFrame);
        p.Children.Add(Label("tuning(製作者名義):"));
        p.Children.Add(_defTuning);
        p.Children.Add(Label("frzAttempt:"));
        p.Children.Add(_defFrzAttempt);
        p.Children.Add(Label("BPM初期値(dos.txtインポートの推定不能時にも使用):"));
        p.Children.Add(_defBpm);
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement BuildEditSavePanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("編集", section: true));
        p.Children.Add(Label("Undo履歴の保持件数(仕様書14章、デフォルト30):"));
        p.Children.Add(_undoSize);
        p.Children.Add(Label("保存", section: true));
        _confirmUnsaved.Margin = new Thickness(0, 0, 0, 4);
        p.Children.Add(_confirmUnsaved);
        p.Children.Add(Label("色履歴", section: true));
        p.Children.Add(Label("色コード使用履歴の上限件数(デフォルト24):"));
        p.Children.Add(_colorHistLimit);
        p.Children.Add(new TextBlock
        {
            Text = "※履歴の記録・呼び出しUI(カラーピッカー連携)は今後の実装ですわ。上限だけ先に設定できますの。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        p.Children.Add(Label("最近開いたファイル(2026-07-28)", section: true));
        p.Children.Add(Label("履歴の保持件数(デフォルト10):"));
        p.Children.Add(_recentFilesLimit);
        p.Children.Add(new TextBlock
        {
            Text = "ファイル > 最近開いたファイルに表示する件数の上限ですわ。減らすと超過分は次回保存時に切り詰められますの。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        p.Children.Add(Label("全選択(Shift+Ctrl+A)の対象", section: true));
        foreach (var cb in new[] { _selAllNote, _selAllFreeze, _selAllSpeed, _selAllBoost, _selAllBpm, _selAllTimeSig, _selAllMarker })
            cb.Margin = new Thickness(0, 0, 0, 2);
        p.Children.Add(_selAllNote);
        p.Children.Add(_selAllFreeze);
        p.Children.Add(_selAllSpeed);
        p.Children.Add(_selAllBoost);
        p.Children.Add(_selAllBpm);
        p.Children.Add(_selAllTimeSig);
        p.Children.Add(_selAllMarker);
        p.Children.Add(new TextBlock
        {
            Text = "Ctrl+A(修飾無し)は常にノート・フリーズアローのみを対象としますわ。ここで選んだ種別は" +
                   "Shift+Ctrl+Aの時だけ有効になりますの。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement BuildKeyboardModePanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("SKB操作モード(Ctrl+,)", section: true));
        p.Children.Add(Label("同時押し判定の閾値(ms、デフォルト30):"));
        p.Children.Add(_kbdThreshold);
        p.Children.Add(new TextBlock
        {
            Text = "この時間以内に連続でノート入力キーを押すと、同じカーソル位置への入力(同時押し)として" +
                   "扱われ、カーソルが進みませんの。SKBエディタの同時押し判定に合わせた仕様ですわ。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        // --- グリッド分解能ショートカット(Ctrl+1〜9,0,-,^、2026-07-26) ---
        p.Children.Add(Label("グリッド分解能ショートカット(Ctrl+1〜9,0,-,^)", section: true));
        _gridShortcutPreset.Items.Add("オリジナルセット(分解能を単純な昇順で割り当て)");
        _gridShortcutPreset.Items.Add("SKB拡張セット(SKBエディタのCtrl+1〜7割り当てを踏襲)");
        p.Children.Add(_gridShortcutPreset);
        p.Children.Add(new TextBlock
        {
            Text = "Ctrl+数字キー(メイン列、テンキー不可)で譜面ビューのグリッド分解能(スナップ)を直接切り替え" +
                   "られますの。オリジナルセットは 1=4分/2=8分/3=12分/4=16分/5=20分/6=24分/7=28分/8=32分/" +
                   "9=40分/0=48分/-=56分/^=64分。SKB拡張セットは 1=4分/2=8分/3=16分/4=12分/5=24分/6=48分/" +
                   "7=32分/8=20分/9=28分/0=40分/-=56分/^=64分(1〜7はSKBエディタと同じ並び)。" +
                   "どちらもマウスモード・キーボードモードの両方で常時使え、選ぶと自動的にスナップもONになりますわ。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        return p;
    }

    private UIElement BuildMusicUrlPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("musicURLからの楽曲取得(2026-07-27)", section: true));
        p.Children.Add(new TextBlock
        {
            Text = "ONにすると、下記フォルダを「カレントディレクトリ」として扱い、プロジェクトのmusicURLで" +
                   "指定されたファイル名の楽曲をそこから読み込めるようになりますの。①タブのmusicURL欄の横に" +
                   "「読込」ボタンが現れ、musicURLを編集すると押せるようになりますわ。" +
                   "musicURL設定済みのプロジェクトファイルを開いた時は自動で読み込みますの。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        _musicUrlEnabled.Margin = new Thickness(0, 0, 0, 8);
        _musicUrlEnabled.Checked += (_, _) => _musicUrlFolder.IsEnabled = _musicUrlBrowse.IsEnabled = true;
        _musicUrlEnabled.Unchecked += (_, _) => _musicUrlFolder.IsEnabled = _musicUrlBrowse.IsEnabled = false;
        p.Children.Add(_musicUrlEnabled);

        p.Children.Add(Label("楽曲フォルダ:"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(_musicUrlFolder);
        _musicUrlBrowse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "楽曲フォルダを選択" };
            if (!string.IsNullOrWhiteSpace(_musicUrlFolder.Text)) dlg.InitialDirectory = _musicUrlFolder.Text;
            if (dlg.ShowDialog(this) == true) _musicUrlFolder.Text = dlg.FolderName;
        };
        row.Children.Add(_musicUrlBrowse);
        p.Children.Add(row);
        return p;
    }

    /// <summary>一覧行の表示用(2026-07-29要望: 「キー種 - ファイル名」形式)</summary>
    private sealed record TemplateListEntry(string KeyTypeId, string FileName, string Path)
    {
        public override string ToString() => $"{KeyTypeId} - {FileName}";
    }

    private UIElement BuildTemplatePanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("キー種テンプレート(temp_*.json)", section: true));
        p.Children.Add(new TextBlock
        {
            Text = "./templateフォルダのテンプレート一覧ですわ。「編集」で選択中のファイルを、" +
                   "「新規作成」で新しいキー種テンプレートを専用ウィンドウで作成・編集できますの。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        p.Children.Add(_templateList);
        _templateList.SelectionChanged += (_, _) => _templateEditButton.IsEnabled = _templateList.SelectedItem is not null;
        _templateList.MouseDoubleClick += (_, _) =>
        {
            if (_templateList.SelectedItem is TemplateListEntry entry) OpenTemplateEditor(entry.Path);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _templateEditButton.Click += (_, _) =>
        {
            if (_templateList.SelectedItem is TemplateListEntry entry) OpenTemplateEditor(entry.Path);
        };
        _templateNewButton.Click += (_, _) => OpenTemplateEditor(null);
        row.Children.Add(_templateEditButton);
        row.Children.Add(_templateNewButton);
        p.Children.Add(row);

        RefreshTemplateList();
        return p;
    }

    private void RefreshTemplateList()
    {
        _templateList.Items.Clear();
        var dir = AppPaths.FindAssetDir("template");
        if (dir is null) return;
        foreach (var path in Directory.EnumerateFiles(dir, "temp_*.json").OrderBy(p => System.IO.Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            string keyTypeId;
            try { keyTypeId = KeyTemplate.Load(path).KeyTypeId; }
            catch { keyTypeId = "?"; }
            _templateList.Items.Add(new TemplateListEntry(keyTypeId, System.IO.Path.GetFileName(path), path));
        }
    }

    private void OpenTemplateEditor(string? path)
    {
        var dir = AppPaths.FindAssetDir("template");
        if (dir is null) { _error.Text = "templateフォルダが見つかりませんの"; return; }
        var win = new TemplateEditorWindow(dir, path) { Owner = this };
        if (win.ShowDialog() != true) return;

        // 2026-07-29: 実行中のTemplateRepositoryキャッシュを破棄し、次回参照時にディスクの最新内容を
        // 再読込させる(編集直後にプロジェクトを新規作成/開いても古い内容のままになるのを防ぐ)。
        if (win.OriginalKeyTypeId is { } oldId) _templates?.Invalidate(oldId);
        if (win.SavedKeyTypeId is { } newId) _templates?.Invalidate(newId);
        RefreshTemplateList();
    }

    private void LoadFrom(AppSettings s)
    {
        _showImages.IsChecked = s.ShowNoteImages;
        _showGrid.IsChecked = s.ShowHighlightGrid;
        _excludeFreezeEndHighlight.IsChecked = s.ExcludeFreezeEndFromHighlight;
        _gridWidth.Text = s.HighlightLineWidth.ToString(CultureInfo.InvariantCulture);
        _gridColor.Text = s.HighlightLineColorHex;
        _gridPreview.Background = SafeBrush(s.HighlightLineColorHex);
        _startLineWidth.Text = s.PlaybackStartLineWidth.ToString(CultureInfo.InvariantCulture);
        _startLineColor.Text = s.PlaybackStartLineColorHex;
        _startLinePreview.Background = SafeBrush(s.PlaybackStartLineColorHex);
        _cursorLineWidth.Text = s.CursorLineWidth.ToString(CultureInfo.InvariantCulture);
        _cursorLineColor.Text = s.CursorLineColorHex;
        _cursorLinePreview.Background = SafeBrush(s.CursorLineColorHex);
        _cursorHighlightWidth.Text = s.CursorHighlightWidth.ToString(CultureInfo.InvariantCulture);
        _cursorHighlightColor.Text = s.CursorHighlightColorHex;
        _cursorHighlightPreview.Background = SafeBrush(s.CursorHighlightColorHex);
        _followMode.SelectedIndex = s.VisualTestFollowMode == "smooth" ? 1 : 0;
        _ptReverse.IsChecked = s.PlaytestReverse;
        _ptHiSpeed.SelectedItem = _ptHiSpeed.Items.Cast<double>().OrderBy(v => Math.Abs(v - s.PlaytestHiSpeed)).First();
        _ptOffset.Text = s.PlaytestOffsetFrames.ToString(CultureInfo.InvariantCulture);
        _ptScale.SelectedItem = _ptScale.Items.Cast<double>().OrderBy(v => Math.Abs(v - s.PlaytestWindowScale)).First();
        _ptQuitDelete.IsChecked = s.PlaytestQuitKeyDelete;
        _ptQuitBackSpace.IsChecked = s.PlaytestQuitKeyBackSpace;
        _ptQuitEscape.IsChecked = s.PlaytestQuitKeyEscape;
        foreach (var (keyTypeId, cb) in _ptReverseByKeyType)
            cb.IsChecked = s.PlaytestReverseByKeyType.TryGetValue(keyTypeId, out var rev) && rev;
        _markerFull.IsChecked = s.MarkerCommentFull;
        _markerHead.IsChecked = !s.MarkerCommentFull;
        _markerHeadChars.Text = s.MarkerCommentHeadChars.ToString(CultureInfo.InvariantCulture);
        _chartViewReverse.IsChecked = s.ChartViewReverse;
        _defStartFrame.Text = s.DefaultStartFrame.ToString(CultureInfo.InvariantCulture);
        _defBlankFrame.Text = s.DefaultBlankFrame.ToString(CultureInfo.InvariantCulture);
        _defTuning.Text = s.DefaultTuning;
        _defFrzAttempt.Text = s.DefaultFrzAttempt.ToString(CultureInfo.InvariantCulture);
        _defBpm.Text = s.DefaultBpm.ToString(CultureInfo.InvariantCulture);
        _undoSize.Text = s.UndoHistorySize.ToString(CultureInfo.InvariantCulture);
        _confirmUnsaved.IsChecked = s.ConfirmUnsavedOnClose;
        _colorHistLimit.Text = s.ColorHistoryLimit.ToString(CultureInfo.InvariantCulture);
        _recentFilesLimit.Text = s.RecentFilesLimit.ToString(CultureInfo.InvariantCulture);
        _selAllNote.IsChecked = s.SelectAllTargetNote;
        _selAllFreeze.IsChecked = s.SelectAllTargetFreeze;
        _selAllSpeed.IsChecked = s.SelectAllTargetSpeed;
        _selAllBoost.IsChecked = s.SelectAllTargetBoost;
        _selAllBpm.IsChecked = s.SelectAllTargetBpm;
        _selAllTimeSig.IsChecked = s.SelectAllTargetTimeSignature;
        _selAllMarker.IsChecked = s.SelectAllTargetMarker;
        _kbdThreshold.Text = s.SimultaneousPressThresholdMs.ToString(CultureInfo.InvariantCulture);
        _gridShortcutPreset.SelectedIndex = s.GridShortcutPreset == GridShortcutPresets.SkbExtended ? 1 : 0;
        _musicUrlEnabled.IsChecked = s.MusicUrlAutoLoadEnabled;
        _musicUrlFolder.Text = s.MusicUrlBaseFolder;
        _musicUrlFolder.IsEnabled = _musicUrlBrowse.IsEnabled = s.MusicUrlAutoLoadEnabled;
    }

    private bool TryCommit()
    {
        _error.Text = "";
        if (_showImages.IsChecked != true && _showGrid.IsChecked != true)
        { _error.Text = "ノート画像と強調グリッドの両方をOFFにはできませんの(どちらかはONにしてくださいまし)"; return false; }
        if (_ptQuitDelete.IsChecked != true && _ptQuitBackSpace.IsChecked != true && _ptQuitEscape.IsChecked != true)
        { _error.Text = "プレイテストの中断キーは最低1つはcheckedにしてくださいまし"; return false; }
        if (!TryPositive(_gridWidth.Text, out var gw))
        { _error.Text = "強調グリッドの太さは正の数値で入力してくださいまし"; return false; }
        if (!TryColor(_gridColor.Text))
        { _error.Text = "強調グリッドの色は #RRGGBB 形式で入力してくださいまし"; return false; }
        if (!TryPositive(_startLineWidth.Text, out var sw))
        { _error.Text = "再生開始ラインの太さは正の数値で入力してくださいまし"; return false; }
        if (!TryColor(_startLineColor.Text))
        { _error.Text = "再生開始ラインの色は #RRGGBB 形式で入力してくださいまし"; return false; }
        if (!TryPositive(_cursorLineWidth.Text, out var clw))
        { _error.Text = "カーソルライン(細い線)の太さは正の数値で入力してくださいまし"; return false; }
        if (!TryColor(_cursorLineColor.Text))
        { _error.Text = "カーソルライン(細い線)の色は #RRGGBB 形式で入力してくださいまし"; return false; }
        if (!TryPositive(_cursorHighlightWidth.Text, out var chw))
        { _error.Text = "カーソルライン(強調帯)の太さは正の数値で入力してくださいまし"; return false; }
        if (!TryColor(_cursorHighlightColor.Text))
        { _error.Text = "カーソルライン(強調帯)の色は #RRGGBB 形式で入力してくださいまし"; return false; }
        if (!double.TryParse(_ptOffset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ofs))
        { _error.Text = "調整オフセットは数値で入力してくださいまし"; return false; }
        if (!int.TryParse(_markerHeadChars.Text, out var headChars) || headChars < 1)
        { _error.Text = "マーカー先頭表示の文字数は1以上の整数で入力してくださいまし"; return false; }
        if (!int.TryParse(_defStartFrame.Text, out var defSf) || defSf < 0)
        { _error.Text = "startFrameは0以上の整数で入力してくださいまし"; return false; }
        if (!int.TryParse(_defBlankFrame.Text, out var defBf) || defBf < 0)
        { _error.Text = "blankFrameは0以上の整数で入力してくださいまし"; return false; }
        if (!int.TryParse(_defFrzAttempt.Text, out var defFa) || defFa < 0)
        { _error.Text = "frzAttemptは0以上の整数で入力してくださいまし"; return false; }
        if (!TryPositive(_defBpm.Text, out var defBpm))
        { _error.Text = "BPM初期値は正の数値で入力してくださいまし"; return false; }
        if (!int.TryParse(_undoSize.Text, out var undoSize) || undoSize < 1)
        { _error.Text = "Undo履歴件数は1以上の整数で入力してくださいまし"; return false; }
        if (!int.TryParse(_colorHistLimit.Text, out var colorLimit) || colorLimit < 1)
        { _error.Text = "色履歴の上限件数は1以上の整数で入力してくださいまし"; return false; }
        if (!int.TryParse(_recentFilesLimit.Text, out var recentLimit) || recentLimit < 1)
        { _error.Text = "最近開いたファイルの保持件数は1以上の整数で入力してくださいまし"; return false; }
        if (!TryPositive(_kbdThreshold.Text, out var kbdThreshold))
        { _error.Text = "同時押し判定の閾値は正の数値で入力してくださいまし"; return false; }
        if (_musicUrlEnabled.IsChecked == true && string.IsNullOrWhiteSpace(_musicUrlFolder.Text))
        { _error.Text = "musicURLからの楽曲取得をONにする場合、楽曲フォルダを指定してくださいまし"; return false; }

        _work.ShowNoteImages = _showImages.IsChecked == true;
        _work.ShowHighlightGrid = _showGrid.IsChecked == true;
        _work.ExcludeFreezeEndFromHighlight = _excludeFreezeEndHighlight.IsChecked == true;
        _work.HighlightLineWidth = gw;
        _work.HighlightLineColorHex = _gridColor.Text;
        _work.PlaybackStartLineWidth = sw;
        _work.PlaybackStartLineColorHex = _startLineColor.Text;
        _work.CursorLineWidth = clw;
        _work.CursorLineColorHex = _cursorLineColor.Text;
        _work.CursorHighlightWidth = chw;
        _work.CursorHighlightColorHex = _cursorHighlightColor.Text;
        _work.VisualTestFollowMode = _followMode.SelectedIndex == 1 ? "smooth" : "page";
        _work.PlaytestReverse = _ptReverse.IsChecked == true;
        if (_ptHiSpeed.SelectedItem is double hs) _work.PlaytestHiSpeed = hs;
        _work.PlaytestOffsetFrames = ofs;
        if (_ptScale.SelectedItem is double sc) _work.PlaytestWindowScale = sc;
        _work.PlaytestQuitKeyDelete = _ptQuitDelete.IsChecked == true;
        _work.PlaytestQuitKeyBackSpace = _ptQuitBackSpace.IsChecked == true;
        _work.PlaytestQuitKeyEscape = _ptQuitEscape.IsChecked == true;
        _work.PlaytestReverseByKeyType = _ptReverseByKeyType.ToDictionary(kv => kv.Key, kv => kv.Value.IsChecked == true);
        _work.MarkerCommentFull = _markerFull.IsChecked == true;
        _work.MarkerCommentHeadChars = headChars;
        _work.ChartViewReverse = _chartViewReverse.IsChecked == true;
        _work.DefaultStartFrame = defSf;
        _work.DefaultBlankFrame = defBf;
        _work.DefaultTuning = _defTuning.Text;
        _work.DefaultFrzAttempt = defFa;
        _work.DefaultBpm = defBpm;
        _work.UndoHistorySize = undoSize;
        _work.ConfirmUnsavedOnClose = _confirmUnsaved.IsChecked == true;
        _work.ColorHistoryLimit = colorLimit;
        _work.RecentFilesLimit = recentLimit;
        if (_work.RecentFiles.Count > recentLimit) _work.RecentFiles.RemoveRange(recentLimit, _work.RecentFiles.Count - recentLimit);
        _work.SelectAllTargetNote = _selAllNote.IsChecked == true;
        _work.SelectAllTargetFreeze = _selAllFreeze.IsChecked == true;
        _work.SelectAllTargetSpeed = _selAllSpeed.IsChecked == true;
        _work.SelectAllTargetBoost = _selAllBoost.IsChecked == true;
        _work.SelectAllTargetBpm = _selAllBpm.IsChecked == true;
        _work.SelectAllTargetTimeSignature = _selAllTimeSig.IsChecked == true;
        _work.SelectAllTargetMarker = _selAllMarker.IsChecked == true;
        _work.SimultaneousPressThresholdMs = kbdThreshold;
        _work.GridShortcutPreset = _gridShortcutPreset.SelectedIndex == 1
            ? GridShortcutPresets.SkbExtended
            : GridShortcutPresets.Original;
        _work.MusicUrlAutoLoadEnabled = _musicUrlEnabled.IsChecked == true;
        _work.MusicUrlBaseFolder = _musicUrlFolder.Text;
        Result = _work;
        return true;
    }

    private static bool TryPositive(string text, out double v) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0;

    private static bool TryColor(string text)
    {
        try { ColorConverter.ConvertFromString(text); return true; }
        catch { return false; }
    }

    private static Brush SafeBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!); }
        catch { return Brushes.Magenta; }
    }
}
