using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BARS_Client_V2.Services;

public interface INearestAirportService
{
    string? GetCachedNearest(double lat, double lon);
    Task<string?> ResolveAndCacheAsync(double lat, double lon, CancellationToken ct = default);
}

internal sealed class NearestAirportService : INearestAirportService
{
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private double _lastLat;
    private double _lastLon;
    private string? _lastIcao;
    private DateTime _lastFetchUtc = DateTime.MinValue;
    // Throttling/backoff state
    private DateTime _nextAllowedAttemptUtc = DateTime.MinValue;
    private int _consecutiveFailures = 0;
    private Task<string?>? _inFlight;
    private double _lastAttemptLat;
    private double _lastAttemptLon;

    private const double MinDistanceNmForRefresh = 2.0;
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private const double MinDistanceNmForRetryAttempt = 0.1; // ~185m movement to re-attempt sooner than time-based backoff

    public NearestAirportService(HttpClient httpClient)
    {
        _http = httpClient;
    }

    public string? GetCachedNearest(double lat, double lon)
    {
        lock (_lock)
        {
            if (_lastIcao == null) return null;
            if ((DateTime.UtcNow - _lastFetchUtc) > MaxAge) return null;
            if (GreatCircleDistanceNm(lat, lon, _lastLat, _lastLon) > MinDistanceNmForRefresh) return null;
            return _lastIcao;
        }
    }

    public Task<string?> ResolveAndCacheAsync(double lat, double lon, CancellationToken ct = default)
    {
        Task<string?>? toAwait = null;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var tooSoonByTime = now < _nextAllowedAttemptUtc;
            var tooSoonByDistance = GreatCircleDistanceNm(lat, lon, _lastAttemptLat, _lastAttemptLon) < MinDistanceNmForRetryAttempt;
            if ((tooSoonByTime && tooSoonByDistance))
            {
                return Task.FromResult<string?>(null);
            }

            if (_inFlight != null && !_inFlight.IsCompleted)
            {
                return _inFlight;
            }

            _lastAttemptLat = lat;
            _lastAttemptLon = lon;

            // Start a single in-flight request for de-duplication
            _inFlight = DoResolveAsync(lat, lon, ct);
            toAwait = _inFlight;
        }

        return toAwait!;
    }

    private async Task<string?> DoResolveAsync(double lat, double lon, CancellationToken ct)
    {
        try
        {
            var url = $"https://v2.stopbars.com/airports/nearest?lat={lat:F6}&lon={lon:F6}";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ApplyFailureBackoff();
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            string? icao = null;
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("icao", out var p)) icao = p.GetString();
                else if (doc.RootElement.TryGetProperty("ICAO", out var p2)) icao = p2.GetString();
            }
            if (!string.IsNullOrWhiteSpace(icao))
            {
                lock (_lock)
                {
                    _lastIcao = icao;
                    _lastLat = lat;
                    _lastLon = lon;
                    _lastFetchUtc = DateTime.UtcNow;
                    _consecutiveFailures = 0;
                    _nextAllowedAttemptUtc = DateTime.UtcNow; // reset backoff; cache age/distance gates future fetches
                }
            }
            else
            {
                ApplyFailureBackoff();
            }
            return icao;
        }
        catch
        {
            ApplyFailureBackoff();
            return null;
        }
        finally
        {
            lock (_lock)
            {
                _inFlight = null;
            }
        }
    }

    private void ApplyFailureBackoff()
    {
        lock (_lock)
        {
            _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 10);
            var backoff = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, BaseBackoff.TotalMilliseconds * Math.Pow(2, _consecutiveFailures - 1)));
            _nextAllowedAttemptUtc = DateTime.UtcNow + backoff;
        }
    }

    private static double GreatCircleDistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R_km = 6371.0;
        double dLat = Deg2Rad(lat2 - lat1);
        double dLon = Deg2Rad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Deg2Rad(lat1)) * Math.Cos(Deg2Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        double km = R_km * c;
        return km * 0.5399568; // km->nm
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
}
