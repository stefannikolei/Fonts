// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UnicodeTrieGenerator;

/// <summary>
/// Generates the classes that order a mark against the marks around it.
/// </summary>
/// <remarks>
/// <para>
/// These are the canonical combining classes with the classes of several scripts
/// renumbered, so that sorting by them leaves the marks of those scripts in the
/// order they are drawn. No Unicode data file derives the renumbering, so it is
/// read out of the reference implementation instead: the table
/// <c>_hb_modified_combining_class</c> in <c>hb-unicode.cc</c>, whose entries are
/// either plain numbers or the <c>HB_MODIFIED_COMBINING_CLASS_CCC*</c> macros
/// defined in <c>hb-unicode.hh</c>, together with the handful of characters that
/// <c>modified_combining_class</c> resolves ahead of the table.
/// </para>
/// <para>
/// Reading it rather than transcribing it means the pin is the single statement of
/// which reference version this library matches, and moving the pin regenerates the
/// table.
/// </para>
/// </remarks>
public static partial class Generator
{
    /// <summary>
    /// The path of the pinned reference implementation, relative to the solution.
    /// </summary>
    private const string ReferenceSubmoduleRelativePath = @"tests\harfbuzz";

    /// <summary>
    /// The number of canonical combining classes, which the standard bounds to one
    /// byte.
    /// </summary>
    private const int CombiningClassCount = 256;

    /// <summary>
    /// Matches one <c>HB_MODIFIED_COMBINING_CLASS_CCC*</c> definition, including the
    /// label the reference attaches to it.
    /// </summary>
    [GeneratedRegex(@"#define\s+HB_MODIFIED_COMBINING_CLASS_CCC(?<ccc>\d+)\s+(?<order>\d+)(?:[ \t]*/\*\s*(?<label>[^*]*?)\s*\*/)?")]
    private static partial Regex MarkOrderingMacroRegex();

    /// <summary>
    /// Matches one entry of the renumbering table, together with the heading that
    /// may precede it and the class name that may follow it on the same line.
    /// </summary>
    [GeneratedRegex(@"(?:/\*\s*(?<heading>[^*]*?)\s*\*/\s*)?(?<entry>HB_MODIFIED_COMBINING_CLASS_CCC\d+|\d+)\s*,(?:[ \t]*/\*\s*(?<name>[^*]*?)\s*\*/)?")]
    private static partial Regex MarkOrderingEntryRegex();

    /// <summary>
    /// Matches the body of the renumbering table.
    /// </summary>
    [GeneratedRegex(@"_hb_modified_combining_class\s*\[\s*256\s*\]\s*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline)]
    private static partial Regex MarkOrderingTableRegex();

    /// <summary>
    /// Matches one of the characters resolved ahead of the table.
    /// </summary>
    [GeneratedRegex(@"u\s*==\s*0x(?<codePoint>[0-9A-Fa-f]+)u\s*\)\s*\)\s*return\s+(?<order>\d+)\s*;")]
    private static partial Regex MarkOrderingOverrideRegex();

    /// <summary>
    /// Matches the body of the function that resolves the characters ordering by
    /// where they are drawn.
    /// </summary>
    [GeneratedRegex(@"modified_combining_class\s*\(hb_codepoint_t\s+u\).*?\}", RegexOptions.Singleline)]
    private static partial Regex MarkOrderingFunctionRegex();

    /// <summary>
    /// Matches the list of Arabic modifier combining marks.
    /// </summary>
    [GeneratedRegex(@"modifier_combining_marks\s*\[\s*\]\s*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline)]
    private static partial Regex ArabicModifierMarkListRegex();

    /// <summary>
    /// Matches one hexadecimal code point in a source list.
    /// </summary>
    [GeneratedRegex(@"0x(?<codePoint>[0-9A-Fa-f]+)u")]
    private static partial Regex SourceCodePointRegex();

    /// <summary>
    /// Matches a block comment, which the table uses to name its entries.
    /// </summary>
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentRegex();

    /// <summary>
    /// Brings the pinned reference implementation into the working tree, so the data
    /// read from it is the data the pin names.
    /// </summary>
    private static void UpdateReferenceSubmodule()
    {
        string solution = SolutionDirectoryFullPath;

        ProcessStartInfo startInfo = new("git", $"submodule update --init -- {ReferenceSubmoduleRelativePath}")
        {
            WorkingDirectory = solution,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new IOException("Unable to run git to update the reference submodule.");
        }

        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new IOException($"Updating the reference submodule failed: {error}");
        }
    }

    /// <summary>
    /// Generates the mark ordering classes from the pinned reference implementation.
    /// </summary>
    private static void GenerateMarkOrderingData()
    {
        UpdateReferenceSubmodule();

        string header = File.ReadAllText(GetReferenceSourcePath("hb-unicode.hh"));
        string source = File.ReadAllText(GetReferenceSourcePath("hb-unicode.cc"));
        string arabicShaper = File.ReadAllText(GetReferenceSourcePath("hb-ot-shaper-arabic.cc"));

        // The table's entries name these macros where a class is renumbered, and each
        // definition carries the label naming the mark it stands for.
        Dictionary<int, MarkOrderingMacro> macros = [];
        foreach (Match match in MarkOrderingMacroRegex().Matches(header))
        {
            macros[ParseInvariantInt(match.Groups["ccc"].Value)] = new MarkOrderingMacro(
                checked((byte)ParseInvariantInt(match.Groups["order"].Value)),
                match.Groups["label"].Value);
        }

        if (macros.Count == 0)
        {
            throw new InvalidDataException("Found no mark ordering macros in the reference implementation.");
        }

        Match table = MarkOrderingTableRegex().Match(source);
        if (!table.Success)
        {
            throw new InvalidDataException("Found no mark ordering table in the reference implementation.");
        }

        MarkOrderingEntry[] entries = ReadMarkOrderingTable(table.Groups["body"].Value, macros);

        // A few characters are resolved before the table is consulted, because they
        // order by where they are drawn rather than by their class.
        List<(int CodePoint, int Order)> overrides = [];
        Match function = MarkOrderingFunctionRegex().Match(header);
        if (!function.Success)
        {
            throw new InvalidDataException("Found no mark ordering overrides in the reference implementation.");
        }

        foreach (Match match in MarkOrderingOverrideRegex().Matches(function.Value))
        {
            overrides.Add((
                int.Parse(match.Groups["codePoint"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                ParseInvariantInt(match.Groups["order"].Value)));
        }

        if (overrides.Count == 0)
        {
            throw new InvalidDataException("Found no mark ordering overrides in the reference implementation.");
        }

        overrides.Sort(static (a, b) => a.CodePoint.CompareTo(b.CodePoint));

        Match modifierMarkList = ArabicModifierMarkListRegex().Match(arabicShaper);
        if (!modifierMarkList.Success)
        {
            throw new InvalidDataException("Found no Arabic modifier combining mark list in the reference implementation.");
        }

        List<int> modifierMarks = [];
        foreach (Match match in SourceCodePointRegex().Matches(modifierMarkList.Groups["body"].Value))
        {
            modifierMarks.Add(int.Parse(match.Groups["codePoint"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        if (modifierMarks.Count == 0)
        {
            throw new InvalidDataException("Found no Arabic modifier combining marks in the reference implementation.");
        }

        // The characters are named from the standard rather than by hand, so a
        // mistaken name cannot creep in.
        Dictionary<int, string> names = ReadCharacterNames(overrides.Select(static o => o.CodePoint).Concat(modifierMarks));

        WriteMarkOrderingData(entries, overrides, modifierMarks, names);
    }

    /// <summary>
    /// Reads the names of the given characters from the standard's character
    /// database.
    /// </summary>
    /// <param name="codePoints">The characters to name.</param>
    /// <returns>The name of each character found.</returns>
    private static Dictionary<int, string> ReadCharacterNames(IEnumerable<int> codePoints)
    {
        HashSet<int> wanted = [.. codePoints];
        Dictionary<int, string> names = [];

        using StreamReader reader = GetStreamReader("UnicodeData.txt");
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string[] parts = line.Split(';');
            if (parts.Length < 2)
            {
                continue;
            }

            int codePoint = ParseHexInt(parts[0]);
            if (wanted.Contains(codePoint))
            {
                names[codePoint] = parts[1];
            }
        }

        return names;
    }

    /// <summary>
    /// Reads the renumbering table's entries in order, resolving each either as a
    /// plain number or through the macro it names.
    /// </summary>
    /// <param name="body">The body of the table.</param>
    /// <param name="macros">The macro values, by the class they renumber.</param>
    /// <returns>The order of each canonical combining class.</returns>
    private static MarkOrderingEntry[] ReadMarkOrderingTable(string body, Dictionary<int, MarkOrderingMacro> macros)
    {
        MarkOrderingEntry[] entries = new MarkOrderingEntry[CombiningClassCount];
        int index = 0;

        foreach (Match match in MarkOrderingEntryRegex().Matches(body))
        {
            if (index >= CombiningClassCount)
            {
                throw new InvalidDataException("The mark ordering table holds more entries than there are combining classes.");
            }

            string entry = match.Groups["entry"].Value;

            // A heading precedes the first entry of a script's run; the reference
            // uses it to say which script the renumbering below it serves.
            string heading = match.Groups["heading"].Success ? match.Groups["heading"].Value : string.Empty;

            // A name follows an entry the reference chose to label.
            string name = match.Groups["name"].Success ? match.Groups["name"].Value : string.Empty;

            if (int.TryParse(entry, NumberStyles.Integer, CultureInfo.InvariantCulture, out int order))
            {
                entries[index++] = new MarkOrderingEntry(checked((byte)order), heading, ReadClassName(name));
                continue;
            }

            const string macroPrefix = "HB_MODIFIED_COMBINING_CLASS_CCC";
            if (!int.TryParse(entry[macroPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ccc)
                || !macros.TryGetValue(ccc, out MarkOrderingMacro macro))
            {
                throw new InvalidDataException($"Unable to read the mark ordering entry '{entry}'.");
            }

            if (ccc != index)
            {
                throw new InvalidDataException($"The mark ordering entry '{entry}' sits at index {index}.");
            }

            // A renumbered entry is labelled by its macro rather than in the table.
            entries[index++] = new MarkOrderingEntry(macro.Order, heading, macro.Label);
        }

        if (index != CombiningClassCount)
        {
            throw new InvalidDataException($"The mark ordering table holds {index} entries, expected {CombiningClassCount}.");
        }

        return entries;
    }

    /// <summary>
    /// Reads the class name the reference attaches to an unrenumbered entry, which it
    /// writes as the name of its own constant.
    /// </summary>
    /// <param name="name">The label, which may be empty.</param>
    /// <returns>The class name, or an empty string.</returns>
    private static string ReadClassName(string name)
    {
        const string classPrefix = "HB_UNICODE_COMBINING_CLASS_";
        if (!name.StartsWith(classPrefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // Rendered as the standard writes the property value rather than as a macro.
        string[] words = name[classPrefix.Length..].Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = string.Concat(words[i][..1], words[i][1..].ToLowerInvariant());
        }

        return string.Join('_', words);
    }

    /// <summary>
    /// Writes the mark ordering classes and the characters resolved ahead of them.
    /// </summary>
    /// <param name="entries">The order of each canonical combining class, annotated.</param>
    /// <param name="overrides">The characters resolved ahead of the table.</param>
    /// <param name="modifierMarks">The Arabic modifier combining marks.</param>
    /// <param name="names">The standard's name for each emitted character.</param>
    private static void WriteMarkOrderingData(MarkOrderingEntry[] entries, List<(int CodePoint, int Order)> overrides, List<int> modifierMarks, Dictionary<int, string> names)
    {
        using FileStream fileStream = GetStreamWriter("MarkOrderingData.Generated.cs");
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
        writer.WriteLine("    /// The classes that order a mark against the marks around it.");
        writer.WriteLine("    /// </summary>");
        writer.WriteLine("    internal static class MarkOrderingData");
        writer.WriteLine("    {");

        writer.WriteLine("        /// <summary>");
        writer.WriteLine("        /// Gets the order given to the canonical combining class at each index. Every");
        writer.WriteLine("        /// class orders as itself except where a script draws its marks in an order its");
        writer.WriteLine("        /// assigned classes do not give, and those are renumbered so that one sort");
        writer.WriteLine("        /// leaves the marks of every script in the order they are drawn.");
        writer.WriteLine("        /// </summary>");
        writer.WriteLine("        public static ReadOnlySpan<byte> Classes => new byte[]");
        writer.WriteLine("        {");

        // Laid out as the reference lays its own table out: a labelled entry stands on
        // its own line carrying that label, and the runs between them are grouped.
        List<int> run = [];
        for (int i = 0; i < entries.Length; i++)
        {
            MarkOrderingEntry entry = entries[i];

            if (entry.Heading.Length > 0 || entry.Label.Length > 0)
            {
                FlushMarkOrderingRun(writer, entries, run);
            }

            if (entry.Heading.Length > 0)
            {
                writer.WriteLine();
                writer.WriteLine($"            // {entry.Heading}.");
            }

            if (entry.Label.Length > 0)
            {
                writer.WriteLine($"            {entry.Order},  // ccc {i}, {entry.Label}");
                continue;
            }

            run.Add(i);
        }

        FlushMarkOrderingRun(writer, entries, run);

        writer.WriteLine("        };");
        writer.WriteLine();

        writer.WriteLine("        /// <summary>");
        writer.WriteLine("        /// Tries to get the order of a character that orders by where it is drawn rather");
        writer.WriteLine("        /// than by its class.");
        writer.WriteLine("        /// </summary>");
        writer.WriteLine("        /// <remarks>");
        writer.WriteLine("        /// Written as a switch rather than a table because there are only a handful, and");
        writer.WriteLine("        /// because a span of anything wider than a byte is a fresh array on every access:");
        writer.WriteLine("        /// a table here would allocate once per character shaped.");
        writer.WriteLine("        /// </remarks>");
        writer.WriteLine("        /// <param name=\"codePoint\">The code point to look up.</param>");
        writer.WriteLine("        /// <param name=\"order\">When this method returns, contains the order.</param>");
        writer.WriteLine("        /// <returns><see langword=\"true\"/> when the character orders by where it is drawn.</returns>");
        writer.WriteLine("        public static bool TryGetOverride(uint codePoint, out byte order)");
        writer.WriteLine("        {");
        writer.WriteLine("            switch (codePoint)");
        writer.WriteLine("            {");

        foreach ((int codePoint, int order) in overrides)
        {
            string name = names.TryGetValue(codePoint, out string? found) ? found : "unnamed";
            writer.WriteLine($"                // {name}");
            writer.WriteLine($"                case 0x{codePoint:X4}:");
            writer.WriteLine($"                    order = {order};");
            writer.WriteLine("                    return true;");
            writer.WriteLine();
        }

        writer.WriteLine("                default:");
        writer.WriteLine("                    order = 0;");
        writer.WriteLine("                    return false;");
        writer.WriteLine("            }");
        writer.WriteLine("        }");
        writer.WriteLine();

        writer.WriteLine("        /// <summary>");
        writer.WriteLine("        /// Determines whether an Arabic mark modifies the combining mark that follows it.");
        writer.WriteLine("        /// </summary>");
        writer.WriteLine("        /// <param name=\"codePoint\">The code point to test.</param>");
        writer.WriteLine("        /// <returns><see langword=\"true\"/> when the code point is a modifier combining mark; otherwise, <see langword=\"false\"/>.</returns>");
        writer.WriteLine("        public static bool IsArabicModifierCombiningMark(uint codePoint)");
        writer.WriteLine("            => codePoint is");
        for (int i = 0; i < modifierMarks.Count; i++)
        {
            int codePoint = modifierMarks[i];
            string suffix = i == modifierMarks.Count - 1 ? ";" : " or";
            writer.WriteLine($"                0x{codePoint:X4}{suffix} // {names[codePoint]}");
        }

        writer.WriteLine("    }");
        writer.WriteLine("}");
    }

    /// <summary>
    /// Writes a run of consecutive entries the reference left unlabelled, ten to a
    /// line, and empties the run.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="entries">The entries.</param>
    /// <param name="run">The indices of the run, emptied by this method.</param>
    private static void FlushMarkOrderingRun(StreamWriter writer, MarkOrderingEntry[] entries, List<int> run)
    {
        const int perLine = 10;

        for (int i = 0; i < run.Count; i += perLine)
        {
            writer.Write("            ");

            int last = Math.Min(i + perLine, run.Count) - 1;
            for (int j = i; j <= last; j++)
            {
                writer.Write($"{entries[run[j]].Order}, ");
            }

            writer.WriteLine($" // ccc {run[i]}..{run[last]}");
        }

        run.Clear();
    }

    /// <summary>
    /// Resolves a path within the pinned reference implementation's sources.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <returns>The full path.</returns>
    private static string GetReferenceSourcePath(string fileName)
        => GetFullPath(Path.Combine(ReferenceSubmoduleRelativePath, "src", fileName));

    /// <summary>
    /// Parses a decimal integer written in the invariant culture.
    /// </summary>
    /// <param name="value">The text to parse.</param>
    /// <returns>The value.</returns>
    private static int ParseInvariantInt(string value)
        => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// <summary>
    /// One entry of the renumbering table, with whatever the reference says about it.
    /// </summary>
    /// <param name="Order">The order given to the class at this index.</param>
    /// <param name="Heading">The heading opening this entry's run, or an empty string.</param>
    /// <param name="Label">What the entry is, or an empty string.</param>
    private readonly record struct MarkOrderingEntry(byte Order, string Heading, string Label);

    /// <summary>
    /// One renumbering macro, with the label the reference attaches to it.
    /// </summary>
    /// <param name="Order">The order the macro gives.</param>
    /// <param name="Label">What the class is.</param>
    private readonly record struct MarkOrderingMacro(byte Order, string Label);
}
