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
    }public class ContributionsResponse
    {
        public List<SceneryContribution> contributions { get; set; }
    }    public class SceneryService
    {
        private const string API_URL = "https://v2.stopbars.com/contributions?status=approved";
        private const string SETTINGS_FILE = "scenerySelections.json";
        private readonly HttpClient _httpClient;
        private Dictionary<string, string> _selectedPackages;
        private static SceneryService? _instance;
        
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
            {                var response = await _httpClient.GetStringAsync(API_URL);
                
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
        }        public string GetSelectedPackage(string icao)
        {
            return _selectedPackages.TryGetValue(icao, out string? package) ? package : string.Empty;
        }

        public void SetSelectedPackage(string icao, string packageName)
        {
            _selectedPackages[icao] = packageName;
            SaveSelectedPackages();
        }        private Dictionary<string, string> LoadSelectedPackages()
        {
            try
            {
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? "",
                    "BARS",
                    "Client"
                );
                string filePath = Path.Combine(appDataPath, SETTINGS_FILE);

                if (!Directory.Exists(appDataPath))
                {
                    Directory.CreateDirectory(appDataPath);
                }

                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var result = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (result != null)
                    {
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading scenery selections: {ex.Message}");
            }

            return new Dictionary<string, string>();
        }

        private void SaveSelectedPackages()
        {
            try
            {
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BARS",
                    "Client"
                );
                string filePath = Path.Combine(appDataPath, SETTINGS_FILE);

                if (!Directory.Exists(appDataPath))
                {
                    Directory.CreateDirectory(appDataPath);
                }

                string json = JsonSerializer.Serialize(_selectedPackages);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving scenery selections: {ex.Message}");
            }
        }
    }
}
