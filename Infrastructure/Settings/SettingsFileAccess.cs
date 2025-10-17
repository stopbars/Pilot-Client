using System.Threading;

namespace BARS_Client_V2.Infrastructure.Settings;

internal static class SettingsFileAccess
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);
}
