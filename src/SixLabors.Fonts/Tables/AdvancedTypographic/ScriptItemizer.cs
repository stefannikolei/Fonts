// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// Splits a shaping buffer into runs of a single script and plans a shaper for
/// each run.
/// <para>
/// Planning is what prepares a run's text and registers its features, so it
/// belongs to every font rather than to the fonts that carry a substitution
/// table. A font without one still needs the characters a script cannot be read
/// without, and positioning reuses the runs planning records.
/// </para>
/// </summary>
internal static class ScriptItemizer
{
    /// <summary>
    /// Reads the run of one script that begins at <paramref name="index"/>,
    /// leaving <paramref name="index"/> on the run's last record.
    /// <para>
    /// Feature lookups are assigned to runs rather than to the text as a whole so
    /// that the shapers of two languages cannot interfere with one another.
    /// Characters belonging to no script of their own join the run around them.
    /// </para>
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the run's first record, left on its last.</param>
    /// <param name="maxCount">The largest record count a run may reach.</param>
    /// <param name="count">When this method returns, contains the number of records in the run.</param>
    /// <returns>The script of the run.</returns>
    public static ScriptClass ReadRun(ShapingBuffer buffer, ref int index, int maxCount, out int count)
    {
        ScriptClass current = CodePoint.GetScriptClass(buffer[index].CodePoint);
        count = 1;

        while (index < buffer.Count - 1)
        {
            ScriptClass next = CodePoint.GetScriptClass(buffer[index + 1].CodePoint);
            if (next != current &&
                current is not ScriptClass.Common and not ScriptClass.Unknown and not ScriptClass.Inherited &&
                next is not ScriptClass.Common and not ScriptClass.Unknown and not ScriptClass.Inherited)
            {
                break;
            }

            if (current is ScriptClass.Common or ScriptClass.Unknown or ScriptClass.Inherited)
            {
                current = next;
            }

            index++;
            count++;

            if (index >= maxCount)
            {
                break;
            }
        }

        return current;
    }

    /// <summary>
    /// Plans a shaper over every run of the buffer and records the runs for the
    /// positioning pass. Used for a font that carries no substitution table,
    /// where there are no lookups to apply but the text still has to be prepared.
    /// </summary>
    /// <remarks>
    /// Stage actions are applied without a substitution table to match HarfBuzz 14.2.1, <c>src/hb-ot-layout.cc</c>, symbol <c>hb_ot_map_t::apply</c>, called by <c>hb_ot_shape_plan_t::substitute</c> in <c>src/hb-ot-shape.cc</c>. The stage-action rule is shaping behavior and is not derivable from the Unicode Character Database.
    /// </remarks>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    public static void PlanRuns(FontMetrics fontMetrics, ShapingBuffer buffer)
    {
        // Set max constraints to prevent OutOfMemoryException or infinite loops from attacks.
        int maxCount = AdvancedTypographicUtils.GetMaxAllowableShapingCollectionCount(buffer.Count);

        for (int i = 0; i < buffer.Count; i++)
        {
            int index = i;
            ScriptClass script = ReadRun(buffer, ref i, maxCount, out int count);

            // With no substitution table the font offers no script of its own, so
            // the run is planned against the default design.
            ShapePlan shapePlan = buffer.GetOrCreatePlan(script, default, fontMetrics);

            // Preparing the text can insert records, so the run grows with it.
            int collectionCount = buffer.Count;

            shapePlan.Shaper.Plan(fontMetrics, buffer, index, count);

            int delta = buffer.Count - collectionCount;
            i += delta;
            count += delta;

            // A substitution table with no lookups still walks every stage and
            // invokes its actions. Preserve that path for fonts with no table,
            // accounting for actions that insert or remove records.
            List<ShapePlanStageGroup<GSub.LookupTable>> groups = shapePlan.GetOrBuildGSubStageGroups();
            List<ShapingStage> stages = shapePlan.Stages;
            for (int g = 0; g < groups.Count; g++)
            {
                ShapePlanStageGroup<GSub.LookupTable> group = groups[g];

                collectionCount = buffer.Count;
                stages[group.Start].PreProcessFeature(shapePlan, buffer, index, count);

                delta = buffer.Count - collectionCount;
                i += delta;
                count += delta;

                collectionCount = buffer.Count;
                stages[group.End - 1].PostProcessFeature(shapePlan, buffer, index, count);

                delta = buffer.Count - collectionCount;
                i += delta;
                count += delta;
            }

            buffer.SegmentPlans.Add((index, count, script, shapePlan));
        }
    }
}
