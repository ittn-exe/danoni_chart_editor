namespace DanoniEditor.Core.Import;

/// <summary>外部ファイル取り込み時の値の上限(不正ファイルによる巨大割り当て・オーバーフロー防止)。</summary>
internal static class ImportLimits
{
    /// <summary>1ページあたりの最大tick数(1680tick/拍 × 1万拍)。</summary>
    public const long MaxTicksPerPage = 1680L * 10_000;
}
