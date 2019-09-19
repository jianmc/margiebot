using System;
using System.Threading.Tasks;

namespace MargieBot.WebSockets
{
    public interface IMargieBotWebSocket
    {
        event EventHandler OnClose;
        event MargieBotWebSocketMessageReceivedEventHandler OnMessage;
        event EventHandler OnOpen;

        Task Connect(string uri);
        Task Connect(Uri uri);
        Task Disconnect();
        void Dispose();
        Task Send(string message);

        bool IsWebSocketOpen { get; }
    }
}