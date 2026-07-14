using System;
using System.IO;

namespace Haukcode.sACN.Model
{
    public class SACNDataPacket : SACNPacket
    {
        public DataFramingLayer DataFramingLayer => (DataFramingLayer)RootLayer.FramingLayer;

        public string SourceName { get { return DataFramingLayer.SourceName; } set { DataFramingLayer.SourceName = value; } }

        public byte[] DMXData => DataFramingLayer.DMPLayer.Data.ToArray();

        public ushort UniverseId { get { return DataFramingLayer.UniverseId; } set { DataFramingLayer.UniverseId = value; } }

        public SACNDataPacket(ushort universeId, string sourceName, Guid uuid, byte sequenceId, ReadOnlyMemory<byte> data, byte priority, ushort syncAddress = 0, byte startCode = 0)
            : base(RootLayer.CreateRootLayerData(uuid, sourceName, universeId, sequenceId, data, priority, syncAddress, startCode))
        {
        }

        public SACNDataPacket(RootLayer rootLayer)
            : base(rootLayer)
        {
        }

        /// <summary>
        /// Reconfigure this packet in place for a new data frame, so a single scratch packet
        /// can be reused on the hot send path instead of allocating a fresh packet and its
        /// nested layers per universe per tick. Sender name and UUID are set at construction and
        /// left unchanged. Safe to reuse because the packet is serialized into the send buffer
        /// synchronously before the next call. Every mutable field is written (including options
        /// reset to their defaults) so no state leaks between frames.
        /// </summary>
        public void Update(ushort universeId, byte sequenceId, ReadOnlyMemory<byte> data, byte priority, ushort syncAddress = 0, byte startCode = 0, bool terminate = false)
        {
            var framingLayer = DataFramingLayer;
            framingLayer.UniverseId = universeId;
            framingLayer.SequenceId = sequenceId;
            framingLayer.Priority = priority;
            framingLayer.SyncAddress = syncAddress;

            var dmpLayer = framingLayer.DMPLayer;
            dmpLayer.Data = data;
            dmpLayer.StartCode = startCode;

            var options = framingLayer.Options;
            options.PreviewData = false;
            options.StreamTerminated = terminate;
            options.ForceSynchronization = syncAddress != 0;
        }
    }
}
