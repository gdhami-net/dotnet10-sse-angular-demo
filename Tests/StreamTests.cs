using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SseDemo.Api;
using Xunit;

namespace SseDemo.Tests;

// Reads the raw text/event-stream bytes through WebApplicationFactory, the same
// bytes curl prints.
public class StreamTests(DemoFactory factory) : IClassFixture<DemoFactory>
{
    [Fact]
    public async Task The_first_event_carries_type_data_id_and_retry_in_that_order()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/jobs/a/progress", HttpCompletionOption.ResponseHeadersRead);
        var reader = new SseReader(await response.Content.ReadAsStreamAsync());

        var first = await reader.NextBlockAsync();

        Assert.Equal("event: progress\ndata: {\"percent\":10}\nid: 1\nretry: 1000\n\n", first);
        Assert.Equal("event: progress\ndata: {\"percent\":20}\nid: 2\nretry: 1000\n\n", await reader.NextBlockAsync());
    }

    [Fact]
    public async Task The_response_headers_turn_off_caching_and_compression()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs/a/progress");
        request.Headers.AcceptEncoding.ParseAdd("gzip, br");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        // The server writes "no-cache,no-store" (curl shows it); HttpClient parses it.
        Assert.True(response.Headers.CacheControl!.NoCache);
        Assert.True(response.Headers.CacheControl.NoStore);
        Assert.Equal("identity", response.Content.Headers.ContentEncoding.Single());
    }

    [Fact]
    public async Task All_ten_events_arrive_and_the_response_then_ends()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/jobs/a/progress", HttpCompletionOption.ResponseHeadersRead);
        var reader = new SseReader(await response.Content.ReadAsStreamAsync());

        var ids = new List<string>();
        while (await reader.NextBlockAsync() is { } block) ids.Add(SseReader.Field(block, "id")!);

        Assert.Equal(Enumerable.Range(1, 10).Select(i => i.ToString()), ids);
    }

    [Fact]
    public async Task Each_event_is_sent_while_the_job_is_still_running()
    {
        using var client = factory.CreateClient();
        await client.DeleteAsync("/debug/connections");
        using var response = await client.GetAsync("/jobs/live/progress", HttpCompletionOption.ResponseHeadersRead);
        var reader = new SseReader(await response.Content.ReadAsStreamAsync());

        await reader.NextBlockAsync();

        // One event is in our hands and the server hasn't finished the response.
        var record = factory.Services.GetRequiredService<ConnectionLog>().All().Single(r => r.Path.Contains("/live/"));
        Assert.Null(record.Status);
        Assert.False(record.Completed);
    }

    [Fact]
    public async Task Last_Event_ID_resumes_after_that_event()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs/a/progress");
        request.Headers.Add("Last-Event-ID", "8");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var reader = new SseReader(await response.Content.ReadAsStreamAsync());

        Assert.Equal("event: progress\ndata: {\"percent\":90}\nid: 9\nretry: 1000\n\n", await reader.NextBlockAsync());
        Assert.Equal("event: progress\ndata: {\"percent\":100}\nid: 10\nretry: 1000\n\n", await reader.NextBlockAsync());
        Assert.Null(await reader.NextBlockAsync());
    }

    [Fact]
    public async Task Closing_the_response_fires_the_request_cancellation_token()
    {
        using var client = factory.CreateClient();
        await client.DeleteAsync("/debug/connections");
        var response = await client.GetAsync("/jobs/gone/progress", HttpCompletionOption.ResponseHeadersRead);
        var reader = new SseReader(await response.Content.ReadAsStreamAsync());
        await reader.NextBlockAsync();

        response.Dispose();

        var record = await factory.WaitForAsync(r => r.Path.Contains("/gone/") && r.Status is not null);
        Assert.True(record.Aborted);
        Assert.False(record.Completed);
    }
}

public class AuthTests(DemoFactory factory) : IClassFixture<DemoFactory>
{
    [Fact]
    public async Task The_bearer_endpoint_answers_401_without_an_Authorization_header()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/bearer/jobs/a/progress");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task The_bearer_endpoint_streams_with_the_header()
    {
        using var client = factory.CreateClient();
        var token = (await (await client.PostAsync("/token", null)).Content.ReadFromJsonAsync<TokenBody>())!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bearer/jobs/a/progress");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var reader = new SseReader(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1", SseReader.Field((await reader.NextBlockAsync())!, "id"));
    }

    [Fact]
    public async Task The_cookie_endpoint_answers_401_without_the_cookie_and_streams_with_it()
    {
        using var anonymous = factory.CreateClient(new() { HandleCookies = false });
        using var refused = await anonymous.GetAsync("/cookie/jobs/a/progress");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var client = factory.CreateClient(); // keeps cookies between calls
        await client.PostAsync("/login", null);
        using var response = await client.GetAsync("/cookie/jobs/a/progress", HttpCompletionOption.ResponseHeadersRead);
        var reader = new SseReader(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1", SseReader.Field((await reader.NextBlockAsync())!, "id"));
    }

    private record TokenBody(string Token);
}
