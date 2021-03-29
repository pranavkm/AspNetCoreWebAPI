using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http.AspNetCore.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace System.Web.Http
{
    public class MiddlewareDelegatingHandler : DelegatingHandler
    {
        private readonly Action<IApplicationBuilder> _configure;
        private RequestDelegate _requestDelegate;

        public MiddlewareDelegatingHandler(Action<IApplicationBuilder> configure)
        {
            _configure = configure;
        }

        public static readonly HttpResponseMessage RequestStartedInAspNetCoreMiddleware = new HttpResponseMessage();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var httpContext = request.GetHttpContext();
            _requestDelegate ??= BuildRequestDelegate(httpContext);

            request.ApplyTo(httpContext);
            await _requestDelegate(httpContext);

            if (httpContext.Response.HasStarted)
            {
                // if the request started, don't call the next handler. Just construct a magic response and let it go.
                return RequestStartedInAspNetCoreMiddleware;
            }

            // Copy things from 
            httpContext.Request.CopyHeaders(request);
            var response = await base.SendAsync(request, cancellationToken);
            httpContext.Response.Copy(response);

            return response;
        }

        private RequestDelegate BuildRequestDelegate(HttpContext httpContext)
        {
            var applicationBuilderContext = httpContext.RequestServices.GetRequiredService<ApplicationBuilderContainer>();
            var appBuilder = applicationBuilderContext.ApplicationBuilder.New();
            _configure(appBuilder);
            appBuilder.Run(async context =>
            {
                // Do nothing
                await Task.Yield();
            });

            return appBuilder.Build();
        }
    }
}
