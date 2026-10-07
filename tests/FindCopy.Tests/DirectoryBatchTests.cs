using System.Buffers.Binary;
using System.Text;
using FindCopy.Core;

static class DirectoryBatchTests
{
    public static void Run(Action<string, Action> test)
    {
        test("S1 directory batches accept Unicode names and complete linked records", () =>
        {
            byte[] first = Record("данные-😀.bin"), second = Record(new string('x', 255));
            Require(DirectoryBatchValidator.IsValid(first), "valid Unicode record rejected");
            BinaryPrimitives.WriteUInt32LittleEndian(first, (uint)first.Length);
            Require(DirectoryBatchValidator.IsValid(first.Concat(second).ToArray()), "valid record chain rejected");
        });
        test("S2 directory batches reject offsets that wrap or escape the buffer", () =>
        {
            foreach (uint next in new uint[] { 0xfffffff8, 0xffffffc0, 65536, 96, 88, 93 })
            {
                byte[] record = Record("f");
                BinaryPrimitives.WriteUInt32LittleEndian(record, next);
                Require(!DirectoryBatchValidator.IsValid(record), $"unsafe offset accepted: {next:X8}");
            }
            byte[] first = Record("f"), second = Record("g");
            BinaryPrimitives.WriteUInt32LittleEndian(first, (uint)first.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(second, 0xfffffff8);
            Require(!DirectoryBatchValidator.IsValid(first.Concat(second).ToArray()), "overflow in a later record accepted");
        });
        test("S3 directory batches reject truncated headers and invalid name lengths", () =>
        {
            Require(!DirectoryBatchValidator.IsValid(new byte[87]), "truncated header accepted");
            foreach (int length in new[] { -2, 0, 1, 10, 1022, int.MaxValue })
            {
                byte[] record = Record("f");
                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(60), length);
                Require(!DirectoryBatchValidator.IsValid(record), "invalid name length accepted: " + length);
            }
        });
    }

    private static byte[] Record(string name)
    {
        byte[] encoded = Encoding.Unicode.GetBytes(name);
        var record = new byte[(88 + encoded.Length + 7) & ~7];
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(60), encoded.Length);
        encoded.CopyTo(record.AsSpan(88));
        return record;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
