using System.Diagnostics;
using ApsMonitor.Models;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio compartido (Scoped) que mantiene el estado de la sesión cargada.
/// Motor de reproducción basado en Stopwatch para máxima estabilidad.
/// </summary>
public class SessionStateService : IDisposable
{
    private System.Threading.Timer? _playTimer;
    private Stopwatch? _playStopwatch;
    private TimeSpan _playStartSessionTime; // Timestamp de la sesión cuando se pulsó Play

    // Throttle: máximo ~30 actualizaciones de UI por segundo
    private const int UI_TICK_MS = 33;

    public SessionDataset? Dataset { get; private set; }
    public List<Signal> Signals { get; private set; } = new();
    public string FileName { get; private set; } = "";
    public int CurrentFrame { get; set; } = 0;
    public bool IsPlaying { get; private set; } = false;
    public bool IsLoaded => Dataset != null && Dataset.FrameCount > 0;
    public TimeSpan TotalDuration => IsLoaded ? Dataset!.Timestamps[Dataset.FrameCount - 1] : TimeSpan.Zero;

    public double VelocidadActual { get; set; } = 1;
    public readonly double[] Velocidades = { 0.1, 0.25, 0.5, 1, 2, 5, 10 };

    public bool IsDarkMode { get; set; } = false; // Default to Light Mode as per user's latest preference

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

    public void Play()
    {
        if (!IsLoaded) return;
        if (CurrentFrame >= Dataset!.FrameCount - 1) CurrentFrame = 0;

        IsPlaying = true;

        // Recordar el timestamp de sesión del frame actual y arrancar el cronómetro
        _playStartSessionTime = Dataset.Timestamps[CurrentFrame];
        _playStopwatch = Stopwatch.StartNew();

        // Un solo timer periódico a 30fps — mucho más estable que timers recursivos
        _playTimer?.Dispose();
        _playTimer = new System.Threading.Timer(OnPlayTick, null, 0, UI_TICK_MS);
    }

    private void OnPlayTick(object? state)
    {
        if (!IsPlaying || !IsLoaded || _playStopwatch == null) return;

        // ¿Cuánto tiempo real ha pasado desde que pulsamos Play?
        double elapsedMs = _playStopwatch.Elapsed.TotalMilliseconds * VelocidadActual;
        TimeSpan targetSessionTime = _playStartSessionTime + TimeSpan.FromMilliseconds(elapsedMs);

        // Avanzar frames hasta alcanzar el tiempo objetivo
        int newFrame = CurrentFrame;
        while (newFrame < Dataset!.FrameCount - 1 && Dataset.Timestamps[newFrame + 1] <= targetSessionTime)
        {
            newFrame++;
        }

        // Si no hubo cambio de frame, no refrescar la UI (ahorra recursos)
        if (newFrame == CurrentFrame) return;

        CurrentFrame = newFrame;

        // ¿Llegamos al final?
        if (CurrentFrame >= Dataset.FrameCount - 1)
        {
            CurrentFrame = Dataset.FrameCount - 1;
            IsPlaying = false;
            _playTimer?.Dispose();
            _playStopwatch?.Stop();
        }

        NotifyStateChanged();
    }

    public void Pause()
    {
        IsPlaying = false;
        _playTimer?.Dispose();
        _playStopwatch?.Stop();
        NotifyStateChanged();
    }

    public void Stop()
    {
        IsPlaying = false;
        _playTimer?.Dispose();
        _playStopwatch?.Stop();
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
            // Re-sincronizar el cronómetro desde la nueva posición
            _playStartSessionTime = Dataset.Timestamps[CurrentFrame];
            _playStopwatch = Stopwatch.StartNew();
        }
        NotifyStateChanged();
    }

    public Dictionary<int, double> GetCurrentValues()
    {
        if (!IsLoaded || CurrentFrame >= Dataset!.FrameCount)
            return new Dictionary<int, double>();

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

    public void ToggleDarkMode()
    {
        IsDarkMode = !IsDarkMode;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnStateChanged?.Invoke();
    }

    public void Dispose()
    {
        _playTimer?.Dispose();
        _playStopwatch?.Stop();
    }
}
