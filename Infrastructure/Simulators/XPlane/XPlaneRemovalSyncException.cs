using System.IO;
using System.Net;
using System.Net.Http;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal sealed record XPlaneRemovalFailure(string Airport, string Reason, bool Retryable);

internal sealed class XPlaneRemovalSyncException(IReadOnlyList<XPlaneRemovalFailure> failures, bool changed)
    : Exception(string.Join(Environment.NewLine, failures.Select(failure => $"{failure.Airport}: {failure.Reason}")))
{
    public IReadOnlyList<XPlaneRemovalFailure> Failures { get; } = failures;
    public bool Changed { get; } = changed;
    public bool CanRetry => Failures.Any(failure => failure.Retryable);

    internal static bool IsRetryable(Exception error) => error switch
    {
        XPlaneRemovalSyncException batch => batch.CanRetry,
        InvalidDataException => false,
        HttpRequestException http => http.StatusCode == null || http.StatusCode == HttpStatusCode.RequestTimeout ||
            http.StatusCode == HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500,
        IOException => true,
        OperationCanceledException => true,
        _ => false
    };
}
