using System;
using System.Collections.Generic;
using BARS_Client_V2.Domain;
using Microsoft.Extensions.Logging;

namespace BARS_Client_V2.Infrastructure.Networking;

/// <summary>
/// Subscribes once to AirportStreamMessageProcessor and forwards to all registered IPointStateListener instances.
/// </summary>
internal sealed class PointStateDispatcher
{
    private readonly IEnumerable<IPointStateListener> _listeners;
    private readonly ILogger<PointStateDispatcher> _logger;

    public PointStateDispatcher(AirportStreamMessageProcessor processor, IEnumerable<IPointStateListener> listeners, ILogger<PointStateDispatcher> logger)
    {
        _listeners = listeners;
        _logger = logger;
        processor.PointStateChanged += OnPointStateChanged;
        _logger.LogInformation("PointStateDispatcher initialized with {count} listeners", _listeners.Count());
    }

    private void OnPointStateChanged(PointState ps)
    {
        foreach (var l in _listeners)
        {
            try { l.OnPointStateChanged(ps); } catch (Exception ex) { _logger.LogDebug(ex, "Listener threw"); }
        }
    }
}
