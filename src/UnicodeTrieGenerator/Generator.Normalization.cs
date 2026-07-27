// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;
using SixLabors.Fonts.Unicode;

namespace UnicodeTrieGenerator;

/// <summary>
/// Generates the canonical combining classes and the canonical decomposition and
/// composition tables used when normalizing text before it is shaped.
/// </summary>
/// <remarks>
/// <para>
/// Inputs, both from the Unicode Character Database 17.0.0: the combining classes
/// come from field 3 of <c>UnicodeData.txt</c> and the canonical mappings from
/// field 5 of the same file, and the characters excluded from composition come
/// from <c>CompositionExclusions.txt</c>.
/// </para>
/// <para>
/// Which pairs are allowed to recompose follows the reference implementation: the
/// <c>dm2</c> table built by <c>gen-ucd-table.py</c> in HarfBuzz 14.2.1 stores a
/// composite of zero, meaning the pair does not recompose, whenever the composite
/// carries <c>Full_Composition_Exclusion</c> or its own combining class is
/// non-zero. The exclusion property is derived here from the exclusions file
/// together with the singleton and non-starter mappings, as UAX #15 specifies.
/// </para>
/// </remarks>
public static partial class Generator
{
    /// <summary>
    /// The number of bits each code point occupies in a packed table entry. A
    /// scalar value never exceeds U+10FFFF, so three of them fit in one 64-bit
    /// entry with the search key in the high bits, leaving the entries sortable
    /// and searchable as plain integers.
    /// </summary>
    private const int NormalizationEntryShift = 21;

    /// <summary>
    /// The first Hangul syllable. Syllables decompose and compose by arithmetic
    /// rather than by table, so they are left out of the tables entirely.
    /// </summary>
    private const int HangulSyllableBase = 0xAC00;

    /// <summary>
    /// The number of Hangul syllables.
    /// </summary>
    private const int HangulSyllableCount = 11172;

    /// <summary>
    /// The documentation written above the decomposition table, one line per line.
    /// </summary>
    private static readonly string[] DecompositionsSummary =
    [
        "Gets the canonical decompositions, ordered by the character that decomposes so that the table can be searched by it.",
        "<para>",
        "Each entry packs three scalar values into 21 bits each, most significant first: the character that decomposes,",
        "the first character it decomposes to, and the second. A singleton decomposition leaves the second character zero.",
        "</para>",
        "<para>",
        "Hangul syllables are absent: they decompose by arithmetic. A compatibility decomposition is absent as well,",
        "because normalization does not use one.",
        "</para>"
    ];

    /// <summary>
    /// The documentation written above the composition table, one line per line.
    /// </summary>
    private static readonly string[] CompositionsSummary =
    [
        "Gets the canonical compositions, ordered by the pair of characters that join so that the table can be searched by the pair.",
        "<para>",
        "Each entry packs three scalar values into 21 bits each, most significant first: the first character of the pair,",
        "the second, and the character they compose to.",
        "</para>",
        "<para>",
        "A pair appears only when it may be recomposed after being taken apart. A character excluded from composition,",
        "one whose own combining class is non-zero, and one whose decomposition begins with a combining mark are all left",
        "out, so composing never contradicts the decomposition it reverses. Hangul syllables compose by arithmetic and are",
        "absent for that reason.",
        "</para>"
    ];

    /// <summary>
    /// Generates the combining class trie and the normalization tables.
    /// </summary>
    private static void GenerateNormalizationData()
    {
        Dictionary<int, int> combiningClasses = [];
        Dictionary<int, int[]> canonicalDecompositions = [];

        using (StreamReader sr = GetStreamReader("UnicodeData.txt"))
        {
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                string[] parts = line.Split(';');
                if (parts.Length < 6)
                {
                    continue;
                }

                int codePoint = ParseHexInt(parts[0]);

                if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int combiningClass)
                    && combiningClass != 0)
                {
                    combiningClasses[codePoint] = combiningClass;
                }

                // Field 5 holds the decomposition mapping. A mapping introduced by a
                // <tag> is a compatibility mapping, which normalization does not use.
                string mapping = parts[5];
                if (mapping.Length == 0 || mapping[0] == '<')
                {
                    continue;
                }

                // Hangul syllables carry canonical mappings that the arithmetic
                // rules already describe.
                if (codePoint >= HangulSyllableBase && codePoint < HangulSyllableBase + HangulSyllableCount)
                {
                    continue;
                }

                canonicalDecompositions[codePoint] = mapping
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(ParseHexInt)
                    .ToArray();
            }
        }

        HashSet<int> exclusions = [];
        using (StreamReader sr = GetStreamReader("CompositionExclusions.txt"))
        {
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                int comment = line.IndexOf('#');
                string body = (comment >= 0 ? line[..comment] : line).Trim();
                if (body.Length == 0)
                {
                    continue;
                }

                exclusions.Add(ParseHexInt(body));
            }
        }

        UnicodeTrieBuilder builder = new(0);
        foreach (KeyValuePair<int, int> pair in combiningClasses)
        {
            builder.Set(pair.Key, (uint)pair.Value);
        }

        GenerateTrieClass("CanonicalCombiningClass", builder.Freeze());

        List<ulong> decompositions = [];
        List<ulong> compositions = [];

        foreach (KeyValuePair<int, int[]> pair in canonicalDecompositions)
        {
            int composite = pair.Key;
            int[] mapping = pair.Value;

            int first = mapping[0];
            int second = mapping.Length > 1 ? mapping[1] : 0;

            decompositions.Add(Pack(composite, first, second));

            if (mapping.Length != 2)
            {
                continue;
            }

            // A pair recomposes unless the composite is excluded from composition,
            // its own combining class is non-zero, or the pair begins with a
            // character that is itself a combining mark.
            combiningClasses.TryGetValue(composite, out int compositeClass);
            combiningClasses.TryGetValue(first, out int firstClass);

            if (exclusions.Contains(composite) || compositeClass != 0 || firstClass != 0)
            {
                continue;
            }

            compositions.Add(Pack(first, second, composite));
        }

        decompositions.Sort();
        compositions.Sort();

        WriteNormalizationTables(decompositions, compositions);
    }

    /// <summary>
    /// Packs three scalar values into one entry, with the search key first so the
    /// packed entries sort in key order.
    /// </summary>
    /// <param name="key">The value searched for.</param>
    /// <param name="second">The second value.</param>
    /// <param name="third">The third value.</param>
    /// <returns>The packed entry.</returns>
    private static ulong Pack(int key, int second, int third)
        => ((ulong)key << (NormalizationEntryShift * 2))
        | ((ulong)second << NormalizationEntryShift)
        | (uint)third;

    /// <summary>
    /// Writes the canonical decomposition and composition tables.
    /// </summary>
    /// <param name="decompositions">The decompositions, ordered by composite.</param>
    /// <param name="compositions">The compositions, ordered by the pair they join.</param>
    private static void WriteNormalizationTables(List<ulong> decompositions, List<ulong> compositions)
    {
        using FileStream fileStream = GetStreamWriter("NormalizationData.Generated.cs");
        using StreamWriter writer = new(fileStream);

        writer.WriteLine("// Copyright (c) Six Labors.");
        writer.WriteLine("// Licensed under the Six Labors Split License.");
        writer.WriteLine();
        writer.WriteLine("// <auto-generated />");
        writer.WriteLine("using System;");
        writer.WriteLine();
        writer.WriteLine("namespace SixLabors.Fonts.Unicode.Resources");
        writer.WriteLine("{");
        writer.WriteLine("    /// <summary>");
        writer.WriteLine("    /// The canonical decomposition and composition tables used when normalizing");
        writer.WriteLine("    /// text before it is shaped.");
        writer.WriteLine("    /// </summary>");
        writer.WriteLine("    internal static class NormalizationData");
        writer.WriteLine("    {");

        WriteEntries(writer, "Decompositions", decompositions, DecompositionsSummary);

        writer.WriteLine();

        WriteEntries(writer, "Compositions", compositions, CompositionsSummary);

        writer.WriteLine("    }");
        writer.WriteLine("}");
    }

    /// <summary>
    /// Writes one packed table as a documented read-only span of bytes.
    /// </summary>
    /// <remarks>
    /// The entries are written as bytes rather than as the 64-bit values they encode
    /// because only a span of a one-byte type becomes a blob in the assembly's
    /// read-only data. A span of a wider type is a fresh array on every access, which
    /// would allocate once per lookup.
    /// </remarks>
    /// <param name="writer">The writer.</param>
    /// <param name="name">The name of the table.</param>
    /// <param name="entries">The packed entries.</param>
    /// <param name="summary">The lines of the documentation summary.</param>
    private static void WriteEntries(StreamWriter writer, string name, List<ulong> entries, string[] summary)
    {
        writer.WriteLine("        /// <summary>");

        foreach (string line in summary)
        {
            writer.WriteLine($"        /// {line}");
        }

        writer.WriteLine("        /// <para>");
        writer.WriteLine("        /// Each entry occupies eight bytes, least significant first, and is read with");
        writer.WriteLine("        /// <see cref=\"System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian\"/>.");
        writer.WriteLine("        /// </para>");
        writer.WriteLine("        /// </summary>");
        writer.WriteLine($"        public static ReadOnlySpan<byte> {name} => new byte[]");
        writer.WriteLine("        {");

        for (int i = 0; i < entries.Count; i++)
        {
            writer.Write("            ");

            ulong value = entries[i];
            for (int b = 0; b < sizeof(ulong); b++)
            {
                writer.Write($"0x{(byte)(value >> (b * 8)):X2}, ");
            }

            writer.WriteLine($" // 0x{value:X}");
        }

        writer.WriteLine("        };");
    }
}
