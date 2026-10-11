using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SseDemo.Api;

namespace SseDemo.Tests;

public class DemoFactory : WebApplicationFactory<Program>
{
    protected virtual IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);
    }

    public async Task<ConnectionRecord> WaitForAsync(Func<ConnectionRecord, bool> match)
    {
        var log = Services.GetRequiredService<ConnectionLog>();
        for (var i = 0; i < 100; i++)
        {
            if (log.All().FirstOrDefault(match) is { } found) return found;
            await Task.Delay(50);
        }
        throw new TimeoutException("No matching connection was logged.");
    }
}

// Same app, with text/event-stream added to the compression MIME list.
public class CompressingFactory : DemoFactory
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Job:CompressEventStream"] = "true",
    };
}

// Same app, with the request log at Information, and every log line kept.
public class LoggingFactory : DemoFactory
{
    public ConcurrentQueue<string> Lines { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l =>
        {
            l.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
            l.AddProvider(new ListLoggerProvider(Lines));
        });
    }

    private sealed class ListLoggerProvider(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string category) => new ListLogger(category, lines);
        public void Dispose() { }
    }

    private sealed class ListLogger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
            => lines.Enqueue($"{category}: {format(state, ex)}");
    }
}

// Splits a text/event-stream into blocks (an event ends at a blank line) and
// returns each block exactly as it came over the wire.
public sealed class SseReader(Stream stream)
{
    private readonly StreamReader _reader = new(stream, Encoding.UTF8);

    public async Task<string?> NextBlockAsync()
    {
        var block = new StringBuilder();
        while (await _reader.ReadLineAsync() is { } line)
        {
            block.Append(line).Append('\n');
            if (line.Length == 0) return block.ToString();
        }
        return block.Length == 0 ? null : block.ToString();
    }

    public static string? Field(string block, string name) => block.Split('\n')
        .Where(l => l.StartsWith(name + ": ")).Select(l => l[(name.Length + 2)..]).FirstOrDefault();
}
