using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BARS_Client_V2.Application;

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
    }

    public JsonSettingsStore()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(root, "BARS", "Client");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.json");
    }

    public async Task<ClientSettings> LoadAsync()
    {
        if (!File.Exists(_path)) return ClientSettings.Empty;
        try
        {
            var json = await File.ReadAllTextAsync(_path);
            var p = JsonSerializer.Deserialize<Persisted>(json, Options);
            if (p == null) return ClientSettings.Empty;

            string? token = null;

            if (!string.IsNullOrWhiteSpace(p.ApiToken))
            {
                try
                {
                    var protectedBytes = Convert.FromBase64String(p.ApiToken);
                    var unprotected = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                    token = Encoding.UTF8.GetString(unprotected);
                }
                catch
                {
                    token = p.ApiToken;
                }
            }
            else
            {
                token = p.ApiToken;
            }

            return new ClientSettings(token, p.AirportPackages ?? new());
        }
        catch
        {
            return ClientSettings.Empty;
        }
    }

    public async Task SaveAsync(ClientSettings settings)
    {
        var p = new Persisted
        {
            AirportPackages = settings.AirportPackages != null ? new Dictionary<string, string>(settings.AirportPackages) : new()
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            try
            {
                var plaintextBytes = Encoding.UTF8.GetBytes(settings.ApiToken);
                var protectedBytes = ProtectedData.Protect(plaintextBytes, Entropy, DataProtectionScope.CurrentUser);
                p.ApiToken = Convert.ToBase64String(protectedBytes);
            }
            catch
            {
                p.ApiToken = null;
            }
        }
        var json = JsonSerializer.Serialize(p, Options);
        await File.WriteAllTextAsync(_path, json);
    }
}
