using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Application;
using BARS_Client_V2.Infrastructure.Diagnostics;

namespace BARS_Client_V2.Infrastructure.Settings;

internal sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BARS.Client.V2|ApiToken|v1");

    private sealed class Persisted
    {
        public string? ApiToken { get; set; }
        public Dictionary<string, string>? AirportPackages { get; set; }
        public Dictionary<string, bool>? SceneryRemovalToggles { get; set; }
        public bool AutoMinimizeOnStart { get; set; }
        public bool DiscordPresenceEnabled { get; set; } = true;
        [JsonConverter(typeof(DrawDistanceConverter))]
        public int LightDrawDistanceMeters { get; set; } = LightDrawDistanceSettings.DefaultMeters;
        public string? Msfs2020RemovalsEtag { get; set; }
        public string? Msfs2024RemovalsEtag { get; set; }
    }

    public JsonSettingsStore()
        : this(GetDefaultPath())
    {
    }

    private sealed class DrawDistanceConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var meters))
                return LightDrawDistanceSettings.Normalize(meters);

            reader.Skip();
            return LightDrawDistanceSettings.DefaultMeters;
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(LightDrawDistanceSettings.Normalize(value));
    }

    internal JsonSettingsStore(string path)
    {
        _path = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("Settings path has no parent directory.", nameof(path));
        Directory.CreateDirectory(folder);
    }

    private static string GetDefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "BARS", "Client", "settings.json");
    }

    public async Task<ClientSettings> LoadAsync()
    {
        StartupTrace.Write("JsonSettingsStore.LoadAsync enter");
        await SettingsFileAccess.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var settings = await LoadCoreAsync().ConfigureAwait(false);
            StartupTrace.Write("JsonSettingsStore.LoadAsync success");
            return settings;
        }
        catch (Exception ex)
        {
            StartupTrace.Write($"JsonSettingsStore.LoadAsync exception: {ex.Message}");
            PreserveUnreadableSettings();
            return ClientSettings.Empty;
        }
        finally
        {
            StartupTrace.Write("JsonSettingsStore.LoadAsync exit");
            SettingsFileAccess.Gate.Release();
        }
    }

    public async Task SaveAsync(ClientSettings settings)
    {
        StartupTrace.Write("JsonSettingsStore.SaveAsync enter");
        await SettingsFileAccess.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings).ConfigureAwait(false);
            StartupTrace.Write("JsonSettingsStore.SaveAsync success");
        }
        finally
        {
            StartupTrace.Write("JsonSettingsStore.SaveAsync exit");
            SettingsFileAccess.Gate.Release();
        }
    }

    public async Task<ClientSettings> UpdateAsync(Func<ClientSettings, ClientSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        await SettingsFileAccess.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync().ConfigureAwait(false);
            var updated = update(current)
                ?? throw new InvalidOperationException("Settings update returned no settings.");
            await SaveCoreAsync(updated).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            SettingsFileAccess.Gate.Release();
        }
    }

    private async Task<ClientSettings> LoadCoreAsync()
    {
        if (!File.Exists(_path)) return ClientSettings.Empty;
        StartupTrace.Write("JsonSettingsStore.LoadAsync reading file");
        var json = await File.ReadAllTextAsync(_path).ConfigureAwait(false);
        var persisted = JsonSerializer.Deserialize<Persisted>(json, Options)
            ?? throw new InvalidDataException("The settings file was empty.");

        string? token = persisted.ApiToken;
        if (!string.IsNullOrWhiteSpace(persisted.ApiToken))
        {
            try
            {
                var protectedBytes = Convert.FromBase64String(persisted.ApiToken);
                var unprotected = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                token = Encoding.UTF8.GetString(unprotected);
            }
            catch (Exception) when (persisted.ApiToken.StartsWith("BARS_", StringComparison.Ordinal))
            {
                // Older settings stored the token as plaintext.
                token = persisted.ApiToken;
            }
            catch (Exception ex)
            {
                throw new CryptographicException("The saved API token could not be decrypted.", ex);
            }
        }

        return new ClientSettings(
            token,
            persisted.AirportPackages ?? new Dictionary<string, string>(),
            persisted.SceneryRemovalToggles ?? new Dictionary<string, bool>(),
            persisted.AutoMinimizeOnStart,
            persisted.Msfs2020RemovalsEtag,
            persisted.Msfs2024RemovalsEtag,
            persisted.DiscordPresenceEnabled,
            LightDrawDistanceSettings.Normalize(persisted.LightDrawDistanceMeters));
    }

    private async Task SaveCoreAsync(ClientSettings settings)
    {
        var persisted = new Persisted
        {
            AirportPackages = settings.AirportPackages != null ? new Dictionary<string, string>(settings.AirportPackages) : new(),
            SceneryRemovalToggles = settings.SceneryRemovalToggles != null ? new Dictionary<string, bool>(settings.SceneryRemovalToggles) : new(),
            AutoMinimizeOnStart = settings.AutoMinimizeOnStart,
            DiscordPresenceEnabled = settings.DiscordPresenceEnabled,
            LightDrawDistanceMeters = LightDrawDistanceSettings.Normalize(settings.LightDrawDistanceMeters),
            Msfs2020RemovalsEtag = settings.Msfs2020RemovalsEtag,
            Msfs2024RemovalsEtag = settings.Msfs2024RemovalsEtag
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(settings.ApiToken);
            var protectedBytes = ProtectedData.Protect(plaintextBytes, Entropy, DataProtectionScope.CurrentUser);
            persisted.ApiToken = Convert.ToBase64String(protectedBytes);
        }

        var json = JsonSerializer.Serialize(persisted, Options);
        await SettingsFileAccess.WriteAllTextAtomicAsync(_path, json).ConfigureAwait(false);
    }

    private void PreserveUnreadableSettings()
    {
        if (!File.Exists(_path)) return;
        var backupPath = _path + ".unreadable";
        if (File.Exists(backupPath)) return;
        try { File.Copy(_path, backupPath, overwrite: false); }
        catch { }
    }
}
