using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BARS_Client_V2.Domain;

/// <summary>
/// Raw data emitted directly from simulator before nearest-airport resolution.
/// </summary>
public sealed record RawFlightSample(double Latitude, double Longitude, bool OnGround);

/// <summary>
/// Abstraction for any flight simulator connector.
/// </summary>
public interface ISimulatorConnector
{
    string SimulatorId { get; }
    string DisplayName { get; }
    bool IsConnected { get; }
    Task<bool> ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    /// <summary>
    /// Stream raw flight samples (lat, lon, onGround). Yields at sensible intervals or when state changes.
    /// </summary>
    IAsyncEnumerable<RawFlightSample> StreamRawAsync(CancellationToken ct = default);
}
