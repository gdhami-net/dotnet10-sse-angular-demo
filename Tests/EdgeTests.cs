using Xunit;

namespace SseDemo.Tests;

public class DropTests(DemoFactory factory) : IClassFixture<DemoFactory>
{
    [Fact]
    public async Task A_dropped_connection_breaks_off_after_the_last_event_it_sent()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/jobs/a/progress?drop=4", HttpCompletionOption.ResponseHeadersRead);
        var reader = new SseReader(await response.Content.ReadAsStreamAsync());

        var ids = new List<string>();
        var broken = false;
        try
        {
            while (await reader.NextBlockAsync() is { } block) ids.Add(SseReader.Field(block, "id")!);
        }
        catch (IOException) { broken = true; }

        Assert.Equal(["1", "2", "3", "4"], ids);
        Assert.True(broken, "the stream should end with an error, not a clean finish");
    }
}

public class CompressionTests(CompressingFactory factory) : IClassFixture<CompressingFactory>
{
    [Fact]
    public async Task Adding_text_event_stream_to_the_compression_list_still_sends_it_uncompressed()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs/a/progress");
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var reader = new SseReader(await response.Content.ReadAsStreamAsync());

        Assert.Equal("identity", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal("1", SseReader.Field((await reader.NextBlockAsync())!, "id"));

        // The same app does compress an ordinary JSON response.
        using var json = new HttpRequestMessage(HttpMethod.Get, "/debug/connections");
        json.Headers.AcceptEncoding.ParseAdd("gzip");
        using var jsonResponse = await client.SendAsync(json);
        Assert.Equal("gzip", jsonResponse.Content.Headers.ContentEncoding.Single());
    }
}

public class QueryTokenTests(LoggingFactory factory) : IClassFixture<LoggingFactory>
{
    [Fact]
    public async Task A_token_in_the_query_string_lands_in_the_request_log()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/jobs/a/progress?access_token=eyJhbGciOi.secret.part", HttpCompletionOption.ResponseHeadersRead);

        Assert.Contains(factory.Lines, l => l.Contains("Request starting") && l.Contains("access_token=eyJhbGciOi.secret.part"));
    }
}
