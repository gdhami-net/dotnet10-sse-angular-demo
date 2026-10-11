# dotnet10-sse-angular-demo

Companion repo for the post
[Live updates from .NET 10 to Angular with Server-Sent Events](https://gdhami.net/blog/live-updates-from-net-10-to-angular-with-server-sent-events).

A .NET 10 minimal API streams job progress with `TypedResults.ServerSentEvents`,
and an Angular 22 page reads it with the browser's own `EventSource`, wrapped
in a small service that exposes a signal. The repo shows:

- the exact bytes the SSE result sends (event type, JSON data, id, retry);
- that `EventSource` reconnects after the server cuts the connection, sends
  `Last-Event-ID`, and that the endpoint can resume from it;
- what `EventSource` does against a bearer-protected endpoint (one 401, then it
  gives up), and the two ways around it: cookies, or a fetch-based reader;
- that closing the tab fires the request's `CancellationToken` on the server.

## Versions it was run on

| Tool | Version |
|---|---|
| .NET SDK | 10.0.201 |
| ASP.NET Core runtime | 10.0.5 |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.5 |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.5 |
| xUnit | 2.9.3 |
| Angular | 22.2.2 |
| TypeScript | 6.0.3 |
| Node | 26.1.0 |
| Playwright | 1.64.0 (Chromium 156.0.8078.4) |
| OS | Windows 11 |

## Run it

```bash
./check.sh          # or: pwsh ./check.ps1
```

That runs the .NET integration tests, builds the Angular page into
`Api/wwwroot`, and runs the Playwright checks (which start the API on ports
5100 and 5101 themselves). Install the browser once with
`npx playwright install chromium` inside `client/` if Playwright asks for it.

To watch it by hand:

```bash
cd client && npx ng build && cd ..
dotnet run --project Api --urls http://localhost:5100
curl -sN -i http://localhost:5100/jobs/7/progress
curl -sN -i -H "Last-Event-ID: 8" http://localhost:5100/jobs/7/progress
```

Open `http://localhost:5100/?drop=4` to see the page lose its connection after
event 4 and carry on.

## Layout

| Path | What it is |
|---|---|
| `Api/Program.cs` | The endpoint (`/jobs/{id}/progress`), a cookie-protected copy (`/cookie/...`), a bearer-protected copy (`/bearer/...`), `/login`, `/token`, and `/debug/connections` |
| `Api/Support.cs` | A middleware that logs every request to a progress endpoint: `Last-Event-ID`, whether an Authorization header or a cookie came with it, the status, and whether the request was aborted |
| `client/src/app/job-progress.ts` | The Angular service: `EventSource` into a signal |
| `client/src/app/fetch-progress.ts` | The fetch-based reader for the bearer case |
| `Tests/` | xUnit + `WebApplicationFactory`, reading the raw stream |
| `client/e2e/sse.spec.ts` | Playwright checks in Chromium |

Query string switches on the endpoint and the page:

- `?drop=N` on the endpoint: the first connection (the one with no
  `Last-Event-ID`) is cut with `HttpContext.Abort()` right after its Nth event
  has been written and flushed. `DropMiddleware` in `Api/Support.cs` does
  this, so the endpoint itself is the code the post shows.
- `?mode=open|cookie|bearer|fetch`, `&api=<other origin>`, `&creds=1` on the
  page pick the case the Playwright checks drive.

## What the checks assert

Integration tests (`dotnet test`):

- The first event is exactly
  `event: progress\ndata: {"percent":10}\nid: 1\nretry: 1000\n\n`, and the
  next one is the same shape with id 2 and 20 percent.
- Headers: `Content-Type: text/event-stream`, `Cache-Control: no-cache,no-store`
  (HttpClient re-orders it when parsing, so the test checks the two flags),
  `Content-Encoding: identity`.
- All ten events arrive, then the response ends.
- The first event can be read while the server is still producing the rest.
- `Last-Event-ID: 8` returns events 9 and 10 only.
- `?drop=4` returns ids 1 to 4 and then the stream ends with an `IOException`.
- Disposing the response mid-stream fires `RequestAborted` on the server.
- The bearer endpoint answers 401 with `WWW-Authenticate: Bearer` without a
  token, and streams with one.
- The cookie endpoint answers 401 without the cookie and streams after `/login`.
- With `text/event-stream` added to the response-compression MIME list and
  `Accept-Encoding: gzip`, the stream still goes out as `identity`, while the
  same app gzips a JSON response.
- A token in the query string (`?access_token=...`) shows up in the
  `Request starting` log line when `Microsoft.AspNetCore.Hosting.Diagnostics`
  logs at Information.

Playwright (`npx playwright test` in `client/`):

- After a cut at event 4, `EventSource` reconnects, sends `Last-Event-ID`, and
  the page ends with ids 1 to 10, each once. After the last event the service
  closes the `EventSource`, and no third connection is made.
- An `EventSource` nobody closes reconnects after a normal end of stream, with
  `Last-Event-ID: 10`.
- Against the bearer endpoint, `EventSource` makes one request with no
  Authorization header, gets 401, goes to `CLOSED`, and makes no second request
  in the next four seconds.
- The fetch-based reader sends the bearer token on both connections and resumes
  with `Last-Event-ID`; the page ends with ids 1 to 10, each once.
- Same origin, the auth cookie goes with the `EventSource` without
  `withCredentials`.
- Cross-origin (page on :5100, API on :5101), the cookie goes only with
  `withCredentials: true`, and only because the API's CORS policy allows
  credentials for that origin. Without it: no cookie, 401.
- Closing the tab after two events fires `RequestAborted` on the server; the
  response never completes.

## Edge cases the post leaves out

- **The last event before a cut is sometimes lost.** With `?drop=4`, the
  `Last-Event-ID` on the reconnect was `4` in nine of sixteen Playwright runs
  and `3` in the other seven (eight runs each for `EventSource` and the fetch
  reader). curl against the same URL got all four events in nine of ten runs.
  The demo cannot show where the event is lost. The ids make it harmless
  (event 4 is sent again on the new connection), which is why the Playwright
  check accepts `3` or `4` and asserts that the page saw each id exactly once.
- **A finished stream gets reconnected.** The SSE protocol has no "done"
  message. When the response ends normally, the browser reconnects. Close the
  `EventSource` on your last event, or have the endpoint answer 204 No Content
  to a reconnect for a finished job (the spec treats any non-200 status as
  "fail the connection", and the browser stops). This demo only does the first.
- **Named events skip `onmessage`.** The endpoint gives every event the type
  `progress`, so the service uses `addEventListener('progress', ...)`.
  `onmessage` only sees events with no type (or the type `message`).
- **The 401 gives you nothing to go on.** The `error` event carries no status
  code. The service can only tell that the `EventSource` is `CLOSED`.
- **The resume is your code.** The framework does not read `Last-Event-ID`;
  the endpoint takes it with `[FromHeader(Name = "Last-Event-ID")]` and decides
  where to start. This demo recomputes the steps; a real job would keep the
  events it sent (or the state they came from) somewhere it can read back.
- **Compression and buffering in ASP.NET Core are handled by the result.**
  `ServerSentEventsResult` sets `Content-Encoding: identity` and calls
  `DisableBuffering()`. A reverse proxy in front of the app (nginx, a CDN) has
  its own buffering and timeouts; the demo has no proxy and says nothing about
  them.
- **Data that is not a string is serialized as JSON** with the app's JSON
  options (camelCase here). `ServerSentEvents` over `IAsyncEnumerable<string>`
  writes the strings as they are.
- **The fetch reader is minimal.** It handles one-line `data:` fields only, and
  treats any non-2xx as final. A token that expires mid-stream would need a
  refresh and a reconnect; it does not do that.
- **Tokens in the query string.** SignalR's browser client does this for
  WebSockets and Server-Sent Events, because browsers cannot set headers on
  those APIs
  ([docs](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0)).
  The default `appsettings.json` keeps `Microsoft.AspNetCore` at Warning, so a
  fresh ASP.NET Core app does not log the request line; proxies and access logs
  usually do.
- **HTTP/1.1 connection limits.** Over HTTP/1.1 a browser keeps about six
  connections per domain, shared by all its tabs, and every open
  `EventSource` holds one ([MDN](https://developer.mozilla.org/en-US/docs/Web/API/EventSource)).
  HTTP/2 multiplexes streams over one connection and raises that limit a lot.
  The demo runs on plain HTTP/1.1 and opens one stream per page.
- The signing key in `Program.cs` is a throwaway for the demo.

## License

MIT
