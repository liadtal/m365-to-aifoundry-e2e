namespace BackendService;

public class SimpleConversation
{
    public string Id { get; set; } = string.Empty;
}

public class IncomingActivity
{
    public SimpleConversation Conversation { get; set; } = new SimpleConversation();
    public string Text { get; set; } = string.Empty;
}
