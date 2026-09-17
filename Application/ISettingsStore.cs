using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BARS_Client_V2.Application;

public interface ISettingsStore
{
    Task<ClientSettings> LoadAsync();
    Task SaveAsync(ClientSettings settings);
    Task<ClientSettings> UpdateAsync(Func<ClientSettings, ClientSettings> update);
}

public sealed record ClientSettings(
    string? ApiToken,
    IDictionary<string, string>? AirportPackages = null,
    IDictionary<string, bool>? SceneryRemovalToggles = null,
    bool AutoMinimizeOnStart = false,
    string? Msfs2020RemovalsEtag = null,
    string? Msfs2024RemovalsEtag = null,
    bool DiscordPresenceEnabled = true,
    int LightDrawDistanceMeters = LightDrawDistanceSettings.DefaultMeters)
{
    public static ClientSettings Empty => new(null, new Dictionary<string, string>(), new Dictionary<string, bool>(), false, null, null);
}
