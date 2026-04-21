using Xunit;

// Disable parallel test execution across test classes in this assembly.
// WebApplicationFactory-based tests share internal logging state that
// gets frozen after first host build, causing failures when classes run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
