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
