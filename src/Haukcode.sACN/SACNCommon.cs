using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace Haukcode.sACN
{
    public static class SACNCommon
    {
        static byte MULTICAST_BYTE_1 = (byte)239;
        static byte MULTICAST_BYTE_2 = (byte)255;
        public static int SACN_PORT = 5568;

        /// <summary>
        /// Get Multicast address from universe id
        /// </summary>
        /// <param name="universeId">Universe Id</param>
        /// <returns></returns>
        public static IPAddress GetMulticastAddress(ushort universeId)
        {
            byte highUniverseId = (byte)(universeId >> 8);
            byte lowUniverseId = (byte)(universeId & 0xFF);
            var multicastAddress = new IPAddress(new byte[] { MULTICAST_BYTE_1, MULTICAST_BYTE_2, highUniverseId, lowUniverseId });

            return multicastAddress;
        }
    }
}
