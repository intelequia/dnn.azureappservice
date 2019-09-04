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

using DotNetNuke.Common.Utilities;
using DotNetNuke.Instrumentation;
using Microsoft.Azure.ServiceBus;
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

        internal static string _subscriptionName = string.Empty;
        internal static string SubscriptionName
        {
            get
            {
                if (string.IsNullOrEmpty(_subscriptionName))
                {
                    _subscriptionName = GetProviderConfigAttribute("subscriptionName", "dnnsubs");
                }
                return _subscriptionName;
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
                    for (var index = 0; index < MaxPoolSize; index++)
                    {
                        var topicClient = new TopicClient(connectionStringBuilder);
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
                    _subscriptionClient = new SubscriptionClient(ServiceBusConnectionString, TopicName, SubscriptionName);
                }
                return _subscriptionClient;
            }
        }

    }

}
