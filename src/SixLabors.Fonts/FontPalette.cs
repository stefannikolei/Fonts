// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts;

/// <summary>
/// Selects the CPAL color palette used to resolve COLR glyph colors and optionally replaces individual palette entries.
/// <para/>
/// A palette index outside the range defined by the font selects the default palette (index 0), and overrides still
/// apply, matching the CSS <c>font-palette</c> behavior. Overrides apply in order, so a later override of the same
/// entry replaces an earlier one, and overrides whose entry index lies outside the font's palette entry range are ignored.
/// </summary>
public sealed class FontPalette : IEquatable<FontPalette>
{
    /// <summary>
    /// The override colors applied on top of the selected palette. Instances are immutable so
    /// that a palette can act as a cache key; the constructor copies the caller's collection.
    /// </summary>
    private readonly FontPaletteOverride[] overrides;

    /// <summary>
    /// The hash code precomputed over the index and override sequence, so repeated cache
    /// lookups do not rehash the override array.
    /// </summary>
    private readonly int hashCode;

    /// <summary>
    /// Initializes a new instance of the <see cref="FontPalette"/> class.
    /// </summary>
    /// <param name="index">The zero-based palette index within the font's CPAL table.</param>
    public FontPalette(int index)
        : this(index, Array.Empty<FontPaletteOverride>())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FontPalette"/> class.
    /// </summary>
    /// <param name="index">The zero-based palette index within the font's CPAL table.</param>
    /// <param name="overrides">The override colors to apply, in order, on top of the selected palette.</param>
    public FontPalette(int index, IReadOnlyList<FontPaletteOverride> overrides)
    {
        Guard.MustBeGreaterThanOrEqualTo(index, 0, nameof(index));
        Guard.NotNull(overrides, nameof(overrides));

        this.Index = index;

        FontPaletteOverride[] copy = new FontPaletteOverride[overrides.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = overrides[i];
        }

        this.overrides = copy;

        HashCode hash = default;
        hash.Add(index);
        for (int i = 0; i < copy.Length; i++)
        {
            hash.Add(copy[i]);
        }

        this.hashCode = hash.ToHashCode();
    }

    /// <summary>
    /// Gets the zero-based palette index within the font's CPAL table.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets the override colors applied, in order, on top of the selected palette.
    /// </summary>
    public IReadOnlyList<FontPaletteOverride> Overrides => this.overrides;

    /// <inheritdoc/>
    public bool Equals(FontPalette? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        // The cached hash rejects almost every non-equal palette without touching the
        // override array; the element loop below only confirms true matches.
        if (this.hashCode != other.hashCode || this.Index != other.Index || this.overrides.Length != other.overrides.Length)
        {
            return false;
        }

        for (int i = 0; i < this.overrides.Length; i++)
        {
            if (!this.overrides[i].Equals(other.overrides[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj is FontPalette other && this.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
        => this.hashCode;
}
