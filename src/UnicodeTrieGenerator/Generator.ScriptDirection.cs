// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Text.RegularExpressions;
using SixLabors.Fonts.Unicode;

namespace UnicodeTrieGenerator;

/// <summary>
/// Generates the direction each script is written in when it is set horizontally.
/// </summary>
/// <remarks>
/// <para>
/// No Unicode data file states this. The bidirectional character types say which way
/// the characters of a script run, but a script's own direction is a property of the
/// script, and the standard publishes it only as prose. It is therefore read out of
/// the reference implementation: <c>hb_script_get_horizontal_direction</c> in
/// <c>hb-common.cc</c>, whose own comment points at the spreadsheet the list was
/// compiled from.
/// </para>
/// <para>
/// A script that may be written either way, such as Old Italic or Runic, is neither
/// left to right nor right to left, and the reference leaves such a run in the order
/// it arrived. That third answer is carried through rather than collapsed.
/// </para>
/// </remarks>
public static partial class Generator
{
    /// <summary>
    /// Matches the body of the function naming each script's direction.
    /// </summary>
    [GeneratedRegex(@"hb_direction_t\s+hb_script_get_horizontal_direction\s*\(hb_script_t\s+script\)\s*\{(?<body>.*?)\n\}", RegexOptions.Singleline)]
    private static partial Regex ScriptDirectionFunctionRegex();

    /// <summary>
    /// Matches either a script the function lists or the direction it returns for the
    /// scripts listed before it.
    /// </summary>
    [GeneratedRegex(@"case\s+HB_SCRIPT_(?<script>\w+)\s*:|return\s+HB_DIRECTION_(?<direction>\w+)\s*;")]
    private static partial Regex ScriptDirectionEntryRegex();

    /// <summary>
    /// Generates the script directions from the pinned reference implementation.
    /// </summary>
    private static void GenerateScriptDirectionData()
    {
        string source = File.ReadAllText(GetReferenceSourcePath("hb-common.cc"));

        Match function = ScriptDirectionFunctionRegex().Match(source);
        if (!function.Success)
        {
            throw new InvalidDataException("Found no script direction function in the reference implementation.");
        }

        // The function lists the scripts sharing a direction and then returns it, so
        // the scripts gathered since the last return are the ones it names.
        List<(ScriptClass Script, string Direction)> directions = [];
        List<ScriptClass> pending = [];

        foreach (Match match in ScriptDirectionEntryRegex().Matches(function.Groups["body"].Value))
        {
            if (match.Groups["script"].Success)
            {
                pending.Add(ReadScriptClass(match.Groups["script"].Value));
                continue;
            }

            string direction = match.Groups["direction"].Value;

            // The final return states the direction of every script the function does
            // not name, so it stands for no gathered script.
            foreach (ScriptClass script in pending)
            {
                directions.Add((script, direction));
            }

            pending.Clear();
        }

        if (pending.Count > 0)
        {
            throw new InvalidDataException("The script direction function names scripts it never returns a direction for.");
        }

        if (directions.Count == 0)
        {
            throw new InvalidDataException("Found no script directions in the reference implementation.");
        }

        directions.Sort(static (a, b) => string.CompareOrdinal(a.Script.ToString(), b.Script.ToString()));

        WriteScriptDirectionData(directions);
    }

    /// <summary>
    /// Reads a script named as the reference names it.
    /// </summary>
    /// <param name="name">The name, in the reference's own spelling.</param>
    /// <returns>The script.</returns>
    private static ScriptClass ReadScriptClass(string name)
    {
        string[] words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = string.Concat(words[i][..1], words[i][1..].ToLowerInvariant());
        }

        string candidate = string.Concat(words);
        if (!Enum.TryParse(candidate, false, out ScriptClass script))
        {
            throw new InvalidDataException($"The reference names the script '{name}', which reads as '{candidate}' and is not a known script.");
        }

        return script;
    }

    /// <summary>
    /// Writes the script directions.
    /// </summary>
    /// <param name="directions">Each script the reference names, with its direction.</param>
    private static void WriteScriptDirectionData(List<(ScriptClass Script, string Direction)> directions)
    {
        using FileStream fileStream = GetStreamWriter("ScriptDirectionData.Generated.cs");
        using StreamWriter writer = new(fileStream);

        writer.WriteLine("// Copyright (c) Six Labors.");
        writer.WriteLine("// Licensed under the Six Labors Split License.");
        writer.WriteLine();
        writer.WriteLine("// <auto-generated />");
        writer.WriteLine("namespace SixLabors.Fonts.Unicode.Resources");
        writer.WriteLine("{");
        writer.WriteLine("    /// <summary>");
        writer.WriteLine("    /// The direction each script is written in when it is set horizontally.");
        writer.WriteLine("    /// </summary>");
        writer.WriteLine("    internal static class ScriptDirectionData");
        writer.WriteLine("    {");
        writer.WriteLine("        /// <summary>");
        writer.WriteLine("        /// Gets the direction the given script is written in. A script the standard writes");
        writer.WriteLine("        /// left to right is not listed here and answers with that direction.");
        writer.WriteLine("        /// </summary>");
        writer.WriteLine("        /// <param name=\"script\">The script to look up.</param>");
        writer.WriteLine("        /// <returns>The direction the script is written in.</returns>");
        writer.WriteLine("        public static ScriptHorizontalDirection GetDirection(ScriptClass script)");
        writer.WriteLine("        {");
        writer.WriteLine("            switch (script)");
        writer.WriteLine("            {");

        foreach (string direction in new[] { "RTL", "INVALID" })
        {
            List<(ScriptClass Script, string Direction)> group = directions.FindAll(d => d.Direction == direction);
            if (group.Count == 0)
            {
                continue;
            }

            foreach ((ScriptClass script, string _) in group)
            {
                writer.WriteLine($"                case ScriptClass.{script}:");
            }

            string answer = direction == "RTL" ? "RightToLeft" : "Either";
            writer.WriteLine($"                    return ScriptHorizontalDirection.{answer};");
            writer.WriteLine();
        }

        writer.WriteLine("                default:");
        writer.WriteLine("                    return ScriptHorizontalDirection.LeftToRight;");
        writer.WriteLine("            }");
        writer.WriteLine("        }");
        writer.WriteLine("    }");
        writer.WriteLine("}");
    }
}
