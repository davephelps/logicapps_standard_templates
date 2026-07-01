using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LogicApp.Tests.Unit
{
    /// <summary>
    /// Shared structural assertions over a workflow definition (JsonElement). No engine, no runtime.
    /// </summary>
    internal static class WorkflowAssert
    {
        public static void RunsAfter(JsonElement parentScope, string action, string predecessor, string status)
        {
            var runAfter = parentScope.TryPath("actions", action, "runAfter");
            Assert.IsNotNull(runAfter, $"{action} has no runAfter");
            var ra = runAfter!.Value;
            Assert.IsTrue(ra.TryGetProperty(predecessor, out var entry),
                $"{action} should runAfter {predecessor}");
            var statuses = entry.EnumerateArray().Select(s => s.GetString()).ToArray();
            CollectionAssert.Contains(statuses, status,
                $"{action} should runAfter {predecessor} with status {status}");
        }

        public static JsonElement Action(JsonElement parentScope, string action)
        {
            var a = parentScope.TryPath("actions", action);
            Assert.IsNotNull(a, $"action '{action}' is missing from scope");
            return a!.Value;
        }

        public static void Type(JsonElement action, string expected)
        {
            Assert.IsTrue(action.TryGetProperty("type", out var t), "action has no 'type'");
            Assert.AreEqual(expected, t.GetString());
        }

        public static void ServiceProviderBinding(JsonElement action, string connectionName, string operationId, string serviceProviderId)
        {
            var sp = action.TryPath("inputs", "serviceProviderConfiguration");
            Assert.IsNotNull(sp, "serviceProviderConfiguration missing");
            Assert.AreEqual(connectionName, sp!.Value.GetProperty("connectionName").GetString());
            Assert.AreEqual(operationId, sp.Value.GetProperty("operationId").GetString());
            Assert.AreEqual(serviceProviderId, sp.Value.GetProperty("serviceProviderId").GetString());
        }
    }
}
