using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LogicApp.Tests.Unit
{
    /// <summary>
    /// Static structural unit tests for the sample-order-servicebus workflow. These assert the
    /// shape of workflow.json without running the engine (no Docker, sub-second). Copy this class
    /// as the starting point for your own per-workflow structural tests.
    /// </summary>
    [TestClass]
    public class SampleWorkflowTests
    {
        private JsonElement _definition;

        [TestInitialize]
        public void Init() => _definition = WorkflowFiles.LoadDefinition("sample-order-servicebus");

        [TestMethod]
        public void HasHttpRequestTrigger()
        {
            var trig = _definition.TryPath("triggers", "When_a_HTTP_request_is_received");
            Assert.IsNotNull(trig, "Request trigger missing");
            Assert.AreEqual("Request", trig!.Value.GetProperty("type").GetString());
            Assert.AreEqual("Http", trig.Value.GetProperty("kind").GetString());
        }

        [TestMethod]
        public void CallExternalApiIsHttpPostToAppSettingUrl()
        {
            var a = WorkflowAssert.Action(_definition, "Call_External_API");
            WorkflowAssert.Type(a, "Http");
            Assert.AreEqual("@appsetting('externalApiUrl')", a.TryPath("inputs", "uri")!.Value.GetString());
            Assert.AreEqual("POST", a.TryPath("inputs", "method")!.Value.GetString());
        }

        [TestMethod]
        public void PublishToServiceBusBindsServiceBusSendMessage()
        {
            var a = WorkflowAssert.Action(_definition, "Publish_To_Service_Bus");
            WorkflowAssert.Type(a, "ServiceProvider");
            WorkflowAssert.ServiceProviderBinding(a, "serviceBus", "sendMessage", "/serviceProviders/serviceBus");
            Assert.AreEqual("sample-queue", a.TryPath("inputs", "parameters", "entityName")!.Value.GetString());
            WorkflowAssert.RunsAfter(_definition, "Publish_To_Service_Bus", "Call_External_API", "SUCCEEDED");
        }

        [TestMethod]
        public void ReturnResponseIs200AfterPublish()
        {
            var r = WorkflowAssert.Action(_definition, "Return_Response");
            WorkflowAssert.Type(r, "Response");
            Assert.AreEqual(200, r.GetProperty("inputs").GetProperty("statusCode").GetInt32());
            WorkflowAssert.RunsAfter(_definition, "Return_Response", "Publish_To_Service_Bus", "SUCCEEDED");
        }
    }
}
