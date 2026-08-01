// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.TrueType.Hinting;

/// <summary>
/// The tuning table for <see cref="GlyphGridFitter"/>. Grid fitting is a perceptual
/// heuristic, so these values are not derivable from first principles alone; each records
/// the rule it implements and the observed rendering defect that pinned it during pixel
/// level comparison against classic bi-level rasterizer output. Change values only with
/// that comparison harness in hand.
/// </summary>
internal static class GridFitterTuning
{
    /// <summary>
    /// The coverage fraction at which an aliased rasterizer turns a pixel on. The sampling
    /// related tolerances below derive from it.
    /// </summary>
    public const float CoverageThreshold = 0.5F;

    /// <summary>
    /// The maximum axis drift, per polygon edge and against the run anchor, for points to
    /// count as one flat stem flank. Half the coverage threshold: variation below this
    /// cannot move the flank across a rounding boundary.
    /// </summary>
    public const float SegmentSlackPx = CoverageThreshold / 2F;

    /// <summary>
    /// The wider drift allowed when detecting a curve extremum as a round stem flank.
    /// Curve control points sit further from the extremum than flat run points do; 'o'
    /// flanks at small sizes fail detection below this value.
    /// </summary>
    public const float RoundExtremumSlackPx = 0.6F;

    /// <summary>
    /// The maximum separation at which same direction segments merge into one edge. Kept
    /// well under the visually significant fraction of a pixel so opposing hairline flanks
    /// can never be bridged by intermediate noise.
    /// </summary>
    public const float EdgeMergePx = 0.2F;

    /// <summary>
    /// The smallest perpendicular extent that qualifies a run as a stem flank at the
    /// smallest sizes. Tahoma stem feet and arch tops at eight pixels per em measure just
    /// above this; raising it makes those features undetectable and their strokes collapse.
    /// </summary>
    public const float MinSegmentExtentFloorPx = 0.45F;

    /// <summary>
    /// The largest value the adaptive extent threshold reaches. Matches the fixed threshold
    /// the fitter shipped with before the threshold became size adaptive; larger sizes use
    /// it to reject shallow curve chunks that are not stems.
    /// </summary>
    public const float MinSegmentExtentCeilingPx = 0.75F;

    /// <summary>
    /// The growth of the extent threshold per pixel of em size between the floor and the
    /// ceiling. Feature sizes scale with the em, so the threshold separating flanks from
    /// curve noise scales with it.
    /// </summary>
    public const float MinSegmentExtentPerPpem = 0.06F;

    /// <summary>
    /// The width below which an opposing pair is a degenerate sliver rather than a stroke.
    /// Guards against coincident contour edges pairing and being widened into phantom ink.
    /// </summary>
    public const float MinStemWidthPx = 0.05F;

    /// <summary>
    /// The width above which a pair is no longer treated as a stem. Strokes wider than
    /// this render acceptably without snapping and moving them risks visible distortion.
    /// </summary>
    public const float MaxStemWidthPx = 4F;

    /// <summary>
    /// The width below which a stem rounds to exactly one pixel rather than to nearest.
    /// Biasing the round up toward one pixel keeps light stems from vanishing while
    /// widths approaching two pixels still round naturally.
    /// </summary>
    public const float OnePixelWidthPx = 1.3F;

    /// <summary>
    /// The width below which rescue mode processes a stroke. One coverage threshold plus
    /// margin: a stroke wider than this always lights at least one pixel on its own, so
    /// rescue would only add weight.
    /// </summary>
    public const float RescueMaxWidthPx = 0.85F;

    /// <summary>
    /// How near a round stem's width must be to a whole pixel count before its flanks are
    /// snapped. Rounder is honest: a round stroke far from an integral width renders
    /// better soft than distorted onto the grid.
    /// </summary>
    public const float RoundWidthSnapPx = 0.35F;

    /// <summary>
    /// The base cap on how far one flank may move. Sub pixel strokes additionally receive
    /// the movement their mandatory widening demands on top of this cap; without that
    /// allowance an anchored thin stroke inverts when its pair is reverted.
    /// </summary>
    public const float MaxEdgeDeltaPx = 0.75F;

    /// <summary>
    /// The range within which a horizontal edge snaps to the baseline, x-height or cap
    /// height anchor. Wide enough to capture overshoots and flats together so round and
    /// square glyph heights agree; anchor snapping applies only to the edge whose ink
    /// faces away from the anchor.
    /// </summary>
    public const float AnchorSnapRangePx = 0.6F;

    /// <summary>
    /// The range within which an unfitted edge follows a fitted same direction neighbor.
    /// Keeps the two edges of a slightly slanted flank moving together instead of being
    /// pulled apart by interpolation.
    /// </summary>
    public const float SatelliteRangePx = 0.5F;

    /// <summary>
    /// The tolerance subtracted before taking the ceiling of a top anchor. An anchor that
    /// already sits on a whole pixel must stay there; without the tolerance, floating
    /// point noise above the integer would push such an anchor a full row higher. Kept
    /// tight: an x-height just five hundredths over the integer still earns its extra row
    /// in classic rasterizer output.
    /// </summary>
    public const float AnchorCeilingFuzzPx = 0.02F;

    /// <summary>
    /// The upward bias when rounding a declared wall stem width to whole pixels. Well
    /// under a half: classic rasterizers regularize declared stems toward the thin side,
    /// keeping a wall of around one and a half pixels at a single crisp pixel. Only walls
    /// qualify: a wall at one pixel fills its column completely, while a diagonal stroke
    /// narrowed the same way drops below the coverage threshold and breaks apart row by
    /// row.
    /// </summary>
    public const float DeclaredWidthRoundBiasPx = 0.25F;

    /// <summary>
    /// The band around a declared flank within which any outline point counts toward its
    /// wall measurement. Wider than the flatness band because a curved wall, such as a
    /// bowl side, sweeps near its extremum rather than resting exactly on it.
    /// </summary>
    public const float WallBandPx = 0.5F;

    /// <summary>
    /// The sustained run length within the wall band that qualifies a flank as a wall. A
    /// bowl side or straight stem runs several pixels close to its flank; a diagonal
    /// crosses its flank in under two.
    /// </summary>
    public const float WallMinExtentPx = 2F;
}
