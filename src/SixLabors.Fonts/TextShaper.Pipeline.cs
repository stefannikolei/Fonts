// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

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
        int end = text.GetGraphemeCount();
        if (end == 0)
        {
            return [];
        }

        if (options.TextRuns is null || options.TextRuns.Count == 0)
        {
            TextRun textRun = new()
            {
                Start = 0,
                End = text.GetGraphemeCount(),
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
    /// resolution for unmapped codepoints). The result contains the positioned glyph collection
    /// and bidi state used by logical line composition.
    /// </remarks>
    /// <param name="text">The text to process.</param>
    /// <param name="options">The text options used while shaping.</param>
    /// <returns>The wrapping-independent shaping state.</returns>
    internal static ShapedText ShapeText(ReadOnlySpan<char> text, TextOptions options)
    {
        // One feature bit assignment for the whole pass: applied feature bits written
        // while substituting are read after the glyph data is copied into the
        // positioning collection, so both collections must agree on bit meaning.
        ShapingFeatureMap featureMap = new();
        GlyphSubstitutionCollection substitutions = new(options, featureMap);
        GlyphPositioningCollection positionings = new(options, featureMap);

        return ShapeText(text, options, substitutions, positionings);
    }

    /// <summary>
    /// Shapes <paramref name="text"/> using caller-supplied shaping collections,
    /// allowing a reusable buffer to supply pre-reset collections whose storage
    /// survives across calls. Both collections must share one
    /// <see cref="ShapingFeatureMap"/> and already reflect <paramref name="options"/>.
    /// </summary>
    /// <param name="text">The text to process.</param>
    /// <param name="options">The text options used while shaping.</param>
    /// <param name="substitutions">The substitution collection to shape into.</param>
    /// <param name="positionings">The positioning collection to shape into.</param>
    /// <returns>The wrapping-independent shaping state.</returns>
    internal static ShapedText ShapeText(
        ReadOnlySpan<char> text,
        TextOptions options,
        GlyphSubstitutionCollection substitutions,
        GlyphPositioningCollection positionings)
    {
        // Gather the font and fallbacks.
        Font[] fallbackFonts = (options.FallbackFontFamilies?.Count > 0)
            ? [.. options.FallbackFontFamilies.Select(x => new Font(x, options.Font.Size, options.Font.RequestedStyle))]
            : [];

        LayoutMode layoutMode = options.LayoutMode;

        var probe = ShapingProbe.Enter();

        // Analyse the text for bidi directional runs.
        BidiAlgorithm bidi = BidiAlgorithm.Instance.Value!;
        BidiData bidiData = new();
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

        // Incrementally build out collection of glyphs.
        IReadOnlyList<TextRun> textRuns = BuildTextRuns(text, options);
        ShapingProbe.Exit(ShapingProbe.BuildTextRuns, probe);

        // First do multiple font runs using the individual text runs.
        bool complete = true;
        int textRunIndex = 0;
        int codePointIndex = 0;
        int bidiRunIndex = 0;
        foreach (TextRun textRun in textRuns)
        {
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
                // source graphemes, source codepoints, or bidi runs.
                substitutions.AddPlaceholder(
                    CodePoint.ObjectReplacementChar,
                    placeholderBidiRun,
                    textRun,
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

        if (!complete)
        {
            // Finally try our fallback fonts.
            // We do a complete run here across the whole collection.
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

        // Update the positions of the glyphs in the completed collection.
        // Each set of metrics is associated with single font and will only be updated
        // by that font so it's safe to use a single collection.
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

            font.FontMetrics.UpdatePositions(positionings);
            lastFont = font;
        }

        foreach (Font font in fallbackFonts)
        {
            font.FontMetrics.UpdatePositions(positionings);
        }

        ShapingProbe.Exit(ShapingProbe.Positioning, probe);

        return new ShapedText(positionings, bidiRuns, bidiMap, layoutMode);
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
    /// <param name="substitutions">The GSUB substitution collection to write into.</param>
    /// <param name="positionings">The GPOS positioning collection to write into.</param>
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
        GlyphSubstitutionCollection substitutions,
        GlyphPositioningCollection positionings)
    {
        // For each run we start with a fresh substitution collection to avoid
        // overwriting the glyph ids.
        substitutions.Clear();

        var probe = ShapingProbe.Enter();

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
                CodePoint? next = graphemeCodePointIndex < graphemeMax
                    ? CodePoint.DecodeFromUtf16At(grapheme, charIndex, out charsConsumed)
                    : null;

                charIndex += charsConsumed;

                // Get the glyph id for the codepoint and add to the collection.
                bool hasGlyph = font.FontMetrics.TryGetGlyphId(current, next, out ushort glyphId, out skipNextCodePoint);

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

                substitutions.AddGlyph(glyphId, current, (TextDirection)bidiRuns[bidiRunIndex].Direction, textRuns[textRunIndex], codePointIndex);

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

        probe = ShapingProbe.Enter();
        bool result = !isFallbackRun
            ? positionings.TryAdd(font, substitutions)
            : positionings.TryUpdate(font, substitutions);
        ShapingProbe.Exit(ShapingProbe.MetricsAdd, probe);
        return result;
    }

    /// <summary>
    /// Substitutes mirrored bracket glyphs (for example <c>(</c> ↔ <c>)</c>) inside right-to-left
    /// bidi runs, per Unicode Bidirectional Algorithm rule L4. Relies on the font's <c>rtlm</c>
    /// feature when available and falls back to the Unicode mirror table otherwise.
    /// </summary>
    /// <param name="fontMetrics">The font metrics used to look up mirrored glyph ids.</param>
    /// <param name="collection">The substitution collection whose glyphs will be rewritten in place.</param>
    private static void SubstituteBidiMirrors(FontMetrics fontMetrics, GlyphSubstitutionCollection collection)
    {
        for (int i = 0; i < collection.Count; i++)
        {
            GlyphShapingData data = collection[i];

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
                collection.Replace(i, glyphId, KnownFeatureTags.RightToLeftMirroredForms);
            }
        }

        // TODO: This only replaces certain glyphs. We should investigate the specification further.
        // https://www.unicode.org/reports/tr50/#vertical_alternates
        if (collection.TextOptions.LayoutMode.IsHorizontal())
        {
            return;
        }

        for (int i = 0; i < collection.Count; i++)
        {
            GlyphShapingData data = collection[i];
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
                collection.Replace(i, glyphId, KnownFeatureTags.VerticalAlternates);
            }
        }
    }
}
