using System.IO;
using System.Text;

namespace MeetingRecorder.Audio;

internal static class WavRepair
{
    public static long ReadRecordedSamples(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(file, Encoding.ASCII);
        if (file.Length < 44 || reader.ReadUInt32() != 0x46464952)
            throw new InvalidDataException("File does not have a complete RIFF/WAVE header.");
        file.Position = 8;
        if (reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("File is not WAVE.");
        var validFormat = false;
        while (file.Position <= file.Length - 8)
        {
            var id = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            var next = file.Position + size + (size & 1);
            if (id == 0x61746164)
            {
                if (!validFormat) throw new InvalidDataException("Recording is not 16 kHz mono 16-bit PCM.");
                // Working WAVs have audio to EOF; crash-stale RIFF/data lengths are deliberately ignored.
                return (file.Length - file.Position) / 2;
            }
            if (next > file.Length) throw new InvalidDataException("WAVE chunk extends beyond the recording.");
            if (id == 0x20746d66 && size >= 16)
                validFormat = reader.ReadUInt16() == 1 && reader.ReadUInt16() == 1
                    && reader.ReadUInt32() == 16000 && reader.ReadUInt32() == 32000
                    && reader.ReadUInt16() == 2 && reader.ReadUInt16() == 16;
            file.Position = next;
        }
        throw new InvalidDataException("Recording has no data chunk.");
    }

    public static void Fix(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(file, Encoding.ASCII, leaveOpen: true);
        if (file.Length < 44 || reader.ReadUInt32() != 0x46464952)
            throw new InvalidDataException("File does not have a complete RIFF/WAVE header.");

        file.Position = 8;
        if (reader.ReadUInt32() != 0x45564157)
            throw new InvalidDataException("File is not a WAVE recording.");
        if (file.Length - 8 > uint.MaxValue)
            throw new InvalidDataException("Recording exceeds the RIFF size limit.");

        while (file.Position <= file.Length - 8)
        {
            var id = reader.ReadUInt32();
            var sizeOffset = file.Position;
            var size = reader.ReadUInt32();
            if (id == 0x61746164)
            {
                var dataLength = file.Length - file.Position;
                dataLength -= dataLength % 2;
                using var writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true);
                file.Position = sizeOffset;
                writer.Write((uint)dataLength);
                file.Position = 4;
                writer.Write((uint)(file.Length - 8));
                return;
            }

            var next = file.Position + size + (size & 1);
            if (next > file.Length)
                throw new InvalidDataException("WAVE chunk extends beyond the recording.");
            file.Position = next;
        }

        throw new InvalidDataException("Recording has no data chunk.");
    }
}
