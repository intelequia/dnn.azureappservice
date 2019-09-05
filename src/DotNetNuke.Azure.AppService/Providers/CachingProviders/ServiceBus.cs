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

using DotNetNuke.Common;
using DotNetNuke.Common.Utilities;
using DotNetNuke.Entities.Host;
using DotNetNuke.Instrumentation;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Management;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;

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

        internal static Lazy<List<TopicClient>> LazyTopicClientPool = new Lazy<List<TopicClient>>(() =>
        {
            Logger.Info("Initializing ServiceBusCachingProvider topic client pool");
            var _topicClientPool = new List<TopicClient>();
            try
            {
                if (!string.IsNullOrEmpty(ServiceBusConnectionString))
                {
                    var connectionStringBuilder =
                        new ServiceBusConnectionStringBuilder(ServiceBusConnectionString)
                        {
                            EntityPath = TopicName
                        };
                    var retryPolicy = new RetryExponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 10);
                    for (var index = 0; index < MaxPoolSize; index++)
                    {
                        var topicClient = new TopicClient(connectionStringBuilder, retryPolicy);
                        _topicClientPool.Add(topicClient);
                    }
                }
                if (_topicClientPool.Count > 0)
                {
                    Logger.Info($"Topic client pool successfully initialzed (Pool size: {_topicClientPool.Count})");
                }
                else
                {
                    Logger.Warn("Service Bus is disabled. Check application settings");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error initializing the service bus topic client pool", ex);

            }
            return _topicClientPool;
        });

        internal static List<TopicClient> TopicClientPool
        {
            get
            {
                return LazyTopicClientPool.Value;
            }
        }

        internal static TopicClient GetTopicClientFromPool()
        {
            if (string.IsNullOrEmpty(ServiceBusConnectionString) || string.IsNullOrEmpty(TopicName))
                return null;
            var index = new Random().Next(MaxPoolSize - 1);
            return TopicClientPool[index];
        }

        internal static SubscriptionClient _subscriptionClient;
        internal static SubscriptionClient SubscriptionClient
        {
            get {
                if (_subscriptionClient == null)
                {
                    // Check if subscription exists
                    CreateServerSubscriptions();

                    var currentServer = ServerController.GetServers().Single(s => s.ServerName == Globals.ServerName && s.IISAppName == Globals.IISAppName);
                    var retryPolicy = new RetryExponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 10);
                    _subscriptionClient = new SubscriptionClient(ServiceBusConnectionString, TopicName, $"server-{currentServer.ServerID}", ReceiveMode.ReceiveAndDelete, retryPolicy);
                }
                return _subscriptionClient;
            }
        }

        private static void CreateServerSubscriptions()
        {
            Logger.Info("Verifying all servers subscription existence");
            var servers = ServerController.GetServers();
            var client = new ManagementClient(ServiceBusConnectionString);

            // Delete old server subscriptions from already deleted servers
            var subscriptions = client.GetSubscriptionsAsync(TopicName).Result;
            foreach (var subscription in subscriptions)
            {
                if (!servers.Any(s => $"server-{s.ServerID}" == subscription.SubscriptionName))
                {
                    try
                    {
                        Logger.Info($"Deleting subscription '{subscription.SubscriptionName}' because server doesn't exist");
                        client.DeleteSubscriptionAsync(TopicName, $"{subscription.SubscriptionName}").RunSynchronously();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Error deleting topic subscription", ex);
                    }
                }
            }

            // Create server subscriptions if they don't exist
            var enabledServers = servers.Where(x => x.Enabled);
            foreach (var server in enabledServers)
            {
                try
                {
                    if (!client.SubscriptionExistsAsync(TopicName, $"server-{server.ServerID}").Result)
                    {
                        Logger.Info($"Creating topic subscription 'server-{server.ServerID}' for server '{server.ServerName}'");
                        client.CreateSubscriptionAsync(new SubscriptionDescription(TopicName, $"server-{server.ServerID}"));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Error creating topic subscription", ex);
                }
            }         
        }
    }

}
