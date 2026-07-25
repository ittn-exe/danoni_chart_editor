using System.Globalization;
using System.Linq;
using System.Text;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Naming;

namespace DanoniEditor.Core.Export;

/// <summary>
/// dos.txt組み立てエンジン(仕様書3.1「キー種に依存しない汎用エンジン」)。
/// テンプレートのlanes[].dataNameとタブ並び順のサフィックス採番(5章)に従い、
/// |パラメータ名=値| 形式のテキストを生成する。
/// </summary>
public sealed class DosExporter
{
    /// <summary>keyTypeId → テンプレートの解決関数(通常はTemplateRepositoryを渡す)</summary>
    private readonly Func<string, KeyTemplate> _templateResolver;

    public DosExporter(Func<string, KeyTemplate> templateResolver)
        => _templateResolver = templateResolver;

    /// <summary>フレーム値の丸め(dos.txtは整数フレームで出力する)</summary>
    private static long RoundFrame(double frame) =>
        (long)Math.Round(frame, MidpointRounding.AwayFromZero);

    private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);

    public string Export(ChartProject project) => Export(project, includeEditorMetadata: false);

    /// <summary>
    /// includeEditorMetadata=true の場合、末尾に de_* パラメータ(StartNumber/BPM/拍子)を埋め込む。
    /// ダンおに本体は未知パラメータを無視するため再生に影響せず、DosImporterがこれを読めば
    /// dos.txt単体からタイミング情報を完全復元できる(仕様書15.2のBPM欠落問題への自前対策)。
    /// </summary>
    public string Export(ChartProject project, bool includeEditorMetadata)
    {
        if (project.Tabs.Count == 0)
            throw new InvalidOperationException("難易度タブが1つもありません");

        var engine = project.CreateTimingEngine();
        // 2026-07-19f: dos.txtのフレーム値は「内部フレーム(StartNumber基準)+blankFrame」で出力する
        // (ユーザー指定)。内部軸はblankFrameを含まないため、本体のゲーム軸(曲がblankFrame遅れで
        // 始まる)へ変換するにはblankFrameを加算する。DosImporter側は対称に減算する(往復整合)。
        double blankShift = project.BlankFrame;
        var sb = new StringBuilder();

        // --- JSラッパー(2026-07-19d、ユーザー指定) ---
        // danoniplusの外部dos読み込み形式: dos.txt全体を externalDosInit() のテンプレートリテラルで包む。
        // FUJIエディタの$dosformat([header]〜[footer])と同じ構造。
        sb.Append("function externalDosInit() {\n");
        sb.Append("  g_externalDos = `\n");

        // --- ヘッダー部 ---
        var titleParts = new List<string> { project.MusicTitle };
        if (project.ArtistName.Length > 0 || project.ArtistUrl.Length > 0) titleParts.Add(project.ArtistName);
        if (project.ArtistUrl.Length > 0) titleParts.Add(project.ArtistUrl);
        AppendParam(sb, "musicTitle", string.Join(",", titleParts));

        // 2026-08-05: GaugeManualEditAfterExportがONの間はdifData内のborder/recovery/damage/initLife%
        // (DifDataExtra)も出力しない(ゲージ関連は一切エディタが触れず、ユーザーが後から手で追記する運用のため)。
        var difData = string.Join("$", project.Tabs.Select(t =>
            $"{t.KeyTypeId},{t.DifficultyName},{Num(t.InitialSpeed)}" +
            (project.GaugeManualEditAfterExport || string.IsNullOrEmpty(t.DifDataExtra) ? "" : $",{t.DifDataExtra}")));
        AppendParam(sb, "difData", difData);

        // 色設定: 先頭タブが共通値の実体(仕様書6.4.2)。上書きはsetColor2等で追記
        // 2026-07-16l: defaultFrzColorUse(dos-h0063)がtrueの間は、frzColorの指定自体を出力しない
        // (本体側の既定フリーズアロー色セットが優先され、frzColorの値が無視される仕様のため。
        // UI側でも入力を無効化・FrzColorOverrideをクリアしているが、念のためexport側でも二重に抑止する)。
        bool defaultFrzColorUse = project.ExtraHeaders.TryGetValue("defaultFrzColorUse", out var dfu) && dfu == "true";
        var firstTab = project.Tabs[0];
        if (firstTab.SetColorOverride is { Count: > 0 } sc)
            AppendParam(sb, "setColor", string.Join(",", sc));
        if (!defaultFrzColorUse && firstTab.FrzColorOverride is { Count: > 0 } fc)
            AppendParam(sb, "frzColor", string.Join(",", fc));
        for (int i = 1; i < project.Tabs.Count; i++)
        {
            var t = project.Tabs[i];
            var suffix = (i + 1).ToString();
            if (t.SetColorOverride is { Count: > 0 } sco)
                AppendParam(sb, $"setColor{suffix}", string.Join(",", sco));
            if (!defaultFrzColorUse && t.FrzColorOverride is { Count: > 0 } fco)
                AppendParam(sb, $"frzColor{suffix}", string.Join(",", fco));
        }

        AppendParam(sb, "startFrame", project.StartFrame.ToString());
        if (project.BlankFrame != 0) AppendParam(sb, "blankFrame", project.BlankFrame.ToString());
        AppendParam(sb, "musicUrl", project.MusicUrl);
        AppendParam(sb, "tuning", project.Tuning);
        if (project.FrzAttempt != 5) AppendParam(sb, "frzAttempt", project.FrzAttempt.ToString());

        foreach (var (key, value) in project.ExtraHeaders)
            AppendParam(sb, key, value);

        AppendGaugeHeaders(sb, project);

        sb.AppendLine();

        // --- 譜面本体(タブごと。サフィックス: 先頭=無し、2番目以降=2,3...) ---
        for (int tabIndex = 0; tabIndex < project.Tabs.Count; tabIndex++)
        {
            var tab = project.Tabs[tabIndex];
            var template = _templateResolver(tab.KeyTypeId);
            var suffix = tabIndex == 0 ? "" : (tabIndex + 1).ToString();

            if (tab.Lanes.Count != template.KeyCount)
                throw new InvalidOperationException(
                    $"タブ'{tab.DifficultyName}'のレーン数({tab.Lanes.Count})がテンプレート({template.KeyCount})と一致しません");

            // 2026-07-23(TBD 3): 最終的な出力直前に(Frame,TargetSuffix,ColorCode,AllFlag)が同一の
            // エントリをまとめて0...7/1・3・5・7/all記法へ圧縮するため、まずはレーンごとの生データ
            // (EngineLaneNumとTargetSuffixを分離した形)で集める。
            var nColorRaw = new List<(long Frame, int EngineLaneNum, string TargetSuffix, string ColorCode, bool AllFlag)>();

            for (int j = 0; j < template.KeyCount; j++)
            {
                var lane = template.Lanes[j];
                var data = tab.Lanes[j];

                if (data.Notes.Count > 0)
                {
                    var frames = data.Notes
                        .Select(t => RoundFrame(engine.TickToFrame(t) + blankShift))
                        .OrderBy(f => f);
                    AppendParam(sb, $"{lane.DataName}{suffix}_data", string.Join(",", frames));
                }

                if (data.Freezes.Count > 0)
                {
                    var frzName = lane.FrzDataNameOverride ?? FrzNameResolver.Resolve(lane.DataName);
                    var pairs = data.Freezes
                        .OrderBy(f => f.StartTick)
                        .SelectMany(f => new[]
                        {
                            RoundFrame(engine.TickToFrame(f.StartTick) + blankShift),
                            RoundFrame(engine.TickToFrame(f.EndTick) + blankShift),
                        });
                    AppendParam(sb, $"{frzName}{suffix}_data", string.Join(",", pairs));
                }

                if (data.ColorOverrides.Count > 0)
                {
                    // 2026-07-24: ncolor_data(allFlg無し)は本家仕様上「指定フレーム以降ずっと持続する」
                    // 永続的な色状態変更であり、1オーバーライド=1行の単純な出力では「着色ノートの後ろに
                    // 置いた無着色ノートまで意図せず着色される」問題が起きる(ユーザー指摘、2026-07-24)。
                    // トラック(Arrow=通常ノート、Normal=フリーズ端点、NormalBar=フリーズ帯)ごとに
                    // tick順で「現在の色状態」との差分がある地点だけncolor_dataを出力する。
                    // 連続する同色区間は状態が変化しないため自動的に省略され、着色→無着色→着色のような
                    // 区間には「基本色へ戻す」行が自動的に挿入される。
                    var overrideByTick = data.ColorOverrides.ToDictionary(c => c.Tick);
                    string arrowDefault = ColorDefaults.ResolveSetColorHex(tab, project, lane.ColorGroup);

                    void ScanTrack(IEnumerable<long> ticks, string defaultHex, string targetSuffix,
                        Func<NColorEntry, string?> pick)
                    {
                        string current = defaultHex;
                        foreach (var tick in ticks.OrderBy(t => t))
                        {
                            overrideByTick.TryGetValue(tick, out var e);
                            string desired = e is not null ? pick(e) ?? defaultHex : defaultHex;
                            if (string.Equals(desired, current, StringComparison.Ordinal)) continue;
                            long frame = RoundFrame(engine.TickToFrame(tick) + blankShift);
                            // allFlg(即時適用)は基本色への自動復帰(e=null)には適用しない。
                            // ユーザーが明示的に塗った箇所(eが存在する)でのみ、その時のチェック状態を反映する。
                            bool allFlag = e?.AllFlag ?? false;
                            nColorRaw.Add((frame, lane.EngineLaneNum, targetSuffix, desired, allFlag));
                            current = desired;
                        }
                    }

                    string arrowShadowDefault = ColorDefaults.ResolveShadowHex(project, lane.ColorGroup, "setShadowColor");

                    if (data.Notes.Count > 0)
                    {
                        ScanTrack(data.Notes, arrowDefault, "", e => e.Color);
                        ScanTrack(data.Notes, arrowShadowDefault, "ArrowShadow", e => e.ShadowColor);
                    }

                    if (data.Freezes.Count > 0)
                    {
                        var (normalDefault, barDefault) =
                            ColorDefaults.ResolveFrzColorsHex(tab, project, arrowDefault);
                        var (hitDefault, hitBarDefault) =
                            ColorDefaults.ResolveFrzHitColorsHex(tab, project, normalDefault, barDefault);
                        string normalShadowDefault = ColorDefaults.ResolveShadowHex(project, lane.ColorGroup, "frzShadowColor");
                        var freezeStartTicks = data.Freezes.Select(f => f.StartTick);
                        ScanTrack(freezeStartTicks, normalDefault, "Normal", e => e.Color);
                        ScanTrack(freezeStartTicks, barDefault, "NormalBar", e => e.BandColor);
                        ScanTrack(freezeStartTicks, normalShadowDefault, "NormalShadow", e => e.ShadowColor);
                        ScanTrack(freezeStartTicks, hitDefault, "Hit", e => e.HitColor);
                        ScanTrack(freezeStartTicks, hitBarDefault, "HitBar", e => e.HitBarColor);
                        ScanTrack(freezeStartTicks, normalShadowDefault, "HitShadow", e => e.HitShadowColor);
                    }
                }
            }

            AppendValueEvents(sb, $"speed{suffix}_data", tab.SpeedEvents, engine, blankShift);
            AppendValueEvents(sb, $"boost{suffix}_data", tab.BoostEvents, engine, blankShift);
            AppendNColorData(sb, $"ncolor{suffix}_data", CompressNColorEntries(nColorRaw, template));
            AppendWordData(sb, suffix, tab, engine, blankShift);
            sb.AppendLine();
        }

        if (includeEditorMetadata)
        {
            AppendParam(sb, "de_schemaVersion", "1");
            AppendParam(sb, "de_startNumber", Num(project.StartNumber));
            AppendParam(sb, "de_bpm", string.Join(",",
                project.BpmEvents.OrderBy(e => e.Tick).SelectMany(e => new[] { e.Tick.ToString(), Num(e.Bpm) })));
            if (project.TimeSignatures.Count > 0)
                AppendParam(sb, "de_timeSig", string.Join(",",
                    project.TimeSignatures.OrderBy(e => e.MeasureIndex)
                        .SelectMany(e => new[] { e.MeasureIndex.ToString(), e.Numerator.ToString(), e.Denominator.ToString() })));
        }

        // --- JSラッパー閉じ(2026-07-19d) ---
        sb.Append("  `;\n");
        sb.Append("}\n");

        return sb.ToString();
    }

    private static void AppendValueEvents(StringBuilder sb, string name,
        List<ValueEvent> events, Timing.TimingEngine engine, double blankShift)
    {
        if (events.Count == 0) return;
        var parts = events
            .OrderBy(e => e.Tick)
            .SelectMany(e => new[] { RoundFrame(engine.TickToFrame(e.Tick) + blankShift).ToString(), Num(e.Value) });
        AppendParam(sb, name, string.Join(",", parts));
    }

    /// <summary>2026-07-23(TBD 3): 同一(Frame,TargetSuffix,ColorCode,AllFlag)で複数レーンが同時に
    /// 色変化するエントリを、本家dos-e0002-ncolorData仕様の省略記法(範囲"0...7"/スラッシュ複数"1/3/5/7"/
    /// 全レーン"all")へ圧縮する。本エディタが対象とする通常譜面(トランスキー以外)ではキーグループは
    /// 常に0のみのため、"all"のみを使用し(g0〜g9のグループ記法は出力しない、キーグループ仕様上g1〜g9は
    /// 通常譜面で意味を持たないため)。</summary>
    private static List<(long Frame, string ColorNo, string ColorCode, bool AllFlag)> CompressNColorEntries(
        List<(long Frame, int EngineLaneNum, string TargetSuffix, string ColorCode, bool AllFlag)> raw,
        KeyTemplate template)
    {
        var allEngineLaneNums = template.Lanes.Select(l => l.EngineLaneNum).ToHashSet();
        return raw
            .GroupBy(e => (e.Frame, e.TargetSuffix, e.ColorCode, e.AllFlag))
            .Select(g => (
                Frame: g.Key.Frame,
                ColorNo: CompressColorNoGroup(g.Select(x => x.EngineLaneNum).ToList(), g.Key.TargetSuffix, allEngineLaneNums),
                ColorCode: g.Key.ColorCode,
                AllFlag: g.Key.AllFlag))
            .ToList();
    }

    /// <summary>矢印番号の集合をncolor_data記法の1トークンへ圧縮する。
    /// 複数かつテンプレート全レーンと一致する場合は"all"、複数かつ連番(EngineLaneNum基準)なら
    /// "min...max"、それ以外はスラッシュ区切り。単一の場合はそのまま数値のみ。</summary>
    private static string CompressColorNoGroup(List<int> engineLaneNums, string targetSuffix, HashSet<int> allEngineLaneNums)
    {
        var sorted = engineLaneNums.Distinct().OrderBy(n => n).ToList();
        string numPart;
        if (sorted.Count > 1 && allEngineLaneNums.SetEquals(sorted))
            numPart = "all";
        else if (sorted.Count > 1 && sorted[^1] - sorted[0] + 1 == sorted.Count)
            numPart = $"{sorted[0]}...{sorted[^1]}";
        else
            numPart = string.Join("/", sorted);
        return targetSuffix.Length == 0 ? numPart : $"{numPart}:{targetSuffix}";
    }

    /// <summary>ncolor_data出力(仕様: 1エントリ=Frame,ColorNo(:TargetPattern),ColorCode(,allFlg)の
    /// 3〜4項目CSV。AllFlag=trueの場合のみ4項目目に"all"を付与する、2026-07-24)。Frame昇順に整列する。</summary>
    private static void AppendNColorData(StringBuilder sb, string name,
        List<(long Frame, string ColorNo, string ColorCode, bool AllFlag)> entries)
    {
        if (entries.Count == 0) return;
        var parts = entries
            .OrderBy(e => e.Frame)
            .SelectMany(e => e.AllFlag
                ? new[] { e.Frame.ToString(), e.ColorNo, e.ColorCode, "all" }
                : new[] { e.Frame.ToString(), e.ColorNo, e.ColorCode });
        AppendParam(sb, name, string.Join(",", parts));
    }

    /// <summary>word_data/wordRev_dataの出力(仕様dos-e0003-wordData、2026-07-23、TBD 4)。
    /// tab.WordLanesのうちIsReverseが同じもの同士をフレーム順にマージし、1つのデータ名(word{suffix}_data
    /// またはwordRev{suffix}_data)としてまとめて出力する(dos.txt側は1タブにつき1つのデータ名しか
    /// 持てないため)。改行区切り形式(1行=1エントリ)で出力する。レーンが1本も無ければ何も出力しない。</summary>
    private static void AppendWordData(StringBuilder sb, string suffix, DifficultyTab tab,
        Timing.TimingEngine engine, double blankShift)
    {
        if (tab.WordLanes.Count == 0) return;

        foreach (var isReverse in new[] { false, true })
        {
            var entries = tab.WordLanes.Where(l => l.IsReverse == isReverse)
                .SelectMany(l => l.Entries)
                .OrderBy(e => e.Tick)
                .ToList();
            if (entries.Count == 0) continue;

            var rows = entries.Select(e => FormatWordRow(e, engine, blankShift));
            string name = (isReverse ? "wordRev" : "word") + suffix + "_data";
            AppendParam(sb, name, string.Join("\n", rows));
        }
    }

    private static string FormatWordRow(WordEntry e, Timing.TimingEngine engine, double blankShift)
    {
        long frame = RoundFrame(engine.TickToFrame(e.Tick) + blankShift);
        return e.Kind switch
        {
            WordEntryKind.Comment => $"{frame},-,{e.Text}",
            WordEntryKind.Control when e.FadeFrame is { } ff => $"{frame},{e.Position},{e.Text},{ff}",
            _ => $"{frame},{e.Position},{e.Text}",
        };
    }

    private static void AppendParam(StringBuilder sb, string name, string value)
        => sb.AppendLine($"|{name}={value}|");

    /// <summary>customGauge{N}/gaugeXXX{N}の出力(2026-08-01、GaugeEditorWindow)。
    /// GaugeRawOverrideText(直接入力モード)に空白以外の内容があれば、その内容を
    /// そのまま出力し、GaugeParams/DifficultyTab.GaugeによるUI組み立てロジックは完全に無視する
    /// (ユーザー確定仕様: 直接入力が常にUI設定より優先)。
    /// 2026-08-05: GaugeManualEditAfterExportがONの間は、直接入力モードを含めゲージ関連の出力を
    /// 一切行わない(何も書き出さず、ユーザーが書き出し後のdos.txtへ自分で追記する運用のため)。</summary>
    private static void AppendGaugeHeaders(StringBuilder sb, ChartProject project)
    {
        if (project.GaugeManualEditAfterExport) return;

        if (!string.IsNullOrWhiteSpace(project.GaugeRawOverrideText))
        {
            foreach (var rawLine in project.GaugeRawOverrideText.Replace("\r\n", "\n").Split('\n'))
            {
                if (rawLine.Length > 0) sb.AppendLine(rawLine);
            }
            return;
        }

        for (int i = 0; i < project.Tabs.Count; i++)
        {
            var gauge = project.Tabs[i].Gauge;
            if (gauge is null) continue;
            var suffix = i == 0 ? "" : (i + 1).ToString();

            if (!string.IsNullOrEmpty(gauge.InheritKeyword))
            {
                AppendParam(sb, $"customGauge{suffix}", gauge.InheritKeyword);
            }
            else if (gauge.Entries.Count > 0)
            {
                var value = string.Join(",", gauge.Entries.Select(e =>
                {
                    var varFlag = e.IsVariable ? "V" : "F";
                    return string.IsNullOrEmpty(e.DisplayName)
                        ? $"{e.Name}::{varFlag}"
                        : $"{e.Name}::{varFlag}::{e.DisplayName}";
                }));
                AppendParam(sb, $"customGauge{suffix}", value);
            }
        }

        foreach (var name in project.GaugeNames)
        {
            var perTabCsv = project.Tabs.Select(t => t.GaugeParams is { } gp && gp.TryGetValue(name, out var csv) ? csv : "");
            var values = perTabCsv.ToList();
            if (values.All(string.IsNullOrEmpty)) continue;
            AppendParam(sb, $"gauge{name}", string.Join("$", values));
        }
    }
}
