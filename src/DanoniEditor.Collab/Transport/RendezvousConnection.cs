using System.Buffers.Binary;
using System.Text.Json;
using DanoniEditor.Collab.Rendezvous;

namespace DanoniEditor.Collab.Transport;

/// <summary>
/// 仲介ヘルパーとのやり取り(<see cref="RendezvousMessage"/>)専用の送受信層(設計メモ4.2節)。
/// <see cref="CollabConnection"/>と同じ「[4バイト長(ビッグエンディアン)][UTF-8 JSON本文]」形式だが、
/// 扱うメッセージ型が異なる(共同編集セッション本体とは無関係の、待ち合わせ専用のやり取りのため)ため、
/// あえてCollabConnectionをジェネリック化せず独立したクラスとして持つ
/// (CollabConnection側は既存テストが通っている状態を保つため、今回は触らない)。
/// </summary>
public sealed class RendezvousConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const int MaxMessageBytes = 4096; // 待ち合わせメッセージは短いテキストのみなので小さめの上限で十分

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public RendezvousConnection(Stream stream, bool ownsStream = true)
    {
        _stream = stream;
        _ownsStream = ownsStream;
    }

    public async Task SendAsync(RendezvousMessage message, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length > MaxMessageBytes)
            throw new InvalidOperationException($"待ち合わせメッセージがサイズ上限を超えています: {body.Length}バイト");

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

    /// <summary>メッセージを1件受信する。接続が正常にクローズされた場合はnullを返す。</summary>
    public async Task<RendezvousMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(header, ct).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxMessageBytes)
            throw new InvalidDataException($"不正な待ち合わせメッセージ長を受信しました: {length}バイト");

        var body = new byte[length];
        if (!await ReadExactAsync(body, ct).ConfigureAwait(false))
            return null;

        return JsonSerializer.Deserialize<RendezvousMessage>(body, JsonOptions);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0) return false;
                throw new IOException("待ち合わせメッセージの途中で接続が切断されました。");
            }
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        if (_ownsStream)
            await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
