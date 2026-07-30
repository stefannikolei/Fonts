// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic.GSub;

/// <summary>
/// A Ligature Substitution (LigatureSubst) subtable identifies ligature substitutions where a single glyph replaces multiple glyphs.
/// One LigatureSubst subtable can specify any number of ligature substitutions.
/// The subtable has one format: LigatureSubstFormat1.
/// <see href="https://docs.microsoft.com/en-us/typography/opentype/spec/gsub#lookuptype-4-ligature-substitution-subtable"/>
/// </summary>
internal static class LookupType4SubTable
{
    /// <summary>
    /// Loads the ligature substitution lookup subtable from the given offset.
    /// </summary>
    /// <param name="reader">The big-endian binary reader.</param>
    /// <param name="offset">The offset to the beginning of the substitution subtable.</param>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    /// <returns>The loaded <see cref="LookupSubTable"/>.</returns>
    public static LookupSubTable Load(BigEndianBinaryReader reader, long offset, LookupFlags lookupFlags, ushort markFilteringSet)
    {
        reader.Seek(offset, SeekOrigin.Begin);
        ushort substFormat = reader.ReadUInt16();

        return substFormat switch
        {
            1 => LookupType4Format1SubTable.Load(reader, offset, lookupFlags, markFilteringSet),
            _ => new NotImplementedSubTable(),
        };
    }
}

/// <summary>
/// Implements ligature substitution format 1. A sequence of glyphs is replaced by a single
/// ligature glyph. The first glyph in the sequence is identified via the coverage table, and
/// the remaining component glyphs are specified in each ligature table.
/// <see href="https://docs.microsoft.com/en-us/typography/opentype/spec/gsub#41-ligature-substitution-format-1"/>
/// </summary>
internal sealed class LookupType4Format1SubTable : LookupSubTable
{
    /// <summary>
    /// The array of ligature set tables, ordered by coverage index.
    /// </summary>
    private readonly LigatureSetTable[] ligatureSetTables;

    /// <summary>
    /// The coverage table that defines the set of first-component glyph IDs.
    /// </summary>
    private readonly CoverageTable coverageTable;

    /// <summary>
    /// Initializes a new instance of the <see cref="LookupType4Format1SubTable"/> class.
    /// </summary>
    /// <param name="ligatureSetTables">The array of ligature set tables.</param>
    /// <param name="coverageTable">The coverage table defining first-component glyphs.</param>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    private LookupType4Format1SubTable(LigatureSetTable[] ligatureSetTables, CoverageTable coverageTable, LookupFlags lookupFlags, ushort markFilteringSet)
        : base(lookupFlags, markFilteringSet)
    {
        this.ligatureSetTables = ligatureSetTables;
        this.coverageTable = coverageTable;
    }

    /// <inheritdoc/>
    public override bool ConsumesDirectly => true;

    /// <summary>
    /// Loads the ligature substitution format 1 subtable from the given offset.
    /// </summary>
    /// <param name="reader">The big-endian binary reader.</param>
    /// <param name="offset">The offset to the beginning of the substitution subtable.</param>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    /// <returns>The loaded <see cref="LookupType4Format1SubTable"/>.</returns>
    public static LookupType4Format1SubTable Load(BigEndianBinaryReader reader, long offset, LookupFlags lookupFlags, ushort markFilteringSet)
    {
        // Ligature Substitution Format 1
        // +----------+--------------------------------------+--------------------------------------------------------------------+
        // | Type     | Name                                 | Description                                                        |
        // +==========+======================================+====================================================================+
        // | uint16   | substFormat                          | Format identifier: format = 1                                      |
        // +----------+--------------------------------------+--------------------------------------------------------------------+
        // | Offset16 | coverageOffset                       | Offset to Coverage table, from beginning of substitution           |
        // |          |                                      | subtable                                                           |
        // +----------+--------------------------------------+--------------------------------------------------------------------+
        // | uint16   | ligatureSetCount                     | Number of LigatureSet tables                                       |
        // +----------+--------------------------------------+--------------------------------------------------------------------+
        // | Offset16 | ligatureSetOffsets[ligatureSetCount] | Array of offsets to LigatureSet tables. Offsets are from beginning |
        // |          |                                      | of substitution subtable, ordered by Coverage index                |
        // +----------+--------------------------------------+--------------------------------------------------------------------+
        ushort coverageOffset = reader.ReadOffset16();
        ushort ligatureSetCount = reader.ReadUInt16();

        using Buffer<ushort> ligatureSetOffsetsBuffer = new(ligatureSetCount);
        Span<ushort> ligatureSetOffsets = ligatureSetOffsetsBuffer.GetSpan();
        reader.ReadUInt16Array(ligatureSetOffsets);

        LigatureSetTable[] ligatureSetTables = new LigatureSetTable[ligatureSetCount];
        for (int i = 0; i < ligatureSetTables.Length; i++)
        {
            // LigatureSet Table
            // +----------+--------------------------------+--------------------------------------------------------------------+
            // | Type     | Name                           | Description                                                        |
            // +==========+================================+====================================================================+
            // | uint16   | ligatureCount                  | Number of Ligature tables                                          |
            // +----------+--------------------------------+--------------------------------------------------------------------+
            // | Offset16 | ligatureOffsets[LigatureCount] | Array of offsets to Ligature tables. Offsets are from beginning of |
            // |          |                                | LigatureSet table, ordered by preference.                          |
            // +----------+--------------------------------+--------------------------------------------------------------------+
            long ligatureSetOffset = offset + ligatureSetOffsets[i];
            reader.Seek(ligatureSetOffset, SeekOrigin.Begin);
            ushort ligatureCount = reader.ReadUInt16();

            using Buffer<ushort> ligatureOffsetsBuffer = new(ligatureCount);
            Span<ushort> ligatureOffsets = ligatureOffsetsBuffer.GetSpan();
            reader.ReadUInt16Array(ligatureOffsets);

            LigatureTable[] ligatureTables = new LigatureTable[ligatureCount];

            // Ligature Table
            // +--------+---------------------------------------+------------------------------------------------------+
            // | Type   | Name                                  | Description                                          |
            // +========+=======================================+======================================================+
            // | uint16 | ligatureGlyph                         | glyph ID of ligature to substitute                   |
            // +--------+---------------------------------------+------------------------------------------------------+
            // | uint16 | componentCount                        | Number of components in the ligature                 |
            // +--------+---------------------------------------+------------------------------------------------------+
            // | uint16 | componentGlyphIDs[componentCount - 1] | Array of component glyph IDs — start with the second |
            // |        |                                       | component, ordered in writing direction              |
            // +--------+---------------------------------------+------------------------------------------------------+
            for (int j = 0; j < ligatureTables.Length; j++)
            {
                reader.Seek(ligatureSetOffset + ligatureOffsets[j], SeekOrigin.Begin);
                ushort ligatureGlyph = reader.ReadUInt16();
                ushort componentCount = reader.ReadUInt16();
                ushort[] componentGlyphIds = reader.ReadUInt16Array(componentCount - 1);
                ligatureTables[j] = new LigatureTable(ligatureGlyph, componentGlyphIds);
            }

            ligatureSetTables[i] = new LigatureSetTable(ligatureTables);
        }

        CoverageTable coverageTable = CoverageTable.Load(reader, offset + coverageOffset);

        return new LookupType4Format1SubTable(ligatureSetTables, coverageTable, lookupFlags, markFilteringSet);
    }

    /// <inheritdoc/>
    public override void CollectDigest(ref GlyphSetDigest digest) => this.coverageTable.CollectDigest(ref digest);

    /// <inheritdoc/>
    public override bool TrySubstitution(
        FontMetrics fontMetrics,
        GSubTable table,
        ShapingBuffer buffer,
        Tag feature,
        uint lookupMask,
        int index,
        int count)
    {
        ushort glyphId = buffer[index].GlyphId;
        if (glyphId == 0)
        {
            return false;
        }

        int offset = this.coverageTable.CoverageIndexOf(glyphId);
        if (offset < 0 || offset >= this.ligatureSetTables.Length)
        {
            return false;
        }

        LigatureSetTable ligatureSetTable = this.ligatureSetTables[offset];
        SkippingGlyphIterator iterator = new(fontMetrics, buffer, index, this.LookupFlags, this.MarkFilteringSet);
        Span<int> matchBuffer = buffer.GetContextMatchPositions()[..AdvancedTypographicUtils.MaxContextLength];
        for (int i = 0; i < ligatureSetTable.Ligatures.Length; i++)
        {
            LigatureTable ligatureTable = ligatureSetTable.Ligatures[i];
            int remaining = count - 1;
            int compLength = ligatureTable.ComponentGlyphs.Length;
            if (compLength > remaining)
            {
                continue;
            }

            if (!AdvancedTypographicUtils.MatchInputSequence(iterator, lookupMask, 1, ligatureTable.ComponentGlyphs, matchBuffer))
            {
                continue;
            }

            Span<int> matches = matchBuffer[..Math.Min(ligatureTable.ComponentGlyphs.Length, matchBuffer.Length)];
            AdvancedTypographicUtils.ApplyLigatureSubstitution(fontMetrics, buffer, index, matches, ligatureTable.GlyphId, feature, count);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public override bool WouldApply(ReadOnlySpan<ushort> glyphs, bool zeroContext)
    {
        int offset = this.coverageTable.CoverageIndexOf(glyphs[0]);
        if (offset < 0 || offset >= this.ligatureSetTables.Length)
        {
            return false;
        }

        foreach (LigatureTable ligature in this.ligatureSetTables[offset].Ligatures)
        {
            ushort[] components = ligature.ComponentGlyphs;
            if (components.Length + 1 != glyphs.Length)
            {
                continue;
            }

            bool matched = true;
            for (int i = 0; i < components.Length; i++)
            {
                if (glyphs[i + 1] != components[i])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Represents a ligature set table containing an array of ligature tables
    /// for a single first-component glyph, ordered by preference.
    /// </summary>
    public readonly struct LigatureSetTable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LigatureSetTable"/> struct.
        /// </summary>
        /// <param name="ligatures">The array of ligature tables.</param>
        public LigatureSetTable(LigatureTable[] ligatures)
            => this.Ligatures = ligatures;

        /// <summary>
        /// Gets the array of ligature tables, ordered by preference.
        /// </summary>
        public LigatureTable[] Ligatures { get; }
    }

    /// <summary>
    /// Represents a ligature table that maps a sequence of component glyphs to a single
    /// ligature glyph.
    /// </summary>
    public readonly struct LigatureTable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LigatureTable"/> struct.
        /// </summary>
        /// <param name="glyphId">The glyph ID of the ligature to substitute.</param>
        /// <param name="componentGlyphs">The array of component glyph IDs (starting with the second component).</param>
        public LigatureTable(ushort glyphId, ushort[] componentGlyphs)
        {
            this.GlyphId = glyphId;
            this.ComponentGlyphs = componentGlyphs;
        }

        /// <summary>
        /// Gets the glyph ID of the ligature to substitute.
        /// </summary>
        public ushort GlyphId { get; }

        /// <summary>
        /// Gets the array of component glyph IDs, starting with the second component,
        /// ordered in writing direction.
        /// </summary>
        public ushort[] ComponentGlyphs { get; }
    }
}
