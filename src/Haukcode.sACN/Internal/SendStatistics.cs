using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Haukcode.sACN
{
    public class SendStatistics
    {
        public int DroppedPackets { get; set; }

        public int QueueLength { get; set; }

        public int DestinationCount { get; set; }

        public int SlowSends { get; set; }
    }
}
