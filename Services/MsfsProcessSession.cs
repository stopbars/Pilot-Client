using System.Diagnostics;

namespace BARS_Client_V2.Services;

internal static class MsfsProcessSession
{
    internal static string? Capture(string simulator)
    {
        var name = simulator switch { "msfs2020" => "FlightSimulator", "msfs2024" => "FlightSimulator2024", _ => null };
        if (name == null) return null;
        var processes = Process.GetProcessesByName(name);
        try
        {
            var process = processes.FirstOrDefault();
            if (process == null) return null;
            return $"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return processes.FirstOrDefault()?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
