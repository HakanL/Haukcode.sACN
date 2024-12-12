﻿using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Haukcode.HighPerfComm;
using Haukcode.sACN.Model;

namespace Haukcode.sACN
{
    public class SACNClient : Client<SACNClient.SendData, ReceiveDataPacket>
    {
        public class SendData : HighPerfComm.SendData
        {
            public ushort UniverseId { get; set; }

            public IPEndPoint Destination { get; set; }

            public SendData(ushort universeId, IPEndPoint destination)
            {
                UniverseId = universeId;
                Destination = destination;
            }
        }

        public const int DefaultPort = 5568;
        public const int ReceiveBufferSize = 680 * 20 * 200;
        private const int SendBufferSize = 680 * 20 * 200;
        private static readonly IPEndPoint _blankEndpoint = new(IPAddress.Any, 0);

        private Socket? listenSocket;
        private readonly Socket sendSocket;
        private readonly IPEndPoint localEndPoint;
        private readonly ISubject<ReceiveDataPacket> packetSubject;
        private readonly Dictionary<ushort, byte> sequenceIds = [];
        private readonly Dictionary<ushort, byte> sequenceIdsSync = [];
        private readonly Lock lockObject = new();
        private readonly HashSet<ushort> dmxUniverses = [];
        private readonly Dictionary<IPAddress, (IPEndPoint EndPoint, bool Multicast)> endPointCache = [];
        private readonly Dictionary<ushort, IPEndPoint> universeMulticastEndpoints = [];

        public SACNClient(Guid senderId, string senderName, IPAddress localAddress, int port = DefaultPort)
            : base(SACNPacket.MAX_PACKET_SIZE)
        {
            if (senderId == Guid.Empty)
                throw new ArgumentException("Invalid sender Id", nameof(senderId));
            SenderId = senderId;

            SenderName = senderName;

            if (port <= 0)
                throw new ArgumentException("Invalid port", nameof(port));
            this.localEndPoint = new IPEndPoint(localAddress, port);

            this.packetSubject = new Subject<ReceiveDataPacket>();

            this.sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            ConfigureSendSocket(this.sendSocket);
        }

        private void ConfigureSendSocket(Socket socket)
        {
            socket.SendBufferSize = SendBufferSize;

            Haukcode.Network.Utils.SetSocketOptions(socket);

            // Multicast socket settings
            socket.DontFragment = true;
            socket.MulticastLoopback = false;

            // Only local LAN group
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 20);
        }

        public IPEndPoint LocalEndPoint => this.localEndPoint;

        public Guid SenderId { get; }

        public string SenderName { get; }

        /// <summary>
        /// Observable that provides all parsed packets. This is buffered on its own thread so the processing can
        /// take any time necessary (memory consumption will go up though, there is no upper limit to amount of data buffered).
        /// </summary>
        public IObservable<ReceiveDataPacket> OnPacket => this.packetSubject.AsObservable();

        /// <summary>
        /// Gets a list of dmx universes this socket has joined to
        /// </summary>
        public IReadOnlyCollection<ushort> DMXUniverses => this.dmxUniverses.ToList();

        public void JoinDMXUniverse(ushort universeId)
        {
            if (this.listenSocket == null)
                throw new ArgumentNullException();

            if (this.dmxUniverses.Contains(universeId))
                throw new InvalidOperationException($"You have already joined the DMX Universe {universeId}");

            // Join group
            var option = new MulticastOption(Haukcode.Network.Utils.GetMulticastAddress(universeId), this.localEndPoint.Address);
            this.listenSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);

            // Add to the list of universes we have joined
            this.dmxUniverses.Add(universeId);
        }

        public void DropDMXUniverse(ushort universeId)
        {
            if (this.listenSocket == null)
                return;

            if (!this.dmxUniverses.Contains(universeId))
                throw new InvalidOperationException($"You are trying to drop the DMX Universe {universeId} but you are not a member");

            // Drop group
            var option = new MulticastOption(Haukcode.Network.Utils.GetMulticastAddress(universeId), this.localEndPoint.Address);
            this.listenSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, option);

            // Remove from the list of universes we have joined
            this.dmxUniverses.Remove(universeId);
        }

        /// <summary>
        /// Send data
        /// </summary>
        /// <param name="address">The optional address to unicast to</param>
        /// <param name="universeId">The Universe ID</param>
        /// <param name="dmxData">Up to 512 bytes of DMX data</param>
        /// <param name="syncAddress">Sync universe id</param>
        /// <param name="startCode">Start code (default 0)</param>
        /// <param name="important">Important</param>
        /// <param name="priority">Priority (default 100)</param>
        /// <param name="terminate">Terminate</param>
        public Task SendDmxData(
            IPAddress? address,
            ushort universeId,
            ReadOnlyMemory<byte> dmxData,
            byte priority = 100,
            ushort syncAddress = 0,
            byte startCode = 0,
            bool important = false,
            bool terminate = false)
        {
            if (!IsOperational)
                return Task.CompletedTask;

            byte sequenceId = GetNewSequenceId(universeId);

            var packet = new SACNDataPacket(universeId, SenderName, SenderId, sequenceId, dmxData, priority, syncAddress, startCode);

            if (terminate)
            {
                packet.DataFramingLayer.Options.StreamTerminated = true;
            }

            if (syncAddress == 0)
            {
                packet.DataFramingLayer.Options.ForceSynchronization = true;
            }

            return QueuePacket(universeId, address, packet, important);
        }

        /// <summary>
        /// Send sync
        /// </summary>
        /// <param name="address">The optional address to unicast to</param>
        /// <param name="syncAddress">Sync universe id</param>
        public Task SendSync(IPAddress? address, ushort syncAddress)
        {
            if (!IsOperational)
                return Task.CompletedTask;

            byte sequenceId = GetNewSequenceIdSync(syncAddress);

            var packet = new SACNPacket(new RootLayer
            {
                UUID = SenderId,
                FramingLayer = new SyncFramingLayer
                {
                    SequenceId = sequenceId,
                    SyncAddress = syncAddress
                }
            });

            return QueuePacket(syncAddress, address, packet, true);
        }

        /// <summary>
        /// Send packet
        /// </summary>
        /// <param name="universeId">Universe Id</param>
        /// <param name="destination">Destination</param>
        /// <param name="packet">Packet</param>
        /// <param name="important">Important</param>
        private async Task QueuePacket(ushort universeId, IPAddress? destination, SACNPacket packet, bool important)
        {
            await base.QueuePacket(packet.Length, important, () =>
            {
                IPEndPoint? sendDataDestination = null;

                if (destination != null)
                {
                    // Specified destination (but could be multicast, so check for that)
                    if (!this.endPointCache.TryGetValue(destination, out var ipEndPointDetails))
                    {
                        ipEndPointDetails = (new IPEndPoint(destination, this.localEndPoint.Port), Haukcode.Network.Utils.IsMulticast(destination));
                        this.endPointCache.Add(destination, ipEndPointDetails);
                    }

                    sendDataDestination = ipEndPointDetails.Multicast ? null : ipEndPointDetails.EndPoint;
                }

                if (sendDataDestination == null)
                {
                    // Set the destination to the multicast address
                    if (!universeMulticastEndpoints.TryGetValue(universeId, out sendDataDestination))
                    {
                        sendDataDestination = new IPEndPoint(Haukcode.Network.Utils.GetMulticastAddress(universeId), this.localEndPoint.Port);
                        universeMulticastEndpoints.Add(universeId, sendDataDestination);
                    }
                }

                return new SendData(universeId, sendDataDestination);
            },
            packet.WriteToBuffer);
        }

        public void WarmUpSockets(IEnumerable<ushort> universeIds)
        {
        }

        private byte GetNewSequenceId(ushort universeId)
        {
            lock (this.lockObject)
            {
                this.sequenceIds.TryGetValue(universeId, out byte sequenceId);

                sequenceId++;

                this.sequenceIds[universeId] = sequenceId;

                return sequenceId;
            }
        }

        private byte GetNewSequenceIdSync(ushort syncAddress)
        {
            lock (this.lockObject)
            {
                this.sequenceIdsSync.TryGetValue(syncAddress, out byte sequenceId);

                sequenceId++;

                this.sequenceIdsSync[syncAddress] = sequenceId;

                return sequenceId;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                try
                {
                    this.sendSocket.Shutdown(SocketShutdown.Both);
                }
                catch
                {
                }
                this.sendSocket.Close();
                this.sendSocket.Dispose();
            }
        }

        protected override ValueTask<int> SendPacketAsync(SendData sendData, ReadOnlyMemory<byte> payload)
        {
            return this.sendSocket.SendToAsync(payload, SocketFlags.None, sendData.Destination!);
        }

        protected async override ValueTask<(int ReceivedBytes, SocketReceiveMessageFromResult Result)> ReceiveData(Memory<byte> memory, CancellationToken cancelToken)
        {
            var result = await this.listenSocket!.ReceiveMessageFromAsync(memory, SocketFlags.None, _blankEndpoint, cancelToken);

            return (result.ReceivedBytes, result);
        }

        protected override ReceiveDataPacket? TryParseObject(ReadOnlyMemory<byte> buffer, double timestampMS, IPEndPoint sourceIP, IPAddress destinationIP)
        {
            var packet = SACNPacket.Parse(buffer);

            // Note that we're still using the memory from the pipeline here, the packet is not allocating its own DMX data byte array
            if (packet != null)
            {
                var parsedObject = new ReceiveDataPacket
                {
                    TimestampMS = timestampMS,
                    Source = sourceIP,
                    Packet = packet
                };

                if (!this.endPointCache.TryGetValue(destinationIP, out var ipEndPoint))
                {
                    ipEndPoint = (new IPEndPoint(destinationIP, this.localEndPoint.Port), Haukcode.Network.Utils.IsMulticast(destinationIP));
                    this.endPointCache.Add(destinationIP, ipEndPoint);
                }

                parsedObject.Destination = ipEndPoint.Multicast ? null : ipEndPoint.EndPoint;

                return parsedObject;
            }

            return null;
        }

        public int? ActualReceiveBufferSize
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    // Linux reports the internal buffer size, which is double the requested size
                    return this.listenSocket?.ReceiveBufferSize / 2;
                else
                    return this.listenSocket?.ReceiveBufferSize;
            }
        }

        protected override void InitializeReceiveSocket()
        {
            this.listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            this.listenSocket.ReceiveBufferSize = ReceiveBufferSize;

            Haukcode.Network.Utils.SetSocketOptions(this.listenSocket);

            // Linux wants IPAddress.Any to get all types of packets (unicast/multicast/broadcast)
            this.listenSocket.Bind(new IPEndPoint(IPAddress.Any, this.localEndPoint.Port));

            // Only join local LAN group
            this.listenSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        }

        protected override void DisposeReceiveSocket()
        {
            try
            {
                this.listenSocket?.Shutdown(SocketShutdown.Both);
            }
            catch
            {
            }

            this.listenSocket?.Close();
            this.listenSocket?.Dispose();
            this.listenSocket = null;
        }
    }
}
