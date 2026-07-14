using System;
using Haukcode.Network;
using Xunit;

namespace Haukcode.sACN.Test
{
    public class BigEndianBinaryReaderTests
    {
        // These used to wrap the bytes in a MemoryStream and read them back out with GetBuffer().
        // A MemoryStream constructed over an existing byte[] is not "publicly visible", so
        // GetBuffer() throws UnauthorizedAccessException and every test here failed. The stream was
        // pointless anyway — the reader takes a ReadOnlyMemory<byte>, so hand it the bytes directly.

        [Theory]
        [InlineData(32768, 0x80, 0x00)]
        [InlineData(0, 0x00, 0x00)]
        [InlineData(65535, 0xFF, 0xFF)]
        [InlineData(1, 0x00, 0x01)]
        [InlineData(256, 0x01, 0x00)]
        public void ReadUInt16(ushort expected, byte first, byte second)
        {
            var reader = new BigEndianBinaryReader(new ReadOnlyMemory<byte>([first, second]));

            Assert.Equal(expected, reader.ReadUInt16());
        }

        [Theory]
        [InlineData(16384, 0x40, 0x00)]
        [InlineData(0, 0x00, 0x00)]
        [InlineData(32767, 0x7F, 0xFF)]
        public void ReadInt16_Positive(short expected, byte first, byte second)
        {
            var reader = new BigEndianBinaryReader(new ReadOnlyMemory<byte>([first, second]));

            Assert.Equal(expected, reader.ReadInt16());
        }

        [Theory]
        [InlineData(-8192, 0xE0, 0x00)]
        [InlineData(-1, 0xFF, 0xFF)]
        [InlineData(-32768, 0x80, 0x00)]
        public void ReadInt16_Negative(short expected, byte first, byte second)
        {
            var reader = new BigEndianBinaryReader(new ReadOnlyMemory<byte>([first, second]));

            Assert.Equal(expected, reader.ReadInt16());
        }

        [Theory]
        [InlineData(268435456, 0x10, 0x00, 0x00, 0x00)]
        [InlineData(0, 0x00, 0x00, 0x00, 0x00)]
        [InlineData(2147483647, 0x7F, 0xFF, 0xFF, 0xFF)]
        [InlineData(1, 0x00, 0x00, 0x00, 0x01)]
        public void ReadInt32_Positive(int expected, byte first, byte second, byte third, byte fourth)
        {
            var reader = new BigEndianBinaryReader(new ReadOnlyMemory<byte>([first, second, third, fourth]));

            Assert.Equal(expected, reader.ReadInt32());
        }

        [Theory]
        [InlineData(-268435456, 0xF0, 0x00, 0x00, 0x00)]
        [InlineData(-1, 0xFF, 0xFF, 0xFF, 0xFF)]
        [InlineData(-2147483648, 0x80, 0x00, 0x00, 0x00)]
        public void ReadInt32_Negative(int expected, byte first, byte second, byte third, byte fourth)
        {
            var reader = new BigEndianBinaryReader(new ReadOnlyMemory<byte>([first, second, third, fourth]));

            Assert.Equal(expected, reader.ReadInt32());
        }

        /// <summary>
        /// The point of the class: multi-byte values are most-significant byte first, so consecutive
        /// reads must advance by exactly the width read.
        /// </summary>
        [Fact]
        public void SequentialReads_AdvanceByWidth()
        {
            byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
            var reader = new BigEndianBinaryReader(new ReadOnlyMemory<byte>(data));

            Assert.Equal(0x0102, reader.ReadUInt16());
            Assert.Equal(0x03040506, reader.ReadInt32());
            Assert.Equal(0x0708, reader.ReadUInt16());
        }
    }
}
