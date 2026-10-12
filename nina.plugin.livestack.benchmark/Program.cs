using BenchmarkDotNet.Running;
using System;
using System.Numerics;
using nina.plugin.livestack.benchmark;

public static class Program {

    public static void Main(string[] args) {
        if (args.FirstOrDefault() == "--verify-archive") {
            Environment.ExitCode = ArchiveVerification.Run(args.Skip(1).ToArray()).GetAwaiter().GetResult();
            return;
        }
        if (args.FirstOrDefault() == "--compare-alignment") {
            Environment.ExitCode = CatalogAlignmentComparison.Run(args.Skip(1).ToArray());
            return;
        }
        Console.WriteLine($"Vector.IsHardwareAccelerated = {Vector.IsHardwareAccelerated}");
        Console.WriteLine($"Vector<float>.Count = {Vector<float>.Count}");
        Console.WriteLine();

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
