# IP Logger

A [Puck](https://store.steampowered.com/app/2994020/Puck/) server mod that logs every connection attempt with IP address, Steam ID, enabled mods, and decision - and optionally blocks connections by IP.

> **Status:** Early testing. Will be on the Steam Workshop eventually; for now grab it from [GitHub releases](https://github.com/PurplePlatinumVirgate/puck-ip-logger/releases) or [build it from source](/notes.md#building).

- [IP Logger](#ip-logger)
  - [What It Does](#what-it-does)
  - [Installation](#installation)
  - [Log File](#log-file)
    - [Example: Approved Connection](#example-approved-connection)
    - [Example: Rejected by Game (Missing Mods)](#example-rejected-by-game-missing-mods)
    - [Example: Blocked by IP Ban](#example-blocked-by-ip-ban)
    - [Example: Blocked by CIDR Range](#example-blocked-by-cidr-range)
    - [Fields](#fields)
    - [Working With the Log](#working-with-the-log)
    - [Console Output](#console-output)
  - [Ban List](#ban-list)
    - [Configuration Format](#configuration-format)
    - [Rule Types](#rule-types)
    - [Include Files](#include-files)
    - [Hot Reload](#hot-reload)
    - [Allowlist](#allowlist)
  - [Blocking VPNs and Datacenters](#blocking-vpns-and-datacenters)
    - [Why Block VPNs?](#why-block-vpns)
    - [Setup](#setup)
    - [Keep Lists Updated](#keep-lists-updated)
    - [Performance](#performance)
    - [Configure ip\_logger to Use Them](#configure-ip_logger-to-use-them)
    - [A Note on False Positives](#a-note-on-false-positives)
  - [Limitations](#limitations)
  - [Troubleshooting](#troubleshooting)


---

## What It Does

Every time a player connects to your server, ip_logger:

1. Resolves their IP address from the unity transport layer
2. Checks the IP against your ban list (if configured)
3. Logs the result - approved, rejected, or blocked - to a structured log file

All of this happens transparently. If you don't configure a ban list, the mod simply logs connections without interfering. 

If you need more technical details check [notes.md](/notes.md)

---

## Installation

Place the mod DLL in your Puck server's Plugins directory. E.g. `Plugins/ip_logger/IpLogger.dll` The mod will create its log file automatically on first connection.

---

## Log File

All connection events are written to `ip_logger.ndjson` in the game's base directory. Each line is a self-contained JSON object, one per connection attempt.

### Example: Approved Connection

```json
{"ts_unix_ms":1709913600000,"ts_utc":"2025-03-08T16:00:00.000Z","decision":"APPROVED","ip":"203.0.113.45","steam_id":"76561198001353738","mods":["3399892384","3401567201"],"reason":"<none>","block_match":null}
```

### Example: Rejected by Game (Missing Mods)

```json
{"ts_unix_ms":1709913605000,"ts_utc":"2025-03-08T16:00:05.000Z","decision":"REJECTED","ip":"198.51.100.12","steam_id":"76561198001353738","mods":["3399892384"],"reason":"MissingMods","block_match":null}
```

### Example: Blocked by IP Ban

```json
{"ts_unix_ms":1709913610000,"ts_utc":"2025-03-08T16:00:10.000Z","decision":"BLOCKED","ip":"192.168.1.100","steam_id":"76561198001353738","mods":["3399892384","3401567201"],"reason":"Banned","block_match":"ip:192.168.1.100"}
```

### Example: Blocked by CIDR Range

```json
{"ts_unix_ms":1709913615000,"ts_utc":"2025-03-08T16:00:15.000Z","decision":"BLOCKED","ip":"10.0.50.7","steam_id":"76561198001353738","mods":[],"reason":"Banned","block_match":"cidr:10.0.0.0/8"}
```

### Fields

| Field | Description |
|-------|-------------|
| `ts_unix_ms` | Unix timestamp in milliseconds |
| `ts_utc` | Human-readable UTC timestamp |
| `decision` | `APPROVED`, `REJECTED`, or `BLOCKED` |
| `ip` | Client's IP address (or `null` if unavailable) |
| `steam_id` | Client's Steam ID (or `null` if unavailable) |
| `mods` | Array of enabled mod IDs the client reported |
| `reason` | Rejection reason from the game (`MissingMods`, `ServerFull`, `Banned`, `InvalidPassword`, etc.), `Banned` for IP blocks, or `<none>` for approved connections |
| `block_match` | The ban list rule that matched (e.g. `ip:1.2.3.4`, `cidr:10.0.0.0/8`, `wildcard:192.168.*.*`), or `null` |

### Working With the Log

The NDJSON format (one JSON object per line) works well with standard command-line tools:

```bash
# Tail the log in real time
tail -f ip_logger.ndjson

# Find all blocked connections
grep '"BLOCKED"' ip_logger.ndjson

# Find all connections from a specific IP
grep '"203.0.113.45"' ip_logger.ndjson

# Find all connections from a specific Steam ID
grep '"76561198001353738"' ip_logger.ndjson

# Count connections per decision type
jq -r '.decision' ip_logger.ndjson | sort | uniq -c

# List all unique IPs that connected today
grep "$(date -u +%Y-%m-%d)" ip_logger.ndjson | jq -r '.ip' | sort -u
```

### Console Output

The mod also writes to the Unity server console for real-time monitoring. These show up alongside the game's normal log output:

```
[ip_logger] APPROVED ip=127.0.0.1 steam=76561198001353738 mods=3612637610,3525612161,3551287814,3544923970,3557092964,3566470321,3574183948,3510279949,3578502215,3578502263,3578513105,3505900588,3505903245,3506676143,3508066071,3493628417,3497550964,3493915291,3496198194,3493810891 reason=<none>
```

```
[ip_logger] BLOCKED ip=127.0.0.1 steam=76561198001353738 mods=3612637610,3525612161,3551287814,3544923970,3557092964,3566470321,3574183948,3510279949,3578502215,3578502263,3578513105,3505900588,3505903245,3506676143,3508066071,3493628417,3497550964,3493915291,3496198194,3493810891 reason=Banned match=ip:127.0.0.1
```

```
[ip_logger] BLOCKED ip=127.0.0.1 steam=76561198001353738 mods=3612637610,3525612161,3551287814,3544923970,3557092964,3566470321,3574183948,3510279949,3578502215,3578502263,3578513105,3505900588,3505903245,3506676143,3508066071,3493628417,3497550964,3493915291,3496198194,3493810891 reason=Banned match=wildcard:127.0.0.*
```

```
[ip_logger] BLOCKED ip=127.0.0.1 steam=76561198001353738 mods=3612637610,3525612161,3551287814,3544923970,3557092964,3566470321,3574183948,3510279949,3578502215,3578502263,3578513105,3505900588,3505903245,3506676143,3508066071,3493628417,3497550964,3493915291,3496198194,3493810891 reason=Banned match=cidr:127.0.0.1/8
```

The `match` field tells you exactly which rule triggered the block, making it easy to trace back to your config.

---

## Ban List

To block connections by IP, create a file called `ip_logger.banned_ip.json` in the game's base directory (next to the executable).

### Configuration Format

```json
{
  "blocklist": [
    "203.0.113.45",
    "198.51.100.0/24",
    "10.0.*.*"
  ],
  "include_files": [
    "extra_bans.txt",
    "vpn_ipv4.txt"
  ],
  "allowlist": [
    "203.0.113.99"
  ],
  "allowlist_include_files": []
}
```

### Rule Types

**Exact IP** - Matches a single address.

```
203.0.113.45
```

**CIDR Range** - Matches an entire subnet. The prefix length must be between 0 and 32.

```
198.51.100.0/24
10.0.0.0/8
```

**Wildcard** - Uses `*` to match any value in an octet position. Supports any pattern, not just trailing wildcards.

```
10.0.*.*
192.168.1.*
172.*.0.1
```

### Include Files

The `include_files` array lets you reference external text files. Paths are relative to the game's base directory (or absolute). Each file contains one rule per line. Blank lines and lines starting with `#` are ignored.

Example `extra_bans.txt`:

```
# Manually banned IPs
203.0.113.45
198.51.100.0/24

# That one guy
192.168.1.100
```

### Hot Reload

You don't need to restart the server to update the ban list. When a player connects, the mod checks whether it's been at least 5 seconds since the last file check. If so, it looks at the timestamps on the main config and all include files. If anything has changed, the entire ban list is rebuilt automatically. No polling or background threads - the check only happens on connection attempts.

### Allowlist

The allowlist lets you exempt specific IPs from the blocklist. This is useful when you're using broad VPN/datacenter block lists but need to let specific players through. Allowlist rules are checked before blocklist rules - if an IP matches the allowlist, it is never blocked.

The allowlist supports the same rule types as the blocklist (exact IP, CIDR, wildcard) and has its own `allowlist_include_files` for external files.

```json
{
  "blocklist": [],
  "include_files": [
    "ip_lists/vpn_ipv4.txt",
    "ip_lists/datacenter_ipv4.txt"
  ],
  "allowlist": [
    "203.0.113.99",
    "198.51.100.0/28"
  ],
  "allowlist_include_files": [
    "trusted_ips.txt"
  ]
}
```

In this example, all VPN and datacenter IPs are blocked except for `203.0.113.99`, the `198.51.100.0/28` subnet, and any IPs listed in `trusted_ips.txt`.

---

## Blocking VPNs and Datacenters

If you want to prevent players from connecting through VPNs or datacenter proxies, you can use the community-maintained IP lists from [X4BNet/lists_vpn](https://github.com/X4BNet/lists_vpn). These lists are updated regularly and cover most commercial VPN providers and datacenter IP ranges.

### Why Block VPNs?

Players who are banned by Steam ID can easily reconnect through a VPN with a new account. Blocking known VPN and datacenter IP ranges makes ban evasion significantly harder. Most legitimate players connect from residential ISPs and won't be affected.

### Setup

Download the lists and set up a cron job to keep them updated:

```bash
# Create a directory for the lists
mkdir -p /path/to/puck-server/ip_lists

# Download the IPv4 lists
curl -sL https://raw.githubusercontent.com/X4BNet/lists_vpn/refs/heads/main/output/vpn/ipv4.txt \
  -o /path/to/puck-server/ip_lists/vpn_ipv4.txt

curl -sL https://raw.githubusercontent.com/X4BNet/lists_vpn/refs/heads/main/output/datacenter/ipv4.txt \
  -o /path/to/puck-server/ip_lists/datacenter_ipv4.txt
```

### Keep Lists Updated

Create a cron job to pull fresh lists daily. The mod's hot reload will pick up the changes automatically.

```bash
# Edit crontab
crontab -e

# Add this line to update at 4 AM daily
0 4 * * * curl -sL https://raw.githubusercontent.com/X4BNet/lists_vpn/refs/heads/main/output/vpn/ipv4.txt -o /path/to/puck-server/ip_lists/vpn_ipv4.txt && curl -sL https://raw.githubusercontent.com/X4BNet/lists_vpn/refs/heads/main/output/datacenter/ipv4.txt -o /path/to/puck-server/ip_lists/datacenter_ipv4.txt
```

### Performance

The VPN and datacenter lists are large (10,000+ CIDR ranges combined), but the matching is fast. Each CIDR check is a single bitwise AND and compare on a 32-bit integer, so scanning the entire list takes microseconds. Connection approval already involves JSON deserialization, network I/O, and puck central websocket validation, all of which are orders of magnitude slower. The lists are parsed once at load time and only reloaded when the files change. Players won't notice any difference.

### Configure ip_logger to Use Them

Add the downloaded files to your ban list config:

```json
{
  "blocklist": [],
  "include_files": [
    "ip_lists/vpn_ipv4.txt",
    "ip_lists/datacenter_ipv4.txt"
  ],
  "allowlist": [],
  "allowlist_include_files": []
}
```

You can combine these with your own manual bans and allowlist exceptions:

```json
{
  "blocklist": [
    "203.0.113.45",
    "198.51.100.0/24"
  ],
  "include_files": [
    "ip_lists/vpn_ipv4.txt",
    "ip_lists/datacenter_ipv4.txt",
    "manual_bans.txt"
  ],
  "allowlist": [
    "198.51.100.12"
  ],
  "allowlist_include_files": [
    "trusted_ips.txt"
  ]
}
```

### A Note on False Positives

These lists are broad. Some legitimate players may use VPNs for privacy, or connect from cloud-hosted machines. If a player reports they can't connect and you see `BLOCKED` entries for their IP with a `cidr:` or `wildcard:` match from the VPN/datacenter lists, you can:

- Add their IP to the `allowlist` in your config (allowlist is checked before blocklist)
- Add their IP to a file referenced in `allowlist_include_files`
- Use only the `vpn_ipv4.txt` list (narrower) without the datacenter list (broader)
- Remove the specific list that's blocking them

---

## Limitations

**Shared IPs** - Multiple players can share the same IP address. This is common with households, LAN events, corporate networks, and university campuses. Keep this in mind before blocking an IP - you may be blocking more than one person. 

**IP is not identity** - IP addresses identify a network endpoint, not a person. They can change (DHCP, mobile networks) and can be shared (NAT, VPNs). Use IP blocking as one tool among several, not as a sole source of truth.

**Steam ID is client-reported** - The Steam ID in the connection payload comes from the connecting client. The game's central server performs its own validation when `usePuckBannedSteamIds` is enabled, but the value logged by this mod is what the client sent. Keep this in mind when correlating Steam IDs across log entries. Historically malicious users have spoofed the Steam ID, and I haven't verified if the central Puck server uses known player websocket ID to validate (It is sent, but I don't know if it's ignored).

**Log correlation** - Matching a Steam ID to an IP is most reliable when done close in time to a specific event (e.g. checking who connected just before something happened in-game). Broad statistical analysis across longer time periods should be treated as lower confidence, especially when multiple Steam IDs appear from the same IP.

**Log file growth** - The log file grows indefinitely. The mod holds the file handle open for the lifetime of the server process, so `logrotate` with `copytruncate` won't work correctly - the writer will keep appending at its old file offset, producing a corrupted file. To rotate the log, stop the server, move or archive `ip_logger.ndjson`, and start the server again. The mod will create a fresh file on the next connection.


---

## Troubleshooting

**No log file appearing?**
The log file is created on the first connection attempt. If no one has connected yet, the file won't exist. Check the Unity log (Player.log or the server console) for `[ip_logger]` error messages.

**IPs showing as `null`?**
This can happen if the network transport isn't a `UnityTransport` instance, or if the client disconnects before the approval callback runs. The connection will still be logged with whatever information was available.

**Ban list not loading?**
Check the server console for `[ip_logger]` warnings. Common issues include malformed JSON in the config file, invalid CIDR notation (e.g., prefix outside 0-32), or include files that don't exist at the specified path.

**Player blocked but shouldn't be?**
Check the `block_match` field in the log to see which rule matched. If you're using VPN/datacenter lists, the player's IP may be in one of those ranges - see the false positives section above.