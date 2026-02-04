using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using BackendService.Models;
using Microsoft.AspNetCore.Mvc;

namespace BackendService.Controllers;

[ApiController]
[Route("api/v1/messages")]
public class MessagesController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public MessagesController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost]
    public async Task Post([FromBody] IncomingActivity activity)
    {
        var externalId = activity.Conversation.Id;
        if (string.IsNullOrEmpty(externalId))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("External conversation ID cannot be null or empty.");
            return;
        }

        var coreServicePayload = new
        {
            text = activity.Text,
            conversationId = externalId
        };

        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:8000/api/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(coreServicePayload), Encoding.UTF8, "application/json")
        };

        try
        {
            Console.WriteLine($"--- Backend: Forwarding message for external conversation {externalId} ---");
            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using Stream stream = await response.Content.ReadAsStreamAsync();

            Response.ContentType = "text/event-stream";

            await foreach (SseItem<string> item in SseParser.Create(stream).EnumerateAsync())
            {
                switch (item.EventType)
                {
                    case "text":
                    case "error":
                        Console.Write(item.Data);
                        if (item.Data != null)
                        {
                            await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(item.Data));
                            await Response.Body.FlushAsync();
                        }
                        break;
                    case "usage":
                        Console.WriteLine();
                        Console.WriteLine($"--- Backend: Usage reported: {item.Data} ---");
                        break;
                    default:
                        Console.WriteLine();
                        Console.WriteLine($"Event: {item.EventType}, Data: {item.Data}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"--- Backend: An error occurred during streaming: {ex.Message} ---");
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
                await Response.WriteAsync("An internal error occurred before streaming could start.");
            }
            else
            {
                string errorMessage = "event: error\ndata: An unexpected error occurred while processing your request.\n\n";
                await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(errorMessage));
                await Response.Body.FlushAsync();
            }
        }

        Console.WriteLine();
        Console.WriteLine("--- Backend: Finished streaming response. ---");
    }
}
