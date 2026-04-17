using ApsMonitor.Models;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio compartido (Scoped) que mantiene el estado de la sesión cargada.
/// </summary>
public class SessionStateService : IDisposable
{
    private System.Threading.Timer? _playTimer;

    public SessionDataset? Dataset { get; private set; }
    public List<Signal> Signals { get; private set; } = new();
    public string FileName { get; private set; } = "";
    public int CurrentFrame { get; set; } = 0;
    public bool IsPlaying { get; private set; } = false;
    public bool IsLoaded => Dataset != null && Dataset.FrameCount > 0;
    public TimeSpan TotalDuration => IsLoaded ? Dataset!.Timestamps[Dataset.FrameCount - 1] : TimeSpan.Zero;

    public double VelocidadActual { get; set; } = 1;
    public readonly double[] Velocidades = { 0.1, 0.25, 0.5, 1, 2, 5, 10 };

    public event Action? OnStateChanged;

    public void LoadSession(SessionDataset dataset, List<Signal> signals, string fileName)
    {
        Stop();
        Dataset = dataset;
        Signals = signals;
        FileName = fileName;
        CurrentFrame = 0;
        NotifyStateChanged();
    }

    private DateTime _playRealStartTime;
    private TimeSpan _playStartOffset;

    public void Play()
    {
        if (!IsLoaded) return;
        if (CurrentFrame >= Dataset!.FrameCount - 1) CurrentFrame = 0;

        IsPlaying = true;
        _playRealStartTime = DateTime.UtcNow;
        _playStartOffset = Dataset!.Timestamps[CurrentFrame];

        // Tasa de actualización de UI
        int tickMs = 50;

        _playTimer?.Dispose();
        _playTimer = new System.Threading.Timer(_ =>
        {
            if (!IsPlaying) return;

            var elapsedRealTime = DateTime.UtcNow - _playRealStartTime;
            var targetTime = _playStartOffset + TimeSpan.FromMilliseconds(elapsedRealTime.TotalMilliseconds * VelocidadActual);

            int newFrame = CurrentFrame;
            while (newFrame < Dataset.FrameCount - 1 && Dataset.Timestamps[newFrame + 1] <= targetTime)
            {
                newFrame++;
            }

            if (newFrame >= Dataset.FrameCount - 1)
            {
                CurrentFrame = Dataset.FrameCount - 1;
                IsPlaying = false;
                _playTimer?.Dispose();
            }
            else
            {
                CurrentFrame = newFrame;
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

    public void CloseSession()
    {
        Stop();
        Dataset = null;
        Signals = new List<Signal>();
        FileName = string.Empty;
        CurrentFrame = 0;
        NotifyStateChanged();
    }

    public void NextFrame()
    {
        if (!IsLoaded || CurrentFrame >= Dataset!.FrameCount - 1) return;
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
        CurrentFrame = Dataset!.FrameCount - 1;
        NotifyStateChanged();
    }

    public void SeekTo(int frame)
    {
        if (!IsLoaded) return;
        CurrentFrame = Math.Clamp(frame, 0, Dataset!.FrameCount - 1);
        if (IsPlaying)
        {
            _playRealStartTime = DateTime.UtcNow;
            _playStartOffset = Dataset!.Timestamps[CurrentFrame];
        }
        NotifyStateChanged();
    }

    public Dictionary<int, double> GetCurrentValues()
    {
        if (!IsLoaded || CurrentFrame >= Dataset!.FrameCount)
            return new Dictionary<int, double>();

        // Reconstrucción dinámica (solo para el frame actual)
        // Esto mantiene la compatibilidad con la UI sin saturar la RAM
        var dict = new Dictionary<int, double>();
        for (int s = 0; s < Dataset.SignalCount; s++)
        {
            dict[Dataset.SignalIds[s]] = Dataset.Values[CurrentFrame, s];
        }
        return dict;
    }

    public Dictionary<int, double> GetRawValues()
    {
        if (!IsLoaded || CurrentFrame >= Dataset!.FrameCount)
            return new Dictionary<int, double>();

        var dict = new Dictionary<int, double>();
        for (int s = 0; s < Dataset.SignalCount; s++)
        {
            dict[Dataset.SignalIds[s]] = Dataset.RawValues[CurrentFrame, s];
        }
        return dict;
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
