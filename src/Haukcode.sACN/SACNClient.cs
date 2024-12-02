﻿using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Haukcode.HighPerfComm;
using Haukcode.sACN.Model;

namespace Haukcode.sACN
{
    public class SACNClient : Client<SACNClient.SendData, SocketReceiveMessageFromResult>
    {
        public class SendData : HighPerfComm.SendData
        {
            public ushort UniverseId { get; set; }

            public IPEndPoint? Destination { get; set; }
        }

        private const int ReceiveBufferSize = 20480;
        private const int SendBufferSize = 680 * 100;
        private static readonly IPEndPoint _blankEndpoint = new(IPAddress.Any, 0);

        private readonly Socket listenSocket;
        private readonly Socket sendSocket;
        private readonly ISubject<ReceiveDataPacket> packetSubject;
        private readonly Dictionary<ushort, byte> sequenceIds = [];
        private readonly Dictionary<ushort, byte> sequenceIdsSync = [];
        private readonly object lockObject = new();
        private readonly HashSet<ushort> dmxUniverses = [];
        private readonly Dictionary<IPAddress, IPEndPoint> endPointCache = [];
        private readonly IPEndPoint localEndPoint;
        private readonly Dictionary<ushort, IPEndPoint> universeMulticastEndpoints = [];

        public SACNClient(Guid senderId, string senderName, IPAddress localAddress, int port = 5568)
            : base(() => new SendData(), ReceiveBufferSize)
        {
            if (senderId == Guid.Empty)
                throw new ArgumentException("Invalid sender Id", nameof(senderId));
            SenderId = senderId;

            SenderName = senderName;

            if (port <= 0)
                throw new ArgumentException("Invalid port", nameof(port));
            this.localEndPoint = new IPEndPoint(localAddress, port);

            this.packetSubject = new Subject<ReceiveDataPacket>();

            this.listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            this.listenSocket.ReceiveBufferSize = ReceiveBufferSize;

            // Set the SIO_UDP_CONNRESET ioctl to true for this UDP socket. If this UDP socket
            //    ever sends a UDP packet to a remote destination that exists but there is
            //    no socket to receive the packet, an ICMP port unreachable message is returned
            //    to the sender. By default, when this is received the next operation on the
            //    UDP socket that send the packet will receive a SocketException. The native
            //    (Winsock) error that is received is WSAECONNRESET (10054). Since we don't want
            //    to wrap each UDP socket operation in a try/except, we'll disable this error
            //    for the socket with this ioctl call.
            try
            {
                uint IOC_IN = 0x80000000;
                uint IOC_VENDOR = 0x18000000;
                uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;

                byte[] optionInValue = { Convert.ToByte(false) };
                byte[] optionOutValue = new byte[4];
                this.listenSocket.IOControl((int)SIO_UDP_CONNRESET, optionInValue, optionOutValue);
            }
            catch
            {
                Debug.WriteLine("Unable to set SIO_UDP_CONNRESET, maybe not supported.");
            }

            this.listenSocket.ExclusiveAddressUse = false;
            this.listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            this.listenSocket.Bind(new IPEndPoint(IPAddress.Any, port));

            // Only join local LAN group
            this.listenSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

            this.sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            ConfigureSendSocket(this.sendSocket);
        }

        private void ConfigureSendSocket(Socket socket)
        {
            socket.SendBufferSize = SendBufferSize;

            // Set the SIO_UDP_CONNRESET ioctl to true for this UDP socket. If this UDP socket
            //    ever sends a UDP packet to a remote destination that exists but there is
            //    no socket to receive the packet, an ICMP port unreachable message is returned
            //    to the sender. By default, when this is received the next operation on the
            //    UDP socket that send the packet will receive a SocketException. The native
            //    (Winsock) error that is received is WSAECONNRESET (10054). Since we don't want
            //    to wrap each UDP socket operation in a try/except, we'll disable this error
            //    for the socket with this ioctl call.
            try
            {
                uint IOC_IN = 0x80000000;
                uint IOC_VENDOR = 0x18000000;
                uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;

                byte[] optionInValue = { Convert.ToByte(false) };
                byte[] optionOutValue = new byte[4];
                socket.IOControl((int)SIO_UDP_CONNRESET, optionInValue, optionOutValue);
            }
            catch
            {
                Debug.WriteLine("Unable to set SIO_UDP_CONNRESET, maybe not supported.");
            }

            socket.ExclusiveAddressUse = false;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            socket.Bind(this.localEndPoint);

            // Multicast socket settings
            socket.DontFragment = true;
            socket.MulticastLoopback = false;

            // Only join local LAN group
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
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
            if (this.dmxUniverses.Contains(universeId))
                throw new InvalidOperationException($"You have already joined the DMX Universe {universeId}");

            // Join group
            var option = new MulticastOption(SACNCommon.GetMulticastAddress(universeId), this.localEndPoint.Address);
            this.listenSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);

            // Add to the list of universes we have joined
            this.dmxUniverses.Add(universeId);
        }

        public void DropDMXUniverse(ushort universeId)
        {
            if (!this.dmxUniverses.Contains(universeId))
                throw new InvalidOperationException($"You are trying to drop the DMX Universe {universeId} but you are not a member");

            // Drop group
            var option = new MulticastOption(SACNCommon.GetMulticastAddress(universeId), this.localEndPoint.Address);
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
        public Task SendDmxData(IPAddress? address, ushort universeId, ReadOnlyMemory<byte> dmxData, byte priority = 100, ushort syncAddress = 0, byte startCode = 0, bool important = false)
        {
            if (!IsOperational)
                return Task.CompletedTask;

            byte sequenceId = GetNewSequenceId(universeId);

            var packet = new SACNDataPacket(universeId, SenderName, SenderId, sequenceId, dmxData, priority, syncAddress, startCode);

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
            await base.QueuePacket(packet.Length, important, (newSendData, memory) =>
            {
                if (destination != null)
                {
                    if (!this.endPointCache.TryGetValue(destination, out var ipEndPoint))
                    {
                        ipEndPoint = new IPEndPoint(destination, this.localEndPoint.Port);
                        this.endPointCache.Add(destination, ipEndPoint);
                    }

                    newSendData.Destination = ipEndPoint;
                }

                newSendData.UniverseId = universeId;

                return packet.WriteToBuffer(memory);
            });
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
                    this.listenSocket.Shutdown(SocketShutdown.Both);
                }
                catch
                {
                }
                this.listenSocket.Close();
                this.listenSocket.Dispose();

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
            var destination = sendData.Destination;

            if (destination == null)
            {
                if (!universeMulticastEndpoints.TryGetValue(sendData.UniverseId, out destination))
                {
                    destination = new IPEndPoint(SACNCommon.GetMulticastAddress(sendData.UniverseId), this.localEndPoint.Port);
                    universeMulticastEndpoints.Add(sendData.UniverseId, destination);
                }
            }

            return this.sendSocket.SendToAsync(payload, SocketFlags.None, destination);
        }

        protected async override ValueTask<(int ReceivedBytes, SocketReceiveMessageFromResult Result)> ReceiveData(Memory<byte> memory, CancellationToken cancelToken)
        {
            var result = await this.listenSocket.ReceiveMessageFromAsync(memory, SocketFlags.None, _blankEndpoint, cancelToken);

            return (result.ReceivedBytes, result);
        }

        protected override void ParseReceiveData(ReadOnlyMemory<byte> memory, SocketReceiveMessageFromResult result, double timestampMS)
        {
            var packet = SACNPacket.Parse(memory);

            if (packet != null)
            {
                var newPacket = new ReceiveDataPacket
                {
                    TimestampMS = timestampMS,
                    Source = (IPEndPoint)result.RemoteEndPoint,
                    Packet = packet
                };

                if (!this.endPointCache.TryGetValue(result.PacketInformation.Address, out var ipEndPoint))
                {
                    ipEndPoint = new IPEndPoint(result.PacketInformation.Address, this.localEndPoint.Port);
                    this.endPointCache.Add(result.PacketInformation.Address, ipEndPoint);
                }

                newPacket.Destination = ipEndPoint;

                this.packetSubject.OnNext(newPacket);
            }
        }
    }
}
