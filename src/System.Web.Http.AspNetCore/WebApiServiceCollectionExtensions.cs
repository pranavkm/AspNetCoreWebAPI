using System.Web.Http.AspNetCore.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace System.Web.Http.AspNetCore
{
    public static class WebApiServiceCollectionExtensions
    {
        public static void AddWebApi(this IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton<ApplicationBuilderContainer>();
            serviceCollection.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, ApplicationBuilderContainer>(services => services.GetRequiredService<ApplicationBuilderContainer>()));
        }
    }
}
