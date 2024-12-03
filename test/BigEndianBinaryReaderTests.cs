using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Haukcode.Network;
using Xunit;

namespace Haukcode.sACN.Test
{
    public class BigEndianBinaryReaderTests
    {
        [Theory]
        [InlineData(32768, 0x80, 0x00)]
        public void ReadUInt16(ushort expected, byte first, byte second)
        {
            var stream = new MemoryStream(new byte[] { first, second });
            var reader = new BigEndianBinaryReader(new ReadOnlyMemory<byte>(stream.GetBuffer()));
            ushort actual = reader.ReadUInt16();

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(16384, 0x40, 0x00)]
        public void ReadInt16_Positive(short expected, byte first, byte second)
        {
            var stream = new MemoryStream(new byte[] { first, second });
            var reader = new BigEndianBinaryReader(new ReadOnlyMemory<byte>(stream.GetBuffer()));
            short actual = reader.ReadInt16();

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(-8192, 0xE0, 0x00)]
        public void ReadInt16_Negative(short expected, byte first, byte second)
        {
            var stream = new MemoryStream(new byte[] { first, second });
            var reader = new BigEndianBinaryReader(new ReadOnlyMemory<byte>(stream.GetBuffer()));
            short actual = reader.ReadInt16();

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(268435456, 0x10, 0x00, 0x00, 0x00)]
        public void ReadInt32_Positive(int expected, byte first, byte second, byte third, byte fourth)
        {
            var stream = new MemoryStream(new byte[] { first, second, third, fourth });
            var reader = new BigEndianBinaryReader(new ReadOnlyMemory<byte>(stream.GetBuffer()));
            int actual = reader.ReadInt32();

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(-268435456, 0xF0, 0x00, 0x00, 0x00)]
        public void ReadInt32_Negative(int expected, byte first, byte second, byte third, byte fourth)
        {
            var stream = new MemoryStream(new byte[] { first, second, third, fourth });
            var reader = new BigEndianBinaryReader(new ReadOnlyMemory<byte>(stream.GetBuffer()));
            int actual = reader.ReadInt32();

            Assert.Equal(expected, actual);
        }
    }
}
