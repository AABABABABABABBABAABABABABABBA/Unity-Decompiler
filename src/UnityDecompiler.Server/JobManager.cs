using System.Collections.Concurrent;
using System.IO.Compression;
using UnityDecompiler.Core;

namespace UnityDecompiler.Server;

public enum JobState { Queued, Running, Done, Failed }

public sealed class JobLogEntry
{
    public required string Message { get; init; }
    public required string Level { get; init; }
    public double? Fraction { get; init; }
}

public sealed class Job
{
    public required string Id { get; init; }
    public required string InputName { get; init; }
    public JobState State { get; set; } = JobState.Queued;
    public double Progress { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Finished { get; set; }

    public string? GameName { get; set; }
    public string? Backend { get; set; }
    public string? UnityVersion { get; set; }
    public int SecretCount { get; set; }
    public int HighSecretCount { get; set; }
    public string? DownloadPath { get; set; }
    public long DownloadSize { get; set; }

    public readonly List<JobLogEntry> Log = new();
    public readonly object Lock = new();

    // Filesystem paths (not serialized to clients).
    public required string WorkDir { get; init; }
    public required string UploadPath { get; init; }
}

/// <summary>
/// Runs decompile jobs on a background worker, one at a time, and keeps their state in memory.
/// Everything for a job lives under a per-job working directory that is deleted when the job expires.
/// </summary>
public sealed class JobManager : BackgroundService
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly System.Threading.Channels.Channel<Job> _queue =
        System.Threading.Channels.Channel.CreateUnbounded<Job>();
    private readonly ServerOptions _options;
    private readonly ILogger<JobManager> _logger;

    public JobManager(ServerOptions options, ILogger<JobManager> logger)
    {
        _options = options;
        _logger = logger;
        Directory.CreateDirectory(_options.DataDirectory);
    }

    public int ActiveJobCount => _jobs.Count(j => j.Value.State is JobState.Queued or JobState.Running);

    public Job Create(string uploadFileName, Stream uploadContent)
    {
        var id = Guid.NewGuid().ToString("N");
        var workDir = Path.Combine(_options.DataDirectory, id);
        Directory.CreateDirectory(workDir);

        var safeName = Path.GetFileName(uploadFileName);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "upload";
        var uploadPath = Path.Combine(workDir, safeName);
        using (var fs = File.Create(uploadPath))
            uploadContent.CopyTo(fs);

        var job = new Job { Id = id, InputName = safeName, WorkDir = workDir, UploadPath = uploadPath };
        _jobs[id] = job;
        _queue.Writer.TryWrite(job);
        return job;
    }

    public Job? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cleanup = CleanupLoop(stoppingToken);
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await Task.Run(() => RunJob(job, stoppingToken), stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { _logger.LogError(e, "Job {Id} crashed", job.Id); }
        }
        await cleanup;
    }

    private void RunJob(Job job, CancellationToken ct)
    {
        job.State = JobState.Running;
        var outputRoot = Path.Combine(job.WorkDir, "output");

        var progress = new Progress<DecompileProgress>(p =>
        {
            if (p.Fraction is double f) job.Progress = f;
            lock (job.Lock)
            {
                job.Log.Add(new JobLogEntry { Message = p.Message, Level = p.Level.ToString().ToLowerInvariant(), Fraction = p.Fraction });
                if (job.Log.Count > 1000) job.Log.RemoveAt(0);
            }
        });

        try
        {
            var options = new DecompileOptions
            {
                InputPath = job.UploadPath,
                OutputDirectory = outputRoot,
                IncludeFrameworkAssemblies = false,
                CopyStreamingAssets = _options.CopyStreamingAssets,
                ScanForSecrets = true,
            };
            var result = GameDecompiler.Run(options, progress, ct);

            job.GameName = result.Game.DisplayName;
            job.Backend = result.Game.Backend.ToString();
            job.UnityVersion = result.Game.UnityVersion;
            job.SecretCount = result.Secrets.Count;
            job.HighSecretCount = result.Secrets.Count(s => s.Severity == SecretSeverity.High);

            ct.ThrowIfCancellationRequested();
            var zipPath = Path.Combine(job.WorkDir, GameDecompiler.SafeFileName(result.Game.DisplayName) + "-decompiled.zip");
            ZipFile.CreateFromDirectory(result.OutputDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            job.DownloadPath = zipPath;
            job.DownloadSize = new FileInfo(zipPath).Length;

            // The raw extracted output and the upload are no longer needed once zipped.
            TryDelete(outputRoot);
            TryDeleteFile(job.UploadPath);

            job.State = JobState.Done;
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.Failed;
            job.Error = "Cancelled (the server is shutting down).";
        }
        catch (UnityGameNotFoundException e)
        {
            job.State = JobState.Failed;
            job.Error = e.Message;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Job {Id} failed", job.Id);
            job.State = JobState.Failed;
            job.Error = "Something went wrong while decompiling: " + e.Message;
        }
        finally
        {
            job.Finished = DateTimeOffset.UtcNow;
            job.Progress = job.State == JobState.Done ? 1 : job.Progress;
        }
    }

    /// <summary>Deletes jobs (and their files) once they pass the retention window.</summary>
    private async Task CleanupLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                var cutoff = DateTimeOffset.UtcNow - _options.JobRetention;
                foreach (var job in _jobs.Values)
                {
                    var reference = job.Finished ?? job.Created;
                    if (job.State is JobState.Done or JobState.Failed && reference < cutoff)
                    {
                        if (_jobs.TryRemove(job.Id, out _)) TryDelete(job.WorkDir);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (Exception) { }
    }

    private static void TryDeleteFile(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (Exception) { }
    }
}
