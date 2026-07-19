using System.Globalization;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Naming;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Import;

/// <summary>dos.txt単体インポートのオプション</summary>
public sealed class DosImportOptions
{
    /// <summary>タイミング情報が復元できない場合に仮定するBPM(仕様書15.2: 環境設定のデフォルト値)</summary>
    public double DefaultBpm { get; init; } = 120;

    /// <summary>明示的なタイミング指定(de_*/es_*より優先)。nullなら自動復元→デフォルトの順</summary>
    public (double StartNumber, IReadOnlyList<BpmEvent> BpmEvents)? TimingOverride { get; init; }

    /// <summary>
    /// tickスナップ方式。Musical(デフォルト)は3tick(64分)/4tick(48分)グリッドの近い方へスナップし、
    /// 整数フレーム化による±0.5F誤差を吸収する。RawTickは最近傍の1tickへ丸める。
    /// </summary>
    public DosSnapMode SnapMode { get; init; } = DosSnapMode.Musical;

    /// <summary>
    /// de_*/es_*が無い場合にノートのフレーム分布からBPMを自動推定する(DosTimingEstimator)。
    /// 推定の一致率がAutoEstimateMinRatio以上のときのみ採用し、未満ならデフォルトBPM仮定へフォールバック。
    /// </summary>
    public bool AutoEstimateTiming { get; init; } = false;

    /// <summary>自動推定を採用する最低一致率(デフォルト0.95)</summary>
    public double AutoEstimateMinRatio { get; init; } = 0.95;
}

public enum DosSnapMode { Musical, RawTick }

/// <summary>dos.txt単体インポートの結果</summary>
public sealed class DosImportResult
{
    public required ChartProject Project { get; init; }
    public required List<string> Warnings { get; init; }
    /// <summary>スナップ時の最大誤差(フレーム)。大きい場合はBPM設定が実際と異なる可能性が高い</summary>
    public required double MaxSnapErrorFrames { get; init; }
    /// <summary>タイミング情報の出所: "de" / "es" / "override" / "default"</summary>
    public required string TimingSource { get; init; }
}

/// <summary>
/// dos.txt単体インポーター(仕様書15.2)。方針:
/// - タイミング復元の優先順: オプション明示指定 > de_*(本エディタ埋め込み) > es_*(SKB埋め込み) > デフォルトBPM仮定
/// - キー種の決定: difDataの各行(先頭フィールド=キー種ID) > es_keyKind。どちらも無ければ例外
///   (オリジナルキー種の手動テンプレート選択は将来のUI側対応)
/// - 完全復元は狙わず、ズレはユーザーが後から調整する(15.2確定方針)。スナップ誤差の統計を返す
/// </summary>
public sealed class DosImporter
{
    private readonly Func<string, KeyTemplate> _templateResolver;

    public DosImporter(Func<string, KeyTemplate> templateResolver)
        => _templateResolver = templateResolver;

    public DosImportResult Import(string dosText, DosImportOptions? options = null)
    {
        options ??= new DosImportOptions();
        var warnings = new List<string>();
        var p = DosParamParser.Parse(dosText);

        double blankFrame = GetDouble(p, "blankFrame") ?? 0;

        // --- タイミング復元 ---
        double startNumber;
        List<BpmEvent> bpmEvents;
        List<TimeSignatureEvent> timeSignatures = [];
        string source;

        if (options.TimingOverride is { } ov)
        {
            (startNumber, bpmEvents, source) = (ov.StartNumber, [.. ov.BpmEvents], "override");
        }
        else if (p.ContainsKey("de_startNumber") && p.ContainsKey("de_bpm"))
        {
            startNumber = double.Parse(p["de_startNumber"], CultureInfo.InvariantCulture);
            var nums = DosParamParser.ParseNumberList(p["de_bpm"]);
            bpmEvents = [];
            for (int i = 0; i + 1 < nums.Length; i += 2)
                bpmEvents.Add(new BpmEvent((long)nums[i], nums[i + 1]));
            if (p.TryGetValue("de_timeSig", out var ts))
            {
                var t = DosParamParser.ParseNumberList(ts);
                for (int i = 0; i + 2 < t.Length; i += 3)
                    timeSignatures.Add(new TimeSignatureEvent((int)t[i], (int)t[i + 1], (int)t[i + 2]));
            }
            source = "de";
        }
        else if (p.ContainsKey("es_bpm") && p.ContainsKey("es_startNumber"))
        {
            // SKBエディタがdos出力に埋め込むメタデータからの復元
            double esBlank = GetDouble(p, "es_blankFrame") ?? blankFrame;
            if (!p.ContainsKey("blankFrame")) blankFrame = esBlank;
            var bpms = DosParamParser.ParseNumberList(p["es_bpm"]);
            var startNums = DosParamParser.ParseNumberList(p["es_startNumber"]);
            var labels = p.TryGetValue("es_label", out var lv) ? DosParamParser.ParseNumberList(lv) : [1];
            var pbns = p.TryGetValue("es_pageBlockNumber", out var pv) ? DosParamParser.ParseNumberList(pv) : [8];
            long ticksPerPage = (long)pbns[0] * TimingEngine.TicksPerBeat;

            startNumber = esBlank + startNums[0];
            bpmEvents = [new BpmEvent(0, bpms[0])];
            for (int i = 1; i < bpms.Length && i < labels.Length; i++)
                bpmEvents.Add(new BpmEvent(((long)labels[i] - 1) * ticksPerPage, bpms[i]));

            // 再同期ジャンプ検出(SkbImporterと同じ理由)
            var probe = new TimingEngine(startNumber, bpmEvents);
            for (int i = 1; i < startNums.Length && i < labels.Length; i++)
            {
                double expected = probe.TickToFrame(((long)labels[i] - 1) * ticksPerPage);
                double actual = esBlank + startNums[i];
                if (Math.Abs(actual - expected) > 0.5)
                    warnings.Add($"es_timing(label={labels[i]}): startNumberが連続値から{actual - expected:+0.0;-0.0}Fずれています(再同期ジャンプは表現不能)");
            }
            source = "es";
        }
        else if (options.AutoEstimateTiming &&
                 EstimateFromData(p, blankFrame, warnings, options) is { } est)
        {
            // 推定はファイル上の生フレーム(blank込み)にフィットするため、StartNumberを内部軸へ変換(2026-07-19f)
            (startNumber, bpmEvents, source) = (est.StartNumber - blankFrame, est.BpmEvents, "estimated");
        }
        else
        {
            startNumber = 0; // 2026-07-19c: blankFrameは軸計算へ織り込まない(ユーザー指示、旧: startNumber=blankFrame)
            bpmEvents = [new BpmEvent(0, options.DefaultBpm)];
            warnings.Add($"タイミング情報が無いためBPM={options.DefaultBpm}・4/4拍子を仮定しました。" +
                         "実際のBPM/拍子はユーザーが確認・調整してください(仕様書15.2)");
            source = "default";
        }

        var engine = new TimingEngine(startNumber, bpmEvents, timeSignatures);

        // --- プロジェクト組み立て(ヘッダー) ---
        var project = new ChartProject
        {
            StartNumber = startNumber,
            BpmEvents = bpmEvents,
            TimeSignatures = timeSignatures,
            BlankFrame = (int)Math.Round(blankFrame),
        };
        if (p.TryGetValue("musicTitle", out var mt))
        {
            var parts = mt.Split(',');
            project.MusicTitle = parts[0].Trim();
            if (parts.Length > 1) project.ArtistName = parts[1].Trim();
            if (parts.Length > 2) project.ArtistUrl = parts[2].Trim();
        }
        if (p.TryGetValue("musicUrl", out var mu)) project.MusicUrl = mu;
        if (p.TryGetValue("tuning", out var tu)) project.Tuning = tu;
        if (GetDouble(p, "startFrame") is { } sf) project.StartFrame = (int)sf;
        if (GetDouble(p, "frzAttempt") is { } fa) project.FrzAttempt = (int)fa;

        // --- 難易度タブの決定 ---
        var tabSpecs = new List<(string KeyTypeId, string Name, double Speed, string? Extra)>();
        if (p.TryGetValue("difData", out var difData))
        {
            foreach (var row in difData.Split('$', '\n')
                         .Select(r => r.Trim()).Where(r => r.Length > 0))
            {
                var f = row.Split(',');
                double speed = f.Length > 2 && double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 3.5;
                string? extra = f.Length > 3 ? string.Join(",", f[3..]) : null;
                tabSpecs.Add((f[0].Trim(), f.Length > 1 ? f[1].Trim() : "", speed, extra));
            }
        }
        else if (p.TryGetValue("es_keyKind", out var kk))
        {
            tabSpecs.Add((kk, "", 3.5, null));
            warnings.Add("difDataが無いためes_keyKindからキー種を決定しました。難易度名は手動設定が必要です");
        }
        else
        {
            throw new InvalidDataException(
                "difDataが無くキー種を決定できません。標準キー種の手動選択またはdifDataの追記が必要です(仕様書15.2)");
        }

        // --- スナップ関数 ---
        // Musical: 4分→64分の順(粗い順)に「整数フレーム丸め(±0.5F)と矛盾しない」最初のグリッドへスナップ。
        // 整数フレームからは隣接する細グリッド位置と原理的に区別できないケースがあるため、
        // 音楽的に単純な位置を優先する決定的な規約とする。
        // 2026-07-19g: 分解能1680/拍化に伴い×35。5連・7連系(20/28/40/56分=336/240/168/120tick)も追加
        long[] musicalGrids = [1680, 840, 560, 420, 336, 280, 240, 210, 168, 140, 120, 105]; // 4,8,12,16,20,24,28,32,40,48,56,64分
        double maxErr = 0;
        int offGridCount = 0;
        long Snap(double frame)
        {
            // 2026-07-19f: dos.txtのフレーム値は「内部フレーム+blankFrame」(エクスポート側と対称)。
            // 内部軸へ戻すためblankFrameを減算してからtick化する。
            frame -= blankFrame;
            double tEst = engine.FrameToTick(frame);
            long tick = (long)Math.Round(tEst); // フォールバック(RawTick相当)
            if (options.SnapMode == DosSnapMode.Musical)
            {
                foreach (var g in musicalGrids)
                {
                    long c = (long)Math.Round(tEst / g) * g;
                    if (Math.Abs(frame - engine.TickToFrame(c)) <= 0.501) { tick = c; break; }
                }
            }
            double err = Math.Abs(frame - engine.TickToFrame(tick));
            maxErr = Math.Max(maxErr, err);
            if (err > 0.6) offGridCount++;
            return tick;
        }

        // --- 各タブのデータ読み込み ---
        for (int i = 0; i < tabSpecs.Count; i++)
        {
            var (keyTypeId, name, speed, extra) = tabSpecs[i];
            var template = _templateResolver(keyTypeId);
            var tab = DifficultyTab.CreateFor(template, name, speed);
            tab.DifDataExtra = extra;
            string suffix = i == 0 ? "" : (i + 1).ToString();

            for (int j = 0; j < template.KeyCount; j++)
            {
                var lane = template.Lanes[j];
                if (p.TryGetValue($"{lane.DataName}{suffix}_data", out var nd) && nd.Length > 0)
                    foreach (var f in DosParamParser.ParseNumberList(nd))
                        tab.Lanes[j].Notes.Add(Snap(f));

                var frzName = lane.FrzDataNameOverride ?? FrzNameResolver.Resolve(lane.DataName);
                if (p.TryGetValue($"{frzName}{suffix}_data", out var fd) && fd.Length > 0)
                {
                    var vals = DosParamParser.ParseNumberList(fd);
                    for (int k = 0; k + 1 < vals.Length; k += 2)
                        tab.Lanes[j].Freezes.Add(new FreezeNote(Snap(vals[k]), Snap(vals[k + 1])));
                    if (vals.Length % 2 == 1)
                        warnings.Add($"{frzName}{suffix}_data: 値が奇数個のため末尾を無視しました");
                }
            }

            void LoadValueEvents(string paramName, List<ValueEvent> target)
            {
                if (!p.TryGetValue(paramName, out var v) || v.Length == 0) return;
                var vals = DosParamParser.ParseNumberList(v);
                for (int k = 0; k + 1 < vals.Length; k += 2)
                    target.Add(new ValueEvent(Snap(vals[k]), vals[k + 1]));
            }
            LoadValueEvents($"speed{suffix}_data", tab.SpeedEvents);
            LoadValueEvents($"boost{suffix}_data", tab.BoostEvents);

            // 色設定(setColor/frzColor、2タブ目以降はsetColor2等)。
            // dos.txt側は"0xRRGGBB"表記のことがあるが、エディタ内部(WPFのColorConverter)は
            // "#RRGGBB"しか解釈できないため、取り込み時に0xプレフィックスを#へ正規化する
            // (2026-07-16i: 「6桁カラーコードとして認識できない」バグ修正)。
            if (p.TryGetValue($"setColor{suffix}", out var sc) && sc.Length > 0)
                tab.SetColorOverride = [.. sc.Split(',', StringSplitOptions.TrimEntries).Select(NormalizeColorToken)];
            if (p.TryGetValue($"frzColor{suffix}", out var fc) && fc.Length > 0)
                tab.FrzColorOverride = [.. fc.Split(',', StringSplitOptions.TrimEntries).Select(NormalizeColorToken)];

            project.Tabs.Add(tab);
        }

        static string NormalizeColorToken(string raw)
        {
            var s = raw.Trim();
            // "0xRRGGBB"/"0XRRGGBB"表記を"#RRGGBB"へ変換する(WPFのColorConverterは#形式のみ対応、
            // 2026-07-16i)。グラデーション等の生文字列(:/@を含む)や既に#始まりのものはそのまま通す。
            if (s.Length > 2 && (s[0] == '0') && (s[1] is 'x' or 'X'))
                return "#" + s[2..];
            return s;
        }

        if (offGridCount > 0)
            warnings.Add($"{offGridCount}個のオブジェクトがグリッドから0.6F以上ずれています(最大{maxErr:F2}F)。" +
                         "BPM/StartNumber設定が実際と異なる可能性があります");

        // --- その他ヘッダー(既知キー・データ系・メタデータ系を除いて保持) ---
        var handled = new HashSet<string> { "musicTitle", "difData", "musicUrl", "tuning",
            "startFrame", "blankFrame", "frzAttempt" };
        foreach (var (key, value) in p)
        {
            if (handled.Contains(key)) continue;
            if (key.EndsWith("_data") || key.StartsWith("es_") || key.StartsWith("de_")) continue;
            if (key.StartsWith("setColor") || key.StartsWith("frzColor")) continue;
            project.ExtraHeaders[key] = value;
        }

        return new DosImportResult
        {
            Project = project,
            Warnings = warnings,
            MaxSnapErrorFrames = maxErr,
            TimingSource = source,
        };
    }

    private static double? GetDouble(Dictionary<string, string> p, string key) =>
        p.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : null;

    /// <summary>
    /// 全*_dataのフレーム値を収集してBPMを自動推定する。
    /// 採用時は位相を[blank, blank+四分間隔)へ正規化してStartNumberとする
    /// (格子等価な位相のうちblankFrameに最も近い代表値を選ぶ)。
    /// </summary>
    private static (double StartNumber, List<BpmEvent> BpmEvents)? EstimateFromData(
        Dictionary<string, string> p, double blankFrame, List<string> warnings, DosImportOptions options)
    {
        var frames = new List<double>();
        foreach (var (key, value) in p)
        {
            if (!key.EndsWith("_data") || key.StartsWith("es_") || key.StartsWith("de_")) continue;
            if (key.StartsWith("speed") || key.StartsWith("boost")) continue; // 値ペア形式のため除外
            if (value.Length == 0) continue;
            try { frames.AddRange(DosParamParser.ParseNumberList(value)); }
            catch { /* 数値でない_dataは無視 */ }
        }

        var candidates = DosTimingEstimator.Estimate(frames, anchorPhase: blankFrame, searchPhase: true);
        if (candidates.Count == 0) return null;
        var best = candidates[0];
        if (best.InlierRatio < options.AutoEstimateMinRatio)
        {
            warnings.Add($"BPM自動推定の一致率が低いため不採用でした(最良候補: BPM={best.Bpm:F2}, 一致率{best.InlierRatio:P1})");
            return null;
        }

        // 位相を [blank, blank+q) へ正規化
        double q = 3600.0 / best.Bpm;
        double phase = best.Phase - Math.Floor((best.Phase - blankFrame) / q) * q;

        warnings.Add($"BPMをノート分布から自動推定しました: BPM={best.Bpm:F2}(一致率{best.InlierRatio:P1}、" +
                     $"ノート{frames.Count}個)。位相(StartNumber={phase:F2})は格子等価な代表値のため、" +
                     "小節頭の位置はユーザーが確認・調整してください");
        return (phase, [new BpmEvent(0, best.Bpm)]);
    }
}
