using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Haukcode.sACN.Model;

namespace Haukcode.sACN
{
    public class SACNClient : IDisposable
    {
        private const int ReceiveBufferSize = 2048;

        private readonly Socket socket;
        private readonly ISubject<Exception> errorSubject;
        private readonly ISubject<ReceiveDataRaw> receiveRawSubject;
        private readonly ISubject<ReceiveDataPacket> packetSubject;
        private readonly ConcurrentQueue<SendData> sendQueue = new ConcurrentQueue<SendData>();
        private readonly Dictionary<ushort, byte> sequenceIds = new Dictionary<ushort, byte>();
        private readonly Dictionary<ushort, byte> sequenceIdsSync = new Dictionary<ushort, byte>();
        private readonly object lockObject = new object();
        private readonly HashSet<ushort> dmxUniverses = new HashSet<ushort>();
        private readonly byte[] buffer = new byte[ReceiveBufferSize];
        private readonly Stopwatch clock = new Stopwatch();
        private readonly SocketAsyncEventArgs receiveEventArgs;
        private readonly SocketAsyncEventArgs sendEventArgs;
        private readonly ManualResetEvent socketCompletedEvent = new ManualResetEvent(false);

        public SACNClient(Guid senderId, string senderName, IPAddress localAddress, int port = 5568)
        {
            if (senderId == Guid.Empty)
                throw new ArgumentException("Invalid sender Id", nameof(senderId));
            SenderId = senderId;

            SenderName = senderName;

            if (port <= 0)
                throw new ArgumentException("Invalid port", nameof(port));
            Port = port;

            this.errorSubject = new Subject<Exception>();
            this.receiveRawSubject = new Subject<ReceiveDataRaw>();
            this.packetSubject = new Subject<ReceiveDataPacket>();

            this.socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            this.receiveEventArgs = new SocketAsyncEventArgs
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0)
            };
            this.receiveEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(Socket_Completed);

            this.receiveEventArgs.SetBuffer(this.buffer, 0, this.buffer.Length);

            this.sendEventArgs = new SocketAsyncEventArgs();
            this.sendEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(Socket_Completed);

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
                this.socket.IOControl((int)SIO_UDP_CONNRESET, optionInValue, optionOutValue);
            }
            catch
            {
                Debug.WriteLine("Unable to set SIO_UDP_CONNRESET, maybe not supported.");
            }

            this.socket.ExclusiveAddressUse = false;
            this.socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            this.socket.Bind(new IPEndPoint(localAddress, port));

            // Multicast socket settings
            this.socket.MulticastLoopback = true;

            // Only join local LAN group
            this.socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

            this.receiveRawSubject.Buffer(1).Subscribe(items =>
            {
                foreach (var d in items)
                {
                    var packet = SACNPacket.Parse(d.Data);

                    if (packet != null)
                    {
                        this.packetSubject.OnNext(new ReceiveDataPacket
                        {
                            TimestampMS = d.TimestampMS,
                            Host = d.Host,
                            Packet = packet
                        });
                    }
                }
            });
        }

        public int Port { get; }

        public Guid SenderId { get; }

        public string SenderName { get; }

        public IObservable<Exception> OnError => this.errorSubject.AsObservable();

        /// <summary>
        /// Observable that exposes all raw udp packets. Note that it's not buffered, processing has to be quick to 
        /// avoid buffer overruns.
        /// </summary>
        public IObservable<ReceiveDataRaw> OnReceiveRaw => this.receiveRawSubject.AsObservable();

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

            Receive();
        }

        public double ReceiveClock => this.clock.Elapsed.TotalMilliseconds;

        private void Receive()
        {
            while (true)
            {
                this.receiveEventArgs.RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

                if (!this.socket.ReceiveFromAsync(this.receiveEventArgs))
                {
                    Process(this.receiveEventArgs);
                }
                else
                    break;
            }
        }

        private void Send()
        {
            while (true)
            {
                if (!this.sendQueue.TryDequeue(out var sendData))
                    break;

                this.sendEventArgs.RemoteEndPoint = sendData.EndPoint;
                this.sendEventArgs.SetBuffer(sendData.Data, 0, sendData.Data.Length);

                this.socketCompletedEvent.Reset();

                if (!this.socket.SendToAsync(this.sendEventArgs))
                {
                    Process(this.sendEventArgs);
                }
                else
                {
                    // Block until complete
                    this.socketCompletedEvent.WaitOne();
                    break;
                }
            }
        }

        private void Process(SocketAsyncEventArgs e)
        {
            // Capture the timestamp first so it's as accurate as possible
            double timestampMS = this.clock.Elapsed.TotalMilliseconds;

            if (e.SocketError != SocketError.Success)
            {
                this.errorSubject.OnNext(new SocketException((int)e.SocketError));

                return;
            }

            if (e.LastOperation == SocketAsyncOperation.ReceiveFrom)
            {
                try
                {
                    byte[] receivedBytes = new byte[e.BytesTransferred];
                    Buffer.BlockCopy(e.Buffer, e.Offset, receivedBytes, 0, receivedBytes.Length);

                    this.receiveRawSubject.OnNext(new ReceiveDataRaw
                    {
                        TimestampMS = timestampMS,
                        Host = (IPEndPoint)e.RemoteEndPoint,
                        Data = receivedBytes
                    });
                }
                catch (Exception ex)
                {
                    this.errorSubject.OnNext(ex);
                }
            }
        }

        private void Socket_Completed(object sender, SocketAsyncEventArgs e)
        {
            this.socketCompletedEvent.Set();
            Process(e);

            switch (e.LastOperation)
            {
                case SocketAsyncOperation.ReceiveFrom:
                    Receive();
                    break;

                case SocketAsyncOperation.SendTo:
                    Send();
                    break;
            }
        }

        public void JoinDMXUniverse(ushort universeId)
        {
            if (this.dmxUniverses.Contains(universeId))
                throw new InvalidOperationException($"You have already joined the DMX Universe {universeId}");

            // Join group
            var option = new MulticastOption(SACNCommon.GetMulticastAddress(universeId), ((IPEndPoint)this.socket.LocalEndPoint).Address);
            this.socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);

            // Add to the list of universes we have joined
            this.dmxUniverses.Add(universeId);
        }

        public void DropDMXUniverse(ushort universeId)
        {
            if (!this.dmxUniverses.Contains(universeId))
                throw new InvalidOperationException($"You are trying to drop the DMX Universe {universeId} but you are not a member");

            // Drop group
            var option = new MulticastOption(SACNCommon.GetMulticastAddress(universeId));
            this.socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, option);

            // Remove from the list of universes we have joined
            this.dmxUniverses.Remove(universeId);
        }

        public void SendBytes(IPEndPoint endPoint, byte[] data)
        {
            this.sendQueue.Enqueue(new SendData
            {
                EndPoint = endPoint,
                Data = data
            });

            Send();
        }

        /// <summary>
        /// Multicast send data
        /// </summary>
        /// <param name="universeId">The universe Id to multicast to</param>
        /// <param name="data">Up to 512 bytes of DMX data</param>
        /// <param name="priority">Priority (default 100)</param>
        /// <param name="syncAddress">Sync universe id</param>
        /// <param name="startCode">Start code (default 0)</param>
        public void SendMulticast(ushort universeId, byte[] data, byte priority = 100, ushort syncAddress = 0, byte startCode = 0)
        {
            byte sequenceId = GetNewSequenceId(universeId);

            var packet = new SACNDataPacket(universeId, SenderName, SenderId, sequenceId, data, priority, syncAddress, startCode);

            SendPacket(SACNCommon.GetMulticastAddress(packet.UniverseId), packet);
        }

        /// <summary>
        /// Unicast send data
        /// </summary>
        /// <param name="address">The address to unicast to</param>
        /// <param name="universeId">The Universe ID</param>
        /// <param name="data">Up to 512 bytes of DMX data</param>
        /// <param name="syncAddress">Sync universe id</param>
        /// <param name="startCode">Start code (default 0)</param>
        public void SendUnicast(IPAddress address, ushort universeId, byte[] data, byte priority = 100, ushort syncAddress = 0, byte startCode = 0)
        {
            byte sequenceId = GetNewSequenceId(universeId);

            var packet = new SACNDataPacket(universeId, SenderName, SenderId, sequenceId, data, priority, syncAddress, startCode);

            SendPacket(address, packet);
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

            SendPacket(SACNCommon.GetMulticastAddress(syncAddress), packet);
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

            SendPacket(address, packet);
        }

        /// <summary>
        /// Send packet
        /// </summary>
        /// <param name="destination">Destination</param>
        /// <param name="packet">Packet</param>
        public void SendPacket(IPAddress destination, SACNPacket packet)
        {
            byte[] packetBytes = packet.ToArray();

            SendBytes(new IPEndPoint(destination, Port), packetBytes);
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
            try
            {
                this.socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
            }

            this.socket.Dispose();
            this.receiveEventArgs.Dispose();
            this.sendEventArgs.Dispose();
        }
    }
}
