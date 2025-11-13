using Grpc.Core;
using Lnrpc;

namespace LNUnit.LND;

public class LNDCustomMessageHandler
{
    public LNDCustomMessageHandler(LNDNodeConnection connection)
    {
        Node = connection;
        Task.Factory.StartNew(ListenForCustomMessages);
    }

    public LNDNodeConnection Node { get; }
    public event EventHandler<CustomMessage>? OnMessage;

    public async Task<SendCustomMessageResponse> SendCustomMessageRequest(CustomMessage m)
    {
        return await Node.LightningClient.SendCustomMessageAsync(new SendCustomMessageRequest
        {
            Data = m.Data,
            Peer = m.Peer,
            Type = m.Type
        });
    }

    private async Task ListenForCustomMessages()
    {
        using (var streamingEvents = Node.LightningClient.SubscribeCustomMessages(new SubscribeCustomMessagesRequest()))
        {
            while (await streamingEvents.ResponseStream.MoveNext())
            {
                var message = streamingEvents.ResponseStream.Current;
                OnMessage?.Invoke(Node, message);
            }
        }
    }
}