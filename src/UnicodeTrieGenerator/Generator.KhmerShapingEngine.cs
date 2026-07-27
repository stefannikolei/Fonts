// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using UnicodeTrieGenerator.StateAutomation;
using KhmerCategories = SixLabors.Fonts.Unicode.Resources.IndicShapingData.KhmerCategories;

namespace UnicodeTrieGenerator;

/// <content>
/// Contains code to generate the Khmer syllable state machine.
/// </content>
public static partial class Generator
{
    /// <summary>
    /// Generates the Khmer syllable state machine from its grammar.
    /// </summary>
    /// <remarks>
    /// The grammar and category alphabet are transcribed from HarfBuzz 14.2.1, <c>src/hb-ot-shaper-khmer-machine.rl</c>, symbol <c>khmer_syllable_machine</c>. They are not derivable from the Unicode Character Database.
    /// </remarks>
    private static void GenerateKhmerShapingData()
    {
        KhmerCategories[] categories = Enum.GetValues<KhmerCategories>();
        Dictionary<string, int> symbols = new(categories.Length);
        int id = 0;

        foreach (KhmerCategories category in categories)
        {
            symbols[category.ToString()] = id++;
        }

        StateMachine machine = GetStateMachine("khmer", symbols);

        GenerateDataClass("KhmerShaping", null, null, machine, false);
    }
}
