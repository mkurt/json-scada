﻿/* 
 * OPC-UA Client Protocol driver for {json:scada}
 * {json:scada} - Copyright (c) 2020 - Ricardo L. Olsen
 * This file is part of the JSON-SCADA distribution (https://github.com/riclolsen/json-scada).
 * 
 * This program is free software: you can redistribute it and/or modify  
 * it under the terms of the GNU General Public License as published by  
 * the Free Software Foundation, version 3.
 *
 * This program is distributed in the hope that it will be useful, but 
 * WITHOUT ANY WARRANTY; without even the implied warranty of 
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU 
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License 
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace OPCUAClientDriver
{
    partial class MainClass
    {
        public enum ExitCode : int
        {
            Ok = 0,
            ErrorCreateApplication = 0x11,
            ErrorDiscoverEndpoints = 0x12,
            ErrorCreateSession = 0x13,
            ErrorBrowseNamespace = 0x14,
            ErrorCreateSubscription = 0x15,
            ErrorMonitoredItem = 0x16,
            ErrorAddSubscription = 0x17,
            ErrorRunning = 0x18,
            ErrorNoKeepAlive = 0x30,
            ErrorInvalidCommandLine = 0x100
        };


        public class OPCUAClient
        {
            const int ReconnectPeriod = 10;
            Session session;
            SessionReconnectHandler reconnectHandler;
            int conn_number = 0;
            string conn_name;
            string endpointURL;
            string configFileName;
            int clientRunTime = Timeout.Infinite;
            static bool autoAccept = false;
            static ExitCode exitCode;

            public OPCUAClient(string _conn_name, int _conn_number, string _endpointURL, string _configFileName, bool _autoAccept, int _stopTimeout)
            {
                conn_name = _conn_name;
                conn_number = _conn_number;
                endpointURL = _endpointURL;
                configFileName = _configFileName;
                autoAccept = _autoAccept;
                clientRunTime = _stopTimeout <= 0 ? Timeout.Infinite : _stopTimeout * 1000;
            }

            public void Run()
            {
                try
                {
                    ConsoleClient().Wait();
                }
                catch (Exception ex)
                {
                    Utils.Trace("ServiceResultException:" + ex.Message);
                    Console.WriteLine("Exception: {0}", ex.Message);
                    return;
                }

                ManualResetEvent quitEvent = new ManualResetEvent(false);
                try
                {
                    Console.CancelKeyPress += (sender, eArgs) =>
                    {
                        quitEvent.Set();
                        eArgs.Cancel = true;
                    };
                }
                catch
                {
                }

                // wait for timeout or Ctrl-C
                quitEvent.WaitOne(clientRunTime);

                // return error conditions
                if (session.KeepAliveStopped)
                {
                    exitCode = ExitCode.ErrorNoKeepAlive;
                    return;
                }

                exitCode = ExitCode.Ok;
            }

            public static ExitCode ExitCode { get => exitCode; }

            private async Task ConsoleClient()
            {
                Console.WriteLine("1 - Create an Application Configuration.");
                exitCode = ExitCode.ErrorCreateApplication;

                ApplicationInstance application = new ApplicationInstance
                {
                    ApplicationName = "JSON-SCADA OPC UA Client",
                    ApplicationType = ApplicationType.Client,
                    ConfigSectionName = ""
                };

                // load the application configuration.
                ApplicationConfiguration config = await application.LoadApplicationConfiguration(configFileName, false);
                config.SecurityConfiguration.AutoAcceptUntrustedCertificates = true;

                // check the application certificate.
                bool haveAppCertificate = await application.CheckApplicationInstanceCertificate(false, 0);

                if (!haveAppCertificate)
                {
                    throw new Exception("Application instance certificate invalid!");
                }

                if (haveAppCertificate)
                {
                    config.ApplicationUri = X509Utils.GetApplicationUriFromCertificate(config.SecurityConfiguration.ApplicationCertificate.Certificate);
                    if (config.SecurityConfiguration.AutoAcceptUntrustedCertificates)
                    {
                        autoAccept = true;
                    }
                    config.CertificateValidator.CertificateValidation += new CertificateValidationEventHandler(CertificateValidator_CertificateValidation);
                }
                else
                {
                    Console.WriteLine("    WARN: missing application certificate, using unsecure connection.");
                }


                Console.WriteLine("2 - Discover endpoints of {0}.", endpointURL);
                exitCode = ExitCode.ErrorDiscoverEndpoints;
                var selectedEndpoint = CoreClientUtils.SelectEndpoint(endpointURL, haveAppCertificate, 15000);
                Console.WriteLine("    Selected endpoint uses: {0}",
                    selectedEndpoint.SecurityPolicyUri.Substring(selectedEndpoint.SecurityPolicyUri.LastIndexOf('#') + 1));

                Console.WriteLine("3 - Create a session with OPC UA server.");
                exitCode = ExitCode.ErrorCreateSession;
                var endpointConfiguration = EndpointConfiguration.Create(config);
                var endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);


                session = await Session.Create(config, endpoint, false, "OPC UA Console Client", 60000, new UserIdentity(new AnonymousIdentityToken()), null);

                // register keep alive handler
                session.KeepAlive += Client_KeepAlive;

                Console.WriteLine("4 - Browse the OPC UA server namespace.");
                exitCode = ExitCode.ErrorBrowseNamespace;
                ReferenceDescriptionCollection references;
                Byte[] continuationPoint;

                references = session.FetchReferences(ObjectIds.ObjectsFolder);

                session.Browse(
                    null,
                    null,
                    ObjectIds.ObjectsFolder,
                    0u,
                    BrowseDirection.Forward,
                    ReferenceTypeIds.HierarchicalReferences,
                    true,
                    (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
                    out continuationPoint,
                    out references);

                var list = new List<MonitoredItem>();
                Console.WriteLine("5 - Create a subscription with publishing interval of x second.");
                exitCode = ExitCode.ErrorCreateSubscription;
                var subscription = new Subscription(session.DefaultSubscription) { PublishingInterval = OPCDefaultPublishingInterval, PublishingEnabled = true };

                Console.WriteLine(" DisplayName, BrowseName, NodeClass");
                foreach (var rd in references)
                {
                    Console.WriteLine(" {0}, {1}, {2}", rd.DisplayName, rd.BrowseName, rd.NodeClass);
                    ReferenceDescriptionCollection nextRefs;
                    byte[] nextCp;
                    session.Browse(
                        null,
                        null,
                        ExpandedNodeId.ToNodeId(rd.NodeId, session.NamespaceUris),
                        0u,
                        BrowseDirection.Forward,
                        ReferenceTypeIds.HierarchicalReferences,
                        true,
                        (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
                        out nextCp,
                        out nextRefs);

                    foreach (var nextRd in nextRefs)
                    {
                        Log(conn_name + " - " + conn_number);
                        Console.WriteLine("   + {0}, {1}, {2}", nextRd.DisplayName, nextRd.BrowseName, nextRd.NodeClass);
                        Console.WriteLine(nextRd);
                        if (nextRd.NodeClass == NodeClass.Variable)
                            list.Add(
                            new MonitoredItem(subscription.DefaultItem)
                            {
                                DisplayName = nextRd.DisplayName.ToString(),
                                StartNodeId = nextRd.NodeId.ToString(),
                                SamplingInterval = OPCDefaultSamplingInterval,
                                QueueSize = OPCDefaultQueueSize,
                                MonitoringMode = MonitoringMode.Reporting,
                                DiscardOldest = true,
                                AttributeId = Attributes.Value
                            });

                        ReferenceDescriptionCollection nextRefs_;
                        byte[] nextCp_;
                        session.Browse(
                            null,
                            null,
                            ExpandedNodeId.ToNodeId(nextRd.NodeId, session.NamespaceUris),
                            0u,
                            BrowseDirection.Forward,
                            ReferenceTypeIds.HierarchicalReferences,
                            true,
                            (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
                            out nextCp_,
                            out nextRefs_);                        

                        foreach (var nextRd_ in nextRefs_)
                        {
                            Console.WriteLine("   + {0}, {1}, {2}", nextRd_.DisplayName, nextRd_.BrowseName, nextRd_.NodeClass);
                            Console.WriteLine(nextRd_.NodeClass);

                            if (nextRd_.NodeClass==NodeClass.Variable)
                            list.Add(
                                new MonitoredItem(subscription.DefaultItem)
                                {
                                    DisplayName = nextRd_.DisplayName.ToString(),
                                    StartNodeId = nextRd_.NodeId.ToString(),
                                    SamplingInterval = OPCDefaultSamplingInterval,
                                    QueueSize = OPCDefaultQueueSize,
                                    MonitoringMode = MonitoringMode.Reporting,
                                    DiscardOldest = true,
                                    AttributeId = Attributes.Value
                                }); ;

                        }
                    }
                }

                Console.WriteLine("6 - Add a list of items (server current time and status) to the subscription.");
                exitCode = ExitCode.ErrorMonitoredItem;
                /*
                list.Add(
                    new MonitoredItem(subscription.DefaultItem)
                    {
                        DisplayName = "ServerStatusCurrentTime", StartNodeId = "i=" + Variables.Server_ServerStatus_CurrentTime.ToString()
                    });
                list.Add(
                    new MonitoredItem(subscription.DefaultItem)
                    {
                        DisplayName = "Demo.Dynamic.Scalar.StatusCode", StartNodeId = "ns=2;s=Demo.Dynamic.Scalar.StatusCode"
                    });
                list.Add(
                    new MonitoredItem(subscription.DefaultItem)
                    {
                        DisplayName = "Demo.Dynamic.Scalar.String", StartNodeId = "ns=2;s=Demo.Dynamic.Scalar.String"
                    });
                */
                list.ForEach(i => i.Notification += OnNotification);
                list.ForEach(i => Console.WriteLine(i.DisplayName));
                subscription.AddItems(list);

                Console.WriteLine("7 - Add the subscription to the session.");
                exitCode = ExitCode.ErrorAddSubscription;
                session.AddSubscription(subscription);
                subscription.Create();

                Console.WriteLine("8 - Running...Press Ctrl-C to exit...");
                exitCode = ExitCode.ErrorRunning;
            }

            private void Client_KeepAlive(Session sender, KeepAliveEventArgs e)
            {
                if (e.Status != null && ServiceResult.IsNotGood(e.Status))
                {
                    Console.WriteLine("{0} {1}/{2}", e.Status, sender.OutstandingRequestCount, sender.DefunctRequestCount);

                    if (reconnectHandler == null)
                    {
                        Console.WriteLine("--- RECONNECTING ---");
                        reconnectHandler = new SessionReconnectHandler();
                        reconnectHandler.BeginReconnect(sender, ReconnectPeriod * 1000, Client_ReconnectComplete);
                    }
                }
            }

            private void Client_ReconnectComplete(object sender, EventArgs e)
            {
                // ignore callbacks from discarded objects.
                if (!Object.ReferenceEquals(sender, reconnectHandler))
                {
                    return;
                }

                session = reconnectHandler.Session;
                reconnectHandler.Dispose();
                reconnectHandler = null;

                Console.WriteLine("--- RECONNECTED ---");
            }

            private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
            {

                //MonitoredItemNotification notification = e.NotificationValue as MonitoredItemNotification;
                //Console.WriteLine("Notification Received for Variable \"{0}\" and Value = {1} type {2}.", item.DisplayName, notification.Value, notification.TypeId);


                foreach (var value in item.DequeueValues())
                {
                    if (value != null)
                    {
                        string tp = "unknown";

                        try
                        {

                            if (value.WrappedValue.TypeInfo != null)
                            {
                                tp = value.WrappedValue.TypeInfo.BuiltInType.ToString();
                                // Log("TYPE: " + tp);
                            }
                            else
                            {
                                Log("TYPE: ?????");
                            }

                            Log(conn_name + " - " + item.ResolvedNodeId + " " + item.DisplayName + " " + value.Value + " " + value.SourceTimestamp + " " + value.StatusCode, LogLevelDetailed);
                            // Console.WriteLine("{0}: {1}, {2}, {3}, {4}", item.ResolvedNodeId, item.DisplayName, value.Value, value.SourceTimestamp, value.StatusCode);

                            if (value.Value != null)
                            {
                                Double dblValue = 0.0;
                                string strValue = "";
                                try
                                {
                                if (tp == "DateTime") 
                                    {
                                    
                                    dblValue = ((DateTimeOffset)System.Convert.ToDateTime(value.Value)).ToUnixTimeMilliseconds();
                                    strValue = System.Convert.ToDateTime(value.Value).ToString("o");
                                    }
                                else
                                    {
                                    dblValue = System.Convert.ToDouble(value.Value);
                                    strValue = value.Value.ToString();
                                    }
                                }
                                catch (Exception excpt)
                                {
                                strValue = value.Value.ToString();
                                }

                                OPC_Value iv =
                                    new OPC_Value()
                                    {
                                        valueJson = JsonSerializer.Serialize(value),
                                        selfPublish = true,
                                        address = item.ResolvedNodeId.ToString(),
                                        asdu = tp,
                                        isDigital = true,
                                        value = dblValue,
                                        valueString = strValue,
                                        hasSourceTimestamp = value.SourceTimestamp!=null,
                                        sourceTimestamp = value.SourceTimestamp,
                                        serverTimestamp = DateTime.Now,
                                        quality = StatusCode.IsGood(value.StatusCode),
                                        cot = 3,
                                        conn_number = conn_number,
                                        conn_name = conn_name,
                                        common_address = "",
                                        display_name = item.DisplayName
                                    };
                                OPCDataQueue.Enqueue(iv);
                            }

                        }
                        catch (Exception excpt)
                        {

                            Log("TYPE:" + tp);
                            Log(conn_name + " - " + item.ResolvedNodeId + " " + item.DisplayName + " " + value.Value + " " + value.SourceTimestamp + " " + value.StatusCode);

                        }

                    }
                    else
                    {
                        Log(conn_name + " - " + item.ResolvedNodeId + " " + item.DisplayName + " NULL VALUE!", LogLevelDetailed);
                    }

                }
            }

            private void CertificateValidator_CertificateValidation(CertificateValidator validator, CertificateValidationEventArgs e)
            {
                if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
                {
                    e.Accept = autoAccept;
                    if (autoAccept)
                    {
                        Console.WriteLine("Accepted Certificate: {0}", e.Certificate.Subject);
                    }
                    else
                    {
                        Console.WriteLine("Rejected Certificate: {0}", e.Certificate.Subject);
                    }
                }
            }
        }
    }
}