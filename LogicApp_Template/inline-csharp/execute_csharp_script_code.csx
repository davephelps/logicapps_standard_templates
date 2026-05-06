// Add the required libraries
#r "Newtonsoft.Json"
#r "Microsoft.Azure.Workflows.Scripting"
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Workflows.Scripting;
using Newtonsoft.Json.Linq;
using System.Text;

/// <summary>
/// Executes the inline csharp code.
/// </summary>
/// <param name="context">The workflow context.</param>
/// <remarks>This is the entry-point to your code. The function signature should remain unchanged.</remarks>
public static async Task<object> Run(WorkflowContext context, ILogger log)
{
    // Get trigger body
    JToken triggerOutputs = (await context.GetTriggerResults().ConfigureAwait(false)).Outputs;
    var requestBody = triggerOutputs?["body"]?.ToString();

    // Parse the input
    JObject input = JObject.Parse(requestBody);

    // Example transformation: count items and create summary
    var items = input["items"] as JArray;
    int itemCount = items?.Count ?? 0;

    log.LogInformation($"Processed {itemCount} items");

    // Return transformed result
    return new {
        totalItems = itemCount,
        processedAt = DateTime.UtcNow.ToString("o"),
        status = "transformed"
    };
}
