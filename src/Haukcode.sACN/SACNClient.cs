using System;
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
using System.Threading.Channels;
using System.Threading.Tasks;
using Haukcode.HighPerfComm;
using Haukcode.sACN.Model;

namespace Haukcode.sACN;

public class SACNClient : Client<SACNClient.SendData, ReceiveDataPacket>
{
    public class SendData : HighPerfComm.SendData
    {
        public IPEndPoint Destination { get; set; }

        /// <summary>
        /// The destination pre-serialized once. Socket.SendTo(..., EndPoint) re-serializes the
        /// EndPoint into a fresh SocketAddress on every call; handing it an already-serialized
        /// SocketAddress instead is worth ~13% of the send path and removes the last per-packet
        /// allocations. Cached alongside the endpoint, so it costs nothing per packet.
        /// </summary>
        public SocketAddress DestinationAddress { get; set; }

        public SendData(IPEndPoint destination)
        {
            Destination = destination;
            DestinationAddress = destination.Serialize();
        }
    }

    // Reused across SendDmxData calls instead of allocating a fresh SACNDataPacket (+ its
    // RootLayer/DataFramingLayer/DMPLayer) per universe per tick. Reconfigured in place by
    // Update() and serialized synchronously inside QueuePacket before the next call, so a
    // single instance is safe on the single-threaded send path (the send queue is SingleWriter).
    private readonly SACNDataPacket scratchDataPacket;

    public const int DefaultPort = 5568;
    public const int ReceiveBufferSize = 680 * 20 * 200;
    public static readonly IPAddress UniverseDiscoveryMulticastAddress = IPAddress.Parse("239.255.250.133");
    private const int SendBufferSize = 680 * 20 * 200;
    private static readonly IPEndPoint _blankEndpoint = new(IPAddress.Any, 0);

    private Socket? listenSocket;

    // Linux only: holds the IGMP memberships so the listen socket's kernel membership list
    // stays empty on the per-packet delivery path (see InitializeReceiveSocket). Null on
    // other platforms, where memberships live on the listen socket itself.
    private Socket? membershipSocket;

    private Socket MembershipSocket => this.membershipSocket ?? this.listenSocket!;

    // Kernel receive timestamping (Linux): packets are stamped on arrival in the network
    // stack instead of when user space reads them, so socket-buffer waits (GC pauses, busy
    // receive loop) no longer distort recorded timing. Null = portable path with user-space
    // timestamps.
    private LinuxReceiveTimestamping? timestampedReceiver;

    /// <summary>
    /// True when packets are being stamped by the kernel on arrival (Linux, SO_TIMESTAMPNS)
    /// rather than by user space when the receive loop reads them.
    /// </summary>
    public bool KernelReceiveTimestampsActive => this.timestampedReceiver != null;

    // One send socket per sender shard. Several threads sharing one UDP socket serialize on the
    // kernel's socket lock, which throws away most of the gain from sharding; a socket each does
    // not. Receivers don't care which source port a frame came from, and each socket binds to an
    // ephemeral port anyway.
    private readonly Socket[] sendSockets;

    private readonly IPEndPoint localEndPoint;
    // Per-universe sACN sequence counters, indexed by universe id (and by sync address for the sync
    // counter). Written only from the single send/scheduler thread on the hot path — one increment
    // per universe per packet, ~36,000/s at 600 universes @ 60 Hz — so a plain array is used instead
    // of a locked dictionary. The lock + dictionary lookup was a measured hot spot (~13% of the
    // scheduler thread at that load). A receiver tolerates an occasional sequence discontinuity, so
    // even a benign race here would be harmless.
    private readonly byte[] sequenceIds = new byte[ushort.MaxValue + 1];
    private readonly byte[] sequenceIdsSync = new byte[ushort.MaxValue + 1];
    private readonly HashSet<ushort> dmxUniverses = [];
    private readonly HashSet<ushort> triggerUniverses = [];
    private readonly Dictionary<IPAddress, (IPEndPoint EndPoint, bool Multicast)> endPointCache = [];
    private readonly Dictionary<ushort, IPEndPoint> universeMulticastEndpoints = [];

    // Serialized destinations, so the hot path never re-serializes an IPEndPoint. Only touched
    // from the single queue-writer thread (the send-data factory), same as the caches above.
    private readonly Dictionary<IPEndPoint, SocketAddress> socketAddressCache = [];

    private bool listenDiscoveryMulticastGroup = false;

    /// <param name="senderCount">
    /// Number of sender threads/sockets. Packets are sharded by universe id, so a universe always
    /// goes out on one thread and one socket and its sequence numbers stay ordered. Default 1 =
    /// the original behavior.
    /// </param>
    public SACNClient(
        Guid senderId,
        string senderName,
        IPAddress localAddress,
        Func<ReceiveDataPacket, Task>? channelWriter = null,
        Action? channelWriterComplete = null,
        int port = DefaultPort,
        int senderCount = 1)
        : base(SACNPacket.MAX_PACKET_SIZE, channelWriter, channelWriterComplete, senderCount)
    {
        if (senderId == Guid.Empty)
            throw new ArgumentException("Invalid sender Id", nameof(senderId));
        SenderId = senderId;
        SenderName = senderName;

        this.scratchDataPacket = new SACNDataPacket(1, senderName, senderId, 0, ReadOnlyMemory<byte>.Empty, 100);
        this.scratchPacketWriter = this.scratchDataPacket.WriteToBuffer;
        this.pendingSendDataFactory = BuildPendingSendData;

        if (port <= 0)
            throw new ArgumentException("Invalid port", nameof(port));

        this.localEndPoint = new IPEndPoint(localAddress, port);

        this.sendSockets = new Socket[SenderCount];
        for (int i = 0; i < SenderCount; i++)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SendBufferSize = SendBufferSize;

            Haukcode.Network.Utils.SetSocketOptions(socket);

            // Multicast socket settings
            socket.DontFragment = true;
            socket.MulticastLoopback = false;

            // Bind to the local interface (ephemeral port)
            socket.Bind(new IPEndPoint(localAddress, 0));

            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 20);

            this.sendSockets[i] = socket;
        }

        StartReceive();
    }

    public IPEndPoint LocalEndPoint => this.localEndPoint;

    public Guid SenderId { get; }

    public string SenderName { get; }

    public void JoinDMXUniverse(ushort universeId)
    {
        if (this.listenSocket == null)
            throw new ArgumentNullException();

        if (this.dmxUniverses.Contains(universeId))
            throw new InvalidOperationException($"You have already joined the DMX Universe {universeId}");

        // See if we already have a listener for this universe for triggers
        if (!this.triggerUniverses.Contains(universeId))
        {
            // Join group
            var option = new MulticastOption(Haukcode.Network.Utils.GetMulticastAddress(universeId), this.localEndPoint.Address);
            MembershipSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
        }

        // Add to the list of universes we have joined
        this.dmxUniverses.Add(universeId);
    }

    public void DropDMXUniverse(ushort universeId)
    {
        if (this.listenSocket == null)
            return;

        if (!this.dmxUniverses.Contains(universeId))
            throw new InvalidOperationException($"You are trying to drop the DMX Universe {universeId} but you are not a member");

        if (!this.triggerUniverses.Contains(universeId))
        {
            // Drop group
            var option = new MulticastOption(Haukcode.Network.Utils.GetMulticastAddress(universeId), this.localEndPoint.Address);
            MembershipSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, option);
        }

        // Remove from the list of universes we have joined
        this.dmxUniverses.Remove(universeId);
    }

    public void DropAllTriggerUniverses()
    {
        foreach (ushort universeId in this.triggerUniverses)
        {
            DropDMXUniverseForTrigger(universeId);
        }
    }

    public void DropAllInputUniverses()
    {
        foreach (ushort universeId in this.dmxUniverses)
        {
            DropDMXUniverse(universeId);
        }
    }

    public void JoinDMXUniverseForTrigger(ushort universeId)
    {
        if (this.listenSocket == null)
            throw new ArgumentNullException();

        if (this.triggerUniverses.Contains(universeId))
            // Already joined
            return;

        if (!this.dmxUniverses.Contains(universeId))
        {
            // Join group
            var option = new MulticastOption(Haukcode.Network.Utils.GetMulticastAddress(universeId), this.localEndPoint.Address);
            MembershipSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
        }

        this.triggerUniverses.Add(universeId);
    }

    public void DropDMXUniverseForTrigger(ushort universeId)
    {
        if (this.listenSocket == null)
            return;

        if (!this.triggerUniverses.Contains(universeId))
            // Already dropped
            return;

        if (!this.dmxUniverses.Contains(universeId))
        {
            // Drop group
            var option = new MulticastOption(Haukcode.Network.Utils.GetMulticastAddress(universeId), this.localEndPoint.Address);
            MembershipSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, option);
        }

        // Remove from the list of universes we have joined
        this.triggerUniverses.Remove(universeId);
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

        // Reconfigure the reused scratch packet in place instead of allocating a new one
        // (and its three nested layers) for every packet on the hot send path.
        this.scratchDataPacket.Update(universeId, sequenceId, dmxData, priority, syncAddress, startCode, terminate);

        return QueuePacketForSending(universeId, address, this.scratchDataPacket, important);
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

        return QueueSyncPacketForSending(syncAddress, address, packet);
    }

    /// <summary>
    /// Send universe discovery (E1.31 extended discovery packet)
    /// </summary>
    /// <param name="address">Optional unicast destination. If null, uses the universe discovery multicast address.</param>
    /// <param name="universes">Universe IDs to advertise.</param>
    /// <param name="page">Page number.</param>
    /// <param name="lastPage">Last page number.</param>
    /// <param name="important">Important</param>
    public Task SendUniverseDiscovery(IPAddress? address, IEnumerable<ushort> universes, byte page = 0, byte lastPage = 0, bool important = true)
    {
        if (!IsOperational)
            return Task.CompletedTask;

        var packet = new SACNUniverseDiscoveryPacket(SenderId, SenderName, universes, page, lastPage);

        var destination = new IPEndPoint(address ?? UniverseDiscoveryMulticastAddress, this.localEndPoint.Port);

        return SendPacketImmediately(destination, packet, important);
    }

    /// <summary>
    /// Send packet
    /// </summary>
    /// <param name="universeId">Universe Id</param>
    /// <param name="destination">Destination</param>
    /// <param name="packet">Packet</param>
    /// <param name="important">Important</param>
    // Arguments for the cached send-data factory below. QueuePacket invokes the factory and the
    // packet writer synchronously before its first await, and SendDmxData runs on the single
    // queue-writer thread (the same assumption the non-locked caches here already rest on), so
    // passing the per-packet arguments through fields lets one cached delegate replace a fresh
    // closure per packet. The sync/barrier path keeps its closure: QueueBarrierPacket invokes
    // the factory several times with possible awaits in between, where fields could be
    // overwritten mid-barrier — and sync packets are rare enough not to matter.
    private ushort pendingUniverseId;
    private IPAddress? pendingDestination;
    private readonly Func<SendData> pendingSendDataFactory;
    private readonly Func<Memory<byte>, int> scratchPacketWriter;

    private SendData BuildPendingSendData() => BuildSendData(this.pendingUniverseId, this.pendingDestination);

    private async Task QueuePacketForSending(ushort universeId, IPAddress? destination, SACNPacket packet, bool important)
    {
        this.pendingUniverseId = universeId;
        this.pendingDestination = destination;

        await base.QueuePacket(packet.Length, important, this.pendingSendDataFactory,
            // The DMX hot path always sends the reused scratch packet, so its writer delegate is
            // cached too; anything else (rare) pays the method-group allocation.
            ReferenceEquals(packet, this.scratchDataPacket) ? this.scratchPacketWriter : packet.WriteToBuffer,
            // Shard by universe: every packet for a universe goes out on the same thread and socket,
            // so its sequence numbers stay monotonic on the wire. Applies equally to unicast — the
            // key is the universe, not the destination.
            shardKey: universeId);
    }

    /// <summary>
    /// Queue a sync packet. It must follow every DMX frame it synchronizes, so with more than one
    /// sender shard it goes out as an ordering barrier — otherwise it could be transmitted while a
    /// slower shard still had that frame's DMX pending, silently breaking synchronization.
    /// </summary>
    private async Task QueueSyncPacketForSending(ushort syncAddress, IPAddress? destination, SACNPacket packet)
    {
        await base.QueueBarrierPacket(packet.Length, CreateSendData(syncAddress, destination), packet.WriteToBuffer,
            shardKey: syncAddress);
    }

    /// <summary>
    /// Factory for the send-data object: resolves the destination (multicast group for the universe
    /// unless an explicit unicast address was given) and rents a pooled object where possible.
    /// Runs on the single queue-writer thread, which is what makes the caches below safe.
    /// </summary>
    private Func<SendData> CreateSendData(ushort universeId, IPAddress? destination)
    {
        return () => BuildSendData(universeId, destination);
    }

    private SendData BuildSendData(ushort universeId, IPAddress? destination)
    {
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

            // Reuse a spent send-data object returned by the sender instead of allocating a new
            // one for every packet on the hot path. Every field is rewritten below.
            var pooledSendData = RentSendData();
            if (pooledSendData != null)
            {
                pooledSendData.Destination = sendDataDestination;
                pooledSendData.DestinationAddress = GetSocketAddress(sendDataDestination);

                return pooledSendData;
            }

            // Pool empty (startup only) — the constructor serializes the destination itself.
            return new SendData(sendDataDestination);
        }
    }

    /// <summary>
    /// Serialized form of a destination, cached. Called from the send-data factory on the single
    /// queue-writer thread.
    /// </summary>
    private SocketAddress GetSocketAddress(IPEndPoint endPoint)
    {
        if (!this.socketAddressCache.TryGetValue(endPoint, out var socketAddress))
        {
            socketAddress = endPoint.Serialize();
            this.socketAddressCache.Add(endPoint, socketAddress);
        }

        return socketAddress;
    }

    /// <summary>
    /// Send packet immediately, bypassing the send queue
    /// </summary>
    /// <param name="universeId">Universe Id</param>
    /// <param name="destination">Destination</param>
    /// <param name="packet">Packet</param>
    /// <param name="important">Important</param>
    public Task SendPacketImmediately(ushort universeId, IPAddress? destination, SACNPacket packet, bool important = false)
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

        return SendPacketImmediately(sendDataDestination, packet, important);
    }

    public async Task SendPacketImmediately(IPEndPoint destination, SACNPacket packet, bool important = false)
    {
        await SendImmediateAsync(
            allocatePacketLength: packet.Length,
            important: important,
            sendDataFactory: () => new SendData(destination),
            packetWriter: packet.WriteToBuffer);

    }

    private byte GetNewSequenceId(ushort universeId)
    {
        // Single-writer increment; byte wraps 255 -> 0 exactly as the old counter did.
        return ++this.sequenceIds[universeId];
    }

    private byte GetNewSequenceIdSync(ushort syncAddress)
    {
        return ++this.sequenceIdsSync[syncAddress];
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            foreach (var socket in this.sendSockets)
            {
                try
                {
                    socket.Shutdown(SocketShutdown.Both);
                }
                catch
                {
                }

                socket.Close();
                socket.Dispose();
            }
        }
    }

    protected override int SendPacket(SendData sendData, ReadOnlyMemory<byte> payload, int senderIndex)
    {
        // SendTo(..., SocketAddress) with a pre-serialized destination: the EndPoint overload
        // re-serializes into a fresh SocketAddress on every call.
        return this.sendSockets[senderIndex].SendTo(payload.Span, SocketFlags.None, sendData.DestinationAddress);
    }

    protected override int ReceiveData(Memory<byte> memory, out IPEndPoint? remoteEndPoint, out IPAddress? destinationAddress)
    {
        if (!MemoryMarshal.TryGetArray<byte>(memory, out var segment))
            throw new InvalidOperationException("Expected an array-backed receive buffer");

        if (this.timestampedReceiver != null)
        {
            int received = this.timestampedReceiver.Receive(segment, out remoteEndPoint, out destinationAddress, out long kernelTimestampNS);
            KernelReceiveTimestampNS = kernelTimestampNS;

            return received;
        }

        var socketFlags = SocketFlags.None;
        EndPoint endPoint = _blankEndpoint;
        int receivedBytes = this.listenSocket!.ReceiveMessageFrom(segment.Array!, segment.Offset, segment.Count, ref socketFlags, ref endPoint, out IPPacketInformation packetInformation);

        remoteEndPoint = endPoint as IPEndPoint;
        destinationAddress = packetInformation.Address;

        return receivedBytes;
    }

    protected override ReceiveDataPacket? TryParseObject(ReadOnlyMemory<byte> buffer, double timestampMS, IPEndPoint sourceIP, IPAddress destinationIP)
    {
        var packet = SACNPacket.Parse(buffer);

        // Note that we're still using the memory from the pipeline here, the packet is not allocating its own DMX data byte array
        if (packet != null)
        {
            if ((packet.FramingLayer as DataFramingLayer)?.Options.StreamTerminated == true)
                // Ignore the terminate packets
                return null;

            var parsedObject = new ReceiveDataPacket
            {
                TimestampMS = timestampMS,
                Source = sourceIP,
                Packet = packet
            };

            ushort? universeId = (packet.FramingLayer as DataFramingLayer)?.UniverseId;
            if (universeId.HasValue)
            {
                if (this.dmxUniverses.Contains(universeId.Value))
                    parsedObject.SubscribeMode |= SubscribeModes.DmxData;
                if (this.triggerUniverses.Contains(universeId.Value))
                    parsedObject.SubscribeMode |= SubscribeModes.Trigger;
            }

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
        this.listenSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 20);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // On Linux the kernel walks the receiving socket's multicast membership list
            // linearly for every delivered datagram (ip_mc_sf_allow), so a recorder joined to
            // hundreds of universes pays hundreds of list-node traversals per packet — measured
            // as several percent packet loss at 24k pkt/s where the same rate unicast was clean.
            // Holding the memberships on a separate socket keeps the busy listen socket's list
            // EMPTY: with an empty list the delivery check short-circuits to IP_MULTICAST_ALL
            // (default on), which delivers every group any socket on the host has joined to our
            // port-bound listen socket. Windows delivers strictly by the receiving socket's own
            // memberships, so there this stays null and joins go to the listen socket as before.
            this.membershipSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        }

        // Kernel arrival timestamps where the platform offers them (currently Linux); falls
        // back to user-space timestamping in the receive loop everywhere else
        this.timestampedReceiver = LinuxReceiveTimestamping.TryCreate(this.listenSocket);
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

        this.membershipSocket?.Dispose();
        this.membershipSocket = null;

        this.timestampedReceiver = null;
    }

    public void JoinDiscoveryMulticastGroup()
    {
        if (this.listenSocket == null)
            throw new ArgumentNullException();

        if (!this.listenDiscoveryMulticastGroup)
        {
            // Join group
            var option = new MulticastOption(UniverseDiscoveryMulticastAddress, this.localEndPoint.Address);
            MembershipSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);

            this.listenDiscoveryMulticastGroup = true;
        }
    }

    public void DropDiscoveryMulticastGroup()
    {
        if (this.listenSocket == null)
            throw new ArgumentNullException();

        if (this.listenDiscoveryMulticastGroup)
        {
            // Leave group
            var option = new MulticastOption(UniverseDiscoveryMulticastAddress, this.localEndPoint.Address);
            MembershipSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, option);

            this.listenDiscoveryMulticastGroup = false;
        }
    }
}
