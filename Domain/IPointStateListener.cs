using System.Threading.Tasks;

namespace BARS_Client_V2.Domain;

/// <summary>
/// Consumer of point state updates (e.g. a simulator-specific controller).
/// </summary>
public interface IPointStateListener
{
    /// <summary>
    /// Called for every point state update (initial + deltas).
    /// Should be non-blocking; heavy work should be queued internally.
    /// </summary>
    void OnPointStateChanged(PointState state);
}
