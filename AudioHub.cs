using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace PhoneSpeaker;

/// <summary>Fans audio frames out to every connected phone.</summary>
sealed class AudioHub
{
    // 30 frames = 300 ms. A phone that falls further behind loses its oldest frames
    // instead of building up delay.
    const int QueueFrames = 30;

    readonly ConcurrentDictionary<Guid, Channel<byte[]>> _clients = new();

    public int Count => _clients.Count;

    public void Broadcast(byte[] frame)
    {
        foreach (var ch in _clients.Values) ch.Writer.TryWrite(frame);
    }

    public async Task ServeAsync(WebSocket ws, CancellationToken aborted)
    {
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueFrames)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);

        // Watch for the phone closing the connection.
        var receive = Task.Run(async () =>
        {
            var buf = new byte[256];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { }
            finally { cts.Cancel(); }
        });

        try
        {
            var hello = Encoding.UTF8.GetBytes(
                $"{{\"rate\":{LoopbackSource.SampleRate},\"channels\":2,\"frame\":{LoopbackSource.FrameSamples}}}");
            await ws.SendAsync(hello, WebSocketMessageType.Text, true, cts.Token);

            _clients[id] = queue;
            await foreach (var frame in queue.Reader.ReadAllAsync(cts.Token))
                await ws.SendAsync(frame, WebSocketMessageType.Binary, true, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            cts.Cancel();
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch { }
            await receive;
        }
    }
}
