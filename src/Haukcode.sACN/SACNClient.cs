using System;
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
using Haukcode.sACN.Model;

namespace Haukcode.sACN
{
    public class SACNClient : IDisposable
    {
        public class SendSocketData
        {
            public Socket Socket;

            public IPEndPoint Destination;

            public Memory<byte> SendBufferMem;
        }

        public class SendData
        {
            public ushort UniverseId;

            public IPEndPoint Destination;

            public IMemoryOwner<byte> Data;

            public int DataLength;

            public Stopwatch Enqueued;

            public double AgeMS => Enqueued.Elapsed.TotalMilliseconds;

            public SendData()
            {
                Enqueued = Stopwatch.StartNew();
            }
        }

        private const int ReceiveBufferSize = 20480;
        private const int SendBufferSize = 1024;
        private static readonly IPEndPoint _blankEndpoint = new(IPAddress.Any, 0);

        private readonly Socket listenSocket;
        private readonly ISubject<Exception> errorSubject;
        private readonly ISubject<ReceiveDataPacket> packetSubject;
        private readonly Dictionary<ushort, byte> sequenceIds = new();
        private readonly Dictionary<ushort, byte> sequenceIdsSync = new();
        private readonly object lockObject = new();
        private readonly HashSet<ushort> dmxUniverses = new();
        private readonly Memory<byte> receiveBufferMem;
        private readonly Stopwatch clock = new();
        private readonly Task receiveTask;
        private readonly Task sendTask;
        private readonly CancellationTokenSource shutdownCTS = new();
        private readonly Dictionary<IPAddress, IPEndPoint> endPointCache = new();
        private readonly ConcurrentDictionary<ushort, SendSocketData> universeSockets = new();
        private readonly IPEndPoint localEndPoint;
        private readonly BlockingCollection<SendData> sendQueue = new();
        private readonly MemoryPool<byte> memoryPool = MemoryPool<byte>.Shared;
        private int droppedPackets;
        private int slowSends;
        private readonly HashSet<(IPAddress Destination, ushort UniverseId)> usedDestinations = new();

        public SACNClient(Guid senderId, string senderName, IPAddress localAddress, int port = 5568)
        {
            if (senderId == Guid.Empty)
                throw new ArgumentException("Invalid sender Id", nameof(senderId));
            SenderId = senderId;

            SenderName = senderName;

            if (port <= 0)
                throw new ArgumentException("Invalid port", nameof(port));
            Port = port;
            this.localEndPoint = new IPEndPoint(localAddress, port);

            var receiveBuffer = GC.AllocateArray<byte>(length: ReceiveBufferSize, pinned: true);
            this.receiveBufferMem = receiveBuffer.AsMemory();

            this.errorSubject = new Subject<Exception>();
            this.packetSubject = new Subject<ReceiveDataPacket>();

            //this.sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            this.listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            //this.sendSocket.SendBufferSize = 5 * 1024 * 1024;
            //this.listenSocket.ReceiveBufferSize = 5 * 1024 * 1024;

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
                //this.sendSocket.IOControl((int)SIO_UDP_CONNRESET, optionInValue, optionOutValue);
                this.listenSocket.IOControl((int)SIO_UDP_CONNRESET, optionInValue, optionOutValue);
            }
            catch
            {
                Debug.WriteLine("Unable to set SIO_UDP_CONNRESET, maybe not supported.");
            }

            this.listenSocket.ExclusiveAddressUse = false;
            //this.sendSocket.ExclusiveAddressUse = false;
            this.listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            //this.sendSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            //this.sendSocket.Bind(new IPEndPoint(localAddress, port));
            this.listenSocket.Bind(new IPEndPoint(IPAddress.Any, port));

            //// Multicast socket settings
            //this.sendSocket.DontFragment = true;
            //this.sendSocket.MulticastLoopback = true;

            // Only join local LAN group
            //this.sendSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            this.listenSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

            this.receiveTask = Task.Run(Receiver);
            this.sendTask = Task.Run(Sender);
        }

        public bool IsOperational => !this.shutdownCTS.IsCancellationRequested;

        private void ConfigureSendSocket(Socket socket)
        {
            socket.SendBufferSize = 1400;

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

        public int Port { get; }

        public Guid SenderId { get; }

        public string SenderName { get; }

        public bool OptimizeSend { get; set; }

        public IObservable<Exception> OnError => this.errorSubject.AsObservable();

        public SendStatistics SendStatistics
        {
            get
            {
                var sendStatistics = new SendStatistics
                {
                    DroppedPackets = this.droppedPackets,
                    QueueLength = this.sendQueue.Count,
                    SlowSends = this.slowSends,
                    DestinationCount = this.usedDestinations.Count
                };

                // Reset
                this.droppedPackets = 0;
                this.slowSends = 0;
                this.usedDestinations.Clear();

                return sendStatistics;
            }
        }

        /// <summary>
        /// Observable that provides all parsed packets. This is buffered on its own thread so the processing can
        /// take any time necessary (memory consumption will go up though, there is no upper limit to amount of data buffered).
        /// </summary>
        public IObservable<ReceiveDataPacket> OnPacket => this.packetSubject.AsObservable();

        /// <summary>
        /// Gets a list of dmx universes this socket has joined to
        /// </summary>
        public IReadOnlyCollection<ushort> DMXUniverses => this.dmxUniverses.ToList();

        public void StartReceive()
        {
            this.clock.Restart();
        }

        public double ReceiveClock => this.clock.Elapsed.TotalMilliseconds;

        private async Task Receiver()
        {
            while (!this.shutdownCTS.IsCancellationRequested)
            {
                try
                {
                    var result = await this.listenSocket.ReceiveMessageFromAsync(this.receiveBufferMem, SocketFlags.None, _blankEndpoint, this.shutdownCTS.Token);

                    // Capture the timestamp first so it's as accurate as possible
                    double timestampMS = this.clock.Elapsed.TotalMilliseconds;

                    if (result.ReceivedBytes > 0)
                    {
                        var readBuffer = this.receiveBufferMem[..result.ReceivedBytes];

                        var packet = SACNPacket.Parse(readBuffer);

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
                                ipEndPoint = new IPEndPoint(result.PacketInformation.Address, Port);
                                this.endPointCache.Add(result.PacketInformation.Address, ipEndPoint);
                            }

                            newPacket.Destination = ipEndPoint;

                            this.packetSubject.OnNext(newPacket);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex is not OperationCanceledException)
                    {
                        this.errorSubject.OnNext(ex);
                    }

                    if (ex is System.Net.Sockets.SocketException)
                    {
                        // Network unreachable
                        this.shutdownCTS.Cancel();
                        break;
                    }
                }
            }
        }

        private async Task Sender()
        {
            while (!this.shutdownCTS.IsCancellationRequested)
            {
                var sendData = this.sendQueue.Take(this.shutdownCTS.Token);

                try
                {
                    if (sendData.AgeMS > 100)
                    {
                        // Old, discard
                        this.droppedPackets++;
                        //Console.WriteLine($"Age {sendData.Enqueued.Elapsed.TotalMilliseconds:N2}   queue length = {this.sendQueue.Count}   Dropped = {this.droppedPackets}");
                        continue;
                    }

                    var socketData = GetSendSocket(sendData.UniverseId);

                    var destination = sendData.Destination ?? socketData.Destination;

                    var watch = Stopwatch.StartNew();
                    await socketData.Socket.SendToAsync(sendData.Data.Memory[..sendData.DataLength], SocketFlags.None, destination);
                    watch.Stop();

                    if (watch.ElapsedMilliseconds > 20)
                        this.slowSends++;
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException)
                        continue;

                    //Console.WriteLine($"Exception in Sender handler: {ex.Message}");
                    this.errorSubject.OnNext(ex);

                    if (ex is System.Net.Sockets.SocketException)
                    {
                        // Network unreachable
                        this.shutdownCTS.Cancel();
                        break;
                    }
                }
                finally
                {
                    // Return to pool
                    sendData.Data.Dispose();
                }
            }
        }

        private SendSocketData GetSendSocket(ushort universeId)
        {
            if (!this.universeSockets.TryGetValue(universeId, out var socketData))
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                ConfigureSendSocket(socket);

                var sendBuffer = GC.AllocateArray<byte>(length: SendBufferSize, pinned: true);

                socketData = new SendSocketData
                {
                    Socket = socket,
                    Destination = new IPEndPoint(SACNCommon.GetMulticastAddress(universeId), Port),
                    SendBufferMem = sendBuffer.AsMemory()
                };
                this.universeSockets.TryAdd(universeId, socketData);
            }

            return socketData;
        }

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
        /// Multicast send data
        /// </summary>
        /// <param name="universeId">The universe Id to multicast to</param>
        /// <param name="data">Up to 512 bytes of DMX data</param>
        /// <param name="priority">Priority (default 100)</param>
        /// <param name="syncAddress">Sync universe id</param>
        /// <param name="startCode">Start code (default 0)</param>
        public void SendMulticast(ushort universeId, ReadOnlyMemory<byte> data, byte priority = 100, ushort syncAddress = 0, byte startCode = 0)
        {
            byte sequenceId = GetNewSequenceId(universeId);

            var packet = new SACNDataPacket(universeId, SenderName, SenderId, sequenceId, data, priority, syncAddress, startCode);

            SendPacket(universeId, packet);
        }

        /// <summary>
        /// Unicast send data
        /// </summary>
        /// <param name="address">The address to unicast to</param>
        /// <param name="universeId">The Universe ID</param>
        /// <param name="data">Up to 512 bytes of DMX data</param>
        /// <param name="syncAddress">Sync universe id</param>
        /// <param name="startCode">Start code (default 0)</param>
        public void SendUnicast(IPAddress address, ushort universeId, ReadOnlyMemory<byte> data, byte priority = 100, ushort syncAddress = 0, byte startCode = 0)
        {
            byte sequenceId = GetNewSequenceId(universeId);

            var packet = new SACNDataPacket(universeId, SenderName, SenderId, sequenceId, data, priority, syncAddress, startCode);

            SendPacket(universeId, address, packet);
        }

        /// <summary>
        /// Multicast send sync
        /// </summary>
        /// <param name="syncAddress">Sync universe id</param>
        public void SendMulticastSync(ushort syncAddress)
        {
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

            SendPacket(syncAddress, packet);
        }

        /// <summary>
        /// Unicast send sync
        /// </summary>
        /// <param name="syncAddress">Sync universe id</param>
        public void SendUnicastSync(IPAddress address, ushort syncAddress)
        {
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

            SendPacket(syncAddress, address, packet);
        }

        /// <summary>
        /// Send packet
        /// </summary>
        /// <param name="universeId">Universe Id</param>
        /// <param name="destination">Destination</param>
        /// <param name="packet">Packet</param>
        public void SendPacket(ushort universeId, IPAddress destination, SACNPacket packet)
        {
            if (!this.endPointCache.TryGetValue(destination, out var ipEndPoint))
            {
                ipEndPoint = new IPEndPoint(destination, Port);
                this.endPointCache.Add(destination, ipEndPoint);
            }

            var memory = this.memoryPool.Rent(packet.Length);

            int packetLength = packet.WriteToBuffer(memory.Memory);

            var newSendData = new SendData
            {
                Data = memory,
                UniverseId = universeId,
                DataLength = packetLength,
                Destination = ipEndPoint
            };

            this.usedDestinations.Add((destination, universeId));

            if (IsOperational)
            {
                this.sendQueue.Add(newSendData);
            }
            else
            {
                // Clear queue
                while (this.sendQueue.TryTake(out _)) ;
            }
        }

        /// <summary>
        /// Send packet
        /// </summary>
        /// <param name="universeId">Universe Id</param>
        /// <param name="packet">Packet</param>
        public void SendPacket(ushort universeId, SACNPacket packet)
        {
            var memory = this.memoryPool.Rent(packet.Length);

            int packetLength = packet.WriteToBuffer(memory.Memory);

            var newSendData = new SendData
            {
                Data = memory,
                UniverseId = universeId,
                DataLength = packetLength
            };

            this.usedDestinations.Add((null, universeId));
            if (IsOperational)
            {
                this.sendQueue.Add(newSendData);
            }
            else
            {
                // Clear queue
                while (this.sendQueue.TryTake(out _)) ;
            }

            //var socketData = GetSendSocket(universeId);
            //try
            //{
            //    await socketData.Socket.SendToAsync(socketData.SendBufferMem[..packetLength], SocketFlags.None, socketData.Destination);
            //}
            //catch (Exception ex)
            //{
            //    this.errorSubject.OnNext(ex);
            //}
        }

        public void WarmUpSockets(IEnumerable<ushort> universeIds)
        {
            foreach (ushort universeId in universeIds)
            {
                GetSendSocket(universeId);
            }
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

        public void Dispose()
        {
            this.shutdownCTS.Cancel();

            foreach (var kvp in this.universeSockets)
            {
                try
                {
                    kvp.Value.Socket.Shutdown(SocketShutdown.Both);
                    kvp.Value.Socket.Close();
                    kvp.Value.Socket.Dispose();
                }
                catch
                {
                }
            }

            try
            {
                this.listenSocket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
            }

            if (this.receiveTask?.IsCanceled == false)
                this.receiveTask?.Wait();
            this.receiveTask?.Dispose();

            if (this.sendTask?.IsCanceled == false)
                this.sendTask?.Wait();
            this.sendTask?.Dispose();

            this.listenSocket.Close();
            this.listenSocket.Dispose();
        }
    }
}
