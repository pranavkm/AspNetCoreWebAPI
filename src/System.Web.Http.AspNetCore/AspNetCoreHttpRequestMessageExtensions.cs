// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Web.Http.AspNetCore;
using System.Web.Http.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace System.Net.Http
{
    internal static class AspNetCoreHttpRequestMessageExtensions
    {
        private const string HttpContextKey = "MS_HttpContext";
        private const string RequestContentKey = "MS_StashedHttpRequest";

        public static HttpContext GetHttpContext(this HttpRequestMessage request)
        {
            return (HttpContext)request.Properties[HttpContextKey];
        }

        public static HttpRequestMessage CreateRequestMessage(IOwinRequest owinRequest, HttpContent requestContent)
        {
            // Create the request
            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(owinRequest.Method), owinRequest.Uri);

            try
            {
                // Set the body
                request.Content = requestContent;

                // Copy the headers
                foreach (KeyValuePair<string, string[]> header in owinRequest.Headers)
                {
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    {
                        bool success = requestContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        Contract.Assert(success,
                            "Every header can be added either to the request headers or to the content headers");
                    }
                }
            }
            catch
            {
                request.Dispose();
                throw;
            }

            return request;
        }

        public static void CopyHeaders(this HttpRequest httpRequest, HttpRequestMessage request)
        {
            // Copy the headers
            foreach (var (key, value) in httpRequest.Headers)
            {
                if (!request.Headers.TryAddWithoutValidation(key, (ICollection<string>)value))
                {
                    request.Content.Headers.TryAddWithoutValidation(key, (ICollection<string>)value);
                }
            }
        }

        public static void Copy(this HttpResponse httpResponse, HttpResponseMessage responseMessage)
        {
            // Copy the headers
            foreach (var (key, value) in httpResponse.Headers)
            {
                if (!responseMessage.Headers.TryAddWithoutValidation(key, (ICollection<string>)value))
                {
                    responseMessage.Content.Headers.TryAddWithoutValidation(key, (ICollection<string>)value);
                }
            }

            if (httpResponse.StatusCode != 0)
            {
                responseMessage.StatusCode = (HttpStatusCode)httpResponse.StatusCode;
            }
        }

        public static void ApplyTo(this HttpRequestMessage requestMessage, HttpContext httpContext)
        {
            foreach (var (key, value) in requestMessage.Headers.Concat(requestMessage.Content.Headers))
            {
                var currentHeaders = httpContext.Request.Headers[key];
                httpContext.Request.Headers[key] = new StringValues(currentHeaders.Concat(value).ToArray());
            }
        }

        private static HttpContent CreateStreamedRequestContent(HttpRequest httpRequest)
        {
            // Note that we must NOT dispose httpRequest.Body in this case. Disposing it would close the input
            // stream and prevent cascaded components from accessing it. The server MUST handle any necessary
            // cleanup upon request completion. NonOwnedStream prevents StreamContent (or its callers including
            // HttpRequestMessage) from calling Close or Dispose on httpRequest.Body.
            return new StreamContent(new NonDisposableStream(httpRequest.Body));
        }

        private class StashedHttpContent
        {
            public HttpContent HttpContent { get; set; }

            public long OriginalBodyHash { get; set; }
        }
    }
}
