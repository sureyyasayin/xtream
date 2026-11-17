namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>
/// Okuma akisi: belirtilen sure boyunca hic veri gelmezse <see cref="TimeoutException"/> atar.
/// HttpClient.Timeout yalnizca yanit basliklarina kadar gecerlidir; saglayici govdeyi
/// gondermeyi keserse okuma sonsuza kadar bekler. Bu sarmalayici bunu onler.
/// </summary>
public sealed class IdleTimeoutStream : Stream
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly Stream _inner;
    private readonly TimeSpan _timeout;
    private readonly string _label;

    public IdleTimeoutStream(Stream inner, TimeSpan timeout, string label)
    {
        _inner = inner;
        _timeout = timeout;
        _label = label;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_timeout);
        try
        {
            return await _inner.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{_label}: Xtream sunucusu {(int)_timeout.TotalSeconds} saniyedir veri gondermiyor, indirme kesildi.");
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Esli okuma (XmlReader): zaman asimi ayni sekilde uygulanir.</summary>
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
