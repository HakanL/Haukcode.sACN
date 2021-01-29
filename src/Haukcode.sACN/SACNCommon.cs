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

        /// <summary>
        /// Get the first local IPAddress from the specified interface type
        /// </summary>
        /// <param name="interfaceType">Interface type</param>
        /// <returns>Local IPAddress</returns>
        public static IEnumerable<(string AdapterName, IPAddress IPAddress)> GetAddressesFromInterfaceType(NetworkInterfaceType interfaceType)
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.SupportsMulticast && adapter.NetworkInterfaceType == interfaceType &&
                    (adapter.OperationalStatus == OperationalStatus.Up || adapter.OperationalStatus == OperationalStatus.Unknown))
                {
#if DEBUG
                    if (adapter.Name.Contains("Docker"))
                        // Skip Docker virtual adapters
                        continue;
#endif
                    IPInterfaceProperties ipProperties = adapter.GetIPProperties();

                    foreach (var ipAddress in ipProperties.UnicastAddresses)
                    {
                        if (ipAddress.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            yield return (adapter.Name, ipAddress.Address);
                    }
                }
            }
        }

        /// <summary>
        /// Return list of ethernet and WiFi network adapters
        /// </summary>
        /// <returns>List of name and IPAddress</returns>
        public static IList<(string AdapterName, IPAddress IPAddress)> GetCommonInterfaces()
        {
            var list = new List<(string AdapterName, IPAddress IPAddress)>();

            list.AddRange(GetAddressesFromInterfaceType(NetworkInterfaceType.Ethernet));
            list.AddRange(GetAddressesFromInterfaceType(NetworkInterfaceType.Wireless80211));

            return list;
        }

        /// <summary>
        /// Find first matching local IPAddress, first ethernet, then WiFi
        /// </summary>
        /// <returns>Local IPAddress</returns>
        public static IPAddress GetFirstBindAddress()
        {
            // Try Ethernet first
            var ipAddresses = GetAddressesFromInterfaceType(NetworkInterfaceType.Ethernet);
            if (ipAddresses.Any())
                return ipAddresses.First().IPAddress;

            ipAddresses = GetAddressesFromInterfaceType(NetworkInterfaceType.Wireless80211);
            if (ipAddresses.Any())
                return ipAddresses.First().IPAddress;

            return null;
        }
    }
}
