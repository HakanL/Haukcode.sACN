using System;
using System.IO;

namespace Haukcode.sACN.Model
{
    public class SACNPacket
    {
        public const ushort FLAGS = (0x7 << 12);
        public const ushort FIRST_FOUR_BITS_MASK = 0b1111_0000_0000_0000;
        public const ushort LAST_TWELVE_BITS_MASK = 0b0000_1111_1111_1111;
        public const int MAX_PACKET_SIZE = 638;

        public RootLayer RootLayer { get; set; }

        public FramingLayer FramingLayer => RootLayer.FramingLayer;

        public Guid UUID { get { return RootLayer.UUID; } set { RootLayer.UUID = value; } }

        public byte SequenceId { get { return FramingLayer.SequenceId; } set { FramingLayer.SequenceId = value; } }

        public SACNPacket(RootLayer rootLayer)
        {
            RootLayer = rootLayer;
        }

        public byte[] ToArray()
        {
            return RootLayer.ToArray();
        }
        public static SACNPacket Parse(byte[] packet)
        {
            using (var stream = new MemoryStream(packet))
            using (var buffer = new BigEndianBinaryReader(stream))
            {
                var rootLayer = RootLayer.Parse(buffer);

                if (rootLayer.FramingLayer is DataFramingLayer)
                    return new SACNDataPacket(rootLayer);
                else
                    return new SACNPacket(rootLayer);
            }

        }
    }
}
