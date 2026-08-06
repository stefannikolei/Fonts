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
    private readonly int initialStemCount;
    private readonly bool lockFixMapOk;
    private readonly CffHintRegion[] hintRegions;
    private readonly CffCounterMask[] counterMasks;

    /// <summary>
    /// Initializes a new instance of the <see cref="CffOutline"/> class.
    /// </summary>
    /// <param name="verbs">The drawing commands in order.</param>
    /// <param name="points">The packed points: one per move or line, three per cubic.</param>
    /// <param name="contourEnds">The index of the last point of each contour.</param>
    /// <param name="verticalStems">The declared vertical stem zones as X edge pairs in pixel space.</param>
    /// <param name="horizontalStems">The declared horizontal stem zones as Y edge pairs in pixel space.</param>
    /// <param name="initialStemCount">The number of stems active at the first movement operator.</param>
    /// <param name="lockFixMapOk">Whether GDI permits its post-lock overlap fixup for this charstring.</param>
    /// <param name="hintRegions">The hint mask regions, each naming the stems live for the run of points that starts at its index.</param>
    /// <param name="counterMasks">The cntrmask events in declaration order, empty when the glyph declares none.</param>
    public CffOutline(CffOutlineVerb[] verbs, Vector2[] points, ushort[] contourEnds, float[] verticalStems, float[] horizontalStems, int initialStemCount, bool lockFixMapOk, CffHintRegion[] hintRegions, CffCounterMask[] counterMasks)
    {
        this.verbs = verbs;
        this.points = points;
        this.contourEnds = contourEnds;
        this.verticalStems = verticalStems;
        this.horizontalStems = horizontalStems;
        this.initialStemCount = initialStemCount;
        this.lockFixMapOk = lockFixMapOk;
        this.hintRegions = hintRegions;
        this.counterMasks = counterMasks;
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
    /// Gets the number of stems that become active together at the first movement operator.
    /// </summary>
    public int InitialStemCount => this.initialStemCount;

    /// <summary>
    /// Gets a value indicating whether GDI permits its post-lock overlap fixup for this charstring.
    /// </summary>
    public bool LockFixMapOk => this.lockFixMapOk;

    /// <summary>
    /// Gets the hint mask regions, each naming the stems the charstring declared live for
    /// the run of points that starts at its index. Empty when the glyph declares no mask.
    /// </summary>
    public CffHintRegion[] HintRegions => this.hintRegions;

    /// <summary>
    /// Gets the cntrmask events in declaration order. Each event retains the stem declaration
    /// count at its operator and is not associated with an outline point range.
    /// </summary>
    public CffCounterMask[] CounterMasks => this.counterMasks;

    /// <summary>
    /// Gets the retained QueryCurveTo subdivision thresholds. Entry <c>n - 1</c>
    /// is the maximum rounded third-difference magnitude represented by <c>n</c>
    /// quadratic segments; the final entry is the native unsigned-short sentinel.
    /// </summary>
    private static ReadOnlySpan<ushort> Cube10 =>
    [
        1,
        80,
        270,
        640,
        1250,
        2160,
        3430,
        5120,
        7290,
        10000,
        13310,
        17280,
        21970,
        27440,
        ushort.MaxValue,
    ];

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
        Vector2 current = default;
        for (int i = 0; i < outlineVerbs.Length; i++)
        {
            switch (outlineVerbs[i])
            {
                case CffOutlineVerb.Move:
                    current = outlinePoints[pointIndex++];
                    target.MoveTo(current);
                    break;

                case CffOutlineVerb.Line:
                    current = outlinePoints[pointIndex++];
                    target.LineTo(current);
                    break;

                default:
                    Vector2 control1 = outlinePoints[pointIndex];
                    Vector2 control2 = outlinePoints[pointIndex + 1];
                    Vector2 end = outlinePoints[pointIndex + 2];
                    pointIndex += 3;

                    if (this.IsFitted)
                    {
                        EmitAsQuadratics(ref target, current, control1, control2, end);
                    }
                    else
                    {
                        target.CubicBezierTo(control1, control2, end);
                    }

                    current = end;
                    break;
            }
        }

        if (target.IsOpen)
        {
            target.EndFigure();
        }
    }

    /// <summary>
    /// Replays one cubic as the quadratic spline produced by the native QueryCurveTo path.
    /// The native path chooses between one and fifteen segments from the cubic's third
    /// difference, then evaluates every control point with signed 16.16 coordinates and
    /// signed 2.30 fractional arithmetic.
    /// </summary>
    /// <param name="target">The renderer receiving the quadratics.</param>
    /// <param name="start">The point the cubic starts at.</param>
    /// <param name="control1">The first cubic control point.</param>
    /// <param name="control2">The second cubic control point.</param>
    /// <param name="end">The point the cubic ends at.</param>
    private static void EmitAsQuadratics(ref TransformingGlyphRenderer target, Vector2 start, Vector2 control1, Vector2 control2, Vector2 end)
    {
        int startX = CffFixedPoint.FromSingle(start.X);
        int startY = CffFixedPoint.FromSingle(start.Y);
        int control1X = CffFixedPoint.FromSingle(control1.X);
        int control1Y = CffFixedPoint.FromSingle(control1.Y);
        int control2X = CffFixedPoint.FromSingle(control2.X);
        int control2Y = CffFixedPoint.FromSingle(control2.Y);
        int endX = CffFixedPoint.FromSingle(end.X);
        int endY = CffFixedPoint.FromSingle(end.Y);

        // QueryCurveTo writes the cubic in Horner form. These are respectively its cubic,
        // unscaled quadratic, and linear coefficients. Every operation wraps at 32 bits
        // before the saturating 2.30 multiply, matching the retained integer instructions.
        int cubicX = unchecked(control1X - control2X);
        cubicX = unchecked(cubicX * 3);
        cubicX = unchecked(cubicX - startX);
        cubicX = unchecked(cubicX + endX);

        int cubicY = unchecked(control1Y - control2Y);
        cubicY = unchecked(cubicY * 3);
        cubicY = unchecked(cubicY + endY);
        cubicY = unchecked(cubicY - startY);

        int quadraticX = unchecked(startX - unchecked(control1X * 2));
        quadraticX = unchecked(quadraticX + control2X);
        int quadraticY = unchecked(startY - unchecked(control1Y * 2));
        quadraticY = unchecked(quadraticY + control2Y);
        int linearX = unchecked(control1X - startX);
        linearX = unchecked(linearX * 3);
        int linearY = unchecked(control1Y - startY);
        linearY = unchecked(linearY * 3);

        // The native absolute-value sequence deliberately leaves Int32.MinValue unchanged,
        // compares the two components as signed integers, rounds the selected 16.16 value
        // upward, then truncates it to the unsigned-short domain used by cube10.
        int magnitudeX = unchecked(-cubicX);
        if (magnitudeX < 0)
        {
            magnitudeX = cubicX;
        }

        int magnitudeY = unchecked(-cubicY);
        if (magnitudeY < 0)
        {
            magnitudeY = cubicY;
        }

        int magnitude = magnitudeX < magnitudeY ? magnitudeY : magnitudeX;
        ushort roundedMagnitude = unchecked((ushort)(unchecked(magnitude + 0xFFFF) >> 16));
        ReadOnlySpan<ushort> cube10 = Cube10;
        int segmentCount = 1;
        while (cube10[segmentCount - 1] < roundedMagnitude)
        {
            segmentCount++;
        }

        // fixratio converts 1 / segmentCount to signed 2.30. QueryCurveTo accumulates that
        // rounded step instead of recomputing i / n, so the last sample can differ slightly
        // from one and the explicit cubic end point must remain the spline's final point.
        int step = FixedRatio(CffFixedPoint.One, segmentCount * CffFixedPoint.One);
        int parameter = step;
        int previousPointHalfX = startX >> 1;
        int previousPointHalfY = startY >> 1;
        int previousDerivativeQuarterX = FractionMultiply(linearX, step) >> 2;
        int previousDerivativeQuarterY = FractionMultiply(linearY, step) >> 2;
        int previousControlX = 0;
        int previousControlY = 0;
        bool hasPreviousControl = false;

        for (int i = 0; i < segmentCount; i++)
        {
            // Evaluate half of C(t) using the retained Horner sequence. Keeping each
            // fracmul separate is required because every stage rounds and saturates.
            int pointX = FractionMultiply(cubicX, parameter);
            pointX = FractionMultiply(unchecked(pointX + unchecked(quadraticX * 3)), parameter);
            pointX = FractionMultiply(unchecked(pointX + linearX), parameter);
            int pointHalfX = unchecked(pointX + startX) >> 1;

            int pointY = FractionMultiply(cubicY, parameter);
            pointY = FractionMultiply(unchecked(pointY + unchecked(quadraticY * 3)), parameter);
            pointY = FractionMultiply(unchecked(pointY + linearY), parameter);
            int pointHalfY = unchecked(pointY + startY) >> 1;

            // The quadratic control is the sum of the two neighbouring half-points plus
            // their signed quarter tangent steps. The current tangent is subtracted and
            // the preceding tangent is added, which is the native spline construction.
            int derivativeX = FractionMultiply(unchecked(cubicX * 3), parameter);
            derivativeX = FractionMultiply(unchecked(derivativeX + unchecked(quadraticX * 6)), parameter);
            derivativeX = FractionMultiply(unchecked(derivativeX + linearX), step);
            int derivativeQuarterX = derivativeX >> 2;

            int derivativeY = FractionMultiply(unchecked(cubicY * 3), parameter);
            derivativeY = FractionMultiply(unchecked(derivativeY + unchecked(quadraticY * 6)), parameter);
            derivativeY = FractionMultiply(unchecked(derivativeY + linearY), step);
            int derivativeQuarterY = derivativeY >> 2;

            int splineControlX = unchecked(pointHalfX - derivativeQuarterX);
            splineControlX = unchecked(splineControlX + previousDerivativeQuarterX);
            splineControlX = unchecked(splineControlX + previousPointHalfX);
            int splineControlY = unchecked(pointHalfY - derivativeQuarterY);
            splineControlY = unchecked(splineControlY + previousDerivativeQuarterY);
            splineControlY = unchecked(splineControlY + previousPointHalfY);

            if (hasPreviousControl)
            {
                // A native quadratic-spline record implies each intermediate on-curve point
                // at the exact midpoint of adjacent fixed controls. The long sum preserves
                // the half-LSB that an arithmetic shift would discard.
                const double FixedMidpointScale = 1D / (CffFixedPoint.One * 2D);
                Vector2 previousControl = new(CffFixedPoint.ToSingle(previousControlX), CffFixedPoint.ToSingle(previousControlY));
                Vector2 intermediate = new(
                    (float)(((long)previousControlX + splineControlX) * FixedMidpointScale),
                    (float)(((long)previousControlY + splineControlY) * FixedMidpointScale));

                target.QuadraticBezierTo(previousControl, intermediate);
            }

            previousControlX = splineControlX;
            previousControlY = splineControlY;
            previousPointHalfX = pointHalfX;
            previousPointHalfY = pointHalfY;
            previousDerivativeQuarterX = derivativeQuarterX;
            previousDerivativeQuarterY = derivativeQuarterY;
            parameter = unchecked(parameter + step);
            hasPreviousControl = true;
        }

        Vector2 finalControl = new(CffFixedPoint.ToSingle(previousControlX), CffFixedPoint.ToSingle(previousControlY));
        Vector2 fixedEnd = new(CffFixedPoint.ToSingle(endX), CffFixedPoint.ToSingle(endY));
        target.QuadraticBezierTo(finalControl, fixedEnd);
    }

    /// <summary>
    /// Multiplies two signed 2.30-compatible values with the native fracmul rounding and
    /// saturation behavior. A 16.16 coordinate may be supplied as either operand because
    /// the binary multiply only assigns the combined thirty fractional bits.
    /// </summary>
    /// <param name="left">The first signed integer operand.</param>
    /// <param name="right">The second signed integer operand.</param>
    /// <returns>The product scaled down by thirty fractional bits.</returns>
    private static int FractionMultiply(int left, int right)
    {
        double value = (double)left * right * 9.313225746154785E-10D;
        return RoundAndSaturateFraction(value);
    }

    /// <summary>
    /// Divides two signed values and returns their ratio in signed 2.30 form, reproducing
    /// the native fixratio zero-denominator and saturation behavior.
    /// </summary>
    /// <param name="numerator">The ratio numerator.</param>
    /// <param name="denominator">The ratio denominator.</param>
    /// <returns>The signed 2.30 ratio.</returns>
    private static int FixedRatio(int numerator, int denominator)
    {
        if (denominator == 0)
        {
            return numerator < 0 ? int.MinValue : int.MaxValue;
        }

        double value = ((double)numerator / denominator) * 1073741824D;
        return RoundAndSaturateFraction(value);
    }

    /// <summary>
    /// Rounds a native fractional-arithmetic result halfway away from zero and clamps it
    /// to the signed 32-bit storage range.
    /// </summary>
    /// <param name="value">The scaled floating-point intermediate.</param>
    /// <returns>The rounded and saturated integer.</returns>
    private static int RoundAndSaturateFraction(double value)
    {
        double rounded = value < 0D ? value - 0.5D : value + 0.5D;
        if (rounded >= int.MaxValue)
        {
            return int.MaxValue;
        }

        if (rounded <= int.MinValue)
        {
            return int.MinValue;
        }

        return (int)rounded;
    }
}
