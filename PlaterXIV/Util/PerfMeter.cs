using System.Diagnostics;

namespace PlaterXIV.Util;

/// <summary>
/// Collects per-frame timings (scan + plate update) and writes one [Perf] line to the Dalamud log
/// every few seconds while plates are on screen. Only the log line itself allocates.
/// </summary>
public sealed class PerfMeter
{
    private readonly Configuration config;
    private long windowStart = Stopwatch.GetTimestamp();
    private double scanSum, scanMax, renderSum, renderMax;
    private int scanSamples, renderSamples, platesMax, shownMax, builtTotal;

    public PerfMeter(Configuration config) => this.config = config;

    public void RecordScan(double microseconds, int plates)
    {
        scanSum += microseconds;
        scanSamples++;
        if (microseconds > scanMax) scanMax = microseconds;
        if (plates > platesMax) platesMax = plates;
        MaybeLog();
    }

    public void RecordRender(double microseconds, int shown, int built)
    {
        renderSum += microseconds;
        renderSamples++;
        if (microseconds > renderMax) renderMax = microseconds;
        if (shown > shownMax) shownMax = shown;
        builtTotal = built;
    }

    private void MaybeLog()
    {
        if (Stopwatch.GetElapsedTime(windowStart).TotalSeconds < config.PerfIntervalSeconds) return;

        if (config.PerfLog && platesMax > 0 && scanSamples > 0)
        {
            var renderAvg = renderSamples > 0 ? renderSum / renderSamples : 0;
            Services.Log.Information(
                $"[Perf] scan avg {scanSum / scanSamples:F1} µs max {scanMax:F1} · plates avg {renderAvg:F1} µs max {renderMax:F1} · game plates ≤{platesMax} · ours shown ≤{shownMax} · pool {builtTotal}/50 · frames {scanSamples} ({Plugin.BuildTag})");
        }

        windowStart = Stopwatch.GetTimestamp();
        scanSum = scanMax = renderSum = renderMax = 0;
        scanSamples = renderSamples = platesMax = shownMax = 0;
    }
}
