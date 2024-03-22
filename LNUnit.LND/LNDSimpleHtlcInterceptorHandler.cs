using Grpc.Core;
using Routerrpc;

namespace LNUnit.LND;

/// <summary>
///     This is a very simple interceptor loop.
/// </summary>
public class LNDSimpleHtlcInterceptorHandler : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly bool _disposed = false;
    private readonly Task<Task> _task;

    public LNDSimpleHtlcInterceptorHandler(LNDNodeConnection connection,
        Func<ForwardHtlcInterceptRequest, Task<ForwardHtlcInterceptResponse>> interceptLogic)
    {
        Node = connection;
        _task = Task.Factory.StartNew(AttachInterceptor, _cancellationTokenSource.Token);
        OnIntercept = interceptLogic;
        while (!Running)
            Task.Delay(100).Wait();
    }

    public bool Running { get; private set; }
    public ulong InterceptCount { get; private set; }

    public LNDNodeConnection Node { get; }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cancellationTokenSource.Dispose();
            _task.Dispose();
            Node.Dispose();
        }
    }

    public event Func<ForwardHtlcInterceptRequest, Task<ForwardHtlcInterceptResponse>> OnIntercept;

    private async Task AttachInterceptor()
    {
        try
        {
            using (var streamingEvents =
                   Node.RouterClient.HtlcInterceptor(cancellationToken: _cancellationTokenSource.Token))
            {
                Running = true;
                while (await streamingEvents.ResponseStream.MoveNext())
                {
                    var message = streamingEvents.ResponseStream.Current;
                    var result = OnIntercept(message);
                    await streamingEvents.RequestStream.WriteAsync(await result);
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
        while (!_task.IsCompleted) Task.Delay(100).Wait();
        Running = false;
        Dispose();
    }
}