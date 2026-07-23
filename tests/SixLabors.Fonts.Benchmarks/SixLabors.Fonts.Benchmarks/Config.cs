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

    public class Standard : Config
    {
        public Standard() => this.AddJob(
                Job.Default.WithRuntime(CoreRuntime.Core80).WithArguments([new MsBuildArgument("/p:DebugType=portable")]));
    }

    public class Short : Config
    {
        public Short() => this.AddJob(
                Job.Default.WithRuntime(CoreRuntime.Core80)
                           .WithLaunchCount(1)
                           .WithWarmupCount(3)
                           .WithIterationCount(3)
                           .WithArguments([new MsBuildArgument("/p:DebugType=portable")]));
    }

#if OS_WINDOWS
    private bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
#endif
}
