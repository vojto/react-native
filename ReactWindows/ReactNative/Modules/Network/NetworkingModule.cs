﻿using Newtonsoft.Json.Linq;
using ReactNative.Bridge;
using ReactNative.Collections;
using ReactNative.Modules.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace ReactNative.Modules.Network
{
    /// <summary>
    /// Implements the XMLHttpRequest JavaScript interface.
    /// </summary>
    public class NetworkingModule : ReactContextNativeModuleBase
    {
        private const int MaxChunkSizeBetweenFlushes = 8 * 1024; // 8kb
        private readonly IHttpClient _client;
        private readonly TaskCancellationManager<int> _tasks;

        private bool _shuttingDown;

        /// <summary>
        /// Instantiates the <see cref="NetworkingModule"/>.
        /// </summary>
        /// <param name="reactContext">The context.</param>
        internal NetworkingModule(ReactContext reactContext)
            : this(CreateDefaultHttpClient(), reactContext)
        {
        }

        /// <summary>
        /// Instantiates the <see cref="NetworkingModule"/>.
        /// </summary>
        /// <param name="client">The HTTP client.</param>
        /// <param name="reactContext">The context.</param>
        internal NetworkingModule(IHttpClient client, ReactContext reactContext)
            : base(reactContext)
        {
            _client = client;
            _tasks = new TaskCancellationManager<int>();
        }

        /// <summary>
        /// The name of the native module.
        /// </summary>
        public override string Name
        {
            get
            {
                return "RCTNetworking";
            }
        }

        private RCTDeviceEventEmitter EventEmitter
        {
            get
            {
                return Context.GetJavaScriptModule<RCTDeviceEventEmitter>();
            }
        }

        /// <summary>
        /// Send an HTTP request on the networking module.
        /// </summary>
        /// <param name="method">The HTTP method.</param>
        /// <param name="url">The URL.</param>
        /// <param name="requestId">The request ID.</param>
        /// <param name="headers">The headers.</param>
        /// <param name="data">The request data.</param>
        /// <param name="useIncrementalUpdates">
        /// <code>true</code> if incremental updates are allowed.
        /// </param>
        /// <param name="timeout">The timeout.</param>
        [ReactMethod]
        public void sendRequest(
            string method,
            Uri url,
            int requestId,
            string[][] headers,
            JObject data,
            bool useIncrementalUpdates,
            int timeout)
        {
            if (method == null)
                throw new ArgumentNullException(nameof(method));
            if (url == null)
                throw new ArgumentNullException(nameof(url));

            var request = new HttpRequestMessage(new HttpMethod(method), url);

            var headerData = default(HttpContentHeaderData);
            if (headers != null)
            {
                headerData = HttpContentHelpers.ExtractHeaders(headers);
                ApplyHeaders(request, headers);
            }

            if (data != null)
            {
                var body = data.Value<string>("string");
                var uri = default(string);
                var formData = default(JArray);
                if (body != null)
                {
                    if (headerData.ContentType == null)
                    {
                        OnRequestError(requestId, "Payload is set but no 'content-type' header specified.", false);
                        return;
                    }

                    request.Content = HttpContentHelpers.CreateFromBody(headerData, body);
                }
                else if ((uri = data.Value<string>("uri")) != null)
                {
                    throw new NotImplementedException("HTTP handling for file payloads not yet implemented.");
                }
                else if ((formData = data.Value<JArray>("formData")) != null)
                {
                    // TODO: (#388) Add support for form data.
                    throw new NotImplementedException("HTTP handling for FormData not yet implemented.");
                }
            }

            _tasks.Add(requestId, token => ProcessRequestAsync(
                requestId,
                useIncrementalUpdates,
                timeout,
                request,
                token));
        }

        /// <summary>
        /// Abort an HTTP request with the given request ID.
        /// </summary>
        /// <param name="requestId">The request ID.</param>
        [ReactMethod]
        public void abortRequest(int requestId)
        {
            _tasks.Cancel(requestId);
        }

        /// <summary>
        /// Called before a <see cref="IReactInstance"/> is disposed.
        /// </summary>
        public override void OnReactInstanceDispose()
        {
            _shuttingDown = true;
        }

        private async Task ProcessRequestAsync(
            int requestId,
            bool useIncrementalUpdates,
            int timeout,
            HttpRequestMessage request,
            CancellationToken token)
        {
            var timeoutSource = timeout > 0
                ? new CancellationTokenSource(timeout)
                : new CancellationTokenSource();

            using (timeoutSource)
            {
                try
                {
                    using (token.Register(timeoutSource.Cancel))
                    using (var response = await _client.SendRequestAsync(request, timeoutSource.Token))
                    {
                        OnResponseReceived(requestId, response);

                        if (useIncrementalUpdates)
                        {
                            using (var inputStream = await response.Content.ReadAsInputStreamAsync())
                            using (var stream = inputStream.AsStreamForRead())
                            {
                                await ProcessResponseIncrementalAsync(requestId, stream, timeoutSource.Token);
                                OnRequestSuccess(requestId);
                            }
                        }
                        else
                        {
                            if (response.Content != null)
                            {
                                var responseBody = await response.Content.ReadAsStringAsync();
                                if (responseBody != null)
                                {
                                    OnDataReceived(requestId, responseBody);
                                }
                            }

                            OnRequestSuccess(requestId);
                        }
                    }
                }
                catch (OperationCanceledException ex)
                when (ex.CancellationToken == timeoutSource.Token)
                {
                    // Cancellation was due to timeout
                    if (!token.IsCancellationRequested)
                    {
                        OnRequestError(requestId, ex.Message, true);
                    }
                }
                catch (Exception ex)
                {
                    if (_shuttingDown)
                    {
                        return;
                    }

                    OnRequestError(requestId, ex.Message, false);
                }
                finally
                {
                    request.Dispose();
                }
            }
        }

        private async Task ProcessResponseIncrementalAsync(int requestId, Stream stream, CancellationToken token)
        {
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, MaxChunkSizeBetweenFlushes, true))
            {
                var buffer = new char[MaxChunkSizeBetweenFlushes];
                var read = default(int);
                while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    OnDataReceived(requestId, new string(buffer, 0, read));
                }
            }
        }

        private void OnResponseReceived(int requestId, HttpResponseMessage response)
        {
            var headerData = new JObject();
            TranslateHeaders(headerData, response.Headers);

            if (response.Content != null)
            {
                TranslateHeaders(headerData, response.Content.Headers);
            }

            var args = new JArray
            {
                requestId,
                (int)response.StatusCode,
                headerData,
                response.RequestMessage.RequestUri.AbsolutePath,
            };

            EventEmitter.emit("didReceiveNetworkResponse", args);
        }

        private void OnDataReceived(int requestId, string responseBody)
        {
            EventEmitter.emit("didReceiveNetworkData", new JArray
            {
                requestId,
                responseBody,
            });
        }

        private void OnRequestError(int requestId, string message, bool timeout)
        {
            EventEmitter.emit("didCompleteNetworkResponse", new JArray
            {
                requestId,
                message,
                timeout
            });
        }

        private void OnRequestSuccess(int requestId)
        {
            EventEmitter.emit("didCompleteNetworkResponse", new JArray
            {
                requestId,
                null,
            });
        }

        private static void ApplyHeaders(HttpRequestMessage request, string[][] headers)
        {
            foreach (var header in headers)
            {
                var key = header[0];
                switch (key.ToLowerInvariant())
                {
                    case "content-encoding":
                    case "content-length":
                    case "content-type":
                        break;
                    default:
                        request.Headers[key] = header[1];
                        break;
                }
            }
        }

        private static void TranslateHeaders(JObject headerData, IDictionary<string, string> headers)
        {
            foreach (var header in headers)
            {
                if (headerData.ContainsKey(header.Key))
                {
                    var existing = headerData[header.Key].Value<string>();
                    headerData[header.Key] = existing + ", " + header.Value;
                }
                else
                {
                    headerData.Add(header.Key, header.Value);
                }
            }
        }

        private static IHttpClient CreateDefaultHttpClient()
        {
            return new DefaultHttpClient(
                new HttpClient(
                    new HttpBaseProtocolFilter
                    {
                        AllowAutoRedirect = false,
                    }));
        }
    }
}
