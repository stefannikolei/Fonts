// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// Resolves a feature tag to its lookups for one layout table while a shape plan is
/// being built. Runs only at plan build; application reads the plan's prebuilt
/// lists.
/// </summary>
/// <typeparam name="TLookup">The layout table's lookup type.</typeparam>
/// <param name="featureTag">The feature tag to resolve.</param>
/// <param name="lookups">When this method returns, contains the resolved lookups if any.</param>
/// <returns><see langword="true"/> if the feature resolved to at least one lookup.</returns>
internal delegate bool ShapePlanLookupResolver<TLookup>(in Tag featureTag, out List<(Tag Feature, ushort Index, TLookup LookupTable)>? lookups);
