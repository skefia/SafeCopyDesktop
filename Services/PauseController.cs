namespace SafeCopy.Desktop.Services;

/// <summary>
/// Cooperative pause gate for managed background scans. Pausing never mutates files;
/// workers simply block at safe checkpoints until Resume is called.
/// </summary>
public sealed class PauseController : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(initialState: true);
    private volatile bool _paused;

    public bool IsPaused => _paused;

    public void Pause()
    {
        _paused = true;
        _gate.Reset();
    }

    public void Resume()
    {
        _paused = false;
        _gate.Set();
    }

    public void Wait(CancellationToken cancellationToken)
    {
        _gate.Wait(cancellationToken);
    }

    public void Dispose()
    {
        _gate.Set();
        _gate.Dispose();
    }
}
