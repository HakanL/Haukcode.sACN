using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace kadmium_sacn_core
{
    public class FramingLayer
    {
        static readonly int FRAMING_VECTOR = 0x00000002;
        static readonly short RESERVED = 0;
        static readonly int SourceNameLength = 64;

        public DMPLayer DMPLayer { get; set; }
        public ushort Length { get { return (ushort)(13 + SourceNameLength + DMPLayer.Length); } }
        public string SourceName { get; set; }
        public ushort UniverseID { get; set; }
        public byte SequenceID { get; set; }
        public byte Priority { get; set; }
        public FramingOptions Options { get; set; }

        public FramingLayer(string sourceName, ushort universeID, byte sequenceID, byte[] data, byte priority, byte startCode = 0)
        {
            SourceName = sourceName;
            UniverseID = universeID;
            SequenceID = sequenceID;
            Options = new FramingOptions();
            DMPLayer = new DMPLayer(data, startCode);
            Priority = priority;
            Options = new FramingOptions();
        }

        public FramingLayer()
        {
        }

        public byte[] ToArray()
        {
            byte[] array;
            using (var stream = new MemoryStream(Length))
            using (var buffer = new BigEndianBinaryWriter(stream))
            {
                ushort flagsAndFramingLength = (ushort)(SACNPacket.FLAGS | Length);
                buffer.Write(flagsAndFramingLength);
                buffer.Write(FRAMING_VECTOR);
                buffer.Write(Encoding.UTF8.GetBytes(SourceName));
                buffer.Write(Enumerable.Repeat((byte)0, 64 - SourceName.Length).ToArray());
                buffer.Write(Priority);
                buffer.Write(RESERVED);
                buffer.Write(SequenceID);
                buffer.Write(Options.ToByte());
                buffer.Write(UniverseID);

                buffer.Write(DMPLayer.ToArray());

                array = stream.ToArray();
            }

            return array;
        }

        internal static FramingLayer Parse(BigEndianBinaryReader buffer)
        {
            ushort flagsAndFramingLength = (ushort)buffer.ReadInt16();
            ushort flags = (ushort)(flagsAndFramingLength & SACNPacket.FIRST_FOUR_BITS_MASK);
            Debug.Assert(flags == SACNPacket.FLAGS);
            ushort length = (ushort)(flagsAndFramingLength & SACNPacket.LAST_TWELVE_BITS_MASK);

            int vector2 = buffer.ReadInt32();
            Debug.Assert(vector2 == FRAMING_VECTOR);
            byte[] sourceNameBytes = buffer.ReadBytes(64);
            string sourceName = new string(Encoding.UTF8.GetChars(sourceNameBytes)).TrimEnd('\0');
            byte priority = buffer.ReadByte();
            short reserved = buffer.ReadInt16();
            Debug.Assert(reserved == RESERVED);
            byte sequenceID = buffer.ReadByte();
            byte optionsByte = buffer.ReadByte();
            var options = FramingOptions.Parse(optionsByte);

            ushort universeID = buffer.ReadUInt16();

            var framingLayer = new FramingLayer
            {
                SequenceID = sequenceID,
                SourceName = sourceName,
                DMPLayer = DMPLayer.Parse(buffer),
                Options = options,
                UniverseID = universeID,
                Priority = priority
            };

            return framingLayer;
        }
    }
}