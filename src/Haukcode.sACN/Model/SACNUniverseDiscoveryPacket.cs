using System;
using System.Collections.Generic;
using System.Linq;

namespace Haukcode.sACN.Model
{
    public class SACNUniverseDiscoveryPacket : SACNPacket
    {
        public UniverseDiscoveryFramingLayer UniverseDiscoveryFramingLayer => (UniverseDiscoveryFramingLayer)RootLayer.FramingLayer;

        public string SourceName
        {
            get { return UniverseDiscoveryFramingLayer.SourceName; }
            set { UniverseDiscoveryFramingLayer.SourceName = value; }
        }

        public byte Page
        {
            get { return UniverseDiscoveryFramingLayer.Page; }
            set { UniverseDiscoveryFramingLayer.Page = value; }
        }

        public byte LastPage
        {
            get { return UniverseDiscoveryFramingLayer.LastPage; }
            set { UniverseDiscoveryFramingLayer.LastPage = value; }
        }

        public ushort[] Universes
        {
            get { return UniverseDiscoveryFramingLayer.Universes.ToArray(); }
            set { UniverseDiscoveryFramingLayer.Universes = value ?? Array.Empty<ushort>(); }
        }

        public SACNUniverseDiscoveryPacket(Guid uuid, string sourceName, IEnumerable<ushort> universes, byte page = 0, byte lastPage = 0)
            : base(RootLayer.CreateRootLayerUniverseDiscovery(uuid, sourceName, universes?.ToArray() ?? [], page, lastPage))
        {
        }

        public SACNUniverseDiscoveryPacket(RootLayer rootLayer)
            : base(rootLayer)
        {
        }
    }
}
