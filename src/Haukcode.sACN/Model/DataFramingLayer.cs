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

        public DMPLayer DMPLayer { get; set; }

        public override ushort Length { get { return (ushort)(13 + SourceNameLength + DMPLayer.Length); } }

        public string SourceName { get; set; }

        public ushort UniverseId { get; set; }

        public byte Priority { get; set; }

        public ushort SyncAddress { get; set; }

        public FramingOptions Options { get; set; }

        public override int RootVector => RootLayer.VECTOR_ROOT_E131_DATA;

        public DataFramingLayer(string sourceName, ushort universeId, byte sequenceId, byte[] data, byte priority, ushort syncAddress = 0, byte startCode = 0)
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

        public override byte[] ToArray()
        {
            using (var stream = new MemoryStream(Length))
            using (var buffer = new BigEndianBinaryWriter(stream))
            {
                ushort flagsAndFramingLength = (ushort)(SACNPacket.FLAGS | Length);
                buffer.Write(flagsAndFramingLength);
                buffer.Write(VECTOR_E131_DATA_PACKET);
                buffer.Write(Encoding.UTF8.GetBytes(SourceName));
                buffer.Write(Enumerable.Repeat((byte)0, 64 - SourceName.Length).ToArray());
                buffer.Write(Priority);
                buffer.Write(SyncAddress);
                buffer.Write(SequenceId);
                buffer.Write(Options.ToByte());
                buffer.Write(UniverseId);

                buffer.Write(DMPLayer.ToArray());

                return stream.ToArray();
            }
        }

        internal static DataFramingLayer Parse(BigEndianBinaryReader buffer)
        {
            ushort flagsAndFramingLength = (ushort)buffer.ReadInt16();
            ushort flags = (ushort)(flagsAndFramingLength & SACNPacket.FIRST_FOUR_BITS_MASK);
            Debug.Assert(flags == SACNPacket.FLAGS);
            ushort length = (ushort)(flagsAndFramingLength & SACNPacket.LAST_TWELVE_BITS_MASK);

            int vector = buffer.ReadInt32();
            Debug.Assert(vector == VECTOR_E131_DATA_PACKET);
            byte[] sourceNameBytes = buffer.ReadBytes(64);
            string sourceName = new string(Encoding.UTF8.GetChars(sourceNameBytes)).TrimEnd('\0');
            byte priority = buffer.ReadByte();
            ushort syncAddress = buffer.ReadUInt16();
            byte sequenceID = buffer.ReadByte();
            byte optionsByte = buffer.ReadByte();
            var options = FramingOptions.Parse(optionsByte);

            ushort universeID = buffer.ReadUInt16();

            var framingLayer = new DataFramingLayer
            {
                SequenceId = sequenceID,
                SourceName = sourceName,
                DMPLayer = DMPLayer.Parse(buffer),
                Options = options,
                UniverseId = universeID,
                Priority = priority,
                SyncAddress = syncAddress
            };

            return framingLayer;
        }
    }
}
