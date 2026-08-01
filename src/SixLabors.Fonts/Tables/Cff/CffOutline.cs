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

    /// <summary>
    /// Initializes a new instance of the <see cref="CffOutline"/> class.
    /// </summary>
    /// <param name="verbs">The drawing commands in order.</param>
    /// <param name="points">The packed points: one per move or line, three per cubic.</param>
    public CffOutline(CffOutlineVerb[] verbs, Vector2[] points)
    {
        this.verbs = verbs;
        this.points = points;
    }

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
