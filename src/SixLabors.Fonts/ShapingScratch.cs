// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts;

/// <summary>
/// Reusable shaping pipeline state: the per-font-run workspace buffer, the accumulated
/// result buffer, and their shared feature map. Buffer storage grows to the workload's
/// high-water mark and is reused across calls, so steady-state shaping performs no
/// per-call allocation for pipeline state.
/// </summary>
/// <remarks>
/// A scratch is exclusively owned by one shaping call at a time, enforced by
/// <see cref="ObjectPool{T}"/> ownership in <see cref="TextShaper"/>. Reuse is safe
/// because every public shaping result is materialized by value before the scratch is
/// returned; nothing the pipeline pools can escape a call.
/// </remarks>
internal sealed class ShapingScratch
{
    /// <summary>
    /// The per-font-run workspace buffer glyphs are substituted in.
    /// </summary>
    private ShapingBuffer? workspace;

    /// <summary>
    /// The accumulated result buffer glyphs are seeded and positioned in.
    /// </summary>
    private ShapingBuffer? result;

    /// <summary>
    /// Gets the reusable shaping buffers, reset for a new pass over the given options.
    /// </summary>
    /// <param name="options">The text options for the pass.</param>
    /// <returns>The reusable buffers, sharing one feature map.</returns>
    public (ShapingBuffer Workspace, ShapingBuffer Result) Prepare(TextOptions options)
    {
        if (this.workspace is null)
        {
            this.workspace = new(options, ShapingBufferRole.Substitution);
            this.result = new(options, ShapingBufferRole.Positioning);
        }
        else
        {
            this.workspace!.Reset(options);
            this.result!.Reset(options);

            // The single-run fast path flips the workspace to the positioning role in
            // place; a pooled scratch must hand out buffers in their home roles.
            this.workspace.SetRole(ShapingBufferRole.Substitution);
            this.result.SetRole(ShapingBufferRole.Positioning);
        }

        return (this.workspace!, this.result!);
    }
}
