// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic.GSub;

/// <summary>
/// The headers of the GSUB and GPOS tables contain offsets to Lookup List tables (LookupList) for
/// glyph substitution (GSUB table) and glyph positioning (GPOS table). The LookupList table contains
/// an array of offsets to Lookup tables (lookupOffsets).
/// <see href="https://docs.microsoft.com/en-us/typography/opentype/spec/chapter2#lookup-list-table"/>
/// </summary>
internal sealed class LookupListTable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LookupListTable"/> class.
    /// </summary>
    /// <param name="lookupCount">The number of lookups in this table.</param>
    /// <param name="lookupTables">The array of lookup tables.</param>
    private LookupListTable(ushort lookupCount, LookupTable[] lookupTables)
    {
        this.LookupCount = lookupCount;
        this.LookupTables = lookupTables;
    }

    /// <summary>
    /// Gets the number of lookups in this table.
    /// </summary>
    public ushort LookupCount { get; }

    /// <summary>
    /// Gets the array of lookup tables.
    /// </summary>
    public LookupTable[] LookupTables { get; }

    /// <summary>
    /// Loads the <see cref="LookupListTable"/> from the binary reader at the given offset.
    /// </summary>
    /// <param name="reader">The big-endian binary reader.</param>
    /// <param name="offset">The offset to the beginning of the lookup list table.</param>
    /// <returns>The loaded <see cref="LookupListTable"/>.</returns>
    public static LookupListTable Load(BigEndianBinaryReader reader, long offset)
    {
        // +----------+----------------------------+---------------------------------------------------------------+
        // | Type     | Name                       | Description                                                   |
        // +==========+============================+===============================================================+
        // | uint16   | lookupCount                | Number of lookups in this table                               |
        // +----------+----------------------------+---------------------------------------------------------------+
        // | Offset16 | lookupOffsets[lookupCount] | Array of offsets to Lookup tables, from beginning             |
        // |          |                            | of LookupList — zero based (first lookup is Lookup index = 0) |
        // +----------+----------------------------+---------------------------------------------------------------+
        reader.Seek(offset, SeekOrigin.Begin);

        ushort lookupCount = reader.ReadUInt16();
        using Buffer<ushort> lookupOffsetsBuffer = new(lookupCount);
        Span<ushort> lookupOffsets = lookupOffsetsBuffer.GetSpan();
        reader.ReadUInt16Array(lookupOffsets);

        LookupTable[] lookupTables = new LookupTable[lookupCount];

        for (int i = 0; i < lookupTables.Length; i++)
        {
            lookupTables[i] = LookupTable.Load(reader, offset + lookupOffsets[i]);
        }

        return new LookupListTable(lookupCount, lookupTables);
    }
}

/// <summary>
/// A Lookup table (Lookup) defines the specific conditions, type, and results of a substitution
/// or positioning action that is used to implement a feature. For example, a substitution
/// operation requires a list of target glyph indices to be replaced, a list of replacement glyph
/// indices, and a description of the type of substitution action.
/// <see href="https://docs.microsoft.com/en-us/typography/opentype/spec/chapter2#lookup-table"/>
/// </summary>
internal sealed class LookupTable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LookupTable"/> class.
    /// </summary>
    /// <param name="lookupType">The lookup type identifying the kind of substitution.</param>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    /// <param name="lookupSubTables">The array of lookup subtables.</param>
    private LookupTable(
        ushort lookupType,
        LookupFlags lookupFlags,
        ushort markFilteringSet,
        LookupSubTable[] lookupSubTables)
    {
        this.LookupType = lookupType;
        this.LookupFlags = lookupFlags;
        this.MarkFilteringSet = markFilteringSet;
        this.LookupSubTables = lookupSubTables;

        // The union of every subtable's gating coverage: a glyph outside the digest
        // cannot be affected by any subtable of this lookup, so application skips it
        // without touching the subtables. See GlyphSetDigest for the accuracy contract.
        GlyphSetDigest digest = default;
        for (int i = 0; i < lookupSubTables.Length; i++)
        {
            // Each subtable also carries its own digest so application can skip
            // subtables whose gating coverage cannot contain the current glyph
            // without paying the virtual probe. Contextual formats that expose no
            // leading coverage flood their digest and therefore always pass.
            GlyphSetDigest subTableDigest = default;
            lookupSubTables[i].CollectDigest(ref subTableDigest);
            lookupSubTables[i].Digest = subTableDigest;
            lookupSubTables[i].CollectDigest(ref digest);
        }

        this.Digest = digest;
    }

    /// <summary>
    /// Gets the lookup type, which determines the kind of substitution performed.
    /// </summary>
    public ushort LookupType { get; }

    /// <summary>
    /// Gets the lookup qualifiers flags that control filtering of glyphs during lookup.
    /// </summary>
    public LookupFlags LookupFlags { get; }

    /// <summary>
    /// Gets the index (base 0) into the GDEF mark glyph sets structure, used when the
    /// <see cref="AdvancedTypographic.LookupFlags.UseMarkFilteringSet"/> flag is set.
    /// </summary>
    public ushort MarkFilteringSet { get; }

    /// <summary>
    /// Gets the array of lookup subtables for this lookup.
    /// </summary>
    public LookupSubTable[] LookupSubTables { get; }

    /// <summary>
    /// Gets the approximate membership filter for the glyphs this lookup can affect.
    /// </summary>
    public GlyphSetDigest Digest { get; }

    /// <summary>
    /// Loads the <see cref="LookupTable"/> from the binary reader at the given offset.
    /// </summary>
    /// <param name="reader">The big-endian binary reader.</param>
    /// <param name="offset">The offset to the beginning of the lookup table.</param>
    /// <returns>The loaded <see cref="LookupTable"/>.</returns>
    public static LookupTable Load(BigEndianBinaryReader reader, long offset)
    {
        // +----------+--------------------------------+-------------------------------------------------------------+
        // | Type     | Name                           | Description                                                 |
        // +==========+================================+=============================================================+
        // | uint16   | lookupType                     | Different enumerations for GSUB and GPOS                    |
        // +----------+--------------------------------+-------------------------------------------------------------+
        // | uint16   | lookupFlag                     | Lookup qualifiers                                           |
        // +----------+--------------------------------+-------------------------------------------------------------+
        // | uint16   | subTableCount                  | Number of subtables for this lookup                         |
        // +----------+--------------------------------+-------------------------------------------------------------+
        // | Offset16 | subtableOffsets[subTableCount] | Array of offsets to lookup subtables, from beginning of     |
        // |          |                                | Lookup table                                                |
        // +----------+--------------------------------+-------------------------------------------------------------+
        // | uint16   | markFilteringSet               | Index (base 0) into GDEF mark glyph sets structure.         |
        // |          |                                | This field is only present if the USE\_MARK\_FILTERING\_SET |
        // |          |                                | lookup flag is set.                                         |
        // +----------+--------------------------------+-------------------------------------------------------------+
        reader.Seek(offset, SeekOrigin.Begin);

        ushort lookupType = reader.ReadUInt16();
        LookupFlags lookupFlags = reader.ReadUInt16<LookupFlags>();
        ushort subTableCount = reader.ReadUInt16();

        using Buffer<ushort> subTableOffsetsBuffer = new(subTableCount);
        Span<ushort> subTableOffsets = subTableOffsetsBuffer.GetSpan();
        reader.ReadUInt16Array(subTableOffsets);

        // The fifth bit indicates the presence of a MarkFilteringSet field in the Lookup table.
        ushort markFilteringSet = ((lookupFlags & LookupFlags.UseMarkFilteringSet) != 0)
            ? reader.ReadUInt16()
            : (ushort)0;

        LookupSubTable[] lookupSubTables = new LookupSubTable[subTableCount];

        for (int i = 0; i < lookupSubTables.Length; i++)
        {
            lookupSubTables[i] = LoadLookupSubTable(lookupType, lookupFlags, markFilteringSet, reader, offset + subTableOffsets[i]);
        }

        return new LookupTable(lookupType, lookupFlags, markFilteringSet, lookupSubTables);
    }

    /// <summary>
    /// Attempts to perform a glyph substitution at the specified index in the collection.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="table">The GSUB table.</param>
    /// <param name="collection">The glyph substitution collection.</param>
    /// <param name="feature">The feature tag to apply.</param>
    /// <param name="index">The index in the collection at which to attempt substitution.</param>
    /// <param name="count">The number of glyphs in the input sequence to consider.</param>
    /// <returns><see langword="true"/> if a substitution was performed; otherwise, <see langword="false"/>.</returns>
    public bool TrySubstitution(
        FontMetrics fontMetrics,
        GSubTable table,
        GlyphSubstitutionCollection collection,
        Tag feature,
        int index,
        int count)
    {
        ushort glyphId = collection[index].GlyphId;
        foreach (LookupSubTable subTable in this.LookupSubTables)
        {
            // A glyph outside the subtable's digest cannot match its coverage, so the
            // probe (a virtual call, coverage search, or full context-match attempt)
            // is skipped entirely.
            if (!subTable.Digest.MightContain(glyphId))
            {
                continue;
            }

            if (ShapingProbe.Enabled)
            {
                ShapingProbe.SubTableProbes++;
            }

            if (subTable.TrySubstitution(fontMetrics, table, collection, feature, index, count))
            {
                // A lookup is finished for a glyph after the client locates the target
                // glyph or glyph context and performs a substitution, if specified.
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Loads a lookup subtable based on the lookup type.
    /// </summary>
    /// <param name="lookupType">The lookup type identifying the kind of substitution.</param>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    /// <param name="reader">The big-endian binary reader.</param>
    /// <param name="offset">The offset to the beginning of the subtable.</param>
    /// <returns>The loaded <see cref="LookupSubTable"/>.</returns>
    private static LookupSubTable LoadLookupSubTable(
        ushort lookupType,
        LookupFlags lookupFlags,
        ushort markFilteringSet,
        BigEndianBinaryReader reader,
        long offset)
        => lookupType switch
        {
            1 => LookupType1SubTable.Load(reader, offset, lookupFlags, markFilteringSet),
            2 => LookupType2SubTable.Load(reader, offset, lookupFlags, markFilteringSet),
            3 => LookupType3SubTable.Load(reader, offset, lookupFlags, markFilteringSet),
            4 => LookupType4SubTable.Load(reader, offset, lookupFlags, markFilteringSet),
            5 => LookupType5SubTable.Load(reader, offset, lookupFlags, markFilteringSet),
            6 => LookupType6SubTable.Load(reader, offset, lookupFlags, markFilteringSet),
            7 => LookupType7SubTable.Load(reader, offset, lookupFlags, markFilteringSet, LoadLookupSubTable),
            8 => LookupType8SubTable.Load(reader, offset, lookupFlags, markFilteringSet),
            _ => new NotImplementedSubTable(),
        };
}

/// <summary>
/// Base class for all GSUB lookup subtables. Each subtable implements a specific
/// type of glyph substitution logic.
/// <see href="https://docs.microsoft.com/en-us/typography/opentype/spec/gsub"/>
/// </summary>
internal abstract class LookupSubTable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LookupSubTable"/> class.
    /// </summary>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    protected LookupSubTable(LookupFlags lookupFlags, ushort markFilteringSet)
    {
        this.LookupFlags = lookupFlags;
        this.MarkFilteringSet = markFilteringSet;
    }

    /// <summary>
    /// Gets the lookup qualifiers flags that control filtering of glyphs during lookup.
    /// </summary>
    public LookupFlags LookupFlags { get; }

    /// <summary>
    /// Gets the index (base 0) into the GDEF mark glyph sets structure.
    /// </summary>
    public ushort MarkFilteringSet { get; }

    /// <summary>
    /// Gets or sets the approximate membership filter for the glyphs this subtable can
    /// affect. Assigned once by the owning <see cref="LookupTable"/> during construction.
    /// </summary>
    public GlyphSetDigest Digest { get; internal set; }

    /// <summary>
    /// Adds the coverage that gates this subtable's applicability to the digest.
    /// The default adds every glyph so the lookup is always attempted, the correct
    /// conservative behavior for subtables whose gating coverage is unknown.
    /// </summary>
    /// <param name="digest">The digest to add to.</param>
    public virtual void CollectDigest(ref GlyphSetDigest digest) => digest.AddAll();

    /// <summary>
    /// Attempts to perform a glyph substitution at the specified index in the collection.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="table">The GSUB table.</param>
    /// <param name="collection">The glyph substitution collection.</param>
    /// <param name="feature">The feature tag to apply.</param>
    /// <param name="index">The index in the collection at which to attempt substitution.</param>
    /// <param name="count">The number of glyphs in the input sequence to consider.</param>
    /// <returns><see langword="true"/> if a substitution was performed; otherwise, <see langword="false"/>.</returns>
    public abstract bool TrySubstitution(
        FontMetrics fontMetrics,
        GSubTable table,
        GlyphSubstitutionCollection collection,
        Tag feature,
        int index,
        int count);
}
