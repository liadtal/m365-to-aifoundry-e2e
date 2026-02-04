using BackendService.Models;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace BackendService.Storage
{
    public class InMemoryConversationMetadataStorage : IConversationMetadataStorage
    {
        private readonly ConcurrentDictionary<string, ConversationMetadata> _storage = new();

        public Task<ConversationMetadata?> GetAsync(string externalId)
        {
            _storage.TryGetValue(externalId, out var metadata);
            return Task.FromResult(metadata);
        }

        public Task SaveAsync(ConversationMetadata metadata)
        {
            _storage[metadata.ExternalId] = metadata;
            return Task.CompletedTask;
        }
    }
}
