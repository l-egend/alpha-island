using System;
using AlphaIsland.PluginSdk;
using alpha_island.Services;

namespace alpha_island.Core;

internal sealed class ExternalNetworkState
{
    public string SourceId { get; private set; } = "";
    public string Title { get; private set; } = "";
    public PluginNetworkSample? Reading { get; private set; }
    public bool Active => Reading != null;
    public NetworkSpeedSample Current => Reading == null ? default : new(Reading.DownBytesPerSecond, Reading.UpBytesPerSecond);
    public long SessionDown { get; private set; }
    public long SessionUp { get; private set; }
    public double PeakDown { get; private set; }
    public double PeakUp { get; private set; }
    public bool Apply(string id, string title, PluginNetworkSample reading)
    {
        bool changed = SourceId != id;
        if (!changed && Reading != null && reading.Timestamp <= Reading.Timestamp) return false;
        if (changed) Reset();
        if (Reading != null && Reading.CounterEpoch == reading.CounterEpoch && reading.Timestamp > Reading.Timestamp)
        {
            if (reading.DownloadCounterBytes is long down && Reading.DownloadCounterBytes is long beforeDown && down >= beforeDown)
                SessionDown = AddSaturated(SessionDown, down - beforeDown);
            if (reading.UploadCounterBytes is long up && Reading.UploadCounterBytes is long beforeUp && up >= beforeUp)
                SessionUp = AddSaturated(SessionUp, up - beforeUp);
        }
        SourceId = id; Title = title; Reading = reading;
        PeakDown = Math.Max(PeakDown, reading.DownBytesPerSecond); PeakUp = Math.Max(PeakUp, reading.UpBytesPerSecond);
        return changed;
    }
    private static long AddSaturated(long total, long delta) => delta > long.MaxValue - total ? long.MaxValue : total + delta;
    public void Reset() { SourceId = Title = ""; Reading = null; SessionDown = SessionUp = 0; PeakDown = PeakUp = 0; }
}
