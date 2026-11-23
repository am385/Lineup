# Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner

Virtual HDHomeRun identities, manual setup URLs and shared UDP/SSDP discovery.

Network discovery is disabled by default to avoid UDP port conflicts. To enable it, turn on **Enable UDP and SSDP network discovery**. A management-hosted
virtual HDHomeRun requires an absolute HTTP or HTTPS **Advertised base URL** reachable by media servers. A dedicated listener advertises its effective
**Client-facing base URL**, which Lineup derives from transport, bind address, listener port, and optional published-port mapping unless an advanced manual
override is configured. Request-derived and container-internal addresses are not substituted automatically.

The first enabled management-hosted virtual HDHomeRun owns the management listener's root protocol URLs. Other management-hosted devices
advertise `hdhomerun/{DeviceID}/` beneath their configured public root, so UDP discovery, SSDP device descriptions, and HTTP lineups identify
the same device. Dedicated outputs advertise their own listener base URLs and do not change which management-hosted device owns the root.

Jellyfin commonly identifies auto-discovered tuners by the source network address. Advertising several virtual devices from one process may therefore
require a distinct host IP for each advertised device; otherwise add the displayed tuner URLs manually. Docker bridge networks frequently do not pass
UDP broadcast or multicast traffic. Use host networking where the platform supports it, or use manual setup.

SSDP uses a shared IPv4 binding on UDP port 1900 and joins the default interface's SSDP multicast group, permitting coexistence with other shared
listeners such as the Windows **SSDP Discovery** service (`SSDPSRV`); disabling that service is not required by Lineup's policy. On Windows,
multicast loopback is enabled so clients on the same machine can discover Lineup. Notifications, responses, malformed searches, and unsupported
search targets are ignored before consuming the response-rate budget. Shared binding does not guarantee delivery of unicast searches to every
listener, and a successful bind alone does not certify remote-LAN delivery, firewall rules, or all network interfaces. Use multicast discovery or
the manual profile URLs shown in Settings. Startup logs identify the bound SSDP socket and multicast-loopback policy; socket failures identify
UDP port 1900, the socket error, and relevant ownership, permissions, or multicast-interface checks.

An SSDP socket failure during binding, multicast setup, notification delivery, or receive processing disables SSDP for that discovery-worker
generation, reports degraded readiness, and logs the unavailable protocol. SiliconDust UDP discovery and control-plane lease renewal continue.
If UDP discovery or the lease ends, the worker cancels and cleans up all remaining discovery tasks. SSDP receive and alive-notification loops
also cancel and observe one another before their shared socket is disposed.

## Related documentation

[Project overview](../../../../README.md) | [Contributor standards](../../../../CONTRIBUTING.md)
