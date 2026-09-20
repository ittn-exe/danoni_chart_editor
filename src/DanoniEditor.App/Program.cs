using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DanoniEditor.App;

/// <summary>
/// エントリポイント(2026-09-20、配布ディレクトリ整理対応。ユーザー要望: 「EXE本体と各種DLLを
/// 同じディレクトリに入れているので、一段下の"lib"ディレクトリへ移してEXE本体ディレクトリを
/// スッキリさせたい」)。
///
/// 単独exe配布(dotnet publish -p:PublishSingleFile=true --self-contained true)であっても、
/// WPF特有の少数のネイティブ相互運用DLL(docs/progress_and_tbd_2026-07-22.md 4章参照:
/// D3DCompiler_47_cor3.dll・PenImc_cor3.dll・PresentationNative_cor3.dll・vcruntime140_cor3.dll・
/// wpfgfx_cor3.dll)だけはどうしても単一exeへ埋め込めず、既定では必ずexeと同じ階層に出力される
/// (.NET SDK側の既知の制約で、このエディタ固有の問題ではない)。これらのDLLは実行時にWPF側の
/// 管理コード(PresentationCore/PresentationFramework)内部のDllImportによってPInvoke経由で
/// 遅延ロードされるため、「exeと同じ階層のlibフォルダ」を解決できるようにしておけば、実体を
/// libフォルダへ移動しても解決できる(.csproj側のMoveNativeInteropDllsToLibターゲットが、
/// publish後にこれらのファイルを実際にlibへ移す)。
///
/// 【2026-09-21追記: 実機検証で起動しない不具合が発生、修正】初回実装ではWin32の
/// AddDllDirectory(プロセス全体のネイティブDLL検索パスへの追加)のみで対応していたが、実機では
/// 起動処理の途中(メッセージボックスも出せない段階)で静かに落ちる不具合が発生した。WPF側の
/// DllImportはこちらのコードが直接制御できない(PresentationCore.dll等、フレームワーク側の内部実装)
/// ため、.NET自身のP/Invokeネイティブライブラリ解決が実際にAddDllDirectoryの検索パスを辿って
/// くれるとは限らない(既定の解決経路で完結してしまい、Win32レベルの検索パス追加まで
/// フォールバックしない可能性がある)。そこで、.NET自身が「既定の解決に失敗した場合にのみ」
/// 呼んでくれる専用のフック(AssemblyLoadContext.ResolvingUnmanagedDll)を主対策として追加した。
/// これは「どのDllImport宣言か」に関わらず、既定のネイティブライブラリ解決が失敗した際に必ず
/// 呼ばれるため、WPF側の内部DllImportに対しても確実に介入できる。AddDllDirectory側は、
/// DLL同士のネイティブな相互依存(例: wpfgfx_cor3.dllがvcruntime140_cor3.dllを必要とする、
/// といったOSローダー自身が解決するケース)への保険として引き続き残してある。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        AddLibDirectoryToNativeSearchPath();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>
    /// exeと同じ階層の"lib"サブディレクトリへ、ネイティブDLLの解決を2段構えで対応させる。
    /// libフォルダが存在しない場合(開発時のdotnet build/dotnet run実行時など、単独exe配布以外の
    /// 実行形態。この場合は従来通り対象DLL自体がexeと同階層に無く、素のnet8.0-windows出力フォルダ
    /// から素直に解決されるため、そもそもこの処理は不要)は何もしない。
    /// </summary>
    private static void AddLibDirectoryToNativeSearchPath()
    {
        var libDir = Path.Combine(AppContext.BaseDirectory, "lib");
        if (!Directory.Exists(libDir)) return;

        // 主対策(2026-09-21追加): .NET自身のネイティブDLL解決(WPF内部のDllImport含む)が
        // 既定の場所で見つけられなかった場合に限り呼ばれるフック。ここでlibフォルダから
        // 該当ファイルを探して読み込む。呼び出し元がライブラリ名を拡張子付き/無しどちらで
        // 指定していても対応できるよう、無しの場合は".dll"を補う。
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, libraryName) =>
        {
            var fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? libraryName
                : libraryName + ".dll";
            var candidate = Path.Combine(libDir, fileName);
            return File.Exists(candidate) ? NativeLibrary.Load(candidate) : IntPtr.Zero;
        };

        // 保険(従来からの対策): Win32のネイティブDLL検索パスへも追加しておく。上記フックは
        // .NETの管理下にあるDllImport呼び出しにのみ効くため、DLL同士がOSローダー経由で
        // 直接依存し合うケース(例: 上記いずれかのDLLが、同じlibフォルダにある別のDLLを
        // 自分の依存先として必要とする場合)はこちらで解決される。
        const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        AddDllDirectory(libDir);

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", libDir + Path.PathSeparator + path);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
