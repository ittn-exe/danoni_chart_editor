using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DanoniEditor.App;

/// <summary>
/// マーカーコメントのホバーポップアップ(2026-08-08新設)。マーカーレーンの表示幅
/// (ChartLayout.MarkerColWidth)は狭く、DrawEventTagのクリップ描画では長いコメントが
/// 読み切れない(「見づらい」)との指摘への対応。マーカーへマウスホバーした際に、
/// コメント全文を表示するポップアップを出す。
///
/// ColorPickerPopup(2026-08-08新設)と同じくPopup(AllowsTransparency)で実装し、以下2点を
/// 同じ操作感で提供する。
/// - ドラッグによる自由移動: ヘッダーのタイトル部分をドラッグするとポップアップ位置を動かせる
///   (Popup自体はドラッグに対応しないため、HorizontalOffset/VerticalOffsetを手動で追従させる)。
/// - ピン留め: 「固定」ボタンでStaysOpenを切り替える。ピン留め中はホバー対象が別のマーカーへ
///   移っても閉じ替えない(内容を見比べたい/メモを取りたい間、開きっぱなしにできる用途を想定)。
///
/// 2026-08-08b要望対応: 「ポップアップ内のボタンを押したいのにマウスに追従して/消えてしまう」
/// 指摘への対応で以下2点を追加した。
/// - マウス追従の廃止: PlacementMode.MouseをやめPlacementMode.Relativeに変更し、マーカーの
///   実座標(呼び出し側でChartLayoutから計算したanchorPoint)に固定表示する。以後カーソルが
///   動いてもポップアップの位置自体は動かない(ドラッグ移動は従来通り可能)。
/// - 消去の猶予(hover-intent): マーカーから離れた瞬間に即座に閉じるのではなく、ScheduleHideで
///   一定時間(HideDelay)だけ猶予を持たせる。その間にポップアップ自体へカーソルが辿り着けば
///   (MouseEnterでキャンセル)閉じない。マーカー→ポップアップ内ボタンへカーソルを動かす一瞬の間、
///   当たり判定上は「マーカーからもポップアップからも外れている」状態を許容するための仕組み。
///
/// 状態(現在開いているPopup・対象tick・ピン留め有無・保留中の消去タイマー)はstaticフィールドで
/// 保持する。譜面ビューは分割表示(Canvas/Canvas2)で2つ存在しうるが、マーカーコメントポップアップは
/// 常に1つだけ表示すれば十分なため(同時に別々のマーカーを指すポップアップが2つ出ても紛らわしいだけ)、
/// クラス全体で単一インスタンスに正規化している。
/// </summary>
internal static class MarkerCommentPopup
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(400);

    private static Popup? _current;
    private static long? _currentTick;
    private static bool _pinned;
    private static DispatcherTimer? _hideTimer;

    /// <summary>ピン留め中でなければ、指定tickのマーカー用ポップアップを(必要なら)開く。
    /// 既に同じtickのポップアップが開いている場合は何もしない(ホバーのたびに再生成してちらつくのを防ぐ)。
    /// ピン留め中は、別のマーカーへホバーが移っても何もしない(固定した内容を保持する)。
    /// anchorPointはanchor(ChartCanvas)のローカル座標系におけるマーカーの表示位置
    /// (呼び出し側でChartLayout.Column(ColumnKind.Marker)とTickToYから計算する)。</summary>
    public static void Show(FrameworkElement anchor, long tick, string comment, Point anchorPoint)
    {
        CancelScheduledHide(); // 新しく開く(または同じマーカーに留まる)以上、保留中の消去は取り消す

        if (_current is { IsOpen: true })
        {
            if (_currentTick == tick) return; // 同じマーカーに居続ける間は再生成しない
            if (_pinned) return; // ピン留め中は他のマーカーへホバーしても切り替えない
            _current.IsOpen = false;
        }

        var popup = new Popup
        {
            PlacementTarget = anchor,
            // 2026-08-08b: マウス追従(PlacementMode.Mouse)をやめ、マーカーの実座標に固定表示する。
            Placement = PlacementMode.Relative,
            HorizontalOffset = anchorPoint.X + 14,
            VerticalOffset = anchorPoint.Y - 10,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        // --- ヘッダー(タイトル=ドラッグハンドル、固定トグル、閉じるボタン)。ColorPickerPopupと同一パターン ---
        var titleText = new TextBlock
        {
            Text = "マーカーコメント", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.SizeAll,
        };
        var pinBtn = new Button { Content = "固定", Width = 44, Margin = new Thickness(4, 0, 0, 0) };
        var closeBtn = new Button { Content = "×", Width = 22, Margin = new Thickness(4, 0, 0, 0) };
        var header = new DockPanel { Margin = new Thickness(6, 6, 6, 4) };
        DockPanel.SetDock(closeBtn, Dock.Right);
        DockPanel.SetDock(pinBtn, Dock.Right);
        header.Children.Add(closeBtn);
        header.Children.Add(pinBtn);
        header.Children.Add(titleText);

        pinBtn.Click += (_, _) =>
        {
            _pinned = !_pinned;
            pinBtn.Content = _pinned ? "固定中" : "固定";
            popup.StaysOpen = _pinned;
            if (_pinned) CancelScheduledHide();
        };
        closeBtn.Click += (_, _) => popup.IsOpen = false;

        // ドラッグ移動: Popup自体はドラッグに対応しないため、オーナーウィンドウ基準のマウス座標の
        // 差分をHorizontalOffset/VerticalOffsetへ加算する(ColorPickerPopupと同一手法)。
        var ownerWindow = Window.GetWindow(anchor) ?? Application.Current.MainWindow;
        Point? dragStart = null;
        double dragStartH = 0, dragStartV = 0;
        titleText.MouseLeftButtonDown += (_, e) =>
        {
            if (ownerWindow is null) return;
            dragStart = e.GetPosition(ownerWindow);
            dragStartH = popup.HorizontalOffset;
            dragStartV = popup.VerticalOffset;
            titleText.CaptureMouse();
            e.Handled = true;
        };
        titleText.MouseMove += (_, e) =>
        {
            if (dragStart is not { } start || ownerWindow is null) return;
            var cur = e.GetPosition(ownerWindow);
            popup.HorizontalOffset = dragStartH + (cur.X - start.X);
            popup.VerticalOffset = dragStartV + (cur.Y - start.Y);
        };
        titleText.MouseLeftButtonUp += (_, _) =>
        {
            dragStart = null;
            titleText.ReleaseMouseCapture();
        };

        var commentText = new TextBlock
        {
            Text = comment,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            Margin = new Thickness(8, 0, 8, 8),
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(commentText);

        var rootBorder = new Border
        {
            Background = Brushes.White, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
            Child = root,
        };
        // 2026-08-08b: ポップアップ自体にカーソルが乗っている間は消去タイマーを止め、離れたら
        // 再度猶予つきで消去をスケジュールする(マーカーの当たり判定を離れてポップアップへ
        // カーソルを動かす一瞬をブリッジするための仕組み)。
        rootBorder.MouseEnter += (_, _) => CancelScheduledHide();
        rootBorder.MouseLeave += (_, _) => ScheduleHide();
        popup.Child = rootBorder;

        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, popup)) { _current = null; _currentTick = null; _pinned = false; }
            CancelScheduledHide();
        };

        _current = popup;
        _currentTick = tick;
        _pinned = false;
        popup.IsOpen = true;
    }

    /// <summary>ホバー対象がマーカーから外れた際に呼ぶ。ピン留め中は何もしない。即座には閉じず、
    /// HideDelay(400ms)だけ猶予を持たせてから閉じる。猶予中にマーカーへ戻る(Show再呼び出しで
    /// CancelScheduledHideされる)か、ポップアップ自体へカーソルが辿り着けば(MouseEnter)
    /// キャンセルされ、開いたままになる。</summary>
    public static void ScheduleHide()
    {
        if (_current is not { IsOpen: true } || _pinned) return;
        CancelScheduledHide();
        _hideTimer = new DispatcherTimer { Interval = HideDelay };
        _hideTimer.Tick += (_, _) =>
        {
            CancelScheduledHide();
            if (_current is { IsOpen: true } p && !_pinned) p.IsOpen = false;
        };
        _hideTimer.Start();
    }

    private static void CancelScheduledHide()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
    }

    /// <summary>猶予無しで即座に閉じる(2026-09-07要望対応)。マーカー付近でのクリック・ドラッグ開始時に
    /// 呼び、開いたままのポップアップが後続の操作(マウスキャプチャやヒットテスト)に干渉するのを防ぐ。
    /// ScheduleHideと同じくピン留め中は閉じない(明示的に固定した内容を意図せず消さないため)。</summary>
    public static void HideImmediately()
    {
        CancelScheduledHide();
        if (_current is { IsOpen: true } p && !_pinned) p.IsOpen = false;
    }
}
