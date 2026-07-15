using System;
using System.Diagnostics;
using System.IO;

namespace Haukcode.sACN.Model
{
    public class FramingOptions
    {
        public const byte PREVIEW_DATA = 0b1000_0000;
        public const byte STREAM_TERMINATED = 0b0100_0000;
        public const byte FORCE_SYNCHRONIZATION = 0b0010_0000;

        public bool PreviewData { get; set; }

        public bool StreamTerminated { get; set; }

        public bool ForceSynchronization { get; set; }

        public FramingOptions()
        {
        }

        public static FramingOptions Parse(byte optionsByte)
        {
            var options = new FramingOptions();
            options.SetFrom(optionsByte);

            return options;
        }

        /// <summary>
        /// Overwrite all flags from the wire byte, so a reused instance carries no state from a
        /// previous packet.
        /// </summary>
        public void SetFrom(byte optionsByte)
        {
            ForceSynchronization = (optionsByte & FORCE_SYNCHRONIZATION) != 0;
            StreamTerminated = (optionsByte & STREAM_TERMINATED) != 0;
            PreviewData = (optionsByte & PREVIEW_DATA) != 0;
        }

        public byte ToByte()
        {
            byte returnVal = 0;

            if (PreviewData)
            {
                returnVal = (byte)(returnVal | PREVIEW_DATA);
            }
            if (StreamTerminated)
            {
                returnVal = (byte)(returnVal | STREAM_TERMINATED);
            }
            if (ForceSynchronization)
            {
                returnVal = (byte)(returnVal | FORCE_SYNCHRONIZATION);
            }

            return returnVal;
        }
    }
}
