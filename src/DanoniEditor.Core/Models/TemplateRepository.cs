namespace DanoniEditor.Core.Models;

/// <summary>
/// ./template 配下の temp_{keyTypeId}.json を読み込むリポジトリ(仕様書3.1)。
/// </summary>
public sealed class TemplateRepository
{
    private readonly string _templateDir;
    private readonly Dictionary<string, KeyTemplate> _cache = [];

    public TemplateRepository(string templateDir) => _templateDir = templateDir;

    public KeyTemplate Get(string keyTypeId)
    {
        if (_cache.TryGetValue(keyTypeId, out var cached)) return cached;
        var path = Path.Combine(_templateDir, $"temp_{keyTypeId}.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"テンプレートが見つかりません: {path}", path);
        var tpl = KeyTemplate.Load(path);
        _cache[keyTypeId] = tpl;
        return tpl;
    }

    /// <summary>利用可能なキー種ID一覧(ファイル名 temp_*.json から列挙)</summary>
    public IEnumerable<string> ListKeyTypeIds() =>
        Directory.EnumerateFiles(_templateDir, "temp_*.json")
            .Select(p => Path.GetFileNameWithoutExtension(p)["temp_".Length..])
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

    /// <summary>指定キー種のキャッシュを破棄する(2026-07-26、テンプレート編集ウィンドウでの保存直後に
    /// 呼び、次回Get呼び出し時にディスクの最新内容を再読込させる)。</summary>
    public void Invalidate(string keyTypeId) => _cache.Remove(keyTypeId);
}
