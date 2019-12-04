using System;
using System.IO;

namespace Haukcode.sACN.Model
{
    public class SACNPacket
    {
        internal static readonly ushort FLAGS = (0x7 << 12);
        internal static readonly ushort FIRST_FOUR_BITS_MASK = 0b1111_0000_0000_0000;
        internal static readonly ushort LAST_TWELVE_BITS_MASK = 0b0000_1111_1111_1111;

        internal static readonly int MAX_PACKET_SIZE = 638;

        public RootLayer RootLayer { get; set; }

        public string SourceName { get { return RootLayer.FramingLayer.SourceName; } set { RootLayer.FramingLayer.SourceName = value; } }

        public Guid UUID { get { return RootLayer.UUID; } set { RootLayer.UUID = value; } }

        public byte SequenceId { get { return RootLayer.FramingLayer.SequenceID; } set { RootLayer.FramingLayer.SequenceID = value; } }

        public byte[] DMXData { get { return RootLayer.FramingLayer.DMPLayer.Data; } }

        public ushort UniverseId { get { return RootLayer.FramingLayer.UniverseID; } set { RootLayer.FramingLayer.UniverseID = value; } }

        public SACNPacket(ushort universeId, string sourceName, Guid uuid, byte sequenceId, byte[] data, byte priority, byte startCode = 0)
        {
            RootLayer = new RootLayer(uuid, sourceName, universeId, sequenceId, data, priority, startCode);
        }

        public SACNPacket(RootLayer rootLayer)
        {
            RootLayer = rootLayer;
        }

        public static SACNPacket Parse(byte[] packet)
        {
            using (var stream = new MemoryStream(packet))
            using (var buffer = new BigEndianBinaryReader(stream))
            {
                var rootLayer = RootLayer.Parse(buffer);

                return new SACNPacket(rootLayer);
            }
        }

        public byte[] ToArray()
        {
            return RootLayer.ToArray();
        }
    }
}
