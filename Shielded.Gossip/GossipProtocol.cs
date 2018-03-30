﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Shielded.Gossip
{
    public class GossipProtocol : IDisposable
    {
        public GossipProtocol(string ownId, IPEndPoint localEndpoint, IDictionary<string, IPEndPoint> servers)
        {
            OwnId = ownId;
            Servers = servers;
            LocalEndpoint = localEndpoint;

            StartListening();
        }

        public readonly string OwnId;
        public readonly IPEndPoint LocalEndpoint;
        public readonly IDictionary<string, IPEndPoint> Servers;

        private UdpClient _listener;

        public void Dispose()
        {
            var listener = _listener;
            if (listener != null)
            {
                listener.Dispose();
                _listener = null;
            }
        }

        private async void StartListening()
        {
            _listener = new UdpClient(LocalEndpoint);
            try
            {
                while (true)
                {
                    var res = await _listener.ReceiveAsync();
                    MessageReceived?.Invoke(this, Serializer.Deserialize<Message>(res.Buffer));
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                ListenerException = ex;
            }
        }

        public Exception ListenerException { get; private set; }

        public event EventHandler<Message> MessageReceived;

        public Task Broadcast(Message msg)
        {
            msg.From = OwnId;
            msg.To = null;
            return Task.WhenAll(Servers.Select(s => Send(s.Key, msg)).ToArray());
        }

        public Task Send(string server, Message msg)
        {
            var ip = Servers[server];
            return Send(ip, Serializer.Serialize(msg));
        }

        private async Task Send(IPEndPoint ip, byte[] bytes)
        {
            using (var client = new UdpClient())
            {
                await client.SendAsync(bytes, bytes.Length, ip);
            }
        }
    }
}
