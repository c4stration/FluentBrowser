using MessagePack;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FluentBrowser;

internal static class SuggestionTrainingLogFormat
{
    private static readonly byte[] HeaderBytes =
        Encoding.ASCII.GetBytes("FBLG0001");

    public const int HeaderLength = 8;
    public const int LengthPrefixSize = sizeof(int);
    public const int MaximumRecordLength = 16 * 1024 * 1024;
    public const long MaximumFileSizeBytes = 10 * 1024 * 1024;
    public const long CompactedFileSizeBytes = 8 * 1024 * 1024;

    public static bool HasHeader(byte[] data) =>
        data.Length >= HeaderLength &&
        data.AsSpan(0, HeaderLength).SequenceEqual(HeaderBytes);

    public static async Task WriteHeaderAsync(Stream stream)
    {
        await stream.WriteAsync(HeaderBytes);
    }

    public static async Task WriteRecordAsync(
        Stream stream,
        byte[] data)
    {
        if (data.Length == 0 || data.Length > MaximumRecordLength)
        {
            throw new InvalidDataException(
                $"Invalid suggestion training record size: {data.Length} bytes.");
        }

        byte[] lengthPrefix = new byte[LengthPrefixSize];

        BinaryPrimitives.WriteInt32LittleEndian(
            lengthPrefix,
            data.Length);

        await stream.WriteAsync(lengthPrefix);
        await stream.WriteAsync(data);
    }

    public static List<T> Deserialize<T>(byte[] data) =>
        Deserialize<T>(data, out _);

    public static List<T> Deserialize<T>(
        byte[] data,
        out int validLength)
    {
        var result = new List<T>();

        if (!HasHeader(data))
        {
            validLength = data.Length;
            DeserializeLegacy(data, result);
            return result;
        }

        int offset = HeaderLength;
        validLength = HeaderLength;

        while (offset < data.Length)
        {
            if (data.Length - offset < LengthPrefixSize)
                break;

            int length = BinaryPrimitives.ReadInt32LittleEndian(
                data.AsSpan(offset, LengthPrefixSize));

            int recordOffset = offset + LengthPrefixSize;

            if (length <= 0 ||
                length > MaximumRecordLength ||
                length > data.Length - recordOffset)
            {
                break;
            }

            try
            {
                var reader = new MessagePackReader(
                    new ReadOnlySequence<byte>(
                        data.AsMemory(recordOffset, length)));

                T? value = MessagePackSerializer.Deserialize<T>(
                    ref reader,
                    MessagePackData.Options);

                if (!reader.End)
                    break;

                if (value is not null)
                    result.Add(value);
            }
            catch (Exception)
            {
                // Keep all complete records before an invalid or truncated tail.
                break;
            }

            offset = recordOffset + length;
            validLength = offset;
        }

        return result;
    }

    private static void DeserializeLegacy<T>(
        byte[] data,
        List<T> result)
    {
        var reader = new MessagePackReader(
            new ReadOnlySequence<byte>(data.AsMemory()));

        while (!reader.End)
        {
            try
            {
                T? value = MessagePackSerializer.Deserialize<T>(
                    ref reader,
                    MessagePackData.Options);

                if (value is not null)
                    result.Add(value);
            }
            catch (Exception)
            {
                // Legacy logs have no framing, so an incomplete final object
                // cannot be recovered. Earlier complete objects remain usable.
                break;
            }
        }
    }
}
