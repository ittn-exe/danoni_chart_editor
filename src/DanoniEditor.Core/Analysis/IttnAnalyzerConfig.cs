namespace DanoniEditor.Core.Analysis;

/// <summary>
/// ITTNアナライザー(難易度解析)の調整係数一式(2026-07-25、`analyzer_and_viewer/analyze.js`の
/// 冒頭CONFIGオブジェクトをそのまま移植したもの)。
/// 「忠実移植パート」(docs/progress_and_tbd_2026-07-24_add.md §1-2の1)として、数値は一切
/// 再校正せず、JS版と一言一句同じ値・同じコメント区分([済]=校正済み/[仮]=要校正)を保持する。
/// 校正はBase/Total値の数値チューニングであり、今回のスコープには含まない。
/// DISCARD(捨て予算解析、実験的機能でbaseRating/totalRatingに不算入)はスコープ外のため移植しない。
///
/// 追記(2026-07-25「BPM強化パート」docs/progress_and_tbd_2026-07-24_add.md §2): VOLTAGE/ALT/MOV/
/// SOFLANのスライディング窓4種(SectionSize/PeakWindow×2/SimulWindow/TimeWindowB)は、フレーム固定窓
/// から小節・拍固定窓(tick基準)へ置き換えた。元のフレーム定数はJS版とのクロスバリデーション用の
/// 参考値としてそのまま残し、実際の計算には新設のMeasures/Beats系[仮]定数(SectionSizeMeasures/
/// PeakWindowMeasures/SimulWindowBeats/TimeWindowBMeasures)を使う。数値はBPM150・4/4を基準とした
/// フレーム値からの換算丸めであり、他の[仮]値と同様に実測データでの再校正待ち。
/// </summary>
public static class IttnAnalyzerConfig
{
    /// <summary>STREAM: 総合密度。APM = 重み付きノーツ数/分</summary>
    public static class Stream
    {
        public const double ApmAt100 = 540;   // [済]
        public const double ApmAt200 = 1151;  // [済]
    }

    /// <summary>VOLTAGE: 局所密度。窓内ノーツ数のスライディング最大</summary>
    public static class Voltage
    {
        public const double SectionSize = 240; // [済] 局所密度を測る窓幅(F)。240F=4秒。JS版由来、参考値として残置
        // 2026-07-25 BPM強化パート: 局所密度窓をフレーム固定→小節固定に置き換え(実際の計算は
        // SectionSizeMeasuresを使う。SectionSizeはJS版クロスバリデーション用の参考値として残す)。
        // 240F≈2.5小節(BPM150・4/4換算、1小節=96F) → 2小節に丸め。[仮](要実測データ校正)
        public const double SectionSizeMeasures = 2; // [仮] 局所密度を測る窓幅(小節数)
        public const double ApmAt100 = 1000;   // [済]
        public const double ApmAt200 = 2300;   // [済]
    }

    /// <summary>CHORD: 同時押し。押し数が多いほど1回あたりの重みが増える</summary>
    public static class Chord
    {
        public const double BaseWeight = 0.2;     // [済] 2個押し1回の基礎点
        public const double BaseIncrement = 0.2;  // [済] 3個押しで加算される重みの初期値
        public const double LoopIncrement = 0.05; // [済] 押し数+1ごとの重み増分
        public const double CpmAt100 = 80;        // [済]
        public const double CpmAt200 = 170;       // [済]
    }

    /// <summary>SOF-LAN: 変速。変速幅×直後ノーツ数 + 非等速区間の常時負荷</summary>
    public static class Soflan
    {
        public const double CoeffA = 1.0;       // [済] 非等速区間を叩き続ける負荷の係数
        public const double CoeffB = 1.0;       // [済] 変速直後の読み直し負荷の係数
        public const double TimeWindowB = 120;  // [済] 変速直後として扱う窓(F)。JS版由来、参考値として残置
        // 2026-07-25 BPM強化パート: 変速直後の読み直し窓をフレーム固定→小節固定に置き換え(実際の
        // 計算はTimeWindowBMeasuresを使う)。120F≈1.25小節(BPM150・4/4換算) → 1小節に丸め。
        // [仮](要実測データ校正)
        public const double TimeWindowBMeasures = 1; // [仮] 変速直後として扱う窓(小節数)
        public const double SpeedCap = 3.0;     // [済] 加速側の偏差上限
        public const double SlowMult = 2.0;     // [済] 減速側の偏差倍率
        public const double SofAt100 = 220;     // [済]
        public const double SofAt200 = 1000;    // [済]
    }

    /// <summary>FREEZE: フリーズアロー。保持中の他ノーツ処理に負荷を課す</summary>
    public static class Freeze
    {
        public const double HoldLoad = 3;   // [済] 保持中に他イベントを1つ処理するごとの負荷
        public const double ArrowBonus = 2; // [済] フリーズ1本あたりの固定点
        public const double FpmAt100 = 847; // [済]
        public const double FpmAt200 = 1694;// [済]
    }

    /// <summary>ONIGIRI: おにぎりレーン密度(レーン数で正規化)</summary>
    public static class Onigiri
    {
        public const double ApmAt100 = 50;  // [済]
        public const double ApmAt200 = 140; // [済]
    }

    /// <summary>JACK: 縦連。速度重みによる連続評価</summary>
    public static class Jack
    {
        public const double MaxGap = 20;      // [仮] この間隔(F)以下を縦連候補とする
        public const double RefGap = 15;      // [仮] 速度重みの基準間隔
        public const double SpeedExp = 1.5;   // [仮] 速度重みの指数
        public const double ComboInc = 0.05;  // [済] 縦連が続くごとの1打あたり重み増分
        public const double JpmAt100 = 2500;  // [仮]
        public const double JpmAt200 = 7500;  // [仮]
    }

    /// <summary>ALT: 上下逆スクロールの見切り難</summary>
    public static class Alt
    {
        public const double AltGap = 120;       // [済] upから見てこのF以内にdownがあればALT状態
        public const double SegGap = 30;        // [済] downから見た逆判定窓 兼 区間連結閾値
        public const double BundleWindow = 6;   // [仮] このF以内のup-downを1認知イベントに束ねる
        public const double BaseAlt = 10;       // [仮] イベント1つの基礎点(存在プレミアム)
        public const double RefIoi = 30;        // [仮] 音価重みの基準間隔
        public const double IoiExp = 1.0;       // [仮] 音価重みの指数
        public const double IoiWMin = 0.5;      // [仮] 音価重みの下限
        public const double IoiWMax = 3.0;      // [仮] 音価重みの上限
        public const double WMain = 1.0;        // [済] 主Alt(上下違い かつ 左右違い)の重み
        public const double WSub = 2.0;         // [済] 副Alt(上下違い かつ 左右同じ)の重み
        public const double WCenter = 1.5;      // [済] 片方がcenterレーン群の場合の重み
        public const double RegMax = 2.0;       // [仮] 規則性係数の上限
        public const double RegCvFull = 0.6;    // [仮] IOI変動係数がこの値で完全に不規則
        public const double RegPatBase = 0.25;  // [仮] 同時押しパターン多様性の基準値
        public const double InsaneMult = 1.5;   // [仮] インセイン係数(縦隣接キーの上下絡み)
        public const double PeakWindow = 600;   // [仮] 局所難所を測る窓幅(F)。JS版由来、参考値として残置
        // 2026-07-25 BPM強化パート: 局所難所窓をフレーム固定→小節固定に置き換え(実際の計算は
        // PeakWindowMeasuresを使う)。600F≈6.25小節(BPM150・4/4換算) → 8小節に丸め。
        // [仮](要実測データ校正)
        public const double PeakWindowMeasures = 8; // [仮] 局所難所を測る窓幅(小節数)
        public const double PeakAlpha = 0.7;    // [仮] 局所max成分の配合比
        public const double VolBeta = 0.3;      // [仮] 総量成分の配合比
        public const double AltAt100 = 7000;    // [仮]
        public const double AltAt200 = 14000;   // [仮]
    }

    /// <summary>MOV: 手の移動負荷 v5「想定運指の破壊」統一モデル(出張⊂移動先)</summary>
    public static class Mov
    {
        public const double SpeedThreshold = 120; // [済] 移動間隔がこのF未満だと速度重みが1.0超になる基準
        public const double SimulWindow = 15;     // [済] 出張候補を探す探索窓(F)。JS版由来、参考値として残置
        // 2026-07-25 BPM強化パート: 出張候補探索窓をフレーム固定→拍固定に置き換え(実際の計算は
        // SimulWindowBeatsを使う)。15F≈0.625拍(BPM150換算) → 2拍に丸め(最終的な同時要求判定は
        // 別定数MOVE_TIMEが担うため、この窓は候補を広めに拾う探索半径として余裕を持たせて選定)。
        // [仮](要実測データ校正)
        public const double SimulWindowBeats = 2; // [仮] 出張候補を探す探索窓(拍数)
        public const double MoveTime = 10;        // [仮・イトトン申告値] 単一方向の片手移動に要する時間(F)
        public const double SimulCooldown = 15;   // [済] 同一simul連打防止のクールダウン(F)
        public const double CostMove = 1.0;       // [済] 手のポジション移動1回の基礎コスト
        public const double CostSimul = 2.0;      // [済] 同一手の複数持ち場同時要求の基礎コスト
        public const double CostSimulDl = 4.0;    // [済] 越境すべき手が自分の持ち場でも忙しい場合のコスト
        public const double BaseExcursion = 3.0;  // [仮] 出張存在プレミアム
        public const double StretchMax = 6.2;     // [仮・イトトン申告値] 変則姿勢(ストレッチ)で届く上限距離(u)
        public const double CostStretch = 2.5;    // [仮] 変則姿勢移動のコスト
        public const double ShiftCooldown = 120;  // [仮] スパン内遷移(段シフト)の同一ペアクールダウン(F)
        public const double ExcMinDist = 3.0;     // [仮] 「片手で覆える距離」ゲート(u)
        public const double RefArmDist = 7.5;     // [仮] 係数1.0となる基準距離(u)
        public const double MoveDistExp = 0.5;    // [仮] 距離の圧縮指数
        public const double DistMin = 0.25;       // [仮] 距離係数の下限
        public const double DistMax = 1.5;        // [仮] 距離係数の上限
        public const double LandLetter = 1.08;    // [仮] 文字キー海への着地係数
        public const double DirectMult = 1.3;     // [仮] 直接移動係数
        public const double CostFinger = 0.5;     // [仮] 指衝突のコスト
        public const double FingerCooldown = 15;  // [仮] 同一ペアの指衝突連打の重複計上防止(F)
        public const double RegMax = 2.0;         // [仮] 不意打ち係数の上限
        public const double RegCvFull = 0.6;      // [仮] 移動間隔CVがこの値で完全に不意打ち
        public const double RegPatBase = 0.25;    // [仮] 移動パターン多様性の基準値
        public const double MovSegGap = 480;      // [仮] 移動イベント列をこの間隔(F)以内で1区間に連結
        public const double PeakWindow = 600;     // [仮] 局所難所を測る窓幅(F)。JS版由来、参考値として残置
        // 2026-07-25 BPM強化パート: 局所難所窓をフレーム固定→小節固定に置き換え(実際の計算は
        // PeakWindowMeasuresを使う)。600F≈6.25小節(BPM150・4/4換算) → 8小節に丸め。
        // [仮](要実測データ校正)
        public const double PeakWindowMeasures = 8; // [仮] 局所難所を測る窓幅(小節数)
        public const double PeakAlpha = 0.7;      // [仮] 局所max成分の配合比
        public const double VolBeta = 0.3;        // [仮] 総量成分の配合比
        public const double MovAt100 = 500;       // [仮]
        public const double MovAt200 = 1000;      // [仮]
    }

    /// <summary>TOTAL: Base値からTotal値への合成</summary>
    public static class Total
    {
        public const double BaseScale = 1.3;      // [済] Base値のスケール除数
        public const double SubBonusRate = 0.15;  // [済] レーダー副要素のボーナス係数
        public const double BaseMustRate = 90;    // [済] Acc係数の基準要求精度(%)
        public const double GaugeScale = 0.03;    // [済] 要求精度1%あたりのAcc係数変動
        public const double AccMin = 0.5;         // [済] Acc係数の下限
        public const double AccMax = 1.5;         // [済] Acc係数の上限
        public const double SatX0 = 50;           // [済] 半飽和点
        public const double KAlt = 0.355;         // [済]
        public const double KMov = 0.355;         // [済]
        public const double KJack = 0.22;         // [済]
        public const double ToolScaleDiv = 5;     // [済] TotalからTool Scaleへの除数
    }
}
