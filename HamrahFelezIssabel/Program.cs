﻿using AsterNET.Manager;
using AsterNET.Manager.Event;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Topshelf;

namespace IssabelCallMonitor
{
    class Program
    {
        static void Main(string[] args)
        {
            HostFactory.Run(x =>
            {
                x.Service<CallMonitorService>(s =>
                {
                    s.ConstructUsing(name => new CallMonitorService());
                    s.WhenStarted(tc => tc.Start());
                    s.WhenStopped(tc => tc.Stop());
                });

                x.RunAsLocalSystem();
                x.SetDescription("Issabel Call Monitor Service for HamrahFelez Asterisk AMI");
                x.SetDisplayName("HamrahFelezIssabelCallMonitor");
                x.SetServiceName("HamrahFelezIssabelCallMonitor");

                x.EnableServiceRecovery(rc =>
                {
                    rc.RestartService(1); // Restart after 1 minute on first failure
                    rc.RestartService(1); // Restart after 1 minute on second failure
                    rc.RestartService(1); // Restart after 1 minute on subsequent failures
                    rc.SetResetPeriod(1); // Reset failure count after 1 day
                });

                x.StartAutomatically();
            });
        }
    }

    class CallMonitorService
    {
        private ManagerConnection manager;
        private readonly HttpClient httpClient = new HttpClient();
        private readonly string apiEndpoint = "http://192.168.50.10:8080/api/admin/Communication/InsertCommunication";
        private readonly string logPrefix = "[IssabelMonitoringApp] ";
        private readonly string logFilePath = @".\log.txt";
        private readonly string fallbackLogFilePath = @".\log-fallback.txt";
        private readonly long maxLogFileSize = 10 * 1024 * 1024; // 10 MB

        // Track active calls by UniqueId
        private readonly Dictionary<string, CallInfo> activeCalls = new Dictionary<string, CallInfo>();

        // Track channels to their associated call UniqueId and timestamp
        private readonly Dictionary<string, ChannelInfo> channelMap = new Dictionary<string, ChannelInfo>();

        // Track bridge events to detect answered calls
        private readonly Dictionary<string, string> channelBridges = new Dictionary<string, string>();

        // Ring group configurations
        private readonly Dictionary<string, RingGroup> ringGroups = new Dictionary<string, RingGroup>
        {
            { "607", new RingGroup { Extensions = new List<string> { "115", "116", "122", "141" }, Strategy = "hunt-prim" } },
            { "608", new RingGroup { Extensions = new List<string> { "222", "223" }, Strategy = "hunt-prim" } },
            { "603", new RingGroup { Extensions = new List<string> { "127", "121", "126" }, Strategy = "hunt-prim" } },
            { "604", new RingGroup { Extensions = new List<string> { "111", "119" }, Strategy = "hunt-prim" } }
        };

        private CancellationTokenSource cancellationTokenSource;

        public void Start()
        {
            cancellationTokenSource = new CancellationTokenSource();
            Task.Run(() => RunAsync(cancellationTokenSource.Token), cancellationTokenSource.Token);
        }

        public void Stop()
        {
            cancellationTokenSource?.Cancel();
            if (manager?.IsConnected() == true)
            {
                manager.Logoff();
                Log("Disconnected from Asterisk AMI.");
            }
            httpClient.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                Log("Starting application...");
                // Initialize AMI connection
                manager = new ManagerConnection("192.168.50.7", 5038, "windows-app", "42+EC!xd67")
                {
                    KeepAlive = true,
                    DefaultResponseTimeout = 5000,
                    DefaultEventTimeout = 5000
                };

                // Subscribe to AMI events
                manager.NewChannel += Manager_NewChannel;
                manager.DialBegin += Manager_DialBegin;
                manager.DialEnd += Manager_DialEnd;
                manager.NewCallerId += Manager_NewCallerId;
                manager.BridgeCreate += Manager_BridgeCreate;
                manager.BridgeEnter += Manager_BridgeEnter;
                manager.Hangup += Manager_Hangup;
                manager.UnhandledEvent += Manager_UnhandledEvent;

                // Enable all events
                manager.RegisterUserEventClass(typeof(AsteriskEvent));
                manager.UserEvents += (s, e) => { };

                // Connect to AMI
                Log("Connecting to Asterisk AMI at 192.168.50.7:5038...");
                manager.Login();
                Log("Connected to Asterisk AMI. Monitoring calls...");
                Log("Listening for incoming calls on trunks: siptci, 2192000119");

                // Start cleanup task
                Task.Run(() => CleanupOldCalls(cancellationToken), cancellationToken);

                // Keep the service running
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Log($"Error in RunAsync: {ex.Message}");
            }
        }

        private async Task CleanupOldCalls(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (activeCalls)
                {
                    lock (channelMap)
                    {
                        var oldCalls = activeCalls.Where(c => (DateTime.Now - channelMap[c.Value.UniqueId].Timestamp).TotalMinutes > 60).ToList();
                        foreach (var call in oldCalls)
                        {
                            activeCalls.Remove(call.Key);
                            Log($"Removed stale call {call.Key} from tracking.");
                        }
                        var oldChannels = channelMap.Where(cm => cm.Value.CallUniqueId == null || !activeCalls.ContainsKey(cm.Value.CallUniqueId)).ToList();
                        foreach (var channel in oldChannels)
                        {
                            channelMap.Remove(channel.Key);
                            Log($"Removed stale channel {channel.Key} from tracking.");
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            }
        }

        private void Manager_UnhandledEvent(object sender, ManagerEvent e)
        {
            Log($"Unhandled event: {e.GetType().Name}, Attributes: {string.Join(", ", e.Attributes.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }

        private void Manager_NewChannel(object sender, NewChannelEvent e)
        {
            var channel = e.Channel;
            var callerId = e.CallerIdNum;
            var uniqueId = e.UniqueId;
            var trunk = channel.StartsWith("SIP/siptci") ? "siptci" : channel.StartsWith("SIP/2192000119") ? "2192000119" : "unknown";

            // تماس‌های ورودی یا خروجی از ترانک
            if (channel.StartsWith("SIP/siptci") || channel.StartsWith("SIP/2192000119"))
            {
                string callType = "Inbound";
                Log($"New {callType} call from {callerId ?? "unknown"} on trunk {trunk}, uniqueid {uniqueId}, channel {channel}");

                var callInfo = new CallInfo
                {
                    UniqueId = uniqueId,
                    Caller = callerId ?? "unknown",
                    Target = null,
                    IsRingGroup = false,
                    RingGroupNumber = null,
                    RingGroupExtensions = new List<string>(),
                    Strategy = null,
                    AnsweredExtensions = new List<string>(),
                    MissedExtensions = new List<string>(),
                    CallType = callType,
                    Duration = 0
                };

                lock (activeCalls)
                {
                    activeCalls[uniqueId] = callInfo;
                }

                lock (channelMap)
                {
                    channelMap[channel] = new ChannelInfo { CallUniqueId = uniqueId, Timestamp = DateTime.Now };
                }
            }
            else
            {
                var ext = ExtractExtensionFromChannel(channel);
                Log($"Processing non-trunk channel for call {uniqueId}, channel {channel}, caller {callerId ?? "unknown"}, ext {ext ?? "null"}");

                lock (activeCalls)
                {
                    lock (channelMap)
                    {
                        var ringGroup = ringGroups.FirstOrDefault(rg => rg.Value.Extensions.Contains(ext));
                        string ringGroupNumber = ringGroup.Key;

                        // تطبیق دقیق با LastDialedExtension
                        var callInfo = activeCalls.Values
                            .Where(c => c.CallType == "Inbound" &&
                                        channelMap.Any(cm => cm.Value.CallUniqueId == c.UniqueId && (DateTime.Now - cm.Value.Timestamp).TotalSeconds < 30) &&
                                        (c.LastDialedExtension == ext || c.LastDialedExtension == null))
                            .OrderByDescending(c =>
                            {
                                var channelInfo = channelMap.FirstOrDefault(cm => cm.Value.CallUniqueId == c.UniqueId);
                                return channelInfo.Key != null ? channelInfo.Value.Timestamp : DateTime.MinValue;
                            })
                            .FirstOrDefault();

                        if (callInfo != null && ext != null)
                        {
                            if (callInfo.CallType == "Inbound")
                            {
                                if (ringGroupNumber != null)
                                {
                                    callInfo.Target = ringGroupNumber;
                                    callInfo.IsRingGroup = true;
                                    callInfo.RingGroupNumber = ringGroupNumber;
                                    callInfo.RingGroupExtensions = ringGroups[ringGroupNumber].Extensions.ToList();
                                    callInfo.Strategy = ringGroups[ringGroupNumber].Strategy;
                                    Log($"Updated call {callInfo.UniqueId} target to {ringGroupNumber} (ring group), IsRingGroup: True, CallType: {callInfo.CallType}");
                                }
                                else if (callInfo.LastDialedExtension == ext || callInfo.Target == null)
                                {
                                    callInfo.Target = ext;
                                    callInfo.IsRingGroup = false;
                                    callInfo.RingGroupNumber = null;
                                    callInfo.RingGroupExtensions = new List<string>();
                                    callInfo.Strategy = null;
                                    Log($"Updated call {callInfo.UniqueId} target to {ext} (direct), IsRingGroup: False, CallType: {callInfo.CallType}");
                                }
                            }

                            channelMap[channel] = new ChannelInfo { CallUniqueId = callInfo.UniqueId, Timestamp = DateTime.Now };
                        }
                        else
                        {
                            channelMap[channel] = new ChannelInfo { CallUniqueId = null, Timestamp = DateTime.Now };
                            Log($"Non-trunk channel {channel} (ext {ext ?? "null"}) queued for potential outbound call, uniqueid {uniqueId}");
                        }
                    }
                }
            }
        }

        private void Manager_DialBegin(object sender, DialBeginEvent e)
        {
            lock (activeCalls)
            {
                var destChannel = e.Destination ?? "unknown";
                var destExt = ExtractExtensionFromChannel(destChannel);
                var callerChannel = e.Channel;
                var callerExt = ExtractExtensionFromChannel(callerChannel);
                var dialedNumber = e.DialString ?? "unknown";

                Log($"Dial begin for call {e.UniqueId}, caller channel {callerChannel}, caller ext {callerExt ?? "null"}, destination channel {destChannel}, dialed number {dialedNumber}");

                if (dialedNumber.StartsWith("2192000119/") || dialedNumber.Contains("/2192000119/"))
                {
                    string targetNumber = dialedNumber.Split('/').LastOrDefault() ?? "unknown";
                    if (string.IsNullOrEmpty(targetNumber) || targetNumber == "unknown")
                    {
                        Log($"Warning: Could not extract target number from DialString: {dialedNumber}");
                        targetNumber = e.CallerIdNum ?? "unknown";
                    }

                    var callInfo = new CallInfo
                    {
                        UniqueId = e.UniqueId,
                        Caller = callerExt ?? e.CallerIdNum ?? "unknown",
                        Target = targetNumber,
                        IsRingGroup = false,
                        RingGroupNumber = null,
                        RingGroupExtensions = new List<string>(),
                        Strategy = null,
                        AnsweredExtensions = new List<string>(),
                        MissedExtensions = new List<string>(),
                        CallType = "Outbound",
                        Duration = 0
                    };

                    activeCalls[e.UniqueId] = callInfo;
                    Log($"New Outbound call from {callInfo.Caller} to {callInfo.Target} on trunk 2192000119, uniqueid {e.UniqueId}");

                    lock (channelMap)
                    {
                        if (!channelMap.ContainsKey(callerChannel))
                        {
                            channelMap[callerChannel] = new ChannelInfo { CallUniqueId = e.UniqueId, Timestamp = DateTime.Now };
                        }
                        var trunkChannel = $"SIP/2192000119-{callerChannel.Split('-').Last()}";
                        if (!channelMap.ContainsKey(trunkChannel))
                        {
                            channelMap[trunkChannel] = new ChannelInfo { CallUniqueId = e.UniqueId, Timestamp = DateTime.Now };
                        }
                    }
                }
                else if (activeCalls.ContainsKey(e.UniqueId))
                {
                    var callInfo = activeCalls[e.UniqueId];

                    // اگر destChannel نامعتبر است، از DialString استفاده می‌کنیم
                    if (destExt == null || destChannel == "unknown")
                    {
                        destExt = dialedNumber; // مثلاً "203"
                        Log($"Extracted destExt={destExt} from DialString for call {e.UniqueId}");
                    }

                    if (string.IsNullOrWhiteSpace(destExt) || destExt == "unknown")
                    {
                        Log($"Skipping DialBegin for call {e.UniqueId}: Invalid destination extension");
                        return;
                    }

                    if (callInfo.Target == null)
                    {
                        var ringGroup = ringGroups.FirstOrDefault(rg => rg.Value.Extensions.Contains(destExt));
                        string ringGroupNumber = ringGroup.Key;

                        if (ringGroupNumber != null)
                        {
                            callInfo.Target = ringGroupNumber;
                            callInfo.IsRingGroup = true;
                            callInfo.RingGroupNumber = ringGroupNumber;
                            callInfo.RingGroupExtensions = ringGroups[ringGroupNumber].Extensions.ToList();
                            callInfo.Strategy = ringGroups[ringGroupNumber].Strategy;
                            Log($"Updated call {e.UniqueId} target to {ringGroupNumber} (ring group), IsRingGroup: True, CallType: {callInfo.CallType}");
                        }
                        else
                        {
                            callInfo.Target = destExt;
                            callInfo.IsRingGroup = false;
                            callInfo.RingGroupNumber = null;
                            callInfo.RingGroupExtensions = new List<string>();
                            callInfo.Strategy = null;
                            Log($"Updated call {e.UniqueId} target to {destExt} (direct), IsRingGroup: False, CallType: {callInfo.CallType}");
                        }
                    }

                    lock (channelMap)
                    {
                        if (!channelMap.ContainsKey(destChannel) && destChannel != "unknown")
                        {
                            channelMap[destChannel] = new ChannelInfo { CallUniqueId = e.UniqueId, Timestamp = DateTime.Now };
                        }
                    }

                    if (callInfo.IsRingGroup && callInfo.RingGroupExtensions.Contains(destExt))
                    {
                        callInfo.LastDialedExtension = destExt; // ذخیره داخلی زنگ‌خورده
                        Log($"Stored LastDialedExtension: {destExt} for call {e.UniqueId}");
                        if (callInfo.Strategy == "hunt-prim")
                        {
                            var dialedIndex = callInfo.RingGroupExtensions.IndexOf(destExt);
                            if (dialedIndex > 0)
                            {
                                var missedExt = callInfo.RingGroupExtensions[dialedIndex - 1];
                                if (!callInfo.AnsweredExtensions.Contains(missedExt) && !callInfo.MissedExtensions.Contains(missedExt))
                                {
                                    callInfo.MissedExtensions.Add(missedExt);
                                    Log($"Marked extension {missedExt} as missed for call {e.UniqueId}, but not sending API request");
                                }
                            }
                        }
                    }
                    else if (!callInfo.IsRingGroup)
                    {
                        callInfo.LastDialedExtension = destExt; // برای تماس‌های مستقیم هم ذخیره می‌کنیم
                        Log($"Stored LastDialedExtension: {destExt} for call {e.UniqueId} (direct)");
                    }
                }
                else
                {
                    Log($"DialBegin ignored for call {e.UniqueId}: Not a trunk call (likely internal)");
                }
            }
        }

        private async void Manager_DialEnd(object sender, DialEndEvent e)
        {
            CallInfo callInfo = null;
            string dialStatus = e.DialStatus;
            string callerIdNum = e.CallerIdNum;
            string destination = e.Destination;

            lock (activeCalls)
            {
                if (activeCalls.ContainsKey(e.UniqueId) && dialStatus != null)
                {
                    callInfo = activeCalls[e.UniqueId];
                    if (callInfo.Caller == "unknown" && !string.IsNullOrWhiteSpace(callerIdNum))
                    {
                        callInfo.Caller = callerIdNum;
                        Log($"Updated CallerIdNum for call {e.UniqueId} from 'unknown' to {callerIdNum}");
                    }
                    if (callInfo.Caller == "unknown")
                    {
                        Log($"Warning: CallerIdNum is unknown for call {e.UniqueId}, Channel: {e.Channel}, Trunk: {(e.Channel.StartsWith("SIP/siptci") ? "siptci" : "unknown")}");
                    }
                }
            }

            if (callInfo != null)
            {
                Log($"DialEnd for call {e.UniqueId}, DialStatus: {dialStatus}, Target: {callInfo.Target}, CallerIdNum: {(callerIdNum ?? "null")}");

                if (callInfo.CallType == "Outbound")
                {
                    if (dialStatus == "ANSWER")
                    {
                        callInfo.AnsweredExtensions.Add(callInfo.Target);
                        callInfo.IsProcessed = true; // تنظیم پرچم
                        await SendApiRequest(callInfo, true, callInfo.Target, callInfo.Target);
                        Log($"Outbound call {e.UniqueId} answered, Target: {callInfo.Target}");
                    }
                    else
                    {
                        callInfo.MissedExtensions.Add(callInfo.Target);
                        callInfo.IsProcessed = true; // تنظیم پرچم
                        await SendApiRequest(callInfo, false, callInfo.Target, "");
                        Log($"Outbound call {e.UniqueId} not answered, DialStatus: {dialStatus}, Target: {callInfo.Target}");
                    }
                }
                else if (callInfo.CallType == "Inbound")
                {
                    var ext = callInfo.LastDialedExtension ?? ExtractExtensionFromChannel(destination);
                    if (ext == null)
                    {
                        // اگر ext همچنان نال است، از Target استفاده می‌کنیم
                        ext = callInfo.Target;
                        Log($"Warning: Could not extract extension from destination {destination} for call {e.UniqueId}, using Target: {ext}");
                    }

                    if (!callInfo.IsRingGroup)
                    {
                        if (dialStatus == "ANSWER")
                        {
                            if (!callInfo.AnsweredExtensions.Contains(ext) && ext != null)
                            {
                                callInfo.AnsweredExtensions.Add(ext);
                                callInfo.IsProcessed = true; // تنظیم پرچم
                                await SendApiRequest(callInfo, true, ext, ext);
                                Log($"Inbound call {e.UniqueId} answered, Target: {callInfo.Target}, AnsweredBy: {ext}");
                            }
                        }
                        else
                        {
                            if (!callInfo.MissedExtensions.Contains(ext) && ext != null)
                            {
                                callInfo.MissedExtensions.Add(ext);
                                callInfo.IsProcessed = true; // تنظیم پرچم
                                await SendApiRequest(callInfo, false, ext, "");
                                Log($"Inbound call {e.UniqueId} not answered, DialStatus: {dialStatus}, Target: {callInfo.Target}");
                            }
                        }
                    }
                    else if (ext != null && callInfo.RingGroupExtensions.Contains(ext))
                    {
                        if (dialStatus == "ANSWER")
                        {
                            if (!callInfo.AnsweredExtensions.Contains(ext))
                            {
                                callInfo.AnsweredExtensions.Add(ext);
                                callInfo.IsProcessed = true; // تنظیم پرچم
                                await SendApiRequest(callInfo, true, callInfo.RingGroupNumber, ext);
                                Log($"Inbound call {e.UniqueId} answered, RingGroup: {callInfo.RingGroupNumber}, AnsweredBy: {ext}");
                            }
                        }
                        else
                        {
                            if (!callInfo.MissedExtensions.Contains(ext))
                            {
                                callInfo.MissedExtensions.Add(ext);
                                Log($"Marked extension {ext} as missed for call {e.UniqueId}, but not sending API request");
                            }
                        }
                    }
                    else
                    {
                        Log($"Skipping DialEnd for call {e.UniqueId}: Invalid extension {ext} for ring group {callInfo.RingGroupNumber}");
                    }
                }
            }
        }
        private void Manager_NewCallerId(object sender, NewCallerIdEvent e)
        {
            lock (activeCalls)
            {
                if (activeCalls.ContainsKey(e.UniqueId))
                {
                    var callInfo = activeCalls[e.UniqueId];
                    if (callInfo.Caller == "unknown" && !string.IsNullOrWhiteSpace(e.CallerIdNum))
                    {
                        callInfo.Caller = e.CallerIdNum;
                        Log($"Updated CallerIdNum for call {e.UniqueId} from 'unknown' to {e.CallerIdNum} via NewCallerIdEvent");
                    }
                }
            }
        }

        private void Manager_BridgeCreate(object sender, BridgeCreateEvent e)
        {
            lock (channelBridges)
            {
                channelBridges[e.BridgeUniqueId] = e.BridgeType;
                Log($"Bridge created: {e.BridgeUniqueId}, type: {e.BridgeType}");
            }
        }

        private async void Manager_BridgeEnter(object sender, BridgeEnterEvent e)
        {
            lock (activeCalls)
            {
                lock (channelBridges)
                {
                    lock (channelMap)
                    {
                        if (channelMap.TryGetValue(e.Channel, out var channelInfo) && channelInfo.CallUniqueId != null && activeCalls.ContainsKey(channelInfo.CallUniqueId))
                        {
                            var callInfo = activeCalls[channelInfo.CallUniqueId];
                            var channel = e.Channel;
                            var ext = ExtractExtensionFromChannel(channel);

                            Log($"Channel {channel} (ext {ext ?? "null"}) entered bridge {e.BridgeUniqueId} for call {callInfo.UniqueId}, CallType: {callInfo.CallType}");

                            if (ext == null && !channel.StartsWith("SIP/2192000119"))
                            {
                                Log($"Skipping BridgeEnter for call {callInfo.UniqueId}: Invalid extension from channel {channel}");
                                return;
                            }

                            if (callInfo.CallType == "Outbound")
                            {
                                Log($"Outbound call {callInfo.UniqueId} bridge entered by channel {channel}, status handled in DialEnd");
                            }
                        }
                        else
                        {
                            Log($"BridgeEnter ignored: No call found for channel {e.Channel}, uniqueid {e.UniqueId}");
                        }
                    }
                }
            }
        }

        private async void Manager_Hangup(object sender, HangupEvent e)
        {
            CallInfo callInfo = null;
            List<string> answeredExtensions = null;
            bool isInbound = false;
            string target = null;
            string ringGroupNumber = null;

            lock (activeCalls)
            {
                lock (channelMap)
                {
                    if (channelMap.Values.Any(c => c.CallUniqueId == e.UniqueId) && activeCalls.ContainsKey(e.UniqueId))
                    {
                        callInfo = activeCalls[e.UniqueId];
                        target = callInfo.Target;
                        answeredExtensions = callInfo.AnsweredExtensions.ToList();
                        isInbound = callInfo.CallType == "Inbound";
                        ringGroupNumber = callInfo.RingGroupNumber;

                        activeCalls.Remove(e.UniqueId);
                        Log($"Call {e.UniqueId} removed from tracking.");
                    }
                }
            }

            if (callInfo != null)
            {
                Log($"Call {e.UniqueId} hung up for target {target ?? "null"}, cause: {e.CauseTxt}");

                if (target == null)
                {
                    Log($"Skipping Hangup for call {e.UniqueId}: No target extension set");
                    return;
                }

                // فقط اگه تماس قبلاً پردازش نشده باشه، درخواست API ارسال می‌کنیم
                if (!callInfo.IsProcessed)
                {
                    if (answeredExtensions.Any())
                    {
                        var effectiveAnsweredExt = callInfo.CallType == "Outbound" ? callInfo.Caller : callInfo.LastDialedExtension ?? answeredExtensions.FirstOrDefault();
                        if (!string.IsNullOrEmpty(effectiveAnsweredExt))
                        {
                            await SendApiRequest(
                                callInfo,
                                true,
                                callInfo.IsRingGroup && isInbound ? ringGroupNumber : target,
                                effectiveAnsweredExt
                            );
                        }
                    }
                    else if (callInfo.IsRingGroup && isInbound)
                    {
                        // اگر هیچ داخلی پاسخ نداده، فقط برای شماره رینگ‌گروپ ثبت می‌کنیم
                        await SendApiRequest(
                            callInfo,
                            false,
                            ringGroupNumber,
                            ""
                        );
                        Log($"Missed call recorded for ring group {ringGroupNumber}, call {callInfo.UniqueId}");
                    }
                    else if (!callInfo.IsRingGroup)
                    {
                        // برای تماس‌های غیر رینگ‌گروپ
                        await SendApiRequest(
                            callInfo,
                            false,
                            target,
                            ""
                        );
                        Log($"Missed call recorded for target {target}, call {callInfo.UniqueId}");
                    }
                }
                else
                {
                    Log($"Skipping SendApiRequest in Hangup for call {e.UniqueId}: Already processed in DialEnd");
                }
            }
        }
        private async Task SendApiRequest(CallInfo call, bool answered, string calledTarget, string answeredBy)
        {
            try
            {
                var endpoint = "http://192.168.50.10:8080/api/admin/Communication/InsertCommunication";

                // اطمینان از مقداردهی درست CalledTarget و AnsweredBy
                string payloadCalledTarget = call.CallType == "Outbound" ? call.Caller : (calledTarget ?? call.Target ?? "");
                string payloadAnsweredBy = call.CallType == "Outbound" ? call.Caller : (answeredBy ?? "");
                string phoneNumber = call.CallType == "Outbound" ? call.Target : call.Caller;

                // اگر CalledTarget همچنان خالی است، خطا لاگ کنیم
                if (string.IsNullOrEmpty(payloadCalledTarget))
                {
                    Log($"Warning: CalledTarget is empty for call {call.UniqueId}, using default empty string");
                }

                Log($"[API] Entering SendApiRequest for call {call.UniqueId}, CalledTarget: {payloadCalledTarget}, AnsweredBy: {payloadAnsweredBy}, answered: {answered}");

                string normalizedPhoneNumber = NormalizePhoneNumber(phoneNumber);

                var payload = new
                {
                    mod = 1,
                    pkCommunication = -1,
                    ParentID = -1,
                    CallId = call.UniqueId,
                    fkOptionDirection = call.CallType == "Outbound" ? 1502 : 1501,
                    fkOptionCommunicationType = 1651,
                    fkOptionCallReason = -1,
                    fkOptionOwnCallReason = -1,
                    fkOptionStatus = -1,
                    fkOptionCallStatus = answered ? 1511 : 1512,
                    PhoneNumber = phoneNumber,
                    NormalizedPhoneNumber = normalizedPhoneNumber,
                    CalledTarget = payloadCalledTarget,
                    AnsweredBy = payloadAnsweredBy,
                    CustomerRating = 0,
                    Duration = 0, // همیشه 0 ارسال می‌شود
                    DescriptionEmployee = "",
                    DescriptionCustomer = "",
                    fkAshkhasCompany = -1,
                    fkAshkhas = -1,
                    fkTozVcCode = -1,
                    fkTozType = -1,
                    CreatorUser = 1,
                    InsertUser = 1,
                    InsertDate = DateTime.Now,
                    InsertIp = "192.168.50.7",
                    items = new[]
                    {
                        new
                        {
                            pkCommunicationProduct = 0,
                            productName = "",
                            fkProduct = 0,
                            productQuantity = 0,
                            productPrice = 0,
                            description = ""
                        }
                    }
                };

                var jsonPayload = JsonConvert.SerializeObject(payload);
                Log($"Sending API request to {endpoint} for call {call.UniqueId}, CalledTarget: {payloadCalledTarget}, AnsweredBy: {payloadAnsweredBy}, Status: {(answered ? "Answered" : "Missed")}, fkOptionDirection: {payload.fkOptionDirection}");
                Log($"API payload: {jsonPayload}");

                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(endpoint, content);

                if (response.IsSuccessStatusCode)
                {
                    Log($"API request succeeded for call {call.UniqueId}, CalledTarget: {payloadCalledTarget}, AnsweredBy: {payloadAnsweredBy}");
                }
                else
                {
                    Log($"API request failed for call {call.UniqueId}: Status {response.StatusCode}, Reason: {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                Log($"Error sending API request for call {call.UniqueId}: {ex.Message}");
            }
        }

        private string ExtractExtensionFromChannel(string channel)
        {
            if (string.IsNullOrWhiteSpace(channel) || channel == "unknown")
                return null;
            var parts = channel.Split('/');
            if (parts.Length > 1)
            {
                var extPart = parts[1].Split('-')[0];
                return extPart;
            }
            return null;
        }

        private void Log(string message)
        {
            string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {logPrefix}{message}\n";
            try
            {
                // Check if log file exceeds max size
                if (File.Exists(logFilePath))
                {
                    FileInfo fileInfo = new FileInfo(logFilePath);
                    if (fileInfo.Length > maxLogFileSize)
                    {
                        string archivePath = Path.Combine(
                            Path.GetDirectoryName(logFilePath),
                            $"log-{DateTime.Now:yyyyMMddHHmmss}.txt"
                        );
                        File.Move(logFilePath, archivePath);
                    }
                }

                // Write to primary log file
                File.AppendAllText(logFilePath, logEntry);
            }
            catch (Exception ex)
            {
                // Fallback to alternate log file
                try
                {
                    File.AppendAllText(fallbackLogFilePath, $"{logEntry}[Primary log failed: {ex.Message}]\n");
                }
                catch { /* Ignore fallback errors */ }
            }
        }
        private static string NormalizePhoneNumber(string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
                return "";

            string normalized = phoneNumber.Trim().Replace("+", "");
            bool isAllDigits = normalized.All(char.IsDigit);

            if (normalized.StartsWith("98"))
            {
                normalized = "0" + normalized.Substring(2);
            }
            else if (normalized.Length == 8 && isAllDigits)
            {
                normalized = "021" + normalized;
            }
            else if (normalized.Length == 10 && normalized.StartsWith("9") && isAllDigits)
            {
                normalized = "0" + normalized;
            }
            else if (normalized.Length == 10 && normalized.StartsWith("3") && isAllDigits)
            {
                normalized = "0" + normalized;
            }

            return normalized.Any(char.IsDigit) ? normalized : "";
        }
    }

    class CallInfo
    {
        public string UniqueId { get; set; }
        public string Caller { get; set; }
        public string Target { get; set; }
        public bool IsRingGroup { get; set; }
        public string RingGroupNumber { get; set; }
        public List<string> RingGroupExtensions { get; set; }
        public string Strategy { get; set; }
        public List<string> AnsweredExtensions { get; set; }
        public List<string> MissedExtensions { get; set; }
        public string CallType { get; set; }
        public string LinkedId { get; set; }
        public string LastDialedExtension { get; set; }
        public int Duration { get; set; } // نگه داشته شده، همیشه 0
        public bool IsProcessed { get; set; } // پرچم جدید برای جلوگیری از پردازش تکراری
    }
    class ChannelInfo
    {
        public string CallUniqueId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    class RingGroup
    {
        public List<string> Extensions { get; set; }
        public string Strategy { get; set; }
    }

    class AsteriskEvent : ManagerEvent
    {
        public AsteriskEvent(ManagerConnection source) : base(source) { }
    }
}