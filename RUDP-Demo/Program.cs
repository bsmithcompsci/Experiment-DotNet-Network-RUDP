using NetworkLibrary;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace RUDP_Demo
{
    internal class Program
    {

        const uint TEST_CLIENTS = 1;

        static NetworkManager ServerManager;
        static List<NetworkManager> ClientManagers = new List<NetworkManager>();

        static long worstPing = 0;
        static void Main(string[] args)
        {
            Debug.Log("Network Experiment Reliable UDP:");
            Debug.Log("-------------------------------------------");

            ServerManager = new NetworkManager();
            ServerManager.OnListenServerInitialized += ServerManager_OnListenServerInitialized;
            ServerManager.OnListenClientAccepted += ServerManager_OnClientConnected;
            ServerManager.OnListenClientAccepting += ServerManager_OnListenClientAccepting;
            ServerManager.OnAnyPacketEvent += Program_OnAnyPacketEvent;
            ServerManager.OnListenClientDropped += ServerManager_OnClientDisconnected;
            ServerManager.Listen("127.0.0.1");

            for (int i = 0; i < TEST_CLIENTS; i++)
            {
                ClientManagers.Add(new NetworkManager());
                ClientManagers[i].OnClientConnected += Program_OnClientConnected;
                ClientManagers[i].OnShutdown += Program_OnClientDisconnected;
                ClientManagers[i].OnAnyPacketEvent += Program_OnAnyPacketEvent;
                ClientManagers[i].ConnectClient("127.0.0.1");
            }

            Thread thread = new Thread(new ThreadStart(() =>
            {
                while (true)
                {
                    NetworkManager firstManager = ClientManagers[0];
                    if (firstManager.ClientPing != -1)
                    {
                        if (worstPing < firstManager.ClientPing)
                            worstPing = firstManager.ClientPing;

                        //if (firstManager.ClientPing <= 400)
                            //Debug.Log($"Client Ping: {firstManager.ClientPing} | {worstPing}");
                    }
                }
            }));
            thread.Name = "UnityEngine Pretend Thread";
            thread.Start();

            thread.Join();
        }

        private static void Program_OnAnyPacketEvent(NetworkManager _manager, Peer _peer, Packet _packet)
        {
            if ((_packet.flags & (PacketFlags.Reliable)) != 0)
            {
                Debug.Log($"[{_manager.NetworkPlatform}] Reliable Recv Packet : {_packet.Size()} bytes / {_packet.ACK} bytes");
            }
        }

        private static bool ServerManager_OnListenClientAccepting(NetworkManager _manager, uint _remoteEndpointCRC32)
        {
            // This could be used for banned connects, to ignore them.
            Debug.Log($"[Server] Client Requested Connection UID: " + Crc32.GetCRCToString(_remoteEndpointCRC32));
            return true;
        }

        private static void ServerManager_OnClientDisconnected(NetworkManager _manager, Peer _peer, string _reason)
        {
            Debug.Log("[Server] Client Disconnected : " + _peer.remoteEndPoint.ToString() + $" (\"{_reason}\")");
        }

        private static void ServerManager_OnClientConnected(NetworkManager _manager, Peer _peer)
        {

            Debug.Log("[Server] Client Connected : " + _peer.remoteEndPoint.ToString() + $" [{_peer.Ping}ms]");
        }

        private static void ServerManager_OnListenServerInitialized(NetworkManager _manager)
        {
            Debug.Log("[Server] Initialized...");
        }

        private static void Program_OnClientDisconnected(NetworkManager _manager)
        {
            Debug.Log("[Client] Client Disconnected...");
        }

        private static void Program_OnClientConnected(NetworkManager _manager, Peer _serverPeer)
        {
            Debug.Log($"[Client] Client Received and Set Connection UID: {_manager.ConnectionUID}");
            Debug.Log("[Client] Client Connected to : " + _serverPeer.remoteEndPoint.ToString() + $" [{_serverPeer.Ping}ms]");
        }
    }
}
