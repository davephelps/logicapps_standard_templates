using FluentAssertions;
using LogicApp.Tests.E2E.Infrastructure;
using Reqnroll;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using System.Text;

namespace LogicApp.Tests.E2E.Steps;

/// <summary>
/// Domain-level E2E step definitions. Drive the sample workflow through the APIM emulator front
/// door, asserting both the gateway contract and the downstream Service Bus side effect.
/// </summary>
[Binding]
public sealed class GatewaySteps
{
    private readonly TestEnvironment _env;
    private readonly string _testDataRoot;

    public GatewaySteps(TestEnvironment env)
    {
        _env = env;
        _testDataRoot = Path.Combine(AppContext.BaseDirectory, "__testdata");
    }

    [Given("the external enrichment API returns 200 with an enriched order")]
    public void GivenExternalApiOk()
    {
        var path = _env.Config["External:SamplePath"] ?? "/external/api";
        _env.WireMock.Server
            .Given(Request.Create().WithPath(path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"status\":\"enriched\",\"orderId\":\"sample-1\"}"));
    }

    [When("a client calls the gateway health endpoint")]
    public async Task WhenHealth()
    {
        _env.LastResponse = await _env.ApimClient.GetHealthAsync();
        _env.LastResponseBody = await _env.LastResponse.Content.ReadAsStringAsync();
    }

    [When("a client POSTs the order {string} to the gateway without a subscription key")]
    public async Task WhenPostNoKey(string fixtureName)
    {
        var body = await File.ReadAllTextAsync(Path.Combine(_testDataRoot, fixtureName));
        _env.LastResponse = await _env.ApimClient.PostAssetAsync(body, subscriptionKeyOverride: "");
        _env.LastResponseBody = await _env.LastResponse.Content.ReadAsStringAsync();
    }

    [When("a client POSTs a malformed body to the gateway")]
    public async Task WhenPostMalformed()
    {
        _env.LastResponse = await _env.ApimClient.PostAssetAsync("not valid json");
        _env.LastResponseBody = await _env.LastResponse.Content.ReadAsStringAsync();
    }

    [When("a valid order {string} is sent through the gateway and Logic App")]
    public async Task WhenValidThrough(string fixtureName)
    {
        var body = await File.ReadAllTextAsync(Path.Combine(_testDataRoot, fixtureName));

        // 1) The gateway accepts the request (subscription key + body-shape contract).
        _env.LastResponse = await _env.ApimClient.PostAssetAsync(body);
        _env.LastResponseBody = await _env.LastResponse.Content.ReadAsStringAsync();

        // 2) In production APIM forwards to the Logic App; locally we invoke the workflow via its
        //    Request-trigger callback so the full chain (LA -> external HTTP -> Service Bus) runs.
        var wf = _env.Config["Workflows:Sample"] ?? "sample-order-servicebus";
        var trg = _env.Config["Triggers:Sample"] ?? "When_a_HTTP_request_is_received";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        await _env.Trigger.InvokeHttpTrigger(wf, trg, content);
    }

    [Then("the gateway response status is {int}")]
    public void ThenStatus(int expected)
    {
        _env.LastResponse.Should().NotBeNull();
        ((int)_env.LastResponse!.StatusCode).Should().Be(expected, _env.LastResponseBody);
    }

    [Then("a message is published to the sample queue")]
    public async Task ThenMessage()
    {
        var msgs = await _env.ServiceBus.ReadMessages(_env.SampleQueueName, expectedCount: 1, TimeSpan.FromSeconds(30));
        msgs.Should().NotBeEmpty();
    }
}
