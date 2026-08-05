using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LogicApp.Tests.Unit
{
    /// <summary>
    /// Cross-cutting static checks that run over every workflow listed in <see cref="Workflows"/>.
    /// Add your workflow folder names to the array as you build them out.
    /// </summary>
    [TestClass]
    public class WorkflowCrossReferenceTests
    {
        public static readonly string[] Workflows = new[]
        {
            "sample-order-servicebus",
        };

        [TestMethod]
        public void EveryParameterReferenceIsDeclared()
        {
            var declared = WorkflowFiles.LoadParameters()
                .EnumerateObject().Select(p => p.Name).ToHashSet();
            var paramRegex = new Regex(@"parameters\('([^']+)'\)");

            foreach (var wf in Workflows)
            {
                var def = WorkflowFiles.LoadDefinition(wf);
                var refs = def.AllStrings()
                    .SelectMany(s => paramRegex.Matches(s).Cast<Match>().Select(m => m.Groups[1].Value))
                    .Distinct();
                foreach (var name in refs)
                    Assert.IsTrue(declared.Contains(name),
                        $"{wf}: parameters('{name}') not declared in parameters.json");
            }
        }

        [TestMethod]
        public void EveryServiceProviderConnectionIsDeclared()
        {
            var connections = WorkflowFiles.LoadConnections();
            Assert.IsTrue(connections.TryGetProperty("serviceProviderConnections", out var spc),
                "connections.json has no serviceProviderConnections");
            var declared = spc.EnumerateObject().Select(p => p.Name).ToHashSet();

            foreach (var wf in Workflows)
            {
                var def = WorkflowFiles.LoadDefinition(wf);
                var used = def.DescendantsAndSelf()
                    .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("connectionName", out _))
                    .Select(e => e.GetProperty("connectionName").GetString())
                    .Where(s => s != null)
                    .Distinct();
                foreach (var name in used)
                    Assert.IsTrue(declared.Contains(name!),
                        $"{wf}: connection '{name}' not declared in connections.json");
            }
        }

        [TestMethod]
        public void NoTerminateActionsAnywhere()
        {
            foreach (var wf in Workflows)
            {
                var def = WorkflowFiles.LoadDefinition(wf);
                var terminates = def.DescendantsAndSelf()
                    .Count(e => e.ValueKind == JsonValueKind.Object
                                && e.TryGetProperty("type", out var t)
                                && t.ValueKind == JsonValueKind.String
                                && t.GetString() == "Terminate");
                Assert.AreEqual(0, terminates,
                    $"{wf}: prefer signalling failure via a non-2xx Response over Terminate actions");
            }
        }
    }
}
