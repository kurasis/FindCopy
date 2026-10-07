using System.Buffers.Binary;

namespace FindCopy.Core;

/// <summary>Checks FILE_ID_EXTD_DIR_INFO offsets before the native reader dereferences them.</summary>
internal static class DirectoryBatchValidator
{
    internal static bool IsValid(ReadOnlySpan<byte> buffer)
    {
        const int HeaderSize = 88;
        int offset = 0;
        while (offset <= buffer.Length - HeaderSize)
        {
            var record = buffer[offset..];
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(record);
            int nameBytes = BinaryPrimitives.ReadInt32LittleEndian(record[60..]);
            if (nameBytes <= 0 || (nameBytes & 1) != 0 || nameBytes > 255 * 2 * 2 ||
                nameBytes > record.Length - HeaderSize) return false;
            if (next == 0) return true;
            // Bound unsigned input before conversion/addition; next must leave a complete header.
            if (next < HeaderSize + nameBytes || (next & 7) != 0 ||
                next > (uint)(record.Length - HeaderSize)) return false;
            offset += (int)next;
        }
        return false;
    }
}
