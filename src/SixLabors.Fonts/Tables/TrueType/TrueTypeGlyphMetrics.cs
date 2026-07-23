// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Collections.Concurrent;
using System.Numerics;
using SixLabors.Fonts.Rendering;
using SixLabors.Fonts.Tables.TrueType.Glyphs;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tables.TrueType;

/// <summary>
/// Represents a glyph metric from a particular TrueType font face.
/// </summary>
public partial class TrueTypeGlyphMetrics : FontGlyphMetrics
{
    private readonly GlyphVector vector;

    /// <summary>
    /// Scaled, hinted, upright outline copies keyed by ppem. Offset translation, synthetic
    /// oblique, and layout rotation are applied per point at emit time so one cached copy
    /// serves every run, layout mode, and positioned offset. Allocated on first render:
    /// shaping and measurement clone metrics without ever rendering them, so an eager cache
    /// would cost a dictionary per glyph per shaping pass.
    /// </summary>
    private ConcurrentDictionary<float, GlyphVector>? scaledVectorCache;

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

    /// <inheritdoc/>
    internal override void RenderOutlineTo(
        IGlyphRenderer renderer,
        Vector2 glyphOrigin,
        GlyphLayoutMode mode,
        TextRun? textRun,
        Vector2 positionOffset,
        float scaledPPEM,
        HintingMode hintingMode)
    {
        ConcurrentDictionary<float, GlyphVector> cache =
            LazyInitializer.EnsureInitialized(ref this.scaledVectorCache, static () => new());
        Vector2 scale = new Vector2(scaledPPEM) / this.ScaleFactor;
        GlyphVector scaledVector = cache.GetOrAdd(scaledPPEM, _ =>
        {
            // Create a scaled deep copy of the vector so that we do not alter
            // the globally cached instance. The hinter always receives the upright,
            // untranslated outline.
            GlyphVector clone = GlyphVector.DeepClone(this.vector);
            GlyphVector.TransformInPlace(ref clone, Matrix3x2.CreateScale(scale));

            float pixelSize = scaledPPEM / 72F;
            this.FontMetrics.ApplyTrueTypeHinting(this.GetHintingMode(hintingMode), this, ref clone, scale, pixelSize);
            return clone;
        });

        IList<ControlPoint> controlPoints = scaledVector.ControlPoints;
        IReadOnlyList<ushort> endPoints = scaledVector.EndPoints;

        // Offset translation, synthetic oblique, and layout rotation are applied per point at
        // emit time so the cached outline stays shareable across runs, modes, and positioned
        // offsets. Placement lands after hinting, matching FreeType's treatment of GPOS
        // offsets, followed by the Y-flip into device space and the origin translation.
        Matrix3x2 emit = Matrix3x2.CreateTranslation((this.Offset + positionOffset) * scale);
        emit *= this.GetOutlineTransform(mode, textRun);
        emit *= Matrix3x2.CreateScale(1F, -1F);
        emit.Translation += glyphOrigin;

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
}
