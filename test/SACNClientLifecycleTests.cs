using System;
using System.Diagnostics;
using System.Net;
using Xunit;

namespace Haukcode.sACN.Test
{
    [CollectionDefinition(nameof(SACNClientLifecycleTests), DisableParallelization = true)]
    public class SACNClientLifecycleCollection
    {
    }

    // Not run in parallel with other tests, so the thread count isn't skewed by their clients
    [Collection(nameof(SACNClientLifecycleTests))]
    public class SACNClientLifecycleTests
    {
        private static int ThreadCount()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();

            return process.Threads.Count;
        }

        [Fact]
        public void FailedConstructors_DontLeakThreads()
        {
            int before = ThreadCount();

            for (int i = 0; i < 20; i++)
            {
                Assert.Throws<ArgumentException>(() => new SACNClient(Guid.Empty, "Test", IPAddress.Loopback, port: TestPorts.GetFreeUdpPort()));
                Assert.Throws<ArgumentException>(() => new SACNClient(Guid.NewGuid(), "Test", IPAddress.Loopback, port: 70_000));

                // An address this host doesn't have: fails to bind after the sender threads started
                Assert.ThrowsAny<System.Net.Sockets.SocketException>(() => new SACNClient(Guid.NewGuid(), "Test", IPAddress.Parse("192.0.2.1"), port: TestPorts.GetFreeUdpPort()));
            }

            // The failed constructors stop their sender threads; 20 leaked ones would show
            Assert.True(ThreadCount() - before < 10, $"Threads went from {before} to {ThreadCount()}");
        }

        [Theory]
        [InlineData((ushort)0)]
        [InlineData((ushort)64000)]
        [InlineData((ushort)64214)]
        public void Joins_RejectReservedUniverses(ushort universe)
        {
            using var client = new SACNClient(Guid.NewGuid(), "Test", IPAddress.Loopback, port: TestPorts.GetFreeUdpPort());

            Assert.Throws<ArgumentOutOfRangeException>(() => client.JoinDMXUniverse(universe));
            Assert.Throws<ArgumentOutOfRangeException>(() => client.JoinDMXUniverseForTrigger(universe));
        }

        [Fact]
        public void AfterDispose_JoinsThrowObjectDisposed()
        {
            var client = new SACNClient(Guid.NewGuid(), "Test", IPAddress.Loopback, port: TestPorts.GetFreeUdpPort());
            client.Dispose();

            Assert.Throws<ObjectDisposedException>(() => client.JoinDMXUniverse(1));
            Assert.Throws<ObjectDisposedException>(() => client.JoinDMXUniverseForTrigger(1));
            Assert.Throws<ObjectDisposedException>(() => client.JoinDiscoveryMulticastGroup());
        }
    }
}
