// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http.Controllers;
using System.Web.Http.ExceptionHandling;
using System.Web.Http.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace System.Web.Http.AspNetCore
{
    public class HttpMessageHandlerAdapter : IDisposable
    {
        private readonly RequestDelegate _next;
        private readonly HttpMessageHandler _messageHandler;
        private readonly HttpMessageInvoker _messageInvoker;
        private readonly IHostBufferPolicySelector _bufferPolicySelector;
        private readonly IExceptionLogger _exceptionLogger;
        private readonly IExceptionHandler _exceptionHandler;

        private bool _disposed;

        public HttpMessageHandlerAdapter(RequestDelegate next, HttpMessageHandlerOptions options, IHostApplicationLifetime applicationLifetime)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            _next = next;
            _messageHandler = options.MessageHandler ?? throw new ArgumentNullException(nameof(options.MessageHandler));
            _messageInvoker = new HttpMessageInvoker(_messageHandler);
            _bufferPolicySelector = options.BufferPolicySelector ?? new AspNetCoreBufferPolicySelector();

            _exceptionLogger = options.ExceptionLogger;
            _exceptionHandler = options.ExceptionHandler;

            if (applicationLifetime.ApplicationStopping.CanBeCanceled)
            {
                applicationLifetime.ApplicationStopping.Register(OnAppDisposing);
            }
        }

        public async Task Invoke(HttpContext httpContext)
        {
            if (httpContext == null)
            {
                throw new ArgumentNullException("context");
            }

            var syncIOFeature = httpContext.Features.Get<IHttpBodyControlFeature>();
            if (syncIOFeature != null)
            {
                syncIOFeature.AllowSynchronousIO = true;
            }

            CancellationToken cancellationToken = httpContext.RequestAborted;
            var httpRequest = httpContext.Request;
            var httpResponse = httpContext.Response;

            bool bufferInput = _bufferPolicySelector.UseBufferedInputStream(hostContext: httpContext);

            if (bufferInput)
            {
                throw new NotSupportedException("Request buffering is not supported by ths host. Consider using HttpRequest.EnableBuffering in a middleware instead.");
            }

            var request = httpContext.ToHttpRequestMessage();
            SetPrincipal(httpContext.User);

            HttpResponseMessage response = null;
            bool callNext;

            try
            {
                response = await _messageInvoker.SendAsync(request, cancellationToken);

                // Handle null responses
                if (response == null)
                {
                    throw new InvalidOperationException("SendAsync returned null HttpResponseMessage.");
                }

                // Handle soft 404s where no route matched - call the next component
                if (IsSoftNotFound(request, response))
                {
                    callNext = true;
                }
                else
                {
                    callNext = false;

                    // Compute Content-Length before calling UseBufferedOutputStream because the default implementation
                    // accesses that header and we want to catch any exceptions calling TryComputeLength here.

                    if (response.Content == null
                        || await ComputeContentLengthAsync(request, response, httpResponse, cancellationToken))
                    {
                        bool bufferOutput = _bufferPolicySelector.UseBufferedOutputStream(response);

                        if (bufferOutput)
                        {
                            var responseBodyFeature = httpContext.Features.Get<IHttpResponseBodyFeature>();
                            responseBodyFeature?.DisableBuffering();
                        }
                        else if (response.Content != null)
                        {
                            response = await BufferResponseContentAsync(request, response, cancellationToken);
                        }

                        if (await PrepareHeadersAsync(request, response, httpResponse, cancellationToken))
                        {
                            await SendResponseMessageAsync(request, response, httpContext, cancellationToken);
                        }
                    }
                }
            }
            finally
            {
                request.DisposeRequestResources();
                request.Dispose();
                if (response != null)
                {
                    response.Dispose();
                }
            }

            // Call the next component if no route matched
            if (callNext)
            {
                await _next(httpContext);
            }
        }

        private static void SetPrincipal(IPrincipal user)
        {
            if (user != null)
            {
                Thread.CurrentPrincipal = user;
            }
        }

        private static bool IsSoftNotFound(HttpRequestMessage request, HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (request.Properties.TryGetValue(HttpPropertyKeys.NoRouteMatched, out var notFound)
                    && (bool)notFound)
                {
                    return true;
                }
            }
            return false;
        }

        private async Task<HttpResponseMessage> BufferResponseContentAsync(HttpRequestMessage request,
            HttpResponseMessage response, CancellationToken cancellationToken)
        {
            ExceptionDispatchInfo exceptionInfo;

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await response.Content.LoadIntoBufferAsync();
                return response;
            }
            catch (OperationCanceledException)
            {
                // Propogate the canceled task without calling exception loggers or handlers.
                throw;
            }
            catch (Exception exception)
            {
                exceptionInfo = ExceptionDispatchInfo.Capture(exception);
            }

            // If the content can't be buffered, create a buffered error response for the exception
            // This code will commonly run when a formatter throws during the process of serialization

            Debug.Assert(exceptionInfo.SourceException != null);

            ExceptionContext exceptionContext = new ExceptionContext(exceptionInfo.SourceException,
                AspNetCoreExceptionCatchBlocks.HttpMessageHandlerAdapterBufferContent, request, response);

            await _exceptionLogger.LogAsync(exceptionContext, cancellationToken);
            HttpResponseMessage errorResponse = await _exceptionHandler.HandleAsync(exceptionContext,
                cancellationToken);

            response.Dispose();

            if (errorResponse == null)
            {
                exceptionInfo.Throw();
                return null;
            }

            // We have an error response to try to buffer and send back.

            response = errorResponse;
            cancellationToken.ThrowIfCancellationRequested();

            Exception errorException;

            try
            {
                // Try to buffer the error response and send it back.
                await response.Content.LoadIntoBufferAsync();
                return response;
            }
            catch (OperationCanceledException)
            {
                // Propogate the canceled task without calling exception loggers.
                throw;
            }
            catch (Exception exception)
            {
                errorException = exception;
            }

            // We tried to send back an error response with content, but we couldn't. It's an edge case; the best we
            // can do is to log that exception and send back an empty 500.

            ExceptionContext errorExceptionContext = new ExceptionContext(errorException,
                AspNetCoreExceptionCatchBlocks.HttpMessageHandlerAdapterBufferError, request, response);
            await _exceptionLogger.LogAsync(errorExceptionContext, cancellationToken);

            response.Dispose();
            return request.CreateResponse(HttpStatusCode.InternalServerError);
        }

        // Prepares Content-Length and Transfer-Encoding headers.
        private Task<bool> PrepareHeadersAsync(HttpRequestMessage request, HttpResponseMessage response,
            HttpResponse httpResponse, CancellationToken cancellationToken)
        {
            Contract.Assert(response != null);
            HttpResponseHeaders responseHeaders = response.Headers;
            Contract.Assert(responseHeaders != null);
            HttpContent content = response.Content;
            bool isTransferEncodingChunked = responseHeaders.TransferEncodingChunked == true;
            HttpHeaderValueCollection<TransferCodingHeaderValue> transferEncoding = responseHeaders.TransferEncoding;

            if (content != null)
            {
                HttpContentHeaders contentHeaders = content.Headers;
                Contract.Assert(contentHeaders != null);

                if (isTransferEncodingChunked)
                {
                    // According to section 4.4 of the HTTP 1.1 spec, HTTP responses that use chunked transfer
                    // encoding must not have a content length set. Chunked should take precedence over content
                    // length in this case because chunked is always set explicitly by users while the Content-Length
                    // header can be added implicitly by System.Net.Http.
                    contentHeaders.ContentLength = null;
                }
                else
                {
                    // Copy the response content headers only after ensuring they are complete.
                    // We ask for Content-Length first because HttpContent lazily computes this header and only
                    // afterwards writes the value into the content headers.
                    return ComputeContentLengthAsync(request, response, httpResponse, cancellationToken);
                }
            }

            // Ignore the Transfer-Encoding header if it is just "chunked"; the host will likely provide it when no
            // Content-Length is present (and if the host does not, there's not much better this code could do to
            // transmit the current response, since HttpContent is assumed to be unframed; in that case, silently drop
            // the Transfer-Encoding: chunked header).
            // HttpClient sets this header when it receives chunked content, but HttpContent does not include the
            // frames. The ASP.NET Core contract is to set this header only when writing chunked frames to the stream.
            // A Web API caller who desires custom framing would need to do a different Transfer-Encoding (such as
            // "identity, chunked").
            if (isTransferEncodingChunked && transferEncoding.Count == 1)
            {
                transferEncoding.Clear();
            }

            return Task.FromResult(true);
        }

        private Task<bool> ComputeContentLengthAsync(HttpRequestMessage request, HttpResponseMessage response,
            HttpResponse httpResponse, CancellationToken cancellationToken)
        {
            Contract.Assert(response != null);
            HttpResponseHeaders responseHeaders = response.Headers;
            Contract.Assert(responseHeaders != null);
            HttpContent content = response.Content;
            Contract.Assert(content != null);
            HttpContentHeaders contentHeaders = content.Headers;
            Contract.Assert(contentHeaders != null);

            Exception exception;

            try
            {
                var unused = contentHeaders.ContentLength;

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            return HandleTryComputeLengthExceptionAsync(exception, request, response, httpResponse, cancellationToken);
        }

        private async Task<bool> HandleTryComputeLengthExceptionAsync(Exception exception, HttpRequestMessage request,
            HttpResponseMessage response, HttpResponse httpResponse, CancellationToken cancellationToken)
        {
            Contract.Assert(httpResponse != null);

            ExceptionContext exceptionContext = new ExceptionContext(exception,
                AspNetCoreExceptionCatchBlocks.HttpMessageHandlerAdapterComputeContentLength, request, response);
            await _exceptionLogger.LogAsync(exceptionContext, cancellationToken);

            // Send back an empty error response if TryComputeLength throws.
            httpResponse.StatusCode = (int)HttpStatusCode.InternalServerError;
            SetHeadersForEmptyResponse(httpResponse);
            return false;
        }

        private Task SendResponseMessageAsync(HttpRequestMessage request, HttpResponseMessage response,
            HttpContext httpContext, CancellationToken cancellationToken)
        {
            var httpResponse = httpContext.Response;
            httpResponse.StatusCode = (int)response.StatusCode;
            var feature = httpContext.Features.Get<IHttpResponseFeature>();
            if (feature != null)
            {
                feature.ReasonPhrase = response.ReasonPhrase;
            }

            // Copy non-content headers
            var responseHeaders = httpResponse.Headers;
            foreach (var header in response.Headers)
            {
                responseHeaders[header.Key] = new StringValues(header.Value.ToArray());
            }

            HttpContent responseContent = response.Content;
            if (responseContent == null)
            {
                SetHeadersForEmptyResponse(httpResponse);
                return Task.CompletedTask;
            }
            else
            {
                // Copy content headers
                foreach (KeyValuePair<string, IEnumerable<string>> contentHeader in responseContent.Headers)
                {
                    responseHeaders[contentHeader.Key] = contentHeader.Value.ToArray();
                }

                // Copy body
                return SendResponseContentAsync(request, response, httpContext, cancellationToken);
            }
        }

        private static void SetHeadersForEmptyResponse(HttpResponse httpResponse)
        {
            // Set the content-length to 0 to prevent the server from sending back the response chunked
            httpResponse.ContentLength = 0;
        }

        private async Task SendResponseContentAsync(HttpRequestMessage request, HttpResponseMessage response,
            HttpContext httpContext, CancellationToken cancellationToken)
        {
            Exception exception;
            cancellationToken.ThrowIfCancellationRequested();
            var httpResponse = httpContext.Response;
            try
            {
                await response.Content.CopyToAsync(httpResponse.Body);
                return;
            }
            catch (OperationCanceledException)
            {
                // Propogate the canceled task without calling exception loggers;
                throw;
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            // We're streaming content, so we can only call loggers, not handlers, as we've already (possibly) send the
            // status code and headers across the wire. Log the exception, but then just abort.
            ExceptionContext exceptionContext = new ExceptionContext(exception,
                AspNetCoreExceptionCatchBlocks.HttpMessageHandlerAdapterStreamContent, request, response);
            await _exceptionLogger.LogAsync(exceptionContext, cancellationToken);

            httpContext.Abort();
        }

        /// <summary>
        /// Releases unmanaged and optionally managed resources.
        /// </summary>
        /// <param name="disposing">
        /// <see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release
        /// only unmanaged resources.
        /// </param>
        /// <remarks>
        /// This class implements <see cref="IDisposable"/> for legacy reasons. New callers should instead provide a
        /// cancellation token via <see cref="AppDisposing"/> using the constructor that takes
        /// <see cref="HttpMessageHandlerOptions"/>.
        /// </remarks>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                OnAppDisposing();
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// This class implements <see cref="IDisposable"/> for legacy reasons. New callers should instead provide a
        /// cancellation token via <see cref="AppDisposing"/> using the constructor that takes
        /// <see cref="HttpMessageHandlerOptions"/>.
        /// </remarks>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void OnAppDisposing()
        {
            if (!_disposed)
            {
                _messageInvoker.Dispose();
                _disposed = true;
            }
        }
    }
}
