using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Haukcode.sACN.Model
{
    public class SyncFramingLayer : FramingLayer
    {
        public override ushort Length { get { return 11; } }

        public ushort SyncAddress { get; set; }

        public override int RootVector => RootLayer.VECTOR_ROOT_E131_EXTENDED;

        public SyncFramingLayer(ushort syncAddress, byte sequenceID)
            : base(sequenceID)
        {
            SyncAddress = syncAddress;
        }

        public SyncFramingLayer()
        {
        }

        public override byte[] ToArray()
        {
            using (var stream = new MemoryStream(Length))
            using (var buffer = new BigEndianBinaryWriter(stream))
            {
                ushort flagsAndFramingLength = (ushort)(SACNPacket.FLAGS | Length);
                buffer.Write(flagsAndFramingLength);
                buffer.Write(VECTOR_E131_EXTENDED_SYNCHRONIZATION);
                buffer.Write(SequenceId);
                buffer.Write(SyncAddress);
                buffer.Write((ushort)0);

                return stream.ToArray();
            }
        }

        internal static SyncFramingLayer Parse(BigEndianBinaryReader buffer)
        {
            ushort flagsAndFramingLength = (ushort)buffer.ReadInt16();
            ushort flags = (ushort)(flagsAndFramingLength & SACNPacket.FIRST_FOUR_BITS_MASK);
            Debug.Assert(flags == SACNPacket.FLAGS);
            ushort length = (ushort)(flagsAndFramingLength & SACNPacket.LAST_TWELVE_BITS_MASK);

            int vector = buffer.ReadInt32();
            Debug.Assert(vector == VECTOR_E131_EXTENDED_SYNCHRONIZATION);
            byte sequenceID = buffer.ReadByte();
            ushort syncAddress = buffer.ReadUInt16();
            ushort reserved = buffer.ReadUInt16();

            var framingLayer = new SyncFramingLayer
            {
                SequenceId = sequenceID,
                SyncAddress = syncAddress
            };

            return framingLayer;
        }
    }
}
