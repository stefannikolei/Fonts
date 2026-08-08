// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts;

/// <summary>
/// Replaces the color of a single palette entry when a <see cref="FontPalette"/> is applied.
/// An override whose <see cref="Index"/> lies outside the font's palette entry range is ignored.
/// </summary>
public readonly struct FontPaletteOverride : IEquatable<FontPaletteOverride>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FontPaletteOverride"/> struct.
    /// </summary>
    /// <param name="index">The zero-based palette entry index to replace.</param>
    /// <param name="color">The replacement color.</param>
    public FontPaletteOverride(int index, GlyphColor color)
    {
        Guard.MustBeGreaterThanOrEqualTo(index, 0, nameof(index));

        this.Index = index;
        this.Color = color;
    }

    /// <summary>
    /// Gets the zero-based palette entry index to replace.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets the replacement color.
    /// </summary>
    public GlyphColor Color { get; }

    /// <summary>
    /// Compares two <see cref="FontPaletteOverride"/> objects for equality.
    /// </summary>
    /// <param name="left">The <see cref="FontPaletteOverride"/> on the left side of the operand.</param>
    /// <param name="right">The <see cref="FontPaletteOverride"/> on the right side of the operand.</param>
    /// <returns>True if the current left is equal to the <paramref name="right"/> parameter; otherwise, false.</returns>
    public static bool operator ==(FontPaletteOverride left, FontPaletteOverride right)
        => left.Equals(right);

    /// <summary>
    /// Compares two <see cref="FontPaletteOverride"/> objects for inequality.
    /// </summary>
    /// <param name="left">The <see cref="FontPaletteOverride"/> on the left side of the operand.</param>
    /// <param name="right">The <see cref="FontPaletteOverride"/> on the right side of the operand.</param>
    /// <returns>True if the current left is unequal to the <paramref name="right"/> parameter; otherwise, false.</returns>
    public static bool operator !=(FontPaletteOverride left, FontPaletteOverride right)
        => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(FontPaletteOverride other)
        => this.Index == other.Index && this.Color.Equals(other.Color);

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj is FontPaletteOverride other && this.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(this.Index, this.Color);
}
