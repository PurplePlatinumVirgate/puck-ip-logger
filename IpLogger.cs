using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport;

namespace IpLogger
{
    public class IpLoggerMod : IPuckMod
    {
        static readonly Harmony harmony = new Harmony("IpLogger");

        [HarmonyPatch(typeof(ServerManager), "Server_ConnectionApproval")]
        public class ConnectionApprovalPatch
        {
            internal static readonly object FileLock = new object();
            static readonly object BanListLock = new object();

            internal static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            internal static string LogPath;
            static readonly string BanListPath = Path.Combine(BaseDir, "ip_logger.banned_ip.json");

            static long _banListLastWriteUtcTicks = DateTime.MinValue.Ticks;
            static volatile bool _banListLoaded = false;

            static readonly Stopwatch _reloadCooldown = Stopwatch.StartNew();
            static long _lastReloadCheckMs = -5000;
            const long ReloadCooldownMs = 5000;

            static HashSet<string> _blockedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            static List<CidrRange> _blockedCidrs = new List<CidrRange>();
            static List<WildcardRule> _blockedWildcards = new List<WildcardRule>();

            static HashSet<string> _allowedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            static List<CidrRange> _allowedCidrs = new List<CidrRange>();
            static List<WildcardRule> _allowedWildcards = new List<WildcardRule>();

            static List<string> _includeFiles = new List<string>();
            static Dictionary<string, DateTime> _includeFileWriteTimesUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            internal static StreamWriter _logWriter;
            static volatile bool _logWriterWarned = false;
            internal static volatile bool _fileLoggingEnabled = true;

            internal sealed class BanListConfig
            {
                public List<string> blocklist = new List<string>();
                public List<string> blocklist_include_files = new List<string>();
                public List<string> allowlist = new List<string>();
                public List<string> allowlist_include_files = new List<string>();
                public bool enable_file_logging = true;
            }

            public class ConnectionState
            {
                public string Ip;
                public string SteamId;
                public string[] Mods;
                public bool Blocked;
                // True when the connection was initiated by an admin while the server
                // was full. The WebSocket success path must skip the capacity check.
                public bool AdminBypass;
                // The Netcode client ID, stored so the WebSocket handler can fire
                // Event_Server_ConnectionApproval with the correct ID on admin bypass.
                public ulong ClientNetworkId;
                // Set when the vanilla method defers approval (Pending=true).
                // Carried to the WebSocket response patch for final logging and auth enforcement.
                public NetworkManager.ConnectionApprovalResponse PendingResponse;
            }

            // Keyed by steamId. Holds connection state for approvals that are still pending
            // the WebSocket auth response, so the WebSocket patch can log the final outcome.
            internal static readonly Dictionary<string, ConnectionState> _pendingStates
                = new Dictionary<string, ConnectionState>();

            private class CidrRange
            {
                public string Original;
                public uint Network;
                public uint Mask;
            }

            private class WildcardRule
            {
                public string Original;
                public Regex Compiled;
            }

            [HarmonyPrefix]
            public static bool Prefix(
                ServerManager __instance,
                NetworkManager.ConnectionApprovalRequest request,
                NetworkManager.ConnectionApprovalResponse response,
                ref ConnectionState __state
            )
            {
                if (request.ClientNetworkId == 0UL)
                    return true;

                EnsureBanListLoaded();

                ConnectionData connectionData = TryDeserializeConnectionData(request.Payload);

                string endpoint = GetClientEndpointString(request.ClientNetworkId);
                string ip = ExtractIp(endpoint);
                string steamId = connectionData != null ? connectionData.SteamId : null;
                string[] mods = ExtractEnabledModIds(connectionData);

                __state = new ConnectionState
                {
                    Ip = ip,
                    SteamId = steamId,
                    Mods = mods,
                    Blocked = false,
                    ClientNetworkId = request.ClientNetworkId
                };

                string blockReason = GetBlockReason(ip);
                if (!string.IsNullOrEmpty(blockReason))
                {
                    response.Approved = false;
                    response.Pending = false;
                    response.Reason = BuildBannedReasonJson(__instance);

                    __state.Blocked = true;

                    UnityEngine.Debug.LogWarning(
                        "[ip_logger] BLOCKED" +
                        " ip=" + ValueOrUnknown(__state.Ip) +
                        " steam=" + ValueOrUnknown(__state.SteamId) +
                        " mods=" + string.Join(",", __state.Mods) +
                        " reason=Banned" +
                        " match=" + blockReason
                    );

                    WriteNdjsonEvent(
                        "BLOCKED",
                        __state.Ip,
                        __state.SteamId,
                        __state.Mods,
                        "Banned",
                        blockReason
                    );

                    TriggerConnectionApprovalEvent(request.ClientNetworkId, false);

                    return false;
                }

                // --- Admin bypass: allow admins to connect when the server is full ---
                // We only take over when the server IS full. If it's not full, vanilla
                // handles everything normally and the WebSocket auth still runs via the
                // standard path.
                //
                // The claimed SteamId is UNVERIFIED at this point. We never approve here;
                // we just send the WebSocket auth request so the auth server validates
                // the SteamId. The actual approval happens in the WebSocket response
                // handler, which checks AdminBypass on the pending state.
                if (IsDedicatedServer()
                    && connectionData != null
                    && !string.IsNullOrEmpty(steamId)
                    && !string.IsNullOrEmpty(connectionData.SocketId)
                    && ServerManager.Instance != null
                    && ServerManager.Instance.AdminSteamIds != null
                    && ServerManager.Instance.AdminSteamIds.Contains(steamId))
                {
                    bool serverFull = NetworkManager.Singleton.ConnectedClientsList.Count
                                      >= __instance.Server.MaxPlayers;

                    if (serverFull)
                    {
                        // Admins must still have required mods - reject immediately if not.
                        bool isModsMissing = __instance.ServerConfigurationManager
                            .ClientRequiredModIds
                            .Any(modId => !connectionData.EnabledModIds.Contains(modId));

                        if (isModsMissing)
                        {
                            // Let vanilla handle the rejection so the client gets the
                            // proper MissingMods rejection code with the mod list.
                            return true;
                        }

                        // Defer to WebSocket auth - do NOT approve yet.
                        response.Pending = true;

                        if (__instance.ConnectionApprovalRequests.ContainsKey(steamId))
                            __instance.ConnectionApprovalRequests.Remove(steamId);
                        __instance.ConnectionApprovalRequests.Add(steamId, response);

                        MonoBehaviourSingleton<WebSocketManager>.Instance.Emit(
                            "serverConnectionApprovalRequest",
                            new Dictionary<string, object>
                            {
                                { "steamId", steamId },
                                { "socketId", connectionData.SocketId }
                            },
                            "serverConnectionApprovalResponse"
                        );

                        __state.AdminBypass = true;

                        UnityEngine.Debug.Log(
                            "[ip_logger] Admin bypass (server full): deferring to auth for " +
                            request.ClientNetworkId + " (" + steamId + ")"
                        );

                        // Fire the event so ServerManagerController tracks the client.
                        // approved=false is correct here: the connection isn't approved
                        // yet, it's pending auth. If we said approved=true the controller
                        // would add it to approvedClients prematurely.
                        TriggerConnectionApprovalEvent(request.ClientNetworkId, false);

                        // Skip vanilla - we've set up the deferred auth ourselves.
                        return false;
                    }
                }

                return true;
            }

            [HarmonyPostfix]
            public static void Postfix(
                NetworkManager.ConnectionApprovalResponse response,
                ConnectionState __state
            )
            {
                if (__state == null || __state.Blocked)
                    return;

                // Approval is deferred - waiting on the WebSocket auth response.
                // Store state so the WebSocket patch can log the final outcome.
                if (response.Pending)
                {
                    if (!string.IsNullOrEmpty(__state.SteamId))
                    {
                        __state.PendingResponse = response;
                        lock (_pendingStates)
                            _pendingStates[__state.SteamId] = __state;
                    }
                    return;
                }

                string decision = response.Approved ? "APPROVED" : "REJECTED";

                string reasonCode = ExtractReasonCode(response.Reason);
                if (string.IsNullOrEmpty(reasonCode))
                    reasonCode = "<none>";

                UnityEngine.Debug.Log(
                    "[ip_logger] " + decision +
                    " ip=" + ValueOrUnknown(__state.Ip) +
                    " steam=" + ValueOrUnknown(__state.SteamId) +
                    " mods=" + string.Join(",", __state.Mods) +
                    " reason=" + reasonCode
                );

                WriteNdjsonEvent(
                    decision,
                    __state.Ip,
                    __state.SteamId,
                    __state.Mods,
                    reasonCode,
                    null
                );
            }

            private static void EnsureBanListLoaded()
            {
                try
                {
                    // Fast path: already loaded and cooldown hasn't elapsed
                    if (_banListLoaded)
                    {
                        long now = _reloadCooldown.ElapsedMilliseconds;
                        if (now - Interlocked.Read(ref _lastReloadCheckMs) < ReloadCooldownMs)
                            return;
                    }

                    lock (BanListLock)
                    {
                        // Re-check inside lock to avoid redundant reloads
                        if (_banListLoaded)
                        {
                            long now = _reloadCooldown.ElapsedMilliseconds;
                            if (now - _lastReloadCheckMs < ReloadCooldownMs)
                                return;

                            Interlocked.Exchange(ref _lastReloadCheckMs, now);
                        }

                        bool needsReload = !_banListLoaded;

                        DateTime currentWrite = File.Exists(BanListPath)
                            ? File.GetLastWriteTimeUtc(BanListPath)
                            : DateTime.MinValue;

                        if (!needsReload && currentWrite.Ticks != Interlocked.Read(ref _banListLastWriteUtcTicks))
                            needsReload = true;

                        if (!needsReload && HaveAnyIncludeFilesChanged())
                            needsReload = true;

                        if (!needsReload)
                            return;

                        ReloadBanListInternal(currentWrite);
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[ip_logger] Ban list reload check failed: " + ex.Message);
                }
            }

            private static bool HaveAnyIncludeFilesChanged()
            {
                foreach (var path in _includeFiles)
                {
                    DateTime current = File.Exists(path)
                        ? File.GetLastWriteTimeUtc(path)
                        : DateTime.MinValue;

                    if (!_includeFileWriteTimesUtc.TryGetValue(path, out var known))
                        return true;

                    if (current != known)
                        return true;
                }

                return false;
            }

            private static void ReloadBanListInternal(DateTime currentWrite)
            {
                HashSet<string> newBlockedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<CidrRange> newBlockedCidrs = new List<CidrRange>();
                List<WildcardRule> newBlockedWildcards = new List<WildcardRule>();

                HashSet<string> newAllowedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<CidrRange> newAllowedCidrs = new List<CidrRange>();
                List<WildcardRule> newAllowedWildcards = new List<WildcardRule>();

                List<string> newIncludeFiles = new List<string>();
                Dictionary<string, DateTime> newTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    if (!File.Exists(BanListPath))
                    {
                        _blockedIps = newBlockedIps;
                        _blockedCidrs = newBlockedCidrs;
                        _blockedWildcards = newBlockedWildcards;
                        _allowedIps = newAllowedIps;
                        _allowedCidrs = newAllowedCidrs;
                        _allowedWildcards = newAllowedWildcards;
                        _includeFiles = newIncludeFiles;
                        _includeFileWriteTimesUtc = newTimes;
                        Interlocked.Exchange(ref _banListLastWriteUtcTicks, currentWrite.Ticks);
                        _banListLoaded = true;
                        return;
                    }

                    BanListConfig config = LoadBanListConfig(BanListPath);

                    // Load blocklist rules
                    foreach (var entry in config.blocklist)
                        AddRuleEntry(entry, newBlockedIps, newBlockedCidrs, newBlockedWildcards);

                    foreach (var include in config.blocklist_include_files)
                    {
                        string path = ResolveConfigRelativePath(include);
                        if (path == null)
                            continue;

                        newIncludeFiles.Add(path);
                        TrackFileTimestamp(path, newTimes);
                        LoadRuleEntriesFromFile(path, newBlockedIps, newBlockedCidrs, newBlockedWildcards);
                    }

                    // Load allowlist rules
                    foreach (var entry in config.allowlist)
                        AddRuleEntry(entry, newAllowedIps, newAllowedCidrs, newAllowedWildcards);

                    foreach (var include in config.allowlist_include_files)
                    {
                        string path = ResolveConfigRelativePath(include);
                        if (path == null)
                            continue;

                        newIncludeFiles.Add(path);
                        TrackFileTimestamp(path, newTimes);
                        LoadRuleEntriesFromFile(path, newAllowedIps, newAllowedCidrs, newAllowedWildcards);
                    }

                    _blockedIps = newBlockedIps;
                    _blockedCidrs = newBlockedCidrs;
                    _blockedWildcards = newBlockedWildcards;
                    _allowedIps = newAllowedIps;
                    _allowedCidrs = newAllowedCidrs;
                    _allowedWildcards = newAllowedWildcards;
                    _includeFiles = newIncludeFiles;
                    _includeFileWriteTimesUtc = newTimes;

                    bool wasEnabled = _fileLoggingEnabled;
                    _fileLoggingEnabled = config.enable_file_logging;

                    if (wasEnabled && !_fileLoggingEnabled)
                    {
                        lock (FileLock)
                        {
                            _logWriter?.Flush();
                            _logWriter?.Dispose();
                            _logWriter = null;
                        }
                        UnityEngine.Debug.Log("[ip_logger] File logging disabled via config");
                    }
                    else if (!wasEnabled && _fileLoggingEnabled)
                    {
                        lock (FileLock)
                        {
                            if (_logWriter == null && LogPath != null)
                            {
                                _logWriter = new StreamWriter(LogPath, true, Encoding.UTF8)
                                    { AutoFlush = true };
                                _logWriterWarned = false;
                            }
                        }
                        UnityEngine.Debug.Log("[ip_logger] File logging enabled via config");
                    }

                    Interlocked.Exchange(ref _banListLastWriteUtcTicks, currentWrite.Ticks);
                    _banListLoaded = true;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[ip_logger] Failed loading ban list: " + ex.Message);
                }
            }

            private static void TrackFileTimestamp(string path, Dictionary<string, DateTime> times)
            {
                DateTime write = File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path)
                    : DateTime.MinValue;

                times[path] = write;
            }

            internal static BanListConfig LoadBanListConfig(string path)
            {
                try
                {
                    return JsonConvert.DeserializeObject<BanListConfig>(File.ReadAllText(path))
                        ?? new BanListConfig();
                }
                catch
                {
                    return new BanListConfig();
                }
            }

            private static void LoadRuleEntriesFromFile(
                string path,
                HashSet<string> ips,
                List<CidrRange> cidrs,
                List<WildcardRule> wildcards
            )
            {
                if (!File.Exists(path))
                    return;

                foreach (string raw in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    string value = raw.Trim();
                    if (value.StartsWith("#"))
                        continue;

                    AddRuleEntry(value, ips, cidrs, wildcards);
                }
            }

            private static void AddRuleEntry(
                string value,
                HashSet<string> ips,
                List<CidrRange> cidrs,
                List<WildcardRule> wildcards
            )
            {
                value = value.Trim();

                if (value.Contains("*"))
                {
                    string regex = "^" + Regex.Escape(value).Replace("\\*", ".*") + "$";
                    wildcards.Add(new WildcardRule
                    {
                        Original = value,
                        Compiled = new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.Compiled)
                    });
                    return;
                }

                if (value.Contains("/"))
                {
                    if (TryParseCidr(value, out var cidr))
                    {
                        cidrs.Add(cidr);
                        return;
                    }

                    UnityEngine.Debug.LogWarning("[ip_logger] Invalid CIDR rule " + value);
                    return;
                }

                string ip = NormalizeIp(value);
                if (ip != null)
                    ips.Add(ip);
            }

            private static string ResolveConfigRelativePath(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                if (Path.IsPathRooted(path))
                    return path;

                return Path.GetFullPath(Path.Combine(BaseDir, path));
            }

            private static bool IsAllowed(uint ipVal, string normalized)
            {
                if (_allowedIps.Contains(normalized))
                    return true;

                foreach (var cidr in _allowedCidrs)
                    if ((ipVal & cidr.Mask) == cidr.Network)
                        return true;

                foreach (var rule in _allowedWildcards)
                    if (rule.Compiled.IsMatch(normalized))
                        return true;

                return false;
            }

            private static string GetBlockReason(string ip)
            {
                if (string.IsNullOrEmpty(ip))
                    return null;

                if (!TryParseIpv4(ip, out uint ipVal))
                    return null;

                string normalized = UInt32ToIpv4(ipVal);

                lock (BanListLock)
                {
                    // Allowlist takes priority over blocklist
                    if (IsAllowed(ipVal, normalized))
                        return null;

                    if (_blockedIps.Contains(normalized))
                        return "ip:" + normalized;

                    foreach (var cidr in _blockedCidrs)
                        if ((ipVal & cidr.Mask) == cidr.Network)
                            return "cidr:" + cidr.Original;

                    foreach (var rule in _blockedWildcards)
                        if (rule.Compiled.IsMatch(normalized))
                            return "wildcard:" + rule.Original;
                }

                return null;
            }

            private static string NormalizeIp(string value)
            {
                if (!TryParseIpv4(value, out uint ip))
                    return null;

                return UInt32ToIpv4(ip);
            }

            private static bool TryParseCidr(string value, out CidrRange cidr)
            {
                cidr = null;

                var parts = value.Split('/');
                if (parts.Length != 2)
                    return false;

                if (!TryParseIpv4(parts[0], out uint ip))
                    return false;

                if (!int.TryParse(parts[1], out int prefix))
                    return false;

                if (prefix < 0 || prefix > 32)
                    return false;

                uint mask = PrefixLengthToMask(prefix);

                cidr = new CidrRange
                {
                    Original = value,
                    Network = ip & mask,
                    Mask = mask
                };

                return true;
            }

            private static uint PrefixLengthToMask(int prefix)
            {
                if (prefix <= 0)
                    return 0;

                if (prefix >= 32)
                    return 0xffffffff;

                return 0xffffffff << (32 - prefix);
            }

            private static bool TryParseIpv4(string value, out uint result)
            {
                result = 0;

                string[] parts = value.Split('.');
                if (parts.Length != 4)
                    return false;

                if (!byte.TryParse(parts[0], out byte a)) return false;
                if (!byte.TryParse(parts[1], out byte b)) return false;
                if (!byte.TryParse(parts[2], out byte c)) return false;
                if (!byte.TryParse(parts[3], out byte d)) return false;

                result = ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
                return true;
            }

            private static string UInt32ToIpv4(uint value)
            {
                return
                    ((value >> 24) & 255) + "." +
                    ((value >> 16) & 255) + "." +
                    ((value >> 8) & 255) + "." +
                    (value & 255);
            }

            private static ConnectionData TryDeserializeConnectionData(byte[] payloadBytes)
            {
                try
                {
                    if (payloadBytes == null || payloadBytes.Length == 0)
                        return null;

                    string payload = Encoding.ASCII.GetString(payloadBytes);
                    return JsonConvert.DeserializeObject<ConnectionData>(payload);
                }
                catch
                {
                    return null;
                }
            }

            private static string[] ExtractEnabledModIds(ConnectionData connectionData)
            {
                if (connectionData == null || connectionData.EnabledModIds == null)
                    return new string[0];

                ulong[] ids = connectionData.EnabledModIds;
                string[] mods = new string[ids.Length];

                for (int i = 0; i < ids.Length; i++)
                    mods[i] = ids[i].ToString();

                return mods;
            }

            internal static void WriteNdjsonEvent(
                string decision,
                string ip,
                string steamId,
                string[] mods,
                string reason,
                string blockMatch
            )
            {
                if (!_fileLoggingEnabled)
                    return;

                JObject obj = new JObject
                {
                    ["ts_unix_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["ts_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    ["decision"] = decision,
                    ["ip"] = ip,
                    ["steam_id"] = steamId,
                    ["mods"] = mods != null ? JArray.FromObject(mods) : new JArray(),
                    ["reason"] = reason,
                    ["block_match"] = blockMatch
                };

                lock (FileLock)
                {
                    if (_logWriter == null)
                    {
                        if (!_logWriterWarned)
                        {
                            _logWriterWarned = true;
                            UnityEngine.Debug.LogError("[ip_logger] Log writer is null, events will be dropped");
                        }
                        return;
                    }

                    _logWriter.WriteLine(obj.ToString(Formatting.None));
                }
            }

            private static string GetClientEndpointString(ulong clientId)
            {
                try
                {
                    UnityTransport transport =
                        NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;

                    NetworkEndpoint endpoint = transport.GetEndpoint(clientId);
                    return endpoint.ToString();
                }
                catch
                {
                    return null;
                }
            }

            private static string ExtractIp(string endpoint)
            {
                if (string.IsNullOrEmpty(endpoint))
                    return null;

                int idx = endpoint.LastIndexOf(':');
                if (idx <= 0)
                    return endpoint;

                return endpoint.Substring(0, idx);
            }

            private static string ExtractReasonCode(string reasonJson)
            {
                try
                {
                    if (string.IsNullOrEmpty(reasonJson))
                        return null;

                    JObject obj = JsonConvert.DeserializeObject<JObject>(reasonJson);
                    var codeToken = obj?["code"];
                    if (codeToken == null)
                        return null;

                    int codeInt = codeToken.Value<int>();
                    if (Enum.IsDefined(typeof(ConnectionRejectionCode), codeInt))
                        return ((ConnectionRejectionCode)codeInt).ToString();

                    return codeInt.ToString();
                }
                catch
                {
                    return null;
                }
            }

            private static string BuildBannedReasonJson(ServerManager serverManager)
            {
                ulong[] mods =
                    serverManager?.ServerConfigurationManager?.ClientRequiredModIds
                    ?? new ulong[0];

                return JsonConvert.SerializeObject(new
                {
                    code = (int)ConnectionRejectionCode.Banned,
                    clientRequiredModIds = mods
                });
            }

            internal static void TriggerConnectionApprovalEvent(ulong clientNetworkId, bool approved)
            {
                try
                {
                    MonoBehaviourSingleton<EventManager>.Instance.TriggerEvent(
                        "Event_Server_ConnectionApproval",
                        new Dictionary<string, object>
                        {
                            { "clientId", clientNetworkId },
                            { "approved", approved }
                        }
                    );
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[ip_logger] Event trigger failed: " + ex.Message);
                }
            }

            internal static string ExtractReasonCodePublic(string reasonJson) => ExtractReasonCode(reasonJson);
            internal static string ValueOrUnknownPublic(string value) => ValueOrUnknown(value);

            private static string ValueOrUnknown(string value)
            {
                return string.IsNullOrEmpty(value) ? "<missing>" : value;
            }

            private static bool IsDedicatedServer()
            {
                return Application.isBatchMode;
            }
        }

        // Patches ServerManagerController.WebSocket_Event_OnServerConnectionApprovalResponse.
        //
        // The vanilla implementation ignores success/error from the auth server and only
        // checks server capacity - a "Player not found" (success=false) response still
        // results in the player being approved. This patch enforces rejection on auth failure
        // and logs the final connection outcome (deferred from the Server_ConnectionApproval
        // postfix above which cannot log while Pending=true).
        [HarmonyPatch]
        public class WebSocketConnectionApprovalResponsePatch
        {
            static MethodBase TargetMethod()
            {
                return typeof(ServerManagerController).GetMethod(
                    "WebSocket_Event_OnServerConnectionApprovalResponse",
                    BindingFlags.NonPublic | BindingFlags.Instance
                );
            }

            // On auth failure: enforce rejection before vanilla runs, then skip vanilla
            // entirely by returning false. This guarantees Unity Netcode sees our rejection
            // before the connection is finalised - vanilla sets Approved=true then clears
            // Pending in the same call, so a postfix-only approach loses the race.
            // On auth success: return true and let vanilla run normally. Admin bypass
            // override (if needed) happens in the postfix, AFTER vanilla has fully run.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(
                ServerManagerController __instance,
                Dictionary<string, object> message
            )
            {
                try
                {
                    var authResponse = GetAuthResponse(message);
                    if (authResponse == null || string.IsNullOrEmpty(authResponse.steamId))
                        return true;

                    // Auth succeeded - let vanilla handle it, postfix will log
                    // (and override for admin bypass if needed).
                    if (authResponse.success)
                        return true;

                    string steamId = authResponse.steamId;
                    string error   = authResponse.error ?? "<no error>";

                    ServerManager serverManager = GetServerManager(__instance);
                    if (serverManager == null)
                    {
                        UnityEngine.Debug.LogError("[ip_logger] WebSocket prefix: could not retrieve ServerManager for steamId=" + steamId);
                        return true;
                    }

                    if (!serverManager.ConnectionApprovalRequests.ContainsKey(steamId))
                        return true;

                    NetworkManager.ConnectionApprovalResponse approvalResponse =
                        serverManager.ConnectionApprovalRequests[steamId];

                    // Enforce rejection before vanilla can approve.
                    approvalResponse.Approved = false;
                    approvalResponse.Pending  = false;
                    approvalResponse.Reason   = BuildRejectionJson(serverManager);

                    serverManager.ConnectionApprovalRequests.Remove(steamId);

                    UnityEngine.Debug.LogWarning(
                        "[ip_logger] Auth server rejected steamId=" + steamId +
                        " error=\"" + error + "\" - enforcing rejection."
                    );

                    // Update the pending state's response reference so the postfix can log.
                    lock (ConnectionApprovalPatch._pendingStates)
                    {
                        if (ConnectionApprovalPatch._pendingStates.TryGetValue(steamId, out var state))
                            state.PendingResponse = approvalResponse;
                    }

                    // Skip vanilla - rejection is already finalised.
                    return false;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[ip_logger] WebSocket prefix error: " + ex.Message);
                    return true;
                }
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            public static void Postfix(
                ServerManagerController __instance,
                Dictionary<string, object> message
            )
            {
                try
                {
                    var authResponse = GetAuthResponse(message);
                    if (authResponse == null || string.IsNullOrEmpty(authResponse.steamId))
                        return;

                    string steamId = authResponse.steamId;

                    ConnectionApprovalPatch.ConnectionState state;
                    lock (ConnectionApprovalPatch._pendingStates)
                    {
                        if (!ConnectionApprovalPatch._pendingStates.TryGetValue(steamId, out state))
                            return;
                        ConnectionApprovalPatch._pendingStates.Remove(steamId);
                    }

                    // PendingResponse is set by the prefix (auth failure) or carried from
                    // Server_ConnectionApproval postfix (auth success, vanilla ran normally).
                    // If vanilla ran (success path), grab the response from state which was
                    // populated by the Server_ConnectionApproval postfix.
                    var response = state.PendingResponse;
                    if (response == null)
                        return;

                    // --- Admin bypass override ---
                    // At this point, BOTH the prefix auth enforcement AND vanilla have
                    // fully run. If this was an admin-bypass connection (server was full)
                    // and the auth server confirmed the SteamId is genuine, vanilla will
                    // have rejected due to capacity. Override that rejection now.
                    if (state.AdminBypass && authResponse.success && !response.Approved)
                    {
                        response.Approved = true;
                        response.Reason   = null;

                        // Fire the approval event so the controller tracks the client
                        // (used for Edgegap deployment cleanup).
                        ConnectionApprovalPatch.TriggerConnectionApprovalEvent(
                            state.ClientNetworkId, true);

                        UnityEngine.Debug.Log(
                            "[ip_logger] Admin bypass: overriding ServerFull rejection " +
                            "for verified admin " + steamId);
                    }

                    string decision  = response.Approved ? "APPROVED" : "REJECTED";
                    string reasonCode = ConnectionApprovalPatch.ExtractReasonCodePublic(response.Reason);
                    if (string.IsNullOrEmpty(reasonCode))
                        reasonCode = "<none>";
                    if (!authResponse.success)
                        reasonCode += "_LikelySpoofAttempt";
                    if (state.AdminBypass)
                        reasonCode += "_AdminBypass";

                    UnityEngine.Debug.Log(
                        "[ip_logger] " + decision +
                        " ip=" + ConnectionApprovalPatch.ValueOrUnknownPublic(state.Ip) +
                        " steam=" + ConnectionApprovalPatch.ValueOrUnknownPublic(state.SteamId) +
                        " mods=" + string.Join(",", state.Mods) +
                        " reason=" + reasonCode
                    );

                    ConnectionApprovalPatch.WriteNdjsonEvent(
                        decision,
                        state.Ip,
                        state.SteamId,
                        state.Mods,
                        reasonCode,
                        null
                    );
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[ip_logger] WebSocket postfix error: " + ex.Message);
                }
            }

            private static ServerConnectionApprovalResponse GetAuthResponse(Dictionary<string, object> message)
            {
                try
                {
                    var socketResponse = (SocketIOClient.SocketIOResponse)message["response"];
                    return socketResponse.GetValue<ServerConnectionApprovalResponse>(0);
                }
                catch
                {
                    return null;
                }
            }

            static FieldInfo _serverManagerField;

            private static ServerManager GetServerManager(ServerManagerController controller)
            {
                try
                {
                    if (_serverManagerField == null)
                        _serverManagerField = typeof(ServerManagerController).GetField(
                            "serverManager", BindingFlags.NonPublic | BindingFlags.Instance);
                    return _serverManagerField?.GetValue(controller) as ServerManager;
                }
                catch { return null; }
            }

            private static string BuildRejectionJson(ServerManager serverManager)
            {
                ulong[] mods = serverManager?.ServerConfigurationManager?.ClientRequiredModIds
                               ?? new ulong[0];
                return JsonConvert.SerializeObject(new
                {
                    code = (int)ConnectionRejectionCode.InvalidSteamId,
                    clientRequiredModIds = mods
                });
            }
        }

        static readonly string ModVersion =
            typeof(IpLoggerMod).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown";

        public bool OnEnable()
        {
            try
            {
                harmony.PatchAll();

                string configPath = Path.Combine(
                    ConnectionApprovalPatch.BaseDir, "ip_logger.banned_ip.json"
                );
                var startupConfig = File.Exists(configPath)
                    ? ConnectionApprovalPatch.LoadBanListConfig(configPath)
                    : new ConnectionApprovalPatch.BanListConfig();
                ConnectionApprovalPatch._fileLoggingEnabled = startupConfig.enable_file_logging;

                if (ConnectionApprovalPatch._fileLoggingEnabled)
                {
                    string logDir = Path.Combine(ConnectionApprovalPatch.BaseDir, "Logs");
                    Directory.CreateDirectory(logDir);

                    string logFileName = string.Format(
                        "ip_logger_{0}_{1}.ndjson",
                        DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"),
                        Process.GetCurrentProcess().Id
                    );

                    lock (ConnectionApprovalPatch.FileLock)
                    {
                        ConnectionApprovalPatch.LogPath = Path.Combine(
                            logDir, logFileName
                        );
                        ConnectionApprovalPatch._logWriter = new StreamWriter(
                            ConnectionApprovalPatch.LogPath, true, Encoding.UTF8
                        ) { AutoFlush = true };
                    }
                }
                else
                {
                    UnityEngine.Debug.Log("[ip_logger] File logging disabled via config");
                }

                UnityEngine.Debug.Log("[ip_logger] Enabled v" + ModVersion);
                return true;
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();

                lock (ConnectionApprovalPatch.FileLock)
                {
                    ConnectionApprovalPatch._logWriter?.Dispose();
                    ConnectionApprovalPatch._logWriter = null;
                }

                UnityEngine.Debug.LogError("[ip_logger] Enable failed: " + e.Message);
                return false;
            }
        }

        public bool OnDisable()
        {
            try
            {
                harmony.UnpatchSelf();
                lock (ConnectionApprovalPatch.FileLock)
                {
                    ConnectionApprovalPatch._logWriter?.Flush();
                    ConnectionApprovalPatch._logWriter?.Dispose();
                    ConnectionApprovalPatch._logWriter = null;
                }
                UnityEngine.Debug.Log("[ip_logger] Disabled v" + ModVersion);
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[ip_logger] Harmony unpatch failed: " + e.Message);
                return false;
            }
        }
    }
}