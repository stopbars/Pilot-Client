using System.Threading.Tasks;
using System.Collections.Generic;

namespace BARS_Client_V2.Application;

public interface ISettingsStore
{
    Task<ClientSettings> LoadAsync();
    Task SaveAsync(ClientSettings settings);
}

public sealed record ClientSettings(string? ApiToken, IDictionary<string, string>? AirportPackages = null)
{
    public static ClientSettings Empty => new(null, new Dictionary<string, string>());
}
