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
    private const int MaxHintEdges = 96;

    private static readonly ObjectPool<GlyphGridFitter> Pool = new(new PooledObjectPolicy());

    private readonly Segment[] segments = new Segment[MaxSegments];
    private readonly Edge[] edges = new Edge[MaxEdges];
    private readonly int[] sortOrder = new int[MaxSegments];
    private readonly HintEdge[] initialMap = new HintEdge[MaxHintEdges];
    private readonly HintEdge[] finalMap = new HintEdge[MaxHintEdges];
    private readonly PendingMove[] pendingMoves = new PendingMove[MaxHintEdges];
    private readonly bool[] stemWall = new bool[MaxHintEdges];
    private float[] axisOriginal = [];
    private float[] axisCurrent = [];
    private float[] perp = [];
    private bool[] touched = [];
    private bool[] consumed = [];
    private int segmentCount;
    private int edgeCount;
    private int initialMapCount;
    private int mapCount;
    private int pendingMoveCount;
    private bool initialMapValid;
    private float minSegmentExtent;

    private GlyphGridFitter()
    {
    }

    [Flags]
    private enum HintEdgeFlags : byte
    {
        None = 0,
        PairBottom = 1,
        PairTop = 2,
        GhostBottom = 4,
        GhostTop = 8,
        Locked = 16,
        Synthetic = 32,
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
    /// <param name="equalizeVerticalCounters">Whether the glyph's counter mask requests counter equalization across its vertical stems.</param>
    /// <param name="equalizeHorizontalCounters">Whether the glyph's counter mask requests counter equalization across its horizontal stems.</param>
    /// <param name="options">The fitting parameters.</param>
    /// <returns><see langword="true"/> if any point was moved; otherwise, <see langword="false"/>.</returns>
    public static bool FitInPlace(Vector2[] points, ushort[] contourEnds, float[] verticalStems, float[] horizontalStems, bool equalizeVerticalCounters, bool equalizeHorizontalCounters, in GridFitOptions options)
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
            bool moved = false;
            if (options.FitX != GridFitAxisMode.None && fitter.FitBufferedAxis(points, verticalStems, equalizeVerticalCounters, in options, true))
            {
                moved = true;
            }

            if (options.FitY != GridFitAxisMode.None && fitter.FitBufferedAxis(points, horizontalStems, equalizeHorizontalCounters, in options, false))
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
    /// Fits one axis of a buffered outline through a hint map built from the declared
    /// stems: captured hints lock to their alignment rows, the rest adjust onto pixel
    /// boundaries, and every point transforms through the resulting piecewise linear map.
    /// </summary>
    /// <param name="points">The outline points, in contour order.</param>
    /// <param name="declaredStems">The declared stem zones for the axis as edge pairs, in declaration order.</param>
    /// <param name="equalizeCounters">Whether the glyph's counter mask requests counter equalization on the axis.</param>
    /// <param name="options">The fitting parameters.</param>
    /// <param name="isXAxis">Whether the horizontal axis is being fitted; otherwise the vertical axis.</param>
    /// <returns><see langword="true"/> if any point was moved on the axis; otherwise, <see langword="false"/>.</returns>
    private bool FitBufferedAxis(Vector2[] points, float[] declaredStems, bool equalizeCounters, in GridFitOptions options, bool isXAxis)
    {
        if (declaredStems.Length < 2)
        {
            return false;
        }

        int pointCount = points.Length;
        for (int i = 0; i < pointCount; i++)
        {
            if (!float.IsFinite(points[i].X) || !float.IsFinite(points[i].Y))
            {
                return false;
            }
        }

        this.ClassifyWallStems(points, declaredStems, isXAxis);

        // The declared path fits through a hint map: hints captured by an alignment zone
        // move rigidly onto the zone's row and lock, remaining hints are positioned
        // through the captured-only initial map and then adjusted so one edge lands on a
        // pixel boundary, and every point transforms through the resulting piecewise
        // linear map. Curves crossing a zone stretch smoothly between hint edges instead
        // of collapsing onto them.
        this.BuildHintMap(declaredStems, in options, isXAxis, true);
        this.BuildHintMap(declaredStems, in options, isXAxis, false);
        if (this.mapCount == 0)
        {
            return false;
        }

        if (equalizeCounters)
        {
            this.EqualizeMapCounters();
        }

        ComputeMapScales(this.finalMap, this.mapCount);

        if (DebugLog is not null)
        {
            DebugLog.AppendLine(FormattableString.Invariant($"hintmap axis={(isXAxis ? "X" : "Y")} edges={this.mapCount}"));
            for (int i = 0; i < this.mapCount; i++)
            {
                ref HintEdge e = ref this.finalMap[i];
                DebugLog.AppendLine(FormattableString.Invariant($"  edge[{i}] cs={e.Cs:0.###} ds={e.Ds:0.###} scale={e.Scale:0.###} flags={e.Flags}"));
            }
        }

        bool moved = false;
        int lastIndex = 0;
        for (int i = 0; i < pointCount; i++)
        {
            float value = isXAxis ? points[i].X : points[i].Y;
            float mapped = MapCoordinate(this.finalMap, this.mapCount, ref lastIndex, value);
            if (mapped != value)
            {
                if (isXAxis)
                {
                    points[i].X = mapped;
                }
                else
                {
                    points[i].Y = mapped;
                }

                moved = true;
            }
        }

        return moved;
    }

    /// <summary>
    /// Builds one of the two hint maps from the declared stems. Hints captured by an
    /// alignment zone insert first, in declaration order, so alignment outranks pixel
    /// rounding when overlapping hints conflict; each insertion rejects hints that
    /// overlap an earlier one in either the unfitted or the fitted coordinate. The
    /// initial map takes only captured hints, plus a synthetic locked edge at the origin
    /// when no hint spans it, and positions the remaining hints of the final map so
    /// stems keep their place relative to the aligned features around them.
    /// </summary>
    /// <param name="declaredStems">The declared stem zones as edge pairs, in declaration order.</param>
    /// <param name="options">The fitting parameters carrying the alignment zones.</param>
    /// <param name="isXAxis">Whether the horizontal axis is being fitted; zones apply only to the vertical axis.</param>
    /// <param name="buildInitial">Whether the captured only initial map is being built; otherwise the final map.</param>
    private void BuildHintMap(float[] declaredStems, in GridFitOptions options, bool isXAxis, bool buildInitial)
    {
        HintEdge[] map = buildInitial ? this.initialMap : this.finalMap;
        int count = 0;

        if (buildInitial)
        {
            this.initialMapValid = false;
        }

        float scale = options.AnchorScale;
        float fuzz = options.BlueFuzz * scale;

        int passCount = buildInitial ? 1 : 2;
        for (int pass = 0; pass < passCount; pass++)
        {
            bool wantCaptured = pass == 0;
            for (int s = 0; s + 1 < declaredStems.Length; s += 2)
            {
                if (!TryInitHintPair(declaredStems[s], declaredStems[s + 1], scale, this.stemWall[s >> 1], out HintEdge bottom, out HintEdge top, out bool isPair))
                {
                    continue;
                }

                bool captured = !isXAxis && TryCaptureHint(ref bottom, ref top, options.Zones, scale, fuzz);
                if (captured != wantCaptured)
                {
                    continue;
                }

                this.InsertHint(map, ref count, bottom, top, isPair, buildInitial);
            }
        }

        if (buildInitial)
        {
            if (count == 0 || map[0].Cs > 0F || map[count - 1].Cs < 0F)
            {
                HintEdge zero = default;
                zero.Flags = HintEdgeFlags.GhostBottom | HintEdgeFlags.Locked | HintEdgeFlags.Synthetic;
                this.InsertHint(map, ref count, zero, default, false, true);
            }

            AdjustHintMap(map, count, this.pendingMoves, ref this.pendingMoveCount);
            ComputeMapScales(map, count);
            this.initialMapCount = count;
            this.initialMapValid = count > 0;
        }
        else
        {
            AdjustHintMap(map, count, this.pendingMoves, ref this.pendingMoveCount);
            this.mapCount = count;
        }
    }

    /// <summary>
    /// Marks each declared stem whose flanks both form walls: sustained runs of outline
    /// close to the flank, such as straight stem sides or the tall near vertical sweeps
    /// of a bowl. A diagonal stroke instead crosses its flank in a short stretch. Walls
    /// tolerate thin regularized widths because one pixel of wall fills its row or
    /// column completely, while a diagonal narrowed the same way drops below the
    /// coverage threshold and breaks apart.
    /// </summary>
    /// <param name="points">The outline points.</param>
    /// <param name="declaredStems">The declared stem zones as edge pairs.</param>
    /// <param name="isXAxis">Whether the horizontal axis is being fitted.</param>
    private void ClassifyWallStems(Vector2[] points, float[] declaredStems, bool isXAxis)
    {
        for (int s = 0; s + 1 < declaredStems.Length; s += 2)
        {
            bool wall = true;
            for (int e = 0; e < 2 && wall; e++)
            {
                float edge = declaredStems[s + e];
                float min = float.MaxValue;
                float max = float.MinValue;
                for (int p = 0; p < points.Length; p++)
                {
                    float axis = isXAxis ? points[p].X : points[p].Y;
                    if (MathF.Abs(axis - edge) <= 0.5F)
                    {
                        float perpendicular = isXAxis ? points[p].Y : points[p].X;
                        min = MathF.Min(min, perpendicular);
                        max = MathF.Max(max, perpendicular);
                    }
                }

                wall = min < max && max - min >= 2F;
            }

            this.stemWall[s >> 1] = wall;
        }
    }

    /// <summary>
    /// Expands one declared stem into hint edges. Ghost stems carry inverted widths of
    /// twenty and twenty one units: a top ghost's real edge is the first value and a
    /// bottom ghost's real edge is the second; other inverted widths are undefined by the
    /// format and are treated as a swapped pair. Pairs thinner than a pixel widen
    /// symmetrically to exactly one pixel in the fitted coordinate so light strokes
    /// cannot fall below the coverage threshold and vanish; wall pairs wider than a pixel
    /// regularize toward the thin side the same way classic rasterizers treat declared
    /// stems, keeping a wall of around one and a half pixels at a single crisp pixel.
    /// </summary>
    /// <param name="a">The first declared edge in pixel space.</param>
    /// <param name="b">The second declared edge in pixel space.</param>
    /// <param name="scale">The pixels per design unit scale identifying ghost widths.</param>
    /// <param name="wall">Whether both flanks of the stem form walls.</param>
    /// <param name="bottom">The bottom hint edge, invalid for a top ghost.</param>
    /// <param name="top">The top hint edge, invalid for a bottom ghost.</param>
    /// <param name="isPair">Whether both edges are valid and move together.</param>
    /// <returns><see langword="true"/> if the stem produced at least one valid edge; otherwise, <see langword="false"/>.</returns>
    private static bool TryInitHintPair(float a, float b, float scale, bool wall, out HintEdge bottom, out HintEdge top, out bool isPair)
    {
        bottom = default;
        top = default;
        isPair = false;

        float width = b - a;
        float ghostTolerance = 0.5F * scale;
        if (MathF.Abs(width - (-21F * scale)) < ghostTolerance)
        {
            bottom.Cs = b;
            bottom.Ds = b;
            bottom.Scale = 1F;
            bottom.Flags = HintEdgeFlags.GhostBottom;
            return true;
        }

        if (MathF.Abs(width - (-20F * scale)) < ghostTolerance)
        {
            top.Cs = a;
            top.Ds = a;
            top.Scale = 1F;
            top.Flags = HintEdgeFlags.GhostTop;
            return true;
        }

        float low = a;
        float high = b;
        if (width < 0F)
        {
            low = b;
            high = a;
        }

        bottom.Cs = low;
        bottom.Ds = low;
        bottom.Scale = 1F;
        bottom.Flags = HintEdgeFlags.PairBottom;

        top.Cs = high;
        top.Ds = high;
        top.Scale = 1F;
        top.Flags = HintEdgeFlags.PairTop;

        float dsWidth = high - low;
        if (dsWidth < GridFitterTuning.OnePixelWidthPx)
        {
            dsWidth = 1F;
        }
        else if (wall)
        {
            dsWidth = MathF.Max(1F, MathF.Floor(dsWidth + 0.25F));
        }

        if (dsWidth != high - low)
        {
            float center = (low + high) * 0.5F;
            bottom.Ds = center - (dsWidth * 0.5F);
            top.Ds = center + (dsWidth * 0.5F);
        }

        isPair = true;
        return true;
    }

    /// <summary>
    /// Tests a hint against the alignment zones in zone order: a bottom edge inside a
    /// bottom zone's band, or a top edge inside a top zone's band, captures the hint.
    /// Both edges then move rigidly so the captured edge lands on the zone's fitted row,
    /// and the hint locks so pixel rounding cannot move it again. Top rows take the
    /// ceiling of the flat edge so an alignment height always earns its full pixel row;
    /// bottom rows round to nearest, keeping the baseline exact and descenders balanced.
    /// </summary>
    /// <param name="bottom">The bottom hint edge.</param>
    /// <param name="top">The top hint edge.</param>
    /// <param name="zones">The alignment zones in design units.</param>
    /// <param name="scale">The pixels per design unit scale.</param>
    /// <param name="fuzz">The band extension in pixels.</param>
    /// <returns><see langword="true"/> if a zone captured the hint; otherwise, <see langword="false"/>.</returns>
    private static bool TryCaptureHint(ref HintEdge bottom, ref HintEdge top, HintZone[] zones, float scale, float fuzz)
    {
        for (int z = 0; z < zones.Length; z++)
        {
            HintZone zone = zones[z];
            float bandBottom = (zone.Bottom * scale) - fuzz;
            float bandTop = (zone.Top * scale) + fuzz;

            float move;
            if (zone.IsBottom && bottom.Flags != HintEdgeFlags.None && bottom.Cs >= bandBottom && bottom.Cs <= bandTop)
            {
                move = MathF.Floor((zone.Flat * scale) + 0.5F) - bottom.Ds;
            }
            else if (!zone.IsBottom && top.Flags != HintEdgeFlags.None && top.Cs >= bandBottom && top.Cs <= bandTop)
            {
                move = MathF.Ceiling((zone.Flat * scale) - GridFitterTuning.AnchorCeilingFuzzPx) - top.Ds;
            }
            else
            {
                continue;
            }

            if (bottom.Flags != HintEdgeFlags.None)
            {
                bottom.Ds += move;
                bottom.Flags |= HintEdgeFlags.Locked;
            }

            if (top.Flags != HintEdgeFlags.None)
            {
                top.Ds += move;
                top.Flags |= HintEdgeFlags.Locked;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Inserts a hint into a map sorted by unfitted coordinate, rejecting any hint that
    /// overlaps an earlier insertion: an edge at the same coordinate, a pair straddling
    /// an existing edge, an insertion between the edges of an existing pair, or fitted
    /// coordinates that would break the map's ordering. When the final map is being
    /// built, unlocked hints are positioned through the initial map: a pair's midpoint
    /// maps and its width is preserved around it, so stems keep their place relative to
    /// the aligned features around them.
    /// </summary>
    /// <param name="map">The map receiving the hint.</param>
    /// <param name="count">The current edge count, updated on insertion.</param>
    /// <param name="bottom">The bottom hint edge, invalid when flagless.</param>
    /// <param name="top">The top hint edge, invalid when flagless.</param>
    /// <param name="isPair">Whether both edges are valid and insert together.</param>
    /// <param name="buildInitial">Whether the initial map is being built, which skips repositioning.</param>
    private void InsertHint(HintEdge[] map, ref int count, HintEdge bottom, HintEdge top, bool isPair, bool buildInitial)
    {
        HintEdge first = bottom;
        HintEdge second = top;
        if (bottom.Flags == HintEdgeFlags.None)
        {
            if (top.Flags == HintEdgeFlags.None)
            {
                return;
            }

            first = top;
            isPair = false;
        }
        else if (top.Flags == HintEdgeFlags.None)
        {
            isPair = false;
        }

        if (isPair && second.Cs < first.Cs)
        {
            return;
        }

        int insertAt = 0;
        while (insertAt < count && map[insertAt].Cs < first.Cs)
        {
            insertAt++;
        }

        if (insertAt < count)
        {
            if (map[insertAt].Cs == first.Cs)
            {
                return;
            }

            if (isPair && map[insertAt].Cs <= second.Cs)
            {
                return;
            }

            if ((map[insertAt].Flags & HintEdgeFlags.PairTop) != 0)
            {
                return;
            }
        }

        if (!buildInitial && this.initialMapValid && (first.Flags & HintEdgeFlags.Locked) == 0)
        {
            int lastIndex = 0;
            if (isPair)
            {
                float midpoint = MapCoordinate(this.initialMap, this.initialMapCount, ref lastIndex, (first.Cs + second.Cs) * 0.5F);
                float half = (second.Ds - first.Ds) * 0.5F;
                first.Ds = midpoint - half;
                second.Ds = midpoint + half;
            }
            else
            {
                first.Ds = MapCoordinate(this.initialMap, this.initialMapCount, ref lastIndex, first.Cs);
            }
        }

        if (insertAt > 0 && first.Ds < map[insertAt - 1].Ds)
        {
            return;
        }

        if (insertAt < count && (isPair ? second.Ds > map[insertAt].Ds : first.Ds > map[insertAt].Ds))
        {
            return;
        }

        int inserted = isPair ? 2 : 1;
        if (count + inserted > MaxHintEdges)
        {
            return;
        }

        for (int i = count - 1; i >= insertAt; i--)
        {
            map[i + inserted] = map[i];
        }

        map[insertAt] = first;
        if (isPair)
        {
            map[insertAt + 1] = second;
        }

        count += inserted;
    }

    /// <summary>
    /// Adjusts the fitted positions of unlocked hints so one edge of each lands on a
    /// pixel boundary, choosing whichever of the four candidate moves is smallest while
    /// keeping at least half a pixel of counter to each neighbor. Pairs move as one so
    /// stem widths survive. Moves blocked by a not yet adjusted neighbor above are saved
    /// and retried top down in a second pass once that neighbor has settled.
    /// </summary>
    /// <param name="map">The map to adjust.</param>
    /// <param name="count">The number of edges in the map.</param>
    /// <param name="pendingMoves">Scratch storage for the second pass.</param>
    /// <param name="pendingMoveCount">The number of saved moves, reset here.</param>
    private static void AdjustHintMap(HintEdge[] map, int count, PendingMove[] pendingMoves, ref int pendingMoveCount)
    {
        pendingMoveCount = 0;

        for (int i = 0; i < count; i++)
        {
            bool isPair = (map[i].Flags & HintEdgeFlags.PairBottom) != 0 && i + 1 < count && (map[i + 1].Flags & HintEdgeFlags.PairTop) != 0;
            int j = isPair ? i + 1 : i;

            float dsLower = map[i].Ds;
            float dsUpper = map[j].Ds;

            if ((map[i].Flags & HintEdgeFlags.Locked) == 0)
            {
                float fracDown = dsLower - MathF.Floor(dsLower);
                float fracUp = dsUpper - MathF.Floor(dsUpper);

                float downMoveDown = -fracDown;
                float upMoveDown = -fracUp;
                float downMoveUp = fracDown == 0F ? 0F : 1F - fracDown;
                float upMoveUp = fracUp == 0F ? 0F : 1F - fracUp;

                float moveUp = MathF.Min(downMoveUp, upMoveUp);
                float moveDown = MathF.Max(downMoveDown, upMoveDown);

                float move = 0F;
                bool saveEdge = false;

                if (j >= count - 1 || map[j + 1].Ds >= dsUpper + moveUp + GridFitterTuning.MinCounterPx)
                {
                    if (i == 0 || map[i - 1].Ds <= dsLower + moveDown - GridFitterTuning.MinCounterPx)
                    {
                        move = -moveDown < moveUp ? moveDown : moveUp;
                    }
                    else
                    {
                        move = moveUp;
                    }
                }
                else
                {
                    if (i == 0 || map[i - 1].Ds <= dsLower + moveDown - GridFitterTuning.MinCounterPx)
                    {
                        move = moveDown;
                        saveEdge = moveUp < -moveDown;
                    }
                    else
                    {
                        saveEdge = true;
                    }
                }

                if (saveEdge && j < count - 1 && (map[j + 1].Flags & HintEdgeFlags.Locked) == 0 && pendingMoveCount < MaxHintEdges)
                {
                    pendingMoves[pendingMoveCount].UpperIndex = j;
                    pendingMoves[pendingMoveCount].MoveUp = moveUp - move;
                    pendingMoveCount++;
                }

                map[i].Ds = dsLower + move;
                if (isPair)
                {
                    map[j].Ds = dsUpper + move;
                }
            }

            if (isPair)
            {
                i++;
            }
        }

        for (int m = pendingMoveCount - 1; m >= 0; m--)
        {
            int j = pendingMoves[m].UpperIndex;
            float moveUp = pendingMoves[m].MoveUp;
            if (map[j + 1].Ds >= map[j].Ds + moveUp + GridFitterTuning.MinCounterPx)
            {
                map[j].Ds += moveUp;
                if ((map[j].Flags & HintEdgeFlags.PairTop) != 0 && j > 0)
                {
                    map[j - 1].Ds += moveUp;
                }
            }
        }
    }

    /// <summary>
    /// Chains successive stem centers when the glyph's counter mask requests counter
    /// equalization: the pitch to the previous stem rounds to whole pixels so equal
    /// design pitches round identically and the stem rhythm survives. A chained move is
    /// skipped when it would close the counter to a neighbor or exceed the movement the
    /// accumulated pitch rounding justifies.
    /// </summary>
    private void EqualizeMapCounters()
    {
        HintEdge[] map = this.finalMap;
        int count = this.mapCount;

        float previousCenterCs = float.MinValue;
        float previousCenterDs = 0F;
        for (int i = 0; i + 1 < count; i++)
        {
            if ((map[i].Flags & HintEdgeFlags.PairBottom) == 0 || (map[i + 1].Flags & HintEdgeFlags.PairTop) == 0)
            {
                continue;
            }

            float centerCs = (map[i].Cs + map[i + 1].Cs) * 0.5F;
            float centerDs = (map[i].Ds + map[i + 1].Ds) * 0.5F;

            if (previousCenterCs > float.MinValue)
            {
                float target = previousCenterDs + MathF.Floor(centerCs - previousCenterCs + 0.5F);
                float delta = target - centerDs;
                if (delta != 0F && MathF.Abs(delta) <= GridFitterTuning.MaxEdgeDeltaPx + 0.5F)
                {
                    bool roomBelow = i == 0 || map[i - 1].Ds <= map[i].Ds + delta - GridFitterTuning.MinCounterPx;
                    bool roomAbove = i + 2 >= count || map[i + 2].Ds >= map[i + 1].Ds + delta + GridFitterTuning.MinCounterPx;
                    if (roomBelow && roomAbove)
                    {
                        map[i].Ds += delta;
                        map[i + 1].Ds += delta;
                        centerDs += delta;
                    }
                }
            }

            previousCenterCs = centerCs;
            previousCenterDs = centerDs;
            i++;
        }
    }

    /// <summary>
    /// Computes the per segment scales of a map after its fitted positions settle, so
    /// coordinates between adjacent edges interpolate linearly between their fitted
    /// positions. The segment above the last edge keeps the nominal scale of one.
    /// </summary>
    /// <param name="map">The map to finalize.</param>
    /// <param name="count">The number of edges in the map.</param>
    private static void ComputeMapScales(HintEdge[] map, int count)
    {
        for (int i = 0; i < count; i++)
        {
            map[i].Scale = 1F;
            if (i + 1 < count && map[i + 1].Cs != map[i].Cs)
            {
                map[i].Scale = (map[i + 1].Ds - map[i].Ds) / (map[i + 1].Cs - map[i].Cs);
            }
        }
    }

    /// <summary>
    /// Transforms one coordinate through a hint map. Coordinates below the first edge
    /// keep the nominal scale anchored to it; all others interpolate from the highest
    /// edge at or below them using that segment's scale.
    /// </summary>
    /// <param name="map">The map to transform through.</param>
    /// <param name="count">The number of edges in the map.</param>
    /// <param name="lastIndex">The segment cache carried between successive lookups.</param>
    /// <param name="value">The unfitted coordinate.</param>
    /// <returns>The fitted coordinate.</returns>
    private static float MapCoordinate(HintEdge[] map, int count, ref int lastIndex, float value)
    {
        if (count == 0)
        {
            return value;
        }

        int i = lastIndex;
        if (i >= count)
        {
            i = count - 1;
        }

        while (i < count - 1 && value >= map[i + 1].Cs)
        {
            i++;
        }

        while (i > 0 && value < map[i].Cs)
        {
            i--;
        }

        lastIndex = i;

        if (i == 0 && value < map[0].Cs)
        {
            return map[0].Ds + (value - map[0].Cs);
        }

        return map[i].Ds + ((value - map[i].Cs) * map[i].Scale);
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
    /// reverted rather than distorted. In rescue mode only strokes thinner than a pixel
    /// are processed; instruction fitted geometry is left exactly where the font put it.
    /// </summary>
    /// <param name="rescueOnly">Whether only sub pixel strokes are processed.</param>
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

    private struct HintEdge
    {
        public float Cs;
        public float Ds;
        public float Scale;
        public HintEdgeFlags Flags;
    }

    private struct PendingMove
    {
        public int UpperIndex;
        public float MoveUp;
    }

    private sealed class PooledObjectPolicy : IPooledObjectPolicy<GlyphGridFitter>
    {
        public GlyphGridFitter Create() => new();

        public bool Return(GlyphGridFitter obj) => true;
    }
}
