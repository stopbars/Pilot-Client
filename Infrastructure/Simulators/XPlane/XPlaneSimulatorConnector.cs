using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using BARS_Client_V2.Domain;
using Microsoft.Extensions.Logging;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

public sealed class XPlaneSimulatorConnector : ISimulatorConnector, IDisposable
{
    public const string DefaultPipeName = "BARS.XPlaneBridge.v1";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(4);
    private static readonly JsonSerializerOptions BridgeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly ILogger<XPlaneSimulatorConnector> _logger;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<RawFlightSample> _telemetry = Channel.CreateBounded<RawFlightSample>(
        new BoundedChannelOptions(4)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _connectionCts;
    private Task? _readTask;
    private int _connectionGeneration;

    public XPlaneSimulatorConnector(ILogger<XPlaneSimulatorConnector> logger)
    {
        _logger = logger;
    }

    public string SimulatorId => "XPLANE";
    public string DisplayName => "X-Plane 12";
    public bool IsConnected => _pipe?.IsConnected == true && _connectionCts?.IsCancellationRequested == false;
    public int ConnectionGeneration => Volatile.Read(ref _connectionGeneration);

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return true;

        await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected) return true;
            await DisconnectCoreAsync().ConfigureAwait(false);

            var pipe = new NamedPipeClientStream(
                ".",
                DefaultPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                pipe.Dispose();
                _logger.LogDebug("X-Plane bridge pipe was not available within {timeout}ms", ConnectTimeout.TotalMilliseconds);
                return false;
            }
            catch (Exception ex)
            {
                pipe.Dispose();
                _logger.LogDebug(ex, "X-Plane bridge connection failed");
                return false;
            }

            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            _connectionCts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_connectionCts.Token), CancellationToken.None);

            try
            {
                var hello = await SendRequestAsync(
                    new
                    {
                        type = "hello",
                        protocolVersion = 1,
                        client = "BARS Pilot Client"
                    },
                    ct).ConfigureAwait(false);
                if (!hello.TryGetProperty("type", out var type) ||
                    !string.Equals(type.GetString(), "hello.ack", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("X-Plane bridge returned an invalid handshake.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "X-Plane bridge handshake failed");
                await DisconnectCoreAsync().ConfigureAwait(false);
                return false;
            }

            Interlocked.Increment(ref _connectionGeneration);
            _logger.LogInformation("Connected to X-Plane bridge via {pipe}", DefaultPipeName);
            return true;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async IAsyncEnumerable<RawFlightSample> StreamRawAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var connectionToken = _connectionCts?.Token ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connectionToken);
        while (!ct.IsCancellationRequested && IsConnected)
        {
            RawFlightSample sample;
            try
            {
                sample = await _telemetry.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            yield return sample;
        }
    }

    public Task ReplaceSceneAsync(
        string airport,
        long generation,
        IReadOnlyCollection<XPlaneBridgeLight> lights,
        CancellationToken ct = default) =>
        SendCommandAsync(new
        {
            type = "scene.replace",
            airport,
            generation,
            lights
        }, ct);

    public Task PatchLightsAsync(
        IReadOnlyCollection<XPlaneBridgeLightPatch> lights,
        CancellationToken ct = default) =>
        SendCommandAsync(new { type = "lights.patch", lights }, ct);

    public Task ClearSceneAsync(CancellationToken ct = default) =>
        SendCommandAsync(new { type = "scene.clear" }, ct);

    public async Task<JsonElement> GetBridgeStatusAsync(CancellationToken ct = default) =>
        await SendRequestAsync(new { type = "status" }, ct).ConfigureAwait(false);

    private async Task SendCommandAsync(object command, CancellationToken ct)
    {
        var response = await SendRequestAsync(command, ct).ConfigureAwait(false);
        var responseType = response.TryGetProperty("type", out var type) ? type.GetString() : null;
        if (string.Equals(responseType, "error", StringComparison.Ordinal))
        {
            var message = response.TryGetProperty("message", out var error)
                ? error.GetString()
                : "X-Plane bridge rejected the command.";
            throw new InvalidOperationException(message);
        }
        if (!string.Equals(responseType, "command.ack", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unexpected X-Plane bridge response '{responseType}'.");
        }
    }

    private async Task<JsonElement> SendRequestAsync(object request, CancellationToken ct)
    {
        if (!IsConnected || _writer == null)
        {
            throw new IOException("X-Plane bridge is not connected.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var requestJson = JsonSerializer.SerializeToElement(request, BridgeJsonOptions);
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in requestJson.EnumerateObject())
        {
            payload[property.Name] = property.Value.Clone();
        }
        payload["requestId"] = JsonSerializer.SerializeToElement(requestId);

        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("Could not allocate an X-Plane bridge request.");
        }

        try
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(
                    JsonSerializer.Serialize(payload, BridgeJsonOptions).AsMemory(),
                    ct).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
            return await completion.Task.WaitAsync(CommandTimeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("requestId", out var requestIdElement) &&
                    requestIdElement.ValueKind == JsonValueKind.String &&
                    _pending.TryRemove(requestIdElement.GetString()!, out var completion))
                {
                    completion.TrySetResult(root.Clone());
                    continue;
                }
                if (root.TryGetProperty("type", out var type) &&
                    string.Equals(type.GetString(), "telemetry", StringComparison.Ordinal))
                {
                    TryPublishTelemetry(root);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "X-Plane bridge read loop ended");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _logger.LogInformation("X-Plane bridge disconnected");
                _connectionCts?.Cancel();
                FailPending(new IOException("X-Plane bridge disconnected."));
            }
        }
    }

    private void TryPublishTelemetry(JsonElement message)
    {
        if (!message.TryGetProperty("latitude", out var latitude) ||
            !message.TryGetProperty("longitude", out var longitude) ||
            !message.TryGetProperty("onGround", out var onGround) ||
            !latitude.TryGetDouble(out var lat) ||
            !longitude.TryGetDouble(out var lon) ||
            (onGround.ValueKind != JsonValueKind.True && onGround.ValueKind != JsonValueKind.False))
        {
            return;
        }
        double? heading = null;
        if (message.TryGetProperty("heading", out var headingElement) &&
            headingElement.TryGetDouble(out var headingValue))
        {
            heading = NormalizeHeading(headingValue);
        }
        _telemetry.Writer.TryWrite(new RawFlightSample(lat, lon, onGround.GetBoolean(), heading));
    }

    private async Task DisconnectCoreAsync()
    {
        var cts = Interlocked.Exchange(ref _connectionCts, null);
        var pipe = Interlocked.Exchange(ref _pipe, null);
        _reader = null;
        _writer = null;
        try { cts?.Cancel(); } catch { }
        try { pipe?.Dispose(); } catch { }
        FailPending(new IOException("X-Plane bridge disconnected."));
        while (_telemetry.Reader.TryRead(out _)) { }
        var readTask = Interlocked.Exchange(ref _readTask, null);
        if (readTask != null && Task.CurrentId != readTask.Id)
        {
            try { await readTask.ConfigureAwait(false); } catch { }
        }
        cts?.Dispose();
    }

    private void FailPending(Exception error)
    {
        foreach (var pending in _pending)
        {
            if (_pending.TryRemove(pending.Key, out var completion))
            {
                completion.TrySetException(error);
            }
        }
    }

    private static double? NormalizeHeading(double heading)
    {
        if (!double.IsFinite(heading)) return null;
        heading %= 360.0;
        if (heading < 0) heading += 360.0;
        return heading;
    }

    public void Dispose()
    {
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
        _connectionGate.Dispose();
        _writeGate.Dispose();
    }
}
