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
    // Update() and serialized synchronously inside QueuePacket, all under sendLock, so callers
    // on different threads can't interleave on it.
    private readonly SACNDataPacket scratchDataPacket;

    // Serializes the send path: the scratch packet, the pending* factory arguments and the
    // sequence counters. Uncontended it costs tens of nanoseconds per packet, small next to the
    // serialization and queueing it covers.
    private readonly object sendLock = new object();

    // Receive-side scratch: the parser fills this packet graph in place for every DATA packet
    // instead of allocating a packet plus three layers per datagram (36,000/s at 600 universes /
    // 60 Hz), and the wrapper handed to the channel writer is reused the same way. Both are only
    // valid until the writer returns, which is the contract Client documents for its buffer too.
    private readonly SACNDataPacket receiveScratchPacket = new SACNDataPacket(1, string.Empty, Guid.Empty, 0, ReadOnlyMemory<byte>.Empty, 100);
    private readonly ReceiveDataPacket receiveScratchObject = new ReceiveDataPacket();

    public const int DefaultPort = 5568;
    public const int ReceiveBufferSize = 680 * 20 * 200;
    public const ushort UniverseDiscoveryUniverse = 64214;
    public static readonly IPAddress UniverseDiscoveryMulticastAddress = Haukcode.Network.Utils.GetMulticastAddress(UniverseDiscoveryUniverse);
    private const int SendBufferSize = 680 * 20 * 200;
    private static readonly IPEndPoint _blankEndpoint = new(IPAddress.Any, 0);

    private Socket? listenSocket;

    // Linux only: holds the IGMP memberships so the listen socket's kernel membership list
    // stays empty on the per-packet delivery path (see InitializeReceiveSocket). Null on
    // other platforms, where memberships live on the listen socket itself.
    private Socket? membershipSocket;

    private Socket MembershipSocket => this.membershipSocket ?? this.listenSocket!;

    // Kernel receive timestamping: packets are stamped on arrival in the network stack
    // instead of when user space reads them, so socket-buffer waits (GC pauses, busy
    // receive loop) no longer distort recorded timing. Null = portable path with user-space
    // timestamps.
    private IReceiveTimestamping? timestampedReceiver;

    /// <summary>
    /// True when packets are being stamped by the kernel on arrival (Linux SO_TIMESTAMPNS,
    /// Windows SIO_TIMESTAMPING, macOS SO_TIMESTAMP) rather than by user space when the
    /// receive loop reads them.
    /// </summary>
    public bool KernelReceiveTimestampsActive => this.timestampedReceiver != null;

    /// <summary>
    /// True once at least one received packet actually carried a kernel timestamp. On
    /// Windows the socket option can be enabled while the adapter driver never stamps
    /// (software timestamping off, the default) — this is the after-the-fact truth,
    /// where Active only means "requested".
    /// </summary>
    public bool KernelReceiveTimestampsObserved => this.timestampedReceiver?.KernelTimestampsObserved == true;

    // One send socket per sender shard. Several threads sharing one UDP socket serialize on the
    // kernel's socket lock, which throws away most of the gain from sharding; a socket each does
    // not. Receivers don't care which source port a frame came from, and each socket binds to an
    // ephemeral port anyway.
    private readonly Socket[] sendSockets;

    private readonly IPEndPoint localEndPoint;
    // Per-universe sACN sequence counters, indexed by universe id (and by sync address for the sync
    // counter). Incremented under sendLock — one increment per universe per packet, ~36,000/s at
    // 600 universes @ 60 Hz — so a plain array is used instead of a dictionary. The dictionary
    // lookup was a measured hot spot (~13% of the scheduler thread at that load).
    private readonly byte[] sequenceIds = new byte[ushort.MaxValue + 1];
    private readonly byte[] sequenceIdsSync = new byte[ushort.MaxValue + 1];

    // What each universe is joined for, indexed by universe id. Join/Drop change it under
    // subscriptionLock on the caller's thread; the receive thread reads it for every data packet
    // without the lock, which is safe because each element is written whole.
    private readonly SubscribeModes[] subscriptions = new SubscribeModes[ushort.MaxValue + 1];
    private readonly object subscriptionLock = new object();

    // Send-side destination caches. The queue-writer thread (SendDmxData), the sync barrier and
    // the public SendPacketImmediately can all resolve a destination, so these are concurrent.
    // Lookups of existing entries take no lock, which is all the hot path does after warm-up.
    private readonly ConcurrentDictionary<IPAddress, (IPEndPoint EndPoint, bool Multicast)> endPointCache = new();
    private readonly ConcurrentDictionary<ushort, IPEndPoint> universeMulticastEndpoints = new();

    // Serialized destinations, so the hot path never re-serializes an IPEndPoint
    private readonly ConcurrentDictionary<IPEndPoint, SocketAddress> socketAddressCache = new();

    // The destination address of received packets, resolved once per address. Only touched on
    // the receive thread.
    private readonly Dictionary<IPAddress, IPEndPoint?> receiveDestinationCache = [];

    private bool listenDiscoveryMulticastGroup = false;

    /// <param name="channelWriter">
    /// Called on the receive thread for every received packet. Data packets are reused for the
    /// next datagram once this returns, so process the packet here, or keep
    /// <see cref="ReceiveDataPacket.Clone"/>, for example before writing it to a Channel.
    /// </param>
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
        // Sized for the largest packet we can receive, a full universe discovery page (1144
        // bytes). A smaller buffer silently truncates those datagrams on Windows. The send pool
        // shares this size, but only holds as many buffers as are actually in flight. The
        // arguments are checked first: the base constructor starts the sender threads.
        : base(CheckArguments(senderId, senderName, localAddress, port), channelWriter, channelWriterComplete, senderCount)
    {
        SenderId = senderId;
        SenderName = senderName;

        this.scratchDataPacket = new SACNDataPacket(1, senderName, senderId, 0, ReadOnlyMemory<byte>.Empty, 100);
        this.scratchPacketWriter = this.scratchDataPacket.WriteToBuffer;
        this.pendingSendDataFactory = BuildPendingSendData;

        this.localEndPoint = new IPEndPoint(localAddress, port);
        this.sendSockets = new Socket[SenderCount];

        try
        {
            for (int i = 0; i < SenderCount; i++)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                this.sendSockets[i] = socket;

                socket.SendBufferSize = SendBufferSize;

                Haukcode.Network.Utils.SetSocketOptions(socket);

                // Multicast socket settings
                socket.DontFragment = true;
                socket.MulticastLoopback = false;

                // Bind to the local interface (ephemeral port)
                socket.Bind(new IPEndPoint(localAddress, 0));

                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 20);
            }

            StartReceive();
        }
        catch
        {
            // Binding to an address this host doesn't have, or a port in use: stop the sender
            // threads and close the sockets made so far, or they'd outlive the failed constructor
            Dispose();
            throw;
        }
    }

    private static int CheckArguments(Guid senderId, string senderName, IPAddress localAddress, int port)
    {
        if (senderId == Guid.Empty)
            throw new ArgumentException("Invalid sender Id", nameof(senderId));
        ArgumentNullException.ThrowIfNull(senderName);
        ArgumentNullException.ThrowIfNull(localAddress);
        if (port <= 0 || port > IPEndPoint.MaxPort)
            throw new ArgumentException("Invalid port", nameof(port));

        return SACNPacket.MAX_UNIVERSE_DISCOVERY_PACKET_SIZE;
    }

    public IPEndPoint LocalEndPoint => this.localEndPoint;

    public Guid SenderId { get; }

    public string SenderName { get; }

    public void JoinDMXUniverse(ushort universeId)
    {
        CheckUniverse(universeId, nameof(universeId));
        ObjectDisposedException.ThrowIf(this.listenSocket == null, this);

        lock (this.subscriptionLock)
        {
            if ((this.subscriptions[universeId] & SubscribeModes.DmxData) != 0)
                throw new InvalidOperationException($"You have already joined the DMX Universe {universeId}");

            Subscribe(universeId, SubscribeModes.DmxData);
        }
    }

    public void DropDMXUniverse(ushort universeId)
    {
        if (this.listenSocket == null)
            return;

        lock (this.subscriptionLock)
        {
            if ((this.subscriptions[universeId] & SubscribeModes.DmxData) == 0)
                throw new InvalidOperationException($"You are trying to drop the DMX Universe {universeId} but you are not a member");

            Unsubscribe(universeId, SubscribeModes.DmxData);
        }
    }

    public void DropAllTriggerUniverses()
    {
        DropAll(SubscribeModes.Trigger);
    }

    public void DropAllInputUniverses()
    {
        DropAll(SubscribeModes.DmxData);
    }

    public void JoinDMXUniverseForTrigger(ushort universeId)
    {
        CheckUniverse(universeId, nameof(universeId));
        ObjectDisposedException.ThrowIf(this.listenSocket == null, this);

        lock (this.subscriptionLock)
        {
            // Already joined is fine
            if ((this.subscriptions[universeId] & SubscribeModes.Trigger) == 0)
                Subscribe(universeId, SubscribeModes.Trigger);
        }
    }

    public void DropDMXUniverseForTrigger(ushort universeId)
    {
        if (this.listenSocket == null)
            return;

        lock (this.subscriptionLock)
        {
            // Already dropped is fine
            if ((this.subscriptions[universeId] & SubscribeModes.Trigger) != 0)
                Unsubscribe(universeId, SubscribeModes.Trigger);
        }
    }

    /// <summary>
    /// Add <paramref name="mode"/> for the universe, joining its multicast group if nothing else
    /// had. Called under subscriptionLock.
    /// </summary>
    private void Subscribe(ushort universeId, SubscribeModes mode)
    {
        var current = this.subscriptions[universeId];
        if (current == SubscribeModes.None)
        {
            var option = new MulticastOption(Haukcode.Network.Utils.GetMulticastAddress(universeId), this.localEndPoint.Address);
            MembershipSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
        }

        this.subscriptions[universeId] = current | mode;
    }

    /// <summary>
    /// Remove <paramref name="mode"/> for the universe, leaving its multicast group once nothing
    /// else needs it. Called under subscriptionLock.
    /// </summary>
    private void Unsubscribe(ushort universeId, SubscribeModes mode)
    {
        var remaining = this.subscriptions[universeId] & ~mode;
        if (remaining == SubscribeModes.None)
        {
            var option = new MulticastOption(Haukcode.Network.Utils.GetMulticastAddress(universeId), this.localEndPoint.Address);
            MembershipSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, option);
        }

        this.subscriptions[universeId] = remaining;
    }

    private void DropAll(SubscribeModes mode)
    {
        if (this.listenSocket == null)
            return;

        lock (this.subscriptionLock)
        {
            for (int universeId = 0; universeId < this.subscriptions.Length; universeId++)
            {
                if ((this.subscriptions[universeId] & mode) != 0)
                    Unsubscribe((ushort)universeId, mode);
            }
        }
    }

    /// <summary>
    /// Send data
    /// </summary>
    /// <param name="address">The optional address to unicast to</param>
    /// <param name="universeId">The Universe ID, 1-63999</param>
    /// <param name="dmxData">Up to 512 bytes of DMX data</param>
    /// <param name="syncAddress">Sync universe id, 1-63999, or 0 for none</param>
    /// <param name="startCode">Start code (default 0)</param>
    /// <param name="important">Never dropped by the send queue's age cut-off or bound (termination packets always are)</param>
    /// <param name="priority">Priority 0-200 (default 100)</param>
    /// <param name="terminate">
    /// Terminate the stream for this universe. E1.31 requires three packets with the
    /// Stream_Terminated bit, so three are sent; stop sending to the universe afterwards.
    /// </param>
    /// <param name="preview">Preview_Data: the data is for visualizers, not for live output</param>
    /// <param name="forceSynchronization">
    /// Force_Synchronization, only sent when <paramref name="syncAddress"/> is set: true (the
    /// default) lets receivers carry on unsynchronized if the sync packets stop, false makes them
    /// hold the last frame until synchronization resumes.
    /// </param>
    public Task SendDmxData(
        IPAddress? address,
        ushort universeId,
        ReadOnlyMemory<byte> dmxData,
        byte priority = 100,
        ushort syncAddress = 0,
        byte startCode = 0,
        bool important = false,
        bool terminate = false,
        bool preview = false,
        bool forceSynchronization = true)
    {
        CheckUniverse(universeId, nameof(universeId));
        if (syncAddress != 0)
            CheckUniverse(syncAddress, nameof(syncAddress));
        if (priority > SACNPacket.MaxPriority)
            throw new ArgumentOutOfRangeException(nameof(priority), priority, $"Priority must be 0-{SACNPacket.MaxPriority}");
        if (dmxData.Length > DMPLayer.MaxSlots)
            throw new ArgumentOutOfRangeException(nameof(dmxData), dmxData.Length, $"At most {DMPLayer.MaxSlots} slots");

        if (!IsOperational)
            return Task.CompletedTask;

        lock (this.sendLock)
        {
            if (terminate)
            {
                // Each call serializes the scratch packet before it returns, so the three can be
                // queued back to back before awaiting any of them. Always important: receivers
                // rely on them to drop the source, so the age cut-off and queue bound mustn't.
                var sends = new Task[TerminationPacketCount];
                for (int i = 0; i < sends.Length; i++)
                {
                    sends[i] = QueueDmxData(address, universeId, dmxData, priority, syncAddress, startCode, important: true, terminate, preview, forceSynchronization);
                }

                return Task.WhenAll(sends);
            }

            return QueueDmxData(address, universeId, dmxData, priority, syncAddress, startCode, important, terminate, preview, forceSynchronization);
        }
    }

    /// <summary>
    /// E1.31 section 6.2.6: a source terminating a stream sends three packets with Stream_Terminated set.
    /// </summary>
    private const int TerminationPacketCount = 3;

    // Called under sendLock
    private Task QueueDmxData(IPAddress? address, ushort universeId, ReadOnlyMemory<byte> dmxData, byte priority, ushort syncAddress, byte startCode, bool important, bool terminate, bool preview, bool forceSynchronization)
    {
        byte sequenceId = GetNewSequenceId(universeId);

        // Reconfigure the reused scratch packet in place instead of allocating a new one
        // (and its three nested layers) for every packet on the hot send path.
        this.scratchDataPacket.Update(universeId, sequenceId, dmxData, priority, syncAddress, startCode, terminate, preview, forceSynchronization);

        return QueuePacketForSending(universeId, address, this.scratchDataPacket, important);
    }

    /// <summary>
    /// Send sync
    /// </summary>
    /// <param name="address">The optional address to unicast to</param>
    /// <param name="syncAddress">Sync universe id</param>
    public Task SendSync(IPAddress? address, ushort syncAddress)
    {
        CheckUniverse(syncAddress, nameof(syncAddress));

        if (!IsOperational)
            return Task.CompletedTask;

        byte sequenceId;
        lock (this.sendLock)
        {
            sequenceId = GetNewSequenceIdSync(syncAddress);
        }

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
    /// Send universe discovery (E1.31 extended discovery packet). The universes are sorted and
    /// split into pages of at most 512, and every page is sent. E1.31 expects this to be repeated
    /// every 10 seconds.
    /// </summary>
    /// <param name="address">Optional unicast destination. If null, uses the universe discovery multicast address.</param>
    /// <param name="universes">Universe IDs to advertise.</param>
    /// <param name="important">Important</param>
    public async Task SendUniverseDiscovery(IPAddress? address, IEnumerable<ushort> universes, bool important = true)
    {
        if (!IsOperational)
            return;

        var destination = new IPEndPoint(address ?? UniverseDiscoveryMulticastAddress, this.localEndPoint.Port);

        foreach (var packet in SACNUniverseDiscoveryPacket.CreatePages(SenderId, SenderName, universes))
        {
            await SendPacketImmediately(destination, packet, important);
        }
    }

    /// <summary>
    /// Send packet
    /// </summary>
    /// <param name="universeId">Universe Id</param>
    /// <param name="destination">Destination</param>
    /// <param name="packet">Packet</param>
    /// <param name="important">Important</param>
    // Arguments for the cached send-data factory below. QueuePacket invokes the factory and the
    // packet writer synchronously before it returns, and SendDmxData holds sendLock across it, so
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
    /// </summary>
    private Func<SendData> CreateSendData(ushort universeId, IPAddress? destination)
    {
        return () => BuildSendData(universeId, destination);
    }

    private SendData BuildSendData(ushort universeId, IPAddress? destination)
    {
        var sendDataDestination = ResolveDestination(universeId, destination);

        // Reuse a spent send-data object returned by the sender instead of allocating a new
        // one for every packet on the hot path. Every field is rewritten below.
        var pooledSendData = RentSendData();
        if (pooledSendData != null)
        {
            pooledSendData.Destination = sendDataDestination;
            pooledSendData.DestinationAddress = this.socketAddressCache.GetOrAdd(sendDataDestination, static endPoint => endPoint.Serialize());

            return pooledSendData;
        }

        // Pool empty (startup only) — the constructor serializes the destination itself.
        return new SendData(sendDataDestination);
    }

    /// <summary>
    /// The endpoint to send to: <paramref name="destination"/> when it's a unicast address,
    /// otherwise (none given, or a multicast address) the universe's multicast group.
    /// </summary>
    private IPEndPoint ResolveDestination(ushort universeId, IPAddress? destination)
    {
        int port = this.localEndPoint.Port;

        if (destination != null)
        {
            var details = this.endPointCache.GetOrAdd(destination,
                static (address, port) => (new IPEndPoint(address, port), Haukcode.Network.Utils.IsMulticast(address)), port);

            if (!details.Multicast)
                return details.EndPoint;
        }

        return this.universeMulticastEndpoints.GetOrAdd(universeId,
            static (universe, port) => new IPEndPoint(Haukcode.Network.Utils.GetMulticastAddress(universe), port), port);
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
        return SendPacketImmediately(ResolveDestination(universeId, destination), packet, important);
    }

    public async Task SendPacketImmediately(IPEndPoint destination, SACNPacket packet, bool important = false)
    {
        await SendImmediateAsync(
            allocatePacketLength: packet.Length,
            important: important,
            sendDataFactory: () => new SendData(destination),
            packetWriter: packet.WriteToBuffer);

    }

    private static void CheckUniverse(ushort universeId, string paramName)
    {
        if (universeId < SACNPacket.MinUniverse || universeId > SACNPacket.MaxUniverse)
            throw new ArgumentOutOfRangeException(paramName, universeId, $"Universe must be {SACNPacket.MinUniverse}-{SACNPacket.MaxUniverse}");
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
            // Null entries when the constructor failed part-way
            foreach (var socket in this.sendSockets ?? [])
            {
                if (socket == null)
                    continue;

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
        var packet = SACNPacket.Parse(buffer, this.receiveScratchPacket);

        // Note that we're still using the memory from the receive buffer here, the packet is not allocating its own DMX data byte array
        if (packet != null)
        {
            // Stream_Terminated packets are delivered too, so the consumer can drop the source
            // straight away instead of waiting out the data loss timeout. E1.31 says to ignore
            // their DMX data, so consumers check Options.StreamTerminated before applying it.
            var dataFramingLayer = packet.FramingLayer as DataFramingLayer;

            // Data packets land in the scratch graph, so their wrapper can be scratch too. Other
            // packet types (sync, discovery) are rare and allocated, and get a fresh wrapper so a
            // subscriber may keep them.
            var parsedObject = ReferenceEquals(packet, this.receiveScratchPacket) ? this.receiveScratchObject : new ReceiveDataPacket();
            parsedObject.TimestampMS = timestampMS;
            parsedObject.Source = sourceIP;
            parsedObject.Packet = packet;
            // Sync packets go to their sync address's multicast group, so they carry that subscription
            parsedObject.SubscribeMode = packet.FramingLayer switch
            {
                DataFramingLayer data => this.subscriptions[data.UniverseId],
                SyncFramingLayer sync => this.subscriptions[sync.SyncAddress],
                _ => SubscribeModes.None
            };

            // Multicast destinations are reported as null, unicast ones as the endpoint
            if (!this.receiveDestinationCache.TryGetValue(destinationIP, out var destination))
            {
                destination = Haukcode.Network.Utils.IsMulticast(destinationIP) ? null : new IPEndPoint(destinationIP, this.localEndPoint.Port);
                this.receiveDestinationCache.Add(destinationIP, destination);
            }

            parsedObject.Destination = destination;

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

        // Kernel arrival timestamps where the platform offers them (Linux, Windows when the
        // adapter has software timestamping enabled, macOS); falls back to user-space
        // timestamping in the receive loop everywhere else
        this.timestampedReceiver = ReceiveTimestamping.TryCreate(this.listenSocket);
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
        ObjectDisposedException.ThrowIf(this.listenSocket == null, this);

        lock (this.subscriptionLock)
        {
            if (!this.listenDiscoveryMulticastGroup)
            {
                // Join group
                var option = new MulticastOption(UniverseDiscoveryMulticastAddress, this.localEndPoint.Address);
                MembershipSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);

                this.listenDiscoveryMulticastGroup = true;
            }
        }
    }

    public void DropDiscoveryMulticastGroup()
    {
        ObjectDisposedException.ThrowIf(this.listenSocket == null, this);

        lock (this.subscriptionLock)
        {
            if (this.listenDiscoveryMulticastGroup)
            {
                // Leave group
                var option = new MulticastOption(UniverseDiscoveryMulticastAddress, this.localEndPoint.Address);
                MembershipSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, option);

                this.listenDiscoveryMulticastGroup = false;
            }
        }
    }
}
