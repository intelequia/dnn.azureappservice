﻿#region Copyright

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
using DotNetNuke.Entities.Host;
using DotNetNuke.Instrumentation;
using DotNetNuke.Services.Cache;
using Newtonsoft.Json;
using System.Configuration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace DotNetNuke.Azure.AppService.Providers.CachingProviders
{
    public class ServiceBusCachingProvider: CachingProvider
    {
        private static readonly ILog Logger = LoggerSource.Instance.GetLogger(typeof(ServiceBusCachingProvider));
        private const string CacheConfigFilePath = "~/DesktopModules/AzureAppService/Caching.config";

        private static CachingConfig _cachingConfig;
        private static CachingConfig CachingConfig
        {
            get
            {
                if (_cachingConfig == null)
                {
                    _cachingConfig = CachingConfig.GetCacheConfig(HttpContext.Current.Server.MapPath(CacheConfigFilePath));
                }
                return _cachingConfig;
            }
        }

        public override bool IsWebFarm()
        {
            // Notify possible 3rd party module dependencies that this caching provider is supporting "WebFarm" mode.
            // Better implementation would be to return the following, but could cause undesired effects on scale out/in operations
            //var self = ServerController.GetServers().Single(s => s.ServerName == Globals.ServerName && s.IISAppName == Globals.IISAppName);
            //return ServerController.GetEnabledServers().Where(s => !(s.ServerName == self.ServerName
            //                                                                            && s.IISAppName == self.IISAppName)
            //                                                                            && (s.ServerGroup == self.ServerGroup || string.IsNullOrEmpty(self.ServerGroup))
            //                                                                            && !string.IsNullOrEmpty(s.Url))
            //                                                        .Count() > 0;
            return true;
        }

        public override void Clear(string type, string data)
        {
            base.ClearCacheInternal(type, data, true);
            if (!CacheExpirationDisable)
            {
                SendEventHubNotificationAsync("Clear_" + type, data).GetAwaiter().GetResult();
            }
            else
            {
                Logger.WarnFormat($"Cache expiration disabled. Clear won't be sent to Service Bus (type: {type}, data: {data})");
            }
        }

        public override void Remove(string key)
        {
            base.RemoveInternal(key);

            if (!CacheExpirationDisable)
            {
                SendEventHubNotificationAsync("Remove", key).GetAwaiter().GetResult();
            }
            else
            {
                Logger.WarnFormat($"Cache expiration disabled. Remove won't be sent to Service Bus (key: {key})");
            }
        }

        private static bool IsUpgradeRequest(HttpRequest request)
        {
            return request.Url.LocalPath.ToLower().Contains("/upgradewizard.aspx")
                || request.Url.AbsoluteUri.ToLower().Contains("/install.aspx?mode=upgrade")
                || request.Url.AbsoluteUri.ToLower().Contains("/install.aspx?mode=installresources");
        }

        private async Task SendEventHubNotificationAsync(string message, string data)
        {
            // On installation and upgrades, don't send cache syncs to avoid installation failures
            if (HttpContext.Current != null && IsUpgradeRequest(HttpContext.Current.Request))
            {
                return;
            }

            if (CachingConfig != null && CachingConfig.CacheExclusions.Any(x => x.Message == message && x.Data == data))
            {
                Logger.Debug($"Message excluded by caching exclusions (message: {message}; data: {data}");
                return;
            }

            // Check if there is more than servers than self
            if (!bool.TryParse(ConfigurationManager.AppSettings["ServiceBusCachingProvider.Debug"], out var isDebug) && !isDebug)
            {
                var self = ServerController.GetServers().FirstOrDefault(s => s.ServerName == Globals.ServerName && s.IISAppName == Globals.IISAppName);
                if (self == null) 
                {
                    // This can happen if the server is not registered in the database for some reason 
                    ServerController.ClearCachedServers();
                }                
                if (self == null || ServerController.GetEnabledServers().Where(s => !(s.ServerName == self.ServerName
                                                                            && s.IISAppName == self.IISAppName)
                                                                            && (s.ServerGroup == self.ServerGroup || string.IsNullOrEmpty(self.ServerGroup))
                                                                            && !string.IsNullOrEmpty(s.Url))
                                                        .Count() == 0)
                {
                    return;
                }
            }

            var client = ServiceBus.GetTopicClientFromPool();
            var serverName = ServerController.GetExecutingServerName();
            var messageData = new CachingMessage
            {
                From = serverName,
                Message = message,
                Data = data
            };
            var messageBody = await Task.Factory.StartNew(() => JsonConvert.SerializeObject(messageData)).ConfigureAwait(false);
            if (client == null)
            {
                Logger.Warn($"Service bus disabled: {messageBody}");
            }
            else
            {
                Logger.Debug($"Sending message: {messageBody}");
                await client.SendAsync(new Microsoft.Azure.ServiceBus.Message(Encoding.UTF8.GetBytes(messageBody))).ConfigureAwait(false);
            }
        }

        internal void ProcessMessage(string message, string data)
        {
            switch (message.Substring(0, 6))
            {
                case "Remove":
                    base.RemoveInternal(data);
                    break;
                case "Clear_":
                    base.ClearCacheInternal(message.Substring(6), data, true);
                    break;
            }
        }

    }
}
