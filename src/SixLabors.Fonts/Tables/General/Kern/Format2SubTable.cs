// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.General.Kern;

/// <summary>
/// Represents class-pair kerning in a format 2 'kern' subtable.
/// </summary>
/// <remarks>
/// Format 2 is defined by the
/// <see href="https://learn.microsoft.com/en-us/typography/opentype/spec/kern#format-2">OpenType 'kern' specification</see>
/// and the
/// <see href="https://developer.apple.com/fonts/TrueType-Reference-Manual/RM06/Chap6kern.html">Apple TrueType Reference Manual</see>.
/// This implementation follows Apple's subtable-relative class-offset interpretation.
/// </remarks>
internal sealed class Format2SubTable : KerningSubTable
{
    /// <summary>
    /// The first glyph covered by the left-hand class offsets.
    /// </summary>
    private readonly ushort firstLeftGlyph;

    /// <summary>
    /// The first glyph covered by the right-hand class offsets.
    /// </summary>
    private readonly ushort firstRightGlyph;

    /// <summary>
    /// Byte offsets selecting a row for each covered left-hand glyph.
    /// </summary>
    private readonly ushort[] leftClassOffsets;

    /// <summary>
    /// Byte offsets selecting a column for each covered right-hand glyph.
    /// </summary>
    private readonly ushort[] rightClassOffsets;

    /// <summary>
    /// The byte offset from the subtable start to the kerning value array.
    /// </summary>
    private readonly ushort arrayOffset;

    /// <summary>
    /// The class-pair kerning values.
    /// </summary>
    private readonly short[] values;

    /// <summary>
    /// Initializes a new instance of the <see cref="Format2SubTable"/> class.
    /// </summary>
    /// <param name="firstLeftGlyph">The first glyph covered by the left-hand class offsets.</param>
    /// <param name="leftClassOffsets">The left-hand class offsets.</param>
    /// <param name="firstRightGlyph">The first glyph covered by the right-hand class offsets.</param>
    /// <param name="rightClassOffsets">The right-hand class offsets.</param>
    /// <param name="arrayOffset">The byte offset to the kerning value array.</param>
    /// <param name="values">The class-pair kerning values.</param>
    /// <param name="coverage">The coverage flags for this subtable.</param>
    private Format2SubTable(ushort firstLeftGlyph, ushort[] leftClassOffsets, ushort firstRightGlyph, ushort[] rightClassOffsets, ushort arrayOffset, short[] values, KerningCoverage coverage)
        : base(coverage)
    {
        this.firstLeftGlyph = firstLeftGlyph;
        this.leftClassOffsets = leftClassOffsets;
        this.firstRightGlyph = firstRightGlyph;
        this.rightClassOffsets = rightClassOffsets;
        this.arrayOffset = arrayOffset;
        this.values = values;
    }

    /// <summary>
    /// Loads class-pair kerning from the specified binary reader.
    /// </summary>
    /// <param name="reader">The reader positioned after the shared subtable header.</param>
    /// <param name="subtableOffset">The table-relative offset of the subtable.</param>
    /// <param name="subtableLength">The length of the subtable in bytes.</param>
    /// <param name="coverage">The coverage flags for this subtable.</param>
    /// <returns>The loaded class-pair kerning subtable.</returns>
    public static Format2SubTable Load(BigEndianBinaryReader reader, long subtableOffset, uint subtableLength, in KerningCoverage coverage)
    {
        // Row width is redundant at lookup time because the left-hand class values already contain complete row offsets.
        _ = reader.ReadUInt16();
        ushort leftClassTableOffset = reader.ReadOffset16();
        ushort rightClassTableOffset = reader.ReadOffset16();
        ushort arrayOffset = reader.ReadOffset16();

        reader.Seek(subtableOffset + leftClassTableOffset, SeekOrigin.Begin);
        ushort firstLeftGlyph = reader.ReadUInt16();
        ushort[] leftClassOffsets = reader.ReadUInt16Array(reader.ReadUInt16());

        reader.Seek(subtableOffset + rightClassTableOffset, SeekOrigin.Begin);
        ushort firstRightGlyph = reader.ReadUInt16();
        ushort[] rightClassOffsets = reader.ReadUInt16Array(reader.ReadUInt16());

        reader.Seek(subtableOffset + arrayOffset, SeekOrigin.Begin);
        short[] values = reader.ReadInt16Array(checked((int)((subtableLength - arrayOffset) / sizeof(short))));

        return new Format2SubTable(firstLeftGlyph, leftClassOffsets, firstRightGlyph, rightClassOffsets, arrayOffset, values, coverage);
    }

    /// <inheritdoc/>
    protected override bool TryGetOffset(ushort index1, ushort index2, out short offset)
    {
        int leftIndex = index1 - this.firstLeftGlyph;
        int rightIndex = index2 - this.firstRightGlyph;
        ushort leftOffset = (uint)leftIndex < (uint)this.leftClassOffsets.Length ? this.leftClassOffsets[leftIndex] : (ushort)0;
        ushort rightOffset = (uint)rightIndex < (uint)this.rightClassOffsets.Length ? this.rightClassOffsets[rightIndex] : (ushort)0;
        int valueIndex = (leftOffset + rightOffset - this.arrayOffset) / sizeof(short);

        if ((uint)valueIndex < (uint)this.values.Length)
        {
            offset = this.values[valueIndex];
            return true;
        }

        offset = 0;
        return false;
    }
}
