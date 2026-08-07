// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Rendering;

/// <summary>
/// Identifies a group transition in a painted layer stream.
/// </summary>
internal enum PaintedCompositeCommandKind
{
    /// <summary>
    /// Begins an isolated group.
    /// </summary>
    Begin,

    /// <summary>
    /// Ends the current group and blends it onto the content below it within its parent.
    /// </summary>
    End
}

/// <summary>
/// Represents a group transition at a position in a painted layer stream.
/// </summary>
internal readonly struct PaintedCompositeCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PaintedCompositeCommand"/> struct for an
    /// end transition, which carries no mode.
    /// </summary>
    /// <param name="layerIndex">The index of the next layer after this command.</param>
    /// <param name="kind">The group transition.</param>
    public PaintedCompositeCommand(int layerIndex, PaintedCompositeCommandKind kind)
        : this(layerIndex, kind, CompositeMode.SrcOver)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PaintedCompositeCommand"/> struct.
    /// </summary>
    /// <param name="layerIndex">The index of the next layer after this command.</param>
    /// <param name="kind">The group transition.</param>
    /// <param name="mode">The mode used by a begin transition to blend the finished group.</param>
    public PaintedCompositeCommand(int layerIndex, PaintedCompositeCommandKind kind, CompositeMode mode)
    {
        this.LayerIndex = layerIndex;
        this.Kind = kind;
        this.Mode = mode;
    }

    /// <summary>
    /// Gets the index of the next layer after this command.
    /// </summary>
    public int LayerIndex { get; }

    /// <summary>
    /// Gets the group transition.
    /// </summary>
    public PaintedCompositeCommandKind Kind { get; }

    /// <summary>
    /// Gets the mode used by a begin transition to blend the finished group.
    /// </summary>
    public CompositeMode Mode { get; }
}
