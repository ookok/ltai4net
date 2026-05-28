using LTAI.Knowledge.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LTAI.Knowledge;

public static class KnowledgeServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIKnowledgeStore(this IServiceCollection services)
    {
        // Register CompositeKnowledgeStore wrapping all available IKnowledgeStore implementations
        services.AddSingleton<IKnowledgeStore>(sp =>
        {
            var stores = new List<IKnowledgeStore>();

            var kb = sp.GetService<KnowledgeBase>();
            if (kb != null) stores.Add(kb);

            var kg = sp.GetService<KnowledgeGraph>();
            if (kg != null) stores.Add(kg);

            // DualMemoryStore — registered separately in LTAI.AI
            // If available, it will be added by LTAI.AI's service registration
            var dualStoreType = System.Type.GetType("LTAI.AI.Governors.DualMemoryStore, LTAI.AI");
            if (dualStoreType != null)
            {
                var dualStore = sp.GetService(dualStoreType) as IKnowledgeStore;
                if (dualStore != null) stores.Add(dualStore);
            }

            return stores.Count switch
            {
                0 => new CompositeKnowledgeStore(),
                1 => stores[0],
                _ => new CompositeKnowledgeStore(stores)
            };
        });

        return services;
    }
}
