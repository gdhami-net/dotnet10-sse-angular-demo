using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SseDemo.Api;

var builder = WebApplication.CreateBuilder(args);

// A throwaway key for the demo's own tokens. Never ship a key in source.
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("demo-only-signing-key-0123456789abcdef"));

builder.Services.AddSingleton<ConnectionLog>();

builder.Services.AddAuthentication()
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, o =>
    {
        // An API answers 401, it doesn't redirect to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    })
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = "sse-demo",
            ValidAudience = "sse-demo",
            IssuerSigningKey = signingKey,
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("cookie", p => p.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser())
    .AddPolicy("bearer", p => p.AddAuthenticationSchemes("Bearer").RequireAuthenticatedUser());

// The Angular page may be served from another origin (the Playwright check uses
// a second port). Cookies only cross origins with credentials allowed here AND
// withCredentials on the EventSource.
var pageOrigin = builder.Configuration["PageOrigin"] ?? "http://localhost:5100";
builder.Services.AddCors(o => o.AddPolicy("page", p => p.WithOrigins(pageOrigin).AllowCredentials()));

// A typical app turns compression on. text/event-stream is not in the default
// MIME list; Job:CompressEventStream adds it to see what that does.
builder.Services.AddResponseCompression(o =>
{
    if (builder.Configuration.GetValue<bool>("Job:CompressEventStream"))
        o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Append("text/event-stream");
});

var app = builder.Build();

app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseMiddleware<ConnectionLogMiddleware>();
app.UseMiddleware<DropMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/login", async (HttpContext http) =>
{
    var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "demo")], CookieAuthenticationDefaults.AuthenticationScheme));
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, user);
    return TypedResults.NoContent();
});

app.MapPost("/token", () => TypedResults.Ok(new
{
    token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "sse-demo",
        Audience = "sse-demo",
        Claims = new Dictionary<string, object> { ["sub"] = "demo" },
        Expires = DateTime.UtcNow.AddMinutes(10),
        SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
    })
}));

app.MapGet("/jobs/{jobId}/progress", Progress);
app.MapGet("/cookie/jobs/{jobId}/progress", Progress).RequireAuthorization("cookie").RequireCors("page");
app.MapGet("/bearer/jobs/{jobId}/progress", Progress).RequireAuthorization("bearer");

app.MapGet("/debug/connections", (ConnectionLog log) => log.All());
app.MapDelete("/debug/connections", (ConnectionLog log) => { log.Clear(); return TypedResults.NoContent(); });

app.Run();

// The browser sends Last-Event-ID when it reconnects. Start after that step.
static ServerSentEventsResult<JobProgress> Progress(
    [FromHeader(Name = "Last-Event-ID")] string? lastEventId,
    CancellationToken cancellationToken)
{
    var after = int.TryParse(lastEventId, out var id) ? id : 0;
    return TypedResults.ServerSentEvents(Steps(after, cancellationToken));
}

static async IAsyncEnumerable<SseItem<JobProgress>> Steps(
    int after, [EnumeratorCancellation] CancellationToken cancellationToken)
{
    for (var step = after + 1; step <= 10; step++)
    {
        await Task.Delay(300, cancellationToken); // the "work"
        yield return new SseItem<JobProgress>(new JobProgress(step * 10), "progress")
        {
            EventId = step.ToString(),
            ReconnectionInterval = TimeSpan.FromSeconds(1),
        };
    }
}

public record JobProgress(int Percent);

public partial class Program;
