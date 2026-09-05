using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Xunit;

namespace Haukcode.sACN.Test
{
    public class SACNClientTests
    {
        [Fact]
        public void DropAllInputUniverses_DropsAllWithoutEnumerationError()
        {
            using var client = CreateClient();
            var dmxUniverses = GetUniverseSet(client, "dmxUniverses");
            var triggerUniverses = GetUniverseSet(client, "triggerUniverses");

            foreach (ushort universeId in new ushort[] { 10, 20, 30 })
            {
                dmxUniverses.Add(universeId);
                triggerUniverses.Add(universeId);
            }

            var exception = Record.Exception(() => client.DropAllInputUniverses());

            Assert.Null(exception);
            Assert.Empty(dmxUniverses);
        }

        [Fact]
        public void DropAllTriggerUniverses_DropsAllWithoutEnumerationError()
        {
            using var client = CreateClient();
            var dmxUniverses = GetUniverseSet(client, "dmxUniverses");
            var triggerUniverses = GetUniverseSet(client, "triggerUniverses");

            foreach (ushort universeId in new ushort[] { 10, 20, 30 })
            {
                dmxUniverses.Add(universeId);
                triggerUniverses.Add(universeId);
            }

            var exception = Record.Exception(() => client.DropAllTriggerUniverses());

            Assert.Null(exception);
            Assert.Empty(triggerUniverses);
        }

        private static SACNClient CreateClient()
        {
            return new SACNClient(Guid.NewGuid(), "test", IPAddress.Loopback, port: GetFreeUdpPort());
        }

        private static int GetFreeUdpPort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        private static HashSet<ushort> GetUniverseSet(SACNClient client, string fieldName)
        {
            var field = typeof(SACNClient).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsType<HashSet<ushort>>(field.GetValue(client));
        }
    }
}
