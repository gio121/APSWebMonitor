using ApsMonitor.Models;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio compartido (Scoped) que mantiene el estado de la sesión cargada.
/// </summary>
public class SessionStateService : IDisposable
{
    private System.Threading.Timer? _playTimer;

    public List<SessionFrame>? Frames { get; private set; }
    public List<Signal> Signals { get; private set; } = new();
    public string FileName { get; private set; } = "";
    public int CurrentFrame { get; set; } = 0;
    public bool IsPlaying { get; private set; } = false;
    public bool IsLoaded => Frames != null && Frames.Count > 0;
    public TimeSpan TotalDuration => IsLoaded ? Frames!.Last().Timestamp : TimeSpan.Zero;

    public double VelocidadActual { get; set; } = 1;
    public readonly double[] Velocidades = { 0.1, 0.25, 0.5, 1, 2, 5, 10 };

    public event Action? OnStateChanged;

    public void LoadSession(List<SessionFrame> frames, List<Signal> signals, string fileName)
    {
        Stop();
        Frames = frames;
        Signals = signals;
        FileName = fileName;
        CurrentFrame = 0;
        NotifyStateChanged();
    }

    public void Play()
    {
        if (!IsLoaded) return;
        if (CurrentFrame >= Frames!.Count - 1) CurrentFrame = 0;

        IsPlaying = true;
        int tickMs = 50;
        int framesPerTick = Math.Max(1, (int)(VelocidadActual * 0.5));

        _playTimer?.Dispose();
        _playTimer = new System.Threading.Timer(_ =>
        {
            if (!IsPlaying) return;

            CurrentFrame += framesPerTick;
            if (CurrentFrame >= Frames!.Count)
            {
                CurrentFrame = Frames.Count - 1;
                IsPlaying = false;
                _playTimer?.Dispose();
            }
            NotifyStateChanged();
        }, null, 0, tickMs);
    }

    public void Pause()
    {
        IsPlaying = false;
        _playTimer?.Dispose();
        NotifyStateChanged();
    }

    public void Stop()
    {
        IsPlaying = false;
        _playTimer?.Dispose();
        CurrentFrame = 0;
        NotifyStateChanged();
    }

    public void NextFrame()
    {
        if (!IsLoaded || CurrentFrame >= Frames!.Count - 1) return;
        CurrentFrame++;
        NotifyStateChanged();
    }

    public void PreviousFrame()
    {
        if (!IsLoaded || CurrentFrame <= 0) return;
        CurrentFrame--;
        NotifyStateChanged();
    }

    public void GoToStart()
    {
        CurrentFrame = 0;
        NotifyStateChanged();
    }

    public void GoToEnd()
    {
        if (!IsLoaded) return;
        CurrentFrame = Frames!.Count - 1;
        NotifyStateChanged();
    }

    public void SeekTo(int frame)
    {
        if (!IsLoaded) return;
        CurrentFrame = Math.Clamp(frame, 0, Frames!.Count - 1);
        NotifyStateChanged();
    }

    public Dictionary<int, double> GetCurrentValues()
    {
        if (!IsLoaded || CurrentFrame >= Frames!.Count)
            return new Dictionary<int, double>();
        return Frames[CurrentFrame].Values;
    }

    private void NotifyStateChanged()
    {
        OnStateChanged?.Invoke();
    }

    public void Dispose()
    {
        _playTimer?.Dispose();
    }
}
