using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;

namespace BARS_Client_V2.Services
{
    public class SceneryContribution
    {
        public string Id { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string UserDisplayName { get; set; } = string.Empty;
        public string AirportIcao { get; set; } = string.Empty;
        public string PackageName { get; set; } = string.Empty;
        public string SubmittedXml { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public DateTime SubmissionDate { get; set; }
        public string Status { get; set; } = string.Empty;
        public string RejectionReason { get; set; } = string.Empty;
        public DateTime? DecisionDate { get; set; }
    }

    public class ContributionsResponse
    {
        // Initialize to empty list to satisfy non-nullable warning
        public List<SceneryContribution> contributions { get; set; } = new();
    }

    public class SceneryService
    {
        private const string API_URL = "https://v2.stopbars.com/contributions?status=approved";
        private const string SETTINGS_FILENAME = "settings.json";
        private readonly HttpClient _httpClient;
        private Dictionary<string, string> _selectedPackages;
        private static SceneryService? _instance;

        // Fired when a user changes the selected scenery package for an airport.
        // Args: (icao, newPackageName)
        public event Action<string, string>? PackageChanged;

        public static SceneryService Instance
        {
            get
            {
                _instance ??= new SceneryService();
                return _instance;
            }
        }

        private SceneryService()
        {
            _httpClient = new HttpClient();
            _selectedPackages = LoadSelectedPackages();
        }

        public async Task<Dictionary<string, List<string>>> GetAvailablePackagesAsync()
        {
            try
            {
                var response = await _httpClient.GetStringAsync(API_URL);

                // Use case-insensitive JSON options
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var data = JsonSerializer.Deserialize<ContributionsResponse>(response, options);

                var packages = new Dictionary<string, List<string>>();

                // Print debug info
                Console.WriteLine($"API Response received, contributions count: {data?.contributions?.Count ?? 0}");

                if (data?.contributions != null && data.contributions.Count > 0)
                {
                    foreach (var contribution in data.contributions)
                    {
                        if (string.IsNullOrEmpty(contribution.AirportIcao) || string.IsNullOrEmpty(contribution.PackageName))
                            continue;

                        if (!packages.ContainsKey(contribution.AirportIcao))
                        {
                            packages[contribution.AirportIcao] = new List<string>();
                        }

                        if (!packages[contribution.AirportIcao].Contains(contribution.PackageName))
                        {
                            packages[contribution.AirportIcao].Add(contribution.PackageName);
                        }
                    }

                    Console.WriteLine($"Processed contributions into {packages.Count} airports with scenery packages");

                    // No need to add "Default Scenery" - the first item will be selected by default
                    return packages;
                }

                // If data?.contributions is null or empty, return an empty dictionary
                Console.WriteLine("No contributions found in API response");
                return new Dictionary<string, List<string>>();
            }
            catch (Exception ex)
            {
                // Log error or handle appropriately
                Console.WriteLine($"Error fetching scenery packages: {ex.Message}");
                return new Dictionary<string, List<string>>();
            }
        }
        public string GetSelectedPackage(string icao)
        {
            if (string.IsNullOrWhiteSpace(icao)) return string.Empty;
            // Case-insensitive lookup to avoid mismatches like "yssy" vs "YSSY"
            return _selectedPackages.TryGetValue(icao, out string? package)
                ? package
                : _selectedPackages.TryGetValue(icao.ToUpperInvariant(), out package)
                    ? package
                    : _selectedPackages.TryGetValue(icao.ToLowerInvariant(), out package)
                        ? package
                        : string.Empty;
        }

        public void SetSelectedPackage(string icao, string packageName)
        {
            if (string.IsNullOrWhiteSpace(icao)) return;
            icao = icao.Trim();
            packageName = packageName?.Trim() ?? string.Empty;
            // Avoid redundant writes/events if unchanged
            if (_selectedPackages.TryGetValue(icao, out var existing) &&
                string.Equals(existing, packageName, StringComparison.Ordinal))
            {
                return;
            }

            _selectedPackages[icao] = packageName;
            SaveSelectedPackages();

            try { PackageChanged?.Invoke(icao, packageName); } catch { }
        }
        private Dictionary<string, string> LoadSelectedPackages()
        {
            try
            {
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? string.Empty,
                    "BARS",
                    "Client"
                );
                if (!Directory.Exists(appDataPath))
                {
                    Directory.CreateDirectory(appDataPath);
                }

                string settingsPath = Path.Combine(appDataPath, SETTINGS_FILENAME);
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

                // Local type matching JsonSettingsStore serialization shape
                var persisted = new SettingsPersisted();

                if (File.Exists(settingsPath))
                {
                    try
                    {
                        string json = File.ReadAllText(settingsPath);
                        var loaded = JsonSerializer.Deserialize<SettingsPersisted>(json, options);
                        if (loaded != null) persisted = loaded;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to read settings.json; {ex.Message}");
                    }
                }

                var result = persisted.AirportPackages ?? new Dictionary<string, string>();
                return new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading scenery selections from settings.json: {ex.Message}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveSelectedPackages()
        {
            try
            {
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? string.Empty,
                    "BARS",
                    "Client"
                );
                if (!Directory.Exists(appDataPath))
                {
                    Directory.CreateDirectory(appDataPath);
                }

                string settingsPath = Path.Combine(appDataPath, SETTINGS_FILENAME);
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

                // Load existing to preserve unrelated fields (e.g., apiToken)
                var persisted = new SettingsPersisted();
                if (File.Exists(settingsPath))
                {
                    try
                    {
                        var current = JsonSerializer.Deserialize<SettingsPersisted>(File.ReadAllText(settingsPath), options);
                        if (current != null) persisted = current;
                    }
                    catch { /* ignore and overwrite minimal */ }
                }

                persisted.AirportPackages = new Dictionary<string, string>(_selectedPackages, StringComparer.OrdinalIgnoreCase);

                var json = JsonSerializer.Serialize(persisted, options);
                File.WriteAllText(settingsPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving scenery selections to settings.json: {ex.Message}");
            }
        }

        private sealed class SettingsPersisted
        {
            public string? ApiToken { get; set; }
            public Dictionary<string, string>? AirportPackages { get; set; }
        }
    }
}
