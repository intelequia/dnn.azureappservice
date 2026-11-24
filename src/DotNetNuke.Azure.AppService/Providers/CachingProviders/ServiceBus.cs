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
using Azure.Messaging.ServiceBus.Administration;
using DotNetNuke.Common;
using DotNetNuke.Common.Utilities;
using DotNetNuke.Entities.Host;
using DotNetNuke.Instrumentation;
using System;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;

namespace DotNetNuke.Azure.AppService.Providers.CachingProviders
{
    internal class ServiceBus
    {
        private static readonly ILog Logger = LoggerSource.Instance.GetLogger(typeof(ServiceBus));

        internal static int _maxPoolSize = -1;
        internal static int MaxPoolSize
        {
            get
            {
                if (_maxPoolSize == -1)
                {
                    _maxPoolSize = int.Parse(GetProviderConfigAttribute("serviceBusPoolSize", "20"));
                }
                return _maxPoolSize;
            }
        }

        internal static int _maxConcurrentCalls = -1;
        internal static int MaxConcurrentCalls
        {
            get
            {
                if (_maxConcurrentCalls == -1)
                {
                    _maxConcurrentCalls = int.Parse(GetProviderConfigAttribute("maxConcurrentCalls", "5"));
                }
                return _maxConcurrentCalls;
            }
        }
        internal static string _topicName = string.Empty;
        internal static string TopicName
        {
            get {
                if (string.IsNullOrEmpty(_topicName))
                {
                    _topicName = GetProviderConfigAttribute("topicName", "dnntopic");
                }
                return _topicName;
            }
        }

        internal static string _serviceBusConnectionString = string.Empty;
        internal static string ServiceBusConnectionString
        {
            get
            {
                if (string.IsNullOrEmpty(_serviceBusConnectionString))
                {
                    _serviceBusConnectionString = ConfigurationManager.ConnectionStrings["ServiceBusCachingProvider"].ConnectionString;
                }
                return _serviceBusConnectionString;
            }
        }

        private static string GetProviderConfigAttribute(string attributeName, string defaultValue = "")
        {
            var provider = Config.GetProvider("caching", nameof(ServiceBusCachingProvider));
            if (provider != null && provider.Attributes.AllKeys.Contains(attributeName))
                return provider.Attributes[attributeName];
            return defaultValue;
        }

        internal static Lazy<ServiceBusClient> LazyClient = new Lazy<ServiceBusClient>(() =>
        {
            Logger.Info("Initializing ServiceBusCachingProvider client");
            try
            {
                if (!string.IsNullOrEmpty(ServiceBusConnectionString))
                {
                    var client = new ServiceBusClient(ServiceBusConnectionString, new ServiceBusClientOptions
                    {
                        RetryOptions = new ServiceBusRetryOptions
                        {
                            Mode = ServiceBusRetryMode.Exponential,
                            Delay = TimeSpan.FromSeconds(1),
                            MaxDelay = TimeSpan.FromSeconds(30),
                            MaxRetries = 10
                        }
                    });
                    Logger.Info("ServiceBus client successfully initialized");
                    return client;
                }
                Logger.Warn("Service Bus is disabled. Check application settings");
            }
            catch (Exception ex)
            {
                Logger.Error("Error initializing the service bus client", ex);
            }
            return null;
        });

        internal static ServiceBusClient Client => LazyClient.Value;

        internal static Lazy<ServiceBusSender> LazySender = new Lazy<ServiceBusSender>(() =>
        {
            if (Client != null)
            {
                var sender = Client.CreateSender(TopicName);
                Logger.Info($"ServiceBus sender successfully initialized (Topic: {TopicName})");
                return sender;
            }
            return null;
        });

        internal static ServiceBusSender Sender => LazySender.Value;

        internal static ServiceBusProcessor CreateProcessorForCurrentServer()
        {
            if (Client == null) return null;
            var currentServer = ServerController.GetServers().Single(s => s.ServerName == Globals.ServerName && s.IISAppName == Globals.IISAppName);
            RefreshServerSubscriptions(currentServer);
            return Client.CreateProcessor(TopicName, $"server-{currentServer.ServerID}", new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = MaxConcurrentCalls,
                ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete
            });
        }

        private static void RefreshServerSubscriptions(ServerInfo currentServer)
        {
            Logger.Info("Verifying all servers subscription existence");
            var servers = ServerController.GetServers();
            var adminClient = new ServiceBusAdministrationClient(ServiceBusConnectionString);
            // Create server subscriptions if they don't exist
            var enabledServers = servers.Where(x => x.Enabled);
            foreach (var server in enabledServers)
            {
                try
                {
                    if (!adminClient.SubscriptionExistsAsync(TopicName, $"server-{server.ServerID}").GetAwaiter().GetResult())
                    {
                        Logger.Info($"Creating topic subscription 'server-{server.ServerID}' for server '{server.ServerName}'");
                        adminClient.CreateSubscriptionAsync(new CreateSubscriptionOptions(TopicName, $"server-{server.ServerID}")
                        {
                            AutoDeleteOnIdle = TimeSpan.FromDays(int.Parse(GetProviderConfigAttribute("autoDeleteOnIdleDays", "15"))),
                            DefaultMessageTimeToLive = TimeSpan.FromSeconds(int.Parse(GetProviderConfigAttribute("defaultMessageTimeToLiveInSeconds", "10"))),
                            MaxDeliveryCount = int.Parse(GetProviderConfigAttribute("maxDeliveryCount", "10")),
                            EnableBatchedOperations = true,
                            UserMetadata = ServerController.GetServerName(server)
                        }).GetAwaiter().GetResult();
                    }
                    else
                    {
                        if (server.ServerID == currentServer.ServerID)
                        {
                            PurgeSubscriptionAsync($"server-{server.ServerID}").GetAwaiter().GetResult();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Error creating topic subscription", ex);
                }
            }
        }

        private static async Task PurgeSubscriptionAsync(string subscriptionName)
        {
            try
            {
                Logger.Info($"Purging subscription '{subscriptionName}'");
                var receiver = Client.CreateReceiver(TopicName, subscriptionName, new ServiceBusReceiverOptions
                {
                    ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete
                });

                int batchSize = 100;
                var operationTimeout = TimeSpan.FromSeconds(3);

                do
                {
                    var messages = await receiver.ReceiveMessagesAsync(batchSize, operationTimeout).ConfigureAwait(false);
                    if (messages == null || messages.Count == 0)
                    {
                        break;
                    }
                }
                while (true);
                await receiver.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error purging subscription {subscriptionName}", ex);
            }
        }

    }

}
