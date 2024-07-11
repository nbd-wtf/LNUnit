using System.Diagnostics;
using Grpc.Core;
using Lnrpc;
using Routerrpc;

namespace LNUnit.LND;

/// <summary>
///     This is a very simple interceptor loop.
/// </summary>
public class LNDGraphEventsHandler : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public LNDNodeConnection Node { get; }
    private readonly bool _disposed = false;
    private readonly Task<Task> _task;

    public LNDGraphEventsHandler(LNDNodeConnection connection,
        Action<GraphTopologyUpdate> onGraphEvent)
    {
        Node = connection;
        _task = Task.Factory.StartNew(StartListening, _cancellationTokenSource.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Current);
        OnGraphEvent = onGraphEvent;
        while (!Running)
            Task.Delay(100).GetAwaiter().GetResult();
    }

    public bool Running { get; private set; }
    public ulong InterceptCount { get; private set; }


    public void Dispose()
    {
        if (!_disposed)
        {
            _cancellationTokenSource.Dispose();
            _task.Dispose();
            Node.Dispose();
        }
    }

    public event Action<GraphTopologyUpdate> OnGraphEvent;

    private async Task StartListening()
    {
        Debug.Print($"StartListening: {Node.LocalAlias}");
        try
        {
            using (var streamingEvents =
                   Node.LightningClient.SubscribeChannelGraph(new GraphTopologySubscription()
                   {

                   }))
            {
                Running = true;
                while (await streamingEvents.ResponseStream.MoveNext().ConfigureAwait(false))
                {
                    Debug.Print($"Event: {Node.LocalAlias}");

                    var message = streamingEvents.ResponseStream.Current;
                    OnGraphEvent(message);
                    InterceptCount++;
                }
            }
        }
        catch (Exception e)
        {
            // do nothing
        }

        Running = false;
    }



    public void Cancel()
    {
        _cancellationTokenSource.Cancel();
        while (!_task.IsCompleted) Task.Delay(100).GetAwaiter().GetResult();
        Running = false;
        Dispose();
    }
}