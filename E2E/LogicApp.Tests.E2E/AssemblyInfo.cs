// AssemblyInfo for LogicApp.Tests.E2E
//
// xUnit + Reqnroll domain-level E2E test project. Composes the APIM emulator front door with the
// Logic App (via func host) and the Service Bus emulator. Reqnroll generates one xUnit test class
// per .feature file at build time.

using Xunit;

// Disable parallel test collection across the whole assembly. The E2E tests share a single func
// host, a single APIM emulator + backend WireMock, and a single Service Bus queue -- parallel
// scenarios would race on ports and cross-talk on the queue.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
