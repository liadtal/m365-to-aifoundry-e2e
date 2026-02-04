using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Responses;

// --- Web Application Setup ---
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:8000");
builder.Services.AddSingleton<AgentService>();
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.MapPost("/api/v1/messages", async (StreamRequest payload, AgentService agentService, HttpContext httpContext) =>
{
    if (string.IsNullOrWhiteSpace(payload.Text))
    {
        httpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        await httpContext.Response.WriteAsync("Text cannot be empty.");
        return;
    }
    if (string.IsNullOrWhiteSpace(payload.ConversationId))
    {
        httpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        await httpContext.Response.WriteAsync("ConversationId cannot be empty.");
        return;
    }

    httpContext.Response.ContentType = "text/event-stream";
    var responseStream = agentService.ProcessChatRequestAsync(payload.Text, payload.ConversationId, CancellationToken.None);
    
    await foreach (var s in responseStream)
    {
        await httpContext.Response.WriteAsync(s);
        await httpContext.Response.Body.FlushAsync();
    }
});

app.Run();

// --- Request Model ---
public record StreamRequest(string Text, string ConversationId);

// --- Agent Service ---
public class AgentService
{
    const string Endpoint = "https://liadtest2i6kh.services.ai.azure.com/api/projects/liadtest2-proji6kh";
    const string AgentId = "Test";

    private readonly AIProjectClient projectClient;
    private readonly ConcurrentDictionary<string, AgentThread> threads = new();

    private AIAgent? cachedAgent;

    public AgentService()
    {
        projectClient = new(new Uri(Endpoint), new DefaultAzureCredential());
    }

    public async IAsyncEnumerable<string> ProcessChatRequestAsync(
        string messageText,
        string conversationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AIAgent agent = await this.GetAgentAsync(cancellationToken);
        if (!this.threads.TryGetValue(conversationId, out AgentThread? thread))
        {
            thread = agent.GetNewThread();
        }

        ChatMessage message = new(ChatRole.User, messageText);
        Console.WriteLine();
        await foreach (AgentRunResponseUpdate response in agent.RunStreamingAsync(message, thread, cancellationToken: cancellationToken))
        {
            string responseText = response.Text;
            if (!string.IsNullOrEmpty(responseText))
            {
                Console.Write(response);
                yield return responseText;
            }
        }
        Console.WriteLine();
    }

    private async Task<AIAgent> GetAgentAsync(CancellationToken cancellationToken)
    {
        if (this.cachedAgent != null)
        {
            return this.cachedAgent;
        }

        // Get agent
        AgentRecord agentRecord = await this.projectClient.Agents.GetAgentAsync(AgentId, cancellationToken: cancellationToken);
        this.cachedAgent = this.projectClient.GetAIAgent(agentRecord, [AIFunctionFactory.Create(GetDailyTasks)]);
        return this.cachedAgent;
    }

    [DisplayName("get_daily_tasks")]
    [Description("Get the daily tasks for a given user.")]
    private static string GetDailyTasks(string username)
    {
        Console.WriteLine($"[Tool] Getting daily tasks for {username}");
        var tasks = new List<object>();
        if (username.ToLower().Contains("lital"))
        {
            tasks.Add(new { id = "task1", title = "Take daughter from kindergarten", completed = false });
            tasks.Add(new { id = "task2", title = "Make dinner", completed = true });
            tasks.Add(new { id = "task3", title = "Read a chapter in my book", completed = false });
        }
        else
        {
            tasks.Add(new { id = "task1", title = "Finish the quarterly report", completed = false });
            tasks.Add(new { id = "task2", title = "Prepare for the team meeting", completed = true });
            tasks.Add(new { id = "task3", title = "Review pull requests", completed = false });
        }
        return JsonSerializer.Serialize(tasks);
    }
}

