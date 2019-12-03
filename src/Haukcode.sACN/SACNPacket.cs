using System;
using System.IO;

namespace Haukcode.sACN
{
    public class SACNPacket
    {
        public static ushort FLAGS = (0x7 << 12);
        public static ushort FIRST_FOUR_BITS_MASK = 0b1111_0000_0000_0000;
        public static ushort LAST_TWELVE_BITS_MASK = 0b0000_1111_1111_1111;

        public static int MAX_PACKET_SIZE = 638;

        public RootLayer RootLayer { get; set; }

        public string SourceName { get { return RootLayer.FramingLayer.SourceName; } set { RootLayer.FramingLayer.SourceName = value; } }
        public Guid UUID { get { return RootLayer.UUID; } set { RootLayer.UUID = value; } }
        public byte SequenceID { get { return RootLayer.FramingLayer.SequenceID; } set { RootLayer.FramingLayer.SequenceID = value; } }
        public byte[] Data { get { return RootLayer.FramingLayer.DMPLayer.Data; } set { RootLayer.FramingLayer.DMPLayer.Data = value; } }
        public ushort UniverseID { get { return RootLayer.FramingLayer.UniverseID; } set { RootLayer.FramingLayer.UniverseID = value; } }

        public SACNPacket(ushort universeID, string sourceName, Guid uuid, byte sequenceID, byte[] data, byte priority, byte startCode = 0)
        {
            RootLayer = new RootLayer(uuid, sourceName, universeID, sequenceID, data, priority, startCode);
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
