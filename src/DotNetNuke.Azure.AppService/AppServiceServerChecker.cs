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
using DotNetNuke.Entities.Controllers;
using DotNetNuke.Entities.Host;
using DotNetNuke.Instrumentation;
using DotNetNuke.Services.Exceptions;
using DotNetNuke.Services.Log.EventLog;
using DotNetNuke.Services.Scheduling;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace DotNetNuke.Azure.AppService
{
    public class AppServiceServerChecker : SchedulerClient
    {
        private static readonly ILog Logger = LoggerSource.Instance.GetLogger(typeof(AppServiceServerChecker));

        private const string ARRAffinityCookieName = "ARRAffinity";

        public AppServiceServerChecker(ScheduleHistoryItem oItem) : base()
        {
            ScheduleHistoryItem = oItem;
        }

        public override void DoWork()
        {
            try
            {
                Logger.Trace("AppService server checker scheduled job starting");
                Progressing();
                PingServers();
                ChangeScheduledTasksServerAffinity();
                CleanupOldServers();
                ScheduleHistoryItem.Succeeded = true;
                Logger.Trace("AppService server checker scheduled job finished successfully");
            }
            catch (Exception ex)
            {
                ScheduleHistoryItem.Succeeded = false;
                ScheduleHistoryItem.AddLogNote($"Error: {ex}");
                Errored(ref ex);
                Exceptions.LogException(ex);
                Logger.Trace("AppService server checker scheduled job finished with errors");
            }
        }

        private void PingServers()
        {
            // Try pinging the other servers by doing a webrequest to keepalive.aspx
            var servers = ServerController.GetEnabledServers()
                .Where(x => x.ServerName != Globals.ServerName 
                            && x.IISAppName != Globals.IISAppName
                            && !string.IsNullOrEmpty(x.Url)
                            && !string.IsNullOrEmpty(x.UniqueId));

            if (servers.Count() != 0)
            {
                foreach (var server in servers)
                {
                    if (PingServer(server)) {
                        ResetPingFailureCounter(server);
                        var message = $"Server '{ServerController.GetServerName(server)}' pinged successfully.";
                        Logger.Info(message);
                        ScheduleHistoryItem.AddLogNote($"<p>{message}</p>");
                    }
                    else
                    {
                        IncreasePingFailureCounter(server);
                        LogServerEvent(server, EventLogController.EventLogType.WEBSERVER_PINGFAILED.ToString());
                        var message = $"Failed to ping server '{ServerController.GetServerName(server)}' ({server.PingFailureCount} ping failures).";
                        Logger.Warn(message);
                        ScheduleHistoryItem.AddLogNote($"<p>{message}</p>");
                        CheckMaxFailures(server);
                    }
                }
            }
            else
            {
                var message = ServerController.GetEnabledServers().Count() > 1
                    ? "Couldn't ping the other servers.Check that the host setting 'WebServer_ServerRequestAdapter' is set to 'DotNetNuke.Azure.AppService.AppServiceServerAdapter, DotNetNuke.Azure.AppService'."
                    : $"There are no other servers to ping. Only {Globals.ServerName} is enabled.";
                if (ServerController.GetEnabledServers().Count() > 1)
                {
                    Logger.Warn(message);
                }
                else
                {
                    Logger.Info(message);
                }
                ScheduleHistoryItem.AddLogNote($"<p>{message}</p>");
            }
        }

        private bool PingServer(ServerInfo server)
        {
            var url = $"{Globals.AddHTTP(server.Url)}/keepalive.aspx";
            try
            {
                var request = Globals.GetExternalRequest(url);
                if (request.CookieContainer == null)
                {
                    request.CookieContainer = new CookieContainer();
                }
                request.CookieContainer.Add(
                    new Cookie(ARRAffinityCookieName, server.UniqueId)
                    {
                        Domain = request.Host
                    });

                Logger.Trace($"Pinging server {url}");
                using (var response = request.GetResponse() as HttpWebResponse)
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        var message = $"Error pinging {url}: {response.StatusCode}";
                        Logger.Warn(message);
                        ScheduleHistoryItem.AddLogNote($"<p>{message}</p>");
                        return false;
                    }
                        
                    // The request was served by the expected server, or the ARR cookie changed and is now another server?
                    if ((response.Headers.AllKeys.Contains(ARRAffinityCookieName) 
                        && response.Headers[ARRAffinityCookieName] != server.UniqueId)
                          || (response.Headers["Set-Cookie"].Contains(ARRAffinityCookieName) 
                          && !response.Headers["Set-Cookie"].Contains(ARRAffinityCookieName + "=" + server.UniqueId)))
                    {
                        var message = $"Error pinging {url}: App Service returned another server";
                        Logger.Warn(message);
                        ScheduleHistoryItem.AddLogNote($"<p>{message}</p>");
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                var message = $"Error pinging {url}: {ex.Message}";
                Logger.Warn(message);
                ScheduleHistoryItem.AddLogNote(message);
                return false;
            }
        }
        private void ResetPingFailureCounter(ServerInfo server)
        {
            if (server.PingFailureCount > 0)
            {
                Logger.Info($"Resetting ping failure counter on server {server.ServerName}");
                server.PingFailureCount = 0;
                server.LastActivityDate = DateTime.Now;
                ServerController.UpdateServerActivity(server);
            }
        }

        private void IncreasePingFailureCounter(ServerInfo server)
        {
            server.PingFailureCount++;
            Logger.Trace($"Increasing ping failure counter on server {server.ServerName}");
            ServerController.UpdateServerActivity(server);
        }

        private void CheckMaxFailures(ServerInfo server)
        {             
            var maxFailures = HostController.Instance.GetInteger("WebServer_MaxPingFailures", 3 * ServerController.GetEnabledServers().Count());
            if (server.PingFailureCount >= maxFailures)
            {
                server.Enabled = false;
                ServerController.UpdateServer(server);

                LogServerEvent(server, EventLogController.EventLogType.WEBSERVER_DISABLED.ToString());
                var message = $"Disabling server {ServerController.GetServerName(server)} because reached {server.PingFailureCount} ping failures.";
                Logger.Warn(message);
                ScheduleHistoryItem.AddLogNote($"<p>{message}</p>");
            }
        }

        private void ChangeScheduledTasksServerAffinity()
        {
            try
            {
                var firstEnabledServer = ServerController.GetEnabledServers().OrderBy(x => x.KeyID).FirstOrDefault();
                var serverName = ServerController.GetServerName(firstEnabledServer);

                // Move all the tasks to the first enagbled server
                var scheduleItems = SchedulingController.GetSchedule();
                var movedSchedules = new List<string>();

                foreach (var item in scheduleItems)
                {
                    if (item.TypeFullName == "DotNetNuke.Azure.AppService.AppServiceServerChecker, DotNetNuke.Azure.AppService"
                        && item.Servers != Null.NullString)
                    {
                        item.Servers = Null.NullString;
                        SchedulingProvider.Instance().UpdateSchedule(item);
                        movedSchedules.Add(item.FriendlyName);
                    }
                    else if (item.Servers != $",{serverName},")
                    {
                        item.Servers = $",{serverName},";
                        SchedulingProvider.Instance().UpdateSchedule(item);
                        movedSchedules.Add(item.FriendlyName);
                    }
                }
                if (movedSchedules.Count() > 0)
                {
                    Logger.Info($"Changed schedules '{string.Join(",", movedSchedules)}' to '{serverName}'");
                    var log = new LogInfo { LogTypeKey = EventLogController.EventLogType.SCHEDULE_UPDATED.ToString() };
                    log.AddProperty("Updated schedules", string.Join(",", movedSchedules));
                    log.AddProperty("Changed to server", serverName);
                    LogController.Instance.AddLog(log);
                }
            }
            catch (Exception ex)
            {
                var message = $"Error changing server affinity: {ex.Message}";
                ScheduleHistoryItem.AddLogNote(message);
                Exceptions.LogException(ex);
            }
        }

        private void CleanupOldServers()
        {
            try
            {
                var maxDisabledServersLifeInDays = HostController.Instance.GetInteger("WebServer_MaxDisabledServersLifeInDays", 30);
                var oldServers = ServerController.GetServers().Where(x => !x.Enabled 
                    && x.LastActivityDate < DateTime.Now.AddDays(-maxDisabledServersLifeInDays));
                foreach (var server in oldServers)
                {
                    var serverName = ServerController.GetServerName(server);
                    ServerController.DeleteServer(server.ServerID);
                    var message = $"Server '{serverName}' was deleted because was disabled for more than {maxDisabledServersLifeInDays} days";
                    Logger.Info(message);
                    ScheduleHistoryItem.AddLogNote(message);
                }
            }
            catch (Exception ex)
            {
                var message = $"Error cleaning up old servers: {ex.Message}";
                ScheduleHistoryItem.AddLogNote(message);
                Exceptions.LogException(ex);
            }
        }

        private void LogServerEvent(ServerInfo server, string eventLogTypeKey)
        {
            var log = new LogInfo { LogTypeKey = eventLogTypeKey };
            log.AddProperty("Server", server.ServerName);
            log.AddProperty("IISAppName", server.IISAppName);
            log.AddProperty("UniqueId", server.UniqueId);
            log.AddProperty("Url", server.Url);
            log.AddProperty("PingFailureCount", server.PingFailureCount.ToString());
            log.AddProperty("Last Activity Date", server.LastActivityDate.ToString());
            LogController.Instance.AddLog(log);
        }



    }
}
