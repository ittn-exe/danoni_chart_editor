// このファイルは analyzer_and_viewer/key_map.json (2026-07-25時点) から機械生成した
// 部分データです。手で編集しないこと。ALT/MOVの計算に必要な laneAlt/laneMov/laneGroup(解決済み)/scroll/pos
// のみを保持し、view系(見た目)やhomePosition等の未使用フィールドは含みません。
// 再生成する場合は元のkey_map.jsonから同じロジック(analyze.jsのcalculateAltLevel/calculateMovLevelが
// 行っているkey→laneGroup名の解決)で作り直すこと。
namespace DanoniEditor.Core.Analysis;

/// <summary>
/// key_map.jsonの1レーン分のALT/MOV用情報。
/// analyze.jsのcalculateAltLevel/calculateMovLevel内で都度計算されている
/// 「key→laneGroup名の解決結果」を、レーン名(=LaneDef.LaneId=KEY_MAP側の"lane"フィールド、
/// 両者が完全一致することは確認済み)をキーとして事前計算済みの形で保持する。
/// </summary>
/// <param name="Group">所属するlaneGroup名(例: "down_left")。無所属(ALT/MOV非対応キー種、または
/// どのグループにも属さないレーン)はnull。</param>
/// <param name="Scroll">スクロール方向。"down"(既定)または"up"。key_map.json未指定時は"down"扱い
/// (analyze.jsの `l.scroll || 'down'` に対応)。</param>
/// <param name="Pos">物理座標[col,row]。ALTのインセイン判定・MOVの距離計算に使う。null=未整備。</param>
public sealed record AnalyzerLaneInfo(string? Group, string Scroll, (double X, double Y)? Pos);

/// <summary>key_map.jsonの1キー種分の情報(ALT/MOVの計算に必要な部分のみ)。</summary>
/// <param name="LaneAlt">false=このキー種はALT計算非対象(analyze.jsの`currentKeyMap.laneAlt === false`)。</param>
/// <param name="LaneMov">false=このキー種はMOV計算非対象(analyze.jsの`currentKeyMap.laneMov === false`)。</param>
/// <param name="Lanes">レーン名→AnalyzerLaneInfoの辞書。</param>
public sealed record AnalyzerKeyMapEntry(bool LaneAlt, bool LaneMov, IReadOnlyDictionary<string, AnalyzerLaneInfo> Lanes);

internal static class IttnAnalyzerKeyMapData
{
    public static readonly IReadOnlyDictionary<string, AnalyzerKeyMapEntry> Entries = new Dictionary<string, AnalyzerKeyMapEntry>
    {
        ["5"] = new AnalyzerKeyMapEntry(false, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["left"] = new AnalyzerLaneInfo(null, "down", (15.75, 6.0)),
            ["down"] = new AnalyzerLaneInfo(null, "down", (16.75, 6.0)),
            ["up"] = new AnalyzerLaneInfo(null, "down", (16.75, 5.0)),
            ["right"] = new AnalyzerLaneInfo(null, "down", (17.75, 6.0)),
            ["space"] = new AnalyzerLaneInfo(null, "down", (6.88, 6.0)),
        }),
        ["7"] = new AnalyzerKeyMapEntry(false, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["left"] = new AnalyzerLaneInfo(null, "down", (3.25, 4.0)),
            ["leftdia"] = new AnalyzerLaneInfo(null, "down", (4.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo(null, "down", (5.25, 4.0)),
            ["space"] = new AnalyzerLaneInfo(null, "down", (6.88, 6.0)),
            ["up"] = new AnalyzerLaneInfo(null, "down", (8.25, 4.0)),
            ["rightdia"] = new AnalyzerLaneInfo(null, "down", (9.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo(null, "down", (10.25, 4.0)),
        }),
        ["8"] = new AnalyzerKeyMapEntry(false, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["left"] = new AnalyzerLaneInfo(null, "down", (3.25, 4.0)),
            ["leftdia"] = new AnalyzerLaneInfo(null, "down", (4.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo(null, "down", (5.25, 4.0)),
            ["space"] = new AnalyzerLaneInfo(null, "down", (6.88, 6.0)),
            ["up"] = new AnalyzerLaneInfo(null, "down", (8.25, 4.0)),
            ["rightdia"] = new AnalyzerLaneInfo(null, "down", (9.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo(null, "down", (10.25, 4.0)),
            ["sleft"] = new AnalyzerLaneInfo(null, "down", (13.88, 4.0)),
        }),
        ["11"] = new AnalyzerKeyMapEntry(true, true, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["left"] = new AnalyzerLaneInfo("down_left", "down", (3.25, 4.0)),
            ["leftdia"] = new AnalyzerLaneInfo("down_left", "down", (4.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo("down_left", "down", (5.25, 4.0)),
            ["space"] = new AnalyzerLaneInfo("down_left", "down", (6.88, 6.0)),
            ["up"] = new AnalyzerLaneInfo("down_right", "down", (8.25, 4.0)),
            ["rightdia"] = new AnalyzerLaneInfo("down_right", "down", (9.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo("down_right", "down", (10.25, 4.0)),
            ["sleft"] = new AnalyzerLaneInfo("up_right", "up", (15.75, 6.0)),
            ["sdown"] = new AnalyzerLaneInfo("up_right", "up", (16.75, 6.0)),
            ["sup"] = new AnalyzerLaneInfo("up_right", "up", (16.75, 5.0)),
            ["sright"] = new AnalyzerLaneInfo("up_right", "up", (17.75, 6.0)),
        }),
        ["12"] = new AnalyzerKeyMapEntry(true, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["sleft"] = new AnalyzerLaneInfo("up_right", "up", (8.0, 3.0)),
            ["sdown"] = new AnalyzerLaneInfo("up_right", "up", (9.0, 3.0)),
            ["sup"] = new AnalyzerLaneInfo("up_right", "up", (8.5, 2.0)),
            ["sright"] = new AnalyzerLaneInfo("up_right", "up", (10.0, 3.0)),
            ["oni"] = new AnalyzerLaneInfo("down_right", "down", (6.88, 6.0)),
            ["left"] = new AnalyzerLaneInfo("down_right", "down", (7.75, 5.0)),
            ["leftdia"] = new AnalyzerLaneInfo("down_right", "down", (8.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo("down_right", "down", (8.75, 5.0)),
            ["space"] = new AnalyzerLaneInfo("down_right", "down", (9.25, 4.0)),
            ["up"] = new AnalyzerLaneInfo("down_right", "down", (9.75, 5.0)),
            ["rightdia"] = new AnalyzerLaneInfo("down_right", "down", (10.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo("down_right", "down", (10.75, 5.0)),
        }),
        ["13"] = new AnalyzerKeyMapEntry(true, true, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["left"] = new AnalyzerLaneInfo("down_left", "down", (3.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo("down_left", "down", (4.25, 4.0)),
            ["up"] = new AnalyzerLaneInfo("down_left", "down", (4.0, 3.0)),
            ["right"] = new AnalyzerLaneInfo("down_left", "down", (5.25, 4.0)),
            ["space"] = new AnalyzerLaneInfo("down_left", "down", (6.88, 6.0)),
            ["sleft"] = new AnalyzerLaneInfo("down_right", "down", (8.25, 4.0)),
            ["sdown"] = new AnalyzerLaneInfo("down_right", "down", (9.25, 4.0)),
            ["sup"] = new AnalyzerLaneInfo("down_right", "down", (9.0, 3.0)),
            ["sright"] = new AnalyzerLaneInfo("down_right", "down", (10.25, 4.0)),
            ["tleft"] = new AnalyzerLaneInfo("up_right", "up", (15.75, 6.0)),
            ["tdown"] = new AnalyzerLaneInfo("up_right", "up", (16.75, 6.0)),
            ["tup"] = new AnalyzerLaneInfo("up_right", "up", (16.75, 5.0)),
            ["tright"] = new AnalyzerLaneInfo("up_right", "up", (17.75, 6.0)),
        }),
        ["14"] = new AnalyzerKeyMapEntry(true, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["sleftdia"] = new AnalyzerLaneInfo("up_right", "up", (6.0, 3.0)),
            ["sleft"] = new AnalyzerLaneInfo("up_right", "up", (8.0, 3.0)),
            ["sdown"] = new AnalyzerLaneInfo("up_right", "up", (9.0, 3.0)),
            ["sup"] = new AnalyzerLaneInfo("up_right", "up", (7.5, 2.0)),
            ["sright"] = new AnalyzerLaneInfo("up_right", "up", (10.0, 3.0)),
            ["srightdia"] = new AnalyzerLaneInfo("up_right", "up", (11.0, 3.0)),
            ["oni"] = new AnalyzerLaneInfo("down_right", "down", (6.88, 6.0)),
            ["left"] = new AnalyzerLaneInfo("down_right", "down", (7.75, 5.0)),
            ["leftdia"] = new AnalyzerLaneInfo("down_right", "down", (8.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo("down_right", "down", (8.75, 5.0)),
            ["space"] = new AnalyzerLaneInfo("down_right", "down", (9.25, 4.0)),
            ["up"] = new AnalyzerLaneInfo("down_right", "down", (9.75, 5.0)),
            ["rightdia"] = new AnalyzerLaneInfo("down_right", "down", (10.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo("down_right", "down", (10.75, 5.0)),
        }),
        ["17"] = new AnalyzerKeyMapEntry(false, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["aleft"] = new AnalyzerLaneInfo(null, "down", (2.25, 4.0)),
            ["bleft"] = new AnalyzerLaneInfo(null, "down", (2.75, 5.0)),
            ["adown"] = new AnalyzerLaneInfo(null, "down", (3.25, 4.0)),
            ["bdown"] = new AnalyzerLaneInfo(null, "down", (3.75, 5.0)),
            ["aup"] = new AnalyzerLaneInfo(null, "down", (4.25, 4.0)),
            ["bup"] = new AnalyzerLaneInfo(null, "down", (4.75, 5.0)),
            ["aright"] = new AnalyzerLaneInfo(null, "down", (5.25, 4.0)),
            ["bright"] = new AnalyzerLaneInfo(null, "down", (5.75, 5.0)),
            ["space"] = new AnalyzerLaneInfo(null, "down", (6.88, 6.0)),
            ["cleft"] = new AnalyzerLaneInfo(null, "down", (7.75, 5.0)),
            ["dleft"] = new AnalyzerLaneInfo(null, "down", (8.25, 4.0)),
            ["cdown"] = new AnalyzerLaneInfo(null, "down", (8.75, 5.0)),
            ["ddown"] = new AnalyzerLaneInfo(null, "down", (9.25, 4.0)),
            ["cup"] = new AnalyzerLaneInfo(null, "down", (9.75, 5.0)),
            ["dup"] = new AnalyzerLaneInfo(null, "down", (10.25, 4.0)),
            ["cright"] = new AnalyzerLaneInfo(null, "down", (10.75, 5.0)),
            ["dright"] = new AnalyzerLaneInfo(null, "down", (11.25, 4.0)),
        }),
        ["23"] = new AnalyzerKeyMapEntry(true, true, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["aleft"] = new AnalyzerLaneInfo("up_left", "up", (3.0, 3.0)),
            ["adown"] = new AnalyzerLaneInfo("up_left", "up", (4.0, 3.0)),
            ["aup"] = new AnalyzerLaneInfo("up_left", "up", (3.5, 2.0)),
            ["aright"] = new AnalyzerLaneInfo("up_left", "up", (5.0, 3.0)),
            ["left"] = new AnalyzerLaneInfo("down_left", "down", (2.75, 5.0)),
            ["leftdia"] = new AnalyzerLaneInfo("down_left", "down", (3.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo("down_left", "down", (3.75, 5.0)),
            ["space"] = new AnalyzerLaneInfo("down_left", "down", (4.25, 4.0)),
            ["up"] = new AnalyzerLaneInfo("down_left", "down", (4.75, 5.0)),
            ["rightdia"] = new AnalyzerLaneInfo("down_left", "down", (5.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo("down_left", "down", (5.75, 5.0)),
            ["oni"] = new AnalyzerLaneInfo("down_right", "down", (6.88, 6.0)),
            ["sleft"] = new AnalyzerLaneInfo("down_right", "down", (7.75, 5.0)),
            ["sleftdia"] = new AnalyzerLaneInfo("down_right", "down", (8.25, 4.0)),
            ["sdown"] = new AnalyzerLaneInfo("down_right", "down", (8.75, 5.0)),
            ["sspace"] = new AnalyzerLaneInfo("down_right", "down", (9.25, 4.0)),
            ["sup"] = new AnalyzerLaneInfo("down_right", "down", (9.75, 5.0)),
            ["srightdia"] = new AnalyzerLaneInfo("down_right", "down", (10.25, 4.0)),
            ["sright"] = new AnalyzerLaneInfo("down_right", "down", (10.75, 5.0)),
            ["bleft"] = new AnalyzerLaneInfo("up_right", "up", (8.0, 3.0)),
            ["bdown"] = new AnalyzerLaneInfo("up_right", "up", (9.0, 3.0)),
            ["bup"] = new AnalyzerLaneInfo("up_right", "up", (8.5, 2.0)),
            ["bright"] = new AnalyzerLaneInfo("up_right", "up", (10.0, 3.0)),
        }),
        ["7i"] = new AnalyzerKeyMapEntry(false, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["left"] = new AnalyzerLaneInfo(null, "down", (2.75, 5.0)),
            ["leftdia"] = new AnalyzerLaneInfo(null, "down", (3.75, 5.0)),
            ["down"] = new AnalyzerLaneInfo(null, "down", (4.75, 5.0)),
            ["space"] = new AnalyzerLaneInfo(null, "down", (15.75, 6.0)),
            ["up"] = new AnalyzerLaneInfo(null, "down", (16.75, 6.0)),
            ["rightdia"] = new AnalyzerLaneInfo(null, "down", (16.75, 5.0)),
            ["right"] = new AnalyzerLaneInfo(null, "down", (17.75, 6.0)),
        }),
        ["9A"] = new AnalyzerKeyMapEntry(false, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["left"] = new AnalyzerLaneInfo(null, "down", (3.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo(null, "down", (4.25, 4.0)),
            ["up"] = new AnalyzerLaneInfo(null, "down", (4.0, 3.0)),
            ["right"] = new AnalyzerLaneInfo(null, "down", (5.25, 4.0)),
            ["space"] = new AnalyzerLaneInfo(null, "down", (6.88, 6.0)),
            ["sleft"] = new AnalyzerLaneInfo(null, "down", (8.25, 4.0)),
            ["sdown"] = new AnalyzerLaneInfo(null, "down", (9.25, 4.0)),
            ["sup"] = new AnalyzerLaneInfo(null, "down", (9.0, 3.0)),
            ["sright"] = new AnalyzerLaneInfo(null, "down", (10.25, 4.0)),
        }),
        ["9B"] = new AnalyzerKeyMapEntry(false, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["left"] = new AnalyzerLaneInfo(null, "down", (2.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo(null, "down", (3.25, 4.0)),
            ["up"] = new AnalyzerLaneInfo(null, "down", (4.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo(null, "down", (5.25, 4.0)),
            ["space"] = new AnalyzerLaneInfo(null, "down", (6.88, 6.0)),
            ["sleft"] = new AnalyzerLaneInfo(null, "down", (8.25, 4.0)),
            ["sdown"] = new AnalyzerLaneInfo(null, "down", (9.25, 4.0)),
            ["sup"] = new AnalyzerLaneInfo(null, "down", (10.25, 4.0)),
            ["sright"] = new AnalyzerLaneInfo(null, "down", (11.25, 4.0)),
        }),
        ["9i"] = new AnalyzerKeyMapEntry(true, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["left"] = new AnalyzerLaneInfo("down_left", "down", (2.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo("down_left", "down", (3.25, 4.0)),
            ["up"] = new AnalyzerLaneInfo("down_left", "down", (4.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo("down_left", "down", (5.25, 4.0)),
            ["space"] = new AnalyzerLaneInfo("down_left", "down", (6.88, 6.0)),
            ["sleft"] = new AnalyzerLaneInfo("up_right", "up", (15.75, 6.0)),
            ["sdown"] = new AnalyzerLaneInfo("up_right", "up", (16.75, 6.0)),
            ["sup"] = new AnalyzerLaneInfo("up_right", "up", (16.75, 5.0)),
            ["sright"] = new AnalyzerLaneInfo("up_right", "up", (17.75, 6.0)),
        }),
        ["9d"] = new AnalyzerKeyMapEntry(false, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["left"] = new AnalyzerLaneInfo(null, "down", (3.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo(null, "down", (4.25, 4.0)),
            ["up"] = new AnalyzerLaneInfo(null, "down", (5.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo(null, "down", (5.75, 5.0)),
            ["space"] = new AnalyzerLaneInfo(null, "down", (6.75, 5.0)),
            ["sleft"] = new AnalyzerLaneInfo(null, "down", (7.75, 5.0)),
            ["sdown"] = new AnalyzerLaneInfo(null, "down", (8.25, 4.0)),
            ["sup"] = new AnalyzerLaneInfo(null, "down", (9.25, 4.0)),
            ["sright"] = new AnalyzerLaneInfo(null, "down", (10.25, 4.0)),
        }),
        ["9h"] = new AnalyzerKeyMapEntry(true, true, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["1x"] = new AnalyzerLaneInfo("up_left", "up", (1.5, 2.0)),
            ["ax"] = new AnalyzerLaneInfo("down_left", "down", (2.25, 4.0)),
            ["zx"] = new AnalyzerLaneInfo("down_left", "down", (2.75, 5.0)),
            ["sx"] = new AnalyzerLaneInfo("down_left", "down", (3.25, 4.0)),
            ["yx"] = new AnalyzerLaneInfo("up_right", "up", (7.0, 3.0)),
            ["ux"] = new AnalyzerLaneInfo("up_right", "up", (8.0, 3.0)),
            ["ix"] = new AnalyzerLaneInfo("up_right", "up", (9.0, 3.0)),
            ["hx"] = new AnalyzerLaneInfo("down_right", "down", (7.25, 4.0)),
            ["mx"] = new AnalyzerLaneInfo("down_right", "down", (8.75, 5.0)),
        }),
        ["11L"] = new AnalyzerKeyMapEntry(true, true, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["sleft"] = new AnalyzerLaneInfo("up_left", "up", (3.0, 3.0)),
            ["sdown"] = new AnalyzerLaneInfo("up_left", "up", (4.0, 3.0)),
            ["sup"] = new AnalyzerLaneInfo("up_left", "up", (3.5, 2.0)),
            ["sright"] = new AnalyzerLaneInfo("up_left", "up", (5.0, 3.0)),
            ["left"] = new AnalyzerLaneInfo("down_left", "down", (3.25, 4.0)),
            ["leftdia"] = new AnalyzerLaneInfo("down_left", "down", (4.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo("down_left", "down", (5.25, 4.0)),
            ["space"] = new AnalyzerLaneInfo("down_left", "down", (6.88, 6.0)),
            ["up"] = new AnalyzerLaneInfo("down_right", "down", (8.25, 4.0)),
            ["rightdia"] = new AnalyzerLaneInfo("down_right", "down", (9.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo("down_right", "down", (10.25, 4.0)),
        }),
        ["11W"] = new AnalyzerKeyMapEntry(true, true, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["left"] = new AnalyzerLaneInfo("down_left", "down", (3.25, 4.0)),
            ["leftdia"] = new AnalyzerLaneInfo("down_left", "down", (4.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo("down_left", "down", (5.25, 4.0)),
            ["sleft"] = new AnalyzerLaneInfo("up_left", "up", (1.5, 2.0)),
            ["sdown"] = new AnalyzerLaneInfo("up_left", "up", (6.0, 3.0)),
            ["space"] = new AnalyzerLaneInfo("down_right", "down", (6.88, 6.0)),
            ["sup"] = new AnalyzerLaneInfo("up_right", "up", (7.0, 3.0)),
            ["sright"] = new AnalyzerLaneInfo("up_right", "up", (10.5, 2.0)),
            ["up"] = new AnalyzerLaneInfo("down_right", "down", (8.25, 4.0)),
            ["rightdia"] = new AnalyzerLaneInfo("down_right", "down", (9.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo("down_right", "down", (10.25, 4.0)),
        }),
        ["11i"] = new AnalyzerKeyMapEntry(false, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["left"] = new AnalyzerLaneInfo(null, "down", (3.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo(null, "down", (3.75, 5.0)),
            ["gor"] = new AnalyzerLaneInfo(null, "down", (4.25, 4.0)),
            ["up"] = new AnalyzerLaneInfo(null, "down", (4.0, 3.0)),
            ["right"] = new AnalyzerLaneInfo(null, "down", (5.25, 4.0)),
            ["space"] = new AnalyzerLaneInfo(null, "down", (6.88, 6.0)),
            ["sleft"] = new AnalyzerLaneInfo(null, "down", (8.25, 4.0)),
            ["sdown"] = new AnalyzerLaneInfo(null, "down", (8.75, 5.0)),
            ["siyo"] = new AnalyzerLaneInfo(null, "down", (9.25, 4.0)),
            ["sup"] = new AnalyzerLaneInfo(null, "down", (9.0, 3.0)),
            ["sright"] = new AnalyzerLaneInfo(null, "down", (10.25, 4.0)),
        }),
        ["11j"] = new AnalyzerKeyMapEntry(false, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["gor"] = new AnalyzerLaneInfo(null, "down", (0.75, 3.0)),
            ["left"] = new AnalyzerLaneInfo(null, "down", (3.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo(null, "down", (4.25, 4.0)),
            ["up"] = new AnalyzerLaneInfo(null, "down", (4.0, 3.0)),
            ["right"] = new AnalyzerLaneInfo(null, "down", (5.25, 4.0)),
            ["space"] = new AnalyzerLaneInfo(null, "down", (6.88, 6.0)),
            ["sleft"] = new AnalyzerLaneInfo(null, "down", (8.25, 4.0)),
            ["sdown"] = new AnalyzerLaneInfo(null, "down", (9.25, 4.0)),
            ["sup"] = new AnalyzerLaneInfo(null, "down", (9.0, 3.0)),
            ["sright"] = new AnalyzerLaneInfo(null, "down", (10.25, 4.0)),
            ["siyo"] = new AnalyzerLaneInfo(null, "down", (13.88, 4.0)),
        }),
        ["12i"] = new AnalyzerKeyMapEntry(false, false, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["oni"] = new AnalyzerLaneInfo("up_left", "down", (2.5, 0.5)),
            ["left"] = new AnalyzerLaneInfo("up_left", "down", (3.5, 0.5)),
            ["leftdia"] = new AnalyzerLaneInfo("up_left", "down", (4.5, 0.5)),
            ["down"] = new AnalyzerLaneInfo("up_left", "down", (5.5, 0.5)),
            ["space"] = new AnalyzerLaneInfo("up_center", "down", (7.0, 0.5)),
            ["up"] = new AnalyzerLaneInfo("up_center", "down", (8.0, 0.5)),
            ["rightdia"] = new AnalyzerLaneInfo("up_center", "down", (9.0, 0.5)),
            ["right"] = new AnalyzerLaneInfo("up_center", "down", (10.0, 0.5)),
            ["sleft"] = new AnalyzerLaneInfo("up_right", "down", (11.5, 0.5)),
            ["sdown"] = new AnalyzerLaneInfo("up_right", "down", (12.5, 0.5)),
            ["sup"] = new AnalyzerLaneInfo("up_right", "down", (13.5, 0.5)),
            ["sright"] = new AnalyzerLaneInfo("up_right", "down", (14.5, 0.5)),
        }),
        ["14i"] = new AnalyzerKeyMapEntry(true, true, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["gor"] = new AnalyzerLaneInfo("up_left", "up", (2.75, 5.0)),
            ["space"] = new AnalyzerLaneInfo("up_left", "up", (3.75, 5.0)),
            ["iyo"] = new AnalyzerLaneInfo("up_left", "up", (4.75, 5.0)),
            ["sleft"] = new AnalyzerLaneInfo("down_left", "down", (3.25, 4.0)),
            ["sleftdia"] = new AnalyzerLaneInfo("down_left", "down", (4.25, 4.0)),
            ["sdown"] = new AnalyzerLaneInfo("down_left", "down", (5.25, 4.0)),
            ["sspace"] = new AnalyzerLaneInfo("down_right", "down", (6.88, 6.0)),
            ["sup"] = new AnalyzerLaneInfo("down_right", "down", (8.25, 4.0)),
            ["srightdia"] = new AnalyzerLaneInfo("down_right", "down", (9.25, 4.0)),
            ["sright"] = new AnalyzerLaneInfo("down_right", "down", (10.25, 4.0)),
            ["left"] = new AnalyzerLaneInfo("up_right", "up", (15.75, 6.0)),
            ["down"] = new AnalyzerLaneInfo("up_right", "up", (16.75, 6.0)),
            ["up"] = new AnalyzerLaneInfo("up_right", "up", (16.75, 5.0)),
            ["right"] = new AnalyzerLaneInfo("up_right", "up", (17.75, 6.0)),
        }),
        ["15A"] = new AnalyzerKeyMapEntry(true, true, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["sleft"] = new AnalyzerLaneInfo("up_left", "up", (3.0, 3.0)),
            ["sdown"] = new AnalyzerLaneInfo("up_left", "up", (4.0, 3.0)),
            ["sup"] = new AnalyzerLaneInfo("up_left", "up", (3.5, 2.0)),
            ["sright"] = new AnalyzerLaneInfo("up_left", "up", (5.0, 3.0)),
            ["left"] = new AnalyzerLaneInfo("down_left", "down", (3.25, 4.0)),
            ["leftdia"] = new AnalyzerLaneInfo("down_left", "down", (4.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo("down_left", "down", (5.25, 4.0)),
            ["space"] = new AnalyzerLaneInfo("down_right", "down", (6.88, 6.0)),
            ["up"] = new AnalyzerLaneInfo("down_right", "down", (8.25, 4.0)),
            ["rightdia"] = new AnalyzerLaneInfo("down_right", "down", (9.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo("down_right", "down", (10.25, 4.0)),
            ["tleft"] = new AnalyzerLaneInfo("up_right", "up", (15.75, 6.0)),
            ["tdown"] = new AnalyzerLaneInfo("up_right", "up", (16.75, 6.0)),
            ["tup"] = new AnalyzerLaneInfo("up_right", "up", (16.75, 5.0)),
            ["tright"] = new AnalyzerLaneInfo("up_right", "up", (17.75, 6.0)),
        }),
        ["15B"] = new AnalyzerKeyMapEntry(true, true, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["sleft"] = new AnalyzerLaneInfo("up_left", "up", (3.0, 3.0)),
            ["sdown"] = new AnalyzerLaneInfo("up_left", "up", (4.0, 3.0)),
            ["sup"] = new AnalyzerLaneInfo("up_left", "up", (3.5, 2.0)),
            ["sright"] = new AnalyzerLaneInfo("up_left", "up", (5.0, 3.0)),
            ["left"] = new AnalyzerLaneInfo("down_left", "down", (3.25, 4.0)),
            ["leftdia"] = new AnalyzerLaneInfo("down_left", "down", (4.25, 4.0)),
            ["down"] = new AnalyzerLaneInfo("down_left", "down", (5.25, 4.0)),
            ["space"] = new AnalyzerLaneInfo("down_right", "down", (6.88, 6.0)),
            ["up"] = new AnalyzerLaneInfo("down_right", "down", (8.25, 4.0)),
            ["rightdia"] = new AnalyzerLaneInfo("down_right", "down", (9.25, 4.0)),
            ["right"] = new AnalyzerLaneInfo("down_right", "down", (10.25, 4.0)),
            ["tleft"] = new AnalyzerLaneInfo("up_right", "up", (8.0, 3.0)),
            ["tdown"] = new AnalyzerLaneInfo("up_right", "up", (9.0, 3.0)),
            ["tup"] = new AnalyzerLaneInfo("up_right", "up", (8.5, 2.0)),
            ["tright"] = new AnalyzerLaneInfo("up_right", "up", (10.0, 3.0)),
        }),
        ["16i"] = new AnalyzerKeyMapEntry(true, true, new Dictionary<string, AnalyzerLaneInfo>
        {
            ["gor"] = new AnalyzerLaneInfo("up_left", "up", (2.75, 5.0)),
            ["space"] = new AnalyzerLaneInfo("up_left", "up", (3.75, 5.0)),
            ["iyo"] = new AnalyzerLaneInfo("up_left", "up", (4.75, 5.0)),
            ["sleft"] = new AnalyzerLaneInfo("down_left", "down", (2.25, 4.0)),
            ["sdown"] = new AnalyzerLaneInfo("down_left", "down", (3.25, 4.0)),
            ["sup"] = new AnalyzerLaneInfo("down_left", "down", (4.25, 4.0)),
            ["sright"] = new AnalyzerLaneInfo("down_left", "down", (5.25, 4.0)),
            ["aspace"] = new AnalyzerLaneInfo("down_right", "down", (6.88, 6.0)),
            ["aleft"] = new AnalyzerLaneInfo("down_right", "down", (8.25, 4.0)),
            ["adown"] = new AnalyzerLaneInfo("down_right", "down", (9.25, 4.0)),
            ["aup"] = new AnalyzerLaneInfo("down_right", "down", (10.25, 4.0)),
            ["aright"] = new AnalyzerLaneInfo("down_right", "down", (11.25, 4.0)),
            ["left"] = new AnalyzerLaneInfo("up_right", "up", (15.75, 6.0)),
            ["down"] = new AnalyzerLaneInfo("up_right", "up", (16.75, 6.0)),
            ["up"] = new AnalyzerLaneInfo("up_right", "up", (16.75, 5.0)),
            ["right"] = new AnalyzerLaneInfo("up_right", "up", (17.75, 6.0)),
        }),
    };
}
