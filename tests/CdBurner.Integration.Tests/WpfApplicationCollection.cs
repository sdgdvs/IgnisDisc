using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Test classes that create the process-wide WPF Application, or whose view models marshal work to its dispatcher,
/// must not run at the same time: while the acceptance test's STA thread is alive it owns Application.Current's
/// dispatcher but never pumps it, so work marshalled there from another test would wait forever.
/// Classes in this collection run one after another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfApplicationCollection
{
    public const string Name = "WPF Application (process-wide)";
}
