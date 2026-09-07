using System.Text;
using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.Cryptography;

internal static class SyncPayloadBinaryCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private const int MaximumUrlBytes = 16_384 * 4;
    private const int MaximumTitleBytes = 1_024 * 4;
    private const int MaximumGroupLabelBytes = 256 * 4;
    private const int MaximumSettingTextBytes = 256 * 4;
    private const uint RecordMagic = 0x4F4E5352; // ONSR
    private const uint TombstoneMagic = 0x4F4E5354; // ONST
    private const uint PurgeMagic = 0x4F4E5350; // ONSP
    private const byte FormatVersion = 1;

    public static byte[] SerializeRecord(SyncRecordPayload record)
    {
        using var stream = new ZeroingMemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(RecordMagic);
        writer.Write(FormatVersion);
        writer.Write((byte)record.Category);
        WriteGuid(writer, record.EntityId.Value);
        writer.Write(record.Revision);
        writer.Write(record.ModifiedAtUtc.UtcTicks);

        switch (record)
        {
            case HistorySyncRecord history:
                WriteString(writer, history.AbsoluteUrl);
                WriteString(writer, history.Title);
                writer.Write(history.LastVisitedAtUtc.UtcTicks);
                writer.Write(history.VisitCount);
                break;

            case OpenTabSyncRecord tab:
                WriteString(writer, tab.AbsoluteUrl);
                WriteString(writer, tab.Title);
                writer.Write(tab.Position);
                writer.Write(tab.GroupLabel is not null);
                if (tab.GroupLabel is not null)
                    WriteString(writer, tab.GroupLabel);
                break;

            case SettingsSyncRecord settings:
                writer.Write((byte)settings.Scope);
                writer.Write(settings.Settings.Count);
                foreach (var entry in settings.Settings)
                {
                    writer.Write((byte)entry.Field);
                    switch (entry.Value)
                    {
                        case BooleanSyncSettingValue boolean:
                            writer.Write((byte)1);
                            writer.Write(boolean.Value);
                            break;
                        case IntegerSyncSettingValue integer:
                            writer.Write((byte)2);
                            writer.Write(integer.Value);
                            break;
                        case DecimalSyncSettingValue decimalValue:
                            writer.Write((byte)3);
                            foreach (var part in decimal.GetBits(decimalValue.Value))
                                writer.Write(part);
                            break;
                        case TextSyncSettingValue text:
                            writer.Write((byte)4);
                            WriteString(writer, text.Value);
                            break;
                        default:
                            throw new InvalidDataException("Unsupported sync setting value type.");
                    }
                }

                break;

            default:
                throw new InvalidDataException("Unsupported sync record type.");
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static SyncRecordPayload DeserializeRecord(
        byte[] plaintext,
        SyncDataCategory expectedCategory)
    {
        using var stream = new MemoryStream(plaintext, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        RequireHeader(reader, RecordMagic);
        var category = ReadDefinedEnum<SyncDataCategory>(reader.ReadByte());
        if (category != expectedCategory)
            throw new InvalidDataException("Record category does not match authenticated metadata.");

        var entityId = new SyncEntityId(ReadGuid(reader));
        var revision = reader.ReadInt64();
        var modifiedAtUtc = ReadUtc(reader);

        SyncRecordPayload record = category switch
        {
            SyncDataCategory.History => new HistorySyncRecord(
                entityId,
                revision,
                modifiedAtUtc,
                ReadString(reader, MaximumUrlBytes),
                ReadString(reader, MaximumTitleBytes),
                ReadUtc(reader),
                reader.ReadInt32()),

            SyncDataCategory.OpenTabs => new OpenTabSyncRecord(
                entityId,
                revision,
                modifiedAtUtc,
                ReadString(reader, MaximumUrlBytes),
                ReadString(reader, MaximumTitleBytes),
                reader.ReadInt32(),
                ReadBoolean(reader) ? ReadString(reader, MaximumGroupLabelBytes) : null),

            SyncDataCategory.Settings => ReadSettings(reader, entityId, revision, modifiedAtUtc),
            _ => throw new InvalidDataException("Unsupported sync category."),
        };

        RequireEnd(stream);
        return record;
    }

    public static byte[] SerializeTombstone(CanonicalSyncAad aad)
    {
        using var stream = new ZeroingMemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(TombstoneMagic);
        writer.Write(FormatVersion);
        writer.Write((byte)aad.Category);
        WriteGuid(writer, aad.EntityId.Value);
        writer.Write(aad.ClientGeneration);
        writer.Flush();
        return stream.ToArray();
    }

    public static AuthenticatedSyncTombstoneReceipt DeserializeTombstone(
        byte[] plaintext,
        CanonicalSyncAad aad)
    {
        using var stream = new MemoryStream(plaintext, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        RequireHeader(reader, TombstoneMagic);
        var category = ReadDefinedEnum<SyncDataCategory>(reader.ReadByte());
        var entityId = new SyncEntityId(ReadGuid(reader));
        var clientGeneration = reader.ReadInt64();
        RequireEnd(stream);

        if (category != aad.Category ||
            entityId != aad.EntityId ||
            clientGeneration != aad.ClientGeneration)
        {
            throw new InvalidDataException("Tombstone marker does not match authenticated metadata.");
        }

        return new AuthenticatedSyncTombstoneReceipt(
            aad.ProfileId,
            aad.DeviceId,
            aad.KeysetId,
            aad.KeyEpoch,
            aad.EnvelopeId,
            aad.Category,
            aad.EntityId,
            aad.ClientGeneration,
            aad.ClientSequence);
    }

    public static byte[] SerializePurge(DecryptedPurgeMarker marker)
    {
        using var stream = new ZeroingMemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(PurgeMagic);
        writer.Write(FormatVersion);
        WriteGuid(writer, marker.OperationId.Value);
        WriteGuid(writer, marker.ProfileId.Value);
        writer.Write((byte)marker.Category);
        writer.Write(marker.ClientGeneration);
        WriteGuid(writer, marker.KeysetId.Value);
        writer.Flush();
        return stream.ToArray();
    }

    public static DecryptedPurgeMarker DeserializePurge(byte[] plaintext)
    {
        using var stream = new MemoryStream(plaintext, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        RequireHeader(reader, PurgeMagic);
        var marker = new DecryptedPurgeMarker(
            new SyncOperationId(ReadGuid(reader)),
            new ProfileId(ReadGuid(reader)),
            ReadDefinedEnum<SyncDataCategory>(reader.ReadByte()),
            reader.ReadInt64(),
            new SyncKeysetId(ReadGuid(reader)));
        RequireEnd(stream);
        return marker;
    }

    private static SettingsSyncRecord ReadSettings(
        BinaryReader reader,
        SyncEntityId entityId,
        long revision,
        DateTimeOffset modifiedAtUtc)
    {
        var scope = ReadDefinedEnum<SyncSettingPersistenceScope>(reader.ReadByte());
        var count = reader.ReadInt32();
        if (count < 0 || count > SyncAllowlist.AllowedSettingFields.Count)
            throw new InvalidDataException("Invalid sync setting count.");

        var settings = new List<SyncSettingEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var field = ReadDefinedEnum<SyncableSettingField>(reader.ReadByte());
            var value = reader.ReadByte() switch
            {
                1 => new BooleanSyncSettingValue(ReadBoolean(reader)) as SyncSettingValue,
                2 => new IntegerSyncSettingValue(reader.ReadInt32()),
                3 => new DecimalSyncSettingValue(new decimal(
                    new[]
                    {
                        reader.ReadInt32(),
                        reader.ReadInt32(),
                        reader.ReadInt32(),
                        reader.ReadInt32(),
                    })),
                4 => new TextSyncSettingValue(ReadString(reader, MaximumSettingTextBytes)),
                _ => throw new InvalidDataException("Invalid sync setting value type."),
            };
            settings.Add(new SyncSettingEntry(field, value));
        }

        return new SettingsSyncRecord(entityId, revision, modifiedAtUtc, scope, settings);
    }

    private static void RequireHeader(BinaryReader reader, uint expectedMagic)
    {
        if (reader.ReadUInt32() != expectedMagic || reader.ReadByte() != FormatVersion)
            throw new InvalidDataException("Unsupported encrypted payload format.");
    }

    private static void RequireEnd(Stream stream)
    {
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Encrypted payload contains trailing data.");
    }

    private static TEnum ReadDefinedEnum<TEnum>(byte value)
        where TEnum : struct, Enum
    {
        var result = (TEnum)Enum.ToObject(typeof(TEnum), value);
        return Enum.IsDefined(result)
            ? result
            : throw new InvalidDataException("Encrypted payload contains an unknown enum value.");
    }

    private static DateTimeOffset ReadUtc(BinaryReader reader)
    {
        var ticks = reader.ReadInt64();
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("Encrypted payload contains an invalid timestamp.");
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static void WriteGuid(BinaryWriter writer, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out var bytesWritten) || bytesWritten != bytes.Length)
            throw new InvalidDataException("Could not encode identifier.");
        writer.Write(bytes);
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (reader.Read(bytes) != bytes.Length)
            throw new EndOfStreamException();
        return new Guid(bytes, bigEndian: true);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string ReadString(BinaryReader reader, int maximumBytes)
    {
        var length = reader.ReadInt32();
        var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        if (length < 0 || length > maximumBytes || length > remaining)
            throw new InvalidDataException("Encrypted payload contains an invalid string length.");

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool ReadBoolean(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("Encrypted payload contains an invalid Boolean value."),
    };

    private sealed class ZeroingMemoryStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing && TryGetBuffer(out var buffer) && buffer.Array is not null)
                CryptographicOperations.ZeroMemory(buffer.AsSpan());
            base.Dispose(disposing);
        }
    }
}
