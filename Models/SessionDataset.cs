using System;
using System.Collections.Generic;

namespace ApsMonitor.Models;

/// <summary>
/// Proporciona un almacenamiento de alto rendimiento para los datos de sesión.
/// Utiliza arreglos planos en lugar de listas de objetos para minimizar la presión sobre el GC.
/// </summary>
public class SessionDataset
{
    public int FrameCount { get; }
    public int SignalCount { get; }
    
    // Matriz de valores: [FrameIndex, SignalIndex]
    public double[,] Values { get; }
    public double[,] RawValues { get; }
    
    // Tiempos para cada frame
    public TimeSpan[] Timestamps { get; }
    public DateTime[] AbsoluteTimestamps { get; }
    
    // Mapeo: SignalId -> Index en la dimensión [SignalCount]
    public Dictionary<int, int> SignalIdToIndex { get; } = new();
    public List<int> SignalIds { get; } = new();

    public SessionDataset(int frameCount, List<int> signalIds)
    {
        FrameCount = frameCount;
        SignalCount = signalIds.Count;
        SignalIds = signalIds;

        Values = new double[frameCount, SignalCount];
        RawValues = new double[frameCount, SignalCount];
        Timestamps = new TimeSpan[frameCount];
        AbsoluteTimestamps = new DateTime[frameCount];

        for (int i = 0; i < signalIds.Count; i++)
        {
            SignalIdToIndex[signalIds[i]] = i;
        }
    }
}
