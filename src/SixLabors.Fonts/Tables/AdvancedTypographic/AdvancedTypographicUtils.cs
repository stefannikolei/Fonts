// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Tables.AdvancedTypographic.GPos;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// Provides shared utility methods for advanced typographic layout processing in GPOS and GSUB tables.
/// </summary>
internal static class AdvancedTypographicUtils
{
    /// <summary>
    /// The maximum context length for sequence matching operations.
    /// Used to prevent excessive processing from maliciously crafted fonts.
    /// Based on HarfBuzz hb-buffer.hh.
    /// </summary>
    public const int MaxContextLength = 64;

    /// <summary>
    /// The maximum length factor multiplied by buffer count to compute max allowable buffer size.
    /// </summary>
    private const int MaxLengthFactor = 64;

    /// <summary>
    /// The minimum value for the max allowable buffer size.
    /// </summary>
    private const int MaxLengthMinimum = 16384;

    /// <summary>
    /// The maximum operations factor multiplied by buffer count to compute max allowable operations.
    /// </summary>
    private const int MaxOperationsFactor = 1024;

    /// <summary>
    /// The minimum value for the max allowable operations count.
    /// </summary>
    private const int MaxOperationsMinimum = 16384;

    /// <summary>
    /// The absolute maximum number of shaping characters, set to half of int.MaxValue.
    /// </summary>
    private const int MaxShapingCharsLength = 0x3FFFFFFF; // Half int max.

    /// <summary>
    /// Defines the direction for sequence matching operations.
    /// </summary>
    internal enum MatchDirection
    {
        /// <summary>
        /// Match in the forward direction.
        /// </summary>
        Forward,

        /// <summary>
        /// Match in the backward direction.
        /// </summary>
        Backward
    }

    /// <summary>
    /// Gets a value indicating whether the glyph represented by the codepoint should be interpreted vertically.
    /// </summary>
    /// <param name="codePoint">The codepoint represented by the glyph.</param>
    /// <param name="layoutMode">The layout mode.</param>
    /// <returns>The <see cref="bool"/>.</returns>
    public static bool IsVerticalGlyph(CodePoint codePoint, LayoutMode layoutMode)
    {
        if (layoutMode.IsVertical())
        {
            return true;
        }

        bool isVerticalLayout = layoutMode.IsVerticalMixed();
        return isVerticalLayout && CodePoint.GetVerticalOrientationType(codePoint) is VerticalOrientationType.Upright or VerticalOrientationType.TransformUpright;
    }

    /// <summary>
    /// Gets the maximum allowable shaping buffer count for the given input length.
    /// </summary>
    /// <param name="length">The input buffer length.</param>
    /// <returns>The maximum allowable count.</returns>
    public static int GetMaxAllowableShapingCollectionCount(int length)
        => (int)Math.Min(Math.Max((long)length * MaxLengthFactor, MaxLengthMinimum), MaxShapingCharsLength);

    /// <summary>
    /// Gets the maximum allowable shaping operations count for the given input length.
    /// </summary>
    /// <param name="length">The input buffer length.</param>
    /// <returns>The maximum allowable operations count.</returns>
    public static int GetMaxAllowableShapingOperationsCount(int length)
        => (int)Math.Min(Math.Max((long)length * MaxOperationsFactor, MaxOperationsMinimum), MaxShapingCharsLength);

    /// <summary>
    /// Applies nested lookups from sequence lookup records for GSUB contextual/chaining lookups.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="table">The GSUB table.</param>
    /// <param name="feature">The feature tag being applied.</param>
    /// <param name="lookupMask">The applying lookup's combined mask, inherited by the nested lookups.</param>
    /// <param name="lookupFlags">The lookup flags for glyph filtering.</param>
    /// <param name="markFilteringSet">The mark filtering set index.</param>
    /// <param name="records">The sequence lookup records specifying which lookups to apply at which positions.</param>
    /// <param name="buffer">The glyph substitution buffer.</param>
    /// <param name="index">The starting index in the buffer.</param>
    /// <param name="count">The number of glyphs in the input sequence.</param>
    /// <returns><see langword="true"/> if the lookups were applied.</returns>
    public static bool ApplyLookupList(
        FontMetrics fontMetrics,
        GSubTable table,
        Tag feature,
        uint lookupMask,
        LookupFlags lookupFlags,
        ushort markFilteringSet,
        SequenceLookupRecord[] records,
        ShapingBuffer buffer,
        int index,
        int count)
    {
        SkippingGlyphIterator iterator = new(fontMetrics, buffer, index, lookupFlags, markFilteringSet);
        if (ShapingProbe.Enabled)
        {
            ShapingProbe.ContextIterators++;
        }

        int currentCount = buffer.Count;

        // Nested lookups mutate the input side in place whatever their type: the
        // contextual match that recursed here owns the pass cursor.
        buffer.PushNestedApplication();

        foreach (SequenceLookupRecord lookupRecord in records)
        {
            ushort sequenceIndex = lookupRecord.SequenceIndex;
            ushort lookupIndex = lookupRecord.LookupListIndex;
            iterator.Index = index;
            iterator.Increment(sequenceIndex);
            GSub.LookupTable lookup = table.LookupList.LookupTables[lookupIndex];
            _ = lookup.TrySubstitution(fontMetrics, table, buffer, feature, lookupMask, iterator.Index, count - (iterator.Index - index));

            // Account for substitutions changing the length of the buffer.
            if (buffer.Count != currentCount)
            {
                count -= currentCount - buffer.Count;
                currentCount = buffer.Count;
            }
        }

        buffer.PopNestedApplication();
        return true;
    }

    /// <summary>
    /// Applies nested lookups from sequence lookup records for GPOS contextual/chaining lookups.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="table">The GPOS table.</param>
    /// <param name="feature">The feature tag being applied.</param>
    /// <param name="lookupFlags">The lookup flags for glyph filtering.</param>
    /// <param name="markFilteringSet">The mark filtering set index.</param>
    /// <param name="records">The sequence lookup records specifying which lookups to apply at which positions.</param>
    /// <param name="buffer">The glyph positioning buffer.</param>
    /// <param name="index">The starting index in the buffer.</param>
    /// <param name="count">The number of glyphs in the input sequence.</param>
    /// <returns><see langword="true"/> if the lookups were applied.</returns>
    public static bool ApplyLookupList(
        FontMetrics fontMetrics,
        GPosTable table,
        Tag feature,
        LookupFlags lookupFlags,
        ushort markFilteringSet,
        SequenceLookupRecord[] records,
        ShapingBuffer buffer,
        int index,
        int count)
    {
        SkippingGlyphIterator iterator = new(fontMetrics, buffer, index, lookupFlags, markFilteringSet);
        if (ShapingProbe.Enabled)
        {
            ShapingProbe.ContextIterators++;
        }

        foreach (SequenceLookupRecord lookupRecord in records)
        {
            ushort sequenceIndex = lookupRecord.SequenceIndex;
            ushort lookupIndex = lookupRecord.LookupListIndex;
            iterator.Index = index;
            iterator.Increment(sequenceIndex);
            LookupTable lookup = table.LookupList.LookupTables[lookupIndex];
            _ = lookup.TryUpdatePosition(fontMetrics, table, buffer, feature, iterator.Index, count - (iterator.Index - index));
        }

        return true;
    }

    /// <summary>
    /// Matches an input glyph sequence by glyph ID, verifying that each glyph has the applying lookup enabled.
    /// </summary>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="featureMask">The applying lookup's combined mask; matched glyphs must have it enabled.</param>
    /// <param name="increment">The initial increment from the iterator's current position.</param>
    /// <param name="sequence">The array of glyph IDs to match.</param>
    /// <param name="matches">A span to store matched glyph indices, or default if not needed.</param>
    /// <returns><see langword="true"/> if the entire sequence was matched; otherwise, <see langword="false"/>.</returns>
    public static bool MatchInputSequence(SkippingGlyphIterator iterator, uint featureMask, ushort increment, ushort[] sequence, Span<int> matches)
    {
        iterator.SetMatchContext(featureMask, false);
        return Match(
            increment,
            sequence,
            iterator,
            static (component, data) => component == data.GlyphId,
            matches);
    }

    /// <summary>
    /// Matches a glyph sequence by glyph ID under the given matcher context.
    /// </summary>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="increment">The initial increment from the iterator's current position.</param>
    /// <param name="sequence">The array of glyph IDs to match.</param>
    /// <param name="mask">The applying lookup's mask; ignored during context matching.</param>
    /// <param name="contextMatch">Whether this matches backtrack or lookahead context.</param>
    /// <returns><see langword="true"/> if the entire sequence was matched; otherwise, <see langword="false"/>.</returns>
    public static bool MatchSequence(SkippingGlyphIterator iterator, int increment, ushort[] sequence, uint mask, bool contextMatch)
        => MatchSequence(iterator, increment, sequence, mask, contextMatch, out _);

    /// <summary>
    /// Matches a glyph sequence by glyph ID under the given matcher context,
    /// reporting the position one past the final matched element.
    /// </summary>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="increment">The initial increment from the iterator's current position.</param>
    /// <param name="sequence">The array of glyph IDs to match.</param>
    /// <param name="mask">The applying lookup's mask; ignored during context matching.</param>
    /// <param name="contextMatch">Whether this matches backtrack or lookahead context.</param>
    /// <param name="matchEnd">The position one past the final matched element.</param>
    /// <returns><see langword="true"/> if the entire sequence was matched; otherwise, <see langword="false"/>.</returns>
    public static bool MatchSequence(SkippingGlyphIterator iterator, int increment, ushort[] sequence, uint mask, bool contextMatch, out int matchEnd)
    {
        iterator.SetMatchContext(mask, contextMatch);
        return Match(
            increment,
            sequence,
            iterator,
            static (component, data) => component == data.GlyphId,
            default,
            out matchEnd);
    }

    /// <summary>
    /// Matches a glyph sequence by class values using a class definition table
    /// under the given matcher context.
    /// </summary>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="increment">The initial increment from the iterator's current position.</param>
    /// <param name="sequence">The array of class values to match.</param>
    /// <param name="classDefinitionTable">The class definition table used to map glyph IDs to class values.</param>
    /// <param name="mask">The applying lookup's mask; ignored during context matching.</param>
    /// <param name="contextMatch">Whether this matches backtrack or lookahead context.</param>
    /// <returns><see langword="true"/> if the entire sequence was matched; otherwise, <see langword="false"/>.</returns>
    public static bool MatchClassSequence(
        SkippingGlyphIterator iterator,
        int increment,
        ushort[] sequence,
        ClassDefinitionTable classDefinitionTable,
        uint mask,
        bool contextMatch)
        => MatchClassSequence(iterator, increment, sequence, classDefinitionTable, mask, contextMatch, out _);

    /// <summary>
    /// Matches a glyph sequence by class values under the given matcher context,
    /// reporting the position one past the final matched element.
    /// </summary>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="increment">The initial increment from the iterator's current position.</param>
    /// <param name="sequence">The array of class values to match.</param>
    /// <param name="classDefinitionTable">The class definition table used to map glyph IDs to class values.</param>
    /// <param name="mask">The applying lookup's mask; ignored during context matching.</param>
    /// <param name="contextMatch">Whether this matches backtrack or lookahead context.</param>
    /// <param name="matchEnd">The position one past the final matched element.</param>
    /// <returns><see langword="true"/> if the entire sequence was matched; otherwise, <see langword="false"/>.</returns>
    public static bool MatchClassSequence(
        SkippingGlyphIterator iterator,
        int increment,
        ushort[] sequence,
        ClassDefinitionTable classDefinitionTable,
        uint mask,
        bool contextMatch,
        out int matchEnd)
    {
        iterator.SetMatchContext(mask, contextMatch);

        // The class table travels as match state so the lambda stays static: a
        // capturing lambda here would allocate a closure and delegate on every
        // contextual rule attempt.
        return Match(
            increment,
            sequence,
            iterator,
            classDefinitionTable,
            static (component, data, table) => component == table.ClassIndexOf(data.GlyphId),
            default,
            out matchEnd);
    }

    /// <summary>
    /// Matches a forward glyph sequence using coverage tables under the given
    /// matcher context.
    /// </summary>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="coverageTable">The array of coverage tables to match against.</param>
    /// <param name="startIndex">The starting index in the buffer.</param>
    /// <param name="endExclusive">The exclusive end index in the buffer.</param>
    /// <param name="mask">The applying lookup's mask; ignored during context matching.</param>
    /// <param name="contextMatch">Whether this matches lookahead context.</param>
    /// <returns><see langword="true"/> if all coverage tables matched; otherwise, <see langword="false"/>.</returns>
    public static bool MatchCoverageSequence(
        SkippingGlyphIterator iterator,
        CoverageTable[] coverageTable,
        int startIndex,
        int endExclusive,
        uint mask,
        bool contextMatch)
        => MatchCoverageSequence(iterator, coverageTable, startIndex, endExclusive, mask, contextMatch, out _);

    /// <summary>
    /// Matches a forward glyph sequence using coverage tables under the given
    /// matcher context, reporting the position one past the final matched element.
    /// </summary>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="coverageTable">The array of coverage tables to match against.</param>
    /// <param name="startIndex">The starting index in the buffer.</param>
    /// <param name="endExclusive">The exclusive end index in the buffer.</param>
    /// <param name="mask">The applying lookup's mask; ignored during context matching.</param>
    /// <param name="contextMatch">Whether this matches lookahead context.</param>
    /// <param name="matchEnd">The position one past the final matched element.</param>
    /// <returns><see langword="true"/> if all coverage tables matched; otherwise, <see langword="false"/>.</returns>
    public static bool MatchCoverageSequence(
        SkippingGlyphIterator iterator,
        CoverageTable[] coverageTable,
        int startIndex,
        int endExclusive,
        uint mask,
        bool contextMatch,
        out int matchEnd)
    {
        iterator.SetMatchContext(mask, contextMatch);
        return Match(
            iterator,
            startIndex,
            coverageTable,
            MatchDirection.Forward,
            endExclusive,
            static (component, data) => component.CoverageIndexOf(data.GlyphId) >= 0,
            default,
            out matchEnd);
    }

    /// <summary>
    /// Matches a backward (backtrack) glyph sequence using coverage tables.
    /// Per the spec, backtrack[0] matches i-1, then i-2, and so on. Backtrack is
    /// always context, so joiners are transparent to it.
    /// </summary>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="backtrack">The array of backtrack coverage tables to match against.</param>
    /// <param name="startIndex">The starting index in the buffer (the first backtrack position).</param>
    /// <param name="endExclusive">The exclusive end index in the buffer.</param>
    /// <returns><see langword="true"/> if all backtrack coverage tables matched; otherwise, <see langword="false"/>.</returns>
    public static bool MatchBacktrackCoverageSequence(
        SkippingGlyphIterator iterator,
        CoverageTable[] backtrack,
        int startIndex,
        int endExclusive)
    {
        iterator.SetMatchContext(0, true);
        return Match(
            iterator,
            startIndex,
            backtrack,
            MatchDirection.Backward,
            endExclusive,
            static (component, data) => component.CoverageIndexOf(data.GlyphId) >= 0,
            default,
            out _);
    }

    /// <summary>
    /// Applies a chained sequence rule by matching backtrack, input, and lookahead glyph ID sequences.
    /// The input matches under the applying lookup's mask and joiner handling;
    /// backtrack and lookahead match as context, transparent to joiners.
    /// </summary>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="rule">The chained sequence rule table to apply.</param>
    /// <param name="mask">The applying lookup's mask.</param>
    /// <returns><see langword="true"/> if all sequences matched; otherwise, <see langword="false"/>.</returns>
    public static bool ApplyChainedSequenceRule(SkippingGlyphIterator iterator, ChainedSequenceRuleTable rule, uint mask)
    {
        int matchEnd = iterator.Index + 1;
        if (rule.InputSequence.Length > 0
            && !MatchSequence(iterator, 1, rule.InputSequence, mask, false, out matchEnd))
        {
            return false;
        }

        if (rule.LookaheadSequence.Length > 0)
        {
            // Lookahead starts exactly one past the final matched input element,
            // not a stepped jump over the input count.
            SkippingGlyphIterator lookahead = iterator;
            lookahead.Index = matchEnd - 1;
            if (!MatchSequence(lookahead, 1, rule.LookaheadSequence, 0, true))
            {
                return false;
            }
        }

        if (rule.BacktrackSequence.Length > 0
            && !MatchSequence(iterator, -rule.BacktrackSequence.Length, rule.BacktrackSequence, 0, true))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies a chained class sequence rule by matching backtrack, input, and lookahead class sequences.
    /// The input matches under the applying lookup's mask and joiner handling;
    /// backtrack and lookahead match as context, transparent to joiners.
    /// </summary>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="rule">The chained class sequence rule table to apply.</param>
    /// <param name="inputClassDefinitionTable">The class definition table for the input sequence.</param>
    /// <param name="backtrackClassDefinitionTable">The class definition table for the backtrack sequence.</param>
    /// <param name="lookaheadClassDefinitionTable">The class definition table for the lookahead sequence.</param>
    /// <param name="mask">The applying lookup's mask.</param>
    /// <returns><see langword="true"/> if all sequences matched; otherwise, <see langword="false"/>.</returns>
    public static bool ApplyChainedClassSequenceRule(
        SkippingGlyphIterator iterator,
        ChainedClassSequenceRuleTable rule,
        ClassDefinitionTable inputClassDefinitionTable,
        ClassDefinitionTable backtrackClassDefinitionTable,
        ClassDefinitionTable lookaheadClassDefinitionTable,
        uint mask)
    {
        int matchEnd = iterator.Index + 1;
        if (rule.InputSequence.Length > 0 &&
            !MatchClassSequence(iterator, 1, rule.InputSequence, inputClassDefinitionTable, mask, false, out matchEnd))
        {
            return false;
        }

        if (rule.LookaheadSequence.Length > 0)
        {
            // Lookahead starts exactly one past the final matched input element,
            // not a stepped jump over the input count.
            SkippingGlyphIterator lookahead = iterator;
            lookahead.Index = matchEnd - 1;
            if (!MatchClassSequence(lookahead, 1, rule.LookaheadSequence, lookaheadClassDefinitionTable, 0, true))
            {
                return false;
            }
        }

        if (rule.BacktrackSequence.Length > 0
            && !MatchClassSequence(iterator, -rule.BacktrackSequence.Length, rule.BacktrackSequence, backtrackClassDefinitionTable, 0, true))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks all coverage tables (backtrack, input, and lookahead) for a chained context Format 3 match.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="lookupFlags">The lookup flags for glyph filtering.</param>
    /// <param name="markFilteringSet">The mark filtering set index.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The starting index of the input sequence.</param>
    /// <param name="count">The number of glyphs available from the starting index.</param>
    /// <param name="input">The array of input coverage tables.</param>
    /// <param name="backtrack">The array of backtrack coverage tables.</param>
    /// <param name="lookahead">The array of lookahead coverage tables.</param>
    /// <param name="mask">The applying lookup's mask; the input matches under it.</param>
    /// <returns><see langword="true"/> if all coverages matched; otherwise, <see langword="false"/>.</returns>
    public static bool CheckAllCoverages(
        FontMetrics fontMetrics,
        LookupFlags lookupFlags,
        ushort markFilteringSet,
        ShapingBuffer buffer,
        int index,
        int count,
        CoverageTable[] input,
        CoverageTable[] backtrack,
        CoverageTable[] lookahead,
        uint mask)
    {
        int endExclusive = index + count;

        SkippingGlyphIterator iterator = new(fontMetrics, buffer, index, lookupFlags, markFilteringSet);
        if (ShapingProbe.Enabled)
        {
            ShapingProbe.ContextIterators++;
        }

        // Compute backtrack start using skippy prev(), not index-1: context steps
        // are transparent to joiners, so the step lands on the first solid glyph.
        int backtrackStart = index;
        if (backtrack.Length > 0)
        {
            SkippingGlyphIterator backIt = iterator;
            backIt.SetMatchContext(0, true);
            backIt.Index = index;
            backtrackStart = backIt.Prev(); // first backtrack glyph (i-1 in skippy space)
        }

        if (!MatchBacktrackCoverageSequence(iterator, backtrack, backtrackStart, endExclusive))
        {
            return false;
        }

        // Input starts at the current glyph position; lookahead starts exactly
        // one past the final matched input element.
        if (!MatchCoverageSequence(iterator, input, index, endExclusive, mask, false, out int matchEnd))
        {
            return false;
        }

        if (!MatchCoverageSequence(iterator, lookahead, matchEnd, endExclusive, 0, true))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies anchor-based positioning for mark-to-base, mark-to-ligature, or mark-to-mark attachment.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="buffer">The glyph positioning buffer.</param>
    /// <param name="index">The index of the mark glyph in the buffer.</param>
    /// <param name="baseAnchor">The anchor table for the base glyph, or <see langword="null"/> if no anchor is defined.</param>
    /// <param name="markRecord">The mark record containing the mark anchor table and class.</param>
    /// <param name="baseGlyphIndex">The index of the base glyph in the buffer.</param>
    /// <param name="feature">The feature tag being applied.</param>
    public static void ApplyAnchor(
        FontMetrics fontMetrics,
        ShapingBuffer buffer,
        int index,
        AnchorTable? baseAnchor,
        MarkRecord markRecord,
        int baseGlyphIndex,
        Tag feature)
    {
        // baseAnchor may be null because OpenType MarkToBase allows NULL anchor offsets
        // in BaseArray/BaseRecord. A NULL offset means "this base glyph has no anchor
        // for this mark class", and the lookup must be ignored for this mark–base pair.
        if (baseAnchor is null)
        {
            return;
        }

        ref GlyphShapingData baseData = ref buffer[baseGlyphIndex];
        AnchorXY baseXY = baseAnchor.GetAnchor(fontMetrics, ref baseData, buffer);

        ref GlyphShapingData markData = ref buffer[index];
        AnchorXY markXY = markRecord.MarkAnchorTable.GetAnchor(fontMetrics, ref markData, buffer);

        ref GlyphShapingPosition markPosition = ref buffer.PositionAt(index);
        markPosition.Bounds.X = baseXY.XCoordinate - markXY.XCoordinate;
        markPosition.Bounds.Y = baseXY.YCoordinate - markXY.YCoordinate;
        markPosition.MarkAttachment = baseGlyphIndex;
        markData.AppliedFeatureMask |= ShapePlanFeatures.GetVerticalMask(feature);
    }

    /// <summary>
    /// Applies a value record's positioning adjustments to a glyph in the buffer.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="buffer">The glyph positioning buffer.</param>
    /// <param name="index">The index of the glyph in the buffer.</param>
    /// <param name="record">The value record containing positioning adjustments.</param>
    /// <param name="feature">The feature tag being applied.</param>
    public static void ApplyPosition(
        FontMetrics fontMetrics,
        ShapingBuffer buffer,
        int index,
        ValueRecord record,
        Tag feature)
    {
        ref GlyphShapingPosition position = ref buffer.PositionAt(index);
        position.Bounds.Width += record.XAdvance;
        position.Bounds.Height += record.YAdvance;
        position.Bounds.X += record.XPlacement;
        position.Bounds.Y += record.YPlacement;

        // Apply variation deltas from VariationIndex tables (variable fonts).
        if (record.HasVariation)
        {
            position.Bounds.X += (short)MathF.Round(fontMetrics.GetGDefVariationDelta(record.XPlacementVariation));
            position.Bounds.Y += (short)MathF.Round(fontMetrics.GetGDefVariationDelta(record.YPlacementVariation));
            position.Bounds.Width += (short)MathF.Round(fontMetrics.GetGDefVariationDelta(record.XAdvanceVariation));
            position.Bounds.Height += (short)MathF.Round(fontMetrics.GetGDefVariationDelta(record.YAdvanceVariation));
        }

        ref GlyphShapingData current = ref buffer[index];
        current.AppliedFeatureMask |= ShapePlanFeatures.GetVerticalMask(feature);
    }

    /// <summary>
    /// Determines whether the specified glyph is a mark glyph based on GDEF class or Unicode properties.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="glyphId">The glyph identifier.</param>
    /// <param name="shapingData">The glyph shaping data.</param>
    /// <returns><see langword="true"/> if the glyph is a mark; otherwise, <see langword="false"/>.</returns>
    public static bool IsMarkGlyph(FontMetrics fontMetrics, ushort glyphId, ref GlyphShapingData shapingData)
    {
        if (!fontMetrics.TryGetGlyphClass(glyphId, out GlyphClassDef? glyphClass) &&
            !CodePoint.IsMark(shapingData.CodePoint))
        {
            return false;
        }

        if (glyphClass != GlyphClassDef.MarkGlyph)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the glyph shaping class (mark, base, ligature, mark attachment type) for the specified glyph,
    /// using GDEF table data if available or falling back to Unicode properties.
    /// Results are cached on the <see cref="GlyphShapingData"/> instance, with
    /// table-derived classes additionally cached on the buffer so re-classification
    /// after a substitution changes a glyph id skips the table walks.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="buffer">The glyph shaping buffer carrying the class cache.</param>
    /// <param name="glyphId">The glyph identifier.</param>
    /// <param name="shapingData">The glyph shaping data, used for caching and Unicode fallback.</param>
    /// <returns>The <see cref="GlyphShapingClass"/>.</returns>
    public static GlyphShapingClass GetGlyphShapingClass(FontMetrics fontMetrics, ShapingBuffer buffer, ushort glyphId, ref GlyphShapingData shapingData)
    {
        // Cache the shaping class on the GlyphShapingData to avoid repeated GDEF lookups.
        // The cache key stores the glyph id; -1 means "not cached".
        if (ShapingProbe.Enabled)
        {
            ShapingProbe.ClassifyCalls++;
        }

        if (shapingData.ShapingClassCacheKey == glyphId)
        {
            return shapingData.CachedShapingClass;
        }

        if (ShapingProbe.Enabled)
        {
            ShapingProbe.ClassifyMisses++;
        }

        if (buffer.TryGetShapingClass(fontMetrics, glyphId, out GlyphShapingClass cached))
        {
            shapingData.CachedShapingClass = cached;
            shapingData.ShapingClassCacheKey = glyphId;
            return cached;
        }

        bool isMark;
        bool isBase;
        bool isLigature;
        bool tableDerived = false;
        ushort markAttachmentType = 0;
        if (fontMetrics.TryGetGlyphClass(glyphId, out GlyphClassDef? glyphClass))
        {
            isMark = glyphClass == GlyphClassDef.MarkGlyph;
            isBase = glyphClass == GlyphClassDef.BaseGlyph;
            isLigature = glyphClass == GlyphClassDef.LigatureGlyph;
            tableDerived = true;
            if (fontMetrics.TryGetMarkAttachmentClass(glyphId, out GlyphClassDef? markAttachmentClass))
            {
                markAttachmentType = (ushort)markAttachmentClass;
            }
        }
        else
        {
            // TODO: We may have to store each codepoint. FontKit checks all.
            isMark = CodePoint.IsMark(shapingData.CodePoint);
            isBase = !isMark;
            isLigature = shapingData.CodePointCount > 1;
        }

        GlyphShapingClass result = new(isMark, isBase, isLigature, markAttachmentType);
        if (tableDerived)
        {
            buffer.SetShapingClass(glyphId, result);
        }

        shapingData.CachedShapingClass = result;
        shapingData.ShapingClassCacheKey = glyphId;
        return result;
    }

    /// <summary>
    /// Determines whether the specified glyph is in the given mark filtering set.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="markFilteringSet">The mark filtering set index.</param>
    /// <param name="glyphId">The glyph identifier.</param>
    /// <returns><see langword="true"/> if the glyph is in the mark filtering set; otherwise, <see langword="false"/>.</returns>
    public static bool IsInMarkFilteringSet(FontMetrics fontMetrics, ushort markFilteringSet, ushort glyphId)
        => fontMetrics.IsInMarkFilteringSet(markFilteringSet, glyphId);

    /// <summary>
    /// Matches a sequence of elements against glyphs using an increment-based approach,
    /// threading caller state through to the condition so callers can use static lambdas
    /// instead of allocating closures.
    /// </summary>
    /// <typeparam name="T">The type of sequence elements to match.</typeparam>
    /// <typeparam name="TState">The type of the state passed to the condition.</typeparam>
    /// <param name="increment">The initial increment from the iterator's current position.</param>
    /// <param name="sequence">The array of elements to match.</param>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="state">The caller state passed to each condition invocation.</param>
    /// <param name="condition">The condition function to test each element against glyph data.</param>
    /// <param name="matches">A span to store matched glyph indices, or default if not needed.</param>
    /// <returns><see langword="true"/> if all elements in the sequence were matched; otherwise, <see langword="false"/>.</returns>
    private static bool Match<T, TState>(
        int increment,
        T[] sequence,
        SkippingGlyphIterator iterator,
        TState state,
        Func<T, GlyphShapingData, TState, bool> condition,
        Span<int> matches)
        => Match(increment, sequence, iterator, state, condition, matches, out _);

    /// <summary>
    /// Matches a sequence of elements against glyphs using an increment-based
    /// approach with match state, reporting the position one past the final
    /// matched element so lookahead can start exactly where the input ended.
    /// </summary>
    /// <typeparam name="T">The type of sequence elements to match.</typeparam>
    /// <typeparam name="TState">The type of state passed to the condition.</typeparam>
    /// <param name="increment">The initial increment from the iterator's current position.</param>
    /// <param name="sequence">The array of elements to match.</param>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="state">The state passed to the condition.</param>
    /// <param name="condition">The condition function to test each element against glyph data.</param>
    /// <param name="matches">A span to store matched glyph indices, or default if not needed.</param>
    /// <param name="matchEnd">The position one past the final matched element.</param>
    /// <returns><see langword="true"/> if all elements in the sequence were matched; otherwise, <see langword="false"/>.</returns>
    private static bool Match<T, TState>(
        int increment,
        T[] sequence,
        SkippingGlyphIterator iterator,
        TState state,
        Func<T, GlyphShapingData, TState, bool> condition,
        Span<int> matches,
        out int matchEnd)
    {
        int position = iterator.Index;
        ShapingBuffer buffer = iterator.Collection;
        matchEnd = position + 1;
        int i = 0;

        // A buffer without a transparent record in reach keeps the solid-glyph
        // walk: every stepped-to record must match or the rule is refused.
        if (!iterator.MatchTransparencyActive)
        {
            int solidOffset = iterator.Increment(increment);
            if (solidOffset < 0)
            {
                return false;
            }

            while (i < sequence.Length && i < MaxContextLength && solidOffset < buffer.Count)
            {
                ref GlyphShapingData solidData = ref buffer[solidOffset];
                if (!iterator.MayMatch(ref solidData) || !condition(sequence[i], solidData, state))
                {
                    break;
                }

                if (matches.Length == MaxContextLength)
                {
                    matches[i] = solidOffset;
                }

                i++;
                matchEnd = solidOffset + 1;
                solidOffset = iterator.Next();
            }

            iterator.Index = position;
            return i == sequence.Length;
        }

        // A single forward step enters the walk directly so a transparent record
        // at the next position still gets its chance to match the first sequence
        // element; larger jumps step over positions other matchers consumed.
        int offset;
        if (increment == 1)
        {
            offset = position + 1;
            iterator.Index = offset;
        }
        else
        {
            offset = iterator.Increment(increment);
        }

        if (offset < 0)
        {
            return false;
        }

        // A transparent record is stepped over unless it matches the sequence
        // position itself; a solid record that fails the shape test refuses the
        // whole match.
        while (i < sequence.Length && i < MaxContextLength && offset < buffer.Count)
        {
            if (iterator.IsPropertySkipped(offset))
            {
                offset = ++iterator.Index;
                continue;
            }

            ref GlyphShapingData data = ref buffer[offset];
            if (iterator.MayMatch(ref data) && condition(sequence[i], data, state))
            {
                if (matches.Length == MaxContextLength)
                {
                    matches[i] = offset;
                }

                i++;
                matchEnd = offset + 1;
                offset = ++iterator.Index;
                continue;
            }

            if (!iterator.IsTransparent(ref data))
            {
                break;
            }

            offset = ++iterator.Index;
        }

        iterator.Index = position;
        return i == sequence.Length;
    }

    /// <summary>
    /// Matches a sequence of elements against glyphs using an increment-based approach.
    /// </summary>
    /// <typeparam name="T">The type of sequence elements to match.</typeparam>
    /// <param name="increment">The initial increment from the iterator's current position.</param>
    /// <param name="sequence">The array of elements to match.</param>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="condition">The condition function to test each element against glyph data.</param>
    /// <param name="matches">A span to store matched glyph indices, or default if not needed.</param>
    /// <returns><see langword="true"/> if all elements in the sequence were matched; otherwise, <see langword="false"/>.</returns>
    private static bool Match<T>(
        int increment,
        T[] sequence,
        SkippingGlyphIterator iterator,
        Func<T, GlyphShapingData, bool> condition,
        Span<int> matches)
        => Match(increment, sequence, iterator, condition, matches, out _);

    /// <summary>
    /// Matches a sequence of elements against glyphs using an increment-based
    /// approach, reporting the position one past the final matched element so
    /// lookahead can start exactly where the input ended.
    /// </summary>
    /// <typeparam name="T">The type of sequence elements to match.</typeparam>
    /// <param name="increment">The initial increment from the iterator's current position.</param>
    /// <param name="sequence">The array of elements to match.</param>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="condition">The condition function to test each element against glyph data.</param>
    /// <param name="matches">A span to store matched glyph indices, or default if not needed.</param>
    /// <param name="matchEnd">The position one past the final matched element.</param>
    /// <returns><see langword="true"/> if all elements in the sequence were matched; otherwise, <see langword="false"/>.</returns>
    private static bool Match<T>(
        int increment,
        T[] sequence,
        SkippingGlyphIterator iterator,
        Func<T, GlyphShapingData, bool> condition,
        Span<int> matches,
        out int matchEnd)
    {
        int position = iterator.Index;
        ShapingBuffer buffer = iterator.Collection;
        matchEnd = position + 1;
        int i = 0;

        // A buffer without a transparent record in reach keeps the solid-glyph
        // walk: every stepped-to record must match or the rule is refused.
        if (!iterator.MatchTransparencyActive)
        {
            int solidOffset = iterator.Increment(increment);
            if (solidOffset < 0)
            {
                return false;
            }

            while (i < sequence.Length && i < MaxContextLength && solidOffset < buffer.Count)
            {
                ref GlyphShapingData solidData = ref buffer[solidOffset];
                if (!iterator.MayMatch(ref solidData) || !condition(sequence[i], solidData))
                {
                    break;
                }

                if (matches.Length == MaxContextLength)
                {
                    matches[i] = solidOffset;
                }

                i++;
                matchEnd = solidOffset + 1;
                solidOffset = iterator.Next();
            }

            iterator.Index = position;
            return i == sequence.Length;
        }

        // A single forward step enters the walk directly so a transparent record
        // at the next position still gets its chance to match the first sequence
        // element; larger jumps step over positions other matchers consumed.
        int offset;
        if (increment == 1)
        {
            offset = position + 1;
            iterator.Index = offset;
        }
        else
        {
            offset = iterator.Increment(increment);
        }

        if (offset < 0)
        {
            return false;
        }

        // A transparent record is stepped over unless it matches the sequence
        // position itself; a solid record that fails the shape test refuses the
        // whole match.
        while (i < sequence.Length && i < MaxContextLength && offset < buffer.Count)
        {
            if (iterator.IsPropertySkipped(offset))
            {
                offset = ++iterator.Index;
                continue;
            }

            ref GlyphShapingData data = ref buffer[offset];
            if (iterator.MayMatch(ref data) && condition(sequence[i], data))
            {
                if (matches.Length == MaxContextLength)
                {
                    matches[i] = offset;
                }

                i++;
                matchEnd = offset + 1;
                offset = ++iterator.Index;
                continue;
            }

            if (!iterator.IsTransparent(ref data))
            {
                break;
            }

            offset = ++iterator.Index;
        }

        iterator.Index = position;
        return i == sequence.Length;
    }

    /// <summary>
    /// Matches a sequence of elements against glyphs using a directional (forward/backward) approach.
    /// </summary>
    /// <typeparam name="T">The type of sequence elements to match.</typeparam>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="startIndex">The starting index in the buffer.</param>
    /// <param name="sequence">The array of elements to match.</param>
    /// <param name="direction">The direction to iterate (forward or backward).</param>
    /// <param name="endExclusive">The exclusive end index in the buffer.</param>
    /// <param name="condition">The condition function to test each element against glyph data.</param>
    /// <param name="matches">A span to store matched glyph indices, or default if not needed.</param>
    /// <param name="matchEnd">The position one past the final matched element; meaningful for forward matching.</param>
    /// <returns><see langword="true"/> if all elements in the sequence were matched; otherwise, <see langword="false"/>.</returns>
    private static bool Match<T>(
        SkippingGlyphIterator iterator,
        int startIndex,
        T[] sequence,
        MatchDirection direction,
        int endExclusive,
        Func<T, GlyphShapingData, bool> condition,
        Span<int> matches,
        out int matchEnd)
    {
        matchEnd = startIndex;
        if (sequence.Length == 0)
        {
            return true;
        }

        int saved = iterator.Index;
        int offset = startIndex;
        int step = direction == MatchDirection.Forward ? 1 : -1;

        ShapingBuffer buffer = iterator.Collection;
        int limit = Math.Min(endExclusive, buffer.Count);

        // A transparent record is stepped over unless it matches the sequence
        // position itself; a solid record that fails the shape test refuses the
        // whole match.
        int i = 0;
        while (i < sequence.Length && i < MaxContextLength)
        {
            if (offset < 0 || offset >= limit)
            {
                iterator.Index = saved;
                return false;
            }

            if (iterator.IsPropertySkipped(offset))
            {
                offset += step;
                continue;
            }

            ref GlyphShapingData data = ref buffer[offset];
            if (iterator.MayMatch(ref data) && condition(sequence[i], data))
            {
                if (matches.Length == MaxContextLength)
                {
                    matches[i] = offset;
                }

                i++;
                matchEnd = offset + 1;
                offset += step;
                continue;
            }

            if (!iterator.IsTransparent(ref data))
            {
                iterator.Index = saved;
                return false;
            }

            offset += step;
        }

        iterator.Index = saved;
        return true;
    }
}
