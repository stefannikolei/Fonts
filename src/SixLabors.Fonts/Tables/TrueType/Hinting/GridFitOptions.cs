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
    /// <param name="topAnchors">The alignment heights that attract top edges, such as the x-height and cap height for TrueType or the blue zone flats for CFF. The baseline at zero always attracts bottom edges. The array is font level state and is never modified.</param>
    /// <param name="anchorScale">The factor converting the anchor heights into pixels, letting callers share one design unit anchor array across all sizes without allocating.</param>
    public GridFitOptions(float pixelsPerEm, GridFitAxisMode fitX, GridFitAxisMode fitY, float[] topAnchors, float anchorScale)
    {
        this.PixelsPerEm = pixelsPerEm;
        this.FitX = fitX;
        this.FitY = fitY;
        this.TopAnchors = topAnchors;
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
    /// Gets the factor converting the anchor heights into pixels.
    /// </summary>
    public float AnchorScale { get; }
}
