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

using Azure.Messaging.ServiceBus;
using DotNetNuke.Entities.Host;
using DotNetNuke.Instrumentation;
using DotNetNuke.Services.Cache;
using DotNetNuke.Web.Api;
using Newtonsoft.Json;
using System.Threading.Tasks;

namespace DotNetNuke.Azure.AppService.Providers.CachingProviders
{
    public class ServiceBusCachingSubscriber : IServiceRouteMapper
    {
        private static readonly ILog Logger = LoggerSource.Instance.GetLogger(typeof(ServiceBusCachingSubscriber));
        private static ServiceBusProcessor _processor;

        public void RegisterRoutes(IMapRoute mapRouteManager)
        {
            // This is fired once on Application startup

            if (string.IsNullOrEmpty(ServiceBus.ServiceBusConnectionString) || string.IsNullOrEmpty(ServiceBus.TopicName))
            {
                Logger.Warn("ServiceBusConnectionString or TopicName is empty. Can't initialize ServiceBusCachingProvider");
                return;
            }

            _processor = ServiceBus.CreateProcessorForCurrentServer();
            if (_processor == null)
            {
                Logger.Warn("Could not create ServiceBus processor. ServiceBusCachingProvider subscriber not initialized");
                return;
            }

            _processor.ProcessMessageAsync += ProcessMessagesAsync;
            _processor.ProcessErrorAsync += ExceptionReceivedHandler;
            _processor.StartProcessingAsync().GetAwaiter().GetResult();
            Logger.Info("ServiceBusCachingProvider subscriber initialized and listening");

        }

        static async Task ProcessMessagesAsync(ProcessMessageEventArgs args)
        {
            var messageBody = args.Message.Body.ToString();
            Logger.Info($"Received message: SequenceNumber:{args.Message.SequenceNumber} Body:{messageBody}");
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
        static Task ExceptionReceivedHandler(ProcessErrorEventArgs args)
        {
            Logger.Error($"Message handler encountered an exception {args.Exception}.");
            Logger.Error($"Exception context for troubleshooting (ErrorSource: {args.ErrorSource}; Entity Path: {args.EntityPath})");
            return Task.CompletedTask;
        }
    }
}
