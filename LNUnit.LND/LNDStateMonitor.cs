using Grpc.Core;
using Lnrpc;

namespace LNUnit.LND;

public class LNDStateMonitor
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly bool _disposed = false;
    private readonly Task<Task> _task;

    public LNDStateMonitor(LNDNodeConnection connection, Action<SubscribeStateResponse>? onStateChange = null)
    {
        Node = connection;
        _task = Task.Factory.StartNew(SubscribeHtlcEventStream, _cancellationTokenSource.Token);
        OnStateChange = onStateChange;
        while (!Running)
            Task.Delay(100).GetAwaiter().GetResult();
    }

    public bool Running { get; set; }

    public LNDNodeConnection Node { get; }
    public event Action<SubscribeStateResponse>? OnStateChange;

    private async Task SubscribeHtlcEventStream()
    {
        try
        {
            using (var streamingEvents = Node.StateClient.SubscribeState(new SubscribeStateRequest()))
            {
                Running = true;
                while (await streamingEvents.ResponseStream.MoveNext())
                {
                    var state = streamingEvents.ResponseStream.Current;
                    OnStateChange?.Invoke(state);
                }
            }
        }
        catch (Exception)
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
            Node?.Dispose();
            Running = false;
        }
    }
}