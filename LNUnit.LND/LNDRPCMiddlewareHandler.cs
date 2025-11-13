using System.Diagnostics;
using Grpc.Core;
using Lnrpc;

namespace LNUnit.LND;

/// <summary>
///     This is a very simple interceptor loop.
/// </summary>
public class LNDRPCMiddlewareHandler : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly bool _disposed = false;
    private readonly Task<Task> _task;

    public LNDRPCMiddlewareHandler(LNDNodeConnection connection,
        Func<RPCMiddlewareRequest, Task<RPCMiddlewareResponse>> interceptLogic)
    {
        Node = connection;
        _task = Task.Factory.StartNew(AttachInterceptor, _cancellationTokenSource.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Current);
        OnIntercept = interceptLogic;
        while (!Running)
            Task.Delay(100).GetAwaiter().GetResult();
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

    public event Func<RPCMiddlewareRequest, Task<RPCMiddlewareResponse>> OnIntercept;

    private async Task AttachInterceptor()
    {
        Debug.Print($"AttachRPCMiddlewareInterceptor: {Node.LocalAlias}");
        try
        {
            using (var streamingEvents =
                   Node.LightningClient.RegisterRPCMiddleware())
            {
                Running = true;
                while (await streamingEvents.ResponseStream.MoveNext().ConfigureAwait(false))
                {
                    Debug.Print($"RPCMiddlewareEvent: {Node.LocalAlias}");

                    var message = streamingEvents.ResponseStream.Current;
                    var result = OnIntercept(message);
                    await streamingEvents.RequestStream.WriteAsync(await result).ConfigureAwait(false);
                    InterceptCount++;
                }
            }
        }
        catch (Exception)
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