// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

#if OS_WINDOWS
using System.Security.Principal;
using BenchmarkDotNet.Diagnostics.Windows;
#endif
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;

namespace SixLabors.Fonts.Benchmarks;

public class Config : ManualConfig
{
    public Config()
    {
        this.AddLogger(ConsoleLogger.Default);
        this.AddColumnProvider(DefaultColumnProviders.Instance);
        this.AddExporter(MarkdownExporter.GitHub, DefaultExporters.Html, DefaultExporters.Csv);
        this.AddDiagnoser(MemoryDiagnoser.Default);

#if OS_WINDOWS
        if (this.IsElevated)
        {
            this.AddDiagnoser(new NativeMemoryProfiler());
        }
#endif

        this.SummaryStyle = SummaryStyle.Default.WithMaxParameterColumnWidth(50);
    }

    /// <summary>
    /// Gets the core runtime matching the host process, so running the suite
    /// from a given target framework benchmarks that same runtime.
    /// </summary>
    private static CoreRuntime HostRuntime => Environment.Version.Major switch
    {
        10 => CoreRuntime.Core10_0,
        _ => CoreRuntime.Core80,
    };

    public class Standard : Config
    {
        public Standard() => this.AddJob(
                Job.Default.WithRuntime(HostRuntime).WithArguments([new MsBuildArgument("/p:DebugType=portable")]));
    }

    public class Short : Config
    {
        public Short() => this.AddJob(
                Job.Default.WithRuntime(HostRuntime)
                           .WithLaunchCount(1)
                           .WithWarmupCount(3)
                           .WithIterationCount(3)
                           .WithArguments([new MsBuildArgument("/p:DebugType=portable")]));
    }

    /// <summary>
    /// The configuration for a benchmark used to gate a change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three iterations, as <see cref="Short"/> uses, cannot gate anything. The
    /// reported error is half of a 99.9% confidence interval, whose width is the
    /// standard deviation times a factor that depends on the number of iterations:
    /// at three iterations that factor is about 31, so a spread under a microsecond
    /// is reported as an error of twelve, and any real difference disappears inside
    /// it. Fifteen iterations bring the factor to about 1.1, so the reported error
    /// is close to the spread actually observed.
    /// </para>
    /// <para>
    /// Three launches matter just as much. Repeated runs of identical shaping code
    /// differed by up to a tenth between processes while barely varying within one,
    /// so the variance that a gate has to see lives between processes, and only more
    /// than one launch exposes it.
    /// </para>
    /// </remarks>
    public class Gate : Config
    {
        public Gate() => this.AddJob(
                Job.Default.WithRuntime(HostRuntime)
                           .WithLaunchCount(3)
                           .WithWarmupCount(5)
                           .WithIterationCount(15)
                           .WithArguments([new MsBuildArgument("/p:DebugType=portable")]));
    }

#if OS_WINDOWS
    private bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
#endif
}
