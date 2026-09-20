using System.Buffers.Binary;
using System.Text.Json;
using DanoniEditor.Collab.Protocol;

namespace DanoniEditor.Collab.Transport;

/// <summary>
/// 1本のTCP接続上で<see cref="CollabMessage"/>をやり取りする層(設計メモ3.1節)。
/// メッセージは「[4バイト長(ビッグエンディアン)][UTF-8 JSON本文]」の形式で区切る。
/// 送信は複数の呼び出し元から並行して呼ばれ得るため内部でロックし、受信は呼び出し側が
/// 1つのループで順番に呼び出す前提(複数スレッドから同時にReceiveAsyncを呼ばない)。
/// </summary>
public sealed class CollabConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public CollabConnection(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>メッセージを1件送信する。</summary>
    public async Task SendAsync(CollabMessage message, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length > CollabProtocol.MaxMessageBytes)
            throw new InvalidOperationException($"送信メッセージがサイズ上限を超えています: {body.Length}バイト");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, ct).ConfigureAwait(false);
            await _stream.WriteAsync(body, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// メッセージを1件受信する。接続が正常にクローズされた場合はnullを返す
    /// (呼び出し側は受信ループを終了させること)。
    /// </summary>
    public async Task<CollabMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(header, ct).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > CollabProtocol.MaxMessageBytes)
            throw new InvalidDataException($"不正なメッセージ長を受信しました: {length}バイト");

        var body = new byte[length];
        if (!await ReadExactAsync(body, ct).ConfigureAwait(false))
            return null;

        return JsonSerializer.Deserialize<CollabMessage>(body, JsonOptions);
    }

    /// <summary>bufferがちょうど埋まるまで読み込む。相手が正常にクローズした場合はfalseを返す。</summary>
    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0) return false; // 何も読めないまま切断=正常終了とみなす
                throw new IOException("メッセージの途中で接続が切断されました。");
            }
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
