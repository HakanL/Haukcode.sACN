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

        /// <summary>
        /// Build the pages that advertise <paramref name="universes"/>: duplicates removed, sorted
        /// ascending and split into pages of at most 512 universes, as E1.31-2018 section 8 requires.
        /// Always returns at least one page, so a source with no universes still announces itself.
        /// </summary>
        public static IReadOnlyList<SACNUniverseDiscoveryPacket> CreatePages(Guid uuid, string sourceName, IEnumerable<ushort> universes)
        {
            var sorted = (universes ?? []).Distinct().Order().ToArray();
            if (sorted.Length > 0 && (sorted[0] < MinUniverse || sorted[^1] > MaxUniverse))
                throw new ArgumentOutOfRangeException(nameof(universes), $"Universes must be {MinUniverse}-{MaxUniverse}");
            int pageCount = Math.Max(1, (sorted.Length + UniverseDiscoveryFramingLayer.MaxUniversesPerPage - 1) / UniverseDiscoveryFramingLayer.MaxUniversesPerPage);

            // 65536 universes fill 128 pages, so the page numbers always fit in a byte
            var pages = new SACNUniverseDiscoveryPacket[pageCount];
            for (int page = 0; page < pageCount; page++)
            {
                var pageUniverses = sorted.Skip(page * UniverseDiscoveryFramingLayer.MaxUniversesPerPage).Take(UniverseDiscoveryFramingLayer.MaxUniversesPerPage);
                pages[page] = new SACNUniverseDiscoveryPacket(uuid, sourceName, pageUniverses, (byte)page, (byte)(pageCount - 1));
            }

            return pages;
        }
    }
}
