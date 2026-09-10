using System.Threading;

namespace BARS_Client_V2.Services;

internal static class RemovalFileAccess
{
    internal static readonly SemaphoreSlim MsfsTransactionGate = new(1, 1);
    internal static readonly SemaphoreSlim MsfsGate = new(1, 1);
}
