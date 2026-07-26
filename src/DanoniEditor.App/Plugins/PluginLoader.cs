using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using DanoniEditor.PluginContracts;

namespace DanoniEditor.App.Plugins;

/// <summary>
/// ./plugins フォルダ内のDLLを読み込み、<see cref="IEditorPlugin"/>実装を収集するローダー
/// (2026-07-26、プラグイン対応の土台。同日、互換性強化のため以下2点を追加)。
///
/// 互換性強化(2026-07-26):
/// 1. 依存解決 — プラグインがNuGetパッケージ等の依存DLLを持つ場合に備え、プラグインごとの
///    <see cref="AssemblyDependencyResolver"/>(.deps.json準拠)+同フォルダ探索のフォールバックで
///    依存を解決する専用ALC(<see cref="PluginAssemblyLoadContext"/>)を使う。
/// 2. 契約DLLの二重読み込み防止 — DanoniEditor.PluginContracts.dllがplugins内へ誤って
///    コピーされていた場合はスキップし、プラグインの依存解決でも契約アセンブリだけは常に
///    本体側(Default ALC)の読み込み済みインスタンスへ委ねる。二重読み込みを許すと
///    「同名だが別物の型」となりIsAssignableFromが静かにfalseを返す(エラー無しでプラグインが
///    認識されない)ため、最も調査しづらい壊れ方を仕組みで塞ぐ。
///
/// 現状は非アンロード可能(isCollectible: false)の簡易実装。プラグインの有効/無効切替を実行時に
/// 行いたくなった場合は、collectibleなALCへ切り替える拡張余地を残してある。1つのDLL・1つの型の
/// 読み込み失敗が他のプラグインの読み込みを止めないよう、例外は個別にログへ集約する。
/// </summary>
internal static class PluginLoader
{
    private const string ContractsAssemblyName = "DanoniEditor.PluginContracts";

    public sealed record LoadResult(IReadOnlyList<IEditorPlugin> Plugins, IReadOnlyList<string> Errors);

    /// <summary>プラグイン1つぶんの読み込みコンテキスト。依存DLLは.deps.json(あれば)→
    /// プラグインと同じフォルダ、の順で解決する。契約アセンブリだけは常にnullを返して
    /// Default ALC(本体が読み込み済みのインスタンス)へフォールバックさせる。</summary>
    private sealed class PluginAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver? _resolver;
        private readonly string _pluginDir;

        public PluginAssemblyLoadContext(string pluginPath)
            : base($"Plugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: false)
        {
            _pluginDir = Path.GetDirectoryName(pluginPath) ?? "";
            try
            {
                _resolver = new AssemblyDependencyResolver(pluginPath);
            }
            catch
            {
                _resolver = null; // .deps.json無し等。同フォルダ探索のみで解決する
            }
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 契約アセンブリは常に本体側へ委ねる(nullを返すとDefault ALCへフォールバックする)。
            // ここで独自に読み込んでしまうと型不一致で全プラグインが静かに認識されなくなる。
            if (string.Equals(assemblyName.Name, ContractsAssemblyName, StringComparison.OrdinalIgnoreCase))
                return null;

            var path = _resolver?.ResolveAssemblyToPath(assemblyName);
            if (path is not null) return LoadFromAssemblyPath(path);

            // .deps.jsonで解決できない場合、プラグインと同じフォルダを探索する(手置きの依存DLL対応)
            var candidate = Path.Combine(_pluginDir, assemblyName.Name + ".dll");
            if (File.Exists(candidate)) return LoadFromAssemblyPath(candidate);

            return null; // 最後はDefault ALC(本体側)へフォールバック
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }

    public static LoadResult LoadAll(string pluginsDir)
    {
        var plugins = new List<IEditorPlugin>();
        var errors = new List<string>();

        if (!Directory.Exists(pluginsDir))
            return new LoadResult(plugins, errors);

        foreach (var dllPath in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            // 契約DLLがpluginsフォルダへ誤ってコピーされていた場合はスキップ(本体側の同アセンブリと
            // 二重読み込みになり型不一致を引き起こすため)。エラーではなく情報としてログに残す。
            if (string.Equals(Path.GetFileNameWithoutExtension(dllPath), ContractsAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                PluginLog.Write($"{Path.GetFileName(dllPath)}: 契約DLLはpluginsフォルダへ置く必要がございませんの(本体側のものを常に使用します)。スキップしましたわ");
                continue;
            }

            Assembly asm;
            try
            {
                var alc = new PluginAssemblyLoadContext(dllPath);
                asm = alc.LoadFromAssemblyPath(dllPath);
            }
            catch (Exception ex)
            {
                var msg = $"{Path.GetFileName(dllPath)}: 読み込みに失敗しましたわ({ex.Message})";
                errors.Add(msg);
                PluginLog.Write(msg);
                continue;
            }

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }
            catch (Exception ex)
            {
                var msg = $"{Path.GetFileName(dllPath)}: 型情報の取得に失敗しましたわ({ex.Message})";
                errors.Add(msg);
                PluginLog.Write(msg);
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IEditorPlugin).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    var msg = $"{Path.GetFileName(dllPath)}: {type.FullName} に引数無しコンストラクタが無いため読み込めませんでしたわ";
                    errors.Add(msg);
                    PluginLog.Write(msg);
                    continue;
                }
                try
                {
                    if (Activator.CreateInstance(type) is IEditorPlugin plugin)
                    {
                        plugins.Add(plugin);
                        PluginLog.Write($"読み込み成功: {plugin.Id} ({plugin.Name} v{plugin.Version}) [{Path.GetFileName(dllPath)}]");
                    }
                }
                catch (Exception ex)
                {
                    var msg = $"{Path.GetFileName(dllPath)}: {type.FullName} の生成に失敗しましたわ({ex.Message})";
                    errors.Add(msg);
                    PluginLog.Write(msg);
                }
            }
        }

        return new LoadResult(plugins, errors);
    }
}
