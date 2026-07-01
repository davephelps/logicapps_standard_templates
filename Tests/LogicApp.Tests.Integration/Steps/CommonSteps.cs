using FluentAssertions;
using LogicApp.Tests.Integration.Infrastructure;
using Reqnroll;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using System.Text;

namespace LogicApp.Tests.Integration.Steps;

/// <summary>
/// Step definitions for the sample integration scenario. This is the template you copy for
/// your own workflows: stub external HTTP with WireMock in [Given] steps, invoke the workflow
/// through its Request-trigger callback URL in [When], assert the HTTP response and any
/// Service Bus / storage side effects in [Then].
///
/// All patterns are Reqnroll Cucumber Expressions ({string}, {int}) or plain text -- do NOT mix
/// in bare regex like (.*), which fails to bind unless anchored with ^...$.
/// </summary>
[Binding]
public sealed class CommonSteps
{
    private readonly TestEnvironment _env;
    private readonly string _testDataRoot;

    public CommonSteps(TestEnvironment env)
    {
        _env = env;
        _testDataRoot = Path.Combine(AppContext.BaseDirectory, "__testdata");
    }

    // ----- Given: external HTTP stubs (WireMock -- external HTTP only) -----

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

    [Given("the external enrichment API returns 500 on every attempt")]
    public void GivenExternalApiFails()
    {
        var path = _env.Config["External:SamplePath"] ?? "/external/api";
        _env.WireMock.Server
            .Given(Request.Create().WithPath(path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500)
                .WithBody("{\"error\":\"enrichment down\"}"));
    }

    // ----- When: invoke the workflow via its Request-trigger callback URL -----

    [When("the caller POSTs the order {string} to the sample workflow")]
    public async Task WhenCallerPostsOrder(string fixtureName)
    {
        var workflow = _env.Config["Workflows:Sample"] ?? "sample-order-servicebus";
        var trigger = _env.Config["Triggers:Sample"] ?? "When_a_HTTP_request_is_received";
        var body = await File.ReadAllTextAsync(Path.Combine(_testDataRoot, fixtureName));
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _env.Trigger.InvokeHttpTrigger(workflow, trigger, content);
        _env.LastResponse = resp;
        _env.LastResponseBody = await resp.Content.ReadAsStringAsync();
    }

    // ----- Then: response + queue assertions -----

    [Then("the caller response status is {int}")]
    public async Task ThenStatusIs(int expected)
    {
        _env.LastResponse.Should().NotBeNull();
        var actual = (int)_env.LastResponse!.StatusCode;
        if (actual != expected)
        {
            var workflow = _env.Config["Workflows:Sample"] ?? "sample-order-servicebus";
            var diag = await _env.Trigger.DumpLatestRunDiagnostics(workflow);
            throw new Xunit.Sdk.XunitException(
                $"Expected status {expected}, got {actual}. Body:\n{_env.LastResponseBody}\n\n{diag}");
        }
    }

    [Then("a message is published to the sample queue")]
    public async Task ThenMessagePublished()
    {
        var msgs = await _env.ServiceBus.ReadMessages(_env.SampleQueueName, expectedCount: 1, TimeSpan.FromSeconds(30));
        if (msgs.Count == 0)
        {
            var workflow = _env.Config["Workflows:Sample"] ?? "sample-order-servicebus";
            var diag = await _env.Trigger.DumpLatestRunDiagnostics(workflow);
            throw new Xunit.Sdk.XunitException(
                $"Expected a message on {_env.SampleQueueName} but got none.\n{diag}");
        }
        _env.LastMessages.AddRange(msgs);
    }

    [Then("no message is published to the sample queue")]
    public async Task ThenNoMessagePublished()
    {
        var msgs = await _env.ServiceBus.ReadMessages(_env.SampleQueueName, expectedCount: 1, TimeSpan.FromSeconds(3));
        msgs.Should().BeEmpty();
    }
}
