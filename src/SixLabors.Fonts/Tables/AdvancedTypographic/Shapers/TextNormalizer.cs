// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;
using SixLabors.Fonts.Unicode.Resources;

namespace SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;

/// <summary>
/// Takes the characters of a run apart, orders the marks, and joins them back
/// together, so that text written in any of the ways the standard calls equivalent
/// is shaped the same way.
/// </summary>
/// <remarks>
/// This follows the Unicode normalization algorithm but departs from it in one
/// respect that matters for shaping: a character comes apart, and a pair joins,
/// only when the font can draw the result. A font offering the joined form gets it,
/// because a joined form usually carries better mark positioning than a font's own
/// mark attachment would give; a font offering only the parts gets the parts.
/// </remarks>
internal static class TextNormalizer
{
    /// <summary>
    /// The hyphen used when a font cannot draw the non-breaking form.
    /// </summary>
    private const int HyphenCodePoint = 0x2010;

    /// <summary>
    /// The non-breaking hyphen, whose visible fallback is the ordinary hyphen.
    /// </summary>
    private const int NonBreakingHyphenCodePoint = 0x2011;

    /// <summary>
    /// The longest run of marks that is ordered. A run longer than this is left
    /// alone, because ordering it costs more than the ordering is worth.
    /// </summary>
    private const int MaxOrderedMarkRun = 32;

    /// <summary>
    /// The most parts one character is taken apart into. A canonical chain reaches
    /// three at its longest, so this leaves room to spare and bounds the gather.
    /// </summary>
    private const int MaxDecompositionParts = 8;

    /// <summary>
    /// The combining grapheme joiner, whose match transparency depends on whether it prevented mark reordering.
    /// </summary>
    private const int CombiningGraphemeJoinerCodePoint = 0x034F;

    /// <summary>
    /// Orders two records by the class that places their marks.
    /// </summary>
    private static readonly Comparison<GlyphShapingData> MarkOrder =
        static (a, b) => a.MarkOrderingClass - b.MarkOrderingClass;

    /// <summary>
    /// Normalizes the given run of the buffer.
    /// </summary>
    /// <param name="shaper">The shaper whose preference and joining rules apply.</param>
    /// <param name="fontMetrics">The font metrics, which decide what the font can draw.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the run's first record.</param>
    /// <param name="count">The number of records in the run.</param>
    /// <returns>The number of records the run gained or lost.</returns>
    public static int Normalize(BaseShaper shaper, FontMetrics fontMetrics, ShapingBuffer buffer, int index, int count)
    {
        NormalizationMode mode = shaper.NormalizationMode;
        if (mode == NormalizationMode.None || count == 0)
        {
            return 0;
        }

        int end = index + count;
        int candidate = index;
        while (candidate < end && (uint)buffer[candidate].CodePoint.Value < NormalizationData.FirstDecompositionCodePoint)
        {
            candidate++;
        }

        if (candidate == end)
        {
            // The generated lower bound precedes every canonical decomposition,
            // combining mark, and shaping control handled below. A run entirely
            // below it cannot change in any of the normalization rounds.
            return 0;
        }

        int before = buffer.Count;

        // A character standing on its own that the font already draws is left
        // untouched, unless the shaper asked for everything to come apart.
        bool mayShortCircuit = mode != NormalizationMode.ComposedDiacriticsNoShortCircuit;
        bool allMarksSeenAlone = Decompose(shaper, fontMetrics, buffer, index, ref count, mayShortCircuit);

        if (!allMarksSeenAlone)
        {
            OrderMarks(shaper, buffer, index, count);

            if (mode is NormalizationMode.ComposedDiacritics or NormalizationMode.ComposedDiacriticsNoShortCircuit)
            {
                Compose(shaper, fontMetrics, buffer, index, ref count);
            }
        }

        end = index + count;
        for (int i = index + 1; i + 1 < end; i++)
        {
            ref GlyphShapingData data = ref buffer[i];
            if (data.CodePoint.Value != CombiningGraphemeJoinerCodePoint)
            {
                continue;
            }

            int previousOrder = buffer[i - 1].MarkOrderingClass;
            int nextOrder = buffer[i + 1].MarkOrderingClass;
            if (nextOrder == 0 || previousOrder <= nextOrder)
            {
                // A joiner that did not block an otherwise-required mark swap may
                // be skipped by substitution matching. One that did block a swap
                // remains matchable so the text's explicit ordering barrier survives.
                data.IsHiddenIgnorable = false;
            }
        }

        return buffer.Count - before;
    }

    /// <summary>
    /// Takes the characters of the run apart. Characters that stand without a mark
    /// after them are passed over when the mode allows it; a character followed by
    /// marks always comes apart, so that the marks can be ordered against the parts.
    /// </summary>
    /// <param name="shaper">The shaper whose joining rules apply.</param>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the run's first record.</param>
    /// <param name="count">The number of records in the run, updated as it changes.</param>
    /// <param name="mayShortCircuit">Whether a character the font draws may be passed over.</param>
    /// <returns>
    /// <see langword="true"/> when no character of the run was followed by a mark, so
    /// there is nothing to order and nothing to join.
    /// </returns>
    private static bool Decompose(BaseShaper shaper, FontMetrics fontMetrics, ShapingBuffer buffer, int index, ref int count, bool mayShortCircuit)
    {
        bool allSimple = true;
        int i = index;
        int end = index + count;

        while (i < end)
        {
            // Find where the marks following this character begin. One character is
            // left ahead of them to carry them.
            int markStart = i + 1;
            while (markStart < end && !CodePoint.IsMark(buffer[markStart].CodePoint))
            {
                markStart++;
            }

            if (markStart < end)
            {
                markStart--;
            }

            // Up to that point the characters stand alone.
            while (i < markStart)
            {
                int delta = DecomposeOne(shaper, fontMetrics, buffer, i, mayShortCircuit, out int produced);
                end += delta;
                i += produced;
            }

            if (i >= end)
            {
                break;
            }

            allSimple = false;

            // The character and the marks after it come apart together.
            int markEnd = i + 1;
            while (markEnd < end && CodePoint.IsMark(buffer[markEnd].CodePoint))
            {
                markEnd++;
            }

            while (i < markEnd)
            {
                int delta = DecomposeOne(shaper, fontMetrics, buffer, i, false, out int produced);
                end += delta;
                markEnd += delta;
                i += produced;
            }
        }

        count = end - index;
        return allSimple;
    }

    /// <summary>
    /// Takes one character apart, replacing it with its parts when the font can draw
    /// them.
    /// </summary>
    /// <param name="shaper">The shaper whose joining rules apply.</param>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="i">The zero-based index of the record.</param>
    /// <param name="mayShortCircuit">Whether a character the font draws may be passed over.</param>
    /// <param name="produced">
    /// When this method returns, contains the number of records now standing where the
    /// character stood, which is how far the caller advances.
    /// </param>
    /// <returns>The number of records the buffer gained.</returns>
    private static int DecomposeOne(BaseShaper shaper, FontMetrics fontMetrics, ShapingBuffer buffer, int i, bool mayShortCircuit, out int produced)
    {
        CodePoint codePoint = buffer[i].CodePoint;

        if (codePoint.Value == NonBreakingHyphenCodePoint && !fontMetrics.TryGetGlyphId(codePoint, out _))
        {
            // The non-breaking character changes line-breaking behavior, not its
            // visible form. When the font omits it, use the ordinary hyphen glyph
            // while retaining the original text bookkeeping on the record.
            CodePoint hyphen = new(HyphenCodePoint);
            if (fontMetrics.TryGetGlyphId(hyphen, out ushort hyphenGlyph))
            {
                buffer.SetGlyphId(i, hyphenGlyph);
                produced = 1;
                return 0;
            }
        }

        // A character that cannot come apart is kept exactly as it stands, whether or
        // not the font draws it, so the font is never asked about it. Testing that
        // first matters: it is a search of a table held in read-only data, while asking
        // the font costs a dictionary probe, and for a run of text with nothing to take
        // apart the font would be asked once per character to no purpose.
        if (!shaper.TryDecompose(codePoint, out CodePoint _, out CodePoint _))
        {
            produced = 1;
            return 0;
        }

        if (mayShortCircuit && fontMetrics.TryGetGlyphId(codePoint, out _))
        {
            produced = 1;
            return 0;
        }

        if (TryWriteDecomposition(shaper, fontMetrics, buffer, i, codePoint, mayShortCircuit, out produced))
        {
            return produced - 1;
        }

        produced = 1;
        return 0;
    }

    /// <summary>
    /// Writes the parts of a character over it, walking down the chain of pairs until
    /// it reaches parts the font can draw. Nothing is written unless the whole chain
    /// resolves, so a character the font cannot draw and cannot take apart is left as
    /// it stands for the substitution passes to deal with.
    /// </summary>
    /// <param name="shaper">The shaper whose joining rules apply.</param>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="i">The zero-based index of the record.</param>
    /// <param name="codePoint">The character to take apart.</param>
    /// <param name="mayShortCircuit">Whether a drawable leading part may end recursive decomposition.</param>
    /// <param name="produced">When this method returns, contains the number of records written.</param>
    /// <returns><see langword="true"/> when the character came apart.</returns>
    private static bool TryWriteDecomposition(BaseShaper shaper, FontMetrics fontMetrics, ShapingBuffer buffer, int i, CodePoint codePoint, bool mayShortCircuit, out int produced)
    {
        produced = 0;

        // The chain is gathered whole before anything is written, so a chain that
        // turns out not to resolve leaves the record exactly as it was.
        Span<CodePoint> parts = stackalloc CodePoint[MaxDecompositionParts];
        Span<ushort> glyphs = stackalloc ushort[MaxDecompositionParts];

        int gathered = 0;
        if (!TryGather(shaper, fontMetrics, codePoint, parts, glyphs, mayShortCircuit, ref gathered))
        {
            return false;
        }

        // One part is a character standing for a single other one, so the record keeps
        // its place and only changes what it is.
        if (gathered == 1)
        {
            buffer.SetGlyphId(i, glyphs[0]);
            buffer[i].CodePoint = parts[0];
            produced = 1;
            return true;
        }

        buffer.Replace(i, glyphs[..gathered], KnownFeatureTags.GlyphCompositionDecomposition);
        for (int part = 0; part < gathered; part++)
        {
            buffer[i + part].CodePoint = parts[part];
        }

        produced = gathered;
        return true;
    }

    /// <summary>
    /// Gathers the parts a character comes apart into, in the order they are written,
    /// following the leading part down while the font cannot draw it.
    /// </summary>
    /// <param name="shaper">The shaper whose joining rules apply.</param>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="codePoint">The character to take apart.</param>
    /// <param name="parts">The characters gathered so far.</param>
    /// <param name="glyphs">The glyphs of those characters.</param>
    /// <param name="mayShortCircuit">Whether a drawable leading part may end recursive decomposition.</param>
    /// <param name="gathered">The number gathered so far, advanced as parts are added.</param>
    /// <returns><see langword="true"/> when the whole chain resolved to drawable parts.</returns>
    private static bool TryGather(BaseShaper shaper, FontMetrics fontMetrics, CodePoint codePoint, Span<CodePoint> parts, Span<ushort> glyphs, bool mayShortCircuit, ref int gathered)
    {
        if (!shaper.TryDecompose(codePoint, out CodePoint first, out CodePoint second))
        {
            return false;
        }

        // The trailing part has to be drawable for the pair to be usable at all, and
        // there has to be room left to record it.
        bool hasSecond = second.Value != 0;
        ushort secondId = 0;
        if (hasSecond && (!fontMetrics.TryGetGlyphId(second, out secondId) || gathered + 2 > parts.Length))
        {
            return false;
        }

        // The composed-diacritics mode keeps the shortest drawable leading part.
        // Indic and related shapers instead follow that part's decomposition to its
        // end even when the font can already draw it.
        bool hasFirst = fontMetrics.TryGetGlyphId(first, out ushort firstId);
        if (mayShortCircuit && hasFirst)
        {
            if (gathered + 1 > parts.Length)
            {
                return false;
            }

            parts[gathered] = first;
            glyphs[gathered] = firstId;
            gathered++;
        }
        else
        {
            // Recursive gathering writes into shared stack storage. Remember the
            // starting length so a leading decomposition that cannot be completed
            // leaves no partial parts before the drawable leading fallback is used.
            int checkpoint = gathered;
            if (!TryGather(shaper, fontMetrics, first, parts, glyphs, mayShortCircuit, ref gathered))
            {
                gathered = checkpoint;
                if (!hasFirst || gathered + 1 > parts.Length)
                {
                    return false;
                }

                parts[gathered] = first;
                glyphs[gathered] = firstId;
                gathered++;
            }
        }

        if (hasSecond)
        {
            if (gathered + 1 > parts.Length)
            {
                return false;
            }

            parts[gathered] = second;
            glyphs[gathered] = secondId;
            gathered++;
        }

        return true;
    }

    /// <summary>
    /// Orders each run of marks by the class that places them, leaving the marks of
    /// every script in the order they are drawn.
    /// </summary>
    /// <param name="shaper">The shaper whose script-specific mark ordering applies.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the run's first record.</param>
    /// <param name="count">The number of records in the run.</param>
    private static void OrderMarks(BaseShaper shaper, ShapingBuffer buffer, int index, int count)
    {
        int end = index + count;
        for (int i = index; i < end; i++)
        {
            if (buffer[i].MarkOrderingClass == 0)
            {
                continue;
            }

            int runEnd = i + 1;
            while (runEnd < end && buffer[runEnd].MarkOrderingClass != 0)
            {
                runEnd++;
            }

            if (runEnd - i <= MaxOrderedMarkRun)
            {
                buffer.Sort(i, runEnd, MarkOrder);
                shaper.ReorderNormalizedMarks(buffer, i, runEnd);
            }

            i = runEnd;
        }
    }

    /// <summary>
    /// Joins each mark onto the character it follows wherever the pair has a joined
    /// form the font can draw. A mark only joins the character that starts its run,
    /// and only when nothing between them outranks it, so the order settled by
    /// <see cref="OrderMarks"/> is never broken.
    /// </summary>
    /// <param name="shaper">The shaper whose joining rules apply.</param>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the run's first record.</param>
    /// <param name="count">The number of records in the run, updated as it changes.</param>
    private static void Compose(BaseShaper shaper, FontMetrics fontMetrics, ShapingBuffer buffer, int index, ref int count)
    {
        int end = index + count;
        int starter = index;

        for (int i = index + 1; i < end; i++)
        {
            CodePoint codePoint = buffer[i].CodePoint;

            // A character that is not a mark never joins the character before it.
            // Beyond sparing every neighbouring pair a lookup, this is what keeps a
            // font's own syllables and the letters they are built from apart.
            if (!CodePoint.IsMark(codePoint))
            {
                if (buffer[i].MarkOrderingClass == 0)
                {
                    starter = i;
                }

                continue;
            }

            int order = buffer[i].MarkOrderingClass;
            bool reachesStarter = starter == i - 1
                || buffer[i - 1].MarkOrderingClass < order;

            if (reachesStarter
                && shaper.TryCompose(buffer[starter].CodePoint, codePoint, out CodePoint composed)
                && fontMetrics.TryGetGlyphId(composed, out ushort composedId))
            {
                // The joined form takes the starter's place and carries the text of
                // both. Only the mark goes: any marks standing between the two
                // outrank it and keep both their place and their order.
                buffer.MergeGlyph(starter, i, composedId, KnownFeatureTags.GlyphCompositionDecomposition);
                buffer[starter].CodePoint = composed;

                end--;
                i--;
                continue;
            }

            if (order == 0)
            {
                starter = i;
            }
        }

        count = end - index;
    }
}
