using Microsoft.Extensions.AI;

namespace TasteTest.Services;

public interface IModelChatClientFactory
{
    IChatClient GetClient(ProviderKind provider);

    ModelIdentity GetIdentity(ProviderKind provider);
}
