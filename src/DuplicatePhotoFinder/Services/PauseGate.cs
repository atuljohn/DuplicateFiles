namespace DuplicatePhotoFinder.Services;

/// <summary>
/// Async pause/resume primitive. Workers await WaitAsync at the top of each unit of work;
/// while paused, the returned Task stays uncompleted and workers block until Resume is called.
/// Safe to call Pause/Resume from the UI thread while workers hold a reference.
/// </summary>
public class PauseGate
{
    private readonly object _lock = new();
    private TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isPaused;

    public PauseGate()
    {
        _tcs.TrySetResult(true);
    }

    public bool IsPaused
    {
        get { lock (_lock) return _isPaused; }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_isPaused) return;
            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _isPaused = true;
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? toComplete = null;
        lock (_lock)
        {
            if (!_isPaused) return;
            _isPaused = false;
            toComplete = _tcs;
        }
        toComplete?.TrySetResult(true);
    }

    public Task WaitAsync(CancellationToken ct = default)
    {
        Task<bool> task;
        lock (_lock) { task = _tcs.Task; }
        return task.IsCompleted ? Task.CompletedTask : task.WaitAsync(ct);
    }
}
