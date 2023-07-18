using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Haukcode.sACN
{
    internal class BigEndianBinaryWriter
    {
        private int writePosition = 0;
        private readonly Memory<byte> buffer;

        public BigEndianBinaryWriter(Memory<byte> buffer)
        {
            this.buffer = buffer;
        }

        public int WrittenBytes => this.writePosition;

        public Memory<byte> Memory => this.buffer.Slice(this.writePosition);

        public void WriteByte(byte value)
        {
            var span = buffer.Span;

            span[this.writePosition++] = (byte)value;
        }

        public void WriteShort(short value)
        {
            var span = buffer.Span;

            span[this.writePosition++] = (byte)(value >> 8);
            span[this.writePosition++] = (byte)value;
        }

        public void WriteUShort(ushort value)
        {
            var span = buffer.Span;

            span[this.writePosition++] = (byte)(value >> 8);
            span[this.writePosition++] = (byte)value;
        }

        public void WriteInt32(int value)
        {
            var span = buffer.Span;

            span[this.writePosition++] = (byte)(value >> 24);
            span[this.writePosition++] = (byte)(value >> 16);
            span[this.writePosition++] = (byte)(value >> 8);
            span[this.writePosition++] = (byte)value;
        }

        public void WriteBytes(byte[] bytes)
        {
            bytes.AsSpan().CopyTo(this.buffer.Slice(this.writePosition).Span);

            this.writePosition += bytes.Length;
        }

        public void WriteString(string value, int length)
        {
            //FIXME
            WriteBytes(Encoding.UTF8.GetBytes(value));
            WriteBytes(Enumerable.Repeat((byte)0, length - value.Length).ToArray());
        }

        private byte[] GuidToByteArray(Guid input)
        {
            var bytes = input.ToByteArray();

            return new byte[] {
                bytes[3],
                bytes[2],
                bytes[1],
                bytes[0],

                bytes[5],
                bytes[4],

                bytes[7],
                bytes[6],

                bytes[8],
                bytes[9],

                bytes[10],
                bytes[11],
                bytes[12],
                bytes[13],
                bytes[14],
                bytes[15]
            };
        }

        public void WriteGuid(Guid value)
        {
            // Fixme
            var bytes = GuidToByteArray(value);

            WriteBytes(bytes);
        }
    }
}
