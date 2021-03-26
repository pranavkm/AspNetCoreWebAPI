// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.ComponentModel;
using Microsoft.AspNetCore.Http;

namespace System.Net.Http
{
    /// <summary>
    /// Provides extension methods for the <see cref="HttpRequestMessage"/> class.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class AspNetCoreHttpRequestMessageExtensions
    {
        private const string HttpEnvironmentKey = "MS_HttpEnvironment";
        private const string HttpContextKey = "MS_HttpContext";

        /// <summary>Sets the OWIN context for the specified request.</summary>
        /// <param name="request">The HTTP request message.</param>
        /// <param name="context">The OWIN context to set.</param>
        public static void SetHttpContext(this HttpRequestMessage request, HttpContext context)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            request.Properties[HttpContextKey] = context;
            // Make sure only one of the two properties exists (single source of truth).
            request.Properties.Remove(HttpEnvironmentKey);
        }
    }
}
