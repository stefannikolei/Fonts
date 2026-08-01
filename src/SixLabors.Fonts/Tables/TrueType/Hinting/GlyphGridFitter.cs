// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using SixLabors.Fonts.Tables.TrueType.Glyphs;

namespace SixLabors.Fonts.Tables.TrueType.Hinting;

/// <summary>
/// Fits a scaled glyph outline to the pixel grid by detecting coherent stem edges and
/// snapping them to whole pixel boundaries, then interpolating the remaining points so
/// curve continuity is preserved. The fitter complements instruction based hinting: it
/// processes only the axes the font's own instructions left unfitted, moving control
/// points in place without changing point or contour counts.
/// </summary>
/// <remarks>
/// The algorithm detects runs of points that are nearly constant on the fitted axis,
/// groups them into logical edges, pairs opposing edges into stems, normalizes stem
/// widths to whole pixels and repositions them with counters kept open, then applies an
/// interpolation pass with the same semantics as the IUP instruction for all untouched
/// points. Guards cap per edge movement and revert any pairing that would collapse a
/// counter, so a glyph the fitter cannot handle safely renders unchanged.
/// </remarks>
internal sealed class GlyphGridFitter
{
    /// <summary>
    /// The largest pixels per em at which fitting applies. Above this size stems span
    /// multiple pixels and area coverage antialiasing is already crisp, so moving points
    /// would trade shape fidelity for nothing.
    /// </summary>
    public const float MaxFitPixelsPerEm = 24F;

    // Capacity bails, not tuning: a glyph exceeding either budget is left unfitted.
    private const int MaxSegments = 256;
    private const int MaxEdges = 48;

    private static readonly ObjectPool<GlyphGridFitter> Pool = new(new PooledObjectPolicy());

    private readonly Segment[] segments = new Segment[MaxSegments];
    private readonly Edge[] edges = new Edge[MaxEdges];
    private readonly int[] sortOrder = new int[MaxSegments];
    private float[] axisOriginal = [];
    private float[] axisCurrent = [];
    private float[] perp = [];
    private bool[] touched = [];
    private bool[] consumed = [];
    private int segmentCount;
    private int edgeCount;
    private float minSegmentExtent;

    private GlyphGridFitter()
    {
    }

    /// <summary>
    /// Gets or sets the diagnostic sink populated during fitting when assigned. Test only;
    /// never set on production paths.
    /// </summary>
    public static System.Text.StringBuilder? DebugLog { get; set; }

    /// <summary>
    /// Fits the outline of the given glyph vector to the pixel grid on the requested axes.
    /// The vector must already be scaled to device pixels, Y up, baseline at zero.
    /// </summary>
    /// <param name="vector">The glyph vector to fit in place.</param>
    /// <param name="options">The fitting parameters.</param>
    /// <returns><see langword="true"/> if any point was moved; otherwise, <see langword="false"/>.</returns>
    public static bool FitInPlace(ref GlyphVector vector, in GridFitOptions options)
    {
        if (options.FitX == GridFitAxisMode.None && options.FitY == GridFitAxisMode.None)
        {
            return false;
        }

        if (vector.ControlPoints.Count < 4 || vector.EndPoints.Count == 0)
        {
            return false;
        }

        GlyphGridFitter fitter = Pool.Get();
        try
        {
            return fitter.Fit(ref vector, in options);
        }
        finally
        {
            Pool.Return(fitter);
        }
    }

    /// <summary>
    /// Fits a buffered outline whose stems are declared by the font rather than detected.
    /// The declared zones seed the edge list directly, so the geometric detection and
    /// pairing heuristics never run; anchor snapping, stem snapping, movement application
    /// and interpolation proceed exactly as for detected edges. The points must be in
    /// upright pixel space, Y up, baseline at zero.
    /// </summary>
    /// <param name="points">The outline points to fit in place, in contour order.</param>
    /// <param name="contourEnds">The index of the last point of each contour.</param>
    /// <param name="verticalStems">The declared vertical stem zones as X edge pairs.</param>
    /// <param name="horizontalStems">The declared horizontal stem zones as Y edge pairs.</param>
    /// <param name="options">The fitting parameters.</param>
    /// <returns><see langword="true"/> if any point was moved; otherwise, <see langword="false"/>.</returns>
    public static bool FitInPlace(Vector2[] points, ushort[] contourEnds, float[] verticalStems, float[] horizontalStems, in GridFitOptions options)
    {
        if (options.FitX == GridFitAxisMode.None && options.FitY == GridFitAxisMode.None)
        {
            return false;
        }

        if (points.Length < 4 || contourEnds.Length == 0)
        {
            return false;
        }

        GlyphGridFitter fitter = Pool.Get();
        try
        {
            fitter.EnsureCapacity(points.Length);

            bool moved = false;
            if (options.FitX != GridFitAxisMode.None && fitter.FitBufferedAxis(points, contourEnds, verticalStems, in options, true))
            {
                moved = true;
            }

            if (options.FitY != GridFitAxisMode.None && fitter.FitBufferedAxis(points, contourEnds, horizontalStems, in options, false))
            {
                moved = true;
            }

            return moved;
        }
        finally
        {
            Pool.Return(fitter);
        }
    }

    /// <summary>
    /// Runs the requested axis passes over the outline using this instance's scratch state.
    /// </summary>
    /// <param name="vector">The glyph vector to fit in place.</param>
    /// <param name="options">The fitting parameters.</param>
    /// <returns><see langword="true"/> if any point was moved; otherwise, <see langword="false"/>.</returns>
    private bool Fit(ref GlyphVector vector, in GridFitOptions options)
    {
        IList<ControlPoint> controlPoints = vector.ControlPoints;
        int pointCount = controlPoints.Count;
        this.EnsureCapacity(pointCount);

        bool moved = false;
        if (options.FitX != GridFitAxisMode.None && this.FitAxis(vector, in options, true))
        {
            moved = true;
        }

        if (options.FitY != GridFitAxisMode.None && this.FitAxis(vector, in options, false))
        {
            moved = true;
        }

        return moved;
    }

    /// <summary>
    /// Fits one axis of the outline: gathers coordinates, detects segments and edges,
    /// snaps anchors and stems, applies the deltas and interpolates untouched points.
    /// </summary>
    /// <param name="vector">The glyph vector being fitted.</param>
    /// <param name="options">The fitting parameters.</param>
    /// <param name="isXAxis">Whether the horizontal axis is being fitted; otherwise the vertical axis.</param>
    /// <returns><see langword="true"/> if any point was moved on the axis; otherwise, <see langword="false"/>.</returns>
    private bool FitAxis(GlyphVector vector, in GridFitOptions options, bool isXAxis)
    {
        IList<ControlPoint> controlPoints = vector.ControlPoints;
        IReadOnlyList<ushort> endPoints = vector.EndPoints;
        int pointCount = controlPoints.Count;

        // Feature sizes scale with the em, so the minimum extent that separates a stem
        // flank from curve noise scales too, within fixed bounds.
        this.minSegmentExtent = Math.Clamp(options.PixelsPerEm * GridFitterTuning.MinSegmentExtentPerPpem, GridFitterTuning.MinSegmentExtentFloorPx, GridFitterTuning.MinSegmentExtentCeilingPx);

        // Pass 0: gather coordinates for the axis, validate, and determine the outline
        // winding so edge directions can classify which side of a flank carries ink.
        float shoelace = 0F;
        int contourStart = 0;
        for (int c = 0; c < endPoints.Count; c++)
        {
            int contourEnd = endPoints[c];
            if (contourEnd >= pointCount || contourEnd < contourStart)
            {
                return false;
            }

            for (int i = contourStart; i <= contourEnd; i++)
            {
                ControlPoint point = controlPoints[i];
                float axis = isXAxis ? point.Point.X : point.Point.Y;
                float perpendicular = isXAxis ? point.Point.Y : point.Point.X;
                if (!float.IsFinite(axis) || !float.IsFinite(perpendicular))
                {
                    return false;
                }

                this.axisOriginal[i] = axis;
                this.axisCurrent[i] = axis;
                this.perp[i] = perpendicular;
                this.touched[i] = false;
                this.consumed[i] = false;

                int next = i == contourEnd ? contourStart : i + 1;
                ControlPoint nextPoint = controlPoints[next];
                shoelace += (point.Point.X * nextPoint.Point.Y) - (nextPoint.Point.X * point.Point.Y);
            }

            contourStart = contourEnd + 1;
        }

        // TrueType outlines wind outer contours clockwise in Y up space, giving a negative
        // shoelace sum with ink on the right of the travel direction. A positive sum marks
        // a reversed outline whose directions must be negated.
        int dirFactor = (isXAxis ? 1 : -1) * (shoelace > 0F ? -1 : 1);

        if (!this.CollectSegments(endPoints, pointCount, dirFactor))
        {
            return false;
        }

        if (this.segmentCount == 0)
        {
            return false;
        }

        if (!this.BuildEdges())
        {
            return false;
        }

        GridFitAxisMode axisMode = isXAxis ? options.FitX : options.FitY;
        if (!isXAxis && axisMode == GridFitAxisMode.Full)
        {
            this.SnapAnchors(in options);
        }

        this.PairStems();

        if (DebugLog is not null)
        {
            DebugLog.AppendLine(FormattableString.Invariant($"axis={(isXAxis ? "X" : "Y")} mode={axisMode} segments={this.segmentCount} edges={this.edgeCount}"));
            for (int i = 0; i < this.edgeCount; i++)
            {
                ref Edge e = ref this.edges[i];
                DebugLog.AppendLine(FormattableString.Invariant($"  edge[{i}] pos={e.Pos:0.###} dir={e.Dir} extent={e.Extent:0.###} perp=[{e.PerpMin:0.###},{e.PerpMax:0.###}] round={e.Round} fitted={e.Fitted} new={e.NewPos:0.###} link={e.Link}"));
            }
        }

        this.SnapStems(axisMode == GridFitAxisMode.Rescue);
        this.AbsorbSatellites();

        if (DebugLog is not null)
        {
            for (int i = 0; i < this.edgeCount; i++)
            {
                ref Edge e = ref this.edges[i];
                DebugLog.AppendLine(FormattableString.Invariant($"  post[{i}] pos={e.Pos:0.###} dir={e.Dir} fitted={e.Fitted} new={e.NewPos:0.###} link={e.Link}"));
            }
        }

        bool movedAny = this.ApplyEdgeDeltas();
        if (!movedAny)
        {
            return false;
        }

        this.InterpolateUntouched(endPoints);

        if (!isXAxis)
        {
            this.SuppressOvershoots(pointCount);
        }

        // Pass 8: scatter the fitted coordinates back into the outline.
        for (int i = 0; i < pointCount; i++)
        {
            if (this.axisCurrent[i] != this.axisOriginal[i])
            {
                ControlPoint point = controlPoints[i];
                if (isXAxis)
                {
                    point.Point.X = this.axisCurrent[i];
                }
                else
                {
                    point.Point.Y = this.axisCurrent[i];
                }

                controlPoints[i] = point;
            }
        }

        return true;
    }

    /// <summary>
    /// Fits one axis of a buffered outline from declared stem zones: gathers coordinates,
    /// seeds edges from the zones, snaps anchors and stems, applies the deltas to points
    /// on the declared flanks and interpolates the rest.
    /// </summary>
    /// <param name="points">The outline points, in contour order.</param>
    /// <param name="contourEnds">The index of the last point of each contour.</param>
    /// <param name="declaredStems">The declared stem zones for the axis as low and high edge pairs.</param>
    /// <param name="options">The fitting parameters.</param>
    /// <param name="isXAxis">Whether the horizontal axis is being fitted; otherwise the vertical axis.</param>
    /// <returns><see langword="true"/> if any point was moved on the axis; otherwise, <see langword="false"/>.</returns>
    private bool FitBufferedAxis(Vector2[] points, ushort[] contourEnds, float[] declaredStems, in GridFitOptions options, bool isXAxis)
    {
        int pointCount = points.Length;

        // Gather the axis and perpendicular coordinates and the outline's perpendicular
        // range, which stands in for per edge extents: declared zones are authoritative,
        // so no overlap scoring is needed.
        float perpLow = float.MaxValue;
        float perpHigh = float.MinValue;
        int contourStart = 0;
        for (int c = 0; c < contourEnds.Length; c++)
        {
            int contourEnd = contourEnds[c];
            if (contourEnd >= pointCount || contourEnd < contourStart)
            {
                return false;
            }

            contourStart = contourEnd + 1;
        }

        for (int i = 0; i < pointCount; i++)
        {
            float axis = isXAxis ? points[i].X : points[i].Y;
            float perpendicular = isXAxis ? points[i].Y : points[i].X;
            if (!float.IsFinite(axis) || !float.IsFinite(perpendicular))
            {
                return false;
            }

            this.axisOriginal[i] = axis;
            this.axisCurrent[i] = axis;
            this.perp[i] = perpendicular;
            this.touched[i] = false;
            perpLow = MathF.Min(perpLow, perpendicular);
            perpHigh = MathF.Max(perpHigh, perpendicular);
        }

        if (!this.SeedDeclaredEdges(declaredStems, perpLow, perpHigh, 20.5F * options.AnchorScale))
        {
            return false;
        }

        GridFitAxisMode axisMode = isXAxis ? options.FitX : options.FitY;
        if (!isXAxis)
        {
            this.SnapAnchors(in options);
        }

        this.SnapStems(axisMode == GridFitAxisMode.Rescue);

        if (!this.ApplyDeclaredEdgeDeltas(pointCount))
        {
            return false;
        }

        this.InterpolateUntouched(contourEnds);

        if (!isXAxis)
        {
            this.SuppressOvershoots(pointCount);
        }

        for (int i = 0; i < pointCount; i++)
        {
            if (this.axisCurrent[i] != this.axisOriginal[i])
            {
                if (isXAxis)
                {
                    points[i].X = this.axisCurrent[i];
                }
                else
                {
                    points[i].Y = this.axisCurrent[i];
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the edge list directly from declared stem zones. Each zone contributes a
    /// linked pair of opposing edges sorted by position. Ghost stems, whose zones are
    /// inverted, declare a single alignment edge with no opposing flank: the first value
    /// of the pair is the real edge and the inverted width selects its side, twenty for a
    /// top edge and twenty one for a bottom edge. They seed unlinked edges that
    /// participate in anchor snapping only.
    /// </summary>
    /// <param name="declaredStems">The declared zones as low and high edge pairs.</param>
    /// <param name="perpLow">The outline's lowest perpendicular coordinate.</param>
    /// <param name="perpHigh">The outline's highest perpendicular coordinate.</param>
    /// <param name="ghostBottomThreshold">The inverted width, in pixels, separating bottom edge ghosts from top edge ghosts: twenty and a half design units under the caller's scale.</param>
    /// <returns><see langword="true"/> if at least one zone was seeded; otherwise, <see langword="false"/>.</returns>
    private bool SeedDeclaredEdges(float[] declaredStems, float perpLow, float perpHigh, float ghostBottomThreshold)
    {
        this.segmentCount = 0;
        this.edgeCount = 0;

        // Order the zones by their first edge; mask alternation can declare them out
        // of order and the stem snapping pass walks edges in ascending position.
        int pairCount = 0;
        int edgesNeeded = 0;
        for (int i = 0; i + 1 < declaredStems.Length; i += 2)
        {
            int contribution = declaredStems[i + 1] > declaredStems[i] ? 2 : 1;
            if (edgesNeeded + contribution <= MaxEdges)
            {
                this.sortOrder[pairCount++] = i;
                edgesNeeded += contribution;
            }
        }

        for (int i = 1; i < pairCount; i++)
        {
            int key = this.sortOrder[i];
            float keyLow = declaredStems[key];
            int j = i - 1;
            while (j >= 0 && declaredStems[this.sortOrder[j]] > keyLow)
            {
                this.sortOrder[j + 1] = this.sortOrder[j];
                j--;
            }

            this.sortOrder[j + 1] = key;
        }

        for (int i = 0; i < pairCount; i++)
        {
            float low = declaredStems[this.sortOrder[i]];
            float high = declaredStems[this.sortOrder[i] + 1];

            if (high <= low)
            {
                ref Edge ghost = ref this.edges[this.edgeCount];
                ghost.FirstSegment = -1;
                ghost.Link = -1;
                ghost.Pos = low;
                ghost.NewPos = low;
                ghost.PerpMin = perpLow;
                ghost.PerpMax = perpHigh;
                ghost.Extent = perpHigh - perpLow;
                ghost.Dir = (sbyte)(low - high > ghostBottomThreshold ? 1 : -1);
                ghost.Round = false;
                ghost.Fitted = false;
                ghost.Anchored = false;

                this.edgeCount++;
                continue;
            }

            ref Edge left = ref this.edges[this.edgeCount];
            left.FirstSegment = -1;
            left.Link = this.edgeCount + 1;
            left.Pos = low;
            left.NewPos = low;
            left.PerpMin = perpLow;
            left.PerpMax = perpHigh;
            left.Extent = perpHigh - perpLow;
            left.Dir = 1;
            left.Round = false;
            left.Fitted = false;
            left.Anchored = false;

            ref Edge right = ref this.edges[this.edgeCount + 1];
            right.FirstSegment = -1;
            right.Link = this.edgeCount;
            right.Pos = high;
            right.NewPos = high;
            right.PerpMin = perpLow;
            right.PerpMax = perpHigh;
            right.Extent = perpHigh - perpLow;
            right.Dir = -1;
            right.Round = false;
            right.Fitted = false;
            right.Anchored = false;

            this.edgeCount += 2;
        }

        return this.edgeCount > 0;
    }

    /// <summary>
    /// Moves every point lying on a fitted declared flank by that flank's delta. Declared
    /// edges carry no detected member segments, so membership is by proximity to the
    /// declared position; each point follows its nearest flank within the alignment band.
    /// </summary>
    /// <param name="pointCount">The number of outline points.</param>
    /// <returns><see langword="true"/> if any edge produced a non zero delta; otherwise, <see langword="false"/>.</returns>
    private bool ApplyDeclaredEdgeDeltas(int pointCount)
    {
        bool moved = false;
        for (int p = 0; p < pointCount; p++)
        {
            float value = this.axisOriginal[p];
            float bestDistance = GridFitterTuning.SegmentSlackPx;
            int bestEdge = -1;
            for (int i = 0; i < this.edgeCount; i++)
            {
                ref Edge edge = ref this.edges[i];
                if (!edge.Fitted)
                {
                    continue;
                }

                float distance = MathF.Abs(value - edge.Pos);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    bestEdge = i;
                }
            }

            if (bestEdge >= 0)
            {
                float delta = this.edges[bestEdge].NewPos - this.edges[bestEdge].Pos;
                this.axisCurrent[p] = value + delta;
                this.touched[p] = true;
                moved |= delta != 0F;
            }
        }

        return moved;
    }

    /// <summary>
    /// Pass 1: detects segments, the maximal runs of consecutive points that stay within a
    /// small band on the fitted axis while sweeping monotonically on the perpendicular
    /// axis, plus curve extrema that act as the flanks of round stems.
    /// </summary>
    /// <param name="endPoints">The indices of the last point of each contour.</param>
    /// <param name="pointCount">The number of points in the outline.</param>
    /// <param name="dirFactor">The sign factor mapping perpendicular sweep direction to the ink side.</param>
    /// <returns><see langword="false"/> when the segment budget is exceeded and the axis must be abandoned.</returns>
    private bool CollectSegments(IReadOnlyList<ushort> endPoints, int pointCount, int dirFactor)
    {
        this.segmentCount = 0;
        int contourStart = 0;
        for (int c = 0; c < endPoints.Count; c++)
        {
            int contourEnd = endPoints[c];
            int count = contourEnd - contourStart + 1;
            if (count >= 2)
            {
                if (!this.CollectContourSegments(contourStart, count, dirFactor))
                {
                    return false;
                }
            }

            contourStart = contourEnd + 1;
        }

        return true;
    }

    /// <summary>
    /// Detects the aligned runs of a single contour.
    /// </summary>
    /// <param name="contourStart">The index of the contour's first point.</param>
    /// <param name="count">The number of points in the contour.</param>
    /// <param name="dirFactor">The sign factor mapping perpendicular sweep direction to the ink side.</param>
    /// <returns><see langword="false"/> when the segment budget is exceeded and the axis must be abandoned.</returns>
    private bool CollectContourSegments(int contourStart, int count, int dirFactor)
    {
        // Start walking at a point whose incoming polygon edge breaks alignment so that no
        // run is split across the walk boundary. A contour with no break is a sliver that
        // stays within the band for its whole length; treat it as a single candidate run.
        int breakOffset = -1;
        for (int k = 0; k < count; k++)
        {
            int prev = contourStart + (((k - 1) + count) % count);
            int cur = contourStart + k;
            if (MathF.Abs(this.axisOriginal[cur] - this.axisOriginal[prev]) > GridFitterTuning.SegmentSlackPx)
            {
                breakOffset = k;
                break;
            }
        }

        if (breakOffset < 0)
        {
            return this.TryAddRun(contourStart, count, contourStart, 0, count, dirFactor);
        }

        int processed = 0;
        int offset = breakOffset;
        while (processed < count)
        {
            int runStartIndex = contourStart + ((offset + processed) % count);
            float runAnchor = this.axisOriginal[runStartIndex];
            int runLength = 1;
            while (processed + runLength < count)
            {
                int prevIndex = contourStart + ((offset + processed + runLength - 1) % count);
                int nextIndex = contourStart + ((offset + processed + runLength) % count);
                if (MathF.Abs(this.axisOriginal[nextIndex] - this.axisOriginal[prevIndex]) > GridFitterTuning.SegmentSlackPx)
                {
                    break;
                }

                if (MathF.Abs(this.axisOriginal[nextIndex] - runAnchor) > GridFitterTuning.SegmentSlackPx)
                {
                    break;
                }

                runLength++;
            }

            if (runLength >= 2 && !this.TryAddRun(contourStart, count, runStartIndex, 0, runLength, dirFactor))
            {
                return false;
            }

            processed += runLength;
        }

        return this.CollectContourExtrema(contourStart, count, dirFactor);
    }

    /// <summary>
    /// Evaluates one aligned run and records it as a segment when it has enough
    /// perpendicular extent and sweeps monotonically enough to be a stem flank.
    /// </summary>
    /// <param name="contourStart">The index of the contour's first point.</param>
    /// <param name="contourCount">The number of points in the contour.</param>
    /// <param name="firstIndex">The point index at which the run starts.</param>
    /// <param name="firstOffset">The offset from <paramref name="firstIndex"/> at which evaluation starts.</param>
    /// <param name="runLength">The number of points in the run.</param>
    /// <param name="dirFactor">The sign factor mapping perpendicular sweep direction to the ink side.</param>
    /// <returns><see langword="false"/> when the segment budget is exceeded and the axis must be abandoned.</returns>
    private bool TryAddRun(int contourStart, int contourCount, int firstIndex, int firstOffset, int runLength, int dirFactor)
    {
        float perpMin = float.MaxValue;
        float perpMax = float.MinValue;
        float posSum = 0F;
        for (int k = 0; k < runLength; k++)
        {
            int index = contourStart + ((firstIndex - contourStart + firstOffset + k) % contourCount);
            float p = this.perp[index];
            perpMin = MathF.Min(perpMin, p);
            perpMax = MathF.Max(perpMax, p);
            posSum += this.axisOriginal[index];
        }

        float extent = perpMax - perpMin;
        if (extent < this.minSegmentExtent)
        {
            return true;
        }

        int first = contourStart + ((firstIndex - contourStart + firstOffset) % contourCount);
        int last = contourStart + ((firstIndex - contourStart + firstOffset + runLength - 1) % contourCount);
        float sweep = this.perp[last] - this.perp[first];
        if (MathF.Abs(sweep) < 0.5F * extent || sweep == 0F)
        {
            return true;
        }

        if (this.segmentCount >= MaxSegments)
        {
            return false;
        }

        ref Segment segment = ref this.segments[this.segmentCount];
        segment.First = first;
        segment.Count = runLength;
        segment.ContourStart = contourStart;
        segment.ContourCount = contourCount;
        segment.NextInEdge = -1;
        segment.Pos = posSum / runLength;
        segment.PerpMin = perpMin;
        segment.PerpMax = perpMax;
        segment.Extent = extent;
        segment.Dir = (sbyte)(MathF.Sign(sweep) * dirFactor);
        segment.Round = false;
        this.segmentCount++;

        for (int k = 0; k < runLength; k++)
        {
            int index = contourStart + ((firstIndex - contourStart + firstOffset + k) % contourCount);
            this.consumed[index] = true;
        }

        return true;
    }

    /// <summary>
    /// Detects strict local extrema on the fitted axis among points no flat run consumed.
    /// These are the flanks of round stems, whose neighboring curve controls sit within a
    /// wider band than flat runs allow.
    /// </summary>
    /// <param name="contourStart">The index of the contour's first point.</param>
    /// <param name="count">The number of points in the contour.</param>
    /// <param name="dirFactor">The sign factor mapping perpendicular sweep direction to the ink side.</param>
    /// <returns><see langword="false"/> when the segment budget is exceeded and the axis must be abandoned.</returns>
    private bool CollectContourExtrema(int contourStart, int count, int dirFactor)
    {
        for (int k = 0; k < count; k++)
        {
            int index = contourStart + k;
            if (this.consumed[index])
            {
                continue;
            }

            int prev = contourStart + (((k - 1) + count) % count);
            int next = contourStart + ((k + 1) % count);
            float axis = this.axisOriginal[index];
            float prevAxis = this.axisOriginal[prev];
            float nextAxis = this.axisOriginal[next];

            bool isMinimum = axis < prevAxis && axis < nextAxis;
            bool isMaximum = axis > prevAxis && axis > nextAxis;
            if (!isMinimum && !isMaximum)
            {
                continue;
            }

            if (MathF.Abs(prevAxis - axis) > GridFitterTuning.RoundExtremumSlackPx || MathF.Abs(nextAxis - axis) > GridFitterTuning.RoundExtremumSlackPx)
            {
                continue;
            }

            float sweep = this.perp[next] - this.perp[prev];
            if (sweep == 0F)
            {
                continue;
            }

            if (this.segmentCount >= MaxSegments)
            {
                return false;
            }

            float perpMin = MathF.Min(this.perp[prev], MathF.Min(this.perp[index], this.perp[next]));
            float perpMax = MathF.Max(this.perp[prev], MathF.Max(this.perp[index], this.perp[next]));

            ref Segment segment = ref this.segments[this.segmentCount];
            segment.First = prev;
            segment.Count = 3;
            segment.ContourStart = contourStart;
            segment.ContourCount = count;
            segment.NextInEdge = -1;
            segment.Pos = axis;
            segment.PerpMin = perpMin;
            segment.PerpMax = perpMax;
            segment.Extent = MathF.Max(perpMax - perpMin, 0.25F);
            segment.Dir = (sbyte)(MathF.Sign(sweep) * dirFactor);
            segment.Round = true;
            this.segmentCount++;

            this.consumed[index] = true;
        }

        return true;
    }

    /// <summary>
    /// Pass 2: sorts segments by position and merges those within a small band and with a
    /// matching direction into logical edges. Opposing flanks never merge regardless of
    /// how close they sit so hairline stems survive as two edges.
    /// </summary>
    /// <returns><see langword="false"/> when the edge budget is exceeded and the axis must be abandoned.</returns>
    private bool BuildEdges()
    {
        int count = this.segmentCount;
        for (int i = 0; i < count; i++)
        {
            this.sortOrder[i] = i;
        }

        // Insertion sort by position: segment counts are small and typically nearly sorted.
        for (int i = 1; i < count; i++)
        {
            int key = this.sortOrder[i];
            float keyPos = this.segments[key].Pos;
            int j = i - 1;
            while (j >= 0 && this.segments[this.sortOrder[j]].Pos > keyPos)
            {
                this.sortOrder[j + 1] = this.sortOrder[j];
                j--;
            }

            this.sortOrder[j + 1] = key;
        }

        this.edgeCount = 0;
        float lastPos = 0F;
        for (int i = 0; i < count; i++)
        {
            int segmentIndex = this.sortOrder[i];
            ref Segment segment = ref this.segments[segmentIndex];

            bool startNew = this.edgeCount == 0;
            if (!startNew)
            {
                ref Edge lastEdge = ref this.edges[this.edgeCount - 1];
                startNew = segment.Pos - lastPos > GridFitterTuning.EdgeMergePx || segment.Dir != lastEdge.Dir;
            }

            if (startNew)
            {
                if (this.edgeCount >= MaxEdges)
                {
                    return false;
                }

                ref Edge edge = ref this.edges[this.edgeCount];
                edge.FirstSegment = segmentIndex;
                edge.Link = -1;
                edge.Pos = 0F;
                edge.NewPos = 0F;
                edge.PerpMin = segment.PerpMin;
                edge.PerpMax = segment.PerpMax;
                edge.Extent = 0F;
                edge.Dir = segment.Dir;
                edge.Round = segment.Round;
                edge.Fitted = false;
                edge.Anchored = false;
                segment.NextInEdge = -1;
                this.edgeCount++;
            }
            else
            {
                ref Edge edge = ref this.edges[this.edgeCount - 1];
                segment.NextInEdge = edge.FirstSegment;
                edge.FirstSegment = segmentIndex;
                edge.PerpMin = MathF.Min(edge.PerpMin, segment.PerpMin);
                edge.PerpMax = MathF.Max(edge.PerpMax, segment.PerpMax);
                edge.Round &= segment.Round;
            }

            lastPos = segment.Pos;
        }

        // Each edge takes the position of its dominant member so a merged flank run still
        // lands exactly on the grid; a mean over members would strand every member a
        // fraction off the boundary. Lesser members keep their offsets from the dominant.
        for (int i = 0; i < this.edgeCount; i++)
        {
            ref Edge edge = ref this.edges[i];
            float weight = 0F;
            float dominantExtent = -1F;
            float dominantPos = 0F;
            for (int s = edge.FirstSegment; s >= 0; s = this.segments[s].NextInEdge)
            {
                float extent = this.segments[s].Extent;
                weight += extent;
                if (extent > dominantExtent)
                {
                    dominantExtent = extent;
                    dominantPos = this.segments[s].Pos;
                }
            }

            edge.Extent = weight;
            edge.Pos = dominantPos;
            edge.NewPos = dominantPos;
        }

        return true;
    }

    /// <summary>
    /// Pass 3: snaps vertical axis edges near the baseline or an alignment height to the
    /// rounded anchor. Snapping overshoots and flats to the same pixel is what keeps round
    /// and flat glyph heights consistent across a line of text. Only the edge whose ink
    /// faces away from the anchor snaps: the baseline and the descender depths attract
    /// bottom edges and the height anchors attract top edges. Each edge snaps to its
    /// nearest candidate because the anchor arrays carry no ordering guarantee. The
    /// stroke's opposite edge then follows through stem width normalization, so a thin
    /// stroke can never collapse onto its own anchor.
    /// </summary>
    /// <param name="options">The fitting parameters carrying the anchor heights.</param>
    private void SnapAnchors(in GridFitOptions options)
    {
        for (int i = 0; i < this.edgeCount; i++)
        {
            ref Edge edge = ref this.edges[i];
            if (edge.Dir == 1)
            {
                float best = 0F;
                float bestDistance = MathF.Abs(edge.Pos);

                float[] bottomAnchors = options.BottomAnchors;
                for (int a = 0; a < bottomAnchors.Length; a++)
                {
                    if (bottomAnchors[a] >= 0F)
                    {
                        continue;
                    }

                    float anchor = bottomAnchors[a] * options.AnchorScale;
                    float distance = MathF.Abs(edge.Pos - anchor);
                    if (distance < bestDistance)
                    {
                        best = anchor;
                        bestDistance = distance;
                    }
                }

                TrySnapAnchor(ref edge, best);
            }
            else if (edge.Dir == -1)
            {
                float best = 0F;
                float bestDistance = float.MaxValue;

                float[] topAnchors = options.TopAnchors;
                for (int a = 0; a < topAnchors.Length; a++)
                {
                    if (topAnchors[a] <= 0F)
                    {
                        continue;
                    }

                    float anchor = topAnchors[a] * options.AnchorScale;
                    float distance = MathF.Abs(edge.Pos - anchor);
                    if (distance < bestDistance)
                    {
                        best = anchor;
                        bestDistance = distance;
                    }
                }

                if (bestDistance != float.MaxValue)
                {
                    TrySnapAnchor(ref edge, best);
                }
            }
        }
    }

    /// <summary>
    /// Snaps one edge to a whole pixel anchor position when it sits within snapping range.
    /// Top edges take the ceiling of the anchor so an alignment height always earns its
    /// full pixel row, stretching small strokes upward rather than truncating them; bottom
    /// edges round to nearest, keeping the baseline exact and descender depths balanced.
    /// </summary>
    /// <param name="edge">The edge to snap.</param>
    /// <param name="anchor">The anchor height in pixels.</param>
    /// <returns><see langword="true"/> if the edge was snapped; otherwise, <see langword="false"/>.</returns>
    private static bool TrySnapAnchor(ref Edge edge, float anchor)
    {
        if (MathF.Abs(edge.Pos - anchor) > GridFitterTuning.AnchorSnapRangePx)
        {
            return false;
        }

        edge.NewPos = edge.Dir == -1 ? MathF.Ceiling(anchor - GridFitterTuning.AnchorCeilingFuzzPx) : MathF.Floor(anchor + 0.5F);
        edge.Fitted = true;
        edge.Anchored = true;
        return true;
    }

    /// <summary>
    /// Flattens overshoots onto their fitted alignment rows. Serif tips and curve extrema
    /// drawn just past an alignment height are shifted rigidly by interpolation, so after
    /// fitting they can poke one row beyond the snapped edge and light a stray pixel row.
    /// Classic rasterizers collapse all ink within the zone onto the aligned row at small
    /// sizes; any point that started within snapping range of an anchored edge and ended
    /// beyond its fitted position clamps onto that position.
    /// </summary>
    /// <param name="pointCount">The number of outline points.</param>
    private void SuppressOvershoots(int pointCount)
    {
        for (int e = 0; e < this.edgeCount; e++)
        {
            ref Edge edge = ref this.edges[e];
            if (!edge.Anchored)
            {
                continue;
            }

            if (edge.Dir == -1)
            {
                float limit = edge.Pos + GridFitterTuning.AnchorSnapRangePx;
                for (int p = 0; p < pointCount; p++)
                {
                    if (this.axisOriginal[p] > edge.Pos && this.axisOriginal[p] <= limit && this.axisCurrent[p] > edge.NewPos)
                    {
                        this.axisCurrent[p] = edge.NewPos;
                        this.touched[p] = true;
                    }
                }
            }
            else
            {
                float limit = edge.Pos - GridFitterTuning.AnchorSnapRangePx;
                for (int p = 0; p < pointCount; p++)
                {
                    if (this.axisOriginal[p] < edge.Pos && this.axisOriginal[p] >= limit && this.axisCurrent[p] < edge.NewPos)
                    {
                        this.axisCurrent[p] = edge.NewPos;
                        this.touched[p] = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Pass 4: pairs opposing edges into stems. A pair requires ink between the edges, a
    /// plausible width and overlapping perpendicular extents, and both edges must choose
    /// each other so a flank is never claimed by two stems.
    /// </summary>
    private void PairStems()
    {
        for (int i = 0; i < this.edgeCount; i++)
        {
            ref Edge edge = ref this.edges[i];
            if (edge.Dir != 1)
            {
                continue;
            }

            edge.Link = this.FindBestPartner(i);
        }

        for (int i = 0; i < this.edgeCount; i++)
        {
            ref Edge edge = ref this.edges[i];
            if (edge.Dir != -1)
            {
                continue;
            }

            int best = this.FindBestPartner(i);
            if (best >= 0 && this.edges[best].Link == i)
            {
                edge.Link = best;
            }
        }

        // Clear one sided links so only mutual pairs survive.
        for (int i = 0; i < this.edgeCount; i++)
        {
            ref Edge edge = ref this.edges[i];
            if (edge.Dir == 1 && edge.Link >= 0 && this.edges[edge.Link].Link != i)
            {
                edge.Link = -1;
            }
        }
    }

    /// <summary>
    /// Finds the opposing edge that forms the most plausible stem with the given edge,
    /// scored by perpendicular overlap against width.
    /// </summary>
    /// <param name="index">The index of the edge seeking a partner.</param>
    /// <returns>The index of the best opposing edge, or <c>-1</c> when none qualifies.</returns>
    private int FindBestPartner(int index)
    {
        ref Edge edge = ref this.edges[index];
        int best = -1;
        float bestScore = float.MinValue;
        for (int j = 0; j < this.edgeCount; j++)
        {
            if (j == index)
            {
                continue;
            }

            ref Edge other = ref this.edges[j];
            if (other.Dir != -edge.Dir)
            {
                continue;
            }

            float width = edge.Dir == 1 ? other.Pos - edge.Pos : edge.Pos - other.Pos;
            if (width < GridFitterTuning.MinStemWidthPx || width > GridFitterTuning.MaxStemWidthPx)
            {
                continue;
            }

            // Both flanks of a real stroke overlap over most of their shared extent. Testing
            // against the larger extent stops a long edge such as a baseline from claiming a
            // small unrelated feature as its opposing flank across the glyph interior.
            float overlap = MathF.Min(edge.PerpMax, other.PerpMax) - MathF.Max(edge.PerpMin, other.PerpMin);
            if (overlap <= 0F || overlap < 0.5F * MathF.Max(edge.Extent, other.Extent))
            {
                continue;
            }

            float score = overlap - (0.3F * width);
            if (score > bestScore)
            {
                bestScore = score;
                best = j;
            }
        }

        return best;
    }

    /// <summary>
    /// Pass 5: decides the fitted position of each stem. Widths round to whole pixels with
    /// sub pixel stems widened to one pixel, positions round from the stem center, and
    /// counters between successive stems never fall below one pixel when they were open in
    /// the design. A pair whose fit would exceed the movement cap or fold a counter is
    /// reverted rather than distorted. In rescue mode only strokes thinner than a pixel are
    /// processed; instruction fitted geometry is left exactly where the font put it.
    /// </summary>
    private void SnapStems(bool rescueOnly)
    {
        float prevRight = float.MinValue;
        float prevRightOriginal = float.MinValue;
        for (int i = 0; i < this.edgeCount; i++)
        {
            ref Edge left = ref this.edges[i];
            if (left.Dir != 1 || left.Link < 0 || left.Link <= i)
            {
                continue;
            }

            ref Edge right = ref this.edges[left.Link];
            if (left.Fitted && right.Fitted)
            {
                prevRight = right.NewPos;
                prevRightOriginal = right.Pos;
                continue;
            }

            float width = right.Pos - left.Pos;
            if (rescueOnly && width >= GridFitterTuning.RescueMaxWidthPx)
            {
                continue;
            }

            if (left.Round && right.Round && width >= GridFitterTuning.OnePixelWidthPx && MathF.Abs(width - MathF.Floor(width + 0.5F)) > GridFitterTuning.RoundWidthSnapPx)
            {
                continue;
            }

            float fittedWidth = width < GridFitterTuning.OnePixelWidthPx ? 1F : MathF.Floor(width + 0.5F);

            // Sub pixel strokes must widen to a full pixel, so the flank movement that the
            // widening itself demands is granted on top of the base cap. Reverting such a
            // pair would leave one anchored flank moved and the other interpolated past it,
            // inverting the stroke, which is far worse than the bounded extra movement.
            float allowance = GridFitterTuning.MaxEdgeDeltaPx + MathF.Max(0F, 1F - width);

            float newLeft;
            float newRight;
            if (left.Fitted != right.Fitted)
            {
                // One side is already anchored; grow the stem from the anchor.
                if (left.Fitted)
                {
                    newLeft = left.NewPos;
                    newRight = newLeft + fittedWidth;
                }
                else
                {
                    newRight = right.NewPos;
                    newLeft = newRight - fittedWidth;
                }
            }
            else
            {
                float center = (left.Pos + right.Pos) * 0.5F;
                bool oddWidth = ((int)fittedWidth & 1) == 1;
                float fittedCenter = oddWidth ? MathF.Floor(center) + 0.5F : MathF.Ceiling(center - 0.5F);
                newLeft = fittedCenter - (fittedWidth * 0.5F);
                newRight = fittedCenter + (fittedWidth * 0.5F);
            }

            // Keep a counter that was open in the design open in the fit.
            if (prevRight > float.MinValue && left.Pos - prevRightOriginal >= 0.5F && newLeft - prevRight < 1F)
            {
                float shift = MathF.Ceiling(1F - (newLeft - prevRight));
                newLeft += shift;
                newRight += shift;
            }

            if (prevRight > float.MinValue && newLeft <= prevRight)
            {
                continue;
            }

            if (MathF.Abs(newLeft - left.Pos) > allowance || MathF.Abs(newRight - right.Pos) > allowance)
            {
                continue;
            }

            left.NewPos = newLeft;
            left.Fitted = true;
            right.NewPos = newRight;
            right.Fitted = true;
            prevRight = newRight;
            prevRightOriginal = right.Pos;
        }
    }

    /// <summary>
    /// Pass 5b: an unfitted edge that runs in the same direction as a nearby fitted edge is
    /// a satellite of the same flank, split off by a slight slant. It follows its sibling's
    /// movement so interpolation cannot drag the two apart.
    /// </summary>
    private void AbsorbSatellites()
    {
        for (int i = 0; i < this.edgeCount; i++)
        {
            ref Edge edge = ref this.edges[i];
            if (edge.Fitted)
            {
                continue;
            }

            for (int j = 0; j < this.edgeCount; j++)
            {
                ref Edge other = ref this.edges[j];
                if (!other.Fitted || other.Dir != edge.Dir)
                {
                    continue;
                }

                if (MathF.Abs(edge.Pos - other.Pos) > GridFitterTuning.SatelliteRangePx)
                {
                    continue;
                }

                float delta = other.NewPos - other.Pos;
                if (delta != 0F)
                {
                    edge.NewPos = edge.Pos + delta;
                    edge.Fitted = true;
                }

                break;
            }
        }
    }

    /// <summary>
    /// Pass 6: moves every member point of each fitted edge by the edge delta, preserving
    /// the point's offset within the edge band so slightly slanted flanks stay slanted.
    /// </summary>
    /// <returns><see langword="true"/> if any edge produced a non zero delta; otherwise, <see langword="false"/>.</returns>
    private bool ApplyEdgeDeltas()
    {
        bool moved = false;
        for (int i = 0; i < this.edgeCount; i++)
        {
            ref Edge edge = ref this.edges[i];
            if (!edge.Fitted)
            {
                continue;
            }

            float delta = edge.NewPos - edge.Pos;
            if (delta == 0F)
            {
                // A zero delta edge still pins its points so interpolation respects it.
                for (int s = edge.FirstSegment; s >= 0; s = this.segments[s].NextInEdge)
                {
                    this.MarkSegment(s, 0F);
                }

                continue;
            }

            for (int s = edge.FirstSegment; s >= 0; s = this.segments[s].NextInEdge)
            {
                this.MarkSegment(s, delta);
            }

            moved = true;
        }

        return moved;
    }

    /// <summary>
    /// Moves every point of one segment by the given delta and marks the points touched.
    /// </summary>
    /// <param name="segmentIndex">The index of the segment to move.</param>
    /// <param name="delta">The movement to apply on the fitted axis.</param>
    private void MarkSegment(int segmentIndex, float delta)
    {
        ref Segment segment = ref this.segments[segmentIndex];
        for (int k = 0; k < segment.Count; k++)
        {
            int index = segment.ContourStart + ((segment.First - segment.ContourStart + k) % segment.ContourCount);
            this.axisCurrent[index] = this.axisOriginal[index] + delta;
            this.touched[index] = true;
        }
    }

    /// <summary>
    /// Pass 7: interpolates every untouched point between the touched points of its contour
    /// with the same three case semantics as the IUP instruction: points between two
    /// references scale linearly, points outside their span shift with the nearest
    /// reference, and a contour with a single touched point shifts rigidly.
    /// </summary>
    /// <param name="endPoints">The indices of the last point of each contour.</param>
    private void InterpolateUntouched(IReadOnlyList<ushort> endPoints)
    {
        int contourStart = 0;
        for (int c = 0; c < endPoints.Count; c++)
        {
            int contourEnd = endPoints[c];
            int count = contourEnd - contourStart + 1;

            int firstTouched = -1;
            for (int i = contourStart; i <= contourEnd; i++)
            {
                if (this.touched[i])
                {
                    firstTouched = i;
                    break;
                }
            }

            if (firstTouched < 0)
            {
                contourStart = contourEnd + 1;
                continue;
            }

            int reference = firstTouched;
            int cursor = firstTouched;
            for (int steps = 0; steps < count; steps++)
            {
                cursor = cursor == contourEnd ? contourStart : cursor + 1;
                if (!this.touched[cursor])
                {
                    continue;
                }

                this.InterpolateSpan(contourStart, contourEnd, reference, cursor);
                reference = cursor;
            }

            contourStart = contourEnd + 1;
        }
    }

    /// <summary>
    /// Interpolates the untouched points that sit between two touched reference points in
    /// contour order.
    /// </summary>
    /// <param name="contourStart">The index of the contour's first point.</param>
    /// <param name="contourEnd">The index of the contour's last point.</param>
    /// <param name="reference1">The index of the touched point opening the span.</param>
    /// <param name="reference2">The index of the touched point closing the span.</param>
    private void InterpolateSpan(int contourStart, int contourEnd, int reference1, int reference2)
    {
        int cursor = reference1 == contourEnd ? contourStart : reference1 + 1;
        if (cursor == reference2)
        {
            return;
        }

        float original1 = this.axisOriginal[reference1];
        float original2 = this.axisOriginal[reference2];
        float current1 = this.axisCurrent[reference1];
        float current2 = this.axisCurrent[reference2];
        float lower;
        float upper;
        float lowerCurrent;
        float upperCurrent;
        if (original1 <= original2)
        {
            lower = original1;
            upper = original2;
            lowerCurrent = current1;
            upperCurrent = current2;
        }
        else
        {
            lower = original2;
            upper = original1;
            lowerCurrent = current2;
            upperCurrent = current1;
        }

        float range = upper - lower;
        float scale = range > 0F ? (upperCurrent - lowerCurrent) / range : 0F;
        while (cursor != reference2)
        {
            float value = this.axisOriginal[cursor];
            if (value <= lower)
            {
                this.axisCurrent[cursor] = value + (lowerCurrent - lower);
            }
            else if (value >= upper)
            {
                this.axisCurrent[cursor] = value + (upperCurrent - upper);
            }
            else if (range > 0F)
            {
                this.axisCurrent[cursor] = lowerCurrent + ((value - lower) * scale);
            }
            else
            {
                this.axisCurrent[cursor] = value + (lowerCurrent - lower);
            }

            cursor = cursor == contourEnd ? contourStart : cursor + 1;
        }
    }

    /// <summary>
    /// Grows the per point scratch arrays to hold at least the given point count. The
    /// arrays are retained across pool rentals so steady state fitting never allocates.
    /// </summary>
    /// <param name="pointCount">The number of points the scratch must hold.</param>
    private void EnsureCapacity(int pointCount)
    {
        if (this.axisOriginal.Length < pointCount)
        {
            int capacity = Math.Max(pointCount, Math.Max(64, this.axisOriginal.Length * 2));
            this.axisOriginal = new float[capacity];
            this.axisCurrent = new float[capacity];
            this.perp = new float[capacity];
            this.touched = new bool[capacity];
            this.consumed = new bool[capacity];
        }
    }

    private struct Segment
    {
        public int First;
        public int Count;
        public int ContourStart;
        public int ContourCount;
        public int NextInEdge;
        public float Pos;
        public float PerpMin;
        public float PerpMax;
        public float Extent;
        public sbyte Dir;
        public bool Round;
    }

    private struct Edge
    {
        public int FirstSegment;
        public int Link;
        public float Pos;
        public float NewPos;
        public float PerpMin;
        public float PerpMax;
        public float Extent;
        public sbyte Dir;
        public bool Round;
        public bool Fitted;
        public bool Anchored;
    }

    private sealed class PooledObjectPolicy : IPooledObjectPolicy<GlyphGridFitter>
    {
        public GlyphGridFitter Create() => new();

        public bool Return(GlyphGridFitter obj) => true;
    }
}
