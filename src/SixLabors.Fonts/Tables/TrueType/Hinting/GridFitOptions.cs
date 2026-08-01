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
    /// <param name="xHeight">The x-height in pixels, or zero when the font does not provide one.</param>
    /// <param name="capHeight">The cap height in pixels, or zero when the font does not provide one.</param>
    public GridFitOptions(float pixelsPerEm, GridFitAxisMode fitX, GridFitAxisMode fitY, float xHeight, float capHeight)
    {
        this.PixelsPerEm = pixelsPerEm;
        this.FitX = fitX;
        this.FitY = fitY;
        this.XHeight = xHeight;
        this.CapHeight = capHeight;
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
    /// Gets the x-height in pixels, or zero when unknown.
    /// </summary>
    public float XHeight { get; }

    /// <summary>
    /// Gets the cap height in pixels, or zero when unknown.
    /// </summary>
    public float CapHeight { get; }
}
