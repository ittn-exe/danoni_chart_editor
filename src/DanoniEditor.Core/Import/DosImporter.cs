using System.Globalization;
using DanoniEditor.Core.Export;
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
            // 2026-07-24: ncolor_data読み込み(状態復元)がこの値(レーンの既定色)を必要とするため、
            // ImportNColorDataより前に確定させる(以前は後で設定していたが、順序を入れ替えた)。
            if (p.TryGetValue($"setColor{suffix}", out var sc) && sc.Length > 0)
                tab.SetColorOverride = [.. sc.Split(',', StringSplitOptions.TrimEntries).Select(NormalizeColorToken)];
            if (p.TryGetValue($"frzColor{suffix}", out var fc) && fc.Length > 0)
                tab.FrzColorOverride = [.. fc.Split(',', StringSplitOptions.TrimEntries).Select(NormalizeColorToken)];

            ImportNColorData($"ncolor{suffix}_data", p, project, tab, template, Snap, warnings);

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

    /// <summary>対応済みTargetPattern(2026-07-24、Hit/Shadow系追加)。省略時("")はArrow扱い。</summary>
    private static readonly string[] SupportedTargetPatterns =
        ["", "Normal", "NormalBar", "NormalShadow", "ArrowShadow", "Hit", "HitBar", "HitShadow"];

    /// <summary>
    /// ncolor_dataの読み込み(色編集モード、2026-07-23。永続状態モデルへ2026-07-24に再設計、
    /// 同日allFlg・Hit/Shadow系対応を追加)。本家仕様ではncolor_data(allFlg無し)は「指定フレーム
    /// 以降ずっと持続する」永続的な色状態変更のため、行を単純に「そのtickのノートの色」として
    /// 読み込むと実際の見た目とズレる。本エディタが対応する範囲(個別矢印番号、TargetPatternは
    /// Arrow/ArrowShadow/Normal/NormalBar/NormalShadow/Hit/HitBar/HitShadowのみ)の行を
    /// TargetPatternごとにtick順の「色状態の変化点」として集約し、レーン内の各ノート/フリーズに
    /// ついて「自分の出現tick時点での状態色」を復元してレーン既定色と異なる場合のみ
    /// ColorOverridesへ格納する(DosExporterの出力アルゴリズムの逆変換)。したがって色変化点は
    /// 必ずしもノート自身のtickと一致する必要はない(以前はtick一致を要求してそれ以外を警告付き
    /// スキップしていたが、本家仕様上は不要な制約だったため撤廃した)。4番目のフィールド(all/ALL)は
    /// NColorEntry.AllFlagとして保持する(1エンティティに寄与する複数トラックで異なるAllFlagが
    /// 復元された場合は、いずれか1つでもtrueならエントリ全体をtrueとして扱う)。範囲指定(0...7)・
    /// スラッシュ複数・グループ(g0等)・未対応TargetPatternの照合不能な行は、データを壊さないよう
    /// 無視した上で件数を警告として積む(仕様書の「読めない物は警告、握りつぶさない」方針に合わせる)。
    /// 値は本エディタの出力(改行なし1行CSV)と、手書き想定の複数行形式の両方を受け付ける。
    /// </summary>
    private static void ImportNColorData(string paramName, Dictionary<string, string> p,
        ChartProject project, DifficultyTab tab, KeyTemplate template, Func<double, long> snap, List<string> warnings)
    {
        if (!p.TryGetValue(paramName, out var raw) || raw.Length == 0) return;

        var engineToLane = new Dictionary<int, int>();
        for (int j = 0; j < template.KeyCount; j++) engineToLane[template.Lanes[j].EngineLaneNum] = j;

        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        List<string> rows;
        if (lines.Count > 1)
        {
            rows = lines; // 手書き想定: 1行=1エントリ
        }
        else
        {
            // 本エディタの出力形式: 改行無しでFrame,ColorNo,ColorCode(,allFlg)の組が連続する1行CSV。
            // allFlg列は行によって有無が変わる(可変長)ため、次のエントリの先頭(Frame)と
            // 区別するために4番目のトークンが"all"かどうかを見て3個組/4個組を判定する
            // (色コード自体が文字列"all"になることは無い前提)。
            var tokens = raw.Split(',', StringSplitOptions.TrimEntries);
            rows = [];
            int i = 0;
            while (i + 2 < tokens.Length)
            {
                bool hasAllFlg = i + 3 < tokens.Length &&
                    tokens[i + 3].Equals("all", StringComparison.OrdinalIgnoreCase);
                int take = hasAllFlg ? 4 : 3;
                rows.Add(string.Join(",", tokens.Skip(i).Take(take)));
                i += take;
            }
        }

        // TargetPattern別・レーン別にtick順の色変化点を集約する(キー: "" | "Normal" | "NormalBar" |
        // "NormalShadow" | "ArrowShadow" | "Hit" | "HitBar" | "HitShadow")
        var changesByPattern = SupportedTargetPatterns.ToDictionary(
            pat => pat, _ => new Dictionary<int, List<(long Tick, string Color, bool AllFlag)>>());

        int skipped = 0;
        foreach (var row in rows)
        {
            var fields = row.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 3) { skipped++; continue; }
            if (fields[1] == "-") continue; // コメント行(仕様書「コメント」節)は静かに無視
            bool allFlag = fields.Length > 3 && fields[3].Equals("all", StringComparison.OrdinalIgnoreCase);

            string colorNoField = fields[1];
            string numPart = colorNoField;
            string target = "";
            int colonIdx = colorNoField.IndexOf(':');
            if (colonIdx >= 0)
            {
                numPart = colorNoField[..colonIdx];
                target = colorNoField[(colonIdx + 1)..];
            }
            string? matchedPattern = SupportedTargetPatterns
                .FirstOrDefault(pat => target.Equals(pat, StringComparison.OrdinalIgnoreCase));
            if (matchedPattern is null)
            { skipped++; continue; } // FrzNormal/FrzHit/Frz等の略記や未対応パターン

            if (!int.TryParse(numPart, out var engineNo) || !engineToLane.TryGetValue(engineNo, out var laneIdx))
            { skipped++; continue; } // 範囲指定(0...7)・スラッシュ複数・グループ(g0等)・未知レーン

            if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var frameVal))
            { skipped++; continue; }

            long tick = snap(frameVal);
            string colorCode = fields[2];

            var bucket = changesByPattern[matchedPattern];
            if (!bucket.TryGetValue(laneIdx, out var list)) bucket[laneIdx] = list = [];
            list.Add((tick, colorCode, allFlag));
        }

        // 状態復元: 各トラックの変化点をtick順に並べ、レーン内の各対象オブジェクトについて
        // 「自分の出現tick以前の最新の変化点」の色を求める(DosExporterのScanTrackの逆変換)。
        // 変化点に対応する対象オブジェクトが1つも無いレーンの変化点は、常に無効な指定として警告する。
        static (string Color, bool AllFlag)? LookupState(List<(long Tick, string Color, bool AllFlag)>? changes, long tick)
        {
            if (changes is null) return null;
            (string Color, bool AllFlag)? found = null;
            foreach (var (t, color, allFlag) in changes)
                if (t <= tick) found = (color, allFlag); else break; // changesは呼び出し側でtick昇順ソート済み
            return found;
        }

        foreach (var byLane in changesByPattern.Values)
            foreach (var list in byLane.Values)
                list.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        List<(long Tick, string Color, bool AllFlag)>? Get(string pattern, int laneIdx) =>
            changesByPattern[pattern].TryGetValue(laneIdx, out var l) ? l : null;

        for (int laneIdx = 0; laneIdx < tab.Lanes.Count; laneIdx++)
        {
            var lane = tab.Lanes[laneIdx];
            var arrowList = Get("", laneIdx);
            var arrowShadowList = Get("ArrowShadow", laneIdx);
            var normalList = Get("Normal", laneIdx);
            var barList = Get("NormalBar", laneIdx);
            var normalShadowList = Get("NormalShadow", laneIdx);
            var hitList = Get("Hit", laneIdx);
            var hitBarList = Get("HitBar", laneIdx);
            var hitShadowList = Get("HitShadow", laneIdx);

            bool hasNoteTrack = arrowList is { Count: > 0 } || arrowShadowList is { Count: > 0 };
            bool hasFreezeTrack = normalList is { Count: > 0 } || barList is { Count: > 0 } ||
                normalShadowList is { Count: > 0 } || hitList is { Count: > 0 } ||
                hitBarList is { Count: > 0 } || hitShadowList is { Count: > 0 };
            if (hasNoteTrack && lane.Notes.Count == 0)
                skipped += (arrowList?.Count ?? 0) + (arrowShadowList?.Count ?? 0);
            if (hasFreezeTrack && lane.Freezes.Count == 0)
                skipped += (normalList?.Count ?? 0) + (barList?.Count ?? 0) + (normalShadowList?.Count ?? 0) +
                           (hitList?.Count ?? 0) + (hitBarList?.Count ?? 0) + (hitShadowList?.Count ?? 0);

            int colorGroup = template.Lanes[laneIdx].ColorGroup;
            lane.ColorOverrides.Clear();

            if (hasNoteTrack && lane.Notes.Count > 0)
            {
                string arrowDefault = ColorDefaults.ResolveSetColorHex(tab, project, colorGroup);
                string arrowShadowDefault = ColorDefaults.ResolveShadowHex(project, colorGroup, "setShadowColor");
                foreach (var tick in lane.Notes)
                {
                    var arrowState = LookupState(arrowList, tick);
                    var shadowState = LookupState(arrowShadowList, tick);
                    string arrowColor = arrowState?.Color ?? arrowDefault;
                    string shadowColor = shadowState?.Color ?? arrowShadowDefault;
                    string? colorOut = string.Equals(arrowColor, arrowDefault, StringComparison.Ordinal) ? null : arrowColor;
                    string? shadowOut = string.Equals(shadowColor, arrowShadowDefault, StringComparison.Ordinal) ? null : shadowColor;
                    if (colorOut is null && shadowOut is null) continue;
                    bool allFlag = (colorOut is not null && (arrowState?.AllFlag ?? false)) ||
                                   (shadowOut is not null && (shadowState?.AllFlag ?? false));
                    lane.ColorOverrides.Add(new NColorEntry(tick, colorOut, null, allFlag, shadowOut));
                }
            }

            if (hasFreezeTrack && lane.Freezes.Count > 0)
            {
                string arrowDefault = ColorDefaults.ResolveSetColorHex(tab, project, colorGroup);
                var (normalDefault, barDefault) =
                    ColorDefaults.ResolveFrzColorsHex(tab, project, colorGroup, arrowDefault);
                var (hitDefault, hitBarDefault) =
                    ColorDefaults.ResolveFrzHitColorsHex(tab, project, colorGroup, normalDefault, barDefault);
                string normalShadowDefault = ColorDefaults.ResolveShadowHex(project, colorGroup, "frzShadowColor");

                foreach (var f in lane.Freezes)
                {
                    var normalState = LookupState(normalList, f.StartTick);
                    var barState = LookupState(barList, f.StartTick);
                    var normalShadowState = LookupState(normalShadowList, f.StartTick);
                    var hitState = LookupState(hitList, f.StartTick);
                    var hitBarState = LookupState(hitBarList, f.StartTick);
                    var hitShadowState = LookupState(hitShadowList, f.StartTick);

                    string normalColor = normalState?.Color ?? normalDefault;
                    string barColor = barState?.Color ?? barDefault;
                    string normalShadowColor = normalShadowState?.Color ?? normalShadowDefault;
                    string hitColor = hitState?.Color ?? hitDefault;
                    string hitBarColor = hitBarState?.Color ?? hitBarDefault;
                    string hitShadowColor = hitShadowState?.Color ?? normalShadowDefault; // Hit専用ヘッダー無し、NormalShadowへフォールバック

                    string? colorOut = string.Equals(normalColor, normalDefault, StringComparison.Ordinal) ? null : normalColor;
                    string? bandOut = string.Equals(barColor, barDefault, StringComparison.Ordinal) ? null : barColor;
                    string? shadowOut = string.Equals(normalShadowColor, normalShadowDefault, StringComparison.Ordinal) ? null : normalShadowColor;
                    string? hitOut = string.Equals(hitColor, hitDefault, StringComparison.Ordinal) ? null : hitColor;
                    string? hitBarOut = string.Equals(hitBarColor, hitBarDefault, StringComparison.Ordinal) ? null : hitBarColor;
                    string? hitShadowOut = string.Equals(hitShadowColor, normalShadowDefault, StringComparison.Ordinal) ? null : hitShadowColor;

                    if (colorOut is null && bandOut is null && shadowOut is null &&
                        hitOut is null && hitBarOut is null && hitShadowOut is null) continue;

                    bool allFlag = (colorOut is not null && (normalState?.AllFlag ?? false)) ||
                                   (bandOut is not null && (barState?.AllFlag ?? false)) ||
                                   (shadowOut is not null && (normalShadowState?.AllFlag ?? false)) ||
                                   (hitOut is not null && (hitState?.AllFlag ?? false)) ||
                                   (hitBarOut is not null && (hitBarState?.AllFlag ?? false)) ||
                                   (hitShadowOut is not null && (hitShadowState?.AllFlag ?? false));

                    lane.ColorOverrides.Add(new NColorEntry(f.StartTick, colorOut, bandOut, allFlag,
                        shadowOut, hitOut, hitBarOut, hitShadowOut));
                }
            }
        }

        if (skipped > 0)
            warnings.Add($"{paramName}: {skipped}件の色変化指定(範囲/グループ/全体色変化/未対応対象部位/" +
                         "対象オブジェクトが存在しないレーンへの指定等)は現在のエディタでは読み込めないため無視しました");
    }

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
