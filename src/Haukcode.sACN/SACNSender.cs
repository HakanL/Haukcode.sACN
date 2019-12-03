using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Haukcode.sACN
{
    public class SACNSender : IDisposable
    {
        private UdpClient udpClient { get; set; }

        public Guid UUID { get; set; }
        public IPAddress UnicastAddress { get; set; }
        public bool Multicast { get { return UnicastAddress == null; } }
        public int Port { get; set; }
        public string SourceName { get; set; }

        private readonly Dictionary<ushort, byte> sequenceIds = new Dictionary<ushort, byte>();

        public SACNSender(Guid uuid, string sourceName, int port)
        {
            SourceName = sourceName;
            UUID = uuid;
            this.udpClient = new UdpClient();
            Port = port;
        }

        public SACNSender(Guid uuid, string sourceName) : this(uuid, sourceName, SACNCommon.SACN_PORT) { }

        /// <summary>
        /// Multicast send
        /// </summary>
        /// <param name="universeID">The universe ID to multicast to</param>
        /// <param name="data">Up to 512 bytes of DMX data</param>
        public async Task Send(ushort universeID, byte[] data, byte priority = 100)
        {
            this.sequenceIds.TryGetValue(universeID, out byte sequenceID);
            var packet = new SACNPacket(universeID, SourceName, UUID, sequenceID++, data, priority);
            this.sequenceIds[universeID] = sequenceID;

            byte[] packetBytes = packet.ToArray();
            await udpClient.SendAsync(packetBytes, packetBytes.Length, GetEndPoint(universeID, Port));
        }

        /// <summary>
        /// Unicast send
        /// </summary>
        /// <param name="hostname">The hostname to unicast to</param>
        /// <param name="universeId">The Universe ID</param>
        /// <param name="data">Up to 512 bytes of DMX data</param>
        public async Task Send(string hostname, ushort universeId, byte[] data, byte priority = 100)
        {
            this.sequenceIds.TryGetValue(universeId, out byte sequenceID);
            var packet = new SACNPacket(universeId, SourceName, UUID, sequenceID++, data, priority);
            this.sequenceIds[universeId] = sequenceID;

            byte[] packetBytes = packet.ToArray();
            await udpClient.SendAsync(packetBytes, packetBytes.Length, hostname, Port);
        }

        private IPEndPoint GetEndPoint(ushort universeId, int port)
        {
            if (Multicast)
            {
                return new IPEndPoint(SACNCommon.GetMulticastAddress(universeId), port);
            }
            else
            {
                return new IPEndPoint(UnicastAddress, port);
            }
        }

        public void Dispose()
        {
            this.udpClient.Dispose();
        }
    }
}