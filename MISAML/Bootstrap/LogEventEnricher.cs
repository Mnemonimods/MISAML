using System;
using System.Diagnostics;
using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace MISAML.Bootstrap;

internal sealed class LogEnricher : ILogEventEnricher
{
    private readonly long _start = Stopwatch.GetTimestamp();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        var seconds = Stopwatch.GetElapsedTime(_start).TotalSeconds;

        logEvent.AddPropertyIfAbsent(
            factory.CreateProperty("Elapsed", seconds.ToString("F6", CultureInfo.InvariantCulture)));
    }
}