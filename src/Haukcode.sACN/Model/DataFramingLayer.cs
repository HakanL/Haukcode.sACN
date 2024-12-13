using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Haukcode.sACN.Model
{
    public class DataFramingLayer : FramingLayer
    {
        public const int SourceNameLength = 64;

        public DMPLayer DMPLayer { get; set; } = null!;

        public override ushort Length { get { return (ushort)(13 + SourceNameLength + DMPLayer.Length); } }

        public string SourceName { get; set; } = null!;

        public ushort UniverseId { get; set; }

        public byte Priority { get; set; }

        public ushort SyncAddress { get; set; }

        public FramingOptions Options { get; set; } = null!;

        public override int RootVector => RootLayer.VECTOR_ROOT_E131_DATA;

        public DataFramingLayer(string sourceName, ushort universeId, byte sequenceId, ReadOnlyMemory<byte> data, byte priority, ushort syncAddress = 0, byte startCode = 0)
            : base(sequenceId)
        {
            SourceName = sourceName;
            UniverseId = universeId;
            DMPLayer = new DMPLayer(data, startCode);
            Priority = priority;
            SyncAddress = syncAddress;
            Options = new FramingOptions();
        }

        public DataFramingLayer()
        {
        }

        public override int WriteToBuffer(Memory<byte> buffer)
        {
            var writer = new BigEndianBinaryWriter(buffer);

            ushort flagsAndFramingLength = (ushort)(SACNPacket.FLAGS | Length);
            writer.WriteUInt16(flagsAndFramingLength);
            writer.WriteInt32(VECTOR_E131_DATA_PACKET);
            writer.WriteString(SourceName, 64);
            writer.WriteByte(Priority);
            writer.WriteUInt16(SyncAddress);
            writer.WriteByte(SequenceId);
            writer.WriteByte(Options.ToByte());
            writer.WriteUInt16(UniverseId);

            return writer.BytesWritten + DMPLayer.WriteToBuffer(writer.Memory);
        }

        internal static DataFramingLayer Parse(BigEndianBinaryReader reader)
        {
            ushort flagsAndFramingLength = reader.ReadUInt16();
            ushort flags = (ushort)(flagsAndFramingLength & SACNPacket.FIRST_FOUR_BITS_MASK);
            Debug.Assert(flags == SACNPacket.FLAGS);
            ushort length = (ushort)(flagsAndFramingLength & SACNPacket.LAST_TWELVE_BITS_MASK);

            int vector = reader.ReadInt32();
            Debug.Assert(vector == VECTOR_E131_DATA_PACKET);
            string sourceName = reader.ReadString(64);
            byte priority = reader.ReadByte();
            ushort syncAddress = reader.ReadUInt16();
            byte sequenceID = reader.ReadByte();
            byte optionsByte = reader.ReadByte();
            var options = FramingOptions.Parse(optionsByte);

            ushort universeID = reader.ReadUInt16();

            var framingLayer = new DataFramingLayer
            {
                SequenceId = sequenceID,
                SourceName = sourceName,
                DMPLayer = DMPLayer.Parse(reader),
                Options = options,
                UniverseId = universeID,
                Priority = priority,
                SyncAddress = syncAddress
            };

            return framingLayer;
        }
    }
}
