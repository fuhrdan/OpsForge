# Linux Agent

OpsForge v1.1.0 supports Linux hosts with the same authenticated heartbeat contract used by Windows agents.

## Metrics

| Capability | Linux source |
|---|---|
| CPU | `/proc/stat` delta sampling |
| Memory | `/proc/meminfo` (`MemAvailable`) |
| Uptime | `/proc/uptime` |
| Processes | .NET `Process` APIs |
| Filesystems | .NET `DriveInfo` |
| Network adapters | .NET `NetworkInterface` |
| Services | `systemctl show` |
| HTTP/TCP/DNS | shared OpsForge probe engine |

### systemd service names

Use systemd unit names in `monitoredServices`:

```json
"monitoredServices": [
  "ssh.service",
  "nginx.service",
  "postgresql.service"
]
```

A name without a suffix is normalized to `.service`.

Common systemd states are mapped to the existing OpsForge vocabulary:

- `active` -> `Running`
- `activating` -> `StartPending`
- `deactivating` -> `StopPending`
- `inactive/dead` -> `Stopped`
- `failed` -> `Failed`

This keeps server-side correlation compatible with existing service rules.

## Security

The Linux service unit intentionally runs as a non-login `opsforge` user and enables several systemd sandboxing controls. The agent does not run arbitrary shell remediation commands. Existing command allowlisting remains in place.

For remote OpsForge servers, use HTTPS. Do not enable insecure remote HTTP outside a disposable trusted lab.

## Supported targets

Initial v1.1 target:

- Linux x64
- Linux ARM64
- systemd-based distributions
- .NET 8 runtime, or publish self-contained if desired

Non-systemd Linux hosts still provide CPU, memory, uptime, process, drive, network and probe telemetry; configured services will report that `systemctl` is unavailable.
