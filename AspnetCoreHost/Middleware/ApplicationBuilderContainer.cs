using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace System.Web.Http.AspNetCore.Middleware
{
    internal sealed class ApplicationBuilderContainer : IStartupFilter
    {
        public IApplicationBuilder ApplicationBuilder { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return MiddlewareFilterBuilder;

            void MiddlewareFilterBuilder(IApplicationBuilder builder)
            {
                ApplicationBuilder = builder.New();

                next(builder);
            }
        }
    }
}
