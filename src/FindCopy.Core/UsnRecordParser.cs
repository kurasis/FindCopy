using System.Buffers.Binary;

namespace FindCopy.Core;

/// <summary>Validates a complete journal packet before committing identities or directory changes.</summary>
internal static class UsnRecordParser
{
    internal static bool Parse(ReadOnlySpan<byte> packet, long from, long to,
        HashSet<(ulong, ulong)>? ids, List<UsnChange>? changes, out long next)
    {
        next = 0;
        if (packet.Length < 8) return false;
        next = BinaryPrimitives.ReadInt64LittleEndian(packet);
        if (next <= from) return false;
        var pending = new List<UsnChange>();
        int position = 8;
        while (position < packet.Length)
        {
            var record = packet[position..];
            if (record.Length < 8) return false;
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(record);
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            if (length < 8 || length > record.Length || (length & 7) != 0) return false;
            record = record[..(int)length];
            ulong low, high, parentLow, parentHigh;
            long usn;
            int nameFields, minimum;
            if (version == 2 && length >= 60)
            {
                low = U64(record, 8); high = 0; parentLow = U64(record, 16); parentHigh = 0;
                usn = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
                nameFields = 56; minimum = 60;
            }
            else if (version == 3 && length >= 76)
            {
                low = U64(record, 8); high = U64(record, 16); parentLow = U64(record, 24); parentHigh = U64(record, 32);
                usn = BinaryPrimitives.ReadInt64LittleEndian(record[40..]);
                nameFields = 72; minimum = 76;
            }
            else if (version == 4 && length >= 64 && changes == null)
            {
                low = U64(record, 8); high = U64(record, 16); parentLow = parentHigh = 0;
                usn = BinaryPrimitives.ReadInt64LittleEndian(record[40..]);
                nameFields = -1; minimum = 64;
            }
            else return false;
            if (nameFields >= 0)
            {
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[nameFields..]);
                int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[(nameFields + 2)..]);
                if ((nameLength & 1) != 0 || offset < minimum || offset + nameLength > length) return false;
            }
            if ((low == 0 && high == 0) || usn < from || usn >= next) return false;
            if (usn < to)
            {
                if (changes != null)
                {
                    if ((parentLow == 0 && parentHigh == 0) || changes.Count + pending.Count >= 1_000_000) return false;
                }
                pending.Add(new UsnChange(low, high, parentLow, parentHigh));
            }
            position += (int)length;
        }
        if (position != packet.Length) return false;
        foreach (var change in pending) ids?.Add((change.FileLow, change.FileHigh));
        changes?.AddRange(pending);
        return true;
    }

    private static ulong U64(ReadOnlySpan<byte> record, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(record[offset..]);
}
