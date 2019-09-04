#region Copyright

// 
// Intelequia Software solutions - https://intelequia.com
// Copyright (c) 2019
// by Intelequia Software Solutions
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated 
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and 
// to permit persons to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or substantial portions 
// of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED 
// TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL 
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF 
// CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.

#endregion

using DotNetNuke.Entities.Host;
using DotNetNuke.Instrumentation;
using DotNetNuke.Services.Cache;
using DotNetNuke.Web.Api;
using Microsoft.Azure.ServiceBus;
using Newtonsoft.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetNuke.Azure.AppService.Providers.CachingProviders
{
    public class ServiceBusCachingSubscriber : IServiceRouteMapper
    {
        private static readonly ILog Logger = LoggerSource.Instance.GetLogger(typeof(ServiceBusCachingSubscriber));

        public void RegisterRoutes(IMapRoute mapRouteManager)
        {
            // This is fired once on Application startup

            // Configure the message handler options in terms of exception handling, number of concurrent messages to deliver, etc.
            var messageHandlerOptions = new MessageHandlerOptions(ExceptionReceivedHandler)
            {
                // Maximum number of concurrent calls to the callback ProcessMessagesAsync(), set to 1 for simplicity.
                // Set it according to how many messages the application wants to process in parallel.
                MaxConcurrentCalls = 1,

                // Indicates whether the message pump should automatically complete the messages after returning from user callback.
                // False below indicates the complete operation is handled by the user callback as in ProcessMessagesAsync().
                AutoComplete = true
            };

            // Register the function that processes messages.
            ServiceBus.SubscriptionClient.RegisterMessageHandler(ProcessMessagesAsync, messageHandlerOptions);

        }

        static async Task ProcessMessagesAsync(Message message, CancellationToken token)
        {
            var messageBody = Encoding.UTF8.GetString(message.Body);
            Logger.Info($"Received message: SequenceNumber:{message.SystemProperties.SequenceNumber} Body{messageBody}");
            var eventMessage = JsonConvert.DeserializeObject<CachingMessage>(messageBody);
            ProcessEventInternal(eventMessage);
            await Task.CompletedTask;
        }

        static internal void ProcessEventInternal(CachingMessage message)
        {
            if (message.From != ServerController.GetExecutingServerName() 
                && !(string.IsNullOrEmpty(message.Message)) && !(string.IsNullOrEmpty(message.Data)))
            {
                var cachingProvider = CachingProvider.Instance();
                if (cachingProvider is ServiceBusCachingProvider)
                {
                    (cachingProvider as ServiceBusCachingProvider).ProcessMessage(message.Message, message.Data);
                }
            }
        }

        // Use this handler to examine the exceptions received on the message pump.
        static Task ExceptionReceivedHandler(ExceptionReceivedEventArgs exceptionReceivedEventArgs)
        {
            Logger.Error($"Message handler encountered an exception {exceptionReceivedEventArgs.Exception}.");
            var context = exceptionReceivedEventArgs.ExceptionReceivedContext;
            Logger.Error($"Exception context for troubleshooting (Endpoint: {context.Endpoint}; Entity Path: {context.EntityPath}; Entity Path: {context.EntityPath}");
            return Task.CompletedTask;
        }
    }
}
