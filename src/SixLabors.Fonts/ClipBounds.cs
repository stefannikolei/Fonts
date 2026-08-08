// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;

namespace SixLabors.Fonts;

/// <summary>
/// Represents a rectangular clipping region as axis-aligned bounds in the design space of the
/// glyph source together with the transform that maps that space to the rendering surface.
/// </summary>
/// <remarks>
/// A renderer clips against <see cref="Bounds"/> in design space first, then applies
/// <see cref="Transform"/> to the result. The bounds are never pre-transformed: a transformed
/// region loses its axis-aligned rectangle and with it the ability to intersect exactly in
/// design space.
/// </remarks>
public readonly struct ClipBounds
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClipBounds"/> struct.
    /// </summary>
    /// <param name="bounds">The axis-aligned bounds of the clipping region in design space.</param>
    /// <param name="transform">The transform from the design space of <paramref name="bounds"/> to the rendering surface.</param>
    public ClipBounds(FontRectangle bounds, Matrix3x2 transform)
    {
        this.Bounds = bounds;
        this.Transform = transform;
    }

    /// <summary>
    /// Gets the axis-aligned bounds of the clipping region in design space.
    /// </summary>
    public FontRectangle Bounds { get; }

    /// <summary>
    /// Gets the transform from the design space of <see cref="Bounds"/> to the rendering surface.
    /// </summary>
    public Matrix3x2 Transform { get; }
}
