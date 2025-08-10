using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BARS_Client_V2.Domain;

public sealed record RawFlightSample(double Latitude, double Longitude, bool OnGround);

public interface ISimulatorConnector
{
    string SimulatorId { get; }
    string DisplayName { get; }
    bool IsConnected { get; }
    Task<bool> ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    IAsyncEnumerable<RawFlightSample> StreamRawAsync(CancellationToken ct = default);
}
