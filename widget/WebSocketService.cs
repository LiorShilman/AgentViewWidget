using System.IO;
using System.Net.WebSockets;
using System.Text.Json;

namespace AgentLiveWidget;

/// <summary>
/// Resilient WebSocket client for the local state server.
/// Reconnects every 3 seconds and reports connection changes.
/// </summary>
public sealed class WebSocketService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Uri _uri;
    private readonly CancellationTokenSource _cts = new();

    public event Action<WidgetSnapshot>? SnapshotReceived;
    public event Action<bool>? ConnectionChanged;

    public WebSocketService(string url) => _uri = new Uri(url);

    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var buffer = new byte[64 * 1024];

        while (!_cts.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await ws.ConnectAsync(_uri, connectTimeout.Token);
                ConnectionChanged?.Invoke(true);

                while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            throw new WebSocketException("server closed connection");
                        }
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var snapshot = JsonSerializer.Deserialize<WidgetSnapshot>(message.ToArray(), JsonOptions);
                    if (snapshot is not null)
                    {
                        SnapshotReceived?.Invoke(snapshot);
                    }
                }
            }
            catch when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // connection failed or dropped — fall through to retry
            }

            ConnectionChanged?.Invoke(false);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
