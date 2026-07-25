using System.Text.Json;
using System.Text.Json.Serialization;

namespace DanoniEditor.Core.Models;

/// <summary>
/// キー種テンプレート(仕様書 4.2 / 4.2.1)。レーン構造の定義のみを持つ。
/// ./template/temp_{keyTypeId}.json として保存される。
/// </summary>
public sealed class KeyTemplate
{
    public required string KeyTypeId { get; init; }          // 例: "5", "11L", "23"
    public required string KeyTypeName { get; init; }        // 例: "5key"
    public required int KeyCount { get; init; }
    public string? Comment { get; init; }

    // --- プレイテスト座標計算用メタ(4.2.1) ---
    public required double Blank { get; init; }              // 隣接ステップゾーン間距離(px)
    public required double DivideCnt { get; init; }          // 上下折返し区切り(= 本体div - 1)
    public required double PosMax { get; init; }             // 位置インデックス最大値(= 本体divMax)

    public required IReadOnlyList<LaneDef> Lanes { get; init; }

    /// <summary>キー種の追加パターン(2026-07-26e要望対応、danoniplus本家の「キーパターン」概念)。
    /// 本家では同じキー種(例: 11key)に対して複数の物理キー配置・色・回転・ステップゾーン位置の組が
    /// 定義され得るが(danoni_constants.jsのkeyCtrl11_0/keyCtrl11_1等)、譜面データの実体(chara)自体は
    /// パターンに依存しない共通のもの。ここでは「パターン0」を既存のLanes等の基底フィールドとして扱い、
    /// パターン1以降だけをExtraPatternsに追加データとして持つ(既存テンプレートとの後方互換のため、
    /// 未指定時は空配列=従来通りパターン0のみのキー種として読み込める)。
    /// プレイテスト画面の見た目・キー入力にのみ影響し、エディタ本体の譜面ビューの列並び・データ名・
    /// FUJI互換番号・キーボードモード入力キーには一切影響しない(2026-07-26e考察の確定仕様)。</summary>
    public IReadOnlyList<KeyPattern> ExtraPatterns { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static KeyTemplate Load(string path) =>
        JsonSerializer.Deserialize<KeyTemplate>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidDataException($"テンプレートの読み込みに失敗: {path}");

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));

    /// <summary>
    /// プレイテスト画面のステップゾーンX座標(仕様書4.2、danoni_main.js getArrowSettings準拠)。
    /// stepX[j] = blank × stdPos + (playingWidth − 50) / 2
    /// </summary>
    public double GetStepX(int laneIndex, double playingWidth)
    {
        const double C_ARW_WIDTH = 50;
        var posj = Lanes[laneIndex].PosIndex;
        var stdPos = posj - ((posj > DivideCnt ? PosMax : 0) + DivideCnt) / 2;
        return Blank * stdPos + (playingWidth - C_ARW_WIDTH) / 2;
    }

    /// <summary>ステップゾーンY座標(全キー種共通固定値、g_posObj.stepY)</summary>
    public const double StepY = 70;

    /// <summary>パターン込みの通し件数(パターン0 + ExtraPatterns)。パターン選択UIの選択肢件数等に使う。</summary>
    [JsonIgnore]
    public int PatternCount => 1 + ExtraPatterns.Count;

    /// <summary>指定パターン番号(0=既定パターン)を適用した「実効テンプレート」を返す(2026-07-26e)。
    /// パターン0またはExtraPatternsの範囲外を指定した場合はthisをそのまま返す(コピーしない)。
    /// プレイテスト画面がこのメソッドで得たテンプレートを使って座標計算・キーマップ構築を行うことで、
    /// パターン差し替えロジックをここに集約する。</summary>
    public KeyTemplate WithPattern(int patternIndex)
    {
        if (patternIndex <= 0 || patternIndex > ExtraPatterns.Count) return this;
        var pattern = ExtraPatterns[patternIndex - 1];
        if (pattern.LaneOverrides.Count != Lanes.Count)
            throw new InvalidDataException(
                $"テンプレート'{KeyTypeId}'のパターン{patternIndex}のレーン件数({pattern.LaneOverrides.Count})が" +
                $"本体のレーン件数({Lanes.Count})と一致しませんの");

        var lanes = Lanes.Select((lane, i) =>
        {
            var o = pattern.LaneOverrides[i];
            return new LaneDef
            {
                LaneId = lane.LaneId,
                DataName = lane.DataName,
                FrzDataNameOverride = lane.FrzDataNameOverride,
                DisplayOrder = lane.DisplayOrder,
                KeyAssign = o.KeyAssign,
                KeyboardInputKeys = lane.KeyboardInputKeys,
                ColorGroup = o.ColorGroup,
                PosIndex = o.PosIndex,
                ScrollDirection = o.ScrollDirection,
                NoteGraphic = o.NoteGraphic,
                RotationAngle = o.RotationAngle,
                EngineLaneNum = lane.EngineLaneNum,
                FujiLaneNum = lane.FujiLaneNum,
            };
        }).ToList();

        return new KeyTemplate
        {
            KeyTypeId = KeyTypeId,
            KeyTypeName = KeyTypeName,
            KeyCount = KeyCount,
            Comment = Comment,
            Blank = pattern.Blank,
            DivideCnt = pattern.DivideCnt,
            PosMax = pattern.PosMax,
            Lanes = lanes,
            ExtraPatterns = ExtraPatterns,
        };
    }
}

/// <summary>キー種の追加パターン1件分(2026-07-26e、danoniplus本家の「キーパターン」概念)。
/// LaneOverridesはKeyTemplate.Lanesと同じ件数・同じ並びで1:1(インデックス)対応する。</summary>
public sealed class KeyPattern
{
    /// <summary>パターンの表示名(任意、未指定可)。本家に正式名称が無いパターンも多いため、
    /// UI側では未指定時「パターンN」のように自動採番して表示する。</summary>
    public string? Name { get; init; }

    public required double Blank { get; init; }
    public required double DivideCnt { get; init; }
    public required double PosMax { get; init; }

    public required IReadOnlyList<LanePatternOverride> LaneOverrides { get; init; }
}

/// <summary>パターンごとのレーン上書き値1件分(2026-07-26e)。LaneId/DataName等のレーンの素性は
/// パターン非依存のためここには含めず、KeyTemplate.Lanesの対応するインデックス側を参照する。</summary>
public sealed class LanePatternOverride
{
    [JsonConverter(typeof(KeyAssignConverter))]
    public required IReadOnlyList<string> KeyAssign { get; init; }

    public required int ColorGroup { get; init; }
    public required double PosIndex { get; init; }

    /// <summary>スクロール方向: "down"(標準) / "up"(折返し上段等の逆行)</summary>
    public required string ScrollDirection { get; init; }

    public required string NoteGraphic { get; init; }
    public required double RotationAngle { get; init; }
}

/// <summary>レーン定義(仕様書4.2)</summary>
public sealed class LaneDef
{
    public required string LaneId { get; init; }              // 例: "left", "sleft"
    public required string DataName { get; init; }            // dos.txt出力ベース名
    public string? FrzDataNameOverride { get; init; }         // frz名が例外の場合のみ(例: leftdia→frzLdia)
    public required int DisplayOrder { get; init; }

    /// <summary>
    /// キー割当の表示ラベル。JSON上は単一なら文字列("S"、"←")、複数なら配列(["E","R"])。
    /// 手編集のしやすさを優先し、どちらの形式も受け付ける。
    /// </summary>
    [JsonConverter(typeof(KeyAssignConverter))]
    public required IReadOnlyList<string> KeyAssign { get; init; }

    /// <summary>SKB操作モード(キーボード入力、2026-07-21)専用のノート入力キー。KeyAssignと書式は
    /// 同じだが別データとして分離している(KeyAssignは本家の実プレイキー割当であり、5key等の多くの
    /// キー種で矢印/Spaceを含む。矢印/Space/BackSpace等はキーボードモードのカーソル移動に予約済みの
    /// ため、そのままでは衝突する)。未指定(空配列)のレーンはキーボードモードでの入力を受け付けない。
    /// 標準テンプレートへの値の追加は別途対応(2026-07-21時点は空のまま出荷)。</summary>
    [JsonConverter(typeof(KeyAssignConverter))]
    public IReadOnlyList<string> KeyboardInputKeys { get; init; } = [];

    public required int ColorGroup { get; init; }
    public required double PosIndex { get; init; }            // 本体pos値(9hkey等で小数あり)

    /// <summary>スクロール方向: "down"(標準) / "up"(折返し上段等の逆行)</summary>
    public required string ScrollDirection { get; init; }

    public required string NoteGraphic { get; init; }         // arrow/onigiri/giko/iyo/c/monar/morara
    public required double RotationAngle { get; init; }
    public required int EngineLaneNum { get; init; }          // ncolor_data等が参照する本体内部レーン番号

    /// <summary>FUJIエディタのデータ列番号(2026-07-18c)。FUJIの$dosformat [aNN]順は本体エンジン順と
    /// 異なるキー種がある(23keyでは本体=a,b,main,oni,s / FUJI=a,main,oni,s,b)ため、
    /// FUJIインポート/エクスポート専用の列番号として分離した。null=EngineLaneNumと同じ(5key等)。</summary>
    public int? FujiLaneNum { get; init; }

    /// <summary>FUJI列番号の実効値。明示指定が無い場合はDisplayOrderを使う
    /// (2026-07-19: FUJIエディタは全キー種を収録していないため、「エディタの表示順=FUJIの列順」
    /// という運用に統一するというユーザー方針。表示順をFUJI互換に整備したテンプレートが前提)。</summary>
    public int EffectiveFujiLane => FujiLaneNum ?? DisplayOrder;

    /// <summary>Oni(おにぎり)レーン判定(仕様書4.2確定: noteGraphicで判定)</summary>
    [JsonIgnore]
    public bool IsOnigiri => NoteGraphic == "onigiri";

    /// <summary>列見出し等に使う表示用ラベル(複数キーは"/"連結)</summary>
    [JsonIgnore]
    public string KeyAssignLabel => string.Join("/", KeyAssign);

    /// <summary>キーボードモードのレーンラベル表示用(2026-07-22)。KeyAssignLabelと同様に"/"連結。
    /// KeyboardInputKeys未設定のレーンは空文字列になる。</summary>
    [JsonIgnore]
    public string KeyboardInputKeysLabel => string.Join("/", KeyboardInputKeys);
}

/// <summary>keyAssignの「文字列 or 文字列配列」両対応コンバータ(書き戻しも元の形式を維持)</summary>
public sealed class KeyAssignConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return [reader.GetString()!];
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                list.Add(reader.GetString()!);
            return list;
        }
        throw new JsonException("keyAssignは文字列または文字列配列で指定してください");
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1) { writer.WriteStringValue(value[0]); return; }
        writer.WriteStartArray();
        foreach (var v in value) writer.WriteStringValue(v);
        writer.WriteEndArray();
    }
}
