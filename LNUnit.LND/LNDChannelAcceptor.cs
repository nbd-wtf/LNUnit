using Lnrpc;

namespace LNUnit.LND;

/// <summary>
///     This is a very simple interceptor loop.
/// </summary>
public class LNDChannelAcceptor : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task<Task> _task;

    private bool _disposed;

    public LNDChannelAcceptor(LNDNodeConnection connection,
        Func<ChannelAcceptRequest, Task<ChannelAcceptResponse>> interceptLogic)
    {
        Node = connection;
        _task = Task.Factory.StartNew(AttachInterceptor, _cancellationTokenSource.Token);
        OnChannelRequest = interceptLogic;
        while (!Running)
            Task.Delay(100).GetAwaiter().GetResult();
    }

    public bool Running { get; private set; }
    public ulong InterceptCount { get; }

    public LNDNodeConnection Node { get; }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _task.Dispose();
            while (Running)
                Task.Delay(100);
            // Node.Dispose();
        }
    }

    public event Func<ChannelAcceptRequest, Task<ChannelAcceptResponse>> OnChannelRequest;

    private async Task AttachInterceptor()
    {
        try
        {
            using (var channelAcceptor = Node.LightningClient.ChannelAcceptor())
            {
                Running = true;
                while (await channelAcceptor.ResponseStream.MoveNext(_cancellationTokenSource.Token))
                {
                    var message = channelAcceptor.ResponseStream.Current;
                    var result = OnChannelRequest(message);
                    await channelAcceptor.RequestStream.WriteAsync(await result);
                }
            }
        }
        catch (Exception)
        {
            Running = false;
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