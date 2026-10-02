namespace UnityDecompiler.Server;

/// <summary>Server configuration, read from environment variables so it's easy to set on a VPS or in Docker.</summary>
public sealed class ServerOptions
{
    /// <summary>Where uploads and results are stored. Env: DATA_DIR.</summary>
    public string DataDirectory { get; init; } = "/data";

    /// <summary>Largest upload accepted, in bytes. Env: MAX_UPLOAD_MB (default 2048).</summary>
    public long MaxUploadBytes { get; init; } = 2048L * 1024 * 1024;

    /// <summary>Optional password gate. Env: ACCESS_PASSWORD. Empty = open to anyone who can reach the server.</summary>
    public string? AccessPassword { get; init; }

    /// <summary>How long finished jobs (and their files) are kept. Env: RETENTION_HOURS (default 6).</summary>
    public TimeSpan JobRetention { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Copy StreamingAssets into results. Env: COPY_STREAMING_ASSETS (default false on a server to save space).</summary>
    public bool CopyStreamingAssets { get; init; }

    /// <summary>Reject a new upload while this many jobs are already queued/running. Env: MAX_ACTIVE_JOBS (default 20).</summary>
    public int MaxActiveJobs { get; init; } = 20;

    public static ServerOptions FromEnvironment()
    {
        string? Env(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : null;

        var dataDir = Env("DATA_DIR") ?? Path.Combine(Path.GetTempPath(), "unity-decompiler-data");
        return new ServerOptions
        {
            DataDirectory = dataDir,
            MaxUploadBytes = (long)(ParseDouble(Env("MAX_UPLOAD_MB"), 2048) * 1024 * 1024),
            AccessPassword = Env("ACCESS_PASSWORD"),
            JobRetention = TimeSpan.FromHours(ParseDouble(Env("RETENTION_HOURS"), 6)),
            CopyStreamingAssets = ParseBool(Env("COPY_STREAMING_ASSETS"), false),
            MaxActiveJobs = (int)ParseDouble(Env("MAX_ACTIVE_JOBS"), 20),
        };
    }

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : fallback;

    private static bool ParseBool(string? value, bool fallback) =>
        bool.TryParse(value, out var b) ? b : value is "1" or "yes" or "on" ? true : fallback;
}
