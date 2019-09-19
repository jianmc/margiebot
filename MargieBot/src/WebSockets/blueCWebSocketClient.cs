using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;
using MargieBot.WebSockets;

namespace blueC.Service.Client.WebSocket.Requests
{
    public sealed class blueCWebSocketClient : IDisposable, MargieBot.WebSockets.IMargieBotWebSocket
    {
        System.Collections.Concurrent.BlockingCollection<string> messageCollection = new System.Collections.Concurrent.BlockingCollection<string>(100);
        private const int ReceiveChunkSize = 1024;
        private const int SendChunkSize = 1024;

        Uri uri;

        private Action<blueCWebSocketClient> Opened;
        private Action<string, blueCWebSocketClient> MessageReceived;
        private Action<blueCWebSocketClient> Closed;
        private Action<blueCWebSocketClient, Exception> Error;

        public void Dispose()
        {
            CloseWebSocket(CancellationToken.None).Wait(5000);
        }

        public ILogger LogUnit { get; }

        public blueCWebSocketClient() : this(null)
        {

        }

        public blueCWebSocketClient(ILogger logUnit = null)
        {
            this.LogUnit = logUnit;

            Task.Run( () => {
                SpinWait.SpinUntil(() => MessageReceived != null, 60000);
                while (!this.messageCollection.IsCompleted)
                    foreach (var message in this.messageCollection.GetConsumingEnumerable())
                    {
                        MessageReceived(message, this);
                    }
            });
        }
        ClientWebSocket WebSocket;

        public bool IsWebSocketOpen { get { return WebSocket?.State == System.Net.WebSockets.WebSocketState.Open; } }

        CancellationTokenSource cancelSource = new CancellationTokenSource();

        public async Task CreateWebSocket(string uri, string customer, TimeSpan expire, 
            Action openAction = null, 
            Action closeAction = null, 
            Action<Exception> errorAction = null, 
            Action renewAction = null, 
            bool keepAliveEnabled = false, TimeSpan? keepAliveInterval = null,
            List<KeyValuePair<string, string>> header = null)
        {
            if (WebSocket?.State == System.Net.WebSockets.WebSocketState.Open)
                return;

            this.uri = new Uri(uri);

            await CloseWebSocket(CancellationToken.None);

            WebSocket = new ClientWebSocket();

            if (header != null)
                foreach (var h in header)
                    WebSocket.Options.SetRequestHeader(h.Key, h.Value);

            if (keepAliveEnabled && keepAliveInterval.HasValue)
                WebSocket.Options.KeepAliveInterval = keepAliveInterval.Value;

            //WebSocket.EnableAutoSendPing = keepAliveEnabled;
            //WebSocket.AutoSendPingInterval = (int)keepAliveInterval?.TotalSeconds;

            this.Opened = (client) => {
                if (client.WebSocket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    LogUnit?.LogInformation($"Websocket Opened");
                    openAction?.Invoke();
                    Task.Factory.StartNew(async () =>
                    {
                        bool done = false;
                        while (!done)
                        {
                            await Task.Delay(expire, cancelSource.Token);

                            if (cancelSource.IsCancellationRequested)
                            {
                                done = true;
                                LogUnit?.LogInformation($"Websocket Expire - Cancelled");
                            }
                            else if (IsWebSocketOpen)
                            {
                                if (keepAliveEnabled && client.LastActiveTime < DateTime.Now.Subtract(keepAliveInterval.Value.Add(keepAliveInterval.Value).Add(keepAliveInterval.Value)))
                                {
                                    done = true;
                                    LogUnit?.LogInformation($"Websocket Expire - Closing");
                                    await client?.CloseWebSocket(CancellationToken.None);
                                }
                                else
                                    renewAction?.Invoke();
                            }
                            else
                            {
                                done = true;
                                LogUnit?.LogInformation($"Websocket Expire - Not Open");
                            }
                        }
                    }, cancelSource.Token);

                    StartListen(cancelSource.Token);
                }
            };
            
            this.Closed = (client) =>
            {
                cancelSource.Cancel();
                LogUnit?.LogInformation($"Websocket Closed");
                closeAction?.Invoke();
            };

            this.Error = (client, e) =>
            {
                cancelSource.Cancel();
                LogUnit?.LogError($"Websocket Error", e);
                errorAction?.Invoke(e);
            };

            CancellationToken token = new CancellationToken();
            await WebSocket.ConnectAsync(this.uri, token);
            this.Opened(this);
        }

        DateTime lastKeepaliveWriteTime = DateTime.Now;

        public void OnMessageReceived(Action<dynamic>[] actions, string customer)
        {
            this.MessageReceived += (message, client) =>
            {
                LastActiveTime = DateTime.Now;
                if (string.IsNullOrWhiteSpace(message))
                    return;

                if (message.Contains("keep") && message.Contains("alive"))
                {
                    if (DateTime.Now > lastKeepaliveWriteTime.AddMinutes(15))
                    {
                        LogUnit?.LogInformation($"Websocket Event: {message}");
                        lastKeepaliveWriteTime = DateTime.Now;
                    }
                }
                else
                    LogUnit?.LogInformation($"Websocket Event: {message}");

                if (actions == null)
                    return;

                foreach (var action in actions)
                    action?.Invoke(Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(message));
            };
        }

        public async Task<bool> OpenWebSocket(CancellationToken token)
        {
            ManualResetEvent manualResetEvent = new ManualResetEvent(false);
            this.Opened += (client) => manualResetEvent.Set();

            if (!IsWebSocketOpen)
                await WebSocket?.ConnectAsync(uri, token);  // If already open, will cause Connecting state. 

            manualResetEvent.WaitOne(2000); // Wait 2 seconds to open.

            return IsWebSocketOpen;
        }

        public async Task CloseWebSocket(CancellationToken token)
        {
            if (IsWebSocketOpen)
                await WebSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "", token);

            WebSocket = null;
        }

        EventWaitHandle waitFirstEvent;
        string firstEvent;
        private DateTime LastActiveTime;

        public event EventHandler OnClose;
        public event MargieBotWebSocketMessageReceivedEventHandler OnMessage;
        public event EventHandler OnOpen;

        public async Task<string> OnFirstEvent(string customer, CancellationToken token)
        {
            waitFirstEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
            this.MessageReceived += WebSocket_FirstEvent;
            if (await OpenWebSocket(token))
            {
                if (waitFirstEvent.WaitOne(2000))
                {
                    LogUnit?.LogInformation($"Websocket Subscribe: {firstEvent}");
                    this.MessageReceived -= WebSocket_FirstEvent;
                    return firstEvent;
                }
            }

            throw new TimeoutException("WebSocket timed out after 2 seconds waiting for subscription event.");
        }

        private void WebSocket_FirstEvent(string message, blueCWebSocketClient client)
        {
            firstEvent = message;
            waitFirstEvent.Set();
        }
    
        private async Task SendBytesAsync(byte[] bytes, CancellationToken token)
        {
            if (WebSocket.State != System.Net.WebSockets.WebSocketState.Open)
            {
                throw new Exception("Connection is not open.");
            }

            var messageBuffer = bytes;
            var messagesCount = (int)Math.Ceiling((double)messageBuffer.Length / SendChunkSize);

            for (var i = 0; i < messagesCount; i++)
            {
                var offset = (SendChunkSize * i);
                var count = SendChunkSize;
                var lastMessage = ((i + 1) == messagesCount);

                if ((count * (i + 1)) > messageBuffer.Length)
                {
                    count = messageBuffer.Length - offset;
                }

                await WebSocket.SendAsync(new ArraySegment<byte>(bytes, offset, count), WebSocketMessageType.Binary, lastMessage, token);
            }
        }

        private async void StartListen(CancellationToken token)
        {
            var buffer = new byte[ReceiveChunkSize];
            var stringResult = new StringBuilder();

            try
            {
                while (WebSocket?.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        stringResult.Clear();
                        result = await WebSocket?.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                        if (result != null)
                        {
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await WebSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                                Closed(this);
                            }
                            else if (result.Count > 0 && buffer != null) 
                            {
                                stringResult?.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                            }
                        }
                    } while (result != null && !result.EndOfMessage);

                    if (stringResult.Length > 0)
                        messageCollection.TryAdd(stringResult.ToString());
                }
            }
            catch (Exception)
            {
                Closed(this);
            }
            finally
            {
                WebSocket.Dispose();
            }
        }

        public async Task Connect(string uri)
        {
            await this.CreateWebSocket(uri, "", TimeSpan.MaxValue);
        }

        public async Task Connect(Uri uri)
        {
            await this.CreateWebSocket(uri.ToString(), "", TimeSpan.MaxValue);
        }

        public async Task Disconnect()
        {
            await this.CloseWebSocket(CancellationToken.None);
        }

        public async Task Send(string message)
        {
            await this.SendBytesAsync(Encoding.UTF8.GetBytes(message), CancellationToken.None);
        }
    }
}
