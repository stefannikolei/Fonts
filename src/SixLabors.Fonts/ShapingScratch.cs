// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Tables.AdvancedTypographic;

namespace SixLabors.Fonts;

/// <summary>
/// Reusable shaping pipeline state: the substitution and positioning collections, their
/// shared feature map, and the pool of retired glyph data instances. This is the port of
/// the reference engine's reusable buffer memory model (hb_buffer_t): storage grows to
/// the workload's high-water mark and is reused across calls, so steady-state shaping
/// performs no per-call allocation for pipeline state.
/// </summary>
/// <remarks>
/// A scratch is exclusively owned by one shaping call at a time, enforced by
/// <see cref="ObjectPool{T}"/> ownership in <see cref="TextShaper"/>. Reuse is safe
/// because every public shaping result is materialized by value before the scratch is
/// returned; nothing the pipeline pools can escape a call.
/// </remarks>
internal sealed class ShapingScratch
{
    /// <summary>The pass-wide feature bit assignment, reset per call.</summary>
    private ShapingFeatureMap? featureMap;

    /// <summary>The reusable substitution collection.</summary>
    private GlyphSubstitutionCollection? substitutions;

    /// <summary>The reusable positioning collection.</summary>
    private GlyphPositioningCollection? positionings;

    /// <summary>
    /// Retired <see cref="GlyphShapingData"/> instances awaiting reuse. Filled from the
    /// positioning collection at reset time, drained by the substitution collection as
    /// glyphs are added.
    /// </summary>
    private readonly List<GlyphShapingData> dataPool = [];

    /// <summary>
    /// Gets the reusable shaping collections, reset for a new pass over the given
    /// options. The positioning collection's retired glyph data instances are returned
    /// to the pool before the substitution collection begins renting.
    /// </summary>
    /// <param name="options">The text options for the pass.</param>
    /// <returns>The reusable collections, sharing one feature map.</returns>
    internal (GlyphSubstitutionCollection Substitutions, GlyphPositioningCollection Positionings) Prepare(TextOptions options)
    {
        if (this.featureMap is null)
        {
            this.featureMap = new();
            this.substitutions = new(options, this.featureMap)
            {
                ReusePool = this.dataPool,
            };
            this.positionings = new(options, this.featureMap);
        }
        else
        {
            this.featureMap.Reset();
            this.positionings!.ResetForReuse(options, this.dataPool);
            this.substitutions!.ResetForReuse(options);
        }

        return (this.substitutions!, this.positionings!);
    }
}
