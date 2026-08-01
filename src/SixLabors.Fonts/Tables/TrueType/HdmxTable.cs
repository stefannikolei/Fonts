// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.TrueType;

/// <summary>
/// Represents the 'hdmx' (Horizontal Device Metrics) table, which stores precomputed
/// integer advance widths for specific pixel sizes so device advances can be resolved
/// without executing the hinting programs that produced them.
/// <see href="https://learn.microsoft.com/en-us/typography/opentype/spec/hdmx"/>
/// </summary>
internal sealed class HdmxTable : Table
{
    /// <summary>
    /// The table tag name.
    /// </summary>
    internal const string TableName = "hdmx";

    private readonly byte[] recordIndex;
    private readonly byte[] widths;
    private readonly int glyphCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="HdmxTable"/> class.
    /// </summary>
    /// <param name="pixelSizes">The pixel size of each device record, in ascending order.</param>
    /// <param name="widths">The advance widths for every record, flattened as records by glyph id.</param>
    /// <param name="glyphCount">The number of glyphs per device record.</param>
    public HdmxTable(byte[] pixelSizes, byte[] widths, int glyphCount)
    {
        this.widths = widths;
        this.glyphCount = glyphCount;

        // Pixel sizes are bytes, so a direct 256 entry map gives constant time lookups.
        // Entries store the record index plus one; zero marks an uncovered size.
        this.recordIndex = new byte[256];
        for (int i = 0; i < pixelSizes.Length && i < byte.MaxValue; i++)
        {
            this.recordIndex[pixelSizes[i]] = (byte)(i + 1);
        }
    }

    /// <summary>
    /// Attempts to resolve the device advance width for the given glyph at the given pixel size.
    /// </summary>
    /// <param name="ppem">The pixels per em to look up.</param>
    /// <param name="glyphId">The glyph identifier.</param>
    /// <param name="advance">The advance width in whole device pixels.</param>
    /// <returns><see langword="true"/> if the table carries a record for the size; otherwise, <see langword="false"/>.</returns>
    public bool TryGetAdvance(int ppem, ushort glyphId, out byte advance)
    {
        advance = 0;
        if (glyphId >= this.glyphCount || (uint)ppem >= 256)
        {
            return false;
        }

        int record = this.recordIndex[ppem];
        if (record == 0)
        {
            return false;
        }

        advance = this.widths[((record - 1) * this.glyphCount) + glyphId];
        return true;
    }

    /// <summary>
    /// Loads the 'hdmx' table from the specified font reader.
    /// </summary>
    /// <param name="fontReader">The font reader.</param>
    /// <param name="glyphCount">The number of glyphs in the font, from the 'maxp' table.</param>
    /// <returns>The <see cref="HdmxTable"/>, or <see langword="null"/> when absent or malformed.</returns>
    public static HdmxTable? Load(FontReader fontReader, ushort glyphCount)
    {
        if (!fontReader.TryGetReaderAtTablePosition(TableName, out BigEndianBinaryReader? binaryReader, out TableHeader? header))
        {
            return null;
        }

        using (binaryReader)
        {
            return Load(binaryReader, header.Length, glyphCount);
        }
    }

    /// <summary>
    /// Loads the 'hdmx' table from the specified binary reader.
    /// </summary>
    /// <param name="reader">The big-endian binary reader positioned at the start of the table.</param>
    /// <param name="tableLength">The length of the table in bytes.</param>
    /// <param name="glyphCount">The number of glyphs in the font, from the 'maxp' table.</param>
    /// <returns>The <see cref="HdmxTable"/>, or <see langword="null"/> when malformed.</returns>
    public static HdmxTable? Load(BigEndianBinaryReader reader, uint tableLength, ushort glyphCount)
    {
        // HEADER
        // Type           | Name             | Description
        // ---------------|------------------|-----------------------------------------------
        // uint16         | version          | Table version number (0).
        // uint16         | numRecords       | Number of device records.
        // uint32         | sizeDeviceRecord | Size of a device record, 32-bit aligned.
        // DeviceRecord[] | records          | Array of device records.
        //
        // DEVICE RECORD
        // uint8    | pixelSize | Pixel size for the following widths.
        // uint8    | maxWidth  | Maximum width across all glyphs.
        // uint8[n] | widths    | Advance width per glyph id, n from 'maxp'.
        reader.ReadUInt16();
        int numRecords = reader.ReadUInt16();
        uint sizeDeviceRecord = reader.ReadUInt32();

        // The table is a device optimization only, so malformed data is discarded rather
        // than failing the font load.
        uint minimumRecordSize = 2U + glyphCount;
        if (numRecords <= 0 || sizeDeviceRecord < minimumRecordSize || 8 + ((ulong)numRecords * sizeDeviceRecord) > tableLength)
        {
            return null;
        }

        int padding = (int)(sizeDeviceRecord - minimumRecordSize);
        byte[] pixelSizes = new byte[numRecords];
        byte[] widths = new byte[numRecords * glyphCount];
        for (int i = 0; i < numRecords; i++)
        {
            pixelSizes[i] = reader.ReadUInt8();
            reader.ReadUInt8();
            reader.ReadUInt8Array(glyphCount).CopyTo(widths, i * glyphCount);
            if (padding > 0)
            {
                reader.ReadBytes(padding);
            }
        }

        return new HdmxTable(pixelSizes, widths, glyphCount);
    }
}
