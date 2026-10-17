namespace WweAllStars.Launcher.Core;

/// <summary>A long job the home screen runs beside Play.</summary>
public enum HomeJob
{
    Repair,
    PrepareShaders,
    /// <summary>The game's prepare-pipelines mode: before Play when the rebuild rule asks, and after Repair.</summary>
    PreparePipelines,
    /// <summary>Checking and unpacking DLC packs into the content folder the game reads.</summary>
    AddDlc,
    /// <summary>Inspecting and installing mods into the mods folder (ticket 07).</summary>
    AddMod,
}

/// <summary>
/// The home screen's running jobs and the one rule that follows from them: Play is off while
/// the game runs or any job works on its files. Each job runs at most once at a time; different
/// jobs may run together. Used from the UI thread only.
/// </summary>
public sealed class HomeJobs
{
    private readonly Dictionary<HomeJob, CancellationTokenSource> _running = [];

    public bool IsRunning(HomeJob job) => _running.ContainsKey(job);

    public bool CanPlay(bool gameRunning) => !gameRunning && _running.Count == 0;

    /// <summary>Starts <paramref name="job"/> and hands out its cancel token; false when it already runs.</summary>
    public bool TryStart(HomeJob job, out CancellationToken cancel)
    {
        cancel = default;
        if (_running.ContainsKey(job)) return false;
        var source = new CancellationTokenSource();
        _running[job] = source;
        cancel = source.Token;
        return true;
    }

    /// <summary>Asks <paramref name="job"/> to stop; it runs until it calls <see cref="End"/>.</summary>
    public void Cancel(HomeJob job)
    {
        if (_running.TryGetValue(job, out var source)) source.Cancel();
    }

    public void CancelAll()
    {
        foreach (var source in _running.Values) source.Cancel();
    }

    /// <summary>The job has stopped, finished or not.</summary>
    public void End(HomeJob job)
    {
        if (_running.Remove(job, out var source)) source.Dispose();
    }
}
