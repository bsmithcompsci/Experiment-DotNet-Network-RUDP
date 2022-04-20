using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityEngine.Assertions;

namespace NetworkLibrary
{
    public enum NetworkPlatform
    {
        /// <summary>
        /// The value has yet to be initialized to one of the states.
        /// </summary>
        UNINITIALIZED,
        /// <summary>
        /// CLIENT Network State refers to this entirely will listen and send to a server.
        /// </summary>
        CLIENT,
        /// <summary>
        /// SERVER Network State refers to this entirely will accept, listen, and send to several clients.
        /// </summary>
        SERVER,
    }
    public enum NetworkState
    {
        UNINITIALIZED,
        CONNECTING,
        CONNECTED,
        DISCONNECTING
    }

    // State of the socket for reading client data asynchronously  
    public class SocketState
    {
        // Size of receive buffer.  
        public const int BufferSize = 1024;

        // Receive buffer.  
        public readonly byte[] buffer = new byte[BufferSize];

        // Collective Buffer
        public readonly List<byte> collectiveBuffer = new List<byte>();

        // Client socket.
        public UdpClient workSocket = null;
    }
    public class SendState
    {
        public UdpClient workSocket = null;
        public NetworkPlatform state = NetworkPlatform.UNINITIALIZED;
    }
    public struct ReliablePacketInfo
    {
        public long sentTime;
        public Packet packet;
    }

    public sealed class Peer
    {
        public IPEndPoint remoteEndPoint;
        public uint nextAcknowledgementNumber;
        public NetworkState state = NetworkState.CONNECTING;
        public long Ping
        {
            get
            {
                return (DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) - lastPinged;
            }
        }
        private bool attemptedPing = false;
        private long lastPinged = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
        private NetworkManager networkManager;

        private Dictionary<PacketFlags, PacketBulk> sendBulkBufferPackets = new Dictionary<PacketFlags, PacketBulk>();
        public bool HasPacketsToSend
        {
            get
            {
                return sendBulkBufferPackets != null ? sendBulkBufferPackets.Count > 0 : false;
            }
        }

        public Peer(IPEndPoint _remoteEndpoint, NetworkManager _managerInstance)
        {
            remoteEndPoint = _remoteEndpoint;
            networkManager = _managerInstance;
        }

        public void Send(Packet _packet)
        {
            networkManager.Send(_packet, this);
        }
        internal void SendImmediate(byte[] _bytes, IPEndPoint _remoteEndpoint = null)
        {
            networkManager.SendImmediate(_bytes, _remoteEndpoint);
        }
        internal void SendBulkedPackets()
        {
            if (sendBulkBufferPackets == null)
                return;

            lock(sendBulkBufferPackets)
            {
                foreach (var sendBulkBufferPacket in sendBulkBufferPackets)
                {
                    Packet packet = new Packet(sendBulkBufferPacket.Value, sendBulkBufferPacket.Key | PacketFlags.Bulk);
                    SendImmediate(packet.Pack(nextAcknowledgementNumber), packet.remoteEndPoint);
                    nextAcknowledgementNumber = packet.ACK.Value;
                }
            }

            // Todo :: Hand off to a send buffer history window.
            sendBulkBufferPackets.Clear();
        }
        internal void AddSendPacket(Packet _packet)
        {
            lock(sendBulkBufferPackets)
            {
                if (!sendBulkBufferPackets.ContainsKey(_packet.flags))
                    sendBulkBufferPackets[_packet.flags] = new PacketBulk(_packet.remoteEndPoint);
                sendBulkBufferPackets[_packet.flags].AddPacket(_packet);
            }
        }

        internal void InvokePing()
        {
            if (networkManager.IsServer)
            {
                // 1111 0000
                SendImmediate(new byte[1] { 240 }, remoteEndPoint);
            }
            else if (networkManager.IsClient)
            {
                // 0000 1111
                SendImmediate(new byte[1] { 15 });
            }
            attemptedPing = true;
        }
        internal void UpdatePing()
        {
            float minTickRate = 1f / networkManager.TICK_RATE;
            if ((Ping > networkManager.TIMEOUTMS / 3) || (!attemptedPing && networkManager.timer >= minTickRate))
            {
                Debug.Log($"[{ networkManager.NetworkPlatform }] Emergency Send Ping");
                InvokePing();
            }
        }
        internal void MarkPing()
        {
            Debug.Log($"[{ networkManager.NetworkPlatform }] Updated Ping");
            attemptedPing = false;
            lastPinged = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
        }
    }

    public sealed class NetworkManager
    {
        public int TIMEOUTMS { get; set; } = 3000;
        public int TICK_RATE { get; set; } = 200;
        public ulong Tick { get; set; }

        internal float timer;
        DateTime lastDeltaTime = DateTime.Now;
        float deltaTime = 0;
        
        private const string InitialPacket = "handshake";
        private const string SyncTickPacket = "syncTick";
        private readonly uint VerificationCRC32 = Crc32.hash("InternalLibraryNetworkHASH"); // Todo :: Network Global User-Setting?
        private readonly uint InitialPacketCRC32 = Crc32.hash(InitialPacket);
        private readonly uint SyncTickPacketCRC32 = Crc32.hash(SyncTickPacket);

        private const ushort DEFAULT_PORT = 45545;
        public NetworkPlatform NetworkPlatform { get; private set; } = NetworkPlatform.UNINITIALIZED;
        public NetworkState NetworkState { get; private set; } = NetworkState.UNINITIALIZED;

        public uint MaxNetworkedClients { get; private set; } = 32;
        public uint MaxNetworkedQueuedClients { get; private set; } = 1000;
        public bool IsServer
        {
            get
            {
                return NetworkPlatform != NetworkPlatform.CLIENT;
            }
        }
        public bool IsClient
        {
            get
            {
                return NetworkPlatform == NetworkPlatform.CLIENT;
            }
        }
        public long ClientPing
        {
            get
            {
                //Assert.AreEqual(NetworkState, NetworkState.CLIENT, "ClientPing function is meant for Client's only.");
                return peers.Count == 1 ? peers.First().Value.Ping : -1;
            }
        }

        private bool shuttingDown;
        public uint? ConnectionUID { get; private set; }
        private Dictionary<uint, Peer> peers = new Dictionary<uint, Peer>();
        public UdpClient socket { get; private set; }

        // Server-Side
        public delegate void OnListenServerInitializedDelegate(NetworkManager _manager);
        public event OnListenServerInitializedDelegate OnListenServerInitialized;
        public delegate bool OnListenClientAcceptingDelegate(NetworkManager _manager, uint _remoteEndpointCRC32);
        public event OnListenClientAcceptingDelegate OnListenClientAccepting;
        public delegate void OnListenClientAcceptedDelegate(NetworkManager _manager, Peer _peer);
        public event OnListenClientAcceptedDelegate OnListenClientAccepted;
        public delegate void OnListenClientDroppedDelegate(NetworkManager _manager, Peer _peer, string _reason);
        public event OnListenClientDroppedDelegate OnListenClientDropped;

        // Client-Side
        public delegate void OnClientConnectedDelegate(NetworkManager _manager, Peer _serverPeer);
        public event OnClientConnectedDelegate OnClientConnected;

        // Shared
        public delegate void OnShutdownDelegate(NetworkManager _manager);
        public event OnShutdownDelegate OnShutdown;
        public delegate void OnPacketEventDelegate(NetworkManager _manager, Peer _peer, Packet _packet);
        public event OnPacketEventDelegate OnAnyPacketEvent;
        private Dictionary<uint, OnPacketEventDelegate> networkEvents = new Dictionary<uint, OnPacketEventDelegate>();

        public NetworkManager()
        {
            Thread thread = new Thread(new ThreadStart(() =>
            {
                while (true)
                {
                    Update();
                }
            }));
            thread.Name = "Network Manager";
            thread.Start();
        }

        public void RegisterEvent(string _eventName, OnPacketEventDelegate _callback)
        {
            networkEvents[Crc32.hash(_eventName)] += _callback;
        }
        public void DeregisterEvent(string _eventName, OnPacketEventDelegate _callback)
        {
            uint crc32 = Crc32.hash(_eventName);
            if (networkEvents.ContainsKey(crc32))
            {
                networkEvents[crc32] -= _callback;
            }
        }
        private void InvokeEvent(uint _eventCRC32, UdpClient _socket, Packet _packet, uint _remoteEPCRC32, Peer _peerIncoming)
        {
            OnAnyPacketEvent?.Invoke(this, _peerIncoming, _packet);

            // If we are the initial packet, we don't have ton of information about this potential client.
            // We want to reply back to this message, notifying the client their unique identifier.
            if (_eventCRC32 == InitialPacketCRC32)
            {
                if ((_packet.flags & PacketFlags.Reliable) != 0)
                {
                    if (IsServer)
                    {
                        // Too many clients.
                        if (peers.Count > MaxNetworkedClients)
                            return;

                        if (OnListenClientAccepting?.Invoke(this, _remoteEPCRC32) ?? true)
                        {
                            Packet packet = new Packet(InitialPacketCRC32, _packet.remoteEndPoint, PacketFlags.Reliable);
                            packet.Write(_remoteEPCRC32);

                            _peerIncoming = new Peer(_packet.remoteEndPoint, this);
                            peers.Add(_remoteEPCRC32, _peerIncoming);

                            Send(packet);
                        }
                    }
                    else if (IsClient)
                    {
                        ConnectionUID = _packet.ReadUInt();

                        NetworkState = NetworkState.CONNECTED;

                        OnClientConnected.Invoke(this, _peerIncoming);
                    }
                }
                else if ((_packet.flags & PacketFlags.Acknowledge) != 0)
                {
                    if (IsServer)
                    {
                        OnListenClientAccepted?.Invoke(this, _peerIncoming);

                        _peerIncoming.state = NetworkState.CONNECTED;
                    }
                }

                return;
            }
            else if (_eventCRC32 == SyncTickPacketCRC32)
            {
                if (IsClient)
                {
                    Tick = _packet.ReadULong();
                }
                return;
            }

            // If we have connections, then we want to send out the remote endpoint and report about the clients we know about.
            // Not ones we don't know about.
            if (_peerIncoming == null)
            {
                return;
            }

            if ((_packet.flags & (PacketFlags.Reliable | PacketFlags.Acknowledge)) != 0)
            {
                Debug.Log($"[{ NetworkPlatform }] Expected : {_peerIncoming.nextAcknowledgementNumber} | Received : {_packet.ACK}");
            }

            // Verify User-Packet is valid.
            if (!networkEvents.ContainsKey(_eventCRC32))
            {
                Debug.LogError($"[{ NetworkPlatform }] A event was attempted over the network. Possible Client/Server mismatch: " + Crc32.GetCRCToString(_eventCRC32));
                return;
            }
            networkEvents[_eventCRC32]?.Invoke(this, _peerIncoming, _packet);
        }

        public void SetMaxNetworkedClients(uint _newLimitOfClients)
        {
            MaxNetworkedClients = _newLimitOfClients;
        }
        public void SetMaxNetworkedQueuedClients(uint _newLimitOfClients)
        {
            MaxNetworkedQueuedClients = _newLimitOfClients;
        }

        public void Listen(string _address = "0.0.0.0", ushort _port = DEFAULT_PORT)
        {
            if (socket != null)
            {
                throw new Exception("Attempting to instantiate a new Socket while a socket already exists!");
            }

            shuttingDown = false;
            NetworkPlatform = NetworkPlatform.SERVER;

            IPEndPoint localEndpoint = new IPEndPoint(_address.Equals("0.0.0.0") ? IPAddress.Any : IPAddress.Parse(_address), _port);

            // Create our Socket to handle the UDP Protocol.
            socket = new UdpClient(localEndpoint);

            // Servers are connectionless, thus we have to handle packets in a special way.
            Recv(new SocketState()
            {
                workSocket = socket,
            });
            OnListenServerInitialized?.Invoke(this);
        }

        public void ConnectClient(string _address, ushort _port = DEFAULT_PORT)
        {
            if (socket != null)
            {
                throw new Exception("Attempting to instantiate a new Socket while a socket already exists!");
            }

            shuttingDown = false;
            NetworkPlatform = NetworkPlatform.CLIENT;
            NetworkState = NetworkState.CONNECTING;

            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Parse(_address), _port);

            socket = new UdpClient();
            socket.Connect(remoteEP);

            SocketState socketBuffer = new SocketState()
            {
                workSocket = socket,
            };

            peers.Add(Crc32.hash(remoteEP.ToString()), new Peer(remoteEP, this));

            Packet initializePacket = new Packet(InitialPacketCRC32, remoteEP, PacketFlags.Reliable);
            initializePacket.Write(VerificationCRC32);
            Send(initializePacket);

            Recv(socketBuffer);
        }

        private void Update()
        {
            // Nothing is happening right now.
            if (NetworkPlatform == NetworkPlatform.UNINITIALIZED)
                return; 

            DateTime now = DateTime.Now;
            deltaTime = (now.Ticks - lastDeltaTime.Ticks) / (float)TimeSpan.TicksPerSecond;

            float dt = deltaTime;
            timer += dt;

            var minTickRate = 1f / TICK_RATE;
            while (timer >= minTickRate)
            {
                timer -= minTickRate;

                if (IsServer)
                {
                    foreach (var pair in peers.ToArray())
                    {
                        Peer peer = pair.Value;
                        peer.SendBulkedPackets();
                        peer.UpdatePing();
                        Debug.Log("[SERVER] Ping:" + peer.Ping);
                        if (peer.Ping > TIMEOUTMS)
                        {
                            DisconnectClient(peer.remoteEndPoint, "Timed out.");
                        }
                    }
                }
                else if (IsClient)
                {
                    if (peers.Count != 1)
                        return;

                    Peer peer = peers.First().Value;
                    peer.SendBulkedPackets();
                    peer.UpdatePing();
                    if (ClientPing > TIMEOUTMS)
                    {
                        Shutdown();
                    }
                }

                Tick++;
            }

            // End of Ticks of frame, we want to send a new update to all clients what the tick is.
            if (IsServer)
            {
                foreach (var peer in peers.ToArray())
                {
                    Packet syncTick = new Packet(SyncTickPacketCRC32, peer.Value.remoteEndPoint);
                    syncTick.Write(Tick);
                    Send(syncTick, peer.Value);
                }
            }

            lastDeltaTime = now;
        }

        public void DisconnectClient(IPEndPoint _remoteEndpoint, string _reason)
        {
            uint remoteEndpointCRC32 = Crc32.hash(_remoteEndpoint.ToString());

            if (peers.ContainsKey(remoteEndpointCRC32))
            {
                Peer peer = peers[remoteEndpointCRC32];
                OnListenClientDropped?.Invoke(this, peer, _reason);
                peers.Remove(remoteEndpointCRC32);
            }
        }

        public void Shutdown()
        {
            if (shuttingDown)
                return;

            OnShutdown?.Invoke(this);
            NetworkState = NetworkState.DISCONNECTING;
            shuttingDown = true;
            peers.Clear();
        }

        private void Recv(SocketState _socketBuffer)
        {
            _socketBuffer.workSocket.BeginReceive(RecvCallback, _socketBuffer);
        }
        private void RecvCallback(IAsyncResult ar)
        {
            if (shuttingDown)
            {
                NetworkPlatform = NetworkPlatform.UNINITIALIZED;
                NetworkState = NetworkState.UNINITIALIZED;
                socket.Close();
                socket = null;
                return;
            }

            try
            {
                SocketState socketBuffer = (SocketState)ar.AsyncState;
                UdpClient receiver = socketBuffer.workSocket;
                IPEndPoint remoteEP = null;

                // Retrieve data from the IOCP
                byte[] data = receiver.EndReceive(ar, ref remoteEP);
                // Continue Listening.
                Recv(socketBuffer);

                uint remoteEPCRC32 = Crc32.hash(remoteEP.ToString());

                // Grab the peer in our database.
                Peer peer = null;
                if (IsServer)
                {
                    if (peers.ContainsKey(remoteEPCRC32))
                    {
                        peer = peers[remoteEPCRC32];
                    }
                }
                else if (IsClient)
                {
                    peer = peers.First().Value;
                }

                // Update the ping value, since we keep track of the last packet was received.
                if (peer != null)
                {
                    peer.MarkPing();
                }

                // Handle Keep Alive.
                // Keep Alive works in the method of, if we are no-longer sending packets at this very moment.
                // We need to notify the other side that we are still alive!
                // But otherwise, if we are sending packets then we are not going to send a keep-alive.
                // Sending packets all the time/keep-alive packets is expensive on the CPU; since the CPU needs to communicate down the bus to your NetIOCP.
                // ---
                // This happens to be triggered in two places. Update function, if the peer may timeout soon. Otherwise it will trigger here to send a new ping,
                // if it received a ping/pong.
                if (data.Length == sizeof(byte))
                {
                    // 0000 1111
                    if (IsServer && (data[0] & 15) == 0)
                        return;
                    // 1111 0000
                    else if (IsClient && (data[0] & 240) == 0)
                        return;

                    if (peer != null)
                    {
                        peer.UpdatePing();
                    }
                }

                if (data.Length < sizeof(int))
                {
                    return;
                }

                // Destruct the packet
                int packetFullSize = 0;
                Packet packet = new Packet(data, ref packetFullSize, remoteEP);

                Packet[] packets = null;
                if ((packet.flags & PacketFlags.Bulk) != 0)
                {
                    PacketBulk packetBulk = new PacketBulk(packet.GetBuffer(), remoteEP);
                    packets = packetBulk.GetPackets();
                }
                else
                {
                    packets = new Packet[1] { packet };
                }

                foreach (Packet packetSegment in packets)
                {
                    // Acknowledge packet that was reliable.
                    if ((packetSegment.flags & PacketFlags.Reliable) != 0)
                    {
                        /*
                        if (peer != null)
                        {
                            peer.RemoveTrackedPacket(packetSegment);
                        }
                        */

                        Packet replyPacket = new Packet(packetSegment.hashedEventName, remoteEP, PacketFlags.Acknowledge);
                        Send(replyPacket);
                    }

                    // Invoke any events due to the packet.
                    InvokeEvent(packetSegment.hashedEventName, receiver, packetSegment, remoteEPCRC32, peer);
                }

            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.ConnectionReset)
                    return;
                Debug.LogError($"[{ NetworkPlatform }] {ex.SocketErrorCode} {ex}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{ NetworkPlatform }] {ex}");
            }
        }

        public void Send(Packet _packet, Peer _peer = null)
        {
            /*
            Packet Architecture:
                Packet Bulk:
                    enum-byte flags : 8-bits (1 bytes)
                    ---
                    uint size : 32-bits (4 bytes)
                    ---
                    Packet Body : n*times
                        uint size       : 32-bits (4 bytes)
                        ---
                        uint eventName  : 32-bits (4 bytes)
                        --- 13 bytes ---
                        byte[] data     : 8-bits (1 byte) * size
            */

            //Debug.Log($"[{NetworkPlatform}] Sending Packet {Crc32.GetCRCToString(_packet.hashedEventName)} {_packet.flags}");
            uint remoteEPCRC32 = Crc32.hash(_packet.remoteEndPoint.ToString());

            if (_peer == null)
            {
                if (peers.ContainsKey(remoteEPCRC32))
                {
                    _peer = peers[remoteEPCRC32];
                }
            }

            if ((_packet.flags & PacketFlags.Reliable) != 0)
            {
                if (_peer != null)
                {
                    // Reliable Packets must be package in a bulk, calling the Update function will send the bulk all together.
                    _peer.AddSendPacket(_packet);
                    //_peer.AddTrackedPacket(_packet);
                }
            }
            else
            {
                // Unreliable Packets get sent immediately.
                // Potential Issues:
                // Out of Order packets, which this should general be okay. If don't care about the information lands at all; then we eventually get something right.
                // Loss of Packets, information that doesn't get transferred over may become a concern. Though this general should be okay.

                byte[] packedData = _packet.Pack();
                SendState sendState = new SendState()
                {
                    workSocket = socket,
                    state = NetworkPlatform
                };
                  
                SendImmediate(socket, packedData, _packet.remoteEndPoint, sendState);
            }

        }
        internal void SendImmediate(byte[] _rawData, IPEndPoint _remoteEndpoint = null)
        {
            SendState sendState = new SendState()
            {
                workSocket = socket,
                state = NetworkPlatform
            };

            SendImmediate(socket, _rawData, _remoteEndpoint, sendState);
        }
        public void SendImmediate(UdpClient _sender, byte[] _data, IPEndPoint _remoteEndpoint = null, object _contextData = null)
        {
            if (_sender == null)
                return;

            if (IsServer)
            {
                if (_remoteEndpoint == null) // Broadcast
                {
                    foreach(var peer in peers)
                    {
                        _sender.BeginSend(_data, _data.Length, peer.Value.remoteEndPoint, new AsyncCallback(SendCallback), _contextData);
                    }
                }
                else // Send To Target
                {
                    _sender.BeginSend(_data, _data.Length, _remoteEndpoint, new AsyncCallback(SendCallback), _contextData);
                }
            }
            else if (IsClient)
            {
                _sender.BeginSend(_data, _data.Length, new AsyncCallback(SendCallback), _contextData);
            }
        }

        private static void SendCallback(IAsyncResult ar)
        {
            SendState handler = (SendState)ar.AsyncState;
            try
            {
                UdpClient socket = handler.workSocket;

                Assert.IsNotNull(socket, "Send Callback had a nulled Socket.");

                int bytesSent = socket.EndSend(ar);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{ handler.state }] { ex }");
            }
        }
    }
}
