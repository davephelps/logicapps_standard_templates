// AssemblyInfo for LogicApp.Tests.Integration
//
// xUnit + Reqnroll integration test project for the Logic App under LogicApp_Template.
// Reqnroll generates one xUnit test class per .feature file at build time (the generated
// class name follows the .feature file name).

using Xunit;

// Disable parallel test collection across the whole assembly. The integration tests share a
// single in-process WireMock server, a single func host, and a single Service Bus queue under
// test -- running scenarios in parallel would cause WireMock stub collisions, port races on the
// func host, and Service Bus message cross-talk. Reqnroll respects xUnit collection semantics.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
