// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using SixLabors.Fonts.Tables.AdvancedTypographic;

namespace SixLabors.Fonts;

/// <summary>
/// Encapsulates logic for laying out text.
/// </summary>
internal static partial class TextLayout
{
    /// <summary>
    /// The tag for the hanging baseline ('hang') in the font's baseline table.
    /// </summary>
    private static readonly Tag HangingBaselineTag = Tag.Parse("hang");

    /// <summary>
    /// The tag for the ideographic-under baseline ('ideo') in the font's baseline table.
    /// </summary>
    private static readonly Tag IdeographicBaselineTag = Tag.Parse("ideo");

    /// <summary>
    /// Lays out the supplied <see cref="TextBox"/>, streaming each laid-out glyph through the
    /// supplied <paramref name="visitor"/> in layout order using the supplied wrapping length for alignment.
    /// </summary>
    /// <remarks>
    /// The visitor type is constrained to a struct implementing <see cref="IGlyphLayoutVisitor"/>
    /// so the JIT specializes dispatch per visitor — no boxing or delegate allocation.
    /// </remarks>
    /// <typeparam name="TVisitor">The concrete visitor struct type.</typeparam>
    /// <param name="textBox">The shaped and line-broken text.</param>
    /// <param name="options">The text options used to lay out <paramref name="textBox"/>.</param>
    /// <param name="wrappingLength">The wrapping length in pixels. Use <c>-1</c> to disable wrapping.</param>
    /// <param name="visitor">The visitor that receives each positioned glyph.</param>
    public static void LayoutText<TVisitor>(
        TextBox textBox,
        TextOptions options,
        float wrappingLength,
        ref TVisitor visitor)
        where TVisitor : struct, IGlyphLayoutVisitor
        => LayoutText(textBox, options, wrappingLength, float.NegativeInfinity, float.PositiveInfinity, ref visitor);

    /// <summary>
    /// Gets a value indicating whether the layout walk must observe every broken line before it
    /// can place the first one. This is the authoritative statement of the walk's full-line-set
    /// dependencies; update it alongside any change to the line layout methods in this file.
    /// </summary>
    /// <remarks>
    /// The reversed layout orders place the last line first. Non-start text alignment and
    /// right-to-left blocks offset each line using the widest line advance, as do centered and
    /// right block alignment; centered and bottom block alignment on the cross axis sum every
    /// line's extent on the first line. <see cref="TextOptions.MaxLines"/> zero short-circuits
    /// breaking with its own empty-box direction rule in
    /// <see cref="BreakLines(in LogicalTextLine, TextOptions, float)"/>.
    /// </remarks>
    /// <param name="options">The text options used to lay out text.</param>
    /// <param name="textDirection">The resolved block-level text direction.</param>
    /// <returns><see langword="true"/> when layout needs the full line set.</returns>
    public static bool LayoutRequiresFullLineSet(TextOptions options, TextDirection textDirection)
        => options.MaxLines == 0 ||
           options.TextAlignment != TextAlignment.Start ||
           options.HorizontalAlignment != HorizontalAlignment.Left ||
           options.VerticalAlignment != VerticalAlignment.Top ||
           options.LayoutMode is not (LayoutMode.HorizontalTopBottom or LayoutMode.VerticalLeftRight or LayoutMode.VerticalMixedLeftRight) ||
           textDirection != TextDirection.LeftToRight;

    /// <summary>
    /// Computes the offset from the dominant baseline to the reference line selected by
    /// <paramref name="baseline"/> for the supplied font, in layout units. Horizontal layouts
    /// measure from the alphabetic baseline along Y, increasing toward the under side.
    /// Vertical layouts measure from the central column axis along X, increasing toward the
    /// over side: each baseline keeps the distance from the central baseline the horizontal
    /// metrics give it, exactly as CSS synthesizes vertical baseline tables, with dedicated
    /// vertical baseline table data taking precedence when the font provides it.
    /// <see cref="TextBaseline.LineBox"/> resolves to the line-box leading edge from the
    /// flow axis metrics so single-glyph callers share the text anchor model.
    /// </summary>
    /// <param name="baseline">The reference line to resolve.</param>
    /// <param name="font">The font whose metrics position the reference lines.</param>
    /// <param name="isVerticalLayout">Whether the layout flows vertically.</param>
    /// <returns>The offset from the dominant baseline in layout units.</returns>
    public static float GetBaselineOffset(TextBaseline baseline, Font font, bool isVerticalLayout)
    {
        FontMetrics metrics = font.FontMetrics;
        float scale = font.Size / metrics.ScaleFactor;

        if (baseline == TextBaseline.LineBox)
        {
            // The line box is column geometry rather than a baseline, so it anchors from the
            // metrics of the flow axis itself. The leading edge sits above the baseline, or
            // left of the column axis, by the delta-adjusted ascender: the delta centers the
            // em box within the font's declared line height, mirroring the layout engine's
            // cell model, so a LineBox-anchored glyph cell starts exactly at the origin.
            float flowAscender;
            float flowLineHeight;
            if (isVerticalLayout)
            {
                VerticalMetrics verticalMetrics = metrics.VerticalMetrics;
                flowAscender = verticalMetrics.Ascender;
                flowLineHeight = verticalMetrics.LineHeight;
            }
            else
            {
                HorizontalMetrics horizontalMetrics = metrics.HorizontalMetrics;
                flowAscender = horizontalMetrics.Ascender;
                flowLineHeight = horizontalMetrics.LineHeight;
            }

            float delta = ((flowLineHeight - metrics.UnitsPerEm) * scale) * .5F;
            return -((flowAscender * scale) - delta);
        }

        if (isVerticalLayout &&
            baseline is TextBaseline.Hanging or TextBaseline.Ideographic &&
            metrics.TryGetBaselineCoordinate(
                baseline == TextBaseline.Hanging ? HangingBaselineTag : IdeographicBaselineTag,
                true,
                out short vertical))
        {
            // Dedicated vertical axis data positions the baseline as an X coordinate from
            // the em box leading edge; re-centering on the column axis subtracts half the em.
            return (vertical - (metrics.UnitsPerEm * .5F)) * scale;
        }

        // Every baseline anchor derives from the horizontal metrics: its height above the
        // alphabetic baseline in horizontal layout is also its distance from the central
        // baseline in vertical layout, which is how CSS synthesizes vertical baselines for
        // fonts without dedicated vertical baseline data.
        HorizontalMetrics horizontal = metrics.HorizontalMetrics;
        float ascender = horizontal.Ascender * scale;
        float descender = horizontal.Descender * scale;
        float height;
        switch (baseline)
        {
            case TextBaseline.TextTop:
                height = ascender;
                break;
            case TextBaseline.Hanging:
            {
                // 80% of the ascender approximates the hanging baseline when the font
                // carries no baseline table data for it.
                height = metrics.TryGetBaselineCoordinate(HangingBaselineTag, false, out short coordinate)
                    ? coordinate * scale
                    : 0.8F * ascender;
                break;
            }

            case TextBaseline.Middle:
            {
                float xHeight = metrics.XHeight * scale;
                if (xHeight <= 0)
                {
                    // Half the ascender approximates the x-height when the font omits it.
                    xHeight = ascender * .5F;
                }

                height = xHeight * .5F;
                break;
            }

            case TextBaseline.Central:
                // The descender is negative, so this lands halfway between em top and bottom.
                height = (ascender + descender) * .5F;
                break;
            case TextBaseline.Ideographic:
            {
                // The em bottom approximates the ideographic-under baseline when the font
                // carries no baseline table data for it.
                height = metrics.TryGetBaselineCoordinate(IdeographicBaselineTag, false, out short coordinate)
                    ? coordinate * scale
                    : descender;
                break;
            }

            case TextBaseline.TextBottom:
                height = descender;
                break;
            default:
                height = 0;
                break;
        }

        if (!isVerticalLayout)
        {
            // Y increases toward the under side, so a reference above the baseline is a
            // negative offset.
            return -height;
        }

        // X increases toward the over side, so the offset is the reference height re-centered
        // on the central baseline.
        return height - ((ascender + descender) * .5F);
    }

    /// <summary>
    /// Converts <see cref="TextOptions.BaselineOffset"/> into a layout-unit offset signed for
    /// the layout axis. This method owns the shift's sign convention: consumers subtract the
    /// returned value from their flow-axis offset, which moves positive shifts toward the
    /// over side on both axes: up for horizontal layouts and +X for vertical layouts.
    /// </summary>
    /// <param name="options">The text options supplying the shift and dpi.</param>
    /// <param name="isVerticalLayout">Whether the layout flows vertically.</param>
    /// <returns>The baseline shift in layout units, to be subtracted by the consumer.</returns>
    public static float GetBaselineShift(TextOptions options, bool isVerticalLayout)
        => GetBaselineShift(options.BaselineOffset, options.Dpi, isVerticalLayout);

    /// <summary>
    /// Computes the total anchor offset for a single glyph: the reference line selected by
    /// <see cref="GlyphOptions.TextBaseline"/> composed with the
    /// <see cref="GlyphOptions.BaselineOffset"/> shift, in layout units. Consumers subtract
    /// the returned value from the origin on the axis the layout mode selects.
    /// </summary>
    /// <param name="options">The glyph options supplying the baseline, font, shift, and dpi.</param>
    /// <param name="isVerticalLayout">Whether the layout flows vertically.</param>
    /// <returns>The combined offset from the dominant baseline in layout units.</returns>
    public static float GetBaselineOffset(GlyphOptions options, bool isVerticalLayout)
        => GetBaselineOffset(options.TextBaseline, options.Font, isVerticalLayout)
            + GetBaselineShift(options.BaselineOffset, options.Dpi, isVerticalLayout);

    /// <summary>
    /// Converts a baseline shift in pixel units into a layout-unit offset signed for the
    /// layout axis, under the subtract-from-offset convention documented on
    /// <see cref="GetBaselineShift(TextOptions, bool)"/>.
    /// </summary>
    /// <param name="baselineOffset">The baseline shift in pixel units, positive toward the over side.</param>
    /// <param name="dpi">The dpi converting pixel units into layout units.</param>
    /// <param name="isVerticalLayout">Whether the layout flows vertically.</param>
    /// <returns>The baseline shift in layout units, to be subtracted by the consumer.</returns>
    private static float GetBaselineShift(float baselineOffset, float dpi, bool isVerticalLayout)
        => (isVerticalLayout ? -baselineOffset : baselineOffset) / dpi;

    /// <summary>
    /// Lays out the supplied <see cref="TextBox"/>, streaming each laid-out glyph through the
    /// supplied <paramref name="visitor"/> in layout order, culling whole lines whose extent along
    /// the block flow axis lies outside the supplied visible band.
    /// </summary>
    /// <remarks>
    /// A culled line advances the pen exactly as a rendered line would but visits no glyphs. The
    /// band is compared against each line's box inflated by one line height on each side, so ink
    /// or decorations overhanging a line box by up to one line height never disappear.
    /// </remarks>
    /// <typeparam name="TVisitor">The concrete visitor struct type.</typeparam>
    /// <param name="textBox">The shaped and line-broken text.</param>
    /// <param name="options">The text options used to lay out <paramref name="textBox"/>.</param>
    /// <param name="wrappingLength">The wrapping length in pixels. Use <c>-1</c> to disable wrapping.</param>
    /// <param name="visibleFlowMin">
    /// The lower edge of the visible band along the block flow axis (Y for horizontal layouts,
    /// X for vertical layouts) in layout units (pixels divided by DPI).
    /// Use <see cref="float.NegativeInfinity"/> to disable culling at this edge.
    /// </param>
    /// <param name="visibleFlowMax">
    /// The upper edge of the visible band along the block flow axis in layout units.
    /// Use <see cref="float.PositiveInfinity"/> to disable culling at this edge.
    /// </param>
    /// <param name="visitor">The visitor that receives each positioned glyph.</param>
    public static void LayoutText<TVisitor>(
        TextBox textBox,
        TextOptions options,
        float wrappingLength,
        float visibleFlowMin,
        float visibleFlowMax,
        ref TVisitor visitor)
        where TVisitor : struct, IGlyphLayoutVisitor
    {
        if (textBox.TextLines.Count == 0)
        {
            return;
        }

        LayoutMode layoutMode = options.LayoutMode;

        Vector2 boxLocation = options.Origin / options.Dpi;
        Vector2 penLocation = boxLocation;

        // When wrapping is enabled, the wrapping length defines the minimum line-box
        // extent used by alignment.
        float maxScaledAdvance = textBox.ScaledMaxAdvance();
        if (options.TextAlignment != TextAlignment.Start && wrappingLength > 0)
        {
            maxScaledAdvance = Math.Max(wrappingLength / options.Dpi, maxScaledAdvance);
        }

        TextDirection direction = textBox.TextDirection();

        if (layoutMode == LayoutMode.HorizontalTopBottom)
        {
            for (int i = 0; i < textBox.TextLines.Count; i++)
            {
                visitor.BeginLine(i);
                LayoutLineHorizontal(
                    textBox,
                    textBox.TextLines[i],
                    direction,
                    maxScaledAdvance,
                    options,
                    i,
                    visibleFlowMin,
                    visibleFlowMax,
                    ref boxLocation,
                    ref penLocation,
                    ref visitor);

                visitor.EndLine();
            }
        }
        else if (layoutMode == LayoutMode.HorizontalBottomTop)
        {
            int index = 0;
            for (int i = textBox.TextLines.Count - 1; i >= 0; i--)
            {
                visitor.BeginLine(i);
                LayoutLineHorizontal(
                    textBox,
                    textBox.TextLines[i],
                    direction,
                    maxScaledAdvance,
                    options,
                    index++,
                    visibleFlowMin,
                    visibleFlowMax,
                    ref boxLocation,
                    ref penLocation,
                    ref visitor);

                visitor.EndLine();
            }
        }
        else if (layoutMode is LayoutMode.VerticalLeftRight)
        {
            for (int i = 0; i < textBox.TextLines.Count; i++)
            {
                visitor.BeginLine(i);
                LayoutLineVertical(
                    textBox,
                    textBox.TextLines[i],
                    direction,
                    maxScaledAdvance,
                    options,
                    i,
                    visibleFlowMin,
                    visibleFlowMax,
                    ref boxLocation,
                    ref penLocation,
                    ref visitor);

                visitor.EndLine();
            }
        }
        else if (layoutMode is LayoutMode.VerticalRightLeft)
        {
            int index = 0;
            for (int i = textBox.TextLines.Count - 1; i >= 0; i--)
            {
                visitor.BeginLine(i);
                LayoutLineVertical(
                    textBox,
                    textBox.TextLines[i],
                    direction,
                    maxScaledAdvance,
                    options,
                    index++,
                    visibleFlowMin,
                    visibleFlowMax,
                    ref boxLocation,
                    ref penLocation,
                    ref visitor);

                visitor.EndLine();
            }
        }
        else if (layoutMode is LayoutMode.VerticalMixedLeftRight)
        {
            for (int i = 0; i < textBox.TextLines.Count; i++)
            {
                visitor.BeginLine(i);
                LayoutLineVerticalMixed(
                    textBox,
                    textBox.TextLines[i],
                    direction,
                    maxScaledAdvance,
                    options,
                    i,
                    visibleFlowMin,
                    visibleFlowMax,
                    ref boxLocation,
                    ref penLocation,
                    ref visitor);

                visitor.EndLine();
            }
        }
        else
        {
            int index = 0;
            for (int i = textBox.TextLines.Count - 1; i >= 0; i--)
            {
                visitor.BeginLine(i);
                LayoutLineVerticalMixed(
                    textBox,
                    textBox.TextLines[i],
                    direction,
                    maxScaledAdvance,
                    options,
                    index++,
                    visibleFlowMin,
                    visibleFlowMax,
                    ref boxLocation,
                    ref penLocation,
                    ref visitor);

                visitor.EndLine();
            }
        }
    }

    /// <summary>
    /// Positions one line of horizontal text. Applies vertical-block alignment (on the first line),
    /// horizontal-block alignment, per-line text alignment, and any first-line ink-overshoot
    /// compensation, then streams each positioned glyph through <paramref name="visitor"/>.
    /// </summary>
    /// <typeparam name="TVisitor">The concrete visitor struct type.</typeparam>
    /// <param name="textBox">The containing text box (used to look up sibling lines for block alignment).</param>
    /// <param name="textLine">The line being laid out.</param>
    /// <param name="direction">The resolved text direction for this line.</param>
    /// <param name="maxScaledAdvance">The widest scaled line advance in the block (or wrapping length).</param>
    /// <param name="options">The text options used to position the line.</param>
    /// <param name="index">The zero-based visual index of this line within the block.</param>
    /// <param name="visibleFlowMin">The lower visible-band edge along Y in layout units.</param>
    /// <param name="visibleFlowMax">The upper visible-band edge along Y in layout units.</param>
    /// <param name="boxLocation">The running top-left position of the glyph boxes; advanced by this method.</param>
    /// <param name="penLocation">The running pen position used for glyph placement; advanced by this method.</param>
    /// <param name="visitor">The visitor that receives each positioned glyph.</param>
    private static void LayoutLineHorizontal<TVisitor>(
        TextBox textBox,
        TextLine textLine,
        TextDirection direction,
        float maxScaledAdvance,
        TextOptions options,
        int index,
        float visibleFlowMin,
        float visibleFlowMax,
        ref Vector2 boxLocation,
        ref Vector2 penLocation,
        ref TVisitor visitor)
        where TVisitor : struct, IGlyphLayoutVisitor
    {
        // Offset the location to center the line vertically.
        bool isFirstLine = index == 0;
        float scaledLineHeight = textLine.ScaledMaxLineHeight;

        // Recover the unscaled line height to calculate proper centering
        float unscaledLineHeight = scaledLineHeight / options.LineSpacing;
        float advanceY = scaledLineHeight;

        // Center the glyphs within the extra space created by LineSpacing
        float offsetY = (advanceY - unscaledLineHeight) * .5F;
        float yLineAdvance = advanceY - offsetY;

        float originX = penLocation.X;
        float offsetX = 0;

        // Set the Y origin for the first horizontal line and account for tall stacks.
        if (isFirstLine)
        {
            if (options.TextBaseline != TextBaseline.LineBox)
            {
                // The walk renders this line's baseline at pen + ScaledMaxAscender, so moving
                // the pen to origin - ascender - reference places the selected reference line,
                // expressed as an offset from that baseline, exactly on the origin. Block
                // alignment and tall-stack compensation position the line box and therefore
                // do not apply to baseline-anchored text.
                offsetY = -textLine.ScaledMaxAscender - GetBaselineOffset(options.TextBaseline, options.Font, false);
            }
            else
            {
                // ScaledMinY is the minimum ink Y for this line in Y down (baseline at 0).
                // -ScaledMinY is the actual ascent required to contain the ink.
                // ScaledMaxAscender is the typographic ascent we already used to build the line box.
                float requiredAscent = -textLine.ScaledMinY;
                float extraAscent = requiredAscent - textLine.ScaledMaxAscender;

                if (extraAscent > 0)
                {
                    // Shift the baseline down only by the extra ascent needed so that
                    // stacked glyphs (Tibetan, etc) fit inside the bitmap. For Latin,
                    // requiredAscent ~= ScaledMaxAscender and extraAscent is zero.
                    offsetY += extraAscent;
                    advanceY += extraAscent;
                }

                switch (options.VerticalAlignment)
                {
                    case VerticalAlignment.Center:
                        for (int i = 0; i < textBox.TextLines.Count; i++)
                        {
                            offsetY -= textBox.TextLines[i].ScaledMaxLineHeight * .5F;
                        }

                        break;
                    case VerticalAlignment.Bottom:
                        for (int i = 0; i < textBox.TextLines.Count; i++)
                        {
                            offsetY -= textBox.TextLines[i].ScaledMaxLineHeight;
                        }

                        break;
                }
            }

            // The baseline shift composes with whichever anchor placed the first line.
            // Later lines stack from the pen, so the whole block carries the shift.
            offsetY -= GetBaselineShift(options, false);
        }

        penLocation.Y += offsetY;

        // Line-band culling: a line whose box, inflated by one line height on each side to cover
        // ink overshoot and decoration reach, lies fully outside the visible band advances the
        // pen and box exactly as a rendered line would without visiting a single glyph. The pen
        // X has not moved yet and a completed line always restores it, so only Y advances here.
        if (penLocation.Y + advanceY + scaledLineHeight < visibleFlowMin ||
            penLocation.Y - scaledLineHeight > visibleFlowMax)
        {
            penLocation.Y += yLineAdvance;
            boxLocation.Y += advanceY;
            return;
        }

        // Set the X-Origin for horizontal alignment.
        switch (options.HorizontalAlignment)
        {
            case HorizontalAlignment.Right:
                offsetX = -maxScaledAdvance;
                break;
            case HorizontalAlignment.Center:
                offsetX = -(maxScaledAdvance * .5F);
                break;
        }

        // Set the alignment of lines within the text.
        if (direction == TextDirection.LeftToRight)
        {
            switch (options.TextAlignment)
            {
                case TextAlignment.End:
                    offsetX += maxScaledAdvance - textLine.ScaledLineAdvance;
                    break;
                case TextAlignment.Center:
                    offsetX += (maxScaledAdvance * .5F) - (textLine.ScaledLineAdvance * .5F);
                    break;
            }
        }
        else
        {
            switch (options.TextAlignment)
            {
                case TextAlignment.Start:
                    offsetX += maxScaledAdvance - textLine.ScaledLineAdvance;
                    break;
                case TextAlignment.Center:
                    offsetX += (maxScaledAdvance * .5F) - (textLine.ScaledLineAdvance * .5F);
                    break;
            }
        }

        penLocation.X += offsetX;
        Vector2 boundsLocation = boxLocation;

        for (int i = 0; i < textLine.Count; i++)
        {
            GlyphLayoutData data = textLine[i];
            float layoutAdvance = data.ScaledAdvance;

            if (data.IsNewLine)
            {
                PositionedGlyphMetrics hardBreakPositioned = data.Metrics.Span[0];
                FontGlyphMetrics metric = hardBreakPositioned.Metrics;

                // Hard breaks bypass the normal glyph loop, but still need the
                // current pen position plus the same baseline origin used by glyphs.
                Vector2 hardBreakGlyphOrigin = penLocation + new Vector2(0, textLine.ScaledMaxAscender);

                visitor.Visit(
                    new GlyphLayout(
                    new Glyph(metric, data.PointSize, hardBreakPositioned.TextRun, hardBreakPositioned.Offset, new Vector2(hardBreakPositioned.AdvanceWidth, hardBreakPositioned.AdvanceHeight)),
                    data.Font,
                    boundsLocation,
                    hardBreakGlyphOrigin,
                    penLocation,
                    data.ScaledAdvance,
                    yLineAdvance,
                    GlyphLayoutMode.Horizontal,
                    data.BidiRun.Level,
                    true,
                    data.GraphemeIndex,
                    data.StringIndex));

                penLocation.X = originX;
                penLocation.Y += yLineAdvance;
                boxLocation.X = originX;
                boxLocation.Y += advanceY;
                boundsLocation.X = originX;
                boundsLocation.Y += advanceY;
                return;
            }

            // The entry's slice is the shaper's visual glyph stream; index it as a
            // span so the per-glyph hot path performs no interface dispatch.
            ReadOnlySpan<PositionedGlyphMetrics> metrics = data.Metrics.Span;
            float glyphAdvanceX = 0;
            for (int j = 0; j < metrics.Length; j++)
            {
                PositionedGlyphMetrics positioned = metrics[j];
                FontGlyphMetrics metric = positioned.Metrics;
                float positionedAdvanceX = positioned.AdvanceWidth * (data.PointSize / metric.ScaleFactor.X);

                // Browsers supply the current accumulated advance to each glyph
                // before adding that glyph's own advance. Preserve that positioned
                // walk when several glyphs share one layout entry.
                Vector2 advanceOrigin = boundsLocation + new Vector2(glyphAdvanceX, 0);
                Vector2 glyphOrigin = penLocation + new Vector2(glyphAdvanceX, textLine.ScaledMaxAscender);

                // Tracking and justification live on the layout entry rather than
                // in shaping. Assign their residual to the final positioned glyph
                // so per-glyph logical boxes still sum to the entry's exact advance.
                float glyphLayoutAdvance = j == metrics.Length - 1
                    ? data.ScaledAdvance - glyphAdvanceX
                    : positionedAdvanceX;

                visitor.Visit(
                    new GlyphLayout(
                    new Glyph(metric, data.PointSize, positioned.TextRun, positioned.Offset, new Vector2(positioned.AdvanceWidth, positioned.AdvanceHeight)),
                    data.Font,
                    advanceOrigin,
                    glyphOrigin,
                    glyphOrigin,
                    glyphLayoutAdvance,
                    advanceY,
                    GlyphLayoutMode.Horizontal,
                    data.BidiRun.Level,
                    i == 0 && j == 0,
                    data.GraphemeIndex,
                    data.StringIndex));

                glyphAdvanceX += positionedAdvanceX;
            }

            boxLocation.X += layoutAdvance;
            penLocation.X += layoutAdvance;
            boundsLocation.X += data.ScaledAdvance;
        }

        boxLocation.X = originX;
        penLocation.X = originX;
        penLocation.Y += yLineAdvance;
        boxLocation.Y += advanceY;
    }

    /// <summary>
    /// Positions one line of vertical text (<see cref="LayoutMode.VerticalLeftRight"/> and
    /// <see cref="LayoutMode.VerticalRightLeft"/>). All glyphs are treated as naturally vertical —
    /// every shaped glyph is positioned at its running vertical advance.
    /// </summary>
    /// <typeparam name="TVisitor">The concrete visitor struct type.</typeparam>
    /// <param name="textBox">The containing text box (used to look up sibling lines for block alignment).</param>
    /// <param name="textLine">The line being laid out.</param>
    /// <param name="direction">The resolved text direction for this line.</param>
    /// <param name="maxScaledAdvance">The longest scaled line advance in the block (or wrapping length).</param>
    /// <param name="options">The text options used to position the line.</param>
    /// <param name="index">The zero-based visual index of this line within the block.</param>
    /// <param name="visibleFlowMin">The lower visible-band edge along X in layout units.</param>
    /// <param name="visibleFlowMax">The upper visible-band edge along X in layout units.</param>
    /// <param name="boxLocation">The running top-left position of the glyph boxes; advanced by this method.</param>
    /// <param name="penLocation">The running pen position used for glyph placement; advanced by this method.</param>
    /// <param name="visitor">The visitor that receives each positioned glyph.</param>
    private static void LayoutLineVertical<TVisitor>(
        TextBox textBox,
        TextLine textLine,
        TextDirection direction,
        float maxScaledAdvance,
        TextOptions options,
        int index,
        float visibleFlowMin,
        float visibleFlowMax,
        ref Vector2 boxLocation,
        ref Vector2 penLocation,
        ref TVisitor visitor)
        where TVisitor : struct, IGlyphLayoutVisitor
    {
        float originY = penLocation.Y;
        float offsetY = 0;

        // Offset the location to center the line horizontally.
        float scaledMaxLineHeight = textLine.ScaledMaxLineHeight;

        // Recover the unscaled line height to calculate proper centering
        float unscaledLineHeight = scaledMaxLineHeight / options.LineSpacing;
        float advanceX = scaledMaxLineHeight;

        // Center the glyphs within the extra space created by LineSpacing
        float offsetX = (advanceX - unscaledLineHeight) * .5F;
        float xLineAdvance = advanceX - offsetX;

        // Set the Y-Origin for the line.
        switch (options.VerticalAlignment)
        {
            case VerticalAlignment.Top:
                offsetY = 0;
                break;
            case VerticalAlignment.Center:
                offsetY -= maxScaledAdvance * .5F;
                break;
            case VerticalAlignment.Bottom:
                offsetY -= maxScaledAdvance;
                break;
        }

        // Set the alignment of lines within the text.
        if (direction == TextDirection.LeftToRight)
        {
            switch (options.TextAlignment)
            {
                case TextAlignment.End:
                    offsetY += maxScaledAdvance - textLine.ScaledLineAdvance;
                    break;
                case TextAlignment.Center:
                    offsetY += (maxScaledAdvance * .5F) - (textLine.ScaledLineAdvance * .5F);
                    break;
            }
        }
        else
        {
            switch (options.TextAlignment)
            {
                case TextAlignment.Start:
                    offsetY += maxScaledAdvance - textLine.ScaledLineAdvance;
                    break;
                case TextAlignment.Center:
                    offsetY += (maxScaledAdvance * .5F) - (textLine.ScaledLineAdvance * .5F);
                    break;
            }
        }

        bool isFirstLine = index == 0;
        if (isFirstLine)
        {
            if (options.TextBaseline != TextBaseline.LineBox)
            {
                // The walk centers glyphs on the column's central axis at pen plus half the
                // unscaled line height; moving the pen so that axis sits at the origin minus
                // the reference offset anchors the selected line. Block alignment positions
                // the column box and therefore does not apply to baseline-anchored text.
                offsetX = -(unscaledLineHeight * .5F) - GetBaselineOffset(options.TextBaseline, options.Font, true);
            }
            else
            {
                // In vertical layout, first-line Y ascent compensation introduces unwanted
                // leading space before the first glyph. Keep first-line handling limited
                // to X-origin block alignment only.

                // Set the X-Origin for horizontal alignment.
                switch (options.HorizontalAlignment)
                {
                    case HorizontalAlignment.Right:
                        for (int i = 0; i < textBox.TextLines.Count; i++)
                        {
                            offsetX -= textBox.TextLines[i].ScaledMaxLineHeight;
                        }

                        break;
                    case HorizontalAlignment.Center:
                        for (int i = 0; i < textBox.TextLines.Count; i++)
                        {
                            offsetX -= textBox.TextLines[i].ScaledMaxLineHeight * .5F;
                        }

                        break;
                }
            }

            // The baseline shift composes with whichever anchor placed the first column.
            // Later columns stack from the pen, so the whole block carries the shift.
            offsetX -= GetBaselineShift(options, true);
        }

        penLocation.Y += offsetY;
        penLocation.X += offsetX;

        // Line-band culling: a column whose box, inflated by one column width on each side to
        // cover ink overshoot and decoration reach, lies fully outside the visible band advances
        // the pen and box exactly as a rendered column would without visiting a single glyph.
        // A completed column keeps its X offset and restores Y to the origin.
        if (penLocation.X + advanceX + scaledMaxLineHeight < visibleFlowMin ||
            penLocation.X - scaledMaxLineHeight > visibleFlowMax)
        {
            boxLocation.Y = originY;
            penLocation.Y = originY;
            boxLocation.X += advanceX;
            penLocation.X += xLineAdvance;
            return;
        }

        Vector2 boundsLocation = boxLocation;

        for (int i = 0; i < textLine.Count; i++)
        {
            GlyphLayoutData data = textLine[i];
            float layoutAdvance = data.ScaledAdvance;
            float scaledLineHeight = data.ScaledLineHeight / options.LineSpacing;

            if (data.IsNewLine)
            {
                PositionedGlyphMetrics hardBreakPositioned = data.Metrics.Span[0];
                FontGlyphMetrics metric = hardBreakPositioned.Metrics;
                Vector2 scale = new Vector2(data.PointSize) / metric.ScaleFactor;

                // Hard breaks bypass the normal glyph loop, but still need the
                // current pen position plus the same vertical glyph origin adjustment.
                Vector2 hardBreakDecorationOrigin = penLocation + new Vector2((unscaledLineHeight - scaledLineHeight) * .5F, 0);
                Vector2 hardBreakGlyphOrigin = hardBreakDecorationOrigin + new Vector2(0, (metric.Bounds.Max.Y + metric.TopSideBearing) * scale.Y);

                visitor.Visit(
                    new GlyphLayout(
                    new Glyph(metric, data.PointSize, hardBreakPositioned.TextRun, hardBreakPositioned.Offset, new Vector2(hardBreakPositioned.AdvanceWidth, hardBreakPositioned.AdvanceHeight)),
                    data.Font,
                    boundsLocation,
                    hardBreakGlyphOrigin,
                    hardBreakDecorationOrigin,
                    xLineAdvance,
                    data.ScaledAdvance,
                    GlyphLayoutMode.Vertical,
                    data.BidiRun.Level,
                    true,
                    data.GraphemeIndex,
                    data.StringIndex));

                boxLocation.X += advanceX;
                boxLocation.Y = originY;
                penLocation.X += xLineAdvance;
                penLocation.Y = originY;
                boundsLocation.X += advanceX;
                boundsLocation.Y = originY;
                return;
            }

            int j = 0;

            // The entry's slice is the shaper's visual glyph stream; index it as a
            // span so the per-glyph hot path performs no interface dispatch.
            ReadOnlySpan<PositionedGlyphMetrics> metrics = data.Metrics.Span;
            float glyphAdvanceY = 0;
            for (int metricIndex = 0; metricIndex < metrics.Length; metricIndex++)
            {
                PositionedGlyphMetrics positioned = metrics[metricIndex];
                FontGlyphMetrics metric = positioned.Metrics;

                // Browsers retain each shaped glyph and advance the vertical pen after
                // positioning it; source grouping only controls added letter spacing.
                Vector2 scale = new Vector2(data.PointSize) / metric.ScaleFactor;

                // Upright glyphs use a vertical origin centered on half their
                // nominal horizontal advance, even when shaping zeroed a mark's
                // positioned advance, so center that nominal width in the line box.
                float glyphAlignX = (scaledLineHeight - (metric.AdvanceWidth * scale.X)) * .5F;
                float verticalOriginY = (metric.Bounds.Max.Y + metric.TopSideBearing) * scale.Y;
                float positionedAdvanceY = positioned.AdvanceHeight * scale.Y;
                VerticalMetrics verticalMetrics = metric.FontMetrics.VerticalMetrics;
                if (verticalMetrics.Synthesized && positioned.AdvanceHeight != 0)
                {
                    // Browsers round the synthesized nominal height before shaping.
                    // Replace that component after shaping while retaining any
                    // positioning delta carried by this glyph.
                    float nominalAdvance = metric.AdvanceHeight * scale.Y;
                    float browserAdvance = (MathF.Floor((verticalMetrics.Ascender * scale.Y * options.Dpi) + .5F)
                        + MathF.Floor((-verticalMetrics.Descender * scale.Y * options.Dpi) + .5F)) / options.Dpi;
                    positionedAdvanceY += browserAdvance - nominalAdvance;
                }

                // Move the glyph origin without changing the advance or decoration origin.
                Vector2 glyphOffset = new(glyphAlignX, verticalOriginY);
                Vector2 advanceOrigin = boundsLocation + new Vector2(0, glyphAdvanceY);
                Vector2 decorationOrigin = penLocation + new Vector2((unscaledLineHeight - scaledLineHeight) * .5F, glyphAdvanceY);
                Vector2 glyphOrigin = decorationOrigin + glyphOffset;

                // The final positioned glyph owns tracking and justification so the
                // logical boxes cover the exact entry advance without changing the
                // HarfBuzz-derived origins of any preceding glyph.
                float glyphLayoutAdvance = metricIndex == metrics.Length - 1
                    ? data.ScaledAdvance - glyphAdvanceY
                    : positionedAdvanceY;

                visitor.Visit(
                    new GlyphLayout(
                    new Glyph(metric, data.PointSize, positioned.TextRun, positioned.Offset, new Vector2(positioned.AdvanceWidth, positioned.AdvanceHeight)),
                    data.Font,
                    advanceOrigin,
                    glyphOrigin,
                    decorationOrigin,
                    advanceX,
                    glyphLayoutAdvance,
                    GlyphLayoutMode.Vertical,
                    data.BidiRun.Level,
                    i == 0 && j == 0,
                    data.GraphemeIndex,
                    data.StringIndex));

                // Several glyphs may share one source position. Advance after each
                // visit so marks and decomposed forms retain their shaped
                // relative positions instead of being painted on one origin.
                glyphAdvanceY += positionedAdvanceY;
                j++;
            }

            penLocation.Y += layoutAdvance;
            boundsLocation.Y += data.ScaledAdvance;
        }

        boxLocation.Y = originY;
        penLocation.Y = originY;
        boxLocation.X += advanceX;
        penLocation.X += xLineAdvance;
    }

    /// <summary>
    /// Positions one line of vertical-mixed text (<see cref="LayoutMode.VerticalMixedLeftRight"/>
    /// and <see cref="LayoutMode.VerticalMixedRightLeft"/>). Transformed entries are rotated 90°
    /// and laid out sideways using the font's horizontal metrics while the pen still advances
    /// along Y; naturally-vertical entries are positioned using their vertical metrics.
    /// </summary>
    /// <typeparam name="TVisitor">The concrete visitor struct type.</typeparam>
    /// <param name="textBox">The containing text box (used to look up sibling lines for block alignment).</param>
    /// <param name="textLine">The line being laid out.</param>
    /// <param name="direction">The resolved text direction for this line.</param>
    /// <param name="maxScaledAdvance">The longest scaled line advance in the block (or wrapping length).</param>
    /// <param name="options">The text options used to position the line.</param>
    /// <param name="index">The zero-based visual index of this line within the block.</param>
    /// <param name="visibleFlowMin">The lower visible-band edge along X in layout units.</param>
    /// <param name="visibleFlowMax">The upper visible-band edge along X in layout units.</param>
    /// <param name="boxLocation">The running top-left position of the glyph boxes; advanced by this method.</param>
    /// <param name="penLocation">The running pen position used for glyph placement; advanced by this method.</param>
    /// <param name="visitor">The visitor that receives each positioned glyph.</param>
    private static void LayoutLineVerticalMixed<TVisitor>(
        TextBox textBox,
        TextLine textLine,
        TextDirection direction,
        float maxScaledAdvance,
        TextOptions options,
        int index,
        float visibleFlowMin,
        float visibleFlowMax,
        ref Vector2 boxLocation,
        ref Vector2 penLocation,
        ref TVisitor visitor)
        where TVisitor : struct, IGlyphLayoutVisitor
    {
        float originY = penLocation.Y;
        float offsetY = 0;

        // Offset the location to center the line horizontally.
        float scaledMaxLineHeight = textLine.ScaledMaxLineHeight;

        // Recover the unscaled line height to calculate proper centering
        float unscaledLineHeight = scaledMaxLineHeight / options.LineSpacing;
        float advanceX = scaledMaxLineHeight;

        // Center the glyphs within the extra space created by LineSpacing
        float offsetX = (advanceX - unscaledLineHeight) * .5F;
        float xLineAdvance = advanceX - offsetX;

        // Set the Y-Origin for the line.
        switch (options.VerticalAlignment)
        {
            case VerticalAlignment.Top:
                offsetY = 0;
                break;
            case VerticalAlignment.Center:
                offsetY -= maxScaledAdvance * .5F;
                break;
            case VerticalAlignment.Bottom:
                offsetY -= maxScaledAdvance;
                break;
        }

        // Set the alignment of lines within the text.
        if (direction == TextDirection.LeftToRight)
        {
            switch (options.TextAlignment)
            {
                case TextAlignment.End:
                    offsetY += maxScaledAdvance - textLine.ScaledLineAdvance;
                    break;
                case TextAlignment.Center:
                    offsetY += (maxScaledAdvance * .5F) - (textLine.ScaledLineAdvance * .5F);
                    break;
            }
        }
        else
        {
            switch (options.TextAlignment)
            {
                case TextAlignment.Start:
                    offsetY += maxScaledAdvance - textLine.ScaledLineAdvance;
                    break;
                case TextAlignment.Center:
                    offsetY += (maxScaledAdvance * .5F) - (textLine.ScaledLineAdvance * .5F);
                    break;
            }
        }

        bool isFirstLine = index == 0;
        if (isFirstLine)
        {
            if (options.TextBaseline != TextBaseline.LineBox)
            {
                // The walk centers glyphs on the column's central axis at pen plus half the
                // unscaled line height; moving the pen so that axis sits at the origin minus
                // the reference offset anchors the selected line. Block alignment positions
                // the column box and therefore does not apply to baseline-anchored text.
                offsetX = -(unscaledLineHeight * .5F) - GetBaselineOffset(options.TextBaseline, options.Font, true);
            }
            else
            {
                // In vertical-mixed layout, first-line Y ascent compensation introduces
                // unwanted leading space before the first glyph. Keep first-line handling
                // limited to X-origin block alignment only.

                // Set the X-Origin for horizontal alignment.
                switch (options.HorizontalAlignment)
                {
                    case HorizontalAlignment.Right:
                        for (int i = 0; i < textBox.TextLines.Count; i++)
                        {
                            offsetX -= textBox.TextLines[i].ScaledMaxLineHeight;
                        }

                        break;
                    case HorizontalAlignment.Center:
                        for (int i = 0; i < textBox.TextLines.Count; i++)
                        {
                            offsetX -= textBox.TextLines[i].ScaledMaxLineHeight * .5F;
                        }

                        break;
                }
            }

            // The baseline shift composes with whichever anchor placed the first column.
            // Later columns stack from the pen, so the whole block carries the shift.
            offsetX -= GetBaselineShift(options, true);
        }

        penLocation.Y += offsetY;
        penLocation.X += offsetX;

        // Line-band culling: a column whose box, inflated by one column width on each side to
        // cover ink overshoot and decoration reach, lies fully outside the visible band advances
        // the pen and box exactly as a rendered column would without visiting a single glyph.
        // A completed column keeps its X offset and restores Y to the origin.
        if (penLocation.X + advanceX + scaledMaxLineHeight < visibleFlowMin ||
            penLocation.X - scaledMaxLineHeight > visibleFlowMax)
        {
            boxLocation.Y = originY;
            penLocation.Y = originY;
            boxLocation.X += advanceX;
            penLocation.X += xLineAdvance;
            return;
        }

        Vector2 boundsLocation = boxLocation;

        for (int i = 0; i < textLine.Count; i++)
        {
            GlyphLayoutData data = textLine[i];
            float layoutAdvance = data.ScaledAdvance;
            float scaledLineHeight = data.ScaledLineHeight / options.LineSpacing;

            if (data.IsNewLine)
            {
                PositionedGlyphMetrics hardBreakPositioned = data.Metrics.Span[0];
                FontGlyphMetrics metric = hardBreakPositioned.Metrics;
                Vector2 scale = new Vector2(data.PointSize) / metric.ScaleFactor;

                // Hard breaks bypass the normal glyph loop, but still need the
                // current pen position plus the same vertical glyph origin adjustment.
                Vector2 hardBreakDecorationOrigin = penLocation + new Vector2((unscaledLineHeight - scaledLineHeight) * .5F, 0);
                Vector2 hardBreakGlyphOrigin = hardBreakDecorationOrigin + new Vector2(0, (metric.Bounds.Max.Y + metric.TopSideBearing) * scale.Y);

                visitor.Visit(
                    new GlyphLayout(
                    new Glyph(metric, data.PointSize, hardBreakPositioned.TextRun, hardBreakPositioned.Offset, new Vector2(hardBreakPositioned.AdvanceWidth, hardBreakPositioned.AdvanceHeight)),
                    data.Font,
                    boundsLocation,
                    hardBreakGlyphOrigin,
                    hardBreakDecorationOrigin,
                    xLineAdvance,
                    data.ScaledAdvance,
                    GlyphLayoutMode.Vertical,
                    data.BidiRun.Level,
                    true,
                    data.GraphemeIndex,
                    data.StringIndex));

                boxLocation.X += advanceX;
                boxLocation.Y = originY;
                penLocation.X += xLineAdvance;
                penLocation.Y = originY;
                boundsLocation.X += advanceX;
                boundsLocation.Y = originY;
                return;
            }

            if (data.IsTransformed)
            {
                // Browsers derive the text origin from the primary font of the styled run, then
                // paints every fallback glyph at that shared baseline. Using each fallback
                // font's ascender and descender here would shift scripts with different metrics
                // across the column even though they belong to the same styled run.
                FontMetrics baselineFontMetrics = data.Metrics.Span[0].TextRun.ResolvedFont.FontMetrics;
                HorizontalMetrics baselineMetrics = baselineFontMetrics.HorizontalMetrics;
                float baselineScale = data.PointSize / baselineFontMetrics.ScaleFactor;
                float centralOffset = (baselineMetrics.Ascender + baselineMetrics.Descender) * .5F * baselineScale;
                float baselineX = (unscaledLineHeight * .5F) - centralOffset;

                // The entry's slice is the shaper's visual glyph stream; index it as
                // a span so the per-glyph hot path performs no interface dispatch.
                ReadOnlySpan<PositionedGlyphMetrics> metrics = data.Metrics.Span;
                float glyphAdvanceY = 0;
                for (int j = 0; j < metrics.Length; j++)
                {
                    PositionedGlyphMetrics positioned = metrics[j];
                    FontGlyphMetrics metric = positioned.Metrics;
                    float positionedAdvanceY = positioned.AdvanceWidth * (data.PointSize / metric.ScaleFactor.X);

                    // The glyph will be rotated 90 degrees for vertical mixed layout.
                    // Its horizontal shaped advance therefore becomes a positive
                    // vertical device-space advance after the clockwise rotation.
                    Vector2 advanceOrigin = boundsLocation + new Vector2(0, glyphAdvanceY);
                    Vector2 glyphOrigin = penLocation + new Vector2(baselineX, glyphAdvanceY);

                    // Preserve the positioned-glyph walk and attach any layout-only
                    // spacing to its final glyph, exactly as in the horizontal path.
                    float glyphLayoutAdvance = j == metrics.Length - 1
                        ? data.ScaledAdvance - glyphAdvanceY
                        : positionedAdvanceY;

                    visitor.Visit(
                        new GlyphLayout(
                        new Glyph(metric, data.PointSize, positioned.TextRun, positioned.Offset, new Vector2(positioned.AdvanceWidth, positioned.AdvanceHeight)),
                        data.Font,
                        advanceOrigin,
                        glyphOrigin,
                        glyphOrigin,
                        advanceX,
                        glyphLayoutAdvance,
                        GlyphLayoutMode.VerticalRotated,
                        data.BidiRun.Level,
                        i == 0 && j == 0,
                        data.GraphemeIndex,
                        data.StringIndex));

                    glyphAdvanceY += positionedAdvanceY;
                }
            }
            else
            {
                // The entry's slice is the shaper's visual glyph stream; index it as
                // a span so the per-glyph hot path performs no interface dispatch.
                ReadOnlySpan<PositionedGlyphMetrics> metrics = data.Metrics.Span;
                float glyphAdvanceY = 0;
                for (int j = 0; j < metrics.Length; j++)
                {
                    PositionedGlyphMetrics positioned = metrics[j];
                    FontGlyphMetrics metric = positioned.Metrics;

                    // Each glyph from one source position retains its shaped origin and
                    // contributes its positioned advance to the following glyph.
                    Vector2 scale = new Vector2(data.PointSize) / metric.ScaleFactor;

                    // Vertical origin fallback places the vertical origin at half the
                    // nominal horizontal advance. Positioned mark advances can be zero,
                    // but that must not move their vertical origin across the column.
                    float glyphAlignX = (scaledLineHeight - (metric.AdvanceWidth * scale.X)) * .5F;
                    float verticalOriginY = (metric.Bounds.Max.Y + metric.TopSideBearing) * scale.Y;
                    float positionedAdvanceY = positioned.AdvanceHeight * scale.Y;
                    VerticalMetrics verticalMetrics = metric.FontMetrics.VerticalMetrics;
                    if (verticalMetrics.Synthesized && positioned.AdvanceHeight != 0)
                    {
                        // Preserve the shaper's positioning delta while replacing the
                        // nominal synthesized height with the device-rounded browser value.
                        float nominalAdvance = metric.AdvanceHeight * scale.Y;
                        float browserAdvance = (MathF.Floor((verticalMetrics.Ascender * scale.Y * options.Dpi) + .5F)
                            + MathF.Floor((-verticalMetrics.Descender * scale.Y * options.Dpi) + .5F)) / options.Dpi;
                        positionedAdvanceY += browserAdvance - nominalAdvance;
                    }

                    Vector2 glyphOffset = new(glyphAlignX, verticalOriginY);
                    Vector2 advanceOrigin = boundsLocation + new Vector2(0, glyphAdvanceY);
                    Vector2 decorationOrigin = penLocation + new Vector2((unscaledLineHeight - scaledLineHeight) * .5F, glyphAdvanceY);
                    Vector2 glyphOrigin = decorationOrigin + glyphOffset;

                    // Layout-only spacing belongs to the final positioned glyph,
                    // preserving exact per-glyph origins and the aggregate advance.
                    float glyphLayoutAdvance = j == metrics.Length - 1
                        ? data.ScaledAdvance - glyphAdvanceY
                        : positionedAdvanceY;

                    visitor.Visit(
                        new GlyphLayout(
                        new Glyph(metric, data.PointSize, positioned.TextRun, positioned.Offset, new Vector2(positioned.AdvanceWidth, positioned.AdvanceHeight)),
                        data.Font,
                        advanceOrigin,
                        glyphOrigin,
                        decorationOrigin,
                        advanceX,
                        glyphLayoutAdvance,
                        GlyphLayoutMode.Vertical,
                        data.BidiRun.Level,
                        i == 0 && j == 0,
                        data.GraphemeIndex,
                        data.StringIndex));

                    // Browser paint walks every positioned glyph in visual order;
                    // source membership controls spacing, not whether the pen advances.
                    glyphAdvanceY += positionedAdvanceY;
                }
            }

            penLocation.Y += layoutAdvance;
            boundsLocation.Y += data.ScaledAdvance;
        }

        boxLocation.Y = originY;
        penLocation.Y = originY;
        boxLocation.X += advanceX;
        penLocation.X += xLineAdvance;
    }

    /// <summary>
    /// Calculates the X offset to apply to a single line of horizontal text so that it is positioned
    /// within the wrapping block according to the requested horizontal and text alignment.
    /// </summary>
    /// <remarks>
    /// The returned offset is in unscaled (pre-Dpi) units and is combined with the pen location at
    /// layout time. The result depends on the text direction because <see cref="TextAlignment.Start"/>
    /// and <see cref="TextAlignment.End"/> flip under right-to-left text.
    /// </remarks>
    /// <param name="lineAdvance">The scaled advance of the current line.</param>
    /// <param name="maxScaledAdvance">The scaled advance of the widest line (or wrapping length, whichever is greater).</param>
    /// <param name="horizontalAlignment">Block-level horizontal alignment of the whole text.</param>
    /// <param name="textAlignment">Per-line alignment within the block.</param>
    /// <param name="direction">The resolved text direction for this line.</param>
    /// <returns>The X offset to add to the line's pen location.</returns>
    public static float CalculateLineOffsetX(
        float lineAdvance,
        float maxScaledAdvance,
        HorizontalAlignment horizontalAlignment,
        TextAlignment textAlignment,
        TextDirection direction)
    {
        float offsetX = 0;

        // Set the X-Origin for horizontal alignment.
        switch (horizontalAlignment)
        {
            case HorizontalAlignment.Right:
                offsetX = -maxScaledAdvance;
                break;
            case HorizontalAlignment.Center:
                offsetX = -(maxScaledAdvance * .5F);
                break;
        }

        // Set the alignment of lines within the text.
        if (direction == TextDirection.LeftToRight)
        {
            switch (textAlignment)
            {
                case TextAlignment.End:
                    offsetX += maxScaledAdvance - lineAdvance;
                    break;
                case TextAlignment.Center:
                    offsetX += (maxScaledAdvance * .5F) - (lineAdvance * .5F);
                    break;
            }
        }
        else
        {
            switch (textAlignment)
            {
                case TextAlignment.Start:
                    offsetX += maxScaledAdvance - lineAdvance;
                    break;
                case TextAlignment.Center:
                    offsetX += (maxScaledAdvance * .5F) - (lineAdvance * .5F);
                    break;
            }
        }

        return offsetX;
    }

    /// <summary>
    /// Calculates the Y offset to apply to a single line of vertical text so that it is positioned
    /// within the wrapping block according to the requested vertical and text alignment.
    /// </summary>
    /// <remarks>
    /// The returned offset is in unscaled (pre-Dpi) units and is combined with the pen location at
    /// layout time. The result depends on the text direction because <see cref="TextAlignment.Start"/>
    /// and <see cref="TextAlignment.End"/> flip under right-to-left text.
    /// </remarks>
    /// <param name="lineAdvance">The scaled advance of the current line.</param>
    /// <param name="maxScaledAdvance">The scaled advance of the longest line (or wrapping length, whichever is greater).</param>
    /// <param name="verticalAlignment">Block-level vertical alignment of the whole text.</param>
    /// <param name="textAlignment">Per-line alignment within the block.</param>
    /// <param name="direction">The resolved text direction for this line.</param>
    /// <returns>The Y offset to add to the line's pen location.</returns>
    public static float CalculateLineOffsetY(
        float lineAdvance,
        float maxScaledAdvance,
        VerticalAlignment verticalAlignment,
        TextAlignment textAlignment,
        TextDirection direction)
    {
        float offsetY = 0;

        // Set the Y-Origin for the line.
        switch (verticalAlignment)
        {
            case VerticalAlignment.Top:
                offsetY = 0;
                break;
            case VerticalAlignment.Center:
                offsetY -= maxScaledAdvance * .5F;
                break;
            case VerticalAlignment.Bottom:
                offsetY -= maxScaledAdvance;
                break;
        }

        // Set the alignment of lines within the text.
        if (direction == TextDirection.LeftToRight)
        {
            switch (textAlignment)
            {
                case TextAlignment.End:
                    offsetY += maxScaledAdvance - lineAdvance;
                    break;
                case TextAlignment.Center:
                    offsetY += (maxScaledAdvance * .5F) - (lineAdvance * .5F);
                    break;
            }
        }
        else
        {
            switch (textAlignment)
            {
                case TextAlignment.Start:
                    offsetY += maxScaledAdvance - lineAdvance;
                    break;
                case TextAlignment.Center:
                    offsetY += (maxScaledAdvance * .5F) - (lineAdvance * .5F);
                    break;
            }
        }

        return offsetY;
    }
}
