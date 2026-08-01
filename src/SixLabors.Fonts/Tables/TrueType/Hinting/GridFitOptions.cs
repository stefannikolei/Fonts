// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.TrueType.Hinting;

/// <summary>
/// Carries the per glyph parameters for geometric grid fitting. All values are expressed
/// in device pixels in the glyph's unhinted coordinate space, Y up with the baseline at zero.
/// </summary>
internal readonly struct GridFitOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GridFitOptions"/> struct.
    /// </summary>
    /// <param name="pixelsPerEm">The pixel size of the em square, which scales the feature detection thresholds.</param>
    /// <param name="fitX">The fitting mode for the horizontal axis.</param>
    /// <param name="fitY">The fitting mode for the vertical axis.</param>
    /// <param name="topAnchors">The alignment heights that attract top edges, such as the x-height and cap height for TrueType. The baseline at zero always attracts bottom edges. The array is font level state and is never modified.</param>
    /// <param name="bottomAnchors">The alignment depths below the baseline that attract bottom edges. The array is font level state and is never modified.</param>
    /// <param name="zones">The declared alignment zones for hint map fitting, in design units. The array is font level state and is never modified.</param>
    /// <param name="blueFuzz">The fuzz distance extending each zone band, in design units.</param>
    /// <param name="anchorScale">The factor converting the anchor heights and zones into pixels, letting callers share one design unit array across all sizes without allocating.</param>
    public GridFitOptions(float pixelsPerEm, GridFitAxisMode fitX, GridFitAxisMode fitY, float[] topAnchors, float[] bottomAnchors, HintZone[] zones, float blueFuzz, float anchorScale)
    {
        this.PixelsPerEm = pixelsPerEm;
        this.FitX = fitX;
        this.FitY = fitY;
        this.TopAnchors = topAnchors;
        this.BottomAnchors = bottomAnchors;
        this.Zones = zones;
        this.BlueFuzz = blueFuzz;
        this.AnchorScale = anchorScale;
    }

    /// <summary>
    /// Gets the pixel size of the em square.
    /// </summary>
    public float PixelsPerEm { get; }

    /// <summary>
    /// Gets the fitting mode for the horizontal axis.
    /// </summary>
    public GridFitAxisMode FitX { get; }

    /// <summary>
    /// Gets the fitting mode for the vertical axis.
    /// </summary>
    public GridFitAxisMode FitY { get; }

    /// <summary>
    /// Gets the alignment heights that attract top edges. Zero valued entries are ignored.
    /// </summary>
    public float[] TopAnchors { get; }

    /// <summary>
    /// Gets the alignment depths below the baseline that attract bottom edges. Entries at
    /// or above zero are ignored.
    /// </summary>
    public float[] BottomAnchors { get; }

    /// <summary>
    /// Gets the declared alignment zones for hint map fitting, in design units.
    /// </summary>
    public HintZone[] Zones { get; }

    /// <summary>
    /// Gets the fuzz distance extending each zone band, in design units.
    /// </summary>
    public float BlueFuzz { get; }

    /// <summary>
    /// Gets the factor converting the anchor heights into pixels.
    /// </summary>
    public float AnchorScale { get; }
}
