using BackendService.Models;
using System.Threading.Tasks;

namespace BackendService.Storage
{
    public interface IConversationMetadataStorage
    {
        Task<ConversationMetadata?> GetAsync(string externalId);
        Task SaveAsync(ConversationMetadata metadata);
    }
}
