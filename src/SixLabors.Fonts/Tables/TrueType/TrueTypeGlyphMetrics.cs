// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Collections.Concurrent;
using System.Numerics;
using SixLabors.Fonts.Rendering;
using SixLabors.Fonts.Tables.TrueType.Glyphs;
using SixLabors.Fonts.Tables.TrueType.Hinting;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tables.TrueType;

/// <summary>
/// Represents a glyph metric from a particular TrueType font face.
/// </summary>
public partial class TrueTypeGlyphMetrics : FontGlyphMetrics
{
    private readonly GlyphVector vector;

    /// <summary>
    /// Scaled, hinted, upright outline copies keyed by ppem and resolved hinting mode.
    /// Offset translation, synthetic oblique, and layout rotation are applied per point at
    /// emit time so one cached copy serves every run, layout mode, and positioned offset.
    /// Allocated on first render: shaping and measurement clone metrics without ever
    /// rendering them, so an eager cache would cost a dictionary per glyph per shaping pass.
    /// </summary>
    private ConcurrentDictionary<ScaledVectorKey, GlyphVector>? scaledVectorCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrueTypeGlyphMetrics"/> class.
    /// </summary>
    /// <param name="font">The font metrics this glyph belongs to.</param>
    /// <param name="glyphId">The glyph identifier.</param>
    /// <param name="codePoint">The Unicode code point for this glyph.</param>
    /// <param name="vector">The glyph outline vector.</param>
    /// <param name="advanceWidth">The advance width in font units.</param>
    /// <param name="advanceHeight">The advance height in font units.</param>
    /// <param name="leftSideBearing">The left side bearing in font units.</param>
    /// <param name="topSideBearing">The top side bearing in font units.</param>
    /// <param name="unitsPerEM">The units per em for the font.</param>
    /// <param name="textAttributes">The text attributes.</param>
    /// <param name="textDecorations">The text decorations.</param>
    /// <param name="glyphType">The glyph type.</param>
    internal TrueTypeGlyphMetrics(
        StreamFontMetrics font,
        ushort glyphId,
        CodePoint codePoint,
        GlyphVector vector,
        ushort advanceWidth,
        ushort advanceHeight,
        short leftSideBearing,
        short topSideBearing,
        ushort unitsPerEM,
        TextAttributes textAttributes,
        TextDecorations textDecorations,
        GlyphType glyphType)
        : base(
              font,
              glyphId,
              codePoint,
              vector.Bounds,
              advanceWidth,
              advanceHeight,
              leftSideBearing,
              topSideBearing,
              unitsPerEM,
              textAttributes,
              textDecorations,
              glyphType)
        => this.vector = vector;

    /// <summary>
    /// Gets the outline for the current glyph.
    /// </summary>
    /// <returns>The <see cref="GlyphVector"/>.</returns>
    internal GlyphVector GetOutline() => this.vector;

    /// <summary>
    /// Returns the size to render/measure the glyph at. Under full hinting the em square is
    /// constrained to whole pixels, matching the classic rasterizers whose instruction
    /// exceptions and control values assume integral pixel sizes.
    /// </summary>
    /// <param name="pointSize">The font size in pt units.</param>
    /// <param name="dpi">The DPI (Dots Per Inch) to render/measure the glyph at</param>
    /// <param name="hintingMode">The hinting mode, which may constrain the size to whole pixels.</param>
    /// <returns>The <see cref="float"/>.</returns>
    internal override float GetScaledSize(float pointSize, float dpi, HintingMode hintingMode)
    {
        float scaledPPEM = base.GetScaledSize(pointSize, dpi, hintingMode);
        if (this.GetHintingMode(hintingMode) == HintingMode.Full)
        {
            scaledPPEM = MathF.Max(1F, MathF.Floor((scaledPPEM / 72F) + 0.5F)) * 72F;
        }

        return scaledPPEM;
    }

    /// <summary>
    /// Attempts to compute the whole pixel advance width for the glyph under full hinting.
    /// The value comes from the 'hdmx' device record when the font carries one, exactly as
    /// classic rasterizers resolve device advances, or from the design advance rounded to
    /// whole pixels otherwise: fonts without device records do not adjust advances in their
    /// instructions. Neither path executes hinting or touches the outline cache, so this is
    /// safe on the layout hot path.
    /// </summary>
    /// <param name="pointSize">The font size in pt units.</param>
    /// <param name="dpi">The DPI (Dots Per Inch) to render/measure the glyph at</param>
    /// <param name="hintingMode">The requested hinting mode.</param>
    /// <param name="advancePx">The advance width in whole device pixels.</param>
    /// <returns><see langword="true"/> if a hinted advance applies; otherwise, <see langword="false"/>.</returns>
    internal override bool TryGetHintedAdvanceWidth(float pointSize, float dpi, HintingMode hintingMode, out float advancePx)
    {
        advancePx = 0F;
        if (this.GetHintingMode(hintingMode) != HintingMode.Full)
        {
            return false;
        }

        // Sub and superscript metrics shrink the glyph by adjusting the scale factor while
        // the em square stays at the base size, so layout maps font units to pixels through
        // a different ratio than the base em. Device advances resolved against the base em
        // would be rescaled by that ratio on the way back into layout units, advancing the
        // pen by the wrong amount, so those runs keep their shaped fractional advances.
        if (this.ScaleFactor.X != this.UnitsPerEm * 72F)
        {
            return false;
        }

        float scaledPPEM = this.GetScaledSize(pointSize, dpi, hintingMode);
        int ppem = (int)(scaledPPEM / 72F);
        if (this.FontMetrics.TryGetDeviceAdvanceWidth(this.GlyphId, ppem, out byte deviceAdvance))
        {
            advancePx = deviceAdvance;
            return true;
        }

        advancePx = MathF.Floor((this.AdvanceWidth * scaledPPEM / (this.UnitsPerEm * 72F)) + 0.5F);
        return true;
    }

    /// <inheritdoc/>
    internal override void RenderOutlineTo(
        IGlyphRenderer renderer,
        Vector2 glyphOrigin,
        GlyphLayoutMode mode,
        TextRun? textRun,
        Vector2 positionOffset,
        Vector2 positionedAdvance,
        float scaledPPEM,
        HintingMode hintingMode)
    {
        ConcurrentDictionary<ScaledVectorKey, GlyphVector> cache =
            LazyInitializer.EnsureInitialized(ref this.scaledVectorCache, static () => new());
        Vector2 scale = new Vector2(scaledPPEM) / this.ScaleFactor;
        HintingMode resolvedMode = this.GetHintingMode(hintingMode);
        GlyphVector scaledVector = cache.GetOrAdd(new ScaledVectorKey(scaledPPEM, resolvedMode), static (key, self) => self.CreateScaledVector(key), this);

        IList<ControlPoint> controlPoints = scaledVector.ControlPoints;
        IReadOnlyList<ushort> endPoints = scaledVector.EndPoints;

        // Offset translation, synthetic oblique, and layout rotation are applied per point at
        // emit time so the cached outline stays shareable across runs, modes, and positioned
        // offsets. Placement lands after hinting, matching FreeType's treatment of GPOS
        // offsets, followed by the Y-flip into device space and the origin translation.
        Matrix3x2 outlineTransform = this.GetOutlineTransform(mode, textRun);
        Matrix3x2 emit = Matrix3x2.CreateTranslation((this.Offset + positionOffset) * scale);
        emit *= outlineTransform;
        emit *= Matrix3x2.CreateScale(1F, -1F);
        emit.Translation += glyphOrigin;

        // Full hinting aligns the outline to the pixel grid in glyph space; snapping the
        // composed translation to whole pixels preserves that alignment in device space.
        // Snapping only applies to upright, untransformed renders where the grid survives.
        if (resolvedMode == HintingMode.Full && scaledVector.IsHinted && outlineTransform.IsIdentity)
        {
            emit.Translation = new Vector2(MathF.Floor(emit.Translation.X + 0.5F), MathF.Floor(emit.Translation.Y + 0.5F));
        }

        float boldStrength = this.GetSyntheticBoldStrength(scaledPPEM, textRun);
        EmboldeningGlyphRenderer? emboldening = null;
        IGlyphRenderer target = renderer;
        if (boldStrength > 0F)
        {
            emboldening = EmboldeningGlyphRenderer.Rent(renderer, boldStrength);
            target = emboldening;
        }

        try
        {
            int endOfContour = -1;
            for (int i = 0; i < scaledVector.EndPoints.Count; i++)
            {
                target.BeginFigure();
                int startOfContour = endOfContour + 1;
                endOfContour = endPoints[i];

                Vector2 prev;
                Vector2 curr = Vector2.Transform(controlPoints[endOfContour].Point, emit);
                Vector2 next = Vector2.Transform(controlPoints[startOfContour].Point, emit);

                if (controlPoints[endOfContour].OnCurve)
                {
                    target.MoveTo(curr);
                }
                else
                {
                    if (controlPoints[startOfContour].OnCurve)
                    {
                        target.MoveTo(next);
                    }
                    else
                    {
                        // If both first and last points are off-curve, start at their middle.
                        Vector2 startPoint = (curr + next) * .5F;
                        target.MoveTo(startPoint);
                    }
                }

                int length = endOfContour - startOfContour + 1;
                for (int p = 0; p < length; p++)
                {
                    prev = curr;
                    curr = next;
                    int currentIndex = startOfContour + p;
                    int nextIndex = startOfContour + ((p + 1) % length);
                    int prevIndex = startOfContour + ((length + p - 1) % length);
                    next = Vector2.Transform(controlPoints[nextIndex].Point, emit);

                    if (controlPoints[currentIndex].OnCurve)
                    {
                        // This is a straight line.
                        target.LineTo(curr);
                    }
                    else
                    {
                        Vector2 prev2 = prev;
                        Vector2 next2 = next;

                        if (!controlPoints[prevIndex].OnCurve)
                        {
                            prev2 = (curr + prev) * .5F;
                            target.LineTo(prev2);
                        }

                        if (!controlPoints[nextIndex].OnCurve)
                        {
                            next2 = (curr + next) * .5F;
                        }

                        target.LineTo(prev2);
                        target.QuadraticBezierTo(curr, next2);
                    }
                }

                target.EndFigure();
            }

            // Emit the completed fill group before FontGlyphMetrics ends the glyph.
            emboldening?.CompleteOutline();
        }
        finally
        {
            emboldening?.Release();
        }
    }

    /// <summary>
    /// Builds the scaled, hinted outline copy cached per pixel size and hinting mode.
    /// A deep copy is scaled so that the globally cached design unit instance is never
    /// altered. The hinter always receives the upright, untranslated outline. Under full
    /// hinting the geometric grid fitter then processes any axis the font's own
    /// instructions left unfitted so lightly hinted fonts also gain crisp stems.
    /// </summary>
    /// <param name="key">The cache key carrying the pixel size and resolved hinting mode.</param>
    /// <returns>The scaled <see cref="GlyphVector"/>.</returns>
    private GlyphVector CreateScaledVector(ScaledVectorKey key)
    {
        Vector2 scale = new Vector2(key.ScaledPPEM) / this.ScaleFactor;
        GlyphVector clone = GlyphVector.DeepClone(this.vector);
        GlyphVector.TransformInPlace(ref clone, Matrix3x2.CreateScale(scale));

        float pixelSize = key.ScaledPPEM / 72F;
        TrueTypeHintingResult result = this.FontMetrics.ApplyTrueTypeHinting(key.HintingMode, this, ref clone, scale, pixelSize);

        if (key.HintingMode == HintingMode.Full && pixelSize <= GlyphGridFitter.MaxFitPixelsPerEm)
        {
            // Axes the instructions grid fitted keep their geometry and only receive the
            // thin stroke rescue, standing in for bi-level dropout control. Axes the
            // instructions left unfitted are fully fitted.
            GridFitAxisMode fitX = result == TrueTypeHintingResult.AppliedXY ? GridFitAxisMode.Rescue : GridFitAxisMode.Full;
            GridFitAxisMode fitY = result is TrueTypeHintingResult.None or TrueTypeHintingResult.Failed ? GridFitAxisMode.Full : GridFitAxisMode.Rescue;
            GridFitOptions options = new(pixelSize, fitX, fitY, this.FontMetrics.GridFitTopAnchors, [], [], 1F, scale.Y);
            if (GlyphGridFitter.FitInPlace(ref clone, in options))
            {
                clone.IsHinted = true;
            }
        }

        return clone;
    }

    /// <summary>
    /// Identifies a cached scaled outline by pixel size and the resolved hinting mode that
    /// shaped it. Both participate in identity so alternating modes at one size never
    /// return an outline fitted for another mode.
    /// </summary>
    private readonly struct ScaledVectorKey : IEquatable<ScaledVectorKey>
    {
        public ScaledVectorKey(float scaledPPEM, HintingMode hintingMode)
        {
            this.ScaledPPEM = scaledPPEM;
            this.HintingMode = hintingMode;
        }

        public float ScaledPPEM { get; }

        public HintingMode HintingMode { get; }

        public bool Equals(ScaledVectorKey other) => this.ScaledPPEM == other.ScaledPPEM && this.HintingMode == other.HintingMode;

        public override bool Equals(object? obj) => obj is ScaledVectorKey other && this.Equals(other);

        public override int GetHashCode() => HashCode.Combine(this.ScaledPPEM, this.HintingMode);
    }
}
