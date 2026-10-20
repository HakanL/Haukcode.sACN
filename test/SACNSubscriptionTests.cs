using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Tasks;
using Haukcode.sACN.Model;
using Xunit;

namespace Haukcode.sACN.Test
{
    public class SACNSubscriptionTests
    {
        private readonly ConcurrentQueue<(ushort Universe, SubscribeModes Mode, IPEndPoint Destination)> received = new();

        private SACNClient CreateClient()
        {
            return new SACNClient(
                Guid.NewGuid(),
                "Test",
                IPAddress.Loopback,
                channelWriter: p =>
                {
                    if (p.Packet is SACNDataPacket data)
                        this.received.Enqueue((data.UniverseId, p.SubscribeMode, p.Destination));

                    return Task.CompletedTask;
                },
                port: TestPorts.GetFreeUdpPort());
        }

        private async Task<(ushort Universe, SubscribeModes Mode, IPEndPoint Destination)> SendAndReceive(SACNClient client, ushort universe)
        {
            this.received.Clear();
            await client.SendDmxData(IPAddress.Loopback, universe, new byte[16]);

            for (int i = 0; i < 100 && this.received.IsEmpty; i++)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            Assert.True(this.received.TryDequeue(out var packet));

            return packet;
        }

        [Fact]
        public async Task ReceivedPackets_ReportTheSubscription()
        {
            using var client = CreateClient();

            Assert.Equal(SubscribeModes.None, (await SendAndReceive(client, 5)).Mode);

            client.JoinDMXUniverse(5);
            Assert.Equal(SubscribeModes.DmxData, (await SendAndReceive(client, 5)).Mode);

            client.JoinDMXUniverseForTrigger(5);
            Assert.Equal(SubscribeModes.DmxData | SubscribeModes.Trigger, (await SendAndReceive(client, 5)).Mode);

            client.DropDMXUniverse(5);
            Assert.Equal(SubscribeModes.Trigger, (await SendAndReceive(client, 5)).Mode);

            client.DropAllTriggerUniverses();
            var packet = await SendAndReceive(client, 5);
            Assert.Equal(SubscribeModes.None, packet.Mode);

            // Unicast, so the destination is reported
            Assert.Equal(IPAddress.Loopback, packet.Destination.Address);
        }

        [Fact]
        public void JoinAndDrop_FollowTheExistingRules()
        {
            using var client = CreateClient();

            client.JoinDMXUniverse(7);
            Assert.Throws<InvalidOperationException>(() => client.JoinDMXUniverse(7));
            Assert.Throws<InvalidOperationException>(() => client.DropDMXUniverse(8));

            // Trigger joins and drops are idempotent
            client.JoinDMXUniverseForTrigger(7);
            client.JoinDMXUniverseForTrigger(7);
            client.DropDMXUniverseForTrigger(9);

            client.DropAllInputUniverses();
            client.JoinDMXUniverse(7);
        }

        [Fact]
        public async Task SyncPackets_CarryTheSubscriptionOfTheirSyncAddress()
        {
            var modes = new ConcurrentQueue<SubscribeModes>();
            using var client = new SACNClient(
                Guid.NewGuid(),
                "Test",
                IPAddress.Loopback,
                channelWriter: p =>
                {
                    if (p.Packet.FramingLayer is SyncFramingLayer sync && sync.SyncAddress == 7000)
                        modes.Enqueue(p.SubscribeMode);

                    return Task.CompletedTask;
                },
                port: TestPorts.GetFreeUdpPort());

            client.JoinDMXUniverseForTrigger(7000);
            await client.SendSync(IPAddress.Loopback, 7000);

            for (int i = 0; i < 100 && modes.IsEmpty; i++)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            Assert.True(modes.TryDequeue(out var mode));
            Assert.Equal(SubscribeModes.Trigger, mode);
        }
    }
}
