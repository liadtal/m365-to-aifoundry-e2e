using System.Text;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;

namespace BotService;

public class AzureAgent : AgentApplication
{
    private readonly ILogger logger;

    public AzureAgent(AgentApplicationOptions options, IConfiguration configuration, ILogger<AzureAgent> logger) : base(options)
    {
        this.logger = logger;

        this.logger.LogInformation($"Creating a new instance of {nameof(AzureAgent)}");

        // Setup Agent with Route handlers to manage connecting and responding from the Microsoft Foundry agent

        // This is handling the sign out event, which will clear the user authorization token.
        OnMessage("--signout", HandleSignOutAsync);

        // This is handling the message activity, which will send the user message to the Microsoft Foundry agent.
        // we are also indicating which auth profile we want to have available for this handler.
        //OnActivity(ActivityTypes.Message, SendMessageToAzureAgent);
        OnActivity(ActivityTypes.Message, SendMessageToAzureAgent);
        this.logger.LogInformation($"Successfully created new instance of {nameof(AzureAgent)}");
    }

    /// <summary>
    /// Handle the sign out event, and clear the logged in user token
    /// </summary>
    /// <param name="turnContext"></param>
    /// <param name="turnState"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task HandleSignOutAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        await UserAuthorization.SignOutUserAsync(turnContext, turnState, cancellationToken: cancellationToken);
        await turnContext.SendActivityAsync("You have signed out", cancellationToken: cancellationToken);
        this.logger.LogInformation("The user has signed out.");
    }

    /// <summary>
    /// This method sends the user message ( just text in this example ) to the Microsoft Foundry agent and streams the response back to the user.
    /// </summary>
    /// <param name="turnContext"></param>
    /// <param name="turnState"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>

    private async Task SendMessageToAzureAgent(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        try
        {
            this.logger.LogInformation("--- Bot: Sending request to backend and listening to stream...");
            await turnContext.StreamingResponse.QueueInformativeUpdateAsync("Just a moment please..", cancellationToken);

            var httpClient = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:5000/api/v1/messages");

            // Serialize the full Activity object to JSON.
            var jsonPayload = JsonSerializer.Serialize(turnContext.Activity);
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            this.logger.LogInformation($"Started reading stream from Azure agent, status code: {response.StatusCode}");

            var buffer = new char[1024];
            int charsRead;
            while (!cancellationToken.IsCancellationRequested && (charsRead = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                var chunk = new string(buffer, 0, charsRead);
                Console.Write(chunk);
                turnContext.StreamingResponse.QueueTextChunk(chunk);
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error sending message to Azure agent");
            turnContext.StreamingResponse.QueueTextChunk($"An error occurred while processing your request. {ex.Message}");
        }
        finally
        {
            this.logger.LogInformation("--- Bot: Finished streaming response.");
            await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
        }
    }
}