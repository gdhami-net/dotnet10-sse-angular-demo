using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Features;

namespace SseDemo.Api;

// What each request to a progress endpoint looked like, so the tests can ask the
// server what the browser actually sent.
public record ConnectionRecord(
    int Number,
    string Path,
    string? LastEventId,
    bool HadAuthorizationHeader,
    bool HadCookie,
    string? Origin)
{
    public int? Status { get; set; }
    public bool DroppedByServer { get; set; }
    public bool Aborted { get; set; }
    public bool Completed { get; set; }
}

public class ConnectionLog
{
    private readonly ConcurrentQueue<ConnectionRecord> _records = new();
    private int _count;

    public ConnectionRecord Add(HttpRequest r) => Enqueue(new ConnectionRecord(
        Interlocked.Increment(ref _count),
        r.Path + r.QueryString,
        r.Headers["Last-Event-ID"].FirstOrDefault(),
        r.Headers.Authorization.Count > 0,
        r.Cookies.Count > 0,
        r.Headers.Origin.FirstOrDefault()));

    private ConnectionRecord Enqueue(ConnectionRecord c) { _records.Enqueue(c); return c; }

    public IReadOnlyList<ConnectionRecord> All() => _records.ToArray();

    public void Clear() { _records.Clear(); Interlocked.Exchange(ref _count, 0); }
}

public class ConnectionLogMiddleware(RequestDelegate next, ConnectionLog log)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Path.Value!.EndsWith("/progress"))
        {
            await next(http);
            return;
        }

        var record = log.Add(http.Request);
        using var _ = http.RequestAborted.Register(() => record.Aborted = true);
        try
        {
            await next(http);
            record.Completed = !http.RequestAborted.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The client went away mid-stream: the request's token fired.
        }
        finally
        {
            record.Status = http.Response.StatusCode;
            record.DroppedByServer = http.Items.ContainsKey("dropped-by-server");
        }
    }
}

// ?drop=N on a progress URL: the first connection (the one with no Last-Event-ID)
// is cut with HttpContext.Abort() right after its Nth event has been written.
// It sits outside the endpoint so the endpoint stays the code the post shows.
public class DropMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Path.Value!.EndsWith("/progress")
            || !int.TryParse(http.Request.Query["drop"], out var drop)
            || http.Request.Headers.ContainsKey("Last-Event-ID"))
        {
            await next(http);
            return;
        }

        var original = http.Features.Get<IHttpResponseBodyFeature>()!;
        var counting = new EventCountingStream(original.Stream, drop, () =>
        {
            http.Items["dropped-by-server"] = true;
            http.Abort();
        });
        http.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(counting, original));
        try { await next(http); }
        finally { http.Features.Set(original); }
    }

    private sealed class EventCountingStream(Stream inner, int drop, Action cut) : Stream
    {
        private int _events;
        private byte _previous;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (_events >= drop) throw new OperationCanceledException("connection cut by ?drop");
            await inner.WriteAsync(buffer, ct);
            await inner.FlushAsync(ct);
            foreach (var b in buffer.Span)
            {
                // An event ends with a blank line: two '\n' in a row.
                if (b == (byte)'\n' && _previous == (byte)'\n') _events++;
                _previous = b;
            }
            if (_events >= drop) cut();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
