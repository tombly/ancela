// Disable parallel test execution for integration tests that make real API calls
// This prevents race conditions and ensures consistent mock verification
[assembly: CollectionBehavior(DisableTestParallelization = true)]