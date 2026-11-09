namespace ApsMonitor.Models;

public sealed record SynopticPoint(double X, double Y);
public sealed record SynopticDrop(string Type, double X, double Y);
public sealed record SynopticEdit(string Id, double X, double Y, double Width, double Height);
