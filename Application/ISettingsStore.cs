using System.Collections.Generic;
using System.Threading.Tasks;

namespace BARS_Client_V2.Application;

public interface ISettingsStore
{
    Task<ClientSettings> LoadAsync();
    Task SaveAsync(ClientSettings settings);
}

public sealed record ClientSettings(string? ApiToken, IDictionary<string, string>? AirportPackages = null, bool AutoMinimizeOnStart = false)
{
    public static ClientSettings Empty => new(null, new Dictionary<string, string>(), false);
}
