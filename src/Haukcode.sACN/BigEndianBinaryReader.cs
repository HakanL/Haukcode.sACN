using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Haukcode.sACN
{
    public class BigEndianBinaryReader
    {
        private int readPosition = 0;
        private readonly ReadOnlyMemory<byte> buffer;

        public BigEndianBinaryReader(ReadOnlyMemory<byte> buffer)
        {
            this.buffer = buffer;
        }

        public short ReadInt16()
        {
            var span = this.buffer.Span;
            byte b1 = span[this.readPosition++];
            byte b2 = span[this.readPosition++];

            return (short)((b1 << 8) | b2);
        }

        public ushort ReadUInt16()
        {
            var span = this.buffer.Span;
            byte b1 = span[this.readPosition++];
            byte b2 = span[this.readPosition++];

            return (ushort)((b1 << 8) | b2);
        }

        public int ReadInt32()
        {
            var span = this.buffer.Span;
            byte b1 = span[this.readPosition++];
            byte b2 = span[this.readPosition++];
            byte b3 = span[this.readPosition++];
            byte b4 = span[this.readPosition++];

            return (b1 << 24) | (b2 << 16) | (b3 << 8) | b4;
        }

        public byte[] ReadBytes(int bytes)
        {
            var span = this.buffer.Slice(this.readPosition, bytes);

            this.readPosition += bytes;

            return span.ToArray();
        }

        public bool VerifyBytes(byte[] bytes)
        {
            var span = this.buffer.Slice(this.readPosition, bytes.Length).Span;
            this.readPosition += bytes.Length;

            for (int i = 0; i < bytes.Length; i++)
            {
                if (span[i] != bytes[i])
                    return false;
            }

            return true;
        }

        public Guid ReadGuid()
        {
            var input = this.buffer.Slice(this.readPosition).Span;
            this.readPosition += 16;

            return new Guid(new byte[] {
                input[3],
                input[2],
                input[1],
                input[0],

                input[5],
                input[4],

                input[7],
                input[6],

                input[8],
                input[9],

                input[10],
                input[11],
                input[12],
                input[13],
                input[14],
                input[15]
            });
        }

        public string ReadString(int bytes)
        {
            var span = this.buffer.Slice(this.readPosition, bytes).Span;
            int terminatorIndex = span.IndexOf((byte)0);
            if (terminatorIndex == -1)
                terminatorIndex = bytes;

            this.readPosition += bytes;

            return Encoding.UTF8.GetString(span[..terminatorIndex]);
        }

        public byte ReadByte()
        {
            return this.buffer.Span[this.readPosition++];
        }
    }
}
