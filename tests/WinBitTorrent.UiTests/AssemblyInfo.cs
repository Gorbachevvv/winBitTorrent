// Every test in this project launches the real app and drives it through the desktop, so two of
// them running at once fight over window focus and the managed backend. xUnit parallelizes across
// test classes by default, which is exactly that situation - so it is turned off here.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
