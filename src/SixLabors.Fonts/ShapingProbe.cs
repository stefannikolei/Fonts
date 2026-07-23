// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

// TEMPORARY diagnostic probe for shaping performance attribution. Delete before commit.
using System.Diagnostics;

namespace SixLabors.Fonts;

#pragma warning disable CS1591, SA1600, SA1401, CA2211, SA1201

public static class ShapingProbe
{
    public const int BuildTextRuns = 0;
    public const int Bidi = 1;
    public const int Populate = 2;
    public const int Mirrors = 3;
    public const int Substitution = 4;
    public const int MetricsAdd = 5;
    public const int Positioning = 6;
    public const int Projection = 7;
    public const int LookupResolve = 8;

    private static readonly string[] Names =
    [
        "BuildTextRuns",
        "Bidi",
        "Populate (glyph ids)",
        "Bidi mirrors",
        "GSUB substitution",
        "Metrics add (clones)",
        "GPOS positioning",
        "Projection (ShapedGlyph)",
        "  of which lookup resolve",
    ];

    private static readonly long[] Ticks = new long[Names.Length];
    private static readonly long[] Bytes = new long[Names.Length];

    public static bool Enabled { get; set; }

    public static long StageFeatureCalls;
    public static long LookupsConsidered;
    public static long LookupsSkippedByDigest;
    public static long GlyphGateChecks;
    public static long SubstitutionAttempts;

    public static void PrintCounters(int iterations)
        => Console.WriteLine(
            $"stages/op={StageFeatureCalls / (double)iterations:F1} " +
            $"lookups/op={LookupsConsidered / (double)iterations:F1} " +
            $"digestSkipped/op={LookupsSkippedByDigest / (double)iterations:F1} " +
            $"glyphGates/op={GlyphGateChecks / (double)iterations:F1} " +
            $"substAttempts/op={SubstitutionAttempts / (double)iterations:F1}");

    public static void ResetCounters()
        => StageFeatureCalls = LookupsConsidered = LookupsSkippedByDigest = GlyphGateChecks = SubstitutionAttempts = 0;

    public static (long Ticks, long Bytes) Enter()
        => Enabled ? (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread()) : default;

    public static void Exit(int phase, (long Ticks, long Bytes) entry)
    {
        if (!Enabled)
        {
            return;
        }

        Bytes[phase] += GC.GetAllocatedBytesForCurrentThread() - entry.Bytes;
        Ticks[phase] += Stopwatch.GetTimestamp() - entry.Ticks;
    }

    public static void Reset()
    {
        Array.Clear(Ticks);
        Array.Clear(Bytes);
    }

    public static void Print(int iterations)
    {
        long totalTicks = 0;
        long totalBytes = 0;
        for (int i = 0; i < Names.Length; i++)
        {
            totalTicks += Ticks[i];
            totalBytes += Bytes[i];
        }

        Console.WriteLine($"{"Phase",-26} {"us/op",10} {"%time",7} {"B/op",10} {"%alloc",7}");
        for (int i = 0; i < Names.Length; i++)
        {
            double us = Ticks[i] * 1_000_000.0 / Stopwatch.Frequency / iterations;
            double bytes = (double)Bytes[i] / iterations;
            Console.WriteLine($"{Names[i],-26} {us,10:F2} {(totalTicks > 0 ? Ticks[i] * 100.0 / totalTicks : 0),6:F1}% {bytes,10:F0} {(totalBytes > 0 ? Bytes[i] * 100.0 / totalBytes : 0),6:F1}%");
        }

        Console.WriteLine($"{"TOTAL",-26} {totalTicks * 1_000_000.0 / Stopwatch.Frequency / iterations,10:F2}         {(double)totalBytes / iterations,10:F0}");
    }
}
