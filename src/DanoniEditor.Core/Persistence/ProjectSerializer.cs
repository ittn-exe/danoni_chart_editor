using System.Text.Json;
using System.Text.Json.Serialization;
using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Persistence;

/// <summary>
/// プロジェクトファイル(./projects/*.json)の保存/読込(仕様書3.1)。
/// schemaVersionを埋め込み、将来のフォーマット変更に備える。
/// </summary>
public static class ProjectSerializer
{
    /// <summary>2026-07-19g: v2=tick分解能1680/拍(旧v1=48/拍)。v1読込時は全tickを×35して移行する。
    /// 2026-07-24: v3=ゲージ別パラメータ(gaugeXXX)の保持方式変更。旧ChartProject.GaugeParams
    /// (Dictionary&lt;string, {perTabCsv:[...]}&gt;、プロジェクト全体・タブ位置インデックス依存)を廃止し、
    /// DifficultyTab.GaugeParams(タブごとのDictionary&lt;string,string&gt;)+ChartProject.GaugeNames
    /// (並び順のみ)へ移行した(タブ削除時にインデックスがズレて値が壊れる不具合の修正)。</summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>v1(48tick/拍)→v2(1680tick/拍)の移行係数</summary>
    private const long V1ToV2Scale = 35;

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語をそのまま保存
    };

    private sealed class Envelope
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public required ChartProject Project { get; set; }
    }

    public static string Serialize(ChartProject project) =>
        JsonSerializer.Serialize(new Envelope { Project = project }, Opts);

    public static ChartProject Deserialize(string json)
    {
        var env = JsonSerializer.Deserialize<Envelope>(json, Opts)
            ?? throw new InvalidDataException("プロジェクトファイルの解析に失敗しました");
        if (env.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException(
                $"このプロジェクトはより新しいバージョンのエディタで作成されています(schemaVersion={env.SchemaVersion})");
        // schemaVersion < Current のマイグレーション
        if (env.SchemaVersion == 1) MigrateV1ToV2(env.Project);
        if (env.SchemaVersion < 3) MigrateV2ToV3(json, env.Project);
        return env.Project;
    }

    public static void Save(ChartProject project, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serialize(project));
    }

    public static ChartProject Load(string path) => Deserialize(File.ReadAllText(path));

    // =====================================================================
    // タブ単体エクスポート(2026-07-23、TBD 5)。合作(複数人で1曲を分担制作)用途で、
    // プロジェクト全体ではなく現在開いている難易度タブ1つ分だけをITTNエディタ形式で書き出す。
    // 合作相手は「開く」/D&Dで自分のプロジェクトへタブとして追加インポートできる。
    // =====================================================================

    /// <summary>タブファイルのJSON構造。"tabExport"キーの有無で通常のプロジェクトファイル(OwnProject)と
    /// 区別する(DroppedFileClassifier参照)。ProjectはTabsが常に1件のみのChartProjectを使い回す。</summary>
    private sealed class TabExportEnvelope
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public bool TabExport { get; set; } = true;
        public required ChartProject Project { get; set; }
    }

    /// <summary>タブ単体エクスポートの読込結果。Sourceはタブ+共通タイミング情報を保持する
    /// 単一タブのChartProject(インポート先が空プロジェクトの場合、タイミングの引き継ぎ元として使う)。</summary>
    public sealed record TabExportResult(ChartProject Source, DifficultyTab Tab);

    /// <summary>カレント難易度タブ1つをタブファイルとしてシリアライズする。tick位置の解釈に必須の
    /// 共通タイミング情報(BPM/拍子/StartNumber/StartFrame/BlankFrame/FrzAttempt/Tuning)と、
    /// 参考用の曲情報の一部を同梱する。Markers/ゲージ共有パラメータ/その他ヘッダーはプロジェクト全体の
    /// 設定のため対象外(2026-07-23ユーザー確定仕様: 「作業としては軽そう」な範囲に留める)。</summary>
    public static string SerializeTabExport(ChartProject project, DifficultyTab tab)
    {
        var single = new ChartProject
        {
            ProjectName = project.ProjectName,
            MusicTitle = project.MusicTitle,
            ArtistName = project.ArtistName,
            ArtistUrl = project.ArtistUrl,
            MusicUrl = project.MusicUrl,
            Tuning = project.Tuning,
            StartFrame = project.StartFrame,
            BlankFrame = project.BlankFrame,
            FrzAttempt = project.FrzAttempt,
            StartNumber = project.StartNumber,
            BpmEvents = [.. project.BpmEvents],
            TimeSignatures = [.. project.TimeSignatures],
            Tabs = [tab],
        };
        return JsonSerializer.Serialize(new TabExportEnvelope { Project = single }, Opts);
    }

    public static TabExportResult DeserializeTabExport(string json)
    {
        var env = JsonSerializer.Deserialize<TabExportEnvelope>(json, Opts)
            ?? throw new InvalidDataException("タブファイルの解析に失敗しました");
        if (env.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException(
                $"このタブファイルはより新しいバージョンのエディタで作成されています(schemaVersion={env.SchemaVersion})");
        if (env.SchemaVersion == 1) MigrateV1ToV2(env.Project);
        if (env.Project.Tabs.Count != 1)
            throw new InvalidDataException("タブファイルの形式が不正です(タブ数が1件ではありません)");
        return new TabExportResult(env.Project, env.Project.Tabs[0]);
    }

    public static void SaveTabExport(ChartProject project, DifficultyTab tab, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, SerializeTabExport(project, tab));
    }

    public static TabExportResult LoadTabExport(string path) => DeserializeTabExport(File.ReadAllText(path));

    /// <summary>v1(48tick/拍)のプロジェクトを読み込んだ際、全tickを×35してv2(1680tick/拍)へ移行する
    /// (2026-07-19g、5連符・7連符対応に伴う分解能引き上げ)。拍子はMeasureIndex基準のため対象外。</summary>
    private static void MigrateV1ToV2(Models.ChartProject p)
    {
        p.BpmEvents = p.BpmEvents.Select(e => e with { Tick = e.Tick * V1ToV2Scale }).ToList();
        p.Markers = p.Markers.Select(m => m with { Tick = m.Tick * V1ToV2Scale }).ToList();
        foreach (var tab in p.Tabs)
        {
            foreach (var lane in tab.Lanes)
            {
                lane.Notes = lane.Notes.Select(t => t * V1ToV2Scale).ToList();
                lane.Freezes = lane.Freezes.Select(f => new Models.FreezeNote(f.StartTick * V1ToV2Scale, f.EndTick * V1ToV2Scale)).ToList();
            }
            tab.SpeedEvents = tab.SpeedEvents.Select(e => e with { Tick = e.Tick * V1ToV2Scale }).ToList();
            tab.BoostEvents = tab.BoostEvents.Select(e => e with { Tick = e.Tick * V1ToV2Scale }).ToList();
        }
    }

    /// <summary>2026-07-24: 旧v2形式のChartProject.GaugeParams(プロジェクト全体のDictionary&lt;string,
    /// {perTabCsv:[...]}&gt;、タブの並び順インデックスに暗黙依存)を、新v3形式(ChartProject.GaugeNames
    /// で並び順のみ保持し、実値はDifficultyTab.GaugeParamsへタブごとに持たせる形)へ変換する。
    /// 型自体を変更済み(ChartProject/DifficultyTabにはもう旧プロパティが存在しない)ため、
    /// 型付きデシリアライズでは値が失われてしまう。ここでは同じjson文字列をJsonDocumentとして
    /// 別途読み直し、旧"gaugeParams"ノードが残っていればその内容を新モデルへ書き戻す
    /// (解析に失敗しても致命的ではないため、諦めて何もしない)。</summary>
    private static void MigrateV2ToV3(string json, Models.ChartProject project)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("project", out var projectEl)) return;
            if (!projectEl.TryGetProperty("gaugeParams", out var gaugeParamsEl) ||
                gaugeParamsEl.ValueKind != JsonValueKind.Object) return;

            var names = new List<string>();
            foreach (var gaugeProp in gaugeParamsEl.EnumerateObject())
            {
                string name = gaugeProp.Name;
                names.Add(name);
                if (!gaugeProp.Value.TryGetProperty("perTabCsv", out var perTabCsvEl) ||
                    perTabCsvEl.ValueKind != JsonValueKind.Array) continue;

                int i = 0;
                foreach (var csvEl in perTabCsvEl.EnumerateArray())
                {
                    if (i >= project.Tabs.Count) break;
                    var csv = csvEl.GetString();
                    if (!string.IsNullOrEmpty(csv))
                    {
                        var tab = project.Tabs[i];
                        (tab.GaugeParams ??= [])[name] = csv;
                    }
                    i++;
                }
            }
            project.GaugeNames = names.Select(n => new Models.GaugeNameDef(n)).ToList();
        }
        catch (JsonException)
        {
            // 旧データの救済に失敗しても致命的ではないため無視する
        }
    }
}

/// <summary>タブ操作(仕様書6.4.2の共通色ルールを含む)</summary>
public static class ProjectOperations
{
    /// <summary>
    /// 難易度タブを並び替える。先頭タブは共通setColor/frzColorの「実体」なので、
    /// 先頭が入れ替わる場合、新しい先頭タブに色実体が無ければ旧先頭から引き継ぐ(仕様書6.4.2)。
    /// </summary>
    public static void MoveTab(ChartProject project, int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;
        var oldFirst = project.Tabs[0];
        var tab = project.Tabs[fromIndex];
        project.Tabs.RemoveAt(fromIndex);
        project.Tabs.Insert(toIndex, tab);

        var newFirst = project.Tabs[0];
        if (!ReferenceEquals(oldFirst, newFirst))
        {
            newFirst.SetColorOverride ??= oldFirst.SetColorOverride;
            newFirst.FrzColorOverride ??= oldFirst.FrzColorOverride;
        }
    }

    /// <summary>
    /// 難易度タブを削除する。先頭タブは共通setColor/frzColorの「実体」なので、先頭タブ自体を削除して
    /// 新しい先頭タブに切り替わる場合、その新しい先頭タブに色実体が無ければ旧先頭から引き継ぐ
    /// (MoveTabと同じ考え方、仕様書6.4.2)。
    /// ゲージパラメータ(DifficultyTab.GaugeParams、2026-07-24でPerTabCsv方式から移行)は
    /// タブ自身が保持するデータになったため、他タブ側のインデックス調整は一切不要
    /// (タブを削除すればそのタブのGaugeParamsも一緒に破棄されるだけで整合する)。
    /// 2026-08-06: タブリンク(LinkedTabId、2026-07-26)の解除もここで行うようCore側へ集約した。
    /// 従来は呼び出し側(MainWindow.CloseTabAt)が事前に相手タブのLinkedTabIdをnullにする作法に
    /// 依存しており、他の経路からRemoveTabが呼ばれると宙に浮いたリンクが残る構造だった
    /// (削除済みタブのTabIdを指したままになり、リンク表示が無言で機能しなくなる)。
    /// </summary>
    public static void RemoveTab(ChartProject project, int index)
    {
        if (index < 0 || index >= project.Tabs.Count) return;
        var oldFirst = project.Tabs[0];
        var removed = project.Tabs[index];
        project.Tabs.RemoveAt(index);
        if (project.Tabs.Count == 0) return;

        // 削除したタブを指しているリンクを解除する(相互参照の相方を残さない)。
        foreach (var t in project.Tabs)
            if (t.LinkedTabId == removed.TabId) t.LinkedTabId = null;

        var newFirst = project.Tabs[0];
        if (!ReferenceEquals(oldFirst, newFirst))
        {
            newFirst.SetColorOverride ??= oldFirst.SetColorOverride;
            newFirst.FrzColorOverride ??= oldFirst.FrzColorOverride;
        }
    }

    /// <summary>
    /// FUJIインポート結果を現在のプロジェクトへ難易度タブとして追加する(仕様書15.3.1)。
    /// プロジェクトが空(タブ0件)ならタイミング情報(blank/BPM/拍子)ごと採用し、
    /// 既存タブがある場合はプロジェクト側のタイミングを維持して警告を返す。
    /// </summary>
    public static List<string> ApplyImport(ChartProject project,
        Import.FujiImportResult result)
    {
        var warnings = new List<string>(result.Warnings);
        if (project.Tabs.Count == 0)
        {
            // 2026-07-17: BlankFrame(ヘッダー記述部由来)とStartNumber(firstNumber-blankFrame)を分離。
            project.StartNumber = result.StartNumber;
            project.BlankFrame = (int)Math.Round(result.BlankFrame);
            project.BpmEvents = [.. result.BpmEvents];
            project.TimeSignatures = [.. result.TimeSignatures];
        }
        else if (Math.Abs(project.BpmEvents[0].Bpm - result.BpmEvents[0].Bpm) > 0.001 ||
                 Math.Abs(project.StartNumber - result.StartNumber) > 0.001)
        {
            warnings.Add("インポート元のタイミング(BPM/StartNumber)がプロジェクトと異なります。プロジェクト側の設定を維持します");
        }
        project.Tabs.Add(result.Tab);
        return warnings;
    }

    /// <summary>ITTNエディタ形式のタブファイル(合作用、2026-07-23、TBD 5)を現在のプロジェクトへ
    /// 難易度タブとして追加する。プロジェクトが空ならタイミング情報(BPM/拍子/StartNumber等)ごと
    /// 採用し、既存タブがある場合はプロジェクト側のタイミングを維持して警告を返す
    /// (Fuji/Skbインポートと同じ方針、ApplyImport(FujiImportResult)参照)。</summary>
    public static List<string> ApplyImport(ChartProject project, ProjectSerializer.TabExportResult result)
    {
        var warnings = new List<string>();
        var source = result.Source;
        if (project.Tabs.Count == 0)
        {
            project.StartNumber = source.StartNumber;
            project.StartFrame = source.StartFrame;
            project.BlankFrame = source.BlankFrame;
            project.FrzAttempt = source.FrzAttempt;
            project.Tuning = source.Tuning;
            project.BpmEvents = [.. source.BpmEvents];
            project.TimeSignatures = [.. source.TimeSignatures];
            if (string.IsNullOrWhiteSpace(project.MusicTitle)) project.MusicTitle = source.MusicTitle;
            if (string.IsNullOrWhiteSpace(project.ArtistName)) project.ArtistName = source.ArtistName;
        }
        else if (Math.Abs(project.BpmEvents[0].Bpm - source.BpmEvents[0].Bpm) > 0.001 ||
                 Math.Abs(project.StartNumber - source.StartNumber) > 0.001)
        {
            warnings.Add("インポート元のタイミング(BPM/StartNumber)がプロジェクトと異なります。プロジェクト側の設定を維持します");
        }
        project.Tabs.Add(result.Tab);
        return warnings;
    }

    /// <summary>SKBインポート結果を現在のプロジェクトへ難易度タブとして追加する(仕様書15.4)。</summary>
    public static List<string> ApplyImport(ChartProject project,
        Import.SkbImportResult result)
    {
        var warnings = new List<string>(result.Warnings);
        if (project.Tabs.Count == 0)
        {
            project.StartNumber = result.StartNumber;
            project.BlankFrame = (int)Math.Round(result.BlankFrame);
            project.BpmEvents = [.. result.BpmEvents];
        }
        else if (Math.Abs(project.StartNumber - result.StartNumber) > 0.001)
        {
            warnings.Add("インポート元のタイミング(StartNumber)がプロジェクトと異なります。プロジェクト側の設定を維持します");
        }
        project.Tabs.Add(result.Tab);
        return warnings;
    }

    /// <summary>
    /// dos.txtインポート結果を現在のプロジェクトへ難易度タブとして追加する(2026-07-25:
    /// FUJI/SKB/タブファイルインポートとインポート先の選択フローを統一。ユーザー要望「インポートの
    /// フローを共通にしてほしい」)。dos.txt単体は1ファイルに複数難易度タブを含み得るため、
    /// DosImporterが組み立てたChartProject全体(result.Project)のタブを「全件」追加する点が
    /// FUJI/SKB/タブファイル(常に1タブ)との違い。プロジェクトが空(タブ0件)ならタイミング情報
    /// (BPM/拍子/StartNumber/BlankFrame)ごと採用し、既存タブがある場合はプロジェクト側の
    /// タイミングを維持して警告を返す(FUJI/SKBと同じ方針、ApplyImport(FujiImportResult)参照)。
    /// </summary>
    public static List<string> ApplyImport(ChartProject project, Import.DosImportResult result)
    {
        var warnings = new List<string>(result.Warnings);
        var source = result.Project;
        if (project.Tabs.Count == 0)
        {
            project.StartNumber = source.StartNumber;
            project.BlankFrame = source.BlankFrame;
            project.BpmEvents = [.. source.BpmEvents];
            project.TimeSignatures = [.. source.TimeSignatures];
        }
        else if (source.BpmEvents.Count == 0 ||
                 Math.Abs(project.BpmEvents[0].Bpm - source.BpmEvents[0].Bpm) > 0.001 ||
                 Math.Abs(project.StartNumber - source.StartNumber) > 0.001)
        {
            warnings.Add("インポート元のタイミング(BPM/StartNumber)がプロジェクトと異なります。プロジェクト側の設定を維持します");
        }
        foreach (var tab in source.Tabs)
        {
            project.Tabs.Add(tab);
        }
        return warnings;
    }

}
