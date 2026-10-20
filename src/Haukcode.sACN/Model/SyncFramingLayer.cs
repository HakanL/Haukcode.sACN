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

        public override uint RootVector => RootLayer.VECTOR_ROOT_E131_EXTENDED;

        public SyncFramingLayer(ushort syncAddress, byte sequenceID)
            : base(sequenceID)
        {
            SyncAddress = syncAddress;
        }

        public SyncFramingLayer()
        {
        }

        public override int WriteToBuffer(Memory<byte> buffer)
        {
            var writer = new SpanBinaryWriter(buffer.Span);

            ushort flagsAndFramingLength = (ushort)(SACNPacket.FLAGS | Length);
            writer.WriteUInt16(flagsAndFramingLength);
            writer.WriteUInt32(VECTOR_E131_EXTENDED_SYNCHRONIZATION);
            writer.WriteByte(SequenceId);
            writer.WriteUInt16(SyncAddress);
            writer.WriteUInt16((ushort)0);

            return writer.BytesWritten;
        }

        /// <summary>
        /// Parse the rest of the layer; RootLayer has read and checked its flags, length and vector.
        /// </summary>
        internal static SyncFramingLayer Parse(BigEndianBinaryReader reader, int framingLength)
        {
            if (framingLength != 11)
                throw new InvalidDataException($"Invalid synchronization framing layer length {framingLength}");

            byte sequenceID = reader.ReadByte();
            ushort syncAddress = reader.ReadUInt16();
            ushort reserved = reader.ReadUInt16();

            var framingLayer = new SyncFramingLayer
            {
                SequenceId = sequenceID,
                SyncAddress = syncAddress
            };

            return framingLayer;
        }
    }
}
