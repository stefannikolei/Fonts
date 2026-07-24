// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;

/// <summary>
/// Abstract base class for all script shapers. Defines the shaping pipeline
/// consisting of preprocessing, feature planning, postprocessing, and feature assignment stages.
/// </summary>
internal abstract class BaseShaper
{
    /// <summary>
    /// Gets or sets the script classification for this shaper.
    /// </summary>
    public ScriptClass ScriptClass { get; protected set; }

    /// <summary>
    /// Gets or sets the mark zeroing mode that determines when mark advances are zeroed.
    /// </summary>
    public MarkZeroingMode MarkZeroingMode { get; protected set; }

    /// <summary>
    /// Assigns the features to each glyph within the buffer.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the elements to assign.</param>
    /// <param name="count">The number of elements to assign.</param>
    public void Plan(ShapingBuffer buffer, int index, int count)
    {
        int collectionCount = buffer.Count;

        this.PlanPreprocessingFeatures(buffer, index, count);

        RecalculateCount(buffer, ref collectionCount, ref count);

        this.PlanFeatures(buffer, index, count);

        RecalculateCount(buffer, ref collectionCount, ref count);

        this.PlanPostprocessingFeatures(buffer, index, count);

        RecalculateCount(buffer, ref collectionCount, ref count);

        this.AssignFeatures(buffer, index, count);
    }

    /// <summary>
    /// Assigns the features to each glyph within the buffer.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the elements to assign.</param>
    /// <param name="count">The number of elements to assign.</param>
    protected abstract void PlanFeatures(ShapingBuffer buffer, int index, int count);

    /// <summary>
    /// Assigns the preprocessing features to each glyph within the buffer.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the elements to assign.</param>
    /// <param name="count">The number of elements to assign.</param>
    protected abstract void PlanPreprocessingFeatures(ShapingBuffer buffer, int index, int count);

    /// <summary>
    /// Assigns the postprocessing features to each glyph within the buffer.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the elements to assign.</param>
    /// <param name="count">The number of elements to assign.</param>
    protected abstract void PlanPostprocessingFeatures(ShapingBuffer buffer, int index, int count);

    /// <summary>
    /// Assigns the shaper specific substitution features to each glyph within the buffer.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the elements to assign.</param>
    /// <param name="count">The number of elements to assign.</param>
    protected abstract void AssignFeatures(ShapingBuffer buffer, int index, int count);

    /// <summary>
    /// Gets the ordered buffer of shaping stages for this shaper. The concrete
    /// list type lets the per-section stage walk enumerate without interface
    /// dispatch or a boxed enumerator.
    /// </summary>
    /// <returns>The shaping stages.</returns>
    public abstract List<ShapingStage> GetShapingStages();

    /// <summary>
    /// Recalculates the count when the buffer size changes during shaping.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="oldCount">The previous buffer count, updated to the current count.</param>
    /// <param name="count">The element count, adjusted by the size delta.</param>
    private static void RecalculateCount(ShapingBuffer buffer, ref int oldCount, ref int count)
    {
        // If the buffer has changed size we need to recalculate the count.
        int delta = buffer.Count - oldCount;
        count += delta;
        oldCount += delta;
    }
}
