// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SixLabors.Fonts.Unicode;

namespace UnicodeTrieGenerator;

/// <content>
/// Generates the character sequences that spell one vowel but read as another,
/// which a shaper separates with a dotted circle rather than rendering.
/// </content>
public static partial class Generator
{
    /// <summary>
    /// The longest sequence the generated lookups recognise. A sequence is
    /// found from the character that begins it and the one or two that follow,
    /// so a longer rule has no form to be emitted in.
    /// </summary>
    private const int MaxConstraintLength = 3;

    /// <summary>
    /// Generates the vowel constraint data from the invalid cluster rules.
    /// Each rule lists the characters of a sequence that must not be shaped as
    /// written; the shaper recognises a sequence and places a dotted circle
    /// before its final character.
    /// </summary>
    public static void GenerateVowelConstraints()
    {
        List<int[]> sequences = ReadConstraintSequences();

        // A sequence is recognised by the script of the character that begins
        // it, which is the script the text must be in for the rule to hold.
        Dictionary<int, ScriptClass> scriptsByCodePoint = ReadScriptNames(sequences.Select(s => s[0]));

        Dictionary<ScriptClass, List<int[]>> byScript = [];
        foreach (int[] sequence in sequences)
        {
            ScriptClass script = scriptsByCodePoint[sequence[0]];
            if (!byScript.TryGetValue(script, out List<int[]>? forScript))
            {
                forScript = [];
                byScript[script] = forScript;
            }

            forScript.Add(sequence);
        }

        List<(ScriptClass Script, int[] Sequence)> constraints = [];
        foreach ((ScriptClass script, List<int[]> forScript) in byScript)
        {
            foreach (int[] sequence in PruneToShortest(forScript))
            {
                if (sequence.Length > MaxConstraintLength)
                {
                    throw new InvalidOperationException(
                        $"The sequence {string.Join(' ', sequence.Select(c => c.ToString("X4", CultureInfo.InvariantCulture)))} is longer than the {MaxConstraintLength} characters a lookup recognises.");
                }

                constraints.Add((script, sequence));
            }
        }

        List<ScriptClass> scripts = [.. byScript.Keys.OrderBy(s => s.ToString(), StringComparer.Ordinal)];

        List<(ulong Key, int Final)> pairs = [];
        List<(ulong Key, int Final)> triples = [];
        foreach ((ScriptClass script, int[] sequence) in constraints)
        {
            (ulong Key, int Final) entry = (PackKey(script, sequence[0], sequence[1]), sequence.Length == MaxConstraintLength ? sequence[2] : 0);

            if (sequence.Length == MaxConstraintLength)
            {
                triples.Add(entry);
            }
            else
            {
                pairs.Add(entry);
            }
        }

        pairs.Sort((x, y) => x.Key.CompareTo(y.Key));
        triples.Sort((x, y) => x.Key != y.Key ? x.Key.CompareTo(y.Key) : x.Final.CompareTo(y.Final));

        string source = BuildVowelConstraintSource(scripts, pairs, triples);

        string path = GetFullPath(Path.Combine(OutputResourcesRelativePath, "VowelConstraintData.Generated.cs"));
        File.WriteAllText(path, source);
    }

    /// <summary>
    /// Reads the prohibited sequences, one per rule.
    /// </summary>
    /// <returns>The characters of each sequence, in order.</returns>
    private static List<int[]> ReadConstraintSequences()
    {
        List<int[]> sequences = [];
        using StreamReader sr = GetStreamReader("IndicShapingInvalidCluster.txt");

        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            int comment = line.IndexOf('#', StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment];
            }

            int terminator = line.IndexOf(';', StringComparison.Ordinal);
            if (terminator >= 0)
            {
                line = line[..terminator];
            }

            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            sequences.Add([.. fields.Select(f => int.Parse(f, NumberStyles.HexNumber, CultureInfo.InvariantCulture))]);
        }

        return sequences;
    }

    /// <summary>
    /// Drops every sequence that begins with a shorter sequence of the same
    /// script. The shorter one is recognised first and consumes the characters
    /// the longer one would need, so the longer one can never be reached.
    /// </summary>
    /// <param name="sequences">The sequences of one script.</param>
    /// <returns>The sequences that remain reachable.</returns>
    private static List<int[]> PruneToShortest(List<int[]> sequences)
    {
        List<int[]> kept = [];
        foreach (int[] sequence in sequences)
        {
            bool shadowed = false;
            foreach (int[] other in sequences)
            {
                if (other.Length >= sequence.Length)
                {
                    continue;
                }

                if (sequence.Take(other.Length).SequenceEqual(other))
                {
                    shadowed = true;
                    break;
                }
            }

            if (!shadowed)
            {
                kept.Add(sequence);
            }
        }

        return kept;
    }

    /// <summary>
    /// Packs a script name and the first two characters of a sequence into the
    /// ordered key the generated lookups search.
    /// </summary>
    /// <param name="script">The script the sequence belongs to.</param>
    /// <param name="first">The character that begins the sequence.</param>
    /// <param name="second">The character that follows it.</param>
    /// <returns>The packed key.</returns>
    private static ulong PackKey(ScriptClass script, int first, int second)
        => ((ulong)script << 42) | ((ulong)(uint)first << 21) | (uint)second;

    /// <summary>
    /// Builds the source of the generated lookup class.
    /// </summary>
    /// <param name="scripts">The scripts that carry at least one sequence.</param>
    /// <param name="pairs">The two character sequences, ordered by key.</param>
    /// <param name="triples">The three character sequences, ordered by key then final character.</param>
    /// <returns>The source text.</returns>
    private static string BuildVowelConstraintSource(
        List<ScriptClass> scripts,
        List<(ulong Key, int Final)> pairs,
        List<(ulong Key, int Final)> triples)
    {
        StringBuilder sb = new();
        sb.AppendLine("// Copyright (c) Six Labors.");
        sb.AppendLine("// Licensed under the Six Labors Split License.");
        sb.AppendLine();
        sb.AppendLine("namespace SixLabors.Fonts.Unicode.Resources;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// The character sequences that spell one vowel but read as another. A font is");
        sb.AppendLine("/// not asked to render such a sequence: a dotted circle is placed before its");
        sb.AppendLine("/// final character so the sequence cannot be mistaken for the vowel it");
        sb.AppendLine("/// imitates. Generated from IndicShapingInvalidCluster.txt.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("internal static class VowelConstraintData");
        sb.AppendLine("{");

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// The two character sequences as packed keys, in ascending order so a");
        sb.AppendLine("    /// lookup is a binary search.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    private static readonly ulong[] PairData =");
        sb.AppendLine("    [");
        foreach ((ulong key, int _) in pairs)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"        0x{key:X}UL,");
        }

        sb.AppendLine("    ];");
        sb.AppendLine();

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// The first two characters of each three character sequence, packed and");
        sb.AppendLine("    /// ordered as the pairs are.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    private static readonly ulong[] TripleData =");
        sb.AppendLine("    [");
        foreach ((ulong key, int _) in triples)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"        0x{key:X}UL,");
        }

        sb.AppendLine("    ];");
        sb.AppendLine();

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// The final character of each three character sequence, positioned as its");
        sb.AppendLine("    /// packed key is.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    private static readonly int[] TripleFinalData =");
        sb.AppendLine("    [");
        foreach ((ulong _, int final) in triples)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"        0x{final:X4},");
        }

        sb.AppendLine("    ];");
        sb.AppendLine();

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Determines whether any sequence is written in the given script. Text in");
        sb.AppendLine("    /// any other script carries no constrained sequence and is left alone.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    /// <param name=\"script\">The script the text is written in.</param>");
        sb.AppendLine("    /// <returns><see langword=\"true\"/> when the script carries sequences.</returns>");
        sb.AppendLine("    public static bool IsConstrainedScript(ScriptClass script)");
        sb.AppendLine("        => script switch");
        sb.AppendLine("        {");
        foreach (ScriptClass script in scripts)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"            ScriptClass.{script} => true,");
        }

        sb.AppendLine("            _ => false,");
        sb.AppendLine("        };");
        sb.AppendLine();

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Determines whether the two characters spell a constrained sequence.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    /// <param name=\"script\">The script the text is written in.</param>");
        sb.AppendLine("    /// <param name=\"first\">The character that begins the sequence.</param>");
        sb.AppendLine("    /// <param name=\"second\">The character that follows it.</param>");
        sb.AppendLine("    /// <returns><see langword=\"true\"/> when the two are constrained.</returns>");
        sb.AppendLine("    public static bool IsConstrainedPair(ScriptClass script, int first, int second)");
        sb.AppendLine("        => Array.BinarySearch(PairData, Key(script, first, second)) >= 0;");
        sb.AppendLine();

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Determines whether the three characters spell a constrained sequence.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    /// <param name=\"script\">The script the text is written in.</param>");
        sb.AppendLine("    /// <param name=\"first\">The character that begins the sequence.</param>");
        sb.AppendLine("    /// <param name=\"second\">The character that follows it.</param>");
        sb.AppendLine("    /// <param name=\"third\">The character that ends it.</param>");
        sb.AppendLine("    /// <returns><see langword=\"true\"/> when the three are constrained.</returns>");
        sb.AppendLine("    public static bool IsConstrainedTriple(ScriptClass script, int first, int second, int third)");
        sb.AppendLine("    {");
        sb.AppendLine("        ulong key = Key(script, first, second);");
        sb.AppendLine("        int index = Array.BinarySearch(TripleData, key);");
        sb.AppendLine("        if (index < 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            return false;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        // Sequences sharing their first two characters sit together, so the");
        sb.AppendLine("        // run around the found position holds every candidate final character.");
        sb.AppendLine("        ReadOnlySpan<ulong> keys = TripleData;");
        sb.AppendLine("        ReadOnlySpan<int> finals = TripleFinalData;");
        sb.AppendLine("        int start = index;");
        sb.AppendLine("        while (start > 0 && keys[start - 1] == key)");
        sb.AppendLine("        {");
        sb.AppendLine("            start--;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        for (int i = start; i < keys.Length && keys[i] == key; i++)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (finals[i] == third)");
        sb.AppendLine("            {");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Packs a script and two characters into one ordered key.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    /// <param name=\"script\">The script the text is written in.</param>");
        sb.AppendLine("    /// <param name=\"first\">The character that begins the sequence.</param>");
        sb.AppendLine("    /// <param name=\"second\">The character that follows it.</param>");
        sb.AppendLine("    /// <returns>The packed key.</returns>");
        sb.AppendLine("    private static ulong Key(ScriptClass script, int first, int second)");
        sb.AppendLine("        => ((ulong)script << 42) | ((ulong)(uint)first << 21) | (uint)second;");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Reads the script each of the given characters belongs to.
    /// </summary>
    /// <param name="codePoints">The characters to resolve.</param>
    /// <returns>The script name for each character, keyed by character.</returns>
    private static Dictionary<int, ScriptClass> ReadScriptNames(IEnumerable<int> codePoints)
    {
        HashSet<int> wanted = [.. codePoints];
        Dictionary<int, ScriptClass> resolved = [];
        Regex regex = UnicodePropertyRowRegex();

        using StreamReader sr = GetStreamReader("Scripts.txt");
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            Match match = regex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            string start = match.Groups[1].Value;
            string end = match.Groups[2].Value;
            string script = match.Groups[3].Value;
            if (string.IsNullOrEmpty(end))
            {
                end = start;
            }

            int from = ParseHexInt(start);
            int to = ParseHexInt(end);
            foreach (int codePoint in wanted)
            {
                if (codePoint >= from && codePoint <= to)
                {
                    resolved[codePoint] = ScriptMap[script];
                }
            }
        }

        return resolved;
    }
}
