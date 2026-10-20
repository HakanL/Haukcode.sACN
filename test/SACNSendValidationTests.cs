using System;
using System.Linq;
using System.Net;
using System.Text;
using Haukcode.sACN.Model;
using Xunit;

namespace Haukcode.sACN.Test
{
    public class SACNSendValidationTests
    {
        private static byte[] SourceNameField(string sourceName)
        {
            var packet = new SACNDataPacket(1, sourceName, Guid.NewGuid(), 0, new byte[1], 100);
            var buffer = new byte[packet.Length];
            packet.WriteToBuffer(buffer);

            return buffer[44..108];
        }

        [Fact]
        public void SourceName_FullField_KeepsNulTerminator()
        {
            var field = SourceNameField(new string('A', 64));

            Assert.All(field[..63], b => Assert.Equal((byte)'A', b));
            Assert.Equal(0, field[63]);
        }

        [Fact]
        public void SourceName_LongerThanField_IsTruncated()
        {
            var field = SourceNameField(new string('A', 200));

            Assert.Equal(0, field[63]);
            Assert.Equal(new string('A', 63), Encoding.UTF8.GetString(field[..63]));
        }

        [Theory]
        [InlineData("é")]        // 2 bytes
        [InlineData("€")]        // 3 bytes
        [InlineData("\U0001F3B5")] // 4 bytes, a surrogate pair
        public void SourceName_IsNotCutInsideACharacter(string wide)
        {
            // 62 ASCII bytes leave room for one more byte only, so the wide character is dropped whole
            var field = SourceNameField(new string('A', 62) + wide);

            Assert.Equal(new string('A', 62), Encoding.UTF8.GetString(field.TakeWhile(b => b != 0).ToArray()));
            Assert.Equal(0, field[62]);
            Assert.Equal(0, field[63]);
        }

        [Fact]
        public void SourceName_RoundTrips()
        {
            var packet = new SACNDataPacket(1, "Ström €", Guid.NewGuid(), 0, new byte[1], 100);
            var buffer = new byte[packet.Length];
            packet.WriteToBuffer(buffer);

            Assert.Equal("Ström €", ((SACNDataPacket)SACNPacket.Parse(buffer)!).SourceName);
        }

        [Fact]
        public void DataPacket_MoreThan512Slots_Throws()
        {
            var packet = new SACNDataPacket(1, "Source", Guid.NewGuid(), 0, new byte[513], 100);

            Assert.Throws<InvalidOperationException>(() => packet.WriteToBuffer(new byte[2000]));
        }

        [Theory]
        [InlineData((ushort)0)]
        [InlineData((ushort)64000)]
        [InlineData((ushort)64214)]
        public void UniverseDiscovery_InvalidUniverse_Throws(ushort universe)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SACNUniverseDiscoveryPacket.CreatePages(Guid.NewGuid(), "Source", [1, universe]));
        }

        [Fact]
        public void SendDmxData_RejectsOutOfRangeArguments()
        {
            using var client = new SACNClient(Guid.NewGuid(), "Test", IPAddress.Loopback, port: TestPorts.GetFreeUdpPort());

            Assert.Throws<ArgumentOutOfRangeException>("universeId", () => { _ = client.SendDmxData(null, 0, new byte[512]); });
            Assert.Throws<ArgumentOutOfRangeException>("universeId", () => { _ = client.SendDmxData(null, 64000, new byte[512]); });
            Assert.Throws<ArgumentOutOfRangeException>("syncAddress", () => { _ = client.SendDmxData(null, 1, new byte[512], syncAddress: 64214); });
            Assert.Throws<ArgumentOutOfRangeException>("priority", () => { _ = client.SendDmxData(null, 1, new byte[512], priority: 201); });
            Assert.Throws<ArgumentOutOfRangeException>("dmxData", () => { _ = client.SendDmxData(null, 1, new byte[513]); });
            Assert.Throws<ArgumentOutOfRangeException>("syncAddress", () => { _ = client.SendSync(null, 0); });
        }
    }
}
