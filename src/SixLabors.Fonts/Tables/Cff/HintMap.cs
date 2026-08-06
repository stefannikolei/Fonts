// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;

namespace SixLabors.Fonts.Tables.Cff;

/// <summary>
/// Fits a buffered CFF outline by constructing a monotone map from character-space
/// coordinates to device-space coordinates. Declared stem edges and blue-zone flats
/// become fixed points of that map; coordinates between adjacent fixed points use linear
/// interpolation, while coordinates outside them retain the nominal font transform.
/// </summary>
internal sealed class HintMap
{
    // A Type 2 charstring can address at most 96 stems. Each stem occupies two adjacent
    // map entries, including a ghost whose sole physical edge is duplicated into a pair.
    private const int MaxStems = 96;
    private const int MaxHintEdges = MaxStems * 2;
    private const int MaxGlobalCounters = (MaxStems * (MaxStems - 1)) / 2;

    /// <summary>
    /// The positive device-space difference <c>standard - width</c> for which
    /// <c>UseStdWidth</c> replaces a stem width by the standard width. The exact signed
    /// 16.16 threshold is <c>0x5700 / 2^16 = 0.33984375</c> pixels.
    /// </summary>
    private const float StandardWidthGrowPx = 22272F / 65536F;

    /// <summary>
    /// The positive device-space difference <c>width - standard</c> for which
    /// <c>UseStdWidth</c> replaces a stem width by the standard width. The exact signed
    /// 16.16 threshold is <c>0xAE00 / 2^16 = 0.6796875</c> pixels, twice the growth reach.
    /// </summary>
    private const float StandardWidthShrinkPx = 44544F / 65536F;

    /// <summary>
    /// The device-space reduction below the nearest whole-pixel width. For the granularity-one
    /// device constant, <c>CalcHW2</c> evaluates
    /// <c>(((0x16A09 - 0x10000) &gt;&gt; 2) * 0x4D41) &gt;&gt; 14 = 0x1FFF</c>
    /// in signed 16.16 arithmetic, so the exact reduction is <c>8191 / 2^16</c> pixels.
    /// </summary>
    private const float StemWidthWeightPx = 8191F / 65536F;

    /// <summary>
    /// The width of the transition interval immediately below the reduced whole-pixel width.
    /// If <c>L = round(width) - StemWidthWeightPx</c>, a raw width in
    /// <c>[L - 3/16, L]</c> is preserved instead of being clamped to either boundary.
    /// </summary>
    private const float StemWidthBandPx = 3F / 16F;

    // Every fitter owns fixed-size scratch arrays sized to the Type 2 stem limit. Pooling the
    // object keeps those arrays off the per-glyph allocation path without sharing mutable state
    // between simultaneous fits.
    private static readonly ObjectPool<HintMap> Pool = new(new PooledObjectPolicy());

    private readonly HintEdge[] finalMap = new HintEdge[MaxHintEdges + 2];
    private readonly float[] activeHorizontal = new float[MaxHintEdges];
    private readonly float[] activeVertical = new float[MaxHintEdges];
    private readonly bool[] horizontalCounterFlags = new bool[MaxStems];
    private readonly bool[] verticalCounterFlags = new bool[MaxStems];
    private readonly int[] horizontalCoverageLow = new int[MaxStems];
    private readonly int[] horizontalCoverageHigh = new int[MaxStems];
    private readonly int[] verticalCoverageLow = new int[MaxStems];
    private readonly int[] verticalCoverageHigh = new int[MaxStems];
    private readonly GlobalStem[] globalStems = new GlobalStem[MaxStems];
    private readonly GlobalCounter[] globalCounters = new GlobalCounter[MaxGlobalCounters];
    private readonly int[] sortedStemIndices = new int[MaxStems];
    private readonly int[] retainedCounterIndices = new int[MaxGlobalCounters];
    private readonly int[] pathCounterIndices = new int[MaxStems];
    private readonly DeviceZone[] deviceZones = new DeviceZone[CffBlueZoneTable.MaximumZoneCount];
    private int mapCount;
    private int deviceZoneCount;
    private int globalCounterHead;

    private HintMap()
    {
    }

    /// <summary>
    /// Describes which physical edge a map entry represents and whether blue-zone capture
    /// has already fixed its lock. Pair and ghost values are mutually exclusive structural
    /// roles; <see cref="Locked"/> and <see cref="Synthetic"/> are independent state bits.
    /// </summary>
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
    /// Fits a buffered outline through maps built from the font's declared stems and
    /// alignment zones. Input points are character-space coordinates in an upright,
    /// Y-up system with the baseline at zero; successful output coordinates are device
    /// pixels in the same orientation.
    /// </summary>
    /// <param name="points">The outline points to fit in place, in contour order.</param>
    /// <param name="verbs">The commands describing the packed outline points.</param>
    /// <param name="contourEnds">The index of the last point of each contour.</param>
    /// <param name="verticalStems">The declared vertical stem zones as X edge pairs.</param>
    /// <param name="horizontalStems">The declared horizontal stem zones as Y edge pairs.</param>
    /// <param name="initialStemCount">The number of stems active at the first movement operator.</param>
    /// <param name="hintRegions">The hint mask regions, empty when the glyph declares none.</param>
    /// <param name="counterMasks">The counter mask groups, empty when the glyph declares none.</param>
    /// <param name="options">The fitting parameters.</param>
    /// <returns><see langword="true"/> if any point was moved; otherwise, <see langword="false"/>.</returns>
    public static bool FitInPlace(Vector2[] points, CffOutlineVerb[] verbs, ushort[] contourEnds, float[] verticalStems, float[] horizontalStems, int initialStemCount, CffHintRegion[] hintRegions, CffCounterMask[] counterMasks, in HintMapOptions options)
    {
        if (!options.FitHorizontal && !options.FitVertical)
        {
            return false;
        }

        if (points.Length < 4 || contourEnds.Length == 0)
        {
            return false;
        }

        HintMap fitter = Pool.Get();
        try
        {
            bool moved = false;
            int horizontalCount = horizontalStems.Length >> 1;
            int verticalCount = verticalStems.Length >> 1;

            fitter.deviceZoneCount = 0;
            if (options.FitVertical)
            {
                // Blue zones constrain Y only. Preparing them once converts each design
                // interval and flat edge through the 16.16 Y scale before any point moves.
                float firstStandardHeight = options.StandardHeights.Length > 0 ? options.StandardHeights[0] : 0F;
                fitter.deviceZoneCount = CffBlueZoneTable.Prepare(
                    options.Zones,
                    options.FamilyZones,
                    options.AnchorScale,
                    firstStandardHeight,
                    fitter.deviceZones);
            }

            FillCounterFlags(counterMasks, 0, horizontalCount, fitter.horizontalCounterFlags);
            FillCounterFlags(counterMasks, horizontalCount, verticalCount, fitter.verticalCounterFlags);

            // A charstring names the stems that are live for each run of points through a
            // hintmask. Fitting a run against any other selection puts stems where the font
            // did not ask for them, and crowded glyphs lose their counters. Without a mask
            // the stems declared when the first movement occurs are live for the outline.
            if (hintRegions.Length == 0)
            {
                int initialHorizontal = Math.Min(horizontalCount, initialStemCount);
                int initialVertical = Math.Min(verticalCount, Math.Max(0, initialStemCount - initialHorizontal));
                ReadOnlySpan<float> initialX = options.FitHorizontal ? verticalStems.AsSpan(0, initialVertical * 2) : [];
                ReadOnlySpan<float> initialY = options.FitVertical ? horizontalStems.AsSpan(0, initialHorizontal * 2) : [];

                // Counter eligibility depends on the original segment direction and its
                // orthogonal coverage interval. Scan both axes before either map mutates a
                // coordinate, otherwise the first fitted axis could change the second axis'
                // near-horizontal or near-vertical classification.
                fitter.ScanStemCoverage(points, verbs, initialX, initialY);

                moved |= fitter.FitBufferedAxis(points, initialX, fitter.verticalCounterFlags.AsSpan(0, initialVertical), 0, points.Length, in options, true);
                moved |= fitter.FitBufferedAxis(points, initialY, fitter.horizontalCounterFlags.AsSpan(0, initialHorizontal), 0, points.Length, in options, false);

                return moved;
            }

            if (hintRegions[0].PointStart > 0)
            {
                int initialHorizontal = Math.Min(horizontalCount, initialStemCount);
                int initialVertical = Math.Min(verticalCount, Math.Max(0, initialStemCount - initialHorizontal));
                ReadOnlySpan<float> initialX = options.FitHorizontal ? verticalStems.AsSpan(0, initialVertical * 2) : [];
                ReadOnlySpan<float> initialY = options.FitVertical ? horizontalStems.AsSpan(0, initialHorizontal * 2) : [];

                moved |= fitter.FitBufferedAxis(points, initialX, fitter.verticalCounterFlags.AsSpan(0, initialVertical), 0, hintRegions[0].PointStart, in options, true);
                moved |= fitter.FitBufferedAxis(points, initialY, fitter.horizontalCounterFlags.AsSpan(0, initialHorizontal), 0, hintRegions[0].PointStart, in options, false);
            }

            for (int r = 0; r < hintRegions.Length; r++)
            {
                CffHintRegion region = hintRegions[r];
                int start = region.PointStart;
                int end = r + 1 < hintRegions.Length ? hintRegions[r + 1].PointStart : points.Length;
                if (end <= start)
                {
                    continue;
                }

                int verticalActive = Select(verticalStems, fitter.activeVertical, region.Mask, horizontalCount, region.StemCount);
                int horizontalActive = Select(horizontalStems, fitter.activeHorizontal, region.Mask, 0, region.StemCount);

                ReadOnlySpan<float> axisVertical = options.FitHorizontal ? fitter.activeVertical.AsSpan(0, verticalActive) : [];
                ReadOnlySpan<float> axisHorizontal = options.FitVertical ? fitter.activeHorizontal.AsSpan(0, horizontalActive) : [];

                // Counter constraints are global-colouring metadata. A filtered lock list
                // must not re-index them onto a different set of stems.
                moved |= fitter.FitBufferedAxis(points, axisVertical, [], start, end, in options, true);
                moved |= fitter.FitBufferedAxis(points, axisHorizontal, [], start, end, in options, false);
            }

            return moved;
        }
        finally
        {
            Pool.Return(fitter);
        }
    }

    /// <summary>
    /// Fits one coordinate of a buffered outline through a piecewise-linear map. Stem edges
    /// supply source coordinates <c>c[i]</c> and fitted lock coordinates <c>l[i]</c>;
    /// a point between two edges uses the slope
    /// <c>(l[i + 1] - l[i]) / (c[i + 1] - c[i])</c>, while the exterior intervals use the
    /// nominal character-to-device scale.
    /// </summary>
    /// <param name="points">The outline points, in contour order.</param>
    /// <param name="declaredStems">The declared stem zones for the axis as edge pairs, in declaration order.</param>
    /// <param name="counterFlags">One flag per stem naming the stems whose gaps stay even.</param>
    /// <param name="start">The index of the first point the map applies to.</param>
    /// <param name="end">The index one past the last point the map applies to.</param>
    /// <param name="options">The fitting parameters.</param>
    /// <param name="isXAxis">Whether the horizontal axis is being fitted; otherwise the vertical axis.</param>
    /// <returns><see langword="true"/> if any point was moved on the axis; otherwise, <see langword="false"/>.</returns>
    private bool FitBufferedAxis(Vector2[] points, ReadOnlySpan<float> declaredStems, ReadOnlySpan<bool> counterFlags, int start, int end, in HintMapOptions options, bool isXAxis)
    {
        float axisScale = isXAxis ? options.HorizontalScale : options.AnchorScale;
        if (declaredStems.Length < 2)
        {
            // No fixed points govern this coordinate, so f(c) = c * axisScale for the
            // whole interval. Every coordinate is converted exactly once from character
            // units to pixels, even when no hint changes its relative position.
            ScaleAxis(points, start, end, axisScale, isXAxis);
            return true;
        }

        int pointCount = points.Length;
        for (int i = 0; i < pointCount; i++)
        {
            if (!float.IsFinite(points[i].X) || !float.IsFinite(points[i].Y))
            {
                return false;
            }
        }

        // Each accepted stem contributes two fixed points. Blue-zone stems take their
        // zone row; other stems take a parity-adjusted centre. Interpolating between fixed
        // points moves curve controls continuously instead of collapsing every coordinate
        // in a stem interval onto its nearest edge.
        this.BuildHintMap(declaredStems, counterFlags, in options, isXAxis);

        return this.ApplyHintMap(points, start, end, axisScale, isXAxis);
    }

    /// <summary>
    /// Builds one combined counter graph for the horizontal and vertical stem records,
    /// then emits a separate map for each fitted coordinate. The graph is shared so record
    /// ordering and counter-path selection are resolved once; each emitted map still uses
    /// its own character-to-device scale.
    /// </summary>
    /// <param name="points">The outline points, in contour order.</param>
    /// <param name="verticalStems">The vertical stems governing X coordinates.</param>
    /// <param name="horizontalStems">The horizontal stems governing Y coordinates.</param>
    /// <param name="start">The index of the first point the maps apply to.</param>
    /// <param name="end">The index one past the last point the maps apply to.</param>
    /// <param name="options">The fitting parameters.</param>
    /// <returns><see langword="true"/> if any point was moved; otherwise, <see langword="false"/>.</returns>
    private bool FitBufferedGlobalAxes(Vector2[] points, ReadOnlySpan<float> verticalStems, ReadOnlySpan<float> horizontalStems, int start, int end, in HintMapOptions options)
    {
        int pointCount = points.Length;
        for (int i = 0; i < pointCount; i++)
        {
            if (!float.IsFinite(points[i].X) || !float.IsFinite(points[i].Y))
            {
                return false;
            }
        }

        // Type 2 assigns horizontal stem mask bits before vertical stem bits. Preserve that
        // order in the combined record array so record indices continue to identify the
        // same declarations when counter relationships are built.
        int stemCount = this.BuildCombinedGlobalHintMap(horizontalStems, verticalStems, in options);
        bool moved = false;

        if (options.FitHorizontal)
        {
            this.EmitGlobalHintMap(stemCount, in options, true);
            moved |= this.ApplyHintMap(points, start, end, options.HorizontalScale, true);
        }

        if (options.FitVertical)
        {
            this.EmitGlobalHintMap(stemCount, in options, false);
            moved |= this.ApplyHintMap(points, start, end, options.AnchorScale, false);
        }

        return moved;
    }

    /// <summary>
    /// Applies the current piecewise-linear map to one coordinate of a point range.
    /// </summary>
    /// <param name="points">The outline points.</param>
    /// <param name="start">The index of the first point.</param>
    /// <param name="end">The index one past the last point.</param>
    /// <param name="axisScale">The plain character-to-device scale for the coordinate.</param>
    /// <param name="isXAxis">Whether the X coordinate is being mapped.</param>
    /// <returns><see langword="true"/> if any point was moved; otherwise, <see langword="false"/>.</returns>
    private bool ApplyHintMap(Vector2[] points, int start, int end, float axisScale, bool isXAxis)
    {
        if (this.mapCount == 0)
        {
            ScaleAxis(points, start, end, axisScale, isXAxis);
            return true;
        }

        this.mapCount = ComputeMapScales(this.finalMap, this.mapCount, axisScale);

        bool moved = false;
        int lastIndex = 0;
        for (int i = start; i < end; i++)
        {
            float value = isXAxis ? points[i].X : points[i].Y;
            float mapped = MapCoordinate(this.finalMap, this.mapCount, ref lastIndex, value, axisScale);
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
    /// Converts one axis of a run of points from character space into device space with
    /// the plain scale, for an axis no hint map governs.
    /// </summary>
    /// <param name="points">The outline points.</param>
    /// <param name="start">The index of the first point.</param>
    /// <param name="end">The index one past the last point.</param>
    /// <param name="scale">The pixels per design unit scale.</param>
    /// <param name="isXAxis">Whether the horizontal axis is being converted.</param>
    private static void ScaleAxis(Vector2[] points, int start, int end, float scale, bool isXAxis)
    {
        for (int i = start; i < end; i++)
        {
            if (isXAxis)
            {
                points[i].X *= scale;
            }
            else
            {
                points[i].Y *= scale;
            }
        }
    }

    /// <summary>
    /// Applies the <c>UseStdWidth</c> selection and reach tests to a stem width. Candidate
    /// selection occurs in character space; replacement occurs only when the transformed
    /// candidate is within the asymmetric device-space growth or shrink threshold.
    /// </summary>
    /// <param name="width">The declared width in character space.</param>
    /// <param name="device">The current width in device space.</param>
    /// <param name="standardWidths">The standard widths in design units, in increasing order.</param>
    /// <param name="scale">The pixels per design unit scale.</param>
    /// <returns>The width to draw, in device space.</returns>
    private static float SnapWidth(float width, float device, float[] standardWidths, float scale)
    {
        if (width <= 0F || standardWidths.Length == 0)
        {
            return device;
        }

        // Let L be the last standard below the declared width W and U the first above it.
        // U replaces L only when 2(U - W) < W - L; equality therefore keeps L. This is not
        // ordinary nearest-neighbour selection: the upper candidate must be less than half
        // the distance to W that separates W from the lower candidate.
        float lower = 0F;
        float chosen = 0F;
        bool haveLower = false;
        for (int i = 0; i < standardWidths.Length; i++)
        {
            float candidate = standardWidths[i];
            if (candidate <= 0F)
            {
                continue;
            }

            if (candidate == width)
            {
                return device;
            }

            if (candidate < width)
            {
                lower = candidate;
                chosen = candidate;
                haveLower = true;
                continue;
            }

            if (!haveLower || ((candidate - width) * 2F) < (width - lower))
            {
                chosen = candidate;
            }

            break;
        }

        if (chosen <= 0F)
        {
            return device;
        }

        float standard = chosen * scale;
        if (device > standard)
        {
            return device - standard <= StandardWidthShrinkPx ? standard : device;
        }

        return standard - device <= StandardWidthGrowPx ? standard : device;
    }

    /// <summary>
    /// Computes the full physical stem width corresponding to the half-width returned by
    /// <c>CalcHW2</c>. Let <c>R = round(abs(snapped))</c>,
    /// <c>L = R - 8191/65536</c>, and <c>B = L - 3/16</c>. Widths at or below one pixel use
    /// <c>1 - 8191/65536</c>; wider stems preserve a raw width in <c>[B, L]</c>, clamp a
    /// snapped width above <c>L</c> down to <c>L</c>, and clamp one below <c>B</c> up to
    /// <c>B</c>.
    /// </summary>
    /// <param name="deviceWidth">The declared width in device space.</param>
    /// <param name="snapped">The width after the standard width snap, in device space.</param>
    /// <returns>The width to draw, in device space.</returns>
    private static float FitWidth(float deviceWidth, float snapped)
    {
        float rounded = MathF.Floor(MathF.Abs(snapped) + 0.5F);
        if (rounded <= 1F)
        {
            return 1F - StemWidthWeightPx;
        }

        float low = rounded - StemWidthWeightPx;
        float band = low - StemWidthBandPx;
        float absoluteWidth = MathF.Abs(deviceWidth);
        if (absoluteWidth >= band && absoluteWidth <= low)
        {
            // The raw outline already lies in the transition interval [B, L], so retaining
            // it avoids a discontinuous jump to either boundary as the scale changes.
            return absoluteWidth;
        }

        return snapped > low ? low : MathF.Max(snapped, band);
    }

    /// <summary>
    /// Places a stem centre on an integer or half-integer device coordinate according to
    /// the parity of its rounded 16.16 width. If <c>N = round(width)</c>, an even <c>N</c>
    /// uses <c>round(centre)</c> and an odd <c>N</c> uses
    /// <c>floor(centre) + 1/2</c>. Widths for which <c>N &lt;= floor(width - 1)</c> invert that
    /// parity. A width rounding to zero always uses a half-integer centre.
    /// </summary>
    /// <param name="deviceWidth">The width the stem is drawn at, in device space.</param>
    /// <param name="deviceCentre">The declared centre of the stem, in device space.</param>
    /// <returns>The centre to draw at, in device space.</returns>
    private static int AdjustCentre(int deviceWidth, int deviceCentre)
    {
        int whole = (deviceWidth + 0x8000) >> 16;
        if (whole == 0)
        {
            return (deviceCentre & unchecked((int)0xFFFF0000)) + 0x8000;
        }

        int parity = whole & 1;
        if (whole <= ((deviceWidth - CffFixedPoint.One) >> 16))
        {
            parity ^= 1;
        }

        return parity == 0
            ? (deviceCentre + 0x8000) & unchecked((int)0xFFFF0000)
            : (deviceCentre & unchecked((int)0xFFFF0000)) + 0x8000;
    }

    /// <summary>
    /// Resolves every counter-mask bit onto the corresponding stem index of one axis. The
    /// resulting flag means that the stem's centre displacement is derived from the already
    /// rounded gap to the preceding flagged stem rather than independently rounded.
    /// </summary>
    /// <param name="counterMasks">The declared counter mask groups.</param>
    /// <param name="bitOffset">The bit index of the axis' first stem.</param>
    /// <param name="stemCount">The number of stems on the axis.</param>
    /// <param name="flags">Receives one flag per stem.</param>
    private static void FillCounterFlags(CffCounterMask[] counterMasks, int bitOffset, int stemCount, bool[] flags)
    {
        flags.AsSpan(0, stemCount).Clear();
        for (int m = 0; m < counterMasks.Length; m++)
        {
            CffCounterMask counter = counterMasks[m];
            for (int i = 0; i < stemCount; i++)
            {
                int bit = bitOffset + i;
                if (bit < counter.StemCount && counter.Mask.IsSet(bit))
                {
                    flags[i] = true;
                }
            }
        }
    }

    /// <summary>
    /// Copies the stems a mask selects into a scratch array, in declaration order.
    /// </summary>
    /// <param name="stems">All declared stems of the axis as edge pairs.</param>
    /// <param name="active">Receives the selected pairs.</param>
    /// <param name="mask">The active stem bits, horizontal stems first.</param>
    /// <param name="bitOffset">The bit index of this axis' first stem.</param>
    /// <param name="stemCount">The number of stem bits that existed when the mask was read.</param>
    /// <returns>The number of floats written.</returns>
    private static int Select(float[] stems, float[] active, CffHintMask mask, int bitOffset, int stemCount)
    {
        int count = 0;
        for (int i = 0; i + 1 < stems.Length; i += 2)
        {
            int bit = bitOffset + (i >> 1);
            if (bit < stemCount && mask.IsSet(bit))
            {
                active[count++] = stems[i];
                active[count++] = stems[i + 1];
            }
        }

        return count;
    }

    /// <summary>
    /// Measures the orthogonal design-space coverage associated with every eligible stem
    /// edge. Lines contribute their complete segment; cubics contribute the tangent from
    /// the start point to the first control and the tangent from the second control to the
    /// endpoint. The resulting intervals determine whether two stems overlap long enough
    /// to form a visible counter.
    /// </summary>
    /// <param name="points">The packed design-space outline points.</param>
    /// <param name="verbs">The commands describing the packed points.</param>
    /// <param name="verticalStems">The active X-axis stem pairs.</param>
    /// <param name="horizontalStems">The active Y-axis stem pairs.</param>
    private void ScanStemCoverage(Vector2[] points, CffOutlineVerb[] verbs, ReadOnlySpan<float> verticalStems, ReadOnlySpan<float> horizontalStems)
    {
        int verticalCount = verticalStems.Length >> 1;
        int horizontalCount = horizontalStems.Length >> 1;

        // Empty intervals start as [+16000, -16000] design units in signed 16.16. The
        // first matching segment replaces both sentinels through min/max accumulation.
        this.verticalCoverageLow.AsSpan(0, verticalCount).Fill(0x3E800000);
        this.verticalCoverageHigh.AsSpan(0, verticalCount).Fill(unchecked((int)0xC1800000));
        this.horizontalCoverageLow.AsSpan(0, horizontalCount).Fill(0x3E800000);
        this.horizontalCoverageHigh.AsSpan(0, horizontalCount).Fill(unchecked((int)0xC1800000));

        int pointIndex = 0;
        Vector2 contourStart = default;
        Vector2 current = default;
        bool hasContour = false;
        for (int i = 0; i < verbs.Length; i++)
        {
            switch (verbs[i])
            {
                case CffOutlineVerb.Move:
                    if (hasContour)
                    {
                        this.ScanGlobalColorLine(current, contourStart, verticalStems, horizontalStems);
                    }

                    current = points[pointIndex++];
                    contourStart = current;
                    hasContour = true;
                    break;

                case CffOutlineVerb.Line:
                    Vector2 lineEnd = points[pointIndex++];
                    this.ScanGlobalColorLine(current, lineEnd, verticalStems, horizontalStems);
                    current = lineEnd;
                    break;

                default:
                    Vector2 control1 = points[pointIndex];
                    Vector2 control2 = points[pointIndex + 1];
                    Vector2 curveEnd = points[pointIndex + 2];
                    pointIndex += 3;

                    // Only endpoint tangents describe which hinted edge the curve leaves or
                    // enters. The endpoint chord could cross a stem that neither tangent follows.
                    this.ScanGlobalColorLine(current, control1, verticalStems, horizontalStems);
                    this.ScanGlobalColorLine(control2, curveEnd, verticalStems, horizontalStems);
                    current = curveEnd;
                    break;
            }
        }

        if (hasContour)
        {
            this.ScanGlobalColorLine(current, contourStart, verticalStems, horizontalStems);
        }
    }

    /// <summary>
    /// Classifies one 16.16 segment as near-vertical or near-horizontal and records its
    /// orthogonal coverage against the nearest eligible stem edge.
    /// </summary>
    /// <param name="start">The segment start in design space.</param>
    /// <param name="end">The segment end in design space.</param>
    /// <param name="verticalStems">The active X-axis stem pairs.</param>
    /// <param name="horizontalStems">The active Y-axis stem pairs.</param>
    private void ScanGlobalColorLine(Vector2 start, Vector2 end, ReadOnlySpan<float> verticalStems, ReadOnlySpan<float> horizontalStems)
    {
        int startX = CffFixedPoint.FromSingle(start.X);
        int startY = CffFixedPoint.FromSingle(start.Y);
        int endX = CffFixedPoint.FromSingle(end.X);
        int endY = CffFixedPoint.FromSingle(end.Y);
        int deltaX = unchecked(endX - startX);
        int deltaY = unchecked(endY - startY);
        int absoluteX = Math.Abs(deltaX);
        int absoluteY = Math.Abs(deltaY);

        // A near-vertical segment satisfies |dx| <= 2 and |dy| >= 15 design units. Its
        // X probe is the exact axis coordinate or the truncated midpoint, while its Y
        // endpoints form the coverage interval. Direction chooses the entering stem edge.
        if (absoluteX <= 0x20000 && absoluteY >= 0xF0000)
        {
            int probe = deltaX == 0 ? startX : startX + (deltaX >> 1);
            bool useLowEdge = deltaY < 0;
            int coverageLow = Math.Min(startY, endY);
            int coverageHigh = Math.Max(startY, endY);
            RecordStemCoverage(verticalStems, this.verticalCoverageLow, this.verticalCoverageHigh, probe, coverageLow, coverageHigh, useLowEdge, false);
            return;
        }

        // The horizontal test is the transpose: |dy| <= 2 and |dx| >= 15 design units.
        if (absoluteY <= 0x20000 && absoluteX >= 0xF0000)
        {
            int probe = deltaY == 0 ? startY : startY + (deltaY >> 1);
            bool useLowEdge = deltaX > 0;
            int coverageLow = Math.Min(startX, endX);
            int coverageHigh = Math.Max(startX, endX);
            RecordStemCoverage(horizontalStems, this.horizontalCoverageLow, this.horizontalCoverageHigh, probe, coverageLow, coverageHigh, useLowEdge, true);
        }
    }

    /// <summary>
    /// Updates the coverage interval of the nearest non-ghost stem edge when the absolute
    /// distance from the segment probe to that edge is at most
    /// <c>0x30000 / 2^16 = 3</c> design units.
    /// </summary>
    /// <param name="stems">The stem pairs on the selected axis.</param>
    /// <param name="coverageLows">The retained lower coverage coordinates.</param>
    /// <param name="coverageHighs">The retained upper coverage coordinates.</param>
    /// <param name="probe">The line's near-axis midpoint.</param>
    /// <param name="coverageLow">The segment's lower orthogonal coordinate.</param>
    /// <param name="coverageHigh">The segment's upper orthogonal coordinate.</param>
    /// <param name="useLowEdge">Whether the probe distance is measured to the stem's low edge; otherwise, its high edge.</param>
    /// <param name="recognizeGhosts">Whether Type 2 horizontal ghost widths are present.</param>
    private static void RecordStemCoverage(ReadOnlySpan<float> stems, int[] coverageLows, int[] coverageHighs, int probe, int coverageLow, int coverageHigh, bool useLowEdge, bool recognizeGhosts)
    {
        int nearestIndex = -1;

        // 0x27100000 is 10000 design units in signed 16.16, larger than any accepted
        // three-unit match and therefore only an initial nearest-distance sentinel.
        int nearestDistance = 0x27100000;
        for (int s = 0; s + 1 < stems.Length; s += 2)
        {
            float first = stems[s];
            float second = stems[s + 1];
            float width = second - first;
            if (recognizeGhosts && (MathF.Abs(width + 20F) < 0.5F || MathF.Abs(width + 21F) < 0.5F))
            {
                continue;
            }

            int firstFixed = CffFixedPoint.FromSingle(first);
            int secondFixed = CffFixedPoint.FromSingle(second);
            int low = Math.Min(firstFixed, secondFixed);
            int high = Math.Max(firstFixed, secondFixed);
            int edge = useLowEdge ? low : high;
            int distance = Math.Abs(unchecked(probe - edge));
            if (distance < nearestDistance)
            {
                nearestIndex = s >> 1;
                nearestDistance = distance;
            }
        }

        if (nearestIndex >= 0 && nearestDistance <= 0x30000)
        {
            coverageLows[nearestIndex] = Math.Min(coverageLows[nearestIndex], coverageLow);
            coverageHighs[nearestIndex] = Math.Max(coverageHighs[nearestIndex], coverageHigh);
        }
    }

    /// <summary>
    /// Builds one coordinate map from declared stem pairs. Each pair supplies a source
    /// centre and a fitted centre; insertion preserves increasing order in both spaces,
    /// rejecting a pair whose fitted centre would cross an earlier pair. Y stems may take
    /// a blue-zone centre, counter-linked stems inherit a rounded centre separation, and
    /// all remaining stems use width-parity centre alignment.
    /// </summary>
    /// <param name="declaredStems">The declared stem zones as edge pairs, in declaration order.</param>
    /// <param name="counterFlags">One flag per stem naming the stems whose gaps stay even.</param>
    /// <param name="options">The fitting parameters carrying the alignment zones.</param>
    /// <param name="isXAxis">Whether the horizontal axis is being fitted; zones apply only to the vertical axis.</param>
    private void BuildHintMap(ReadOnlySpan<float> declaredStems, ReadOnlySpan<bool> counterFlags, in HintMapOptions options, bool isXAxis)
    {
        HintEdge[] map = this.finalMap;
        int count = 0;

        float scale = isXAxis ? options.HorizontalScale : options.AnchorScale;
        float fuzz = options.BlueFuzz;
        int scaleFixed = CffFixedPoint.FromSingle(scale);
        int inverseScaleFixed = CffFixedPoint.Divide(CffFixedPoint.One, scaleFixed);

        int lastCounterIndex = -1;
        float lastCounterCentre = 0F;
        float lastCounterDeclared = 0F;

        // Resolve and insert each stem immediately in declaration order. Because insertion
        // rejects source-order or lock-order crossings, reordering the loop would change
        // which of two conflicting stem constraints survives.
        for (int s = 0; s + 1 < declaredStems.Length; s += 2)
        {
            int stemIndex = s >> 1;
            bool counterStem = stemIndex < counterFlags.Length && counterFlags[stemIndex];
            if (!TryInitHintPair(declaredStems[s], declaredStems[s + 1], scale, !isXAxis, out HintEdge bottom, out HintEdge top, out bool isPair))
            {
                continue;
            }

            // Width selection has two consecutive stages: the entry stage applies
            // UseStdWidth to the transformed raw width, and the adjustment stage applies
            // the same asymmetric reach test to that result. Keeping both stages preserves
            // their distinct 16.16 rounding boundaries even when the operation is otherwise
            // numerically idempotent.
            float rawLow = MathF.Min(declaredStems[s], declaredStems[s + 1]);
            float rawHigh = MathF.Max(declaredStems[s], declaredStems[s + 1]);
            float designWidth = rawHigh - rawLow;
            int rawDeviceWidthFixed = CffFixedPoint.Multiply(CffFixedPoint.FromSingle(designWidth), scaleFixed);
            float rawDeviceWidth = CffFixedPoint.ToSingle(rawDeviceWidthFixed);
            float firstSnapped = SnapWidth(designWidth, rawDeviceWidth, isXAxis ? options.StandardWidths : options.StandardHeights, scale);
            float snapped = SnapWidth(designWidth, firstSnapped, isXAxis ? options.StandardWidths : options.StandardHeights, scale);
            float outlineHalf = FitWidth(rawDeviceWidth, snapped) * 0.5F;

            // Convert the full physical device width back through 1/scale before halving.
            // Halving after the fixed multiply matches the signed truncation of GCDoLock;
            // inverse-transforming an already halved float can differ by one 16.16 unit.
            int lockHalf = CffFixedPoint.Multiply(CffFixedPoint.FromSingle(outlineHalf * 2F), inverseScaleFixed) >> 1;

            // A captured stem uses two widths for different purposes. Its centre is offset
            // from the zone row by round(snappedWidth)/2, so the row identifies the intended
            // feature edge. The final outline edges then use CalcHW2's smaller physical
            // half-width around that already-settled centre.
            float capturedCentre = 0F;
            float capturedEdge = 0F;
            bool captured = !isXAxis && TryCaptureHint(
                ref bottom,
                ref top,
                this.deviceZones.AsSpan(0, this.deviceZoneCount),
                scale,
                fuzz,
                options.BlueShift,
                options.BlueScale,
                snapped,
                out capturedCentre,
                out capturedEdge);

            if (isPair)
            {
                int sourceCentre = bottom.FixedCs + ((top.FixedCs - bottom.FixedCs) >> 1);
                int declaredCentre = CffFixedPoint.Multiply(sourceCentre, scaleFixed);
                int centre;
                if (captured)
                {
                    centre = CffFixedPoint.FromSingle(capturedCentre);
                }
                else if (counterStem && lastCounterIndex >= 0)
                {
                    // Let C be this ideal centre and P the preceding linked ideal centre.
                    // The fitted centre is previousFitted + round(C - P), so every linked
                    // gap is an integer number of pixels and independent centre rounding
                    // cannot make adjacent equal gaps differ by one pixel.
                    float declaredCentreValue = CffFixedPoint.ToSingle(declaredCentre);
                    centre = CffFixedPoint.FromSingle(lastCounterCentre + MathF.Floor((declaredCentreValue - lastCounterDeclared) + 0.5F));
                }
                else
                {
                    centre = AdjustCentre(CffFixedPoint.FromSingle(snapped), declaredCentre);
                }

                if (counterStem)
                {
                    lastCounterIndex = stemIndex;
                    lastCounterCentre = CffFixedPoint.ToSingle(centre);
                    lastCounterDeclared = CffFixedPoint.ToSingle(declaredCentre);
                }

                int lockCentre = CffFixedPoint.Multiply(centre, inverseScaleFixed);
                bottom.FixedLock = lockCentre - lockHalf;
                top.FixedLock = lockCentre + lockHalf;
                bottom.Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(bottom.FixedLock, scaleFixed));
                top.Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(top.FixedLock, scaleFixed));
                bottom.Kind = (byte)(captured ? 1 : 0);
                top.Kind = (byte)(captured ? 1 : 0);
            }
            else
            {
                // A ghost has one physical edge. At granularity one its lock is displaced
                // by 4095/65536 pixel from the rounded row before inverse transformation;
                // the sign is negative for a top ghost and positive for a bottom ghost.
                const float ghostHalf = 4095F / 65536F;
                float ghostRow;
                if (top.Flags != HintEdgeFlags.None)
                {
                    ghostRow = captured ? capturedEdge : MathF.Floor(top.Ds + 0.5F);
                    int deviceEdge = CffFixedPoint.FromSingle(ghostRow - ghostHalf);
                    top.FixedLock = CffFixedPoint.Multiply(deviceEdge, inverseScaleFixed);
                    top.Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(top.FixedLock, scaleFixed));
                    top.Kind = (byte)(captured ? 0x11 : 0x10);
                }
                else
                {
                    ghostRow = captured ? capturedEdge : MathF.Floor(bottom.Ds + 0.5F);
                    int deviceEdge = CffFixedPoint.FromSingle(ghostRow + ghostHalf);
                    bottom.FixedLock = CffFixedPoint.Multiply(deviceEdge, inverseScaleFixed);
                    bottom.Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(bottom.FixedLock, scaleFixed));
                    bottom.Kind = (byte)(captured ? 0x11 : 0x10);
                }
            }

            InsertHint(map, ref count, bottom, top, isPair);
        }

        // The unsigned interval test is equivalent to
        // 0x68001 <= LinesPerEm < 0x118000, or approximately 6.500015 through just under
        // 17.5 pixels in 16.16. Within that size range, separate centres that would land
        // closer than 1.5 pixels before any interpolation slope is calculated.
        if (options.LockFixMapOk && unchecked((uint)(options.LinesPerEm - 0x68001)) < 0xAFFFFU)
        {
            FixupMap(map, count, inverseScaleFixed, scaleFixed);
        }

        this.mapCount = count;
    }

    /// <summary>
    /// Constructs the global-colouring state for both axes: initialize stem bands, enforce
    /// non-crossing and blue-zone constraints, enumerate visible counters, then solve the
    /// retained counter paths onto integer-pixel widths.
    /// </summary>
    /// <param name="horizontalStems">The active horizontal stem pairs.</param>
    /// <param name="verticalStems">The active vertical stem pairs.</param>
    /// <param name="options">The fixed transform, standard widths, and blue zones.</param>
    /// <returns>The number of combined global stem records.</returns>
    private int BuildCombinedGlobalHintMap(ReadOnlySpan<float> horizontalStems, ReadOnlySpan<float> verticalStems, in HintMapOptions options)
    {
        int stemCount = 0;
        stemCount = this.InitializeGlobalStems(horizontalStems, stemCount, in options, false);
        stemCount = this.InitializeGlobalStems(verticalStems, stemCount, in options, true);

        this.FixGlobalLocations(stemCount);
        for (int i = 0; i < stemCount; i++)
        {
            ref GlobalStem stem = ref this.globalStems[i];

            // Pairwise reconciliation can translate or resize a coarse band. Counter
            // solving uses its resulting non-negative device width, not the initial width.
            stem.AdjustedWidth = Math.Abs(unchecked(stem.LocationHigh - stem.LocationLow));
        }

        int counterCount = this.BuildGlobalCounters(stemCount);
        this.GlobalColor(stemCount, counterCount, options.ExpansionFactor);

        return stemCount;
    }

    /// <summary>
    /// Appends one axis' stem records to the combined list. Each record stores design edges,
    /// ideal device edges, orthogonal coverage, a twice-standardized device width, and an
    /// initial coarse band. A blue-zone capture supplies a fixed centre; otherwise the
    /// centre follows the rounded-width parity rule.
    /// </summary>
    /// <param name="declaredStems">The active stem pairs for the axis.</param>
    /// <param name="start">The first free combined-record index.</param>
    /// <param name="options">The fixed transform, standard widths, and blue zones.</param>
    /// <param name="isXAxis">Whether these are vertical stems governing X coordinates.</param>
    /// <returns>The first free index after the appended records.</returns>
    private int InitializeGlobalStems(ReadOnlySpan<float> declaredStems, int start, in HintMapOptions options, bool isXAxis)
    {
        int stemCount = declaredStems.Length >> 1;
        int scale = CffFixedPoint.FromSingle(isXAxis ? options.HorizontalScale : options.AnchorScale);
        float scaleValue = CffFixedPoint.ToSingle(scale);
        float[] standardWidths = isXAxis ? options.StandardWidths : options.StandardHeights;
        int[] coverageLows = isXAxis ? this.verticalCoverageLow : this.horizontalCoverageLow;
        int[] coverageHighs = isXAxis ? this.verticalCoverageHigh : this.horizontalCoverageHigh;

        for (int i = 0; i < stemCount; i++)
        {
            float first = declaredStems[i * 2];
            float second = declaredStems[(i * 2) + 1];
            int firstFixed = CffFixedPoint.FromSingle(first);
            int secondFixed = CffFixedPoint.FromSingle(second);
            int designLow = Math.Min(firstFixed, secondFixed);
            int designHigh = Math.Max(firstFixed, secondFixed);
            int idealLow = CffFixedPoint.Multiply(designLow, scale);
            int idealHigh = CffFixedPoint.Multiply(designHigh, scale);
            int rawWidth = unchecked(idealHigh - idealLow);
            float designWidth = CffFixedPoint.ToSingle(unchecked(designHigh - designLow));
            float firstSnapped = SnapWidth(designWidth, CffFixedPoint.ToSingle(rawWidth), standardWidths, scaleValue);
            float secondSnapped = SnapWidth(designWidth, firstSnapped, standardWidths, scaleValue);

            int globalIndex = start + i;
            ref GlobalStem stem = ref this.globalStems[globalIndex];
            stem = default;
            stem.DesignLow = designLow;
            stem.DesignHigh = designHigh;
            stem.IdealLow = idealLow;
            stem.IdealHigh = idealHigh;
            stem.CoverageLow = coverageLows[i];
            stem.CoverageHigh = coverageHighs[i];
            stem.AdjustedWidth = CffFixedPoint.FromSingle(secondSnapped);
            stem.RawWidth = rawWidth;
            stem.Flags = (isXAxis ? 0 : 1) | 4;
            stem.OutgoingCounter = -1;
            stem.PredecessorCounter = -1;

            _ = TryInitHintPair(first, second, scaleValue, false, out HintEdge bottom, out HintEdge top, out _);
            float capturedCentre = 0F;
            bool captured = !isXAxis && TryCaptureHint(
                ref bottom,
                ref top,
                this.deviceZones.AsSpan(0, this.deviceZoneCount),
                scaleValue,
                options.BlueFuzz,
                options.BlueShift,
                options.BlueScale,
                secondSnapped,
                out capturedCentre,
                out _);

            if (captured)
            {
                // The centre displacement is capturedCentre - (idealHigh/2 + idealLow/2).
                // Split halves preserve the signed 16.16 truncation used for the ideal centre.
                FindGlobalLocations(ref stem, CffFixedPoint.FromSingle(capturedCentre) - (idealHigh >> 1) - (idealLow >> 1));
                stem.Flags |= 2;
            }
            else
            {
                CalculateGlobalLocations(ref stem);
            }
        }

        return start + stemCount;
    }

    /// <summary>
    /// Emits the final lock pair for every record on one axis. The coarse band contributes
    /// its centre, while the physical half-width comes from <c>CalcHW2</c>; both values are
    /// inverse-transformed into character space before insertion into the map.
    /// </summary>
    /// <param name="stemCount">The number of combined global stem records.</param>
    /// <param name="options">The fixed transforms for both axes.</param>
    /// <param name="isXAxis">Whether the X-coordinate map is being emitted.</param>
    private void EmitGlobalHintMap(int stemCount, in HintMapOptions options, bool isXAxis)
    {
        int scale = CffFixedPoint.FromSingle(isXAxis ? options.HorizontalScale : options.AnchorScale);
        int inverseScale = CffFixedPoint.Divide(CffFixedPoint.One, scale);
        HintEdge[] map = this.finalMap;
        int mapEdgeCount = 0;
        for (int i = 0; i < stemCount; i++)
        {
            ref GlobalStem stem = ref this.globalStems[i];
            if (((stem.Flags & 1) == 0) != isXAxis)
            {
                continue;
            }

            float low = CffFixedPoint.ToSingle(stem.DesignLow);
            float high = CffFixedPoint.ToSingle(stem.DesignHigh);
            _ = TryInitHintPair(low, high, CffFixedPoint.ToSingle(scale), false, out HintEdge bottom, out HintEdge top, out _);

            // Compute centre = low/2 + high/2 in device space, inverse-transform it once,
            // and add/subtract the inverse-transformed physical half-width. Transforming
            // the two edges independently would round each product separately and can
            // change their distance by one 16.16 unit.
            int centreDevice = (stem.LocationHigh >> 1) + (stem.LocationLow >> 1);
            int centreLock = CffFixedPoint.Multiply(centreDevice, inverseScale);
            int halfLock = CffFixedPoint.Multiply(unchecked(stem.ActualHalfWidth * 2), inverseScale) >> 1;
            bottom.FixedLock = unchecked(centreLock - halfLock);
            top.FixedLock = unchecked(centreLock + halfLock);

            bottom.Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(bottom.FixedLock, scale));
            top.Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(top.FixedLock, scale));
            bottom.Kind = (byte)((stem.Flags & 2) != 0 ? 1 : 0);
            top.Kind = bottom.Kind;
            InsertHint(map, ref mapEdgeCount, bottom, top, true);
        }

        this.mapCount = mapEdgeCount;
    }

    /// <summary>
    /// Centres an unanchored stem according to its rounded-width parity, then derives its
    /// coarse integer-pixel band and its smaller physical outline half-width.
    /// </summary>
    /// <param name="stem">The record to position.</param>
    private static void CalculateGlobalLocations(ref GlobalStem stem)
    {
        int idealCentre = (stem.IdealLow >> 1) + (stem.IdealHigh >> 1);
        int adjustedCentre = AdjustCentre(stem.AdjustedWidth, idealCentre);
        FindGlobalLocations(ref stem, unchecked(adjustedCentre - idealCentre));
    }

    /// <summary>
    /// Converts an adjusted device width and centre displacement into a coarse grid band.
    /// The half-band is quantized to half-pixel steps with a minimum of one half pixel;
    /// low and high edges are then rounded outward to whole-pixel boundaries. The physical
    /// outline width is computed separately and does not alter that coarse band.
    /// </summary>
    /// <param name="stem">The record receiving its coarse locations and physical half-width.</param>
    /// <param name="delta">The displacement from the ideal centre.</param>
    private static void FindGlobalLocations(ref GlobalStem stem, int delta)
    {
        // halfBand = floor((adjustedWidth + 1/2) / 2, to a 1/2-pixel quantum).
        int halfBand = (unchecked(stem.AdjustedWidth + 0x8000) >> 1) & unchecked((int)0xFFFF8000);
        if (halfBand < 0x8000)
        {
            halfBand = 0x8000;
        }

        int centre = (unchecked(stem.IdealHigh + stem.IdealLow) >> 1) + delta;

        // The coarse band encloses at least one whole pixel. Its low edge rounds
        // centre - halfBand upward at the half tie, while its high edge rounds
        // centre + halfBand downward at the half tie and then advances one pixel.
        int low = unchecked(centre - halfBand + 0x8000) & unchecked((int)0xFFFF0000);
        int high = (unchecked(centre + halfBand - 0x8000) & unchecked((int)0xFFFF0000)) + CffFixedPoint.One;
        stem.LocationLow = low;
        stem.LocationHigh = high <= low ? unchecked(low + CffFixedPoint.One) : high;

        float fittedWidth = FitWidth(CffFixedPoint.ToSingle(stem.RawWidth), CffFixedPoint.ToSingle(stem.AdjustedWidth));
        stem.ActualHalfWidth = CffFixedPoint.FromSingle(fittedWidth * 0.5F);
    }

    /// <summary>
    /// Reconciles every coarse band with all other bands on the same axis before counters
    /// are constructed. Declaration order is significant because an already anchored band
    /// supplies bounds that later unanchored bands must respect.
    /// </summary>
    /// <param name="stemCount">The number of live records.</param>
    private void FixGlobalLocations(int stemCount)
    {
        for (int i = 0; i < stemCount; i++)
        {
            this.FixOneGlobalLocation(i, stemCount);
        }
    }

    /// <summary>
    /// Constrains one coarse band against every band on the same axis. Blue-zone bands
    /// provide the nearest lower and upper bounds; nested bands are translated until their
    /// fitted containment matches their ideal containment; disjoint ideal bands are kept
    /// from crossing in fitted space.
    /// </summary>
    /// <param name="targetIndex">The record being constrained.</param>
    /// <param name="stemCount">The number of live records.</param>
    private void FixOneGlobalLocation(int targetIndex, int stemCount)
    {
        ref GlobalStem target = ref this.globalStems[targetIndex];
        bool hasAnchorBounds = false;
        int lowerHigh = int.MinValue;
        int upperHigh = int.MaxValue;
        int lowerLow = int.MinValue;
        int upperLow = int.MaxValue;

        for (int i = 0; i < stemCount; i++)
        {
            if (i == targetIndex)
            {
                continue;
            }

            ref GlobalStem other = ref this.globalStems[i];
            if (((other.Flags ^ target.Flags) & 1) != 0)
            {
                continue;
            }

            if ((other.Flags & 2) != 0)
            {
                // Retain the tightest fitted bound on each side of each target edge. Bounds
                // compare ideal coordinates, so only an anchor on the corresponding source
                // side can constrain that fitted side.
                bool suppliedBound = false;
                if (other.IdealLow <= target.IdealLow && other.LocationLow > lowerLow)
                {
                    lowerLow = other.LocationLow;
                    suppliedBound = true;
                }

                if (other.IdealHigh <= target.IdealHigh && other.LocationHigh > lowerHigh)
                {
                    lowerHigh = other.LocationHigh;
                    suppliedBound = true;
                }

                if (other.IdealLow >= target.IdealLow && other.LocationLow < upperLow)
                {
                    upperLow = other.LocationLow;
                    suppliedBound = true;
                }

                if (other.IdealHigh >= target.IdealHigh && other.LocationHigh < upperHigh)
                {
                    upperHigh = other.LocationHigh;
                    suppliedBound = true;
                }

                hasAnchorBounds |= suppliedBound;
            }

            if ((target.Flags & other.Flags & 2) != 0)
            {
                continue;
            }

            int containedIndex = -1;
            int containerIndex = -1;
            if (other.IdealLow > target.IdealLow)
            {
                if (other.IdealHigh <= target.IdealHigh)
                {
                    containedIndex = i;
                    containerIndex = targetIndex;
                }
            }
            else if (target.IdealHigh <= other.IdealHigh)
            {
                containedIndex = targetIndex;
                containerIndex = i;
            }

            if (containedIndex >= 0)
            {
                // If one ideal interval contains the other, translate the unanchored band
                // just far enough to restore containment while preserving its coarse width.
                ref GlobalStem contained = ref this.globalStems[containedIndex];
                ref GlobalStem container = ref this.globalStems[containerIndex];
                if (contained.LocationLow < container.LocationLow)
                {
                    if ((container.Flags & 2) == 0)
                    {
                        int delta = unchecked(contained.LocationLow - container.LocationLow);
                        container.LocationLow = contained.LocationLow;
                        container.LocationHigh = unchecked(container.LocationHigh + delta);
                    }
                    else
                    {
                        int delta = unchecked(container.LocationLow - contained.LocationLow);
                        contained.LocationLow = container.LocationLow;
                        contained.LocationHigh = unchecked(contained.LocationHigh + delta);
                    }
                }
                else if (contained.LocationHigh > container.LocationHigh)
                {
                    if ((container.Flags & 2) == 0)
                    {
                        int width = unchecked(container.LocationLow - container.LocationHigh);
                        container.LocationHigh = contained.LocationHigh;
                        container.LocationLow = unchecked(width + contained.LocationHigh);
                    }
                    else
                    {
                        int width = unchecked(contained.LocationLow - contained.LocationHigh);
                        contained.LocationHigh = container.LocationHigh;
                        contained.LocationLow = unchecked(width + container.LocationHigh);
                    }
                }

                continue;
            }

            // Disjoint ideal intervals must remain non-crossing after fitting. Move the
            // complete target band by the overlap amount rather than changing its width.
            if (target.IdealHigh <= other.IdealLow && target.LocationHigh > other.LocationLow)
            {
                int delta = unchecked(other.LocationLow - target.LocationHigh);
                target.LocationHigh = other.LocationLow;
                target.LocationLow = unchecked(target.LocationLow + delta);
            }

            if (target.IdealLow <= other.IdealHigh && target.LocationLow > other.LocationHigh)
            {
                int delta = unchecked(other.LocationHigh - target.LocationLow);
                target.LocationLow = other.LocationHigh;
                target.LocationHigh = unchecked(target.LocationHigh + delta);
            }
        }

        if (hasAnchorBounds)
        {
            // Apply all blue-zone constraints simultaneously after pairwise reconciliation.
            target.LocationLow = Math.Clamp(target.LocationLow, lowerLow, upperLow);
            target.LocationHigh = Math.Clamp(target.LocationHigh, lowerHigh, upperHigh);
        }
    }

    /// <summary>
    /// Enumerates candidate counters between every pair of non-overlapping stems on the
    /// same axis. A pair survives only when their orthogonal coverage intersects and the
    /// intersection covers at least half of the shorter stem. The counter width is the
    /// ideal-space gap from the lower stem's high edge to the upper stem's low edge.
    /// </summary>
    /// <param name="stemCount">The number of live records.</param>
    /// <returns>The number of counters entered.</returns>
    private int BuildGlobalCounters(int stemCount)
    {
        int count = 0;
        this.globalCounterHead = -1;
        for (int i = 0; i < stemCount; i++)
        {
            ref GlobalStem first = ref this.globalStems[i];
            for (int j = i + 1; j < stemCount; j++)
            {
                ref GlobalStem second = ref this.globalStems[j];
                if (((first.Flags ^ second.Flags) & 1) != 0)
                {
                    continue;
                }

                int upperIndex;
                int lowerIndex;
                if (first.DesignHigh < second.DesignLow)
                {
                    upperIndex = j;
                    lowerIndex = i;
                }
                else if (second.DesignHigh < first.DesignLow)
                {
                    upperIndex = i;
                    lowerIndex = j;
                }
                else
                {
                    continue;
                }

                ref GlobalStem upper = ref this.globalStems[upperIndex];
                ref GlobalStem lower = ref this.globalStems[lowerIndex];
                int overlapLow = Math.Max(upper.CoverageLow, lower.CoverageLow);
                int overlapHigh = Math.Min(upper.CoverageHigh, lower.CoverageHigh);
                int overlap = unchecked(overlapHigh - overlapLow);
                if (overlap <= 0)
                {
                    continue;
                }

                int upperCoverage = unchecked(upper.CoverageHigh - upper.CoverageLow);
                int lowerCoverage = unchecked(lower.CoverageHigh - lower.CoverageLow);

                // overlap >= min(upperCoverage, lowerCoverage) / 2.
                if (Math.Min(upperCoverage, lowerCoverage) > overlap * 2)
                {
                    continue;
                }

                ref GlobalCounter counter = ref this.globalCounters[count];
                counter = default;
                counter.Width = unchecked(upper.IdealLow - lower.IdealHigh);
                counter.UpperStem = upperIndex;
                counter.LowerStem = lowerIndex;
                counter.NextGlobal = this.globalCounterHead;
                counter.NextOutgoing = -1;
                this.globalCounterHead = count;
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Solves the global counter constraints. Stems are partitioned by axis and sorted by
    /// design low edge; counters occluded by intervening coverage are removed; the deepest
    /// retained counter chains are quantized together; records outside a chain are aligned
    /// independently without violating already fixed bands.
    /// </summary>
    /// <param name="stemCount">The number of live records.</param>
    /// <param name="counterCount">The number of entered global counters.</param>
    /// <param name="expansionFactor">The counter expansion factor in signed 16.16 form.</param>
    private void GlobalColor(int stemCount, int counterCount, int expansionFactor)
    {
        // Partition X-governing stems before Y-governing stems, then order each partition
        // independently by design low edge. Counter edges can then be traversed as an
        // acyclic lower-to-upper graph within each contiguous partition.
        int verticalCount = 0;
        for (int i = 0; i < stemCount; i++)
        {
            if ((this.globalStems[i].Flags & 1) == 0)
            {
                this.sortedStemIndices[verticalCount++] = i;
            }
        }

        int sortedCount = verticalCount;
        for (int i = 0; i < stemCount; i++)
        {
            if ((this.globalStems[i].Flags & 1) != 0)
            {
                this.sortedStemIndices[sortedCount++] = i;
            }
        }

        for (int partition = 0; partition < 2; partition++)
        {
            int start = partition == 0 ? 0 : verticalCount;
            int end = partition == 0 ? verticalCount : sortedCount;
            for (int i = start; i < end - 1; i++)
            {
                int selected = i;
                int selectedLow = this.globalStems[this.sortedStemIndices[i]].DesignLow;
                for (int j = i + 1; j < end; j++)
                {
                    int candidateLow = this.globalStems[this.sortedStemIndices[j]].DesignLow;
                    if (candidateLow < selectedLow)
                    {
                        selected = j;
                        selectedLow = candidateLow;
                    }
                }

                if (selected != i)
                {
                    (this.sortedStemIndices[i], this.sortedStemIndices[selected]) = (this.sortedStemIndices[selected], this.sortedStemIndices[i]);
                }
            }
        }

        for (int i = 0; i < stemCount; i++)
        {
            int stemIndex = this.sortedStemIndices[i];
            this.globalStems[stemIndex].SortedIndex = (short)i;
            this.globalStems[stemIndex].Flags |= 0x10;
        }

        int retainedCount = 0;
        int counterIndex = this.globalCounterHead;
        for (int visited = 0; visited < counterCount && counterIndex >= 0; visited++)
        {
            if (this.IsSimpleCounter(counterIndex))
            {
                this.retainedCounterIndices[retainedCount++] = counterIndex;
            }

            counterIndex = this.globalCounters[counterIndex].NextGlobal;
        }

        for (int i = 0; i < stemCount; i++)
        {
            this.globalStems[i].OutgoingCounter = -1;
        }

        for (int i = 0; i < retainedCount; i++)
        {
            int retainedIndex = this.retainedCounterIndices[i];
            ref GlobalCounter counter = ref this.globalCounters[retainedIndex];
            ref GlobalStem lower = ref this.globalStems[counter.LowerStem];
            counter.NextOutgoing = lower.OutgoingCounter;
            lower.OutgoingCounter = retainedIndex;
        }

        this.FixGlobalBands(stemCount, retainedCount, expansionFactor);

        this.AlignIsolatedGlobalStems(stemCount);
    }

    /// <summary>
    /// Repeatedly selects the longest lower-to-upper counter chain, extends its upper end
    /// through the narrowest available counters, and quantizes the complete chain. Fixed
    /// stems break predecessor chains, so each iteration removes at least one unresolved
    /// path from the acyclic graph.
    /// </summary>
    /// <param name="stemCount">The number of sorted records.</param>
    /// <param name="retainedCount">The number of counters available to the path pass.</param>
    /// <param name="expansionFactor">The counter expansion factor in signed 16.16 form.</param>
    private void FixGlobalBands(int stemCount, int retainedCount, int expansionFactor)
    {
        int deepestStem = -1;
        int deepest = 0;
        for (int i = 0; i < stemCount; i++)
        {
            ref GlobalStem stem = ref this.globalStems[this.sortedStemIndices[i]];
            stem.Depth = 0;
            stem.PredecessorCounter = -1;
        }

        for (int i = 0; i < stemCount; i++)
        {
            int lowerIndex = this.sortedStemIndices[i];
            ref GlobalStem lower = ref this.globalStems[lowerIndex];
            short nextDepth = (short)(lower.Depth + 1);
            for (int counterIndex = lower.OutgoingCounter; counterIndex >= 0; counterIndex = this.globalCounters[counterIndex].NextOutgoing)
            {
                ref GlobalCounter counter = ref this.globalCounters[counterIndex];
                ref GlobalStem upper = ref this.globalStems[counter.UpperStem];
                if ((upper.Flags & 2) != 0)
                {
                    continue;
                }

                bool replace = upper.Depth < nextDepth;
                if (!replace && upper.Depth == nextDepth)
                {
                    // Equal-depth predecessors prefer the larger shared coverage interval;
                    // an exact tie prefers the narrower counter because it is more likely
                    // to disappear when widths are quantized independently.
                    int candidateOverlap = Math.Min(upper.CoverageHigh, lower.CoverageHigh)
                        - Math.Max(upper.CoverageLow, lower.CoverageLow);
                    ref GlobalCounter previous = ref this.globalCounters[upper.PredecessorCounter];
                    ref GlobalStem previousLower = ref this.globalStems[previous.LowerStem];
                    int previousOverlap = Math.Min(upper.CoverageHigh, previousLower.CoverageHigh)
                        - Math.Max(upper.CoverageLow, previousLower.CoverageLow);
                    replace = previousOverlap < candidateOverlap
                        || (previousOverlap == candidateOverlap && counter.Width < previous.Width);
                }

                if (replace)
                {
                    upper.Depth = nextDepth;
                    upper.PredecessorCounter = counterIndex;
                    if (deepest < nextDepth)
                    {
                        deepest = nextDepth;
                        deepestStem = counter.UpperStem;
                    }
                }
            }
        }

        while (deepestStem >= 0)
        {
            int top = this.ExtendGlobalPathToAnchor(deepestStem);
            if (!this.FixOneGlobalPath(top, retainedCount, expansionFactor))
            {
                return;
            }

            deepestStem = -1;
            deepest = 0;
            for (int i = 0; i < stemCount; i++)
            {
                int stemIndex = this.sortedStemIndices[i];
                ref GlobalStem stem = ref this.globalStems[stemIndex];
                if ((stem.Flags & 2) == 0 && stem.PredecessorCounter >= 0)
                {
                    ref GlobalCounter predecessor = ref this.globalCounters[stem.PredecessorCounter];
                    stem.Depth = (short)(this.globalStems[predecessor.LowerStem].Depth + 1);
                    if (deepest < stem.Depth)
                    {
                        deepest = stem.Depth;
                        deepestStem = stemIndex;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Extends the upper end of a selected path by repeatedly taking its narrowest outgoing
    /// counter. Narrow counters are the most sensitive to losing a pixel, so they become
    /// part of the jointly quantized path before wider alternatives.
    /// </summary>
    /// <param name="stemIndex">The current upper end of the path.</param>
    /// <returns>The final upper record.</returns>
    private int ExtendGlobalPathToAnchor(int stemIndex)
    {
        while (this.globalStems[stemIndex].OutgoingCounter >= 0)
        {
            int chosen = -1;
            int chosenWidth = 0x27100000;
            for (int counterIndex = this.globalStems[stemIndex].OutgoingCounter; counterIndex >= 0; counterIndex = this.globalCounters[counterIndex].NextOutgoing)
            {
                int width = this.globalCounters[counterIndex].Width;
                if (width < chosenWidth)
                {
                    chosen = counterIndex;
                    chosenWidth = width;
                }
            }

            if (chosen < 0)
            {
                break;
            }

            int upper = this.globalCounters[chosen].UpperStem;
            this.globalStems[upper].PredecessorCounter = chosen;
            stemIndex = upper;
        }

        return stemIndex;
    }

    /// <summary>
    /// Quantizes one counter chain into whole-pixel stem and counter widths while preserving
    /// its available endpoint span. Similar fractional counter widths are grouped before
    /// rounding, then the expansion factor chooses which complete groups receive the extra
    /// pixel at the floor/ceiling split.
    /// </summary>
    /// <param name="topStemIndex">The upper record at the start of the predecessor walk.</param>
    /// <param name="retainedCount">The maximum predecessor counters available to the path scratch.</param>
    /// <param name="expansionFactor">The counter expansion factor in signed 16.16 form.</param>
    /// <returns><see langword="true"/> when the path was fixed.</returns>
    private bool FixOneGlobalPath(int topStemIndex, int retainedCount, int expansionFactor)
    {
        ref GlobalStem top = ref this.globalStems[topStemIndex];
        if (top.PredecessorCounter < 0)
        {
            return false;
        }

        int pathCount = 0;
        int stemIntegerSum = 0;
        int currentIndex = topStemIndex;

        // Walk predecessor counters from top to bottom. stemIntegerSum accumulates the
        // integer part floor(adjustedWidth) for every stem in the eventual path span.
        while (this.globalStems[currentIndex].PredecessorCounter >= 0
            && (currentIndex == topStemIndex || (this.globalStems[currentIndex].Flags & 2) == 0))
        {
            stemIntegerSum += unchecked((short)(this.globalStems[currentIndex].AdjustedWidth >> 16));
            if (pathCount == retainedCount || pathCount == this.pathCounterIndices.Length)
            {
                return false;
            }

            int counterIndex = this.globalStems[currentIndex].PredecessorCounter;
            this.pathCounterIndices[pathCount++] = counterIndex;
            currentIndex = this.globalCounters[counterIndex].LowerStem;
        }

        int bottomStemIndex = currentIndex;

        if (pathCount > 2)
        {
            // A chain of at least three counters has enough context to be solved globally;
            // its member stems no longer participate in the isolated-stem pass.
            for (int i = 0; i < pathCount; i++)
            {
                ref GlobalCounter counter = ref this.globalCounters[this.pathCounterIndices[i]];
                this.globalStems[counter.UpperStem].Flags &= ~0x10;
                this.globalStems[counter.LowerStem].Flags &= ~0x10;
            }
        }

        stemIntegerSum += unchecked((short)(this.globalStems[bottomStemIndex].AdjustedWidth >> 16));
        int idealSpan = Math.Abs(unchecked(top.IdealHigh - top.IdealLow));

        // The grouping tolerance is min(12 * idealSpan / designSpan, 0x999A/2^16).
        // idealSpan/designSpan is the device scale, and the cap is approximately 0.6 pixel.
        int clump = CffFixedPoint.Divide(unchecked(idealSpan * 12), unchecked(top.DesignHigh - top.DesignLow));
        clump = Math.Min(clump, 0x999A);
        this.ClumpGlobalCounters(pathCount, clump);
        this.SortGlobalCounterGroups(pathCount);

        int counterIntegerSum = 0;
        for (int i = 0; i < pathCount; i++)
        {
            counterIntegerSum += unchecked((short)(this.globalCounters[this.pathCounterIndices[i]].WorkingWidth >> 16));
        }

        ref GlobalStem bottom = ref this.globalStems[bottomStemIndex];
        int bottomPosition = (bottom.Flags & 2) != 0 ? bottom.LocationLow : bottom.IdealLow;
        int topPosition = (top.Flags & 2) != 0 ? top.LocationHigh : top.IdealHigh;
        int available = Math.Abs(unchecked(topPosition - bottomPosition));

        // With P counters there are P + 1 stems. The residual number of whole pixels is
        // sum(floor(counterWidth)) - round(available) + sum(floor(stemWidth)) + P.
        // Zero means flooring every counter exactly fills the endpoint span.
        short remaining = (short)(counterIntegerSum - ((available + 0x8000) >> 16) + stemIntegerSum + pathCount);

        while (remaining < 0)
        {
            // Flooring leaves unused pixels. Increase every counter by one pixel until the
            // sum of their integer parts has absorbed the deficit.
            int previous = counterIntegerSum;
            counterIntegerSum = 0;
            for (int i = 0; i < pathCount; i++)
            {
                ref GlobalCounter counter = ref this.globalCounters[this.pathCounterIndices[i]];
                counter.WorkingWidth = unchecked(counter.WorkingWidth + CffFixedPoint.One);
                counterIntegerSum += unchecked((short)(counter.WorkingWidth >> 16));
            }

            remaining = (short)(remaining + counterIntegerSum - previous);
        }

        while (remaining > pathCount)
        {
            // More than one extra pixel per counter cannot be represented by a single
            // floor/ceiling split, so first remove one whole pixel from every counter.
            for (int i = 0; i < pathCount; i++)
            {
                ref GlobalCounter counter = ref this.globalCounters[this.pathCounterIndices[i]];
                counter.WorkingWidth = unchecked(counter.WorkingWidth - CffFixedPoint.One);
            }

            remaining = (short)(remaining - pathCount);
        }

        int anchorFactor = (bottom.Flags & 2) != 0 ? 0x8000 : CffFixedPoint.One;
        if ((top.Flags & 2) != 0)
        {
            anchorFactor -= 0x8000;
        }

        // anchorFactor is 1 for no anchored endpoint, 1/2 for one, and 0 for both. The
        // rounded product available * anchorFactor * expansionFactor limits how many
        // fractional rounding slots may move away from the anchored geometry.
        int fractionalSlots = (CffFixedPoint.Multiply(CffFixedPoint.Multiply(available, anchorFactor), expansionFactor) + 0x8000) >> 16;
        int split = remaining;
        if (fractionalSlots != 0 && remaining > 0)
        {
            byte lastGroup = this.globalCounters[this.pathCounterIndices[remaining - 1]].GroupEnd;
            if (lastGroup != remaining - 1)
            {
                int groupStart = 0;
                while (this.globalCounters[this.pathCounterIndices[groupStart]].GroupEnd < lastGroup)
                {
                    groupStart++;
                }

                // Never split a clumped group unless the available fractional slots reach
                // the complete group; equal-width counters must round together.
                split = fractionalSlots < remaining - groupStart
                    ? ((lastGroup - remaining) < fractionalSlots ? lastGroup + 1 : remaining)
                    : groupStart;
            }
        }

        counterIntegerSum = 0;
        for (int i = 0; i < pathCount; i++)
        {
            ref GlobalCounter counter = ref this.globalCounters[this.pathCounterIndices[i]];

            // Counters before split take floor(width); the rest take ceil(width).
            counter.WorkingWidth = i < split
                ? counter.WorkingWidth & unchecked((int)0xFFFF0000)
                : (counter.WorkingWidth & unchecked((int)0xFFFF0000)) + CffFixedPoint.One;
            counterIntegerSum += unchecked((short)(counter.WorkingWidth >> 16));
        }

        int total = unchecked((stemIntegerSum + counterIntegerSum) * CffFixedPoint.One);
        int excess = unchecked(total - available);
        if ((top.Flags & 2) == 0)
        {
            // With no top anchor, place the solved total symmetrically by rounding half of
            // excess at the top edge. A bottom anchor instead fixes the path exactly total
            // pixels above or below that anchor.
            int coarseWidth = Math.Abs(unchecked(top.LocationHigh - top.LocationLow));
            int high;
            if (top.IdealLow < top.IdealHigh)
            {
                high = (bottom.Flags & 2) == 0
                    ? unchecked((excess / 2) + top.IdealHigh + 0x8000) & unchecked((int)0xFFFF0000)
                    : unchecked(bottom.LocationLow + total);
                top.LocationLow = unchecked(high - coarseWidth);
            }
            else
            {
                high = (bottom.Flags & 2) == 0
                    ? unchecked(top.IdealHigh - (excess / 2) + 0x7FFF) & unchecked((int)0xFFFF0000)
                    : unchecked(bottom.LocationLow - total);
                top.LocationLow = unchecked(high + coarseWidth);
            }

            top.LocationHigh = high;
            top.Flags |= 2;
            top.Depth = 0;
        }

        int direction = top.IdealLow < top.IdealHigh ? -1 : 1;
        currentIndex = topStemIndex;
        while (this.globalStems[currentIndex].PredecessorCounter >= 0)
        {
            // Walk downward from the fixed top band. Each next high edge is the current low
            // edge plus the signed, quantized counter width; the stem's coarse width remains
            // unchanged around that new edge.
            int counterIndex = this.globalStems[currentIndex].PredecessorCounter;
            int nextEdge = unchecked(this.globalStems[currentIndex].LocationLow + (direction * this.globalCounters[counterIndex].WorkingWidth));
            int nextStemIndex = this.globalCounters[counterIndex].LowerStem;
            ref GlobalStem next = ref this.globalStems[nextStemIndex];
            if ((next.Flags & 2) != 0)
            {
                break;
            }

            int coarseWidth = Math.Abs(unchecked(next.LocationHigh - next.LocationLow));
            next.LocationHigh = nextEdge;
            next.LocationLow = top.IdealLow < top.IdealHigh
                ? unchecked(nextEdge - coarseWidth)
                : unchecked(nextEdge + coarseWidth);
            next.Flags |= 2;
            next.Depth = 0;
            currentIndex = nextStemIndex;
        }

        return true;
    }

    /// <summary>
    /// Groups adjacent counter widths whose accumulated ranges overlap or are separated by
    /// no more than the scale-derived threshold. A group's admissible interval is
    /// <c>[max(member lows), min(member highs)]</c>; every member receives the arithmetic
    /// midpoint of that interval before integer rounding.
    /// </summary>
    /// <param name="pathCount">The number of counters in the current path.</param>
    /// <param name="threshold">The maximum 16.16 gap between two accumulated width intervals.</param>
    private void ClumpGlobalCounters(int pathCount, int threshold)
    {
        for (int i = 0; i < pathCount; i++)
        {
            ref GlobalCounter counter = ref this.globalCounters[this.pathCounterIndices[i]];
            counter.GroupEnd = (byte)i;
            counter.WorkingWidth = counter.Width;
            counter.GroupLow = counter.Width;
            counter.GroupHigh = counter.Width;
            counter.Joined = false;
        }

        while (true)
        {
            int candidate = -1;
            int closest = 0;
            for (int i = 1; i < pathCount; i++)
            {
                ref GlobalCounter current = ref this.globalCounters[this.pathCounterIndices[i]];
                if (current.Joined)
                {
                    continue;
                }

                int distance = Math.Abs(unchecked(current.Width - this.globalCounters[this.pathCounterIndices[i - 1]].Width));
                if (candidate < 0 || distance < closest)
                {
                    candidate = i;
                    closest = distance;
                }
            }

            if (candidate < 0)
            {
                break;
            }

            ref GlobalCounter right = ref this.globalCounters[this.pathCounterIndices[candidate]];
            right.Joined = true;
            ref GlobalCounter left = ref this.globalCounters[this.pathCounterIndices[candidate - 1]];
            int groupLow = Math.Max(left.GroupLow, right.GroupLow);
            int groupHigh = Math.Min(left.GroupHigh, right.GroupHigh);
            if (groupLow - groupHigh > threshold)
            {
                continue;
            }

            int start = candidate - 1;
            while (start > 0 && this.globalCounters[this.pathCounterIndices[start - 1]].GroupEnd == candidate - 1)
            {
                start--;
            }

            int end = right.GroupEnd;
            for (int i = start; i < candidate; i++)
            {
                this.globalCounters[this.pathCounterIndices[i]].GroupEnd = (byte)end;
            }

            for (int i = start; i <= end; i++)
            {
                ref GlobalCounter member = ref this.globalCounters[this.pathCounterIndices[i]];
                member.GroupLow = groupLow;
                member.GroupHigh = groupHigh;
            }
        }

        int index = 0;
        while (index < pathCount)
        {
            ref GlobalCounter counter = ref this.globalCounters[this.pathCounterIndices[index]];
            int end = counter.GroupEnd;
            if (index < end)
            {
                int midpoint = (counter.GroupHigh >> 1) + (counter.GroupLow >> 1);
                for (int i = index; i <= end; i++)
                {
                    this.globalCounters[this.pathCounterIndices[i]].WorkingWidth = midpoint;
                }
            }

            index = end + 1;
        }
    }

    /// <summary>
    /// Orders counter groups by the fractional part of their working width, then rewrites
    /// each member's group-end index for the new order. Integer floor/ceiling assignment can
    /// therefore use one split without dividing a group.
    /// </summary>
    /// <param name="pathCount">The number of path counters to sort.</param>
    private void SortGlobalCounterGroups(int pathCount)
    {
        for (int i = 0; i < pathCount - 1; i++)
        {
            int selected = i;
            for (int j = i + 1; j < pathCount; j++)
            {
                if (this.IsGlobalCounterGreater(this.pathCounterIndices[selected], this.pathCounterIndices[j]))
                {
                    selected = j;
                }
            }

            if (selected != i)
            {
                (this.pathCounterIndices[i], this.pathCounterIndices[selected]) = (this.pathCounterIndices[selected], this.pathCounterIndices[i]);
            }
        }

        int start = 0;
        while (start < pathCount)
        {
            byte group = this.globalCounters[this.pathCounterIndices[start]].GroupEnd;
            int end = start;
            while (end + 1 < pathCount && this.globalCounters[this.pathCounterIndices[end + 1]].GroupEnd == group)
            {
                end++;
            }

            for (int i = start; i <= end; i++)
            {
                this.globalCounters[this.pathCounterIndices[i]].GroupEnd = (byte)end;
            }

            start = end + 1;
        }
    }

    /// <summary>
    /// Compares two counters for fractional-width ordering. Subpixel widths in
    /// <c>[1/2, 1)</c> sort after other widths; otherwise the larger 16-bit fraction sorts
    /// later. Equal fractions use the lower stem's design high edge, with opposite spatial
    /// direction for the two axes.
    /// </summary>
    /// <param name="leftIndex">The first counter.</param>
    /// <param name="rightIndex">The second counter.</param>
    /// <returns><see langword="true"/> when the left counter sorts after the right.</returns>
    private bool IsGlobalCounterGreater(int leftIndex, int rightIndex)
    {
        ref GlobalCounter left = ref this.globalCounters[leftIndex];
        ref GlobalCounter right = ref this.globalCounters[rightIndex];
        if (left.GroupEnd != right.GroupEnd)
        {
            int leftInteger = left.WorkingWidth & unchecked((int)0xFFFF0000);
            int rightInteger = right.WorkingWidth & unchecked((int)0xFFFF0000);
            int leftFraction = unchecked(left.WorkingWidth - leftInteger);
            int rightFraction = unchecked(right.WorkingWidth - rightInteger);
            bool leftHalfBelowOne = leftInteger == 0 && leftFraction >= 0x8000;
            bool rightHalfBelowOne = rightInteger == 0 && rightFraction >= 0x8000;
            if (leftHalfBelowOne != rightHalfBelowOne)
            {
                return leftHalfBelowOne;
            }

            if (rightFraction < leftFraction)
            {
                return true;
            }

            if (leftFraction < rightFraction)
            {
                return false;
            }
        }

        ref GlobalStem leftLower = ref this.globalStems[left.LowerStem];
        ref GlobalStem rightLower = ref this.globalStems[right.LowerStem];
        return (leftLower.Flags & 1) == 0
            ? rightLower.DesignHigh < leftLower.DesignHigh
            : leftLower.DesignHigh < rightLower.DesignHigh;
    }

    /// <summary>
    /// Aligns stems that were not consumed by a sufficiently long global counter path.
    /// Each stem first takes its independent coarse band, then is translated or clamped so
    /// equal, nested, and ordered design edges remain equal, nested, and ordered in fitted
    /// device space.
    /// </summary>
    /// <param name="stemCount">The number of sorted records.</param>
    private void AlignIsolatedGlobalStems(int stemCount)
    {
        for (int i = 0; i < stemCount; i++)
        {
            int stemIndex = this.sortedStemIndices[i];
            ref GlobalStem stem = ref this.globalStems[stemIndex];
            if ((stem.Flags & 0x10) == 0)
            {
                continue;
            }

            stem.OutgoingCounter = -1;
            stem.PredecessorCounter = -1;
            CalculateGlobalLocations(ref stem);
            this.FixOneGlobalLocation(stemIndex, stemCount);

            bool lowMatched = false;
            bool highMatched = false;
            for (int otherIndex = 0; otherIndex < stemCount; otherIndex++)
            {
                if (otherIndex == stemIndex)
                {
                    continue;
                }

                ref GlobalStem other = ref this.globalStems[otherIndex];
                if (((other.Flags ^ stem.Flags) & 1) != 0)
                {
                    continue;
                }

                bool nested = stem.IdealLow == other.IdealLow
                    || (other.IdealLow < stem.IdealLow && stem.IdealHigh <= other.IdealHigh)
                    || (stem.IdealLow < other.IdealLow && other.IdealHigh <= stem.IdealHigh);
                if (nested)
                {
                    // For nested bands, let S be the difference between their fitted widths
                    // and D the low-edge separation. Compute q = round((D / S) * 2^30),
                    // saturate q to a signed 2.30 integer, then recover
                    // offset = round(S * q / 2^30). Finally round offset to a whole pixel.
                    // This retains the original proportional low-edge position while keeping
                    // the final band on the grid.
                    int relativeSpan = unchecked(
                        stem.LocationHigh
                        - stem.LocationLow
                        - other.LocationHigh
                        + other.LocationLow);
                    int lowDelta = unchecked(other.LocationLow - stem.LocationLow);
                    int ratio;
                    if (relativeSpan == 0)
                    {
                        ratio = lowDelta < 0 ? int.MinValue : int.MaxValue;
                    }
                    else
                    {
                        double ratioValue = ((double)lowDelta / relativeSpan) * 1073741824D;
                        double roundedRatio = ratioValue < 0D ? ratioValue - 0.5D : ratioValue + 0.5D;
                        ratio = roundedRatio >= int.MaxValue
                            ? int.MaxValue
                            : roundedRatio <= int.MinValue
                                ? int.MinValue
                                : (int)roundedRatio;
                    }

                    double offsetValue = (double)relativeSpan * ratio * 9.313225746154785E-10D;
                    double roundedOffsetValue = offsetValue < 0D ? offsetValue - 0.5D : offsetValue + 0.5D;
                    int offset = roundedOffsetValue >= int.MaxValue
                        ? int.MaxValue
                        : roundedOffsetValue <= int.MinValue
                            ? int.MinValue
                            : (int)roundedOffsetValue;
                    int roundedOffset = unchecked(offset + 0x8000) & unchecked((int)0xFFFF0000);
                    if (!lowMatched)
                    {
                        stem.LocationLow = unchecked(other.LocationLow - roundedOffset);
                    }

                    if (!highMatched)
                    {
                        stem.LocationHigh = unchecked(other.LocationHigh - roundedOffset + relativeSpan);
                    }
                }

                if (!lowMatched)
                {
                    // Preserve the source ordering of the target low edge against both
                    // edges of the other band. Until the high edge is independently fixed,
                    // translate it by the same delta to preserve target width.
                    bool snapToOtherLow;
                    if (stem.DesignLow < other.DesignLow)
                    {
                        snapToOtherLow = stem.LocationLow > other.LocationLow;
                    }
                    else if (stem.DesignLow > other.DesignLow)
                    {
                        snapToOtherLow = stem.LocationLow < other.LocationLow;
                    }
                    else
                    {
                        snapToOtherLow = stem.LocationLow != other.LocationLow;
                    }

                    if (snapToOtherLow)
                    {
                        int delta = unchecked(other.LocationLow - stem.LocationLow);
                        if (!highMatched)
                        {
                            stem.LocationHigh = unchecked(stem.LocationHigh + delta);
                        }

                        stem.LocationLow = other.LocationLow;
                    }

                    bool snapToOtherHigh;
                    if (stem.DesignLow < other.DesignHigh)
                    {
                        snapToOtherHigh = stem.LocationLow > other.LocationHigh;
                    }
                    else if (stem.DesignLow > other.DesignHigh)
                    {
                        snapToOtherHigh = stem.LocationLow < other.LocationHigh;
                    }
                    else
                    {
                        snapToOtherHigh = stem.LocationLow != other.LocationHigh;
                    }

                    if (snapToOtherHigh)
                    {
                        int delta = unchecked(other.LocationHigh - stem.LocationLow);
                        if (!highMatched)
                        {
                            stem.LocationHigh = unchecked(stem.LocationHigh + delta);
                        }

                        stem.LocationLow = other.LocationHigh;
                    }
                }

                if (!highMatched)
                {
                    // Apply the transposed ordering constraints to the target high edge,
                    // translating the low edge with it until that low edge becomes fixed.
                    bool snapToOtherLow;
                    if (stem.DesignHigh < other.DesignLow)
                    {
                        snapToOtherLow = stem.LocationHigh > other.LocationLow;
                    }
                    else if (stem.DesignHigh > other.DesignLow)
                    {
                        snapToOtherLow = stem.LocationHigh < other.LocationLow;
                    }
                    else
                    {
                        snapToOtherLow = stem.LocationHigh != other.LocationLow;
                    }

                    if (snapToOtherLow)
                    {
                        int delta = unchecked(other.LocationLow - stem.LocationHigh);
                        if (!lowMatched)
                        {
                            stem.LocationLow = unchecked(stem.LocationLow + delta);
                        }

                        stem.LocationHigh = other.LocationLow;
                    }

                    bool snapToOtherHigh;
                    if (stem.DesignHigh < other.DesignHigh)
                    {
                        snapToOtherHigh = stem.LocationHigh > other.LocationHigh;
                    }
                    else if (stem.DesignHigh > other.DesignHigh)
                    {
                        snapToOtherHigh = stem.LocationHigh < other.LocationHigh;
                    }
                    else
                    {
                        snapToOtherHigh = stem.LocationHigh != other.LocationHigh;
                    }

                    if (snapToOtherHigh)
                    {
                        int delta = unchecked(other.LocationHigh - stem.LocationHigh);
                        if (!lowMatched)
                        {
                            stem.LocationLow = unchecked(stem.LocationLow + delta);
                        }

                        stem.LocationHigh = other.LocationHigh;
                    }
                }

                if (!lowMatched)
                {
                    if ((stem.DesignLow < other.DesignLow && stem.LocationLow > other.LocationLow)
                        || (stem.DesignLow > other.DesignLow && stem.LocationLow < other.LocationLow)
                        || (stem.DesignLow == other.DesignLow && stem.LocationLow != other.LocationLow))
                    {
                        stem.LocationLow = other.LocationLow;
                    }

                    if ((stem.DesignLow < other.DesignHigh && stem.LocationLow > other.LocationHigh)
                        || (stem.DesignLow > other.DesignHigh && stem.LocationLow < other.LocationHigh)
                        || (stem.DesignLow == other.DesignHigh && stem.LocationLow != other.LocationHigh))
                    {
                        stem.LocationLow = other.LocationHigh;
                    }
                }

                if (!highMatched)
                {
                    if ((stem.DesignHigh < other.DesignLow && stem.LocationHigh > other.LocationLow)
                        || (stem.DesignHigh > other.DesignLow && stem.LocationHigh < other.LocationLow)
                        || (stem.DesignHigh == other.DesignLow && stem.LocationHigh != other.LocationLow))
                    {
                        stem.LocationHigh = other.LocationLow;
                    }

                    if ((stem.DesignHigh < other.DesignHigh && stem.LocationHigh > other.LocationHigh)
                        || (stem.DesignHigh > other.DesignHigh && stem.LocationHigh < other.LocationHigh)
                        || (stem.DesignHigh == other.DesignHigh && stem.LocationHigh != other.LocationHigh))
                    {
                        stem.LocationHigh = other.LocationHigh;
                    }
                }

                // Equal design edges become authoritative independently. Once both edges
                // match, later records cannot move either boundary of this band.
                if (stem.DesignHigh == other.DesignHigh && !highMatched)
                {
                    highMatched = true;
                    stem.LocationHigh = other.LocationHigh;
                }

                if (stem.DesignLow == other.DesignLow && !lowMatched)
                {
                    lowMatched = true;
                    stem.LocationLow = other.LocationLow;
                }

                if (lowMatched && highMatched)
                {
                    break;
                }
            }

            stem.Flags |= 2;
        }
    }

    /// <summary>
    /// Tests whether intervening stems leave a counter visibly simple. Starting with the
    /// shared orthogonal coverage interval of its two boundary stems, subtract every
    /// intervening coverage interval. The counter survives when the total uncovered length
    /// is at least half the original shared interval.
    /// </summary>
    /// <param name="counterIndex">The candidate counter.</param>
    /// <returns><see langword="true"/> when the counter survives.</returns>
    private bool IsSimpleCounter(int counterIndex)
    {
        ref GlobalCounter counter = ref this.globalCounters[counterIndex];
        ref GlobalStem upper = ref this.globalStems[counter.UpperStem];
        ref GlobalStem lower = ref this.globalStems[counter.LowerStem];
        short rangeLow = unchecked((short)(Math.Max(upper.CoverageLow, lower.CoverageLow) >> 16));
        short rangeHigh = unchecked((short)(Math.Min(upper.CoverageHigh, lower.CoverageHigh) >> 16));
        int originalWidth = rangeHigh - rangeLow;

        // Two endpoints encode each remaining interval. The fixed 24-value scratch can
        // represent at most 12 disjoint uncovered intervals; a more fragmented candidate
        // is rejected rather than allocating on the glyph-fitting path.
        Span<short> endpoints = stackalloc short[24];
        Span<short> nextEndpoints = stackalloc short[24];
        endpoints[0] = rangeLow;
        endpoints[1] = rangeHigh;
        int endpointCount = 2;

        int start = lower.SortedIndex + 1;
        int end = upper.SortedIndex;
        for (int sorted = start; sorted < end; sorted++)
        {
            ref GlobalStem intervening = ref this.globalStems[this.sortedStemIndices[sorted]];
            if ((intervening.Flags & 0x60) != 0)
            {
                continue;
            }

            short cutLow = unchecked((short)(intervening.CoverageLow >> 16));
            short cutHigh = unchecked((short)(intervening.CoverageHigh >> 16));
            cutLow = Math.Max(cutLow, rangeLow);
            cutHigh = Math.Min(cutHigh, rangeHigh);
            if (cutLow >= cutHigh)
            {
                continue;
            }

            int nextCount = 0;
            for (int i = 0; i < endpointCount; i += 2)
            {
                short low = endpoints[i];
                short high = endpoints[i + 1];
                if (cutHigh <= low || cutLow >= high)
                {
                    if (nextCount + 2 > nextEndpoints.Length)
                    {
                        return false;
                    }

                    nextEndpoints[nextCount++] = low;
                    nextEndpoints[nextCount++] = high;
                    continue;
                }

                if (low < cutLow)
                {
                    if (nextCount + 2 > nextEndpoints.Length)
                    {
                        return false;
                    }

                    nextEndpoints[nextCount++] = low;
                    nextEndpoints[nextCount++] = cutLow;
                }

                if (cutHigh < high)
                {
                    if (nextCount + 2 > nextEndpoints.Length)
                    {
                        return false;
                    }

                    nextEndpoints[nextCount++] = cutHigh;
                    nextEndpoints[nextCount++] = high;
                }
            }

            nextEndpoints[..nextCount].CopyTo(endpoints);
            endpointCount = nextCount;
            if (endpointCount == 0)
            {
                return false;
            }
        }

        int uncovered = 0;
        for (int i = 0; i < endpointCount; i += 2)
        {
            uncovered += endpoints[i + 1] - endpoints[i];
        }

        return originalWidth <= uncovered * 2;
    }

    /// <summary>
    /// Expands one declared stem into source and ideal-device edges. An inverted width of
    /// -20 design units denotes a top ghost whose first value is the physical edge; -21
    /// denotes a bottom ghost whose second value is physical. Other negative widths are
    /// normalized by swapping their edges. Ordinary pairs preserve their design width and
    /// start at ideal device coordinates <c>edge * scale</c>.
    /// </summary>
    /// <param name="a">The first declared edge in design units.</param>
    /// <param name="b">The second declared edge in design units.</param>
    /// <param name="scale">The pixels per design unit scale.</param>
    /// <param name="recognizeGhosts">Whether horizontal Type 2 ghost widths have their single-edge meaning.</param>
    /// <param name="bottom">The bottom hint edge, invalid for a top ghost.</param>
    /// <param name="top">The top hint edge, invalid for a bottom ghost.</param>
    /// <param name="isPair">Whether both edges are valid and move together.</param>
    /// <returns><see langword="true"/> if the stem produced at least one valid edge; otherwise, <see langword="false"/>.</returns>
    private static bool TryInitHintPair(float a, float b, float scale, bool recognizeGhosts, out HintEdge bottom, out HintEdge top, out bool isPair)
    {
        bottom = default;
        top = default;
        isPair = false;

        float width = b - a;
        const float ghostTolerance = 0.5F;
        if (recognizeGhosts && MathF.Abs(width + 21F) < ghostTolerance)
        {
            bottom.Cs = b;
            bottom.FixedCs = CffFixedPoint.FromSingle(b);
            bottom.Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(bottom.FixedCs, CffFixedPoint.FromSingle(scale)));
            bottom.Scale = scale;
            bottom.Flags = HintEdgeFlags.GhostBottom;
            return true;
        }

        if (recognizeGhosts && MathF.Abs(width + 20F) < ghostTolerance)
        {
            top.Cs = a;
            top.FixedCs = CffFixedPoint.FromSingle(a);
            top.Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(top.FixedCs, CffFixedPoint.FromSingle(scale)));
            top.Scale = scale;
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
        bottom.FixedCs = CffFixedPoint.FromSingle(low);
        bottom.Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(bottom.FixedCs, CffFixedPoint.FromSingle(scale)));
        bottom.Scale = scale;
        bottom.Flags = HintEdgeFlags.PairBottom;

        top.Cs = high;
        top.FixedCs = CffFixedPoint.FromSingle(high);
        top.Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(top.FixedCs, CffFixedPoint.FromSingle(scale)));
        top.Scale = scale;
        top.Flags = HintEdgeFlags.PairTop;

        isPair = true;
        return true;
    }

    /// <summary>
    /// Captures a horizontal stem edge into the first matching blue-zone interval. A bottom
    /// zone tests the stem's low edge and a top zone tests its high edge. The selected zone
    /// edge is rounded to an integer device row, an optional integer overshoot is applied,
    /// and the stem centre is displaced toward the ink by half its rounded snapped width.
    /// </summary>
    /// <param name="bottom">The bottom hint edge.</param>
    /// <param name="top">The top hint edge.</param>
    /// <param name="zones">The transformed alignment zones in declaration order.</param>
    /// <param name="scale">The pixels per design unit scale.</param>
    /// <param name="fuzz">The band extension in design units.</param>
    /// <param name="blueShift">The overshoot in design units at which an edge keeps its overshoot.</param>
    /// <param name="blueScale">The zone-height-adjusted size factor for overshoot suppression.</param>
    /// <param name="snappedWidth">The standard-width-snapped stem width in device pixels.</param>
    /// <param name="centre">Receives the device space centre the claimed stem draws around.</param>
    /// <param name="edge">Receives the device space zone edge selected for the claimed stem.</param>
    /// <returns><see langword="true"/> if a zone captured the hint; otherwise, <see langword="false"/>.</returns>
    private static bool TryCaptureHint(ref HintEdge bottom, ref HintEdge top, ReadOnlySpan<DeviceZone> zones, float scale, float fuzz, float blueShift, float blueScale, float snappedWidth, out float centre, out float edge)
    {
        centre = 0F;
        edge = 0F;

        int scaleFixed = CffFixedPoint.FromSingle(scale);
        int fuzzFixed = CffFixedPoint.FromSingle(fuzz);
        int blueShiftFixed = CffFixedPoint.FromSingle(blueShift);
        int blueScaleFixed = CffFixedPoint.FromSingle(blueScale);

        // Test the exact zone intervals first and the fuzz-expanded intervals second. This
        // gives an edge inside one zone priority over an earlier declaration it approaches
        // only through fuzz.
        for (int pass = 0; pass < 2; pass++)
        {
            int band = pass == 0 ? 0 : fuzzFixed;
            for (int z = 0; z < zones.Length; z++)
            {
                DeviceZone zone = zones[z];
                int claimed;
                if (zone.IsBottom)
                {
                    claimed = CffFixedPoint.FromSingle(bottom.Cs);
                    if (bottom.Flags == HintEdgeFlags.None
                        || claimed < zone.DesignLower - band
                        || claimed > zone.DesignUpper + band)
                    {
                        continue;
                    }
                }
                else
                {
                    claimed = CffFixedPoint.FromSingle(top.Cs);
                    if (top.Flags == HintEdgeFlags.None
                        || claimed < zone.DesignLower - band
                        || claimed > zone.DesignUpper + band)
                    {
                        continue;
                    }
                }

                // A bottom zone always selects DeviceUpper. A top zone selects DeviceLower
                // exactly when
                // round(DesignLower * scale) >= abs(round(DesignUpper * blueScale));
                // otherwise it selects DeviceUpper. DeviceLower and DeviceUpper already
                // contain the family-zone substitution and zone-table transformation.
                int selectedDevice = zone.DeviceUpper;
                if (!zone.IsBottom)
                {
                    int transformedLower = CffFixedPoint.Multiply(zone.DesignLower, scaleFixed);
                    int roundedLower = unchecked(transformedLower + 0x8000) & unchecked((int)0xFFFF0000);
                    int upperAtBlueScale = CffFixedPoint.Multiply(zone.DesignUpper, blueScaleFixed);
                    int roundedUpperAtBlueScale = unchecked(upperAtBlueScale + 0x8000) & unchecked((int)0xFFFF0000);
                    if (roundedUpperAtBlueScale < 0)
                    {
                        roundedUpperAtBlueScale = unchecked(-roundedUpperAtBlueScale);
                    }

                    if (roundedLower >= roundedUpperAtBlueScale)
                    {
                        selectedDevice = zone.DeviceLower;
                    }
                }

                // floor(selectedDevice + 1/2) in signed 16.16 gives the granularity-one row.
                int row = unchecked(selectedDevice + 0x8000) & unchecked((int)0xFFFF0000);

                // Let d = |claimed - flat|. Distances at most BlueFuzz are normalized to the
                // flat edge. For the remaining signed overshoot o, compute deviceOvershoot
                // = o * scale. When o >= BlueShift, subtract
                // clamp(o * blueScale - 1/2, -0x7FFF/2^16, 0x7FFF/2^16), then round the
                // result to a whole pixel. Bottom zones subtract that integer overshoot from
                // the flat row; top zones add it.
                int flat = zone.DesignFlat;
                int distanceFromFlat = claimed - flat;
                if (distanceFromFlat < 0)
                {
                    distanceFromFlat = unchecked(-distanceFromFlat);
                }

                int normalizedClaimed = distanceFromFlat <= fuzzFixed ? flat : claimed;
                int overshoot = zone.IsBottom
                    ? zone.DesignUpper - normalizedClaimed
                    : normalizedClaimed - zone.DesignLower;
                int deviceOvershoot = CffFixedPoint.Multiply(overshoot, scaleFixed);
                if (overshoot >= blueShiftFixed)
                {
                    int reduction = CffFixedPoint.Multiply(overshoot, blueScaleFixed) - 0x8000;
                    reduction = Math.Clamp(reduction, -0x7FFF, 0x7FFF);
                    deviceOvershoot -= reduction;
                }

                int kept = unchecked(deviceOvershoot + 0x8000) & unchecked((int)0xFFFF0000);
                row = zone.IsBottom ? row - kept : row + kept;
                edge = CffFixedPoint.ToSingle(row);

                // The row is a feature edge, not a centre. Let W = round(snappedWidth), with
                // W forced to one when it is zero. A bottom-zone centre is row + W/2 and a
                // top-zone centre is row - W/2; W remains signed 16.16 so odd pixel widths
                // produce an exact half-pixel centre.
                int roundedWidth = unchecked(CffFixedPoint.FromSingle(snappedWidth) + 0x8000) & unchecked((int)0xFFFF0000);
                if (roundedWidth == 0)
                {
                    roundedWidth = CffFixedPoint.One;
                }

                int centreFixed = zone.IsBottom ? row + (roundedWidth >> 1) : row - (roundedWidth >> 1);
                centre = CffFixedPoint.ToSingle(centreFixed);

                if (bottom.Flags != HintEdgeFlags.None)
                {
                    bottom.Flags |= HintEdgeFlags.Locked;
                }

                if (top.Flags != HintEdgeFlags.None)
                {
                    top.Flags |= HintEdgeFlags.Locked;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Inserts a source/lock pair while keeping both centre sequences monotone. Equal source
    /// centres are duplicates and are rejected. Opposite signs for
    /// <c>newSource - oldSource</c> and <c>newLock - oldLock</c> would make two map segments
    /// cross and are also rejected. At an equal lock centre, a captured pair outranks an
    /// uncaptured pair; equal-priority pairs keep the larger source centre.
    /// </summary>
    /// <param name="map">The map receiving the hint.</param>
    /// <param name="count">The current edge count, updated on insertion.</param>
    /// <param name="bottom">The bottom hint edge, invalid when flagless.</param>
    /// <param name="top">The top hint edge, invalid when flagless.</param>
    /// <param name="isPair">Whether both edges are valid and insert together.</param>
    private static void InsertHint(HintEdge[] map, ref int count, HintEdge bottom, HintEdge top, bool isPair)
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

        if (!isPair)
        {
            second = first;
        }

        int sourceCentre = unchecked(first.FixedCs + second.FixedCs) >> 1;
        int lockCentre = unchecked(first.FixedLock + second.FixedLock) >> 1;
        int insertAt = count;
        for (int i = 0; i < count; i += 2)
        {
            int existingSourceCentre = unchecked(map[i].FixedCs + map[i + 1].FixedCs) >> 1;
            int existingLockCentre = unchecked(map[i].FixedLock + map[i + 1].FixedLock) >> 1;
            if (sourceCentre == existingSourceCentre)
            {
                return;
            }

            // Monotonicity requires sign(sourceCentre - existingSourceCentre) to agree
            // with sign(lockCentre - existingLockCentre).
            if ((sourceCentre < existingSourceCentre && lockCentre > existingLockCentre)
                || (sourceCentre > existingSourceCentre && lockCentre < existingLockCentre))
            {
                return;
            }

            if (lockCentre == existingLockCentre)
            {
                int existingKind = map[i].Kind & 1;
                int newKind = first.Kind & 1;
                bool replace = (existingKind == 0 && newKind == 1)
                    || (existingKind == newKind && sourceCentre > existingSourceCentre);
                if (replace)
                {
                    map[i] = first;
                    map[i + 1] = second;
                }

                return;
            }

            if (sourceCentre < existingSourceCentre)
            {
                insertAt = i;
                break;
            }
        }

        if (count + 2 > MaxHintEdges)
        {
            return;
        }

        for (int i = count - 1; i >= insertAt; i--)
        {
            map[i + 2] = map[i];
        }

        map[insertAt] = first;
        map[insertAt + 1] = second;
        count += 2;
    }

    /// <summary>
    /// Separates adjacent lock-pair centres whose distance is less than 1.5 device pixels.
    /// The comparison occurs in character space using <c>1.5 / scale</c>; a movable pair is
    /// translated by exactly <c>1 / scale</c>, provided that translation leaves more than
    /// 1.5 pixels to the next centre on the same side.
    /// </summary>
    /// <param name="map">The source-ordered lock pairs to adjust.</param>
    /// <param name="count">The number of live lock edges.</param>
    /// <param name="devicePixel">One device pixel inverse-transformed to character space.</param>
    /// <param name="scale">The character-to-device scale in signed 16.16 form.</param>
    private static void FixupMap(HintEdge[] map, int count, int devicePixel, int scale)
    {
        int minimumSeparation = unchecked(devicePixel + (devicePixel >> 1));
        int lastBottom = count - 2;

        // Every ordinary stem occupies an adjacent bottom/top pair. Visit each internal
        // pair boundary once and compare the arithmetic centres of the two neighbouring pairs.
        for (int currentTop = 1; currentTop < lastBottom; currentTop += 2)
        {
            int currentBottom = currentTop - 1;
            int nextBottom = currentTop + 1;
            int nextTop = currentTop + 2;

            // A one-edge ghost at either boundary does not have a movable two-edge width,
            // so the adjacent centre pair is ineligible for this separation adjustment.
            if (((map[currentTop].Kind | map[nextBottom].Kind) & 0x10) != 0)
            {
                continue;
            }

            int currentCentre = unchecked(map[currentBottom].FixedLock + map[currentTop].FixedLock) >> 1;
            int nextCentre = unchecked(map[nextBottom].FixedLock + map[nextTop].FixedLock) >> 1;
            if (minimumSeparation <= unchecked(nextCentre - currentCentre))
            {
                continue;
            }

            bool moveCurrentDown = (map[nextTop].Kind & 1) != 0;
            if (!moveCurrentDown && nextBottom != lastBottom)
            {
                int followingBottom = nextTop + 1;
                int followingTop = nextTop + 2;
                int followingCentre = unchecked(map[followingBottom].FixedLock + map[followingTop].FixedLock) >> 1;
                moveCurrentDown = followingCentre <= unchecked(nextCentre + minimumSeparation + devicePixel);
            }

            if (!moveCurrentDown)
            {
                map[nextBottom].FixedLock = unchecked(map[nextBottom].FixedLock + devicePixel);
                map[nextTop].FixedLock = unchecked(map[nextTop].FixedLock + devicePixel);
                map[nextBottom].Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(map[nextBottom].FixedLock, scale));
                map[nextTop].Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(map[nextTop].FixedLock, scale));
                continue;
            }

            bool hasRoomBelow = currentTop == 1;
            if (!hasRoomBelow)
            {
                int previousBottom = currentTop - 3;
                int previousTop = currentTop - 2;
                int previousCentre = unchecked(map[previousBottom].FixedLock + map[previousTop].FixedLock) >> 1;
                hasRoomBelow = previousCentre < unchecked(currentCentre - minimumSeparation - devicePixel);
            }

            if ((map[currentBottom].Kind & 1) == 0 && hasRoomBelow)
            {
                map[currentBottom].FixedLock = unchecked(map[currentBottom].FixedLock - devicePixel);
                map[currentTop].FixedLock = unchecked(map[currentTop].FixedLock - devicePixel);
                map[currentBottom].Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(map[currentBottom].FixedLock, scale));
                map[currentTop].Ds = CffFixedPoint.ToSingle(CffFixedPoint.Multiply(map[currentTop].FixedLock, scale));
            }
        }
    }

    /// <summary>
    /// Computes the affine transform for every interval after lock positions settle. For
    /// adjacent source locks <c>c0,c1</c> and character-space fitted locks <c>l0,l1</c>, the
    /// device mapping is <c>f(c) = m*c + b</c>, where
    /// <c>m = uniformScale * (l1-l0)/(c1-c0)</c> and
    /// <c>b = uniformScale*l0 - m*c0</c>. Exterior intervals retain
    /// <c>m = uniformScale</c> and the displacement of their nearest lock.
    /// </summary>
    /// <param name="map">The map to finalize.</param>
    /// <param name="count">The number of edges in the map.</param>
    /// <param name="uniformScale">The plain scale the segment above the last edge carries.</param>
    private static int ComputeMapScales(HintEdge[] map, int count, float uniformScale)
    {
        int uniformScaleFixed = CffFixedPoint.FromSingle(uniformScale);
        for (int i = count - 1; i >= 0; i--)
        {
            map[i + 1] = map[i];
        }

        // Bracket the real locks with Int32.MinValue/MaxValue source sentinels. The lower
        // exterior interval uses b = uniformScale * (firstLock - firstSource); the upper
        // interval uses the equivalent displacement of the last lock.
        map[0] = default;
        map[0].FixedCs = int.MinValue;
        map[0].FixedScale = uniformScaleFixed;
        map[0].FixedIntercept = CffFixedPoint.Multiply(
            map[1].FixedLock - map[1].FixedCs,
            uniformScaleFixed);

        map[count].FixedScale = uniformScaleFixed;
        map[count].FixedIntercept = CffFixedPoint.Multiply(
            map[count].FixedLock - map[count].FixedCs,
            uniformScaleFixed);

        map[count + 1] = default;
        map[count + 1].FixedCs = int.MaxValue;

        for (int i = 1; i < count; i++)
        {
            int sourceDelta = map[i + 1].FixedCs - map[i].FixedCs;
            if (sourceDelta <= 0)
            {
                // Reversed or equal source edges cannot define a positive-width interval.
                // Coalesce them at (c0+c1)/2 and transfer sourceDelta/2 to the two fitted
                // locks in opposite directions, preserving the pair's combined displacement.
                int midpoint = unchecked(map[i + 1].FixedCs + map[i].FixedCs) >> 1;
                int halfDelta = sourceDelta >> 1;
                map[i].FixedCs = midpoint;
                map[i].FixedLock += halfDelta;
                map[i + 1].FixedLock -= halfDelta;
                map[i + 1].FixedCs = midpoint;
                map[i].FixedScale = 0;
                map[i].FixedIntercept = CffFixedPoint.Multiply(map[i].FixedLock, uniformScaleFixed);
                continue;
            }

            if (map[i + 1].FixedLock <= map[i].FixedLock)
            {
                // A non-increasing fitted interval is collapsed to a constant map at l0.
                // Updating l1 as well keeps every later interval anchored to that same
                // monotone boundary.
                map[i + 1].FixedLock = map[i].FixedLock;
                map[i].FixedScale = 0;
                map[i].FixedIntercept = CffFixedPoint.Multiply(map[i].FixedLock, uniformScaleFixed);
                continue;
            }

            int lockRatio = CffFixedPoint.Divide(map[i + 1].FixedLock - map[i].FixedLock, sourceDelta);
            int segmentScale = CffFixedPoint.Multiply(uniformScaleFixed, lockRatio);

            // Cap m at 0x2AE14/2^16, approximately 2.6800 device units per source unit,
            // so a narrow source interval cannot create an unbounded interpolation slope.
            map[i].FixedScale = Math.Min(segmentScale, 0x2AE14);
            map[i].FixedIntercept = CffFixedPoint.Multiply(map[i].FixedLock, uniformScaleFixed)
                - CffFixedPoint.Multiply(map[i].FixedCs, map[i].FixedScale);
        }

        return count + 2;
    }

    /// <summary>
    /// Evaluates the affine segment containing one character-space coordinate. The cached
    /// segment index makes contour-ordered coordinates cheap when they stay in the same or
    /// an adjacent interval; forward and backward walks still produce the exact interval
    /// for non-monotone contour order.
    /// </summary>
    /// <param name="map">The map to transform through.</param>
    /// <param name="count">The number of edges in the map.</param>
    /// <param name="lastIndex">The segment cache carried between successive lookups.</param>
    /// <param name="value">The character space coordinate.</param>
    /// <param name="uniformScale">The plain scale for coordinates the map does not span.</param>
    /// <returns>The fitted coordinate.</returns>
    private static float MapCoordinate(HintEdge[] map, int count, ref int lastIndex, float value, float uniformScale)
    {
        if (count == 0)
        {
            return CffFixedPoint.ToSingle(CffFixedPoint.Multiply(CffFixedPoint.FromSingle(value), CffFixedPoint.FromSingle(uniformScale)));
        }

        int fixedValue = CffFixedPoint.FromSingle(value);
        if (fixedValue == int.MaxValue)
        {
            return CffFixedPoint.ToSingle(int.MaxValue);
        }

        int i = lastIndex;
        if (i >= count)
        {
            i = count - 1;
        }

        while (i < count - 1 && fixedValue >= map[i + 1].FixedCs)
        {
            i++;
        }

        while (i > 0 && fixedValue < map[i].FixedCs)
        {
            i--;
        }

        lastIndex = i;

        // Non-negative multiples of 1/2 whose upper nibble is clear can multiply exactly
        // as ((value / 2^15) * scale) / 2. Other values use the general signed 16.16
        // multiply. Both branches evaluate fixedValue * segmentScale / 2^16.
        int product = ((uint)fixedValue & 0xF0007FFFU) == 0
            ? unchecked((int)((((uint)(fixedValue >> 15) & 0xFFFFU) * (uint)map[i].FixedScale) >> 1))
            : CffFixedPoint.Multiply(map[i].FixedScale, fixedValue);
        int mapped = product + map[i].FixedIntercept;
        return CffFixedPoint.ToSingle(mapped);
    }

    /// <summary>
    /// Stores one edge constraint and the affine segment beginning at that constraint.
    /// <see cref="FixedCs"/> and <see cref="FixedLock"/> are respectively the source and
    /// fitted character-space coordinates in signed 16.16. <see cref="FixedScale"/> and
    /// <see cref="FixedIntercept"/> satisfy <c>device = source * scale + intercept</c>.
    /// </summary>
    private struct HintEdge
    {
        public float Cs;
        public float Ds;
        public float Scale;
        public int FixedCs;
        public int FixedLock;
        public int FixedScale;
        public int FixedIntercept;
        public HintEdgeFlags Flags;
        public byte Kind;
    }

    /// <summary>
    /// Stores one stem while the full glyph is solved as a counter graph. Design and
    /// coverage coordinates are signed 16.16 design units; ideal, fitted location, width,
    /// and half-width coordinates are signed 16.16 device pixels. Counter fields are array
    /// indices, and a value of <c>-1</c> terminates the corresponding chain.
    /// </summary>
    private struct GlobalStem
    {
        public int AdjustedWidth;
        public int DesignLow;
        public int DesignHigh;
        public int IdealLow;
        public int IdealHigh;
        public int CoverageLow;
        public int CoverageHigh;
        public int LocationLow;
        public int LocationHigh;
        public int RawWidth;
        public int ActualHalfWidth;
        public int OutgoingCounter;
        public int PredecessorCounter;
        public int Flags;
        public short Depth;
        public short SortedIndex;
    }

    /// <summary>
    /// Stores the open interval between two stems. Widths and clumping bounds are signed
    /// 16.16 device pixels. The linked-list indices connect the global candidate list and
    /// each lower stem's outgoing edges; group fields identify counters jointly quantized
    /// to integer pixels along one retained path.
    /// </summary>
    private struct GlobalCounter
    {
        public int NextGlobal;
        public int Width;
        public int UpperStem;
        public int LowerStem;
        public int NextOutgoing;
        public int GroupLow;
        public int GroupHigh;
        public int WorkingWidth;
        public byte GroupEnd;
        public bool Joined;
    }

    private sealed class PooledObjectPolicy : IPooledObjectPolicy<HintMap>
    {
        public HintMap Create() => new();

        public bool Return(HintMap obj) => true;
    }
}
