// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using SixLabors.Fonts.Tables.AdvancedTypographic;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts;

/// <summary>
/// The shaping pipeline: font-run itemization, bidi analysis, glyph population, GSUB
/// substitution, and GPOS positioning. <see cref="TextLayout"/> and the public shaping
/// API both consume this single pipeline; layout composes and positions lines from its
/// output but performs no shaping of its own.
/// </summary>
public static partial class TextShaper
{
    /// <summary>
    /// The pool of reusable pipeline state. A scratch is exclusively owned between
    /// <see cref="ObjectPool{T}.Get"/> and <see cref="ObjectPool{T}.Return"/>, and the
    /// shaped result is copied out by value before the scratch is returned, so nothing
    /// pooled escapes a call. Retained scratch storage stays at its high-water mark.
    /// </summary>
    private static readonly ObjectPool<ShapingScratch> ScratchPool = new(new ShapingScratchPooledObjectPolicy());

    /// <summary>
    /// Resolves the ordered sequence of <see cref="TextRun"/> instances that cover <paramref name="text"/>.
    /// </summary>
    /// <remarks>
    /// If <see cref="TextOptions.TextRuns"/> is <see langword="null"/> or empty, a single run covering the entire
    /// grapheme range of <paramref name="text"/> using <see cref="TextOptions.Font"/> is returned. Otherwise the
    /// supplied runs are ordered, gaps are filled with default-font runs, and overlapping ranges are trimmed.
    /// </remarks>
    /// <param name="text">The text to partition into runs.</param>
    /// <param name="options">The text options supplying the default font and optional user-defined runs.</param>
    /// <returns>The resolved runs that together cover the entire grapheme range of <paramref name="text"/>.</returns>
    internal static IReadOnlyList<TextRun> BuildTextRuns(ReadOnlySpan<char> text, TextOptions options)
    {
        int start = 0;
        var graphemeProbe = ShapingProbe.Enter();
        int end = text.GetGraphemeCount();
        ShapingProbe.Exit(ShapingProbe.GraphemeCount, graphemeProbe);
        if (end == 0)
        {
            return [];
        }

        if (options.TextRuns is null || options.TextRuns.Count == 0)
        {
            TextRun textRun = new()
            {
                Start = 0,
                End = end,
                Font = options.Font
            };

            textRun.ResolveFontWeight(options.FontWeight);
            return [textRun];
        }

        List<TextRun> textRuns = [];
        foreach (TextRun textRun in options.TextRuns.OrderBy(x => x.Start))
        {
            // Fill gaps within runs.
            if (textRun.Start > start)
            {
                textRuns.Add(new()
                {
                    Start = start,
                    End = textRun.Start,
                    Font = options.Font
                });
            }

            // Add the current run, ensuring the font is not null.
            textRun.Font ??= options.Font;

            if (textRun.Placeholder.HasValue && textRun.End != textRun.Start)
            {
                throw new ArgumentException("Placeholder text runs must be zero-length insertion runs.", nameof(options));
            }

            // Ensure that the previous run does not overlap the current.
            if (textRuns.Count > 0)
            {
                int prevIndex = textRuns.Count - 1;
                TextRun previous = textRuns[prevIndex];
                previous.End = Math.Min(previous.End, textRun.Start);
            }

            textRuns.Add(textRun);
            start = textRun.End;
        }

        // Add a final run if required.
        if (start < end)
        {
            textRuns.Add(new()
            {
                Start = start,
                End = end,
                Font = options.Font
            });
        }

        foreach (TextRun textRun in textRuns)
        {
            textRun.ResolveFontWeight(options.FontWeight);
        }

        return textRuns;
    }

    /// <summary>
    /// Shapes <paramref name="text"/> into shaping state that is independent of the wrapping length.
    /// </summary>
    /// <remarks>
    /// Performs the font-run build, bidi analysis, GSUB/GPOS shaping (including fallback font
    /// resolution for unmapped codepoints). The result contains the positioned glyph buffer
    /// and bidi state used by logical line composition.
    /// </remarks>
    /// <param name="text">The text to process.</param>
    /// <param name="options">The text options used while shaping.</param>
    /// <returns>The wrapping-independent shaping state.</returns>
    internal static ShapedText ShapeText(ReadOnlySpan<char> text, TextOptions options)
    {
        // The single pooling site for the shaping pipeline: rent the reusable pipeline
        // state, shape, copy the result out by value, and return the state before the
        // caller sees the result. Every consumer of shaping goes through here and
        // shares the pooled machinery without knowing it exists.
        ShapingScratch scratch = ScratchPool.Get();
        try
        {
            (ShapingBuffer substitutions, ShapingBuffer positionings) = scratch.Prepare(options);
            return ShapeText(text, options, substitutions, positionings);
        }
        finally
        {
            ScratchPool.Return(scratch);
        }
    }

    /// <summary>
    /// Shapes <paramref name="text"/> using caller-supplied shaping collections. Both
    /// collections must share one <see cref="ShapingFeatureMap"/> and already reflect
    /// <paramref name="options"/>.
    /// </summary>
    /// <param name="text">The text to process.</param>
    /// <param name="options">The text options used while shaping.</param>
    /// <param name="substitutions">The substitution buffer to shape into.</param>
    /// <param name="positionings">The positioning buffer to shape into.</param>
    /// <returns>The wrapping-independent shaping state.</returns>
    private static ShapedText ShapeText(
        ReadOnlySpan<char> text,
        TextOptions options,
        ShapingBuffer substitutions,
        ShapingBuffer positionings)
    {
        // Gather the font and fallbacks.
        Font[] fallbackFonts = (options.FallbackFontFamilies?.Count > 0)
            ? [.. options.FallbackFontFamilies.Select(x => new Font(x, options.Font.Size, options.Font.RequestedStyle))]
            : [];

        LayoutMode layoutMode = options.LayoutMode;

        var probe = ShapingProbe.Enter();

        // Analyse the text for bidi directional runs.
        BidiAlgorithm bidi = BidiAlgorithm.Instance.Value!;
        BidiData bidiData = BidiData.Instance.Value!;
        bidiData.Init(text, (sbyte)options.TextDirection);

        if (options.TextBidiMode == TextBidiMode.Override)
        {
            BidiCharacterType overrideType = options.TextDirection == TextDirection.Auto
                ? (bidi.ResolveEmbeddingLevel(bidiData.Types) == 1 ? BidiCharacterType.RightToLeft : BidiCharacterType.LeftToRight)
                : (options.TextDirection == TextDirection.RightToLeft ? BidiCharacterType.RightToLeft : BidiCharacterType.LeftToRight);

            for (int i = 0; i < bidiData.Types.Length; i++)
            {
                // Bidi override is a higher-level protocol override: real text behaves as the requested
                // strong direction, while separators and explicit bidi controls keep their structural role.
                bidiData.Types[i] = bidiData.Types[i] switch
                {
                    BidiCharacterType.ParagraphSeparator
                    or BidiCharacterType.SegmentSeparator
                    or BidiCharacterType.BoundaryNeutral
                    or BidiCharacterType.LeftToRightEmbedding
                    or BidiCharacterType.RightToLeftEmbedding
                    or BidiCharacterType.LeftToRightOverride
                    or BidiCharacterType.RightToLeftOverride
                    or BidiCharacterType.PopDirectionalFormat
                    or BidiCharacterType.LeftToRightIsolate
                    or BidiCharacterType.RightToLeftIsolate
                    or BidiCharacterType.FirstStrongIsolate
                    or BidiCharacterType.PopDirectionalIsolate => bidiData.Types[i],
                    _ => overrideType,
                };
            }
        }

        // Purely left-to-right text resolves to a single even-level run without running
        // the bidirectional algorithm: with no right-to-left or directional codepoints and
        // a left-to-right (or auto) paragraph direction, every resolved level is zero.
        // This is the overwhelmingly common case for Latin text and skips the full UAX#9
        // pass. An overridden or right-to-left paragraph always resolves levels.
        BidiRun[] bidiRuns;
        if (options.TextDirection != TextDirection.RightToLeft
            && options.TextBidiMode != TextBidiMode.Override
            && bidiData.IsUniformLeftToRight)
        {
            bidiRuns = [new BidiRun(BidiCharacterType.LeftToRight, 0, 0, bidiData.Types.Length)];
        }
        else
        {
            bidi.Process(bidiData);
            bidiRuns = [.. BidiRun.CoalesceLevels(bidi.ResolvedLevels)];
        }

        int[] bidiMap = new int[bidiData.Types.Length];
        Array.Fill(bidiMap, -1);
        ShapingProbe.Exit(ShapingProbe.Bidi, probe);

        probe = ShapingProbe.Enter();

        // Incrementally build out buffer of glyphs. Both buffers share the run list so
        // per-glyph run indices agree when records are seeded across them.
        IReadOnlyList<TextRun> textRuns = BuildTextRuns(text, options);
        substitutions.SetTextRuns(textRuns);
        positionings.SetTextRuns(textRuns);
        ShapingProbe.Exit(ShapingProbe.BuildTextRuns, probe);

        // First do multiple font runs using the individual text runs.
        bool complete = true;
        int textRunIndex = 0;
        int codePointIndex = 0;
        int bidiRunIndex = 0;

        // Single-run fast path: shape and seed one buffer in place and flip it to the
        // positioning role, so no record is copied between buffers. When fallback
        // glyphs remain and fallback fonts exist, fall through to the general
        // cross-buffer seed so the fallback passes can merge into the accumulator.
        ShapingBuffer shaped = positionings;
        if (textRuns.Count == 1 && !textRuns[0].Placeholder.HasValue)
        {
            TextRun onlyRun = textRuns[0];
            PopulateAndSubstitute(
                text,
                onlyRun.Start,
                textRuns,
                ref textRunIndex,
                ref codePointIndex,
                ref bidiRunIndex,
                onlyRun.ResolvedFont,
                bidiRuns,
                bidiMap,
                substitutions);

            var seedProbe = ShapingProbe.Enter();
            complete = substitutions.SeedMetricsInPlace(onlyRun.ResolvedFont);
            ShapingProbe.Exit(ShapingProbe.MetricsAdd, seedProbe);

            if (complete || fallbackFonts.Length == 0)
            {
                substitutions.SetRole(ShapingBufferRole.Positioning);
                shaped = substitutions;
                complete = true;
            }
            else
            {
                seedProbe = ShapingProbe.Enter();
                complete = positionings.TryAdd(onlyRun.ResolvedFont, substitutions);
                ShapingProbe.Exit(ShapingProbe.MetricsAdd, seedProbe);
            }

            goto FallbackPasses;
        }

        for (int runIndex = 0; runIndex < textRuns.Count; runIndex++)
        {
            TextRun textRun = textRuns[runIndex];
            if (textRun.Placeholder.HasValue)
            {
                substitutions.Clear();

                while (bidiRunIndex < bidiRuns.Length && codePointIndex == bidiRuns[bidiRunIndex].End)
                {
                    bidiRunIndex++;
                }

                // Placeholder direction comes from the bidi region at the insertion
                // point. If the insertion point is after all source text, use the
                // default even/LTR embedding level.
                BidiRun placeholderBidiRun = bidiRunIndex < bidiRuns.Length
                    ? bidiRuns[bidiRunIndex]
                    : new(BidiCharacterType.LeftToRight, 2, codePointIndex, 0);

                // Placeholder runs are inserted into the layout stream and do not consume
                // source graphemes, source codepoints, or bidi runs. The loop position
                // is the placeholder's own run index; the populate tracker below may
                // lag it between runs.
                substitutions.AddPlaceholder(
                    CodePoint.ObjectReplacementChar,
                    placeholderBidiRun,
                    (ushort)runIndex,
                    codePointIndex);

                complete &= positionings.TryAdd(textRun.ResolvedFont, substitutions);
                textRunIndex++;
                continue;
            }

            if (!DoFontRun(
                textRun.Slice(text),
                textRun.Start,
                textRuns,
                ref textRunIndex,
                ref codePointIndex,
                ref bidiRunIndex,
                false,
                textRun.ResolvedFont,
                bidiRuns,
                bidiMap,
                substitutions,
                positionings))
            {
                complete = false;
            }
        }

        FallbackPasses:
        if (!complete)
        {
            // Finally try our fallback fonts.
            // We do a complete run here across the whole buffer.
            foreach (Font font in fallbackFonts)
            {
                textRunIndex = 0;
                codePointIndex = 0;
                bidiRunIndex = 0;
                if (DoFontRun(
                    text,
                    0,
                    textRuns,
                    ref textRunIndex,
                    ref codePointIndex,
                    ref bidiRunIndex,
                    true,
                    font,
                    bidiRuns,
                    bidiMap,
                    substitutions,
                    positionings))
                {
                    break;
                }
            }
        }

        // Update the positions of the glyphs in the completed buffer.
        // Each set of metrics is associated with single font and will only be updated
        // by that font so it's safe to use a single buffer.
        probe = ShapingProbe.Enter();
        Font? lastFont = null;
        for (int i = 0; i < textRuns.Count; i++)
        {
            TextRun textRun = textRuns[i];

            Font font = textRun.ResolvedFont;
            if (font == lastFont)
            {
                continue;
            }

            font.FontMetrics.UpdatePositions(shaped);
            lastFont = font;
        }

        foreach (Font font in fallbackFonts)
        {
            font.FontMetrics.UpdatePositions(shaped);
        }

        ShapingProbe.Exit(ShapingProbe.Positioning, probe);

        // Copy the shaped result out of the pooled collections: run-constant state
        // deduplicates into a run table and per-glyph state splits into parallel
        // identity and geometry arrays of pure values, so the scratch can go back to
        // the pool before consumption and no metrics reference survives shaping.
        ulong verticalMask = shaped.GetVerticalFeatureMask();
        int count = shaped.Count;
        ShapedGlyphInfo[] infos = new ShapedGlyphInfo[count];
        ShapedGlyphPosition[] positions = new ShapedGlyphPosition[count];
        List<ShapedTextRun> runs = [];

        Font? runFont = null;
        int runTextRunIndex = -1;
        BidiRun runBidiRun = default;
        for (int i = 0; i < count; i++)
        {
            ref GlyphShapingData shaping = ref shaped[i];
            ref ShapingBuffer.GlyphMetricsEntry entry = ref shaped.MetricsAt(i);
            ref GlyphShapingPosition shapingPosition = ref shaped.PositionAt(i);

            // Placeholders carry a bidi run of their own, so they always cut a run.
            BidiRun shapingBidiRun = shaping.IsPlaceholder
                ? shaped.GetPlaceholderBidiRun(shaping.CodePointIndex)
                : default;
            if (entry.Font != runFont
                || shaping.TextRunIndex != runTextRunIndex
                || (shaping.IsPlaceholder && !shapingBidiRun.Equals(runBidiRun)))
            {
                runFont = entry.Font;
                runTextRunIndex = shaping.TextRunIndex;
                runBidiRun = shapingBidiRun;
                runs.Add(new(entry.Font, entry.PointSize, shaped.TextRuns[shaping.TextRunIndex], shapingBidiRun));
            }

            ShapedGlyphFlags flags = ShapedGlyphFlags.None;
            if (shaping.IsPlaceholder)
            {
                flags |= ShapedGlyphFlags.Placeholder;
            }

            if (shaping.IsSubstituted)
            {
                flags |= ShapedGlyphFlags.Substituted;
            }

            if (shaping.IsDecomposed)
            {
                flags |= ShapedGlyphFlags.Decomposed;
            }

            if ((shaping.AppliedFeatureMask & verticalMask) != 0)
            {
                flags |= ShapedGlyphFlags.VerticalSubstituted;
            }

            infos[i] = new(
                shaping.CodePointIndex,
                shaping.CodePoint,
                shaping.CodePointCount,
                entry.Metrics.GlyphId,
                (ushort)(runs.Count - 1),
                flags);

            positions[i] = new(
                entry.GetAdvanceWidth(in shapingPosition),
                entry.GetAdvanceHeight(in shapingPosition),
                new Vector2(shapingPosition.Bounds.X, shapingPosition.Bounds.Y),
                entry.Metrics.Offset);
        }

        return new ShapedText([.. runs], infos, positions, bidiRuns, bidiMap, layoutMode);
    }

    /// <summary>
    /// Shapes a single font run — maps codepoints in <paramref name="text"/> to glyph ids using
    /// <paramref name="font"/>, then runs GSUB substitution and GPOS positioning. Codepoints that
    /// the font cannot map are recorded for a later fallback pass.
    /// </summary>
    /// <param name="text">The run-relative text slice to shape.</param>
    /// <param name="start">The starting grapheme index (absolute within the original input).</param>
    /// <param name="textRuns">The ordered list of resolved text runs.</param>
    /// <param name="textRunIndex">The index of the current text run; advanced as the enumerator crosses run boundaries.</param>
    /// <param name="codePointIndex">The running codepoint index (absolute within the original input).</param>
    /// <param name="bidiRunIndex">The running bidi run index.</param>
    /// <param name="isFallbackRun">
    /// <see langword="true"/> if this call is the fallback-font pass (in which case unmapped codepoints
    /// may still emit <c>.notdef</c> glyphs).
    /// </param>
    /// <param name="font">The font to shape with.</param>
    /// <param name="bidiRuns">The resolved bidi runs covering the whole input.</param>
    /// <param name="bidiMap">A codepoint → bidi-run mapping accumulated across shaping passes.</param>
    /// <param name="substitutions">The GSUB substitution buffer to write into.</param>
    /// <param name="positionings">The GPOS positioning buffer to write into.</param>
    /// <returns>
    /// <see langword="true"/> if every codepoint mapped successfully; <see langword="false"/> if any
    /// codepoint remains unmapped (so a fallback-font pass is needed).
    /// </returns>
    private static bool DoFontRun(
        ReadOnlySpan<char> text,
        int start,
        IReadOnlyList<TextRun> textRuns,
        ref int textRunIndex,
        ref int codePointIndex,
        ref int bidiRunIndex,
        bool isFallbackRun,
        Font font,
        BidiRun[] bidiRuns,
        int[] bidiMap,
        ShapingBuffer substitutions,
        ShapingBuffer positionings)
    {
        PopulateAndSubstitute(
            text,
            start,
            textRuns,
            ref textRunIndex,
            ref codePointIndex,
            ref bidiRunIndex,
            font,
            bidiRuns,
            bidiMap,
            substitutions);

        var seedProbe = ShapingProbe.Enter();
        bool result = !isFallbackRun
            ? positionings.TryAdd(font, substitutions)
            : positionings.TryUpdate(font, substitutions);
        ShapingProbe.Exit(ShapingProbe.MetricsAdd, seedProbe);
        return result;
    }

    /// <summary>
    /// Populates the substitution buffer from <paramref name="text"/> and runs bidi
    /// mirroring and GSUB substitution over it, leaving the shaped records in the
    /// buffer for either in-place metrics seeding or a cross-buffer seed.
    /// </summary>
    /// <param name="text">The run-relative text slice to shape.</param>
    /// <param name="start">The starting grapheme index (absolute within the original input).</param>
    /// <param name="textRuns">The ordered list of resolved text runs.</param>
    /// <param name="textRunIndex">The index of the current text run; advanced as the enumerator crosses run boundaries.</param>
    /// <param name="codePointIndex">The running codepoint index (absolute within the original input).</param>
    /// <param name="bidiRunIndex">The running bidi run index.</param>
    /// <param name="font">The font to shape with.</param>
    /// <param name="bidiRuns">The resolved bidi runs covering the whole input.</param>
    /// <param name="bidiMap">A codepoint → bidi-run mapping accumulated across shaping passes.</param>
    /// <param name="substitutions">The GSUB substitution buffer to write into.</param>
    private static void PopulateAndSubstitute(
        ReadOnlySpan<char> text,
        int start,
        IReadOnlyList<TextRun> textRuns,
        ref int textRunIndex,
        ref int codePointIndex,
        ref int bidiRunIndex,
        Font font,
        BidiRun[] bidiRuns,
        int[] bidiMap,
        ShapingBuffer substitutions)
    {
        // For each run we start with a fresh substitution buffer to avoid
        // overwriting the glyph ids.
        substitutions.Clear();

        var probe = ShapingProbe.Enter();

        // A font without variation sequences never consumes the following codepoint
        // during glyph lookup, so the per-codepoint lookahead decode is skipped.
        bool hasVariationSequences = font.FontMetrics.HasUnicodeVariationSequences;

        // Enumerate through each grapheme in the text.
        int graphemeIndex = start;
        SpanGraphemeEnumerator graphemeEnumerator = new(text);
        while (graphemeEnumerator.MoveNext())
        {
            ReadOnlySpan<char> grapheme = graphemeEnumerator.Current.Span;
            int graphemeMax = grapheme.Length - 1;
            int graphemeCodePointIndex = 0;
            int charIndex = 0;

            while (textRunIndex < textRuns.Count - 1 && graphemeIndex == textRuns[textRunIndex].End)
            {
                textRunIndex++;
            }

            // Now enumerate through each codepoint in the grapheme.
            bool skipNextCodePoint = false;
            SpanCodePointEnumerator codePointEnumerator = new(grapheme);
            while (codePointEnumerator.MoveNext())
            {
                if (codePointIndex == bidiRuns[bidiRunIndex].End)
                {
                    bidiRunIndex++;
                }

                if (skipNextCodePoint)
                {
                    codePointIndex++;
                    graphemeCodePointIndex++;
                    continue;
                }

                bidiMap[codePointIndex] = bidiRunIndex;

                int charsConsumed = 0;
                CodePoint current = codePointEnumerator.Current;
                charIndex += current.Utf16SequenceLength;
                CodePoint? next = hasVariationSequences && graphemeCodePointIndex < graphemeMax
                    ? CodePoint.DecodeFromUtf16At(grapheme, charIndex, out charsConsumed)
                    : null;

                charIndex += charsConsumed;

                // Get the glyph id for the codepoint and add to the buffer.
                bool hasGlyph = substitutions.TryGetGlyphId(font.FontMetrics, current, next, out ushort glyphId, out skipNextCodePoint);

                // Unsupported default-ignorable code points such as FE0F should not block
                // GSUB sequences like emoji ZWJ ligatures. Preserve joiners explicitly.
                if (!hasGlyph &&
                    UnicodeUtility.IsDefaultIgnorableCodePoint((uint)current.Value) &&
                    !UnicodeUtility.ShouldRenderWhiteSpaceOnly(current) &&
                    !CodePoint.IsZeroWidthJoiner(current) &&
                    !CodePoint.IsZeroWidthNonJoiner(current))
                {
                    codePointIndex++;
                    graphemeCodePointIndex++;
                    continue;
                }

                substitutions.AddGlyph(glyphId, current, (TextDirection)bidiRuns[bidiRunIndex].Direction, (ushort)textRunIndex, codePointIndex);

                codePointIndex++;
                graphemeCodePointIndex++;
            }

            graphemeIndex++;
        }

        ShapingProbe.Exit(ShapingProbe.Populate, probe);

        // Apply the simple and complex substitutions.
        // TODO: Investigate HarfBuzz normalizer.
        probe = ShapingProbe.Enter();
        SubstituteBidiMirrors(font.FontMetrics, substitutions);
        ShapingProbe.Exit(ShapingProbe.Mirrors, probe);

        probe = ShapingProbe.Enter();
        font.FontMetrics.ApplySubstitution(substitutions);
        ShapingProbe.Exit(ShapingProbe.Substitution, probe);
    }

    /// <summary>
    /// Substitutes mirrored bracket glyphs (for example <c>(</c> ↔ <c>)</c>) inside right-to-left
    /// bidi runs, per Unicode Bidirectional Algorithm rule L4. Relies on the font's <c>rtlm</c>
    /// feature when available and falls back to the Unicode mirror table otherwise.
    /// </summary>
    /// <param name="fontMetrics">The font metrics used to look up mirrored glyph ids.</param>
    /// <param name="buffer">The substitution buffer whose glyphs will be rewritten in place.</param>
    private static void SubstituteBidiMirrors(FontMetrics fontMetrics, ShapingBuffer buffer)
    {
        for (int i = 0; i < buffer.Count; i++)
        {
            ref GlyphShapingData data = ref buffer[i];

            if (data.Direction != TextDirection.RightToLeft)
            {
                continue;
            }

            if (!CodePoint.TryGetBidiMirror(data.CodePoint, out CodePoint mirror))
            {
                continue;
            }

            if (fontMetrics.TryGetGlyphId(mirror, out ushort glyphId))
            {
                buffer.Replace(i, glyphId, KnownFeatureTags.RightToLeftMirroredForms);
            }
        }

        // TODO: This only replaces certain glyphs. We should investigate the specification further.
        // https://www.unicode.org/reports/tr50/#vertical_alternates
        if (buffer.TextOptions.LayoutMode.IsHorizontal())
        {
            return;
        }

        for (int i = 0; i < buffer.Count; i++)
        {
            ref GlyphShapingData data = ref buffer[i];
            if (CodePoint.GetVerticalOrientationType(data.CodePoint) is VerticalOrientationType.Upright or VerticalOrientationType.TransformUpright)
            {
                continue;
            }

            if (!CodePoint.TryGetVerticalMirror(data.CodePoint, out CodePoint mirror))
            {
                continue;
            }

            if (fontMetrics.TryGetGlyphId(mirror, out ushort glyphId))
            {
                buffer.Replace(i, glyphId, KnownFeatureTags.VerticalAlternates);
            }
        }
    }

    /// <summary>
    /// The pooling policy for <see cref="ShapingScratch"/> instances: scratch state is
    /// reset on acquisition by <see cref="ShapingScratch.Prepare"/>, so returned
    /// instances are always accepted.
    /// </summary>
    private sealed class ShapingScratchPooledObjectPolicy : IPooledObjectPolicy<ShapingScratch>
    {
        /// <inheritdoc/>
        public ShapingScratch Create() => new();

        /// <inheritdoc/>
        public bool Return(ShapingScratch obj) => true;
    }
}
