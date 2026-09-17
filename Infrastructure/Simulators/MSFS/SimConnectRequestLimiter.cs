using System;
using System.Threading;
using System.Threading.Tasks;

namespace BARS_Client_V2.Infrastructure.Simulators.Msfs;

/// <summary>
/// Centralized SimConnect throughput guard to keep every interaction within the 1000 requests/second ceiling.
/// </summary>
internal static class SimConnectRequestLimiter
{
    internal const int MaxRequestsPerSecond = 1000;

    private static readonly object _lock = new();
    private static DateTime _windowStartUtc = DateTime.UtcNow;
    private static int _requestsInWindow;

    public static Task WaitAsync(int requestCost, CancellationToken cancellationToken = default)
    {
        if (requestCost <= 0 || requestCost > MaxRequestsPerSecond)
        {
            requestCost = Math.Clamp(requestCost, 1, MaxRequestsPerSecond);
        }

        return WaitInternalAsync(requestCost, cancellationToken);
    }

    private static async Task WaitInternalAsync(int requestCost, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _windowStartUtc;
                if (elapsed >= TimeSpan.FromSeconds(1))
                {
                    _windowStartUtc = now;
                    _requestsInWindow = 0;
                    elapsed = TimeSpan.Zero;
                }

                if (_requestsInWindow + requestCost <= MaxRequestsPerSecond)
                {
                    _requestsInWindow += requestCost;
                    return;
                }

                delay = TimeSpan.FromSeconds(1) - elapsed;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
    }
}
