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

        public int Length => RootLayer.Length;

        public SACNPacket(RootLayer rootLayer)
        {
            RootLayer = rootLayer;
        }

        public int WriteToBuffer(Memory<byte> outputBuffer)
        {
            return RootLayer.WriteToBuffer(outputBuffer);
        }

        public static SACNPacket Parse(ReadOnlyMemory<byte> inputBuffer)
        {
            return Parse(inputBuffer, scratchDataPacket: null);
        }

        /// <summary>
        /// Parse a packet, reusing <paramref name="scratchDataPacket"/> (and its nested layers)
        /// for DATA packets instead of allocating a fresh object graph per packet — the read-side
        /// mirror of SACNDataPacket.Update on the send path. When the input is a data packet the
        /// scratch instance is returned with every mutable field rewritten; its DMPLayer.Data is
        /// a slice of <paramref name="inputBuffer"/>, so both are only valid until the caller's
        /// next parse or buffer reuse. Non-data packets (sync, discovery — rare) allocate as
        /// before. Pass null to always allocate.
        /// </summary>
        public static SACNPacket Parse(ReadOnlyMemory<byte> inputBuffer, SACNDataPacket? scratchDataPacket)
        {
            var reader = new BigEndianBinaryReader(inputBuffer);
            var rootLayer = RootLayer.Parse(reader, scratchDataPacket?.RootLayer);

            if (scratchDataPacket != null && ReferenceEquals(rootLayer, scratchDataPacket.RootLayer))
                return scratchDataPacket;

            if (rootLayer.FramingLayer is DataFramingLayer)
                return new SACNDataPacket(rootLayer);
            if (rootLayer.FramingLayer is UniverseDiscoveryFramingLayer)
                return new SACNUniverseDiscoveryPacket(rootLayer);
            else
                return new SACNPacket(rootLayer);
        }
    }
}
