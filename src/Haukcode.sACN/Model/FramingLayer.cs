using System;

namespace Haukcode.sACN.Model
{
    public abstract class FramingLayer
    {
        public const uint VECTOR_E131_DATA_PACKET = 0x00000002;
        public const uint VECTOR_E131_EXTENDED_SYNCHRONIZATION = 0x00000001;
        public const uint VECTOR_E131_EXTENDED_DISCOVERY = 0x00000002;

        public abstract ushort Length { get; }

        public byte SequenceId { get; set; }

        public abstract uint RootVector { get; }

        public FramingLayer(byte sequenceId)
        {
            SequenceId = sequenceId;
        }

        public FramingLayer()
        {
        }

        public abstract int WriteToBuffer(Memory<byte> buffer);
    }
}
