# OpsForge v1.1.0 — Linux Agent Upgrade

This overlay upgrades the existing OpsForge v1.0.0 agent to first-class Windows + Linux telemetry.

## What changes

- Linux CPU utilization from `/proc/stat`
- Linux memory utilization from `/proc/meminfo`
- Linux host uptime from `/proc/uptime`
- systemd service monitoring using `systemctl show`
- Existing process, drive, network, HTTP, TCP and DNS probes remain cross-platform
- Existing authentication, enrollment, mTLS, OpenTelemetry and heartbeat contracts are unchanged
- Existing Windows metrics and ServiceController monitoring remain intact
- Adds Linux publish/install scripts and a hardened systemd unit

## Apply to a clean/current OpsForge checkout

From the root of your OpsForge repository:

```bash
git checkout main
git pull
git checkout -b feature/v1.1-linux-agent
```

Copy these overlay files into the repository, preserving paths:

```text
OpsForge.Agent/SystemMetricsCollector.cs
OpsForge.Agent/appsettings.linux-example.json
linux/opsforge-agent.service
linux/install-agent.sh
linux/publish-agent.sh
docs/LINUX-AGENT.md
```

Then apply:

```bash
git apply v1.1.0.patch
```

If `Directory.Build.props` does not contain a central `<Version>1.0.0</Version>`, edit the actual version location manually to `1.1.0` and apply the remaining hunks.

## Build validation

Windows:

```powershell
dotnet restore .\OpsForge.sln
dotnet build .\OpsForge.sln --configuration Release --no-restore
dotnet run --project .\OpsForge.CorrelationChecks\OpsForge.CorrelationChecks.csproj --configuration Release
.\Test-OpsForge.ps1
```

Linux:

```bash
dotnet restore OpsForge.sln
dotnet build OpsForge.sln -c Release --no-restore
bash linux/publish-agent.sh linux-x64
```

For ARM64:

```bash
bash linux/publish-agent.sh linux-arm64
```

## Linux deployment

Edit `OpsForge.Agent/appsettings.linux-example.json` first. In particular:

- `serverUrl`
- `agentId`
- `site`
- `environmentName`
- `monitoredProcesses`
- `monitoredServices`
- health checks

Publish:

```bash
bash linux/publish-agent.sh linux-x64
```

Install:

```bash
sudo bash linux/install-agent.sh publish/linux-x64
```

Set the one-time enrollment token through your preferred secure mechanism, or enroll/configure the agent before enabling the unit.

Start:

```bash
sudo systemctl start opsforge-agent
sudo systemctl status opsforge-agent
```

Logs:

```bash
journalctl -u opsforge-agent -f
```

## GitHub update

After the Windows and Linux build checks pass:

```bash
git status
git add OpsForge.Agent/SystemMetricsCollector.cs \
        OpsForge.Agent/Program.cs \
        OpsForge.Agent/OpsForge.Agent.csproj \
        OpsForge.Agent/appsettings.linux-example.json \
        linux/ \
        docs/LINUX-AGENT.md \
        README.md \
        CHANGELOG.md \
        Directory.Build.props

git commit -m "Build OpsForge v1.1.0 cross-platform Linux agent support"
git push -u origin feature/v1.1-linux-agent
```

Create a PR from `feature/v1.1-linux-agent` to `main`.

Suggested PR title:

```text
OpsForge v1.1.0: Linux agent support and systemd monitoring
```

After CI passes and the PR is merged:

```bash
git checkout main
git pull
git tag -a v1.1.0 -m "OpsForge v1.1.0 - Linux agent support"
git push origin v1.1.0
```

Create a GitHub Release from tag `v1.1.0`.

## Suggested release summary

OpsForge v1.1.0 expands the authenticated monitoring agent beyond Windows with native Linux CPU, memory, uptime, process, drive, network, probe and systemd-service telemetry. It keeps the existing heartbeat, correlation, mTLS, OpenTelemetry and evidence-based recovery model unchanged, so mixed Windows/Linux fleets can participate in the same incident workflow.
