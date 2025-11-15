using Grpc.Core;
using Lnrpc;

namespace LNUnit.LND;

public class LNDChannelInterceptorHandler
{
    public LNDChannelInterceptorHandler(LNDNodeConnection connection)
    {
        Node = connection;
        Task.Factory.StartNew(ListenFromChannelAccept);
    }

    public LNDNodeConnection Node { get; }
    public event Func<ChannelAcceptRequest, Task<ChannelAcceptResponse>>? OnChannelRequest;

    private async Task ListenFromChannelAccept()
    {
        using (var streamingEvents = Node.LightningClient.ChannelAcceptor())
        {
            while (await streamingEvents.ResponseStream.MoveNext().ConfigureAwait(false))
            {
                var channelRequest = streamingEvents.ResponseStream.Current;
                var response = OnChannelRequest?.Invoke(channelRequest);
                if (response != null)
                    await streamingEvents.RequestStream.WriteAsync(await response).ConfigureAwait(false);
            }
        }
    }
}