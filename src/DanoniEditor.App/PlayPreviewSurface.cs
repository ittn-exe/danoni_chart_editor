using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Settings;
using DanoniEditor.Editing;

namespace DanoniEditor.App;

/// <summary>
/// プレイ画面プレビュー(2026-07-29要望対応、右パネル「プレビュー」タブ)。プレイテスト画面の見た目を
/// そのまま右パネルへ再現する仮想スナップショット。PlaytestWindow.PlaySurfaceと違い判定エンジン
/// (PlaytestEngine)は持たないが、「再生開始ラインより手前のノート・フリーズは判定済み(このプレイ
/// テストでは登場しない)ものとして扱う」という簡易ルールで表示・非表示を判定する(2026-07-29要望対応、
/// 詳細はOnRender参照)。フリーズは始点のみが再生開始ラインより手前の場合、始点ノートを非表示にし、
/// 帯をステップゾーン中央(stepY)までクランプして描く(=既にホールド中の見た目)。
/// 仮想画面の範囲外には一切描写しない(ClipToBounds)。
/// 幅・スクロール速度の計算式はPlaytestWindowコンストラクタと同一(本家danoni_main.js準拠)。
/// CurrentFrameを更新するたびに再描画する。ドキュメント・タブ・環境設定が変わった際はRebuildを、
/// 再生開始ラインだけが変わった際はSetStartFrameを呼ぶこと。
/// </summary>
internal sealed class PlayPreviewSurface : FrameworkElement
{
    private const double ArrowSize = 50; // C_ARW_WIDTH(本家準拠、PlaytestWindow.ArrowSizeと同じ)

    private EditorDocument? _doc;
    private KeyTemplate? _template;
    private bool _reverse;
    private double _hiSpeed = 1.0;
    private double _offsetFrames;
    private double _playingWidth = 600, _playingHeight = 500;
    private double _baseScrollSpeed = 1.0;
    private double _stepYTop = 35, _stepYBottom = 465;
    private List<(double Frame, double Value)> _speedBreaks = [];
    private List<(double Frame, double Value)> _boostBreaks = [];

    private double _currentFrame;

    /// <summary>再生開始ライン(frame)。judging用の基準として、これより手前のノート・フリーズを
    /// 非表示にする(2026-07-29要望対応)。CurrentFrame(スクロール位置)とは独立して管理する
    /// (目視テスト中はCurrentFrameだけが進み、こちらはテスト開始時点の値に固定されたままにする)。</summary>
    private double _startFrame;

    /// <summary>ノートの表示期限モード(2026-07-29要望対応、環境設定「プレビュー」参照)。
    /// false=「ステップゾーンを通過するまで」(既定、frame &lt; 再生開始ラインで非表示)、
    /// true=「ステップゾーンに重なるまで」(frame &lt;= 再生開始ラインで非表示、frame=再生開始ラインも消す)。</summary>
    private bool _noteExpiryIncludesEqual;

    public PlayPreviewSurface()
    {
        // 2026-07-29要望対応: 仮想画面の範囲外(Width/Heightの外)には一切描写しない
        ClipToBounds = true;
    }

    /// <summary>現在の再生位置(frame)。設定するたびに再描画する。</summary>
    public double CurrentFrame
    {
        get => _currentFrame;
        set { _currentFrame = value; InvalidateVisual(); }
    }

    /// <summary>再生開始ライン(frame)を更新する(2026-07-29要望対応)。再生開始ラインを再設置した際、
    /// 目視テスト中でなくても即座にプレビューの表示内容(judging基準)へ反映するために使う。
    /// 幾何(speed/boost等)の再計算は行わない軽量な更新。</summary>
    public void SetStartFrame(double startFrame)
    {
        if (_startFrame == startFrame) return;
        _startFrame = startFrame;
        InvalidateVisual();
    }

    /// <summary>ノートの表示期限モードを更新する(2026-07-29要望対応)。環境設定を都度Rebuildし直さずに
    /// ラジオボタン変更を即座に反映するための軽量な更新。</summary>
    public void SetNoteExpiryIncludesEqual(bool includesEqual)
    {
        if (_noteExpiryIncludesEqual == includesEqual) return;
        _noteExpiryIncludesEqual = includesEqual;
        InvalidateVisual();
    }

    /// <summary>ドキュメント・難易度タブ・環境設定(Reverse/HiSpeed/調整オフセット等)が変わった際に
    /// 呼び出し、幅・スクロール速度・speed_data/boost_data等を再計算する
    /// (PlaytestWindowコンストラクタの該当ロジックと同一)。</summary>
    public void Rebuild(EditorDocument? doc, AppSettings? appSettings)
    {
        _doc = doc;
        if (doc is null)
        {
            _template = null;
            _startFrame = 0;
            Width = _playingWidth;
            Height = _playingHeight;
            InvalidateVisual();
            return;
        }

        _startFrame = doc.Project.PlaybackStartFrame ?? 0; // 2026-07-29要望対応
        int patternIndex = PlaytestWindow.ResolvePlaytestPatternIndex(appSettings, doc.CurrentTemplate.KeyTypeId);
        _template = doc.CurrentTemplate.WithPattern(patternIndex);
        _reverse = appSettings?.PlaytestReverse ?? false;
        _hiSpeed = Math.Max(0.25, appSettings?.PlaytestHiSpeed ?? 1.0);
        // 2026-07-29要望対応: プレイテスト用の「調整F」(タイミング調整オフセット)はプレビューには
        // 適用しない(常に0固定)。実際のプレイテストと違って判定のズレ補正が目的の値であり、
        // プレビューの見た目には無関係なため。
        _offsetFrames = 0.0;
        _noteExpiryIncludesEqual = appSettings?.PreviewNoteExpiryMode == "overlap";

        double widthFallback = appSettings is not null
            ? PlaytestWindow.ResolveWindowWidthFallback(appSettings, _template.KeyTypeId)
            : PlaytestWindow.AutoSpreadWidth(_template.KeyTypeId);
        _playingWidth = HeaderDouble(doc, "playingWidth", widthFallback);
        _playingHeight = HeaderDouble(doc, "playingHeight", 500);

        double stepYHeader = HeaderDoubleAny(doc, "stepY", KeyTemplate.StepY);
        double stepYRHeader = HeaderDoubleAny(doc, "stepYR", 0);
        double distY = _playingHeight - KeyTemplate.StepY + stepYRHeader;
        double baseSpeed = 1 + ((distY - (stepYHeader - KeyTemplate.StepY) * 2) / (500 - KeyTemplate.StepY) - 1) * 0.85;
        _baseScrollSpeed = _hiSpeed * baseSpeed * 2;
        _stepYTop = stepYHeader + ArrowSize / 2;
        _stepYBottom = _playingHeight + stepYRHeader - stepYHeader - ArrowSize / 2;

        var timing = doc.Project.CreateTimingEngine();
        // 2026-07-30追記: リンク(自動スムージング)区間の中間点もExpandLinkedEventsで展開してから変換する。
        _speedBreaks = DanoniEditor.Core.Timing.ValueEventSmoothing.ExpandLinkedEvents(doc.CurrentTab.SpeedEvents)
            .Select(e => (Frame: timing.TickToFrame(e.Tick) + _offsetFrames, e.Value))
            .OrderBy(b => b.Frame).ToList();
        _boostBreaks = DanoniEditor.Core.Timing.ValueEventSmoothing.ExpandLinkedEvents(doc.CurrentTab.BoostEvents)
            .Select(e => (Frame: timing.TickToFrame(e.Tick) + _offsetFrames, e.Value))
            .OrderBy(b => b.Frame).ToList();

        Width = _playingWidth;
        Height = _playingHeight;
        InvalidateVisual();
    }

    private static double HeaderDouble(EditorDocument doc, string key, double fallback) =>
        doc.Project.ExtraHeaders.TryGetValue(key, out var v)
        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? d : fallback;

    private static double HeaderDoubleAny(EditorDocument doc, string key, double fallback) =>
        doc.Project.ExtraHeaders.TryGetValue(key, out var v)
        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : fallback;

    /// <summary>boost_data相当(PlaytestWindow.GetBoostFactorと同一ロジック)。</summary>
    private double GetBoostFactor(double frame)
    {
        double result = 1.0;
        foreach (var b in _boostBreaks)
        {
            if (b.Frame > frame) break;
            result = b.Value;
        }
        return result;
    }

    /// <summary>speed_data相当の累積移動距離(PlaytestWindow.CumulativeSpeedDistanceと同一ロジック)。</summary>
    private double CumulativeSpeedDistance(double frame)
    {
        double dist = 0;
        double prevFrame = 0;
        double currentValue = 1.0;
        foreach (var b in _speedBreaks)
        {
            if (b.Frame >= frame) break;
            dist += (b.Frame - prevFrame) * currentValue;
            prevFrame = b.Frame;
            currentValue = b.Value;
        }
        dist += (frame - prevFrame) * currentValue;
        return dist;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, _playingWidth, _playingHeight));
        if (_doc is null || _template is null) return;

        var tab = _doc.CurrentTab;
        var project = _doc.Project;
        var timing = _doc.Project.CreateTimingEngine();

        for (int i = 0; i < _template.Lanes.Count && i < tab.Lanes.Count; i++)
        {
            var laneDef = _template.Lanes[i];
            double cx = _template.GetStepX(i, _playingWidth) + ArrowSize / 2;
            bool flipped = (laneDef.ScrollDirection == "down") ^ _reverse;
            double stepY = flipped ? _stepYBottom : _stepYTop;
            double dir = flipped ? -1 : 1;

            var image = ChartCanvas.GetNoteImage(laneDef.NoteGraphic);
            var brush = ChartCanvas.LaneBrush(tab, project, laneDef.ColorGroup);
            var color = ((SolidColorBrush)brush).Color;
            var (frzNoteColor, frzBandColor) = ChartCanvas.FrzColors(tab, project, laneDef.ColorGroup, brush);
            var colorOverrides = tab.Lanes[i].ColorOverrides.ToDictionary(c => c.Tick);

            DrawNote(dc, image, laneDef, cx, stepY, Colors.DimGray, ArrowSize);

            double YOf(double frame, double boost) =>
                stepY + boost * (CumulativeSpeedDistance(frame) - CumulativeSpeedDistance(_currentFrame)) * _baseScrollSpeed * dir;
            bool Visible(double y) => y > -ArrowSize && y < _playingHeight + ArrowSize;

            // フリーズ(帯→端点の順に描画)。2026-07-29要望対応: 「判定をしたもの」として扱い、
            // 終点まで再生開始ラインより手前(=このプレイテストでは一切登場しない)なら描画自体をスキップ。
            // 始点だけが再生開始ラインより手前(=既にホールド中とみなす)の場合は、始点ノートを非表示にし、
            // 帯の描写限界をステップゾーン中央(stepY)にクランプする。
            foreach (var f in tab.Lanes[i].Freezes)
            {
                double startFrame = timing.TickToFrame(f.StartTick) + _offsetFrames;
                double endFrame = timing.TickToFrame(f.EndTick) + _offsetFrames;
                // 2026-07-29要望対応: 「ノートの表示期限」モードに応じて比較演算子を切り替える
                // (既定=frame<startFrame、「重なるまで」=frame<=startFrameでも非表示)。
                bool endExpired = _noteExpiryIncludesEqual ? endFrame <= _startFrame : endFrame < _startFrame;
                if (endExpired) continue; // 終点まで再生開始ラインより手前(またはちょうど) → 登場しない

                bool startAlreadyPassed = _noteExpiryIncludesEqual ? startFrame <= _startFrame : startFrame < _startFrame;
                double boost = GetBoostFactor(startFrame);
                double y1 = startAlreadyPassed ? stepY : YOf(startFrame, boost);
                double y2 = YOf(endFrame, boost);
                if (!Visible(y1) && !Visible(y2) && Math.Sign(y1 - _playingHeight / 2) == Math.Sign(y2 - _playingHeight / 2)) continue;

                colorOverrides.TryGetValue(f.StartTick, out var fOver);
                // 2026-07-30要望対応: 即時適用(AllFlag)の簡易ライブシミュレーション。自分より前の
                // tickで既に発火済みの即時適用があれば、その色を優先する(近似ルール、詳細はヘルパー参照)。
                string? edgeColorCode = ChartCanvas.ResolveImmediateAppliedColor(
                    tab.Lanes[i].ColorOverrides, f.StartTick, _currentFrame, e => e.Color, timing.TickToFrame, fOver?.Color);
                string? bandColorCode = ChartCanvas.ResolveImmediateAppliedColor(
                    tab.Lanes[i].ColorOverrides, f.StartTick, _currentFrame, e => e.BandColor, timing.TickToFrame, fOver?.BandColor);
                var edgeColor = edgeColorCode is { } ec ? ChartCanvas.ParseDisplayColor(ec, frzNoteColor) : frzNoteColor;
                var bandColor = bandColorCode is { } bc ? ChartCanvas.ParseDisplayColor(bc, frzBandColor) : frzBandColor;

                var bandBrush = new SolidColorBrush(bandColor) { Opacity = 0.5 };
                bandBrush.Freeze();
                dc.DrawRectangle(bandBrush, null,
                    new Rect(cx - ArrowSize / 4, Math.Min(y1, y2), ArrowSize / 2, Math.Abs(y2 - y1)));
                if (!startAlreadyPassed) DrawNote(dc, image, laneDef, cx, y1, edgeColor, ArrowSize); // 始点ノートは非表示
                DrawNote(dc, image, laneDef, cx, y2, edgeColor, ArrowSize);
            }

            // 通常ノート。2026-07-29要望対応: 「判定をしたもの」として扱い、再生開始ラインより手前の
            // ノートは非表示にする(このプレイテストでは一切登場しない)。
            foreach (var tick in tab.Lanes[i].Notes)
            {
                double frame = timing.TickToFrame(tick) + _offsetFrames;
                bool expired = _noteExpiryIncludesEqual ? frame <= _startFrame : frame < _startFrame;
                if (expired) continue;
                double y = YOf(frame, GetBoostFactor(frame));
                if (!Visible(y)) continue;
                colorOverrides.TryGetValue(tick, out var nOver);
                string? noteColorCode = ChartCanvas.ResolveImmediateAppliedColor(
                    tab.Lanes[i].ColorOverrides, tick, _currentFrame, e => e.Color, timing.TickToFrame, nOver?.Color);
                var noteColor = noteColorCode is { } nc ? ChartCanvas.ParseDisplayColor(nc, color) : color;
                DrawNote(dc, image, laneDef, cx, y, noteColor, ArrowSize);
            }
        }
    }

    private static void DrawNote(DrawingContext dc, System.Windows.Media.Imaging.BitmapImage? image, LaneDef laneDef, double cx, double y, Color color, double size)
    {
        if (image is not null)
        {
            ChartCanvas.DrawNoteImage(dc, image, laneDef, cx, y, size, color);
        }
        else
        {
            var b = new SolidColorBrush(color);
            b.Freeze();
            dc.DrawRectangle(b, new Pen(Brushes.Black, 0.5), new Rect(cx - size / 2, y - size / 2, size, size));
        }
    }
}
