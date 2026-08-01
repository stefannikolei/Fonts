// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// A buffered CFF glyph outline in upright pixel space, Y up with the baseline at zero.
/// Charstrings are evaluated once per size into this form; rendering replays it through
/// the same per point transformation the streaming path applies, and grid fitting can
/// move the buffered points before replay. One instance is cached per pixel size and
/// hinting mode, mirroring the TrueType scaled outline cache.
/// </summary>
internal sealed class CffOutline
{
    private readonly CffOutlineVerb[] verbs;
    private readonly Vector2[] points;
    private readonly ushort[] contourEnds;
    private readonly float[] verticalStems;
    private readonly float[] horizontalStems;

    /// <summary>
    /// Initializes a new instance of the <see cref="CffOutline"/> class.
    /// </summary>
    /// <param name="verbs">The drawing commands in order.</param>
    /// <param name="points">The packed points: one per move or line, three per cubic.</param>
    /// <param name="contourEnds">The index of the last point of each contour.</param>
    /// <param name="verticalStems">The declared vertical stem zones as X edge pairs in pixel space.</param>
    /// <param name="horizontalStems">The declared horizontal stem zones as Y edge pairs in pixel space.</param>
    public CffOutline(CffOutlineVerb[] verbs, Vector2[] points, ushort[] contourEnds, float[] verticalStems, float[] horizontalStems)
    {
        this.verbs = verbs;
        this.points = points;
        this.contourEnds = contourEnds;
        this.verticalStems = verticalStems;
        this.horizontalStems = horizontalStems;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the outline has been grid fitted. Only
    /// fitted outlines qualify for whole pixel origin snapping at replay time.
    /// </summary>
    public bool IsFitted { get; set; }

    /// <summary>
    /// Gets the index of the last point of each contour.
    /// </summary>
    public ushort[] ContourEnds => this.contourEnds;

    /// <summary>
    /// Gets the packed outline points for in place fitting. Layout follows the verbs: one
    /// point per move or line, and two control points followed by the end point per cubic.
    /// </summary>
    public Vector2[] Points => this.points;

    /// <summary>
    /// Gets the drawing commands in order.
    /// </summary>
    public CffOutlineVerb[] Verbs => this.verbs;

    /// <summary>
    /// Gets the declared vertical stem zones as low and high X edge pairs in pixel space.
    /// Ghost stems retain their inverted edges so consumers can recognize edge hints.
    /// </summary>
    public float[] VerticalStems => this.verticalStems;

    /// <summary>
    /// Gets the declared horizontal stem zones as low and high Y edge pairs in pixel space.
    /// Ghost stems retain their inverted edges so consumers can recognize edge hints.
    /// </summary>
    public float[] HorizontalStems => this.horizontalStems;

    /// <summary>
    /// Replays the outline into the given transforming renderer, reproducing the exact
    /// call sequence the streaming evaluation path produces, including the implicit
    /// figure handling inside the transforming renderer.
    /// </summary>
    /// <param name="target">The transforming renderer that applies placement and receives the outline.</param>
    public void ReplayTo(ref TransformingGlyphRenderer target)
    {
        CffOutlineVerb[] outlineVerbs = this.verbs;
        Vector2[] outlinePoints = this.points;
        int pointIndex = 0;
        for (int i = 0; i < outlineVerbs.Length; i++)
        {
            switch (outlineVerbs[i])
            {
                case CffOutlineVerb.Move:
                    target.MoveTo(outlinePoints[pointIndex++]);
                    break;

                case CffOutlineVerb.Line:
                    target.LineTo(outlinePoints[pointIndex++]);
                    break;

                default:
                    target.CubicBezierTo(outlinePoints[pointIndex], outlinePoints[pointIndex + 1], outlinePoints[pointIndex + 2]);
                    pointIndex += 3;
                    break;
            }
        }

        if (target.IsOpen)
        {
            target.EndFigure();
        }
    }
}
