using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DanoniEditor.Core.Export;
using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Settings;
using DanoniEditor.Core.Timing;
using DanoniEditor.Editing;
using Microsoft.Win32;

namespace DanoniEditor.App;

public partial class MainWindow : Window
{
    private readonly TemplateRepository _templates;
    private EditorDocument? _document;
    private SmartToolController? _controller;
    private bool _suppressSelectionEvent;
    private bool _suppressPropertyPanelEvents;
    private bool _suppressObjectPanelEvents;
    private ObjectRef? _currentPropertyObject;
    private EditorDocument? _selectionSubscribedDoc;
    private string? _currentFilePath;

    // --- 音楽ファイル再生(目テスト・プレイテスト用) ---
    private readonly MediaPlayer _audioPlayer = new();
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(33) }; // ≒30fps同期

    /// <summary>Spaceキーで開始する目視テスト中か(2026-07-17f)。目視テスト中のみ
    /// 追従スクロールと終了時のスクロール復帰が働く。</summary>
    private bool _visualTestActive;

    /// <summary>音楽ファイル読込済みか(2026-07-17g: 再生ボタン撤去に伴いIsEnabledの代わりに保持)</summary>
    private bool _audioLoaded;

    // --- 波形表示(2026-07-18) ---
    private Core.Audio.WaveformPeaks? _waveformPeaks;
    private string? _waveformPath;
    private bool _waveformDecoding;

    // --- アプリ環境設定(仕様書14章、2026-07-16b: ノート強調グリッドの太さ・色から実装開始) ---
    private AppSettings _appSettings = new();

    /// <summary>
    /// コンストラクタ完了フラグ。ShowNoteImagesToggleのIsChecked="True"(XAML)は
    /// InitializeComponent()実行中、ツリーがまだ構築し切っていない段階(Canvasはこのトグルより
    /// 後方の要素なのでまだ未生成)でChecked イベントを同期的に発火させてしまい、
    /// ApplyDisplaySettingsToCanvas()内でCanvasがnullのままNullReferenceExceptionが飛ぶ
    /// (=ウィンドウが一度も表示されずに落ちる。2026-07-16c: 「ビルドは通るが起動しない」の原因)。
    /// このフラグでInitializeComponent中に暴発したイベントを無視する。
    /// </summary>
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        _templates = new TemplateRepository(FindTemplateDir());
        SnapDivisionCombo.ItemsSource = SnapService.Divisions;
        SnapDivisionCombo.SelectedItem = 16;

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _playbackTimer.Tick += PlaybackTimer_Tick;

        // 2026-07-17f: 上部パネルのカーソル位置表示(tick/frame/秒)。ChartCanvasのOnMouseMoveは
        // キャプチャ中しかControllerへ流さないが、添付ハンドラは常時発火するのでここで拾う。
        Canvas.MouseMove += (_, me) =>
        {
            if (_document is null) return;
            double t = Math.Max(0, _document.CurrentLayout.YToTick(me.GetPosition(Canvas).Y));
            CursorPosText.Text = FormatTickPos(t);
        };

        // StartNumberドラッグ確定時に右パネルの数値表示を同期する(2026-07-18)
        Canvas.StartNumberChangedByDrag += RefreshProjectPropertiesPanel;

        _appSettings = AppSettings.Load(AppPaths.SettingsFilePath);
        ShowNoteImagesToggle.IsChecked = _appSettings.ShowNoteImages;
        ShowHighlightGridToggle.IsChecked = _appSettings.ShowHighlightGrid;
        ApplyDisplaySettingsToCanvas();

        // 2026-07-17g: プレイテスト設定(Reverse/ハイスピ/調整オフセット)の初期化
        PlaytestHiSpeedCombo.ItemsSource = Enumerable.Range(1, 40).Select(i => i * 0.25).ToList(); // x0.25〜x10(2026-07-19: 0.25刻み化、TBD§1-4の一部)
        PlaytestHiSpeedCombo.SelectedItem = PlaytestHiSpeedValues_Nearest(_appSettings.PlaytestHiSpeed);
        PlaytestReverseCheck.IsChecked = _appSettings.PlaytestReverse;
        PlaytestOffsetBox.Text = _appSettings.PlaytestOffsetFrames.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PlaytestScaleCombo.ItemsSource = PlaytestScaleValues; // ウィンドウサイズ倍率 x0.5〜3(2026-07-17h)
        PlaytestScaleCombo.SelectedItem = PlaytestScaleValues.OrderBy(v => Math.Abs(v - _appSettings.PlaytestWindowScale)).First();

        _initialized = true;
    }

    private void ApplyDisplaySettingsToCanvas()
    {
        var color = (Color)ColorConverter.ConvertFromString(_appSettings.HighlightLineColorHex)!;
        Canvas.ApplyDisplaySettings(_appSettings.ShowNoteImages, _appSettings.ShowHighlightGrid, _appSettings.HighlightLineWidth, color);
        var startColor = (Color)ColorConverter.ConvertFromString(_appSettings.PlaybackStartLineColorHex)!;
        Canvas.ApplyPlaybackStartLineSettings(_appSettings.PlaybackStartLineWidth, startColor);
        Canvas.MarkerCommentFull = _appSettings.MarkerCommentFull;   // 2026-07-19b
        Canvas.MarkerCommentHeadChars = Math.Max(1, _appSettings.MarkerCommentHeadChars);
    }

    private void ShowNoteImagesToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return; // XAML初期値設定によるInitializeComponent中の発火を無視(上記コメント参照)
        EnforceAndApplyDisplayToggles();
    }

    private void ShowHighlightGridToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        EnforceAndApplyDisplayToggles();
    }

    /// <summary>
    /// 「ノート画像 表示」と「強調表示」の両方が同時にOFFにならないようにする(2026-07-16j)。
    /// 2つのトグルの現在状態をUIから読み直し、両方OFFならノート画像側を強制的にONへ戻す
    /// (このIsChecked代入で本メソッドが再度呼ばれ、その時点で両方OFFではなくなっているので
    /// 下のif には入らず最終的な状態がまとめて_appSettingsへ確定・保存される)。
    /// </summary>
    private void EnforceAndApplyDisplayToggles()
    {
        bool showImages = ShowNoteImagesToggle.IsChecked == true;
        bool showGrid = ShowHighlightGridToggle.IsChecked == true;
        if (!showImages && !showGrid)
        {
            ShowNoteImagesToggle.IsChecked = true;
            return;
        }
        _appSettings.ShowNoteImages = showImages;
        _appSettings.ShowHighlightGrid = showGrid;
        ApplyDisplaySettingsToCanvas();
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    /// <summary>設定 > 環境設定(2026-07-19)。旧DisplaySettingsDialogを置き換えるカテゴリ式ウィンドウ</summary>
    private void OpenPreferences_Click(object sender, RoutedEventArgs e) => OpenPreferences(0);

    /// <summary>上部パネルの「表示設定...」ボタン(従来動作互換: 表示カテゴリを開く)</summary>
    private void DisplaySettings_Click(object sender, RoutedEventArgs e) => OpenPreferences(0);

    private void OpenPreferences(int category)
    {
        var win = new PreferencesWindow(_appSettings, category) { Owner = this };
        if (win.ShowDialog() != true || win.Result is null) return;
        _appSettings = win.Result;
        _appSettings.Save(AppPaths.SettingsFilePath);
        ApplyDisplaySettingsToCanvas();
        if (_document is not null) _document.UndoStack.Capacity = Math.Max(1, _appSettings.UndoHistorySize); // 2026-07-19b

        // 上部パネルの同項目コントロールへ反映(各Changedハンドラが再保存するが実害なし)
        ShowNoteImagesToggle.IsChecked = _appSettings.ShowNoteImages;
        ShowHighlightGridToggle.IsChecked = _appSettings.ShowHighlightGrid;
        PlaytestReverseCheck.IsChecked = _appSettings.PlaytestReverse;
        PlaytestHiSpeedCombo.SelectedItem = PlaytestHiSpeedValues_Nearest(_appSettings.PlaytestHiSpeed);
        PlaytestOffsetBox.Text = _appSettings.PlaytestOffsetFrames.ToString(CultureInfo.InvariantCulture);
        PlaytestScaleCombo.SelectedItem = PlaytestScaleValues.OrderBy(v => Math.Abs(v - _appSettings.PlaytestWindowScale)).First();
        Canvas.InvalidateVisual();
    }

    /// <summary>
    /// ./templateフォルダを探す。まずexeと同じフォルダ(publish単独exe配布時、csprojのContent項目でコピーされる場所)を見て、
    /// 無ければAppContext.BaseDirectoryから上へ辿る(開発中のDebug実行、tests側と同じ探索方式)。
    /// </summary>
    private static string FindTemplateDir() => AppPaths.FindAssetDir("template")
        ?? throw new DirectoryNotFoundException("./template フォルダが見つかりません(実行ファイルと同じ場所、および上位ディレクトリを探索しました)");

    /// <summary>1ウィンドウ1曲構成のため、複数曲を並行編集したい場合は新しいプロセスとして
    /// もう1つエディタを起動する(2026-07-17)。現在編集中のプロジェクトとは完全に独立する。</summary>
    private void NewWindow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                MessageBox.Show(this, "実行ファイルのパスを取得できませんでしたわ。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            System.Diagnostics.Process.Start(exePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"新しいウィンドウの起動に失敗しましたわ: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // =====================================================================
    // プロジェクト操作: 新規/開く/保存
    // =====================================================================

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var choice = NewProjectDialog.Ask(this, _templates, _appSettings.DefaultBpm);
        if (choice is not { } c) return;

        var template = _templates.Get(c.KeyTypeId);
        var project = new ChartProject
        {
            ProjectName = "untitled",
            MusicTitle = "無題の楽曲",
            BpmEvents = [new BpmEvent(0, c.Bpm)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
            // 環境設定のheaderDefaults(仕様書6.4.1/14章、2026-07-19b)。musicURLは対象外(都度入力)
            StartFrame = _appSettings.DefaultStartFrame,
            BlankFrame = _appSettings.DefaultBlankFrame,
            Tuning = _appSettings.DefaultTuning,
            FrzAttempt = _appSettings.DefaultFrzAttempt,
        };
        project.Tabs.Add(DifficultyTab.CreateFor(template, c.DifficultyName));
        _currentFilePath = null;
        OpenDocument(new EditorDocument(project, _templates));
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "プロジェクトファイル (*.json)|*.json|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var project = ProjectSerializer.Load(dlg.FileName);
            _currentFilePath = dlg.FileName;
            OpenDocument(new EditorDocument(project, _templates));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"読み込みに失敗しましたわ: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            MessageBox.Show(this, "プロジェクトが開かれていませんわ。", "保存できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var path = _currentFilePath;
        if (path is null)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "プロジェクトファイル (*.json)|*.json",
                FileName = _document.Project.ProjectName + ".json",
            };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }

        try
        {
            ProjectSerializer.Save(_document.Project, path);
            _currentFilePath = path;
            _document.MarkSaved(); // 未保存フラグ解除→タイトルバーの'*'も消える(2026-07-19b)
            UpdateWindowTitle();
            StatusText.Text = $"保存しました: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存に失敗しましたわ: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // =====================================================================
    // dos.txtエクスポート
    // =====================================================================

    private void ExportDos_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            MessageBox.Show(this, "プロジェクトが開かれていませんわ。", "エクスポートできません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "dos.txt (*.txt)|*.txt",
            FileName = "dos.txt",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var exporter = new DosExporter(_templates.Get);
            var text = exporter.Export(_document.Project, includeEditorMetadata: true);
            File.WriteAllText(dlg.FileName, text);
            StatusText.Text = $"エクスポートしました: {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"エクスポートに失敗しましたわ: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // =====================================================================
    // FUJI / SKB インポート
    // =====================================================================

    private void ImportFuji_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "FUJIエディタファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var fileName = Path.GetFileName(dlg.FileName);

        // 2026-07-17: 「キー種入力や難易度選択の時、自分がどのファイルをインポートしようとしているのか
        // 忘れてしまう」との要望対応。以降のダイアログにファイル名を表示する。
        var keyTypeId = SimplePrompt.Ask(this, "キー種の指定",
            $"インポート中のファイル: {fileName}\n\nこのFUJIファイルのキー種ID(例: 5, 7, 11, 11L, 9A 等)を入力してくださいませ。", "5");
        if (string.IsNullOrWhiteSpace(keyTypeId)) return;

        try
        {
            var text = File.ReadAllText(dlg.FileName);
            var importer = new FujiImporter(_templates.Get);
            var result = importer.Import(text, keyTypeId);

            // difDataに同一キー種の候補が複数ある場合、どれを使うか選んでもらう(2026-07-16e)。
            // Import側は暫定的に先頭候補を適用済みなので、選ばれなければそのまま先頭候補が使われる。
            if (result.DifDataCandidates.Count > 1)
            {
                var chosen = DifDataPickerDialog.Ask(this, result.DifDataCandidates, fileName);
                if (chosen is not null)
                {
                    result.Tab.DifficultyName = chosen.DifficultyName;
                    if (chosen.InitialSpeed is { } sp) result.Tab.InitialSpeed = sp;
                }
                else
                {
                    result.Warnings.Add("難易度候補の選択がキャンセルされたため、暫定値(先頭候補)のままです。手動で確認・修正してくださいませ");
                }
            }

            var project = GetOrCreateProjectForImport();
            var warnings = ProjectOperations.ApplyImport(project, result);
            FinishTabImport(project, warnings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"FUJIインポートに失敗しましたわ: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportSkb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "SKBエディタファイル (*.txt;*.json)|*.txt;*.json|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var fileName = Path.GetFileName(dlg.FileName);

        try
        {
            var text = File.ReadAllText(dlg.FileName);
            var importer = new SkbImporter(_templates.Get);
            var result = importer.Import(text);

            var name = SimplePrompt.Ask(this, "難易度名の指定",
                $"インポート中のファイル: {fileName}\n\nSKB形式には難易度名が保存されていないため、手動で入力してくださいませ。", "Normal");
            if (!string.IsNullOrWhiteSpace(name)) result.Tab.DifficultyName = name;

            var project = GetOrCreateProjectForImport();
            var warnings = ProjectOperations.ApplyImport(project, result);
            FinishTabImport(project, warnings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"SKBインポートに失敗しましたわ: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportDos_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "dos.txt (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;

        var autoEstimate = MessageBox.Show(this,
            "タイミング情報(de_*/es_*)が見つからなかった場合、ノートの分布からBPMを自動推定してみますか?\n" +
            "(推定できなければ既定BPM=120・4/4拍子を仮定します)",
            "BPM自動推定", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        try
        {
            var text = File.ReadAllText(dlg.FileName);
            var importer = new DosImporter(_templates.Get);
            var options = new DosImportOptions { AutoEstimateTiming = autoEstimate, DefaultBpm = _appSettings.DefaultBpm }; // 環境設定(仕様書15.2、2026-07-19b)
            var result = importer.Import(text, options);

            // dos.txtインポートは単体でプロジェクト全体(タブ複数を含む)を作るため、既存プロジェクトへの
            // タブ追加ではなく丸ごと置き換え("開く"に近い挙動)。
            _currentFilePath = null;
            OpenDocument(new EditorDocument(result.Project, _templates));

            var warnings = new List<string>(result.Warnings)
            {
                $"タイミング情報の出所: {result.TimingSource} / 最大スナップ誤差: {result.MaxSnapErrorFrames:F2}フレーム",
            };
            ReportImportWarnings(warnings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"dos.txtインポートに失敗しましたわ: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// FUJI/SKBインポート先のChartProjectを返す。プロジェクトが未作成の場合はここで新規作成する
    /// (「プロジェクトが無い状態でのインポートは新規作成を兼ねる」というユーザー要望に対応)。
    /// 空のEditorDocumentは作らない(EditorDocumentはタブ0件だと構築時に例外を投げる仕様のため、
    /// 先にProjectOperations.ApplyImportでタブを追加してからEditorDocumentを作る順序を守ること)。
    /// </summary>
    private ChartProject GetOrCreateProjectForImport() => _document?.Project ?? new ChartProject { ProjectName = "untitled" };

    /// <summary>
    /// ApplyImport後の後始末。projectは呼び出し元がGetOrCreateProjectForImport()で取得し、
    /// 実際にApplyImportへ渡したのと同一の参照でなければならない(取り違えるとインポートしたタブが
    /// 見えなくなる)。_documentが未作成だった場合はここでEditorDocumentを新規構築し、
    /// 既存プロジェクトへのインポートだった場合はUndo履歴・選択状態を維持したまま画面だけ更新する。
    /// </summary>
    private void FinishTabImport(ChartProject project, List<string> warnings)
    {
        if (_document is null)
        {
            _currentFilePath = null;
            OpenDocument(new EditorDocument(project, _templates));
        }
        else
        {
            OpenDocument(_document); // タブ一覧の再読込のみ。Undo履歴・選択状態はそのまま
        }
        _document!.CurrentTabIndex = _document.Project.Tabs.Count - 1;

        // 2026-07-16d バグ修正: OpenDocument内のDifficultyTabControl.SelectedIndex設定は、
        // 上のCurrentTabIndex代入より「前」に(古いCurrentTabIndexを使って)行われてしまうため、
        // ここで改めてUI側のタブ選択をモデルの最終値(=インポートされたタブ)に合わせないと、
        // 見た目だけ1つ前のタブのままになってしまう(モデルは正しく最終タブを指している)。
        _suppressSelectionEvent = true;
        DifficultyTabControl.SelectedIndex = _document.CurrentTabIndex;
        _suppressSelectionEvent = false;

        ReportImportWarnings(warnings);
    }

    private void ReportImportWarnings(List<string> warnings)
    {
        if (warnings.Count == 0)
        {
            StatusText.Text = "インポート完了(警告なし)";
            return;
        }
        StatusText.Text = $"インポート完了({warnings.Count}件の警告あり)";
        MessageBox.Show(this, string.Join("\n\n", warnings), "インポート時の警告", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // =====================================================================
    // ドキュメントの画面反映
    // =====================================================================

    private void OpenDocument(EditorDocument doc)
    {
        _document = doc;
        _controller = new SmartToolController(doc);
        _controller.CurrentTickChanged += () => CurrentTickText.Text = _controller.CurrentTick is { } ct ? FormatTickPos(ct) : "-"; // 2026-07-17f: tick単独→tick/frame/秒の複合表記へ

        Canvas.Document = doc;
        Canvas.Controller = _controller;
        FrameEditToggle.IsChecked = false; // 新ドキュメントは拍情報モードから(仕様書7.6、2026-07-17i)
        doc.UndoStack.Capacity = Math.Max(1, _appSettings.UndoHistorySize); // 仕様書14章(2026-07-19b)
        doc.Changed += UpdateWindowTitle; // タイトルバーの'*'表示(未解決事項§2-6、2026-07-19b)
        UpdateWindowTitle();

        ProjectTitleText.Text = $"{doc.Project.ProjectName} ({doc.Project.MusicTitle})";

        _suppressSelectionEvent = true;
        DifficultyTabControl.ItemsSource = null;
        DifficultyTabControl.ItemsSource = doc.Project.Tabs;
        DifficultyTabControl.DisplayMemberPath = nameof(DifficultyTab.DifficultyName);
        DifficultyTabControl.SelectedIndex = Math.Min(doc.CurrentTabIndex, doc.Project.Tabs.Count - 1);
        _suppressSelectionEvent = false;

        // 右パネル③(選択中オブジェクトのプロパティ)はDocument.Changedを購読して選択状態を追随する。
        // 同じdocインスタンスでOpenDocumentが再呼び出しされるケース(インポート後の再読込)があるため、
        // 重複購読を避けて古いdocからは外す。
        if (!ReferenceEquals(_selectionSubscribedDoc, doc))
        {
            if (_selectionSubscribedDoc is not null) _selectionSubscribedDoc.Changed -= RefreshSelectedObjectPanel;
            doc.Changed += RefreshSelectedObjectPanel;
            _selectionSubscribedDoc = doc;
        }

        ApplySnapToDocument();
        ResetAudioForDocument(doc);
        RefreshProjectPropertiesPanel();
        RefreshSelectedObjectPanel();
        RefreshColorPanel();
        RefreshExtraHeadersPanel();
    }

    // =====================================================================
    // 音楽ファイル読み込み/再生(目テスト・プレイテスト用)
    // =====================================================================

    /// <summary>ドキュメントを開き直した時の音楽状態リセット。プロジェクトに保存済みパスがあれば自動読込を試みる</summary>
    private void ResetAudioForDocument(EditorDocument doc)
    {
        _visualTestActive = false; // ドキュメント切替時は目視テストを強制終了(2026-07-17g)
        _playbackTimer.Stop();
        _audioPlayer.Stop();
        Canvas.PlaybackTick = null;
        _waveformPeaks = null; // 波形キャッシュは曲に紐づくためクリア(2026-07-18)
        _waveformPath = null;
        Canvas.Waveform = null;

        if (!string.IsNullOrEmpty(doc.Project.AudioFilePath) && File.Exists(doc.Project.AudioFilePath))
        {
            LoadAudioFile(doc.Project.AudioFilePath);
        }
        else
        {
            AudioFileText.Text = "音楽未読込";
            AudioFileText.FontStyle = FontStyles.Italic;
            _audioLoaded = false;
            AudioTimeText.Text = "-";
        }
    }

    private void LoadAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            MessageBox.Show(this, "先にプロジェクトを作成/読み込みしてくださいませ。", "音楽ファイル", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new OpenFileDialog { Filter = "音楽ファイル (*.mp3;*.wav;*.wma;*.ogg)|*.mp3;*.wav;*.wma;*.ogg|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        LoadAudioFile(dlg.FileName);
    }

    private void LoadAudioFile(string path)
    {
        try
        {
            _audioPlayer.Open(new Uri(path, UriKind.Absolute));
            _document!.Project.AudioFilePath = path;
            AudioFileText.Text = Path.GetFileName(path);
            AudioFileText.FontStyle = FontStyles.Normal;
            _audioLoaded = true;
            if (WaveformToggle.IsChecked == true) EnsureWaveformDecoded(); // 2026-07-18
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"音楽ファイルの読み込みに失敗しましたわ: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 2026-07-17g: Play/Pause/Stopボタンとそのハンドラは撤去。音楽再生はテスト専用のため
    // Space(目視テスト開始/終了)・Ctrl+Space(現在位置で終了)に一本化した。

    /// <summary>
    /// 再生位置(曲頭からの経過時間)をtickへ変換してカレントフレーム線を更新する。
    /// frame = 経過秒 × 60(仕様書の60fps基準)。この値はStartNumber/blankFrameを含む絶対フレーム軸と
    /// 同じ基準(曲頭=0)なので、TimingEngine.FrameToTickへそのまま渡せる。
    /// </summary>
    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_document is null || !_audioPlayer.NaturalDuration.HasTimeSpan) return;
        var pos = _audioPlayer.Position;
        AudioTimeText.Text = pos.ToString(@"mm\:ss\.ff");

        double frame = pos.TotalSeconds * 60.0;
        var engine = _document.Project.CreateTimingEngine();
        Canvas.PlaybackTick = engine.FrameToTick(frame);
        Canvas.InvalidateVisual();

        // 2026-07-17f: 目視テスト中の追従スクロール(未解決事項§2-1、方式はAppSettingsで選択)
        if (_visualTestActive && Canvas.PlaybackTick is { } lineTick)
        {
            double lineY = _document.CurrentLayout.TickToY(lineTick);
            double off = ChartScrollViewer.VerticalOffset;
            double vh = ChartScrollViewer.ViewportHeight;
            if (_appSettings.VisualTestFollowMode == "smooth")
            {
                // (B)スムーズスクロール: ラインを画面上端から35%の固定位置に据えて譜面側を流す
                ChartScrollViewer.ScrollToVerticalOffset(Math.Max(0, lineY - vh * 0.35));
            }
            else if (lineY > off + vh - 8 || lineY < off)
            {
                // (A)ページ送り: ラインが画面外へ出た瞬間、ラインが画面上端(+8pxマージン)に来るよう切替
                ChartScrollViewer.ScrollToVerticalOffset(Math.Max(0, lineY - 8));
            }
        }
    }

    // =====================================================================
    // 上段パネル(スナップ・難易度タブ)
    // =====================================================================

    /// <summary>選択中の難易度タブを閉じる(2026-07-17: 「タブを閉じるができない」要望対応)。
    /// 最低1タブは残す(0件になるとCurrentTab等が参照できなくなるため)。</summary>
    private void CloseCurrentTab_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var tabs = _document.Project.Tabs;
        if (tabs.Count <= 1)
        {
            MessageBox.Show(this, "最後の1タブは閉じられませんわ。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int idx = _document.CurrentTabIndex;
        var target = tabs[idx];
        var confirm = MessageBox.Show(this, $"タブ「{target.DifficultyName}」を閉じますか？(この操作はUndoできません)",
            "タブを閉じる", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        tabs.RemoveAt(idx);
        _document.CurrentTabIndex = Math.Min(idx, tabs.Count - 1);
        _document.Selection.Clear();
        OpenDocument(_document); // タブ一覧・各右パネルをまとめて再構築する
    }

    private void DifficultyTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvent || _document is null) return;
        if (DifficultyTabControl.SelectedIndex < 0) return;
        _document.CurrentTabIndex = DifficultyTabControl.SelectedIndex;
        Canvas.InvalidateMeasure();
        Canvas.InvalidateVisual();
        RefreshProjectPropertiesPanel();
        RefreshColorPanel();
    }

    // =====================================================================
    // 右パネル②: 色設定(setColor/frzColor、仕様書6.4.2)
    // =====================================================================

    private bool _suppressColorPanelEvents;

    private static readonly string[] DefaultSetColorPalette = ["#99FFFF", "#CCCCCC", "#FFFFFF", "#FF0066", "#99FFFF"];
    private static readonly string[] DefaultFrzColorSlots = ["#66FFFF", "#6666FF", "#FFFF66", "#FFFF66"];
    private static readonly string[] FrzSlotLabels = ["始点終点(通常)", "帯(通常)", "始点終点(判定中)", "帯(判定中)"];

    private int ColorGroupCount() =>
        _document!.CurrentTemplate.Lanes.Select(l => l.ColorGroup).DefaultIfEmpty(0).Max() + 1;

    private static List<string> DefaultSetColors(int groupCount) =>
        Enumerable.Range(0, groupCount).Select(i => DefaultSetColorPalette[i % DefaultSetColorPalette.Length]).ToList();

    private static List<string> DefaultFrzColors(int groupCount) =>
        Enumerable.Range(0, groupCount * 4).Select(i => DefaultFrzColorSlots[i % 4]).ToList();

    /// <summary>tab.SetColorOverrideをgroupCount件になるよう保証し、そのリスト参照を返す(1タブ目・
    /// 独自上書き中のタブいずれも、このメソッドを通して初めて実データを持つ)。</summary>
    private static List<string> EnsureSetColors(DifficultyTab tab, int groupCount)
    {
        tab.SetColorOverride ??= DefaultSetColors(groupCount);
        while (tab.SetColorOverride.Count < groupCount) tab.SetColorOverride.Add("");
        return tab.SetColorOverride;
    }

    private static List<string> EnsureFrzColors(DifficultyTab tab, int groupCount)
    {
        tab.FrzColorOverride ??= DefaultFrzColors(groupCount);
        while (tab.FrzColorOverride.Count < groupCount * 4) tab.FrzColorOverride.Add("");
        return tab.FrzColorOverride;
    }

    /// <summary>hexとしてパースできればそのブラシ、できなければ(グラデーション等の生文字列)灰色のプレビュー。</summary>
    private static Brush SafeColorBrush(string text)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(text)!); }
        catch { return Brushes.LightGray; }
    }

    private void AddColorField(Panel parent, string labelText, string value, bool enabled, (string Kind, int Group, int Slot) tag)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var label = new TextBlock { Text = labelText, Width = 100, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var swatch = new Border
        {
            Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0),
            BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
            Background = SafeColorBrush(value),
        };
        var box = new TextBox { Width = 140, Text = value, IsEnabled = enabled, Tag = tag };
        box.TextChanged += (_, _) => swatch.Background = SafeColorBrush(box.Text);
        box.LostFocus += ColorField_LostFocus;

        row.Children.Add(label);
        row.Children.Add(swatch);
        row.Children.Add(box);
        parent.Children.Add(row);
    }

    /// <summary>タブ切替・ドキュメント読込・共通チェック切替のたびに呼ばれ、②タブの表示を再構築する。</summary>
    private void RefreshColorPanel()
    {
        if (_document is null) return;
        SetColorPanel.Children.Clear();
        FrzColorPanel.Children.Clear();

        bool isFirstTab = _document.CurrentTabIndex == 0;
        ColorTab0NoticeText.Visibility = isFirstTab ? Visibility.Visible : Visibility.Collapsed;
        ColorCommonCheck.Visibility = isFirstTab ? Visibility.Collapsed : Visibility.Visible;

        var tab0 = _document.Project.Tabs[0];
        var currentTab = _document.CurrentTab;
        int groupCount = ColorGroupCount();

        _suppressColorPanelEvents = true;

        bool useCommon = !isFirstTab && currentTab.SetColorOverride is null;
        if (!isFirstTab) ColorCommonCheck.IsChecked = useCommon;

        bool editable = isFirstTab || !useCommon;
        var setSource = editable && !isFirstTab ? EnsureSetColors(currentTab, groupCount) : EnsureSetColors(tab0, groupCount);
        var frzSource = editable && !isFirstTab ? EnsureFrzColors(currentTab, groupCount) : EnsureFrzColors(tab0, groupCount);

        for (int g = 0; g < groupCount; g++)
            AddColorField(SetColorPanel, $"色グループ{g}", g < setSource.Count ? setSource[g] : "", editable, ("set", g, -1));

        // 2026-07-16l: defaultFrzColorUse(dos-h0063)がONの間、frzColorの指定は強制的にOFFにする
        // (本体側の既定フリーズアロー色セットが優先され、frzColorの値自体が無視されるため)。
        bool defaultFrzColorUse = _document.Project.ExtraHeaders.TryGetValue("defaultFrzColorUse", out var dfu) && dfu == "true";
        if (defaultFrzColorUse)
        {
            FrzColorPanel.Children.Add(new TextBlock
            {
                Text = "④タブのdefaultFrzColorUseが有効なため、frzColorは指定できません。",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }
        bool frzEditable = editable && !defaultFrzColorUse;

        for (int g = 0; g < groupCount; g++)
        {
            FrzColorPanel.Children.Add(new TextBlock
            {
                Text = $"色グループ{g}", FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, g == 0 ? 0 : 8, 0, 2),
            });
            for (int s = 0; s < 4; s++)
            {
                int idx = g * 4 + s;
                AddColorField(FrzColorPanel, FrzSlotLabels[s], idx < frzSource.Count ? frzSource[idx] : "", frzEditable, ("frz", g, s));
            }
        }

        _suppressColorPanelEvents = false;
    }

    private void ColorField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressColorPanelEvents || _document is null) return;
        var box = (TextBox)sender;
        if (!box.IsEnabled) return;
        var (kind, group, slot) = ((string Kind, int Group, int Slot))box.Tag;

        bool isFirstTab = _document.CurrentTabIndex == 0;
        var targetTab = isFirstTab ? _document.Project.Tabs[0] : _document.CurrentTab;

        if (kind == "set")
        {
            if (targetTab.SetColorOverride is null || group >= targetTab.SetColorOverride.Count) return;
            if (targetTab.SetColorOverride[group] == box.Text) return;
            targetTab.SetColorOverride[group] = box.Text;
        }
        else
        {
            int idx = group * 4 + slot;
            if (targetTab.FrzColorOverride is null || idx >= targetTab.FrzColorOverride.Count) return;
            if (targetTab.FrzColorOverride[idx] == box.Text) return;
            targetTab.FrzColorOverride[idx] = box.Text;
        }
        _document.NotifyChanged();
        Canvas.InvalidateVisual(); // レーン色プレビュー(LaneBrush)へ反映
    }

    private void ColorCommonCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressColorPanelEvents || _document is null || _document.CurrentTabIndex == 0) return;
        var tab = _document.CurrentTab;
        var tab0 = _document.Project.Tabs[0];
        int groupCount = ColorGroupCount();

        if (ColorCommonCheck.IsChecked == true)
        {
            tab.SetColorOverride = null;
            tab.FrzColorOverride = null;
        }
        else
        {
            tab.SetColorOverride = [.. EnsureSetColors(tab0, groupCount)];
            tab.FrzColorOverride = [.. EnsureFrzColors(tab0, groupCount)];
        }
        _document.NotifyChanged();
        RefreshColorPanel();
    }

    // =====================================================================
    // 右パネル④: その他のヘッダー機能(仕様書6.4.4)
    // =====================================================================

    /// <summary>ドキュメント読込時に一度だけ構築する(ExtraHeadersはプロジェクト共通・タブ非依存のため、
    /// タブ切替のたびに作り直す必要はない)。</summary>
    private void RefreshExtraHeadersPanel()
    {
        if (_document is null) return;
        ExtraHeadersPanel.Children.Clear();
        var headers = _document.Project.ExtraHeaders;

        string? lastCategory = null;
        foreach (var def in ExtraHeaderDefs.All)
        {
            if (def.Category != lastCategory)
            {
                ExtraHeadersPanel.Children.Add(new TextBlock
                {
                    Text = def.Category,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, lastCategory is null ? 0 : 14, 0, 6),
                });
                lastCategory = def.Category;
            }
            AddExtraHeaderRow(def, headers);
        }
    }

    private void AddExtraHeaderRow(HeaderParamDef def, Dictionary<string, string> headers)
    {
        bool hasValue = headers.TryGetValue(def.Name, out var existing);
        string initial = hasValue ? existing! : def.Default;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock { Text = def.Name, Width = 150, VerticalAlignment = VerticalAlignment.Center });

        if (def.Type == HeaderParamType.Bool)
        {
            // 真偽値パラメータは「使用する」チェックを持たず、チェック自体がON/OFF値を兼ねる
            // (ONならtrueとして出力、OFFなら未出力=エンジン既定値。2026-07-16g、簡略化の設計判断)。
            var boolCheck = new CheckBox { IsChecked = hasValue && existing == "true", VerticalAlignment = VerticalAlignment.Center };
            boolCheck.Checked += (_, _) => { headers[def.Name] = "true"; _document!.NotifyChanged(); };
            boolCheck.Unchecked += (_, _) => { headers.Remove(def.Name); _document!.NotifyChanged(); };

            if (def.Name == "defaultFrzColorUse")
            {
                // 2026-07-16l: defaultFrzColorUseがONになった場合、frzColorの指定を強制的にOFFにする
                // (dos-h0063の仕様上、true時はfrzColorの値自体が無視されるため。ONにした瞬間、
                // 全タブのFrzColorOverrideをクリアし、②タブのfrzColor入力欄も編集不可にする)。
                boolCheck.Checked += (_, _) =>
                {
                    foreach (var t in _document!.Project.Tabs) t.FrzColorOverride = null;
                    RefreshColorPanel();
                };
                boolCheck.Unchecked += (_, _) => RefreshColorPanel();
            }

            row.Children.Add(boolCheck);
            ExtraHeadersPanel.Children.Add(row);
            return;
        }

        var useCheck = new CheckBox { Content = "使用する", IsChecked = hasValue, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        row.Children.Add(useCheck);

        switch (def.Type)
        {
            case HeaderParamType.Dropdown:
                {
                    var combo = new ComboBox { ItemsSource = def.Options, Width = 140, IsEnabled = hasValue, SelectedItem = initial };
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (useCheck.IsChecked != true) return;
                        headers[def.Name] = combo.SelectedItem as string ?? def.Default;
                        _document!.NotifyChanged();
                    };
                    useCheck.Checked += (_, _) =>
                    {
                        combo.IsEnabled = true;
                        headers[def.Name] = combo.SelectedItem as string ?? def.Default;
                        _document!.NotifyChanged();
                    };
                    useCheck.Unchecked += (_, _) =>
                    {
                        combo.IsEnabled = false;
                        headers.Remove(def.Name);
                        _document!.NotifyChanged();
                    };
                    row.Children.Add(combo);
                    break;
                }

            case HeaderParamType.Color:
                {
                    var swatch = new Border
                    {
                        Width = 14, Height = 14, Margin = new Thickness(4, 0, 4, 0),
                        BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
                        Background = SafeColorBrush(initial),
                    };
                    var box = new TextBox { Width = 120, Text = initial, IsEnabled = hasValue };
                    box.TextChanged += (_, _) => swatch.Background = SafeColorBrush(box.Text);
                    box.LostFocus += (_, _) =>
                    {
                        if (useCheck.IsChecked != true) return;
                        headers[def.Name] = box.Text;
                        _document!.NotifyChanged();
                    };
                    useCheck.Checked += (_, _) =>
                    {
                        box.IsEnabled = true;
                        headers[def.Name] = box.Text;
                        _document!.NotifyChanged();
                    };
                    useCheck.Unchecked += (_, _) =>
                    {
                        box.IsEnabled = false;
                        headers.Remove(def.Name);
                        _document!.NotifyChanged();
                    };
                    row.Children.Add(swatch);
                    row.Children.Add(box);
                    break;
                }

            default: // Number / Text / Raw
                {
                    var box = new TextBox { Width = def.Type == HeaderParamType.Raw ? 220 : 140, Text = initial, IsEnabled = hasValue };
                    box.LostFocus += (_, _) =>
                    {
                        if (useCheck.IsChecked != true) return;
                        headers[def.Name] = box.Text;
                        _document!.NotifyChanged();
                    };
                    useCheck.Checked += (_, _) =>
                    {
                        box.IsEnabled = true;
                        headers[def.Name] = box.Text;
                        _document!.NotifyChanged();
                    };
                    useCheck.Unchecked += (_, _) =>
                    {
                        box.IsEnabled = false;
                        headers.Remove(def.Name);
                        _document!.NotifyChanged();
                    };
                    row.Children.Add(box);
                    break;
                }
        }

        ExtraHeadersPanel.Children.Add(row);
    }

    // =====================================================================
    // 右パネル①: プロジェクトのプロパティ(仕様書6.4.1)
    // =====================================================================

    /// <summary>現在のProject/CurrentTabの値をプロパティパネルへ反映する(ドキュメント読込・タブ切替時)。</summary>
    private void RefreshProjectPropertiesPanel()
    {
        if (_document is null) return;
        _suppressPropertyPanelEvents = true;

        var p = _document.Project;
        MusicTitleBox.Text = p.MusicTitle;
        ArtistNameBox.Text = p.ArtistName;
        ArtistUrlBox.Text = p.ArtistUrl;
        BpmBox.Text = (p.BpmEvents.Count > 0 ? p.BpmEvents[0].Bpm : 120).ToString(CultureInfo.InvariantCulture);
        StartNumberBox.Text = p.StartNumber.ToString(CultureInfo.InvariantCulture);
        StartFrameBox.Text = p.StartFrame.ToString(CultureInfo.InvariantCulture);
        BlankFrameBox.Text = p.BlankFrame.ToString(CultureInfo.InvariantCulture);
        MusicUrlBox.Text = p.MusicUrl;
        TuningBox.Text = p.Tuning;
        FrzAttemptBox.Text = p.FrzAttempt.ToString(CultureInfo.InvariantCulture);

        var tab = _document.CurrentTab;
        DifficultyNameBox.Text = tab.DifficultyName;
        InitialSpeedBox.Text = tab.InitialSpeed.ToString(CultureInfo.InvariantCulture);

        _suppressPropertyPanelEvents = false;

        UpdateRequiredFieldWarning(MusicTitleBox, MusicTitleWarning);
        UpdateRequiredFieldWarning(DifficultyNameBox, DifficultyNameWarning);
    }

    /// <summary>
    /// 必須項目の未入力表示(仕様書6.7): 空欄なら赤枠+警告テキストを出す共通ルール。
    /// 今後の他タブ(色設定・その他ヘッダー等)の必須項目もこの関数を使い回す想定。
    /// </summary>
    private static void UpdateRequiredFieldWarning(TextBox box, TextBlock warning)
    {
        bool isEmpty = string.IsNullOrWhiteSpace(box.Text);
        warning.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        box.BorderBrush = isEmpty ? Brushes.Red : SystemColors.ActiveBorderBrush;
        box.BorderThickness = new Thickness(isEmpty ? 2 : 1);
    }

    /// <summary>文字列項目: 入力の都度、即座にモデルへ反映する。</summary>
    private void ProjectStringField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressPropertyPanelEvents || _document is null) return;
        var box = (TextBox)sender;
        var p = _document.Project;
        var tab = _document.CurrentTab;

        if (box == MusicTitleBox)
        {
            p.MusicTitle = box.Text;
            UpdateRequiredFieldWarning(MusicTitleBox, MusicTitleWarning);
            ProjectTitleText.Text = $"{p.ProjectName} ({p.MusicTitle})";
        }
        else if (box == ArtistNameBox) p.ArtistName = box.Text;
        else if (box == ArtistUrlBox) p.ArtistUrl = box.Text;
        else if (box == MusicUrlBox) p.MusicUrl = box.Text;
        else if (box == TuningBox) p.Tuning = box.Text;
        else if (box == DifficultyNameBox)
        {
            tab.DifficultyName = box.Text;
            UpdateRequiredFieldWarning(DifficultyNameBox, DifficultyNameWarning);
            DifficultyTabControl.Items.Refresh(); // DifficultyTabはINotifyPropertyChanged非対応のため明示リフレッシュ
        }
        _document.NotifyChanged();
    }

    /// <summary>
    /// 数値項目: 入力の都度ではなくフォーカスが外れた時点で確定する(タイプ中の不完全な文字列で
    /// パースエラーを起こさないため)。パース失敗時はモデルを変更せず、表示だけ直前の値に戻す。
    /// </summary>
    private void ProjectNumericField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var box = (TextBox)sender;
        var p = _document.Project;
        var tab = _document.CurrentTab;
        bool ok;
        // 共通BPM・StartNumberはUndo履歴を通らない直接書換のため、フレーム情報モード中は
        // OnTimingChangedDirectlyで逆算再配置を行う(2026-07-17i)
        bool timingChangedDirectly = false;

        if (box == BpmBox)
        {
            ok = TryParseDouble(box.Text, out var v) && v > 0;
            if (ok && p.BpmEvents.Count > 0 && p.BpmEvents[0].Tick == 0)
            {
                p.BpmEvents[0] = p.BpmEvents[0] with { Bpm = v };
                timingChangedDirectly = true;
            }
        }
        else if (box == StartNumberBox)
        {
            ok = TryParseDouble(box.Text, out var v);
            if (ok) { p.StartNumber = v; timingChangedDirectly = true; }
        }
        else if (box == StartFrameBox)
        {
            ok = int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
            if (ok) p.StartFrame = v;
        }
        else if (box == BlankFrameBox)
        {
            ok = int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
            if (ok) p.BlankFrame = v;
        }
        else if (box == FrzAttemptBox)
        {
            ok = int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
            if (ok) p.FrzAttempt = v;
        }
        else if (box == InitialSpeedBox)
        {
            ok = TryParseDouble(box.Text, out var v) && v > 0;
            if (ok) tab.InitialSpeed = v;
        }
        else
        {
            ok = true;
        }

        if (!ok) { RefreshProjectPropertiesPanel(); return; } // 不正入力は直前の値に戻す
        if (timingChangedDirectly) _document.OnTimingChangedDirectly();
        else _document.NotifyChanged();
        Canvas.InvalidateVisual();
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // =====================================================================
    // 右パネル③: 選択中オブジェクトのプロパティ(仕様書6.4.3)
    // =====================================================================

    /// <summary>タブindex: 0=プロジェクト, 1=色設定, 2=オブジェクト, 3=その他(XAMLの並び順と対応)</summary>
    private const int ObjectTabIndex = 2;
    private const int ProjectTabIndex = 0;

    private void AutoSwitchToObjectTab()
    {
        if (ObjectTabPinCheck.IsChecked == true) return; // ピン留め中は自動切替しない(仕様書6.4.3)
        PropertyTabControl.SelectedIndex = ObjectTabIndex;
    }

    /// <summary>Document.Changed購読(選択状態を含む変化全般)のたびに呼ばれ、③タブの表示を同期する。</summary>
    private void RefreshSelectedObjectPanel()
    {
        UpdateStartFrameText(); // Changedイベントごとに再生開始フレーム表示も更新する(2026-07-17f、専用購読を増やさないための相乗り)
        if (_document is null) return;
        var sel = _document.Selection;

        if (sel.Count == 0)
        {
            _currentPropertyObject = null;
            ObjectNoSelectionText.Visibility = Visibility.Visible;
            ObjectMultiSelectText.Visibility = Visibility.Collapsed;
            ObjectDetailPanel.Visibility = Visibility.Collapsed;
            if (ObjectTabPinCheck.IsChecked != true && PropertyTabControl.SelectedIndex == ObjectTabIndex)
                PropertyTabControl.SelectedIndex = ProjectTabIndex; // 選択解除→プロジェクトタブへ自動復帰(仕様書6.4.3)
            return;
        }

        if (sel.Count > 1)
        {
            _currentPropertyObject = null;
            ObjectNoSelectionText.Visibility = Visibility.Collapsed;
            ObjectMultiSelectText.Text = $"{sel.Count}個のオブジェクトを選択中(複数選択時は個別編集非対応。移動・削除はキャンバス上の操作をご利用くださいませ)";
            ObjectMultiSelectText.Visibility = Visibility.Visible;
            ObjectDetailPanel.Visibility = Visibility.Collapsed;
            AutoSwitchToObjectTab();
            return;
        }

        // --- 単一選択 ---
        ObjectNoSelectionText.Visibility = Visibility.Collapsed;
        ObjectMultiSelectText.Visibility = Visibility.Collapsed;
        ObjectDetailPanel.Visibility = Visibility.Visible;
        AutoSwitchToObjectTab();

        var r = sel.Single();
        _currentPropertyObject = r;
        var engine = _document.Project.CreateTimingEngine();
        var tab = _document.CurrentTab;

        _suppressObjectPanelEvents = true;
        ObjectFrameLabel.Text = "Frame";
        ObjectFrameBox.IsEnabled = true;
        ObjectEndFrameLabel.Visibility = Visibility.Collapsed;
        ObjectEndFrameBox.Visibility = Visibility.Collapsed;
        ObjectValueLabel.Visibility = Visibility.Collapsed;
        ObjectValueBox.Visibility = Visibility.Collapsed;
        ObjectCommentLabel.Visibility = Visibility.Collapsed;
        ObjectCommentBox.Visibility = Visibility.Collapsed;

        switch (r.Kind)
        {
            case ObjectKind.Note:
                ObjectKindText.Text = "ノート";
                ObjectFrameBox.Text = FormatFrame(engine.TickToFrame(r.Tick));
                break;

            case ObjectKind.FreezeStart:
            case ObjectKind.FreezeEnd:
            case ObjectKind.FreezeBody:
                {
                    var f = tab.Lanes[r.Lane].Freezes.FirstOrDefault(x => x.StartTick == r.Tick);
                    ObjectKindText.Text = "フリーズアロー";
                    ObjectFrameLabel.Text = "Frame(開始)";
                    ObjectFrameBox.Text = f is null ? "-" : FormatFrame(engine.TickToFrame(f.StartTick));
                    ObjectEndFrameLabel.Visibility = Visibility.Visible;
                    ObjectEndFrameBox.Visibility = Visibility.Visible;
                    ObjectEndFrameBox.Text = f is null ? "-" : FormatFrame(engine.TickToFrame(f.EndTick));
                    break;
                }

            case ObjectKind.Speed:
                {
                    var ev = tab.SpeedEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    ObjectKindText.Text = "速度変更(speed_data)";
                    ObjectFrameBox.Text = FormatFrame(engine.TickToFrame(r.Tick));
                    ObjectValueLabel.Visibility = Visibility.Visible;
                    ObjectValueBox.Visibility = Visibility.Visible;
                    ObjectValueBox.Text = (ev?.Value ?? 0).ToString(CultureInfo.InvariantCulture);
                    break;
                }

            case ObjectKind.Boost:
                {
                    var ev = tab.BoostEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    ObjectKindText.Text = "ブースト変更(boost_data)";
                    ObjectFrameBox.Text = FormatFrame(engine.TickToFrame(r.Tick));
                    ObjectValueLabel.Visibility = Visibility.Visible;
                    ObjectValueBox.Visibility = Visibility.Visible;
                    ObjectValueBox.Text = (ev?.Value ?? 0).ToString(CultureInfo.InvariantCulture);
                    break;
                }

            case ObjectKind.Bpm:
                {
                    var ev = _document.Project.BpmEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    bool isFixed = r.Tick == 0;
                    ObjectKindText.Text = isFixed ? "BPM変更(曲頭・移動/削除不可)" : "BPM変更";
                    ObjectFrameBox.Text = FormatFrame(engine.TickToFrame(r.Tick));
                    ObjectFrameBox.IsEnabled = !isFixed;
                    ObjectValueLabel.Visibility = Visibility.Visible;
                    ObjectValueBox.Visibility = Visibility.Visible;
                    ObjectValueBox.Text = (ev?.Bpm ?? 120).ToString(CultureInfo.InvariantCulture);
                    break;
                }

            case ObjectKind.Marker:
                {
                    var m = _document.Project.Markers.FirstOrDefault(x => x.Tick == r.Tick);
                    ObjectKindText.Text = "マーカー";
                    ObjectFrameBox.Text = FormatFrame(engine.TickToFrame(r.Tick));
                    ObjectCommentLabel.Visibility = Visibility.Visible;
                    ObjectCommentBox.Visibility = Visibility.Visible;
                    ObjectCommentBox.Text = m?.Comment ?? "";
                    break;
                }

            case ObjectKind.TimeSignature:
                {
                    var sig = _document.Project.TimeSignatures.FirstOrDefault(s => s.MeasureIndex == r.Tick);
                    ObjectKindText.Text = sig is null
                        ? "拍子(データ取得失敗)"
                        : $"拍子 {sig.Numerator}/{sig.Denominator}(小節番号{sig.MeasureIndex}・このタブでの編集は今回未対応)";
                    ObjectFrameLabel.Text = "小節番号";
                    ObjectFrameBox.Text = r.Tick.ToString();
                    ObjectFrameBox.IsEnabled = false;
                    break;
                }
        }
        _suppressObjectPanelEvents = false;
    }

    private static string FormatFrame(double frame) => frame.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>フリーズの選択参照を、リサイズ後の実際の開始tickへ更新する(ResizeFreezeActionはSelectionを
    /// 更新しないため、③タブが古いtickを指したままにならないよう明示的に合わせる)。</summary>
    private void ReselectFreeze(int lane, long newStartTick)
    {
        _document!.Selection.Clear();
        _document.Selection.Add(new ObjectRef(ObjectKind.FreezeStart, lane, newStartTick));
        _document.NotifyChanged();
    }

    private void ObjectFrame_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;
        if (!TryParseDouble(ObjectFrameBox.Text, out var frame)) { RefreshSelectedObjectPanel(); return; }

        var engine = _document.Project.CreateTimingEngine();
        long newTick = _document.Snap.Snap(engine.FrameToTick(frame));

        if (r.Kind is ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody)
        {
            var f = _document.CurrentTab.Lanes[r.Lane].Freezes.FirstOrDefault(x => x.StartTick == r.Tick);
            if (f is null) { RefreshSelectedObjectPanel(); return; }
            if (newTick == f.StartTick) return;
            _document.Execute(new ResizeFreezeAction(r.Lane, f, newTick, f.EndTick));
            var moved = _document.CurrentTab.Lanes[r.Lane].Freezes.First(x => x.EndTick == f.EndTick || x.StartTick == newTick);
            ReselectFreeze(r.Lane, moved.StartTick);
            return;
        }

        if (r.Kind == ObjectKind.Bpm && r.Tick == 0) { RefreshSelectedObjectPanel(); return; } // 移動不可(不変条件)

        long tickDelta = newTick - r.Tick;
        if (tickDelta == 0) return;
        _document.Execute(new MoveObjectsAction([r], 0, tickDelta));
    }

    private void ObjectEndFrame_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;
        if (r.Kind is not (ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody)) return;
        if (!TryParseDouble(ObjectEndFrameBox.Text, out var frame)) { RefreshSelectedObjectPanel(); return; }

        var f = _document.CurrentTab.Lanes[r.Lane].Freezes.FirstOrDefault(x => x.StartTick == r.Tick);
        if (f is null) { RefreshSelectedObjectPanel(); return; }

        var engine = _document.Project.CreateTimingEngine();
        long newTick = _document.Snap.Snap(engine.FrameToTick(frame));
        if (newTick == f.EndTick) return;

        _document.Execute(new ResizeFreezeAction(r.Lane, f, f.StartTick, newTick));
        var moved = _document.CurrentTab.Lanes[r.Lane].Freezes.FirstOrDefault(x => x.StartTick == f.StartTick)
                    ?? _document.CurrentTab.Lanes[r.Lane].Freezes.First();
        ReselectFreeze(r.Lane, moved.StartTick);
    }

    private void ObjectValue_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;
        if (!TryParseDouble(ObjectValueBox.Text, out var v)) { RefreshSelectedObjectPanel(); return; }

        switch (r.Kind)
        {
            case ObjectKind.Speed:
                _document.Execute(new CompositeEditAction(
                    [new DeleteValueEventAction(ValueEventKind.Speed, r.Tick), new PlaceValueEventAction(ValueEventKind.Speed, r.Tick, v)],
                    "速度変更値編集"));
                break;

            case ObjectKind.Boost:
                _document.Execute(new CompositeEditAction(
                    [new DeleteValueEventAction(ValueEventKind.Boost, r.Tick), new PlaceValueEventAction(ValueEventKind.Boost, r.Tick, v)],
                    "ブースト変更値編集"));
                break;

            case ObjectKind.Bpm when r.Tick == 0:
                // tick0のBPMはDelete/Place系アクションの不変条件で弾かれるため直接書き換える
                // (右パネル①のプロジェクト共通BPM欄と同じ扱い、Undo非対応)。
                if (v > 0)
                {
                    var idx = _document.Project.BpmEvents.FindIndex(x => x.Tick == 0);
                    if (idx >= 0)
                    {
                        _document.Project.BpmEvents[idx] = _document.Project.BpmEvents[idx] with { Bpm = v };
                        _document.OnTimingChangedDirectly(); // フレーム情報モード中は全オブジェクトを逆算再配置(2026-07-17i)
                    }
                }
                else RefreshSelectedObjectPanel();
                break;

            case ObjectKind.Bpm:
                if (v > 0)
                {
                    _document.Execute(new CompositeEditAction(
                        [new DeleteValueEventAction(ValueEventKind.Bpm, r.Tick), new PlaceValueEventAction(ValueEventKind.Bpm, r.Tick, v)],
                        "BPM変更値編集"));
                }
                else RefreshSelectedObjectPanel();
                break;
        }
    }

    private void ObjectComment_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null) return;
        if (_currentPropertyObject is not { Kind: ObjectKind.Marker } r) return;

        var m = _document.Project.Markers.FirstOrDefault(x => x.Tick == r.Tick);
        if (m is null || m.Comment == ObjectCommentBox.Text) return; // 変更なしならUndo履歴を汚さない
        _document.Execute(new CompositeEditAction(
            [new DeleteMarkerAction(r.Tick), new PlaceMarkerAction(r.Tick, ObjectCommentBox.Text)],
            "マーカーコメント編集"));
    }

    /// <summary>波形表示トグル(2026-07-18)。初回ONで音声をバックグラウンドデコードする</summary>
    private void WaveformToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        bool on = WaveformToggle.IsChecked == true;
        Canvas.ShowWaveform = on;
        if (on)
        {
            if (!_audioLoaded)
            {
                MessageBox.Show(this, "音楽ファイルが読み込まれていませんの。波形表示には音楽の読み込みが必要ですわ。", "波形表示", MessageBoxButton.OK, MessageBoxImage.Information);
                WaveformToggle.IsChecked = false;
                return;
            }
            EnsureWaveformDecoded();
        }
        Canvas.InvalidateVisual();
    }

    /// <summary>音声ファイルをデコードして波形ピークを用意する(非同期、結果はキャッシュ)(2026-07-18)</summary>
    private async void EnsureWaveformDecoded()
    {
        if (_document is null || _waveformDecoding) return;
        var path = _document.Project.AudioFilePath;
        if (path == _waveformPath && _waveformPeaks is not null)
        {
            Canvas.Waveform = _waveformPeaks;
            Canvas.InvalidateVisual();
            return;
        }
        _waveformDecoding = true;
        try
        {
            var peaks = await System.Threading.Tasks.Task.Run(() => WaveformDecoder.Decode(path));
            _waveformPeaks = peaks;
            _waveformPath = path;
            Canvas.Waveform = peaks;
            Canvas.InvalidateVisual();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"波形の解析に失敗しましたわ: {ex.Message}\n(ogg等、未対応の形式の可能性がありますの)", "波形表示", MessageBoxButton.OK, MessageBoxImage.Warning);
            WaveformToggle.IsChecked = false;
            Canvas.ShowWaveform = false;
        }
        finally
        {
            _waveformDecoding = false;
        }
    }

    /// <summary>StartNumber編集モード切替(2026-07-18、要望メモ07-15項目8)。
    /// ON中は通常編集無効・ドラッグ=StartNumber調整・クリック=ガイド線。波形は自動ON(手動OFF可)。
    /// フレーム情報モードとは排他(フレーム固定とStartNumber移動は意味が衝突するため)。</summary>
    private void StartNumberEditToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        bool on = StartNumberEditToggle.IsChecked == true;
        if (on)
        {
            if (_document is null)
            {
                StartNumberEditToggle.IsChecked = false;
                return;
            }
            if (_document.IsFrameEditMode)
            {
                MessageBox.Show(this, "フレーム情報モード中はStartNumber編集モードに切り替えられませんの。先にフレーム情報モードを終了してくださいまし。", "StartNumber編集", MessageBoxButton.OK, MessageBoxImage.Information);
                StartNumberEditToggle.IsChecked = false;
                return;
            }
            Canvas.StartNumberEditMode = true;
            // モード依存デフォルト: 波形は自動ON(要望メモ07-15項目7。手動でOFFにも戻せる)
            if (WaveformToggle.IsChecked != true && _audioLoaded) WaveformToggle.IsChecked = true;
        }
        else
        {
            Canvas.StartNumberEditMode = false;
            RefreshProjectPropertiesPanel(); // StartNumber数値表示を最終同期
        }
        Canvas.InvalidateVisual();
    }

    /// <summary>拍情報/フレーム情報モード切替(仕様書7.6、2026-07-17i)。OFF時は丸め衝突を検査し、
    /// 「統合して続行」か「フレーム情報モードに留まる」かをユーザーが選ぶ(確定仕様)。</summary>
    private void FrameEditToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _document is null)
        {
            if (_document is null) FrameEditToggle.IsChecked = false;
            return;
        }
        bool on = FrameEditToggle.IsChecked == true;
        if (on == _document.IsFrameEditMode) return; // 再入(IsChecked書き戻し時)ガード

        if (on)
        {
            if (Canvas.StartNumberEditMode)
            {
                MessageBox.Show(this, "StartNumber編集モード中はフレーム情報モードに切り替えられませんの。先にStartNumber編集を終了してくださいまし。", "フレーム情報モード", MessageBoxButton.OK, MessageBoxImage.Information);
                FrameEditToggle.IsChecked = false;
                return;
            }
            _document.EnterFrameEditMode();
        }
        else
        {
            var collisions = _document.FindFrameEditCollisions();
            if (collisions.Count > 0)
            {
                var head = string.Join("\n", collisions.Take(10));
                var more = collisions.Count > 10 ? $"\n…ほか{collisions.Count - 10}件" : "";
                var r = MessageBox.Show(this,
                    $"丸め込みにより同一位置へ重なったオブジェクトがありますわ:\n{head}{more}\n\n" +
                    "「はい」= 重複を統合して拍情報モードへ戻る(統合はUndo可能)\n「いいえ」= フレーム情報モードに留まる",
                    "フレーム情報モード終了", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes)
                {
                    FrameEditToggle.IsChecked = true; // モード継続
                    return;
                }
                _document.ExitFrameEditMode(mergeDuplicates: true);
            }
            else
            {
                _document.ExitFrameEditMode(mergeDuplicates: false);
            }
        }
        Canvas.InvalidateVisual();
    }

    private void SnapToggle_Changed(object sender, RoutedEventArgs e) => ApplySnapToDocument();
    private void SnapDivision_Changed(object sender, SelectionChangedEventArgs e) => ApplySnapToDocument();

    private void ApplySnapToDocument()
    {
        if (_document is null) return;
        _document.Snap.Enabled = SnapEnabledCheck.IsChecked == true;
        if (SnapDivisionCombo.SelectedItem is int division) _document.Snap.Division = division;
        Canvas.InvalidateVisual();
    }

    // =====================================================================
    // スクロール連動(ChartCanvasの可視範囲カリング用)
    // =====================================================================

    private void ChartScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var rect = new Rect(ChartScrollViewer.HorizontalOffset, ChartScrollViewer.VerticalOffset,
            ChartScrollViewer.ViewportWidth, ChartScrollViewer.ViewportHeight);
        Canvas.UpdateViewport(rect);
    }

    // =====================================================================
    // ショートカット(仕様書13章): Ctrl+S/E/Z/Y
    // =====================================================================

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_document is null) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // テキスト入力中はエディタショートカット(Delete/BackSpace/Space等)を奪わない
        // (2026-07-17f、未解決事項§2-3の条件「フォーカスがテキストボックスに無い」)。
        // Ctrl系ショートカットは入力欄フォーカス中でも有効のまま(一般的なエディタの慣習)。
        bool textInputFocused = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;

        if (ctrl)
        {
            if (e.Key == Key.Z) { _document.Undo(); Canvas.InvalidateVisual(); e.Handled = true; }
            else if (e.Key == Key.Y) { _document.Redo(); Canvas.InvalidateVisual(); e.Handled = true; }
            else if (e.Key == Key.S) { SaveProject_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.E) { ExportDos_Click(this, new RoutedEventArgs()); e.Handled = true; }
            // --- 2026-07-17f: マウスモードのショートカット追加(Ctrl系) ---
            else if (e.Key == Key.Home) { ChartScrollViewer.ScrollToVerticalOffset(0); e.Handled = true; } // 譜面先頭へ
            else if (e.Key == Key.End) { ScrollToLastNote(); e.Handled = true; } // 末尾ノートを画面中央へ
            else if (e.Key == Key.Space && _visualTestActive) { StopVisualTest(returnToStart: false); e.Handled = true; } // 現在位置で終了
            else if (e.Key == Key.P) { StartPlaytest(); e.Handled = true; } // 2026-07-17g: プレイテスト開始(仕様書12.2)
            return;
        }

        if (textInputFocused) return;

        // --- 2026-07-17f: 修飾なしキー(テキスト入力中は無効) ---
        switch (e.Key)
        {
            case Key.PageUp: // 1画面分上へ
                ChartScrollViewer.ScrollToVerticalOffset(Math.Max(0, ChartScrollViewer.VerticalOffset - ChartScrollViewer.ViewportHeight));
                e.Handled = true;
                break;
            case Key.PageDown: // 1画面分下へ
                ChartScrollViewer.ScrollToVerticalOffset(ChartScrollViewer.VerticalOffset + ChartScrollViewer.ViewportHeight);
                e.Handled = true;
                break;
            case Key.Delete: // 選択中オブジェクトの削除(未解決事項§2-3)
                if (_controller is not null && _controller.DeleteSelection()) Canvas.InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Back: // 再生開始フレームのリセット
                if (_document.Project.PlaybackStartFrame is not null)
                {
                    _document.Project.PlaybackStartFrame = null;
                    _document.NotifyChanged();
                }
                e.Handled = true;
                break;
            case Key.Space: // 目視テスト開始/終了(終了後はテスト開始位置へ復帰)
                ToggleVisualTest();
                e.Handled = true; // 再生ボタン等のフォーカス誤発火防止(要望メモ07-15の注意点)
                break;
        }
    }

    // =====================================================================
    // 目視テスト(Space開始/終了、2026-07-17f)と位置表示ヘルパ
    // =====================================================================

    /// <summary>tick位置を「tick / frame / 秒」の複合表記にする(上部パネル表示用、2026-07-17f)</summary>
    private string FormatTickPos(double tick)
    {
        if (_document is null) return "-";
        var engine = _document.Project.CreateTimingEngine();
        long t = (long)Math.Round(tick);
        double frame = engine.TickToFrame(t);
        return $"{t}t / {frame:0.0}f / {frame / 60.0:0.00}s";
    }

    /// <summary>上部パネルの再生開始フレーム表示を更新する(2026-07-17f)</summary>
    private void UpdateStartFrameText()
    {
        if (_document?.Project.PlaybackStartFrame is { } f)
        {
            var engine = _document.Project.CreateTimingEngine();
            StartFramePosText.Text = $"{(long)Math.Round(engine.FrameToTick(f))}t / {f:0.0}f / {f / 60.0:0.00}s";
        }
        else
        {
            StartFramePosText.Text = "-";
        }
    }

    /// <summary>Ctrl+End: 全レーン中の末尾ノート(通常ノート/フリーズ終端の最大tick)を画面中央へ(2026-07-17f)</summary>
    private void ScrollToLastNote()
    {
        if (_document is null) return;
        long maxTick = -1;
        foreach (var lane in _document.CurrentTab.Lanes)
        {
            foreach (var t in lane.Notes) maxTick = Math.Max(maxTick, t);
            foreach (var f in lane.Freezes) maxTick = Math.Max(maxTick, f.EndTick);
        }
        if (maxTick < 0) return; // ノートが1つも無ければ何もしない
        double y = _document.CurrentLayout.TickToY(maxTick);
        ChartScrollViewer.ScrollToVerticalOffset(Math.Max(0, y - ChartScrollViewer.ViewportHeight / 2));
    }

    private void ToggleVisualTest()
    {
        if (_visualTestActive) StopVisualTest(returnToStart: true);
        else StartVisualTest();
    }

    /// <summary>目視テスト開始: 再生開始フレーム(未設定なら曲頭)から音楽再生+再生位置ライン表示(2026-07-17f)</summary>
    private void StartVisualTest()
    {
        if (_document is null) return;
        if (!_audioLoaded)
        {
            MessageBox.Show(this, "音楽ファイルが読み込まれていませんの。目視テストには音楽の読み込みが必要ですわ。", "目視テスト", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        double startFrame = _document.Project.PlaybackStartFrame ?? 0;
        _audioPlayer.Position = TimeSpan.FromSeconds(startFrame / 60.0);
        _audioPlayer.Play();
        _playbackTimer.Start();
        _visualTestActive = true;
    }

    /// <summary>目視テスト終了。returnToStart=true(Space)なら「表示範囲の一番上が再生開始フレームの
    /// 1小節前」までスクロールを戻す(未解決事項§2-2の終了時挙動)。false(Ctrl+Space)なら
    /// 現在の再生位置表示ラインの位置に留まる(未解決事項§2-1派生の要望)。</summary>
    private void StopVisualTest(bool returnToStart)
    {
        _visualTestActive = false;
        _audioPlayer.Stop();
        _playbackTimer.Stop();
        Canvas.PlaybackTick = null;
        Canvas.InvalidateVisual();
        AudioTimeText.Text = "-";

        if (!returnToStart || _document is null) return;
        ReturnScrollToStartFrame();
    }

    /// <summary>「表示範囲の一番上が再生開始フレームの1小節前」までスクロールを戻す
    /// (未解決事項§2-2の終了時挙動。目視テストSpace終了とプレイテスト終了で共用、2026-07-17g)</summary>
    private void ReturnScrollToStartFrame()
    {
        if (_document is null) return;
        var engine = _document.Project.CreateTimingEngine();
        double startFrame = _document.Project.PlaybackStartFrame ?? 0;
        long startTick = Math.Max(0, (long)Math.Round(engine.FrameToTick(startFrame)));
        var (measure, _) = engine.TickToMeasurePosition(startTick);
        long topTick = engine.MeasureStartTick(Math.Max(0, measure - 1));
        ChartScrollViewer.ScrollToVerticalOffset(Math.Max(0, _document.CurrentLayout.TickToY(topTick)));
    }

    // =====================================================================
    // プレイテスト(Ctrl+P、仕様書12.2、2026-07-17g)
    // =====================================================================

    private static double PlaytestHiSpeedValues_Nearest(double v) =>
        Math.Clamp(Math.Round(v * 4) / 4, 0.25, 10.0); // 0.25刻み(2026-07-19)

    /// <summary>ウィンドウサイズ倍率の選択肢(x0.5〜3、2026-07-17h)</summary>
    private static readonly List<double> PlaytestScaleValues = [0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    /// <summary>上部パネルのプレイテスト設定変更をAppSettingsへ保存する</summary>
    private void PlaytestSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _appSettings.PlaytestReverse = PlaytestReverseCheck.IsChecked == true;
        if (PlaytestHiSpeedCombo.SelectedItem is double hs) _appSettings.PlaytestHiSpeed = hs;
        if (PlaytestScaleCombo.SelectedItem is double sc) _appSettings.PlaytestWindowScale = sc;
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    private void PlaytestOffset_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (double.TryParse(PlaytestOffsetBox.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ofs))
        {
            _appSettings.PlaytestOffsetFrames = ofs;
            _appSettings.Save(AppPaths.SettingsFilePath);
        }
        else
        {
            PlaytestOffsetBox.Text = _appSettings.PlaytestOffsetFrames.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>タイトルバー表示: 「プロジェクト名 (*) - ダンおに譜面エディタ」(2026-07-19b、未解決事項§2-6)</summary>
    private void UpdateWindowTitle()
    {
        var name = _document?.Project.ProjectName ?? "";
        var star = _document?.IsModified == true ? " *" : "";
        Title = string.IsNullOrEmpty(name) ? "ダンおに譜面エディタ" : $"{name}{star} - ダンおに譜面エディタ";
    }

    /// <summary>未保存の変更がある場合の終了確認(未解決事項§2-6、環境設定でON/OFF可、2026-07-19b)</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_document?.IsModified != true || !_appSettings.ConfirmUnsavedOnClose) return;

        var r = MessageBox.Show(this,
            "未保存の変更がありますわ。保存してから終了しますか?\n\n「はい」= 保存して終了\n「いいえ」= 保存せず終了\n「キャンセル」= 終了を中止",
            "終了の確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (r == MessageBoxResult.Yes)
        {
            SaveProject_Click(this, new RoutedEventArgs());
            if (_document.IsModified) e.Cancel = true; // 保存ダイアログをキャンセルした場合は終了も中止
        }
    }

    private void StartPlaytest()
    {
        if (_document is null) return;
        if (!_audioLoaded)
        {
            MessageBox.Show(this, "音楽ファイルが読み込まれていませんの。プレイテストには音楽の読み込みが必要ですわ。", "プレイテスト", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_visualTestActive) StopVisualTest(returnToStart: false); // 目視テスト中なら止めてから

        var win = new PlaytestWindow(
            _document,
            _appSettings.PlaytestReverse,
            _appSettings.PlaytestHiSpeed,
            _appSettings.PlaytestOffsetFrames,
            _document.Project.PlaybackStartFrame ?? 0,
            _appSettings.PlaytestWindowScale)
        { Owner = this };
        win.ShowDialog();

        // 終了後はテスト開始位置に戻る(Space目視テスト終了と同じ挙動、ユーザー確定仕様)
        ReturnScrollToStartFrame();
    }
}
