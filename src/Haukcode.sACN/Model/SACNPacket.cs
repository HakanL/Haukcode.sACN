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

        /// <summary>
        /// Universes (and synchronization addresses) E1.31 allows; 0 and 64000-65535 are reserved.
        /// </summary>
        public const ushort MinUniverse = 1;
        public const ushort MaxUniverse = 63999;

        public const byte MaxPriority = 200;
        public const byte DefaultPriority = 100;

        /// <summary>
        /// A universe discovery page with the full 512 universes: 120 header bytes plus 2 per universe.
        /// </summary>
        public const int MAX_UNIVERSE_DISCOVERY_PACKET_SIZE = 120 + (UniverseDiscoveryFramingLayer.MaxUniversesPerPage * 2);

        public RootLayer RootLayer { get; set; }

        /// <summary>
        /// Check a PDU's flags (always 0x7 in E1.31) and return the length in its low 12 bits.
        /// </summary>
        internal static int ReadPduLength(ushort flagsAndLength, string layer)
        {
            if ((flagsAndLength & FIRST_FOUR_BITS_MASK) != FLAGS)
                throw new InvalidDataException($"Invalid {layer} flags 0x{flagsAndLength >> 12:X}");

            return flagsAndLength & LAST_TWELVE_BITS_MASK;
        }

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

        /// <summary>
        /// Parse a packet. Returns null for a datagram that isn't an E1.31 packet, or uses a
        /// root or extended framing vector this library doesn't know; E1.31 says to discard
        /// those. Throws InvalidDataException for a packet that claims to be E1.31 but is malformed.
        /// </summary>
        public static SACNPacket? Parse(ReadOnlyMemory<byte> inputBuffer)
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
        /// before. Pass null to always allocate. Returns null, or throws, as the overload above.
        /// </summary>
        public static SACNPacket? Parse(ReadOnlyMemory<byte> inputBuffer, SACNDataPacket? scratchDataPacket)
        {
            var reader = new BigEndianBinaryReader(inputBuffer);
            var rootLayer = RootLayer.Parse(reader, inputBuffer.Length, scratchDataPacket?.RootLayer);
            if (rootLayer == null)
                return null;

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
