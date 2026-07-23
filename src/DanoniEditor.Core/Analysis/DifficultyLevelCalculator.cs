namespace DanoniEditor.Core.Analysis;

/// <summary>
/// danoniplus本体の「レベル計算ツール++」アルゴリズム(2026-08-01、ユーザー要望「ツール値(難易度)計算」)。
/// `danoni_main.js`の`calcLevel`関数をそのまま移植したもの(ナレッジの本体ソースで検証済み、
/// アルゴリズム自体は本体側に一切手を加えていない)。
/// 入力は「1レーン分のフレーム値配列」のリスト(通常ノート)と、同じくレーンごとの
/// フリーズ開始/終了フレームペアのリスト。フレームは本体の実データと同じ整数フレーム値であること
/// (tick単位ではない。呼び出し側でTimingEngine.TickToFrameしたうえで四捨五入すること)。
/// </summary>
public static class DifficultyLevelCalculator
{
    /// <summary>計算結果(本体のcalcLevelが返すオブジェクトに対応)。</summary>
    public sealed record Result(string Tool, double Tate, double Douji, int Push3Cnt);

    /// <summary>
    /// ツール値(難易度レベル)を計算する。
    /// </summary>
    /// <param name="arrowFramesPerLane">レーンごとの通常ノート(フリーズ始点含む前段階ではなく、
    /// 素の通常ノートのみ)のフレーム値配列。</param>
    /// <param name="freezeFramesPerLane">レーンごとのフリーズ(開始フレーム, 終了フレーム)の配列。</param>
    public static Result Calculate(
        IReadOnlyList<IReadOnlyList<long>> arrowFramesPerLane,
        IReadOnlyList<IReadOnlyList<(long Start, long End)>> freezeFramesPerLane)
    {
        int laneCount = arrowFramesPerLane.Count;

        // --- フリーズデータ分解: フリーズ始点を各レーンの矢印データへ組み込む ---
        var arrowData = new List<List<long>>(laneCount);
        var frzStartData = new List<long>();
        var frzEndData = new List<long>();

        for (int j = 0; j < laneCount; j++)
        {
            var lane = new List<long>(arrowFramesPerLane[j]);
            if (j < freezeFramesPerLane.Count)
            {
                foreach (var (start, end) in freezeFramesPerLane[j])
                {
                    lane.Add(start);
                    frzStartData.Add(start);
                    frzEndData.Add(end);
                }
            }
            // sort + 重複除去(本体: sort→filter(indexOf===i))
            arrowData.Add(lane.Distinct().OrderBy(v => v).ToList());
        }

        frzStartData.Sort();
        frzEndData.Sort();

        // --- データ結合・整理: 全レーンの矢印データを連結し、前後にダミーフレームを追加 ---
        var allScorebook = new List<long>();
        foreach (var lane in arrowData) allScorebook.AddRange(lane);

        // 2026-08-01: ノートが1件も無い場合は本体側の想定外入力(allScorebook[0]-100がNaNになる)にあたるため、
        // 当エディタ側の防御的ガードとして"0.00"を返す(本体の挙動そのものではない)。
        if (allScorebook.Count == 0)
            return new Result("0.00", 0, 0, 0);

        allScorebook.Sort();
        allScorebook.Insert(0, allScorebook[0] - 100);
        allScorebook.Add(allScorebook[^1] + 100);
        int allCnt = allScorebook.Count;

        frzEndData.Add(allScorebook[^1]);

        // --- 間隔フレーム数の調和平均計算+補正 ---
        double levelcount = 0;
        int freezenum = 0;
        int pushCnt = 1;
        double twoPushCount = 0;
        var push3List = new List<long>();

        int frzStartIdx = 0, frzEndIdx = 0;

        for (int i = 1; i < allCnt - 2; i++)
        {
            // フリーズ始点の検索
            while (frzStartIdx < frzStartData.Count && frzStartData[frzStartIdx] == allScorebook[i])
            {
                if (allScorebook[i] == allScorebook[i + 1]) break; // 同時押しの場合
                frzStartIdx++;
                freezenum++;
            }

            // フリーズ終点の検索
            while (frzEndIdx < frzEndData.Count && frzEndData[frzEndIdx] < allScorebook[i + 1])
            {
                frzEndIdx++;
                freezenum--;
            }

            if (allScorebook[i + 1] == allScorebook[i] && freezenum == 0)
            {
                // 同時押し補正(フリーズアローが絡まない場合)
                long chk = (allScorebook[i + 2] - allScorebook[i + 1]) * (allScorebook[i] - allScorebook[i - pushCnt]);
                if (chk != 0)
                    twoPushCount += 40.0 / chk;
                else
                    push3List.Add(allScorebook[i]);
                pushCnt++;
            }
            else
            {
                // 単押し+フリーズアローの補正処理
                pushCnt = 1;
                long chk2 = (2 - freezenum) * (allScorebook[i + 1] - allScorebook[i]);
                if (chk2 > 0)
                    levelcount += 2.0 / chk2;
                else
                    push3List.Add(allScorebook[i]);
            }
        }
        levelcount += twoPushCount;
        double leveltmp = levelcount;

        // --- 同方向連打補正: 同一レーン内で隣接フレーム間隔が10未満の場合に加算 ---
        foreach (var lane in arrowData)
        {
            for (int k = 0; k < lane.Count - 1; k++)
            {
                long diff = lane[k + 1] - lane[k];
                if (diff < 10)
                    levelcount += 10.0 / (diff * diff) - 1.0 / 10.0;
            }
        }

        // --- 表示: 曲長・3つ押し補正を行い最終的な難易度レベル値を算出 ---
        int push3Cnt = push3List.Count;
        int calcArrowCnt = allCnt - push3Cnt - 3;
        static double ToDecimal2(double num) => Math.Round(num * 100) / 100;
        double CalcDifLevel(double num) => calcArrowCnt > 0 ? ToDecimal2(num / Math.Sqrt(calcArrowCnt) * 4) : 0;

        double baseDifLevel = CalcDifLevel(levelcount);
        double difLevel = calcArrowCnt > 0 ? ToDecimal2(baseDifLevel * (allCnt - 3) / calcArrowCnt) : 0;

        string tool = allCnt == 3
            ? "0.01"
            : $"{difLevel.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}{(push3Cnt > 0 ? "*" : "")}";

        return new Result(tool, ToDecimal2(baseDifLevel - CalcDifLevel(leveltmp)), CalcDifLevel(twoPushCount), push3Cnt);
    }
}
