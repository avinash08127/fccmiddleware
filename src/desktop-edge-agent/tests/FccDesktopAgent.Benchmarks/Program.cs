using BenchmarkDotNet.Running;
using FccDesktopAgent.Benchmarks;

// Run all benchmarks in Release mode.
// Usage:
//   dotnet run -c Release --project tests/FccDesktopAgent.Benchmarks
//
// Individual benchmark category:
//   dotnet run -c Release -- --filter "*TransactionQuery*"
//   dotnet run -c Release -- --filter "*ReplayThroughput*"
//   dotnet run -c Release -- --filter "*PreAuth*"
//   dotnet run -c Release -- --filter "*Memory*"

BenchmarkSwitcher
    .FromAssembly(typeof(Program).Assembly)
    .Run(args);
