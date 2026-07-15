using System;
using System.Text;

namespace Haukcode.sACN.Model
{
    /// <summary>
    /// Zero-allocation big-endian writer for the packet serialization hot path. The class-based
    /// BigEndianBinaryWriter cost three allocations per data packet (one per layer) plus two
    /// array allocations inside WriteGuid — ~6.5 MB/s of young garbage at 600 universes / 60 Hz.
    /// This ref struct lives on the stack and writes the GUID without intermediate arrays.
    /// </summary>
    internal ref struct SpanBinaryWriter
    {
        private readonly Span<byte> buffer;
        private int writePosition;

        public SpanBinaryWriter(Span<byte> buffer)
        {
            this.buffer = buffer;
            this.writePosition = 0;
        }

        public int BytesWritten => this.writePosition;

        public void WriteByte(byte value)
        {
            this.buffer[this.writePosition++] = value;
        }

        public void WriteInt16(short value)
        {
            // High byte, Low byte
            this.buffer[this.writePosition++] = (byte)(value >> 8);
            this.buffer[this.writePosition++] = (byte)value;
        }

        public void WriteUInt16(ushort value)
        {
            // High byte, Low byte
            this.buffer[this.writePosition++] = (byte)(value >> 8);
            this.buffer[this.writePosition++] = (byte)value;
        }

        public void WriteInt32(int value)
        {
            this.buffer[this.writePosition++] = (byte)(value >> 24);
            this.buffer[this.writePosition++] = (byte)(value >> 16);
            this.buffer[this.writePosition++] = (byte)(value >> 8);
            this.buffer[this.writePosition++] = (byte)value;
        }

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(this.buffer.Slice(this.writePosition));

            this.writePosition += bytes.Length;
        }

        public void WriteString(string value, int length)
        {
            // Encode the string directly into the buffer
            int bytesWritten = Encoding.UTF8.GetBytes(value, this.buffer.Slice(this.writePosition, length));

            // Fill the remaining bytes with zero
            this.buffer.Slice(this.writePosition + bytesWritten, length - bytesWritten).Clear();

            this.writePosition += length;
        }

        public void WriteGuid(Guid value)
        {
            // RFC 4122 wire order = big-endian, with no intermediate arrays.
            value.TryWriteBytes(this.buffer.Slice(this.writePosition, 16), bigEndian: true, out _);

            this.writePosition += 16;
        }
    }
}
