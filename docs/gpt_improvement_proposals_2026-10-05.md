# GPT改善提案（danoni_editor）— 2026-10-05

Claudeへ渡す実装検討資料。レビューで確認したコード上のリスクに対し、変更方針とC#例をまとめる。

## 前提・実装時の注意

- この資料は提案であり、パッチではない。プロトコルや永続化形式の変更は互換性を確認してから実装する。
- `ChartProject` 等のモデルは公開mutableコレクションを持つ。キャッシュを追加する場合、変更通知なしに正しさを保てる前提を置かない。
- ダンおに本体が仕様上の正とする挙動（BPM・拍子の許容範囲等）は、根拠となる本体コードまたは実データを確認して決める。
- 以下のコードは設計例。既存の命名、ターゲットフレームワーク、例外表示、ログ機構に合わせて調整する。
- WPFの未処理例外を捕まえて処理続行する案は採らない。描画例外の握りつぶしはUI状態の破損を隠す可能性がある。

## 優先度一覧

| 優先度 | 対象 | 提案 |
|---|---|---|
| P0 | 保存・自動保存 | 同じディレクトリに一時ファイルを書き、フラッシュ後に置換する。manifestは複数プロセス間で直列化する |
| P0 | 共同編集 | UI所有データのスナップショット取得をDispatcherへ移す。接続・Hello・読み取りに上限とタイムアウトを設ける |
| P0 | 入力検証 | インポート／JSON／共同編集の境界でタイミング値とモデル構造を検証する |
| P1 | TimingEngine | タイミングを不変スナップショット化し、区間開始フレームと拍子区間情報を一度だけ計算する |
| P1 | 音源読込 | 古い非同期デコード結果を破棄し、音源サイズ・メモリ上限を扱う |
| P2 | 描画 | 計測後、レーン情報の前処理・描画資源の再利用・キャッシュ上限を入れる |
| P2 | 運用 | 自動保存失敗を利用者に通知し、クラッシュ判定にPIDとプロセス開始時刻を使う |

---

## 1. 原子的なファイル保存とmanifestの競合防止

### 対象

- `src/DanoniEditor.Core/Persistence/ProjectSerializer.cs` (`Save`, `SaveTabExport`)
- `src/DanoniEditor.Core/Persistence/AutoSaveManager.cs` (`WriteSlot`, `SaveManifest`, `SetCrashFlag`)

### 方針

1. 出力先と同じディレクトリにユニークな一時ファイルを作る（同一ボリューム上で置換するため）。
2. 全内容を書いて `Flush(flushToDisk: true)` し、閉じた後に既存ファイルを置換する。初回作成はMove、既存ファイルはReplaceまたは置換Moveを使う。対象ファイルシステム・例外時の挙動をWindowsで確認する。
3. 置換前の失敗では元ファイルを維持する。finallyで残った一時ファイルを削除する。
4. manifestのLoad→変更→Saveを、全プロセスが共有する同期（名前付きMutex等）で囲む。ロック中にslotファイルを書かない。slot更新とmanifest更新の順序・クラッシュ復旧ルールを定める。
5. projectファイルの同時編集上書きは別問題。必要なら保存前に読み込み時の更新日時／ハッシュを比較し、外部変更時に上書きを確認する。

```csharp
static void WriteAtomically(string path, ReadOnlySpan<byte> bytes)
{
    var fullPath = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(fullPath)!;
    Directory.CreateDirectory(directory);
    var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

    try
    {
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                   FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(fullPath))
            File.Replace(temp, fullPath, destinationBackupFileName: null);
        else
            File.Move(temp, fullPath);
    }
    finally
    {
        try { if (File.Exists(temp)) File.Delete(temp); }
        catch (IOException) { /* 掃除失敗は元の保存結果を隠さない。必要ならログ */ }
    }
}
```

上記は概念例。`File.Replace` が対象ボリュームで使えない場合のフォールバック方針を決める。`File.WriteAllText` の文字コード・BOM挙動を変えないよう、文字列からUTF-8へ変換する際は既存仕様に合わせる。

manifest更新の骨格例：

```csharp
using var mutex = new Mutex(initiallyOwned: false, name: @"Local\ITTN.DanoniEditor.AutoSaveManifest");
bool acquired = false;
try
{
    acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
    if (!acquired) throw new TimeoutException("自動保存manifestをロックできません。");

    var entries = LoadManifest(autoSaveDir);
    update(entries);
    SaveManifestAtomically(autoSaveDir, entries);
}
finally
{
    if (acquired) mutex.ReleaseMutex();
}
```

Mutex名のスコープ（同一ユーザー内でよいか）、AbandonedMutexException、ロックタイムアウト、終了時削除の扱いを決める。壊れたmanifestを空配列として上書きすると情報を失うため、読込失敗は別状態として扱い、破損ファイルを退避してユーザーに知らせる案も検討する。

## 2. 共同編集のスナップショットをUI所有データから安全に作る

### 対象

- `src/DanoniEditor.App/Collab/CollabSessionController.cs` (`SnapshotProvider`)
- `src/DanoniEditor.Collab/Session/CollabHost.cs` (`HandleClientAsync`)

### 方針

`HandleClientAsync` は `ConfigureAwait(false)` を使うため、providerはUIスレッドとは限らないスレッドで呼ばれる。UIスレッド所有のProjectをその場でシリアライズしない。

推奨案は、Dispatcher上で整合した深いコピーを作り、そのコピーだけをバックグラウンドでシリアライズする方式。コピー実装が全ネストコレクションを複製することを確認する。コピーAPIがない場合は、Dispatcher上でシリアライズを完了させる（UI停止時間を測定する）。

```csharp
host.SnapshotProvider = async ct =>
{
    var snapshotProject = await dispatcher.InvokeAsync(
        () => DeepCloneProject(document.Project),
        DispatcherPriority.Send,
        ct);

    var json = await Task.Run(() => ProjectSerializer.Serialize(snapshotProject), ct)
                         .ConfigureAwait(false);
    return new SnapshotMessage(json);
};
```

この場合 `SnapshotProvider` の型を `Func<CancellationToken, Task<SnapshotMessage>>` に変更し、Host側もawaitする。セッション終了後に古いdocumentを参照しないよう、providerがクロージャで可変フィールド `_document!` を読むのではなく、開始時のdocumentを捕捉し、停止時にキャンセルする。

## 3. 共同編集の受信制限・バージョン検査・エラー処理

### 対象

- `src/DanoniEditor.Collab/Transport/CollabConnection.cs`
- `src/DanoniEditor.Collab/Protocol/CollabMessage.cs`
- `src/DanoniEditor.Collab/Session/CollabHost.cs`
- `src/DanoniEditor.App/Collab/CollabSessionController.cs`

### 方針

- 固定64 MiBを受け入れるだけでなく、接続段階ごとのサイズ上限を設ける。Helloは小さな上限、snapshotは別上限など、プロトコル種別に応じて制限する。
- 接続数にSemaphoreSlim等の上限を設ける。上限超過は受け入れ直後に切断する。
- TCP接続からHello受信完了まで、さらに各メッセージ受信にタイムアウトを設ける。ホスト停止時のCancellationTokenもリンクする。
- 初回メッセージがHelloであることに加え、ProtocolVersion、表示名長、色の形式を検査する。不一致は明示的な拒否理由を返すか切断する。
- 待受アドレスは既定値をLoopbackまたは利用者が選べる明示的な設定にする。LAN公開が必要ならUIで説明して選択させる。
- `JsonException`、`InvalidDataException`、I/O例外は接続単位で記録して切断する。例外ログには受信本文や個人情報を無制限に含めない。
- `MessageReceived` 等のイベント購読側例外が受信ループを抜けるなら、接続単位の捕捉・記録を追加する。
- AcceptExternalClient経由を含む全クライアント処理Taskを追跡し、DisposeAsyncで終了を待つ。既存コードのようにTaskを破棄すると終了処理中に継続する可能性がある。

長さ上限例（メッセージサイズの上限値は既存の正当なsnapshotサイズを測定して設定する）：

```csharp
if (length < HeaderMinimumBytes || length > maxBytesForThisPhase)
    throw new InvalidDataException($"受信サイズが許容範囲外です: {length}");

using var timeout = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
timeout.CancelAfter(TimeSpan.FromSeconds(10)); // Hello用の例。通常メッセージと値を分ける
var message = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false);
```

受信後のJSONだけを見て種別ごとの上限を判定すると、本文確保後になる。まず全体上限を十分小さく抑え、可能ならヘッダー後・JSON parse前に上限を適用する。ストリーミングJSON化は必要性を計測してから導入する。

## 4. タイミング値とプロジェクト構造の検証

### 対象

- `src/DanoniEditor.Core/Timing/TimingEngine.cs`
- `src/DanoniEditor.Core/Import/DosParamParser.cs`, `DosImporter.cs`
- `src/DanoniEditor.Core/Persistence/ProjectSerializer.cs`
- 共同編集で受信したセル差分・snapshotの適用境界

### 方針

検証を一箇所の `ProjectValidator`（またはTiming専用validator）に集約し、インポート・JSON読込・共同編集適用の各境界で呼ぶ。UI入力だけを検証しても、外部ファイル・旧版データ・ネットワーク入力を保護できない。

少なくとも以下を検討する：

- BPM値、StartNumber、FrameAnchorが有限値であり、BPMが正である。
- tick位置、イベント数、拍子番号が許容範囲内。BPM tickは重複・降順をどう扱うか定義する。
- 拍子分子・分母が正、分母が0でない。`TicksPerMeasure` の計算結果が0にならない。
- 拍子MeasureIndexが非負で、重複・降順をどう扱うか定義する。
- `LinkGridDivision` がnullまたは有効な正値で、リンク先のBPMがRamp式で扱える範囲（正値かつ有限）にある。
- テンプレートのレーン数と各配列・イベント数、tab/song参照などのプロジェクト構造が整合する。
- 入力テキストの長さ、項目数、JSON深度・コレクション数に上限を設ける。

```csharp
public static IReadOnlyList<ValidationIssue> ValidateTiming(
    double startNumber,
    IReadOnlyList<BpmEvent> bpmEvents,
    IReadOnlyList<TimeSignatureEvent> signatures)
{
    var issues = new List<ValidationIssue>();
    if (!double.IsFinite(startNumber))
        issues.Add(Error("startNumber", "StartNumberは有限値である必要があります。"));

    if (bpmEvents.Count == 0 || bpmEvents[0].Tick != 0)
        issues.Add(Error("bpmEvents", "tick 0の初期BPMイベントが必要です。"));

    foreach (var bpm in bpmEvents)
    {
        if (!double.IsFinite(bpm.Bpm) || bpm.Bpm <= 0)
            issues.Add(Error("bpmEvents", $"tick {bpm.Tick} のBPMが不正です。"));
        if (bpm.FrameAnchor is { } anchor && !double.IsFinite(anchor))
            issues.Add(Error("bpmEvents", $"tick {bpm.Tick} のFrameAnchorが不正です。"));
    }

    foreach (var sig in signatures)
    {
        if (sig.MeasureIndex < 0 || sig.Numerator <= 0 || sig.Denominator <= 0 ||
            sig.TicksPerMeasure <= 0)
            issues.Add(Error("timeSignatures", $"小節 {sig.MeasureIndex} の拍子が不正です。"));
    }
    return issues;
}
```

validatorは報告だけにするか例外を投げるかを用途別に選ぶ。読込時はユーザーが直せる詳細なエラーを表示し、ネットワーク入力は無効な操作を適用せず接続を切るなど、境界ごとの扱いを明示する。検査前に危険な演算（除算・巨大配列確保）をしない。

`DosParamParser.ParseNumberList` は `double.Parse` ではなくInvariantCultureの `TryParse` と `double.IsFinite` を用い、不正項目番号を含む診断結果を返す案がよい。単に失敗値を0へ置換すると不正値が別の危険な値になるため避ける。

```csharp
if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
    !double.IsFinite(value))
    return ParseResult.Failure(parameterName, tokenIndex, token);
```

## 5. TimingEngineの計算前処理と検索

### 対象

- `src/DanoniEditor.Core/Timing/TimingEngine.cs`
- `src/DanoniEditor.Core/Models/ChartProject.cs` (`CreateTimingEngine`)

### 方針

`TickToFrame` / `FrameToTick` が呼ばれるたびにsegment開始フレーム配列を作らない。TimingEngine生成時にソート済みイベントと開始フレームを一体の不変配列として構築し、二分探索する。

```csharp
private readonly record struct BpmSegment(BpmEvent Event, double StartFrame);
private readonly BpmSegment[] _segments;

public double TickToFrame(long tick)
{
    int i = FindSegmentByTick(tick); // _segments上のupper-bound二分探索
    var segment = _segments[i];
    return EvaluateTick(segment, tick);
}
```

同様に拍子について、拍子変更ごとの開始小節・開始tickを構築する。`MeasureStartTick`、`TickToMeasurePosition`、`SignatureAt`は該当区間を二分探索して局所計算する。`ChartCanvas.DrawGridAndMeasureLines`ではtickを小節長ずつ進め、その時点の拍子区間を前進ポインタで更新し、毎小節`SignatureAt`を先頭から呼ばない。

### キャッシュ無効化の重要点

現モデルは `BpmEvents` / `TimeSignatures` がmutable listであるため、`CreateTimingEngine()` の結果をChartProjectに無条件キャッシュすると編集後の古い値を返す。安全にキャッシュする選択肢：

1. 編集操作をすべて通す変更APIを作り、タイミング変更時にversionを増やす。
2. TimingEngine構築時にイベントを配列へコピーし、不変snapshotを明示的に所有する。編集側が変更時に新snapshotを作る。
3. キャッシュを入れず、まず `ComputeSegmentStartFrames` のインスタンス内前計算だけ行う。これでも同じengine内の繰り返し呼び出しの割り当てをなくせる。

公開mutableリストの内容署名比較は、比較自体が毎呼び出しO(N)になるため、イベント数・呼び出し頻度を測定しないまま最適化策として採用しない。

### 数値上の注意

- ランプ区間は正の有限BPMが前提。検証なしのlog/expはNaN/Infinityを生む。
- `tick - event.Tick`、小節数×ticksPerMeasure、境界加算でlong overflowしない入力上限を設ける。
- 二分探索はFrameAnchorにより開始frameが単調増加しないケースを考慮する。`FrameToTick`がframe順で検索できる前提をvalidatorで保証するか、従来互換の検索ロジックを維持する。
- 負tick、FrameAnchor境界、同tickイベント等の既存意味をテストで固定してからアルゴリズムを置き換える。

## 6. 音源読み込みの競合・メモリ・重複デコード

### 対象

- `src/DanoniEditor.App/NAudioBgmPlayer.cs` (`OpenAsync`, `DecodeWholeFile`)
- `src/DanoniEditor.App/WaveformDecoder.cs`

### 方針

**古い読込結果を捨てる。** 読込開始ごとに世代番号を増やし、完了後に現在の世代と一致する場合だけ採用する。必要なら前回のCancellationTokenSourceもCancelする。

```csharp
private int _openGeneration;
private CancellationTokenSource? _openCts;

public async Task OpenAsync(string path)
{
    var generation = Interlocked.Increment(ref _openGeneration);
    var cts = new CancellationTokenSource();
    var previous = Interlocked.Exchange(ref _openCts, cts);
    previous?.Cancel();

    var decoded = await Task.Run(() => DecodeWholeFile(path, cts.Token), cts.Token)
                            .ConfigureAwait(true);
    if (generation != Volatile.Read(ref _openGeneration) || cts.IsCancellationRequested)
        return; // stale resultは出力デバイスへ設定しない

    ApplyDecodedAudio(decoded);
}
```

実装ではreaderの読み取りループ内でもtokenを確認し、例外・キャンセル時にCTSを適切にDisposeする。OpenAsyncの失敗を握りつぶす場合も、UIへ失敗を通知する経路を用意する。

**メモリ見積もりと制限。** `AudioFileReader.Length` の意味がフォーマットごとに期待通りか確認し、フレーム数・チャンネル数・総bytesをlongで見積もる。配列長へのint cast前に上限を確認する。上限を超える場合は読み込みを拒否して説明するか、ディスクベース／ストリーミング再生へ切り替える。`List<float>`を容量見積もりなしで全音源分拡張しない。

**波形と再生音のデコード共有。** 可能なら一度デコードしたPCMから波形ピークを作る。ただし現行UIのロード順・PCM寿命を確認し、大きなPCMを保持し続けるコストと再デコード時間のどちらが問題か実測して決める。共有しないならWaveformPeaks用のピーク集計をストリーム処理し、全音源のmono PCMを一時保持しない構成を優先する。

## 7. 描画時の割り当てとTintCache

### 対象

- `src/DanoniEditor.App/ChartCanvas.cs` (`DrawGridAndMeasureLines`, `DrawNotesAndFreezes`, `TintCache`)

### 方針

最初に代表譜面・画面サイズでOnRender時間、割り当てbytes/frame、Gen0 GC頻度を計測する。計測で目立った箇所だけを直す。

- `DrawNotesAndFreezes` のレーンごとの `ToDictionary` / `ToHashSet` は、全tickを対象に毎描画で構築している。イベントtick順を保証できるならソート済み配列をモデル更新時に作る、または辞書をモデルversionに紐づけて編集時だけ再構築する。
- `Pen` / `Brush` は共有可能な不変資源をstatic readonlyで作り、WPFの`Freezable.Freeze()`を呼ぶ。描画ごとの色・太さバリエーションが多い場合は小さなキー付きキャッシュを使う。
- TintCacheは上限付きLRUまたは世代単位のクリアを実装し、BitmapSourceの数・概算バイトを制限する。キーに元画像の安定IDを用い、同名キーが異なる画像を指す可能性を確認する。
- 全面再描画をDrawingVisual等へ分割するのは、プロファイルでカーソル移動時の波形・ノート再描画がボトルネックと確認できた場合に行う。無効化範囲、選択表示、ズーム・スクロールとの同期が増えるため、先に導入しない。

簡易的なキャッシュ上限例：

```csharp
const int MaxTintedImages = 512;
// 追加時に最古のエントリを除去する。実装はDictionary + LinkedList等。
// 上限は実データの画像サイズと色変更ワークロードを計測して設定する。
```

## 8. 自動保存エラーの可視化

### 対象

- `src/DanoniEditor.App/MainWindow.xaml.cs` (`AutoSaveTimer_Tick`)

失敗時に編集を止めない方針は維持しつつ、無言で永続失敗しないようにする。例：最後の自動保存成功時刻を保持し、最初の失敗または連続失敗時にステータス表示、再成功時に回復表示。毎timer tickでダイアログを繰り返さない。

```csharp
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
{
    session.AutoSaveFailureCount++;
    if (session.ShouldNotifyAutoSaveFailure(DateTime.UtcNow))
        ShowStatus($"自動保存に失敗しました。ディスク容量と保存先を確認してください。{ex.Message}");
    LogAutoSaveFailure(session.SlotId, ex);
}
```

自動保存全体のバグを隠す一般的なcatchをなくし、予期しない例外はログに残す。利用者に例外全文や個人パスを不用意に表示しない。

## 9. PID再利用を避けるクラッシュフラグ

### 対象

- `src/DanoniEditor.Core/Persistence/AutoSaveManager.cs`
- `src/DanoniEditor.App/App.xaml.cs`

フラグにPIDだけでなく起動時刻（UTC ticks等）を記録し、現在のProcessの開始時刻と照合する。取得したProcessはusingで破棄し、アクセス拒否・終了競合等を個別に扱う。

```csharp
using var process = Process.GetProcessById(pid);
var actualStartUtc = process.StartTime.ToUniversalTime();
return Math.Abs((actualStartUtc - recordedStartUtc).TotalSeconds) < 2;
```

開始時刻精度・権限・PIDがチェック中に終了する競合を考慮し、判定不能を「生存」または「クラッシュ」と決め打ちしない状態を設ける。`ClearCrashFlag`等の終了経路は失敗をアプリ終了の例外にしない一方、ログに記録する。

## 10. 推奨する実装順と確認項目

1. 不正タイミング値validatorと保存境界の原子化。異常ファイルでも元ファイルが残ることを確認。
2. 共同編集のversion、サイズ、タイムアウト、接続数制限。互換版・非互換版・切断・巨大長さ・無応答Helloを確認。
3. SnapshotをUI thread上で一貫して作り、編集を並行して参加させても例外・欠落がないことを確認。
4. `TimingEngine`の境界ケース（tick 0、イベント境界、負tick、FrameAnchor、ランプ、拍子変更、極端値）を固定してから高速化。
5. 音源を短時間に連続選択、キャンセル、読込失敗、大容量で検証。
6. 描画最適化前後の同一譜面ベンチマークを記録。平均だけでなくp95描画時間と割り当て量を比較。

自動テスト対象外のWPF・音声・ネットワーク部分は、少なくとも手順化した手動確認を行う。数値上限や互換動作は、danoniplus本体の仕様・既存実データに基づいて決める。
