// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic.GSub;

/// <summary>
/// A Contextual Substitution subtable describes glyph substitutions in context that replace one
/// or more glyphs within a certain pattern of glyphs.
/// <see href="https://docs.microsoft.com/en-us/typography/opentype/spec/gsub#lookuptype-5-contextual-substitution-subtable"/>
/// </summary>
internal static class LookupType5SubTable
{
    /// <summary>
    /// Loads the contextual substitution lookup subtable from the given offset.
    /// </summary>
    /// <param name="reader">The big-endian binary reader.</param>
    /// <param name="offset">The offset to the beginning of the substitution subtable.</param>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    /// <returns>The loaded <see cref="LookupSubTable"/>.</returns>
    public static LookupSubTable Load(BigEndianBinaryReader reader, long offset, LookupFlags lookupFlags, ushort markFilteringSet)
    {
        reader.Seek(offset, SeekOrigin.Begin);
        ushort subTableFormat = reader.ReadUInt16();

        return subTableFormat switch
        {
            1 => LookupType5Format1SubTable.Load(reader, offset, lookupFlags, markFilteringSet),
            2 => LookupType5Format2SubTable.Load(reader, offset, lookupFlags, markFilteringSet),
            3 => LookupType5Format3SubTable.Load(reader, offset, lookupFlags, markFilteringSet),
            _ => new NotImplementedSubTable(),
        };
    }
}

/// <summary>
/// Implements context substitution format 1 (simple glyph contexts).
/// Substitution rules are defined with specific glyph sequences.
/// <see href="https://docs.microsoft.com/en-us/typography/opentype/spec/gsub#51-context-substitution-format-1-simple-glyph-contexts"/>
/// </summary>
internal sealed class LookupType5Format1SubTable : LookupSubTable
{
    /// <summary>
    /// The coverage table that defines the set of first input glyph IDs.
    /// </summary>
    private readonly CoverageTable coverageTable;

    /// <summary>
    /// The array of sequence rule set tables, ordered by coverage index.
    /// </summary>
    private readonly SequenceRuleSetTable[] seqRuleSetTables;

    /// <summary>
    /// Initializes a new instance of the <see cref="LookupType5Format1SubTable"/> class.
    /// </summary>
    /// <param name="coverageTable">The coverage table defining first input glyphs.</param>
    /// <param name="seqRuleSetTables">The array of sequence rule set tables.</param>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    private LookupType5Format1SubTable(CoverageTable coverageTable, SequenceRuleSetTable[] seqRuleSetTables, LookupFlags lookupFlags, ushort markFilteringSet)
        : base(lookupFlags, markFilteringSet)
    {
        this.coverageTable = coverageTable;
        this.seqRuleSetTables = seqRuleSetTables;
    }

    /// <summary>
    /// Loads the context substitution format 1 subtable from the given offset.
    /// </summary>
    /// <param name="reader">The big-endian binary reader.</param>
    /// <param name="offset">The offset to the beginning of the substitution subtable.</param>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    /// <returns>The loaded <see cref="LookupType5Format1SubTable"/>.</returns>
    public static LookupType5Format1SubTable Load(BigEndianBinaryReader reader, long offset, LookupFlags lookupFlags, ushort markFilteringSet)
    {
        SequenceRuleSetTable[] seqRuleSets = TableLoadingUtils.LoadSequenceContextFormat1(reader, offset, out CoverageTable coverageTable);

        return new LookupType5Format1SubTable(coverageTable, seqRuleSets, lookupFlags, markFilteringSet);
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
        if (offset < 0 || offset >= this.seqRuleSetTables.Length)
        {
            return false;
        }

        // TODO: Check this.
        // https://docs.microsoft.com/en-us/typography/opentype/spec/gsub#example-7-contextual-substitution-format-1
        SequenceRuleSetTable ruleSetTable = this.seqRuleSetTables[offset];
        SkippingGlyphIterator iterator = new(fontMetrics, buffer, index, this.LookupFlags, this.MarkFilteringSet);

        // Slot zero holds the coverage-matched glyph; the sequence match fills
        // the rest, so nested lookups address the records the match consumed.
        Span<int> matchPositions = stackalloc int[AdvancedTypographicUtils.MaxContextLength + 1];
        matchPositions[0] = index;
        foreach (SequenceRuleTable ruleTable in ruleSetTable.SequenceRuleTables)
        {
            int remaining = count - 1;
            int seqLength = ruleTable.InputSequence.Length;
            if (seqLength > remaining)
            {
                continue;
            }

            if (!AdvancedTypographicUtils.MatchSequence(iterator, 1, ruleTable.InputSequence, lookupMask, false, matchPositions[1..], out int matchEnd))
            {
                continue;
            }

            // It's a match. Perform substitutions and return true if anything changed.
            return AdvancedTypographicUtils.ApplyLookupList(
                fontMetrics,
                table,
                feature,
                lookupMask,
                ruleTable.SequenceLookupRecords,
                buffer,
                matchPositions,
                seqLength + 1,
                matchEnd);
        }

        return false;
    }

    /// <inheritdoc />
    public override bool WouldApply(ReadOnlySpan<ushort> glyphs, bool zeroContext)
    {
        int offset = this.coverageTable.CoverageIndexOf(glyphs[0]);
        if (offset < 0 || offset >= this.seqRuleSetTables.Length)
        {
            return false;
        }

        foreach (SequenceRuleTable rule in this.seqRuleSetTables[offset].SequenceRuleTables)
        {
            ushort[] input = rule.InputSequence;
            if (input.Length + 1 != glyphs.Length)
            {
                continue;
            }

            bool matched = true;
            for (int i = 0; i < input.Length; i++)
            {
                if (glyphs[i + 1] != input[i])
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
}

/// <summary>
/// Implements context substitution format 2 (class-based glyph contexts).
/// Substitution rules are defined using glyph class definitions.
/// <see href="https://docs.microsoft.com/en-us/typography/opentype/spec/gsub#52-context-substitution-format-2-class-based-glyph-contexts"/>
/// </summary>
internal sealed class LookupType5Format2SubTable : LookupSubTable
{
    /// <summary>
    /// The coverage table that defines the set of first input glyph IDs.
    /// </summary>
    private readonly CoverageTable coverageTable;

    /// <summary>
    /// The class definition table used to classify input glyphs.
    /// </summary>
    private readonly ClassDefinitionTable classDefinitionTable;

    /// <summary>
    /// The array of class sequence rule set tables, indexed by class value.
    /// </summary>
    private readonly ClassSequenceRuleSetTable[] sequenceRuleSetTables;

    /// <summary>
    /// Initializes a new instance of the <see cref="LookupType5Format2SubTable"/> class.
    /// </summary>
    /// <param name="sequenceRuleSetTables">The array of class sequence rule set tables.</param>
    /// <param name="classDefinitionTable">The class definition table for input glyphs.</param>
    /// <param name="coverageTable">The coverage table defining first input glyphs.</param>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    private LookupType5Format2SubTable(
        ClassSequenceRuleSetTable[] sequenceRuleSetTables,
        ClassDefinitionTable classDefinitionTable,
        CoverageTable coverageTable,
        LookupFlags lookupFlags,
        ushort markFilteringSet)
        : base(lookupFlags, markFilteringSet)
    {
        this.sequenceRuleSetTables = sequenceRuleSetTables;
        this.classDefinitionTable = classDefinitionTable;
        this.coverageTable = coverageTable;
    }

    /// <summary>
    /// Loads the context substitution format 2 subtable from the given offset.
    /// </summary>
    /// <param name="reader">The big-endian binary reader.</param>
    /// <param name="offset">The offset to the beginning of the substitution subtable.</param>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    /// <returns>The loaded <see cref="LookupType5Format2SubTable"/>.</returns>
    public static LookupType5Format2SubTable Load(BigEndianBinaryReader reader, long offset, LookupFlags lookupFlags, ushort markFilteringSet)
    {
        CoverageTable coverageTable = TableLoadingUtils.LoadSequenceContextFormat2(reader, offset, out ClassDefinitionTable classDefTable, out ClassSequenceRuleSetTable[] classSeqRuleSets);

        return new LookupType5Format2SubTable(classSeqRuleSets, classDefTable, coverageTable, lookupFlags, markFilteringSet);
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

        if (this.coverageTable.CoverageIndexOf(glyphId) <= -1)
        {
            return false;
        }

        // TODO: Check this.
        // https://docs.microsoft.com/en-us/typography/opentype/spec/gsub#52-context-substitution-format-2-class-based-glyph-contexts
        int offset = this.classDefinitionTable.ClassIndexOf(glyphId);
        if (offset < 0)
        {
            return false;
        }

        ClassSequenceRuleSetTable? ruleSetTable = this.sequenceRuleSetTables[offset];
        if (ruleSetTable is null)
        {
            return false;
        }

        SkippingGlyphIterator iterator = new(fontMetrics, buffer, index, this.LookupFlags, this.MarkFilteringSet);

        // Slot zero holds the coverage-matched glyph; the sequence match fills
        // the rest, so nested lookups address the records the match consumed.
        Span<int> matchPositions = stackalloc int[AdvancedTypographicUtils.MaxContextLength + 1];
        matchPositions[0] = index;
        foreach (ClassSequenceRuleTable ruleTable in ruleSetTable.SequenceRuleTables)
        {
            int remaining = count - 1;
            int seqLength = ruleTable.InputSequence.Length;
            if (seqLength > remaining)
            {
                continue;
            }

            if (!AdvancedTypographicUtils.MatchClassSequence(iterator, 1, ruleTable.InputSequence, this.classDefinitionTable, lookupMask, false, matchPositions[1..], out int matchEnd))
            {
                continue;
            }

            // It's a match. Perform substitutions and return true if anything changed.
            return AdvancedTypographicUtils.ApplyLookupList(
                fontMetrics,
                table,
                feature,
                lookupMask,
                ruleTable.SequenceLookupRecords,
                buffer,
                matchPositions,
                seqLength + 1,
                matchEnd);
        }

        return false;
    }

    /// <inheritdoc />
    public override bool WouldApply(ReadOnlySpan<ushort> glyphs, bool zeroContext)
    {
        int classId = this.classDefinitionTable.ClassIndexOf(glyphs[0]);
        ClassSequenceRuleTable[]? rules = classId >= 0 && classId < this.sequenceRuleSetTables.Length
            ? this.sequenceRuleSetTables[classId]?.SequenceRuleTables
            : null;

        if (rules is null)
        {
            return false;
        }

        foreach (ClassSequenceRuleTable rule in rules)
        {
            ushort[] input = rule.InputSequence;
            if (input.Length + 1 != glyphs.Length)
            {
                continue;
            }

            bool matched = true;
            for (int i = 0; i < input.Length; i++)
            {
                if (this.classDefinitionTable.ClassIndexOf(glyphs[i + 1]) != input[i])
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
}

/// <summary>
/// Implements context substitution format 3 (coverage-based glyph contexts).
/// Substitution rules are defined using coverage tables for each position in the input sequence.
/// <see href="https://docs.microsoft.com/en-us/typography/opentype/spec/gsub#53-context-substitution-format-3-coverage-based-glyph-contexts"/>
/// </summary>
internal sealed class LookupType5Format3SubTable : LookupSubTable
{
    /// <summary>
    /// The array of coverage tables, one for each position in the input sequence.
    /// </summary>
    private readonly CoverageTable[] coverageTables;

    /// <summary>
    /// The array of sequence lookup records that define the substitutions to apply.
    /// </summary>
    private readonly SequenceLookupRecord[] sequenceLookupRecords;

    /// <summary>
    /// Initializes a new instance of the <see cref="LookupType5Format3SubTable"/> class.
    /// </summary>
    /// <param name="coverageTables">The array of coverage tables for each input position.</param>
    /// <param name="sequenceLookupRecords">The array of sequence lookup records.</param>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    private LookupType5Format3SubTable(
        CoverageTable[] coverageTables,
        SequenceLookupRecord[] sequenceLookupRecords,
        LookupFlags lookupFlags,
        ushort markFilteringSet)
        : base(lookupFlags, markFilteringSet)
    {
        this.coverageTables = coverageTables;
        this.sequenceLookupRecords = sequenceLookupRecords;
    }

    /// <summary>
    /// Loads the context substitution format 3 subtable from the given offset.
    /// </summary>
    /// <param name="reader">The big-endian binary reader.</param>
    /// <param name="offset">The offset to the beginning of the substitution subtable.</param>
    /// <param name="lookupFlags">The lookup qualifiers flags.</param>
    /// <param name="markFilteringSet">The index into the GDEF mark glyph sets structure.</param>
    /// <returns>The loaded <see cref="LookupType5Format3SubTable"/>.</returns>
    public static LookupType5Format3SubTable Load(BigEndianBinaryReader reader, long offset, LookupFlags lookupFlags, ushort markFilteringSet)
    {
        SequenceLookupRecord[] seqLookupRecords = TableLoadingUtils.LoadSequenceContextFormat3(reader, offset, out CoverageTable[] coverageTables);

        return new LookupType5Format3SubTable(coverageTables, seqLookupRecords, lookupFlags, markFilteringSet);
    }

    /// <inheritdoc/>
    public override void CollectDigest(ref GlyphSetDigest digest)
    {
        if (this.coverageTables.Length == 0)
        {
            // Degenerate table data: without a first position coverage the gate
            // cannot be known, so the lookup is always attempted.
            digest.AddAll();
            return;
        }

        this.coverageTables[0].CollectDigest(ref digest);
    }

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

        // https://docs.microsoft.com/en-us/typography/opentype/spec/gsub#53-context-substitution-format-3-coverage-based-glyph-contexts
        // The input coverage array covers the whole input including its first
        // glyph, so the match fills every position nested lookups address.
        SkippingGlyphIterator iterator = new(fontMetrics, buffer, index, this.LookupFlags, this.MarkFilteringSet);
        Span<int> matchPositions = stackalloc int[AdvancedTypographicUtils.MaxContextLength];
        if (!AdvancedTypographicUtils.MatchCoverageSequence(iterator, this.coverageTables, index, index + count, lookupMask, false, matchPositions, out int matchEnd))
        {
            return false;
        }

        // It's a match. Perform substitutions and return true if anything changed.
        return AdvancedTypographicUtils.ApplyLookupList(
            fontMetrics,
            table,
            feature,
            lookupMask,
            this.sequenceLookupRecords,
            buffer,
            matchPositions,
            this.coverageTables.Length,
            matchEnd);
    }

    /// <inheritdoc />
    public override bool WouldApply(ReadOnlySpan<ushort> glyphs, bool zeroContext)
    {
        CoverageTable[] coverages = this.coverageTables;
        if (coverages.Length != glyphs.Length)
        {
            return false;
        }

        for (int i = 1; i < glyphs.Length; i++)
        {
            if (coverages[i].CoverageIndexOf(glyphs[i]) < 0)
            {
                return false;
            }
        }

        return true;
    }
}
