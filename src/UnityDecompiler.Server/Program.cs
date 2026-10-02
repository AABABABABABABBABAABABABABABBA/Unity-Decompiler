using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using UnityDecompiler.Server;

var options = ServerOptions.FromEnvironment();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<JobManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobManager>());

// Allow large game uploads.
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = options.MaxUploadBytes;
    o.ValueLengthLimit = int.MaxValue;
});
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.MaxUploadBytes);

var app = builder.Build();

var auth = new PasswordGate(options.AccessPassword);

// --- password gate (only if ACCESS_PASSWORD is set) ------------------------
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "/";
    if (!auth.Enabled || path is "/login" or "/api/login" || path.StartsWith("/assets/"))
    {
        await next();
        return;
    }
    if (auth.IsAuthenticated(context.Request))
    {
        await next();
        return;
    }
    if (path.StartsWith("/api/"))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Password required." });
        return;
    }
    context.Response.Redirect("/login");
});

// --- static UI -------------------------------------------------------------
app.MapGet("/", () => Results.Content(UiPages.Index, "text/html"));
app.MapGet("/login", () => Results.Content(UiPages.Login(auth.Enabled), "text/html"));

app.MapPost("/api/login", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var password = form["password"].ToString();
    if (auth.Verify(password))
    {
        auth.SignIn(ctx.Response);
        return Results.Ok(new { ok = true });
    }
    return Results.Json(new { error = "Wrong password." }, statusCode: StatusCodes.Status401Unauthorized);
});

// --- jobs API --------------------------------------------------------------
app.MapPost("/api/jobs", async (HttpContext ctx, JobManager jobs) =>
{
    if (jobs.ActiveJobCount >= options.MaxActiveJobs)
        return Results.Json(new { error = "The server is busy. Please try again in a few minutes." }, statusCode: StatusCodes.Status503ServiceUnavailable);

    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest(new { error = "Upload the game as a file (multipart/form-data)." });

    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.GetFile("game");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "No file was uploaded." });
    if (file.Length > options.MaxUploadBytes)
        return Results.BadRequest(new { error = $"That file is larger than the {options.MaxUploadBytes / (1024 * 1024)} MB limit." });

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".zip" or ".dll"))
        return Results.BadRequest(new { error = "Please upload a .zip of the game folder (or a single Assembly-CSharp.dll)." });

    await using var stream = file.OpenReadStream();
    var job = jobs.Create(file.FileName, stream);
    return Results.Ok(new { id = job.Id });
});

app.MapGet("/api/jobs/{id}", (string id, JobManager jobs) =>
{
    var job = jobs.Get(id);
    if (job is null) return Results.NotFound(new { error = "Unknown job (it may have expired)." });

    JobLogEntry[] log;
    lock (job.Lock) log = job.Log.ToArray();

    return Results.Ok(new
    {
        id = job.Id,
        state = job.State.ToString().ToLowerInvariant(),
        progress = job.Progress,
        error = job.Error,
        input = job.InputName,
        game = job.GameName,
        backend = job.Backend,
        unityVersion = job.UnityVersion,
        secretCount = job.SecretCount,
        highSecretCount = job.HighSecretCount,
        downloadSize = job.DownloadSize,
        canDownload = job.State == JobState.Done && job.DownloadPath != null,
        log = log.Select(l => new { l.Message, l.Level }),
    });
});

app.MapGet("/api/jobs/{id}/download", (string id, JobManager jobs) =>
{
    var job = jobs.Get(id);
    if (job?.DownloadPath is null || !File.Exists(job.DownloadPath))
        return Results.NotFound(new { error = "Result not available (the job may have expired)." });
    var name = Path.GetFileName(job.DownloadPath);
    return Results.File(job.DownloadPath, "application/zip", name);
});

app.MapGet("/api/config", () => Results.Ok(new
{
    maxUploadMb = options.MaxUploadBytes / (1024 * 1024),
    retentionHours = options.JobRetention.TotalHours,
    requiresPassword = auth.Enabled,
}));

app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();

/// <summary>Minimal cookie-based password gate. A cookie carries an HMAC of a fixed token so no server state is needed.</summary>
sealed class PasswordGate
{
    private const string CookieName = "ud_auth";
    private readonly string? _password;
    private readonly byte[] _key;
    private readonly string _expectedCookie;

    public PasswordGate(string? password)
    {
        _password = password;
        // Derive a per-process key from the password so cookies can't be forged without it.
        _key = SHA256.HashData(Encoding.UTF8.GetBytes((password ?? "") + Environment.MachineName));
        _expectedCookie = Convert.ToHexString(new HMACSHA256(_key).ComputeHash(Encoding.UTF8.GetBytes("authenticated")));
    }

    public bool Enabled => !string.IsNullOrEmpty(_password);

    public bool Verify(string? candidate) =>
        Enabled && candidate != null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(_password!));

    public void SignIn(HttpResponse response) =>
        response.Cookies.Append(CookieName, _expectedCookie, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = response.HttpContext.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(7),
        });

    public bool IsAuthenticated(HttpRequest request) =>
        request.Cookies.TryGetValue(CookieName, out var value) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(_expectedCookie));
}
