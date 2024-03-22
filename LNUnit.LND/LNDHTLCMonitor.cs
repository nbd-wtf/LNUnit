using Grpc.Core;
using Routerrpc;

namespace LNUnit.LND;

public class LNDHTLCMonitor
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly bool _disposed = false;
    private readonly Task<Task> _task;

    public LNDHTLCMonitor(LNDNodeConnection connection, Action<HtlcEvent> onHtlcEvent = null)
    {
        Node = connection;
        _task = Task.Factory.StartNew(SubscribeHtlcEventStream, _cancellationTokenSource.Token);
        OnHtlcEvent = onHtlcEvent;
        while (!Running)
            Task.Delay(100).Wait();
    }

    public bool Running { get; set; }

    public LNDNodeConnection Node { get; }
    public event Action<HtlcEvent> OnHtlcEvent;

    private async Task SubscribeHtlcEventStream()
    {
        try
        {
            using (var streamingEvents = Node.RouterClient.SubscribeHtlcEvents(new SubscribeHtlcEventsRequest()))
            {
                Running = true;
                while (await streamingEvents.ResponseStream.MoveNext())
                {
                    var htlcEvent = streamingEvents.ResponseStream.Current;
                    OnHtlcEvent(htlcEvent);
                }
            }
        }
        catch (Exception e)
        {
            // do nothing
        }

        Running = false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cancellationTokenSource.Dispose();
            _task.Dispose();
            Node.Dispose();
            Running = false;
        }
    }
}