# Project Dependency Map

This document catalogs every project included in `Lineup.slnx`. An arrow points from a project to a direct project dependency:

```text
Consumer --> Dependency
```

NuGet packages and framework references are intentionally omitted. Transitive dependencies can be inferred by following arrows through the graph.

`Lineup.Integration.Streaming.Sweep.Tests` references the opt-in `Lineup.Integration.Streaming.Sweep` executable to test its
non-live policies. That project reference builds the executable but never runs it. The executable
and sibling `Lineup.Integration.Streaming.Sweep.FaultSource` remain excluded as solution/CI entry points.
No ordinary test launches Docker, Playwright or a tuner. The executable retains its existing Core,
Lineup Link, SiliconDust guide/physical tuner and Web Player references for matrix generation.

## Production dependency graph

```mermaid
flowchart TB
    subgraph Apps
        Web["Lineup.App.Web"]
        Tui["Lineup.App.Tui"]
    end

    subgraph Core
        Common["Lineup.Core.Common"]
        Ext["Lineup.Core.Extensibility"]
        CoreApp["Lineup.Core"]
        CoreHosting["Lineup.Core.Hosting"]
        FFmpeg["Lineup.Core.Streaming.FFmpeg"]
        VOProtocol["Lineup.Core.VirtualOutputs.Protocol"]
        VOHosting["Lineup.Core.VirtualOutputs.Hosting"]
        VORuntime["Lineup.Core.VirtualOutputs.Runtime"]
    end

    subgraph GuideOutputs
        GuideJson["Lineup.Plugin.GuideOutputs.Json"]
        GuideJsonPlugin["Lineup.Plugin.GuideOutputs.Json.Plugin"]
        GuideXmltv["Lineup.Plugin.GuideOutputs.Xmltv"]
        GuideXmltvPlugin["Lineup.Plugin.GuideOutputs.Xmltv.Plugin"]
    end

    subgraph LineupLink
        Link["Lineup.Plugin.LineupLink"]
        LinkHost["Lineup.Plugin.LineupLink.VirtualOutputHost"]
    end

    subgraph SiliconDust
        HdDevice["Lineup.Plugin.SiliconDust.HDHomeRun.Device"]
        HdApi["Lineup.Plugin.SiliconDust.HDHomeRun.Api"]
        HdPhysical["Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner"]
        HdVirtual["Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner"]
        HdGuide["Lineup.Plugin.SiliconDust.GuideSource"]
        HdDiscoveryRuntime["Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime"]
        HdDiscoveryHost["Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost"]
        HdOutputHost["Lineup.Plugin.SiliconDust.HDHomeRun.VirtualOutputHost"]
    end

    subgraph WebPlayer
        Player["Lineup.Plugin.WebPlayer"]
        PlayerFFmpeg["Lineup.Plugin.WebPlayer.FFmpeg"]
        PlayerHost["Lineup.Plugin.WebPlayer.VirtualOutputHost"]
    end

    Ext --> Common
    CoreApp --> Common
    CoreApp --> Ext
    CoreHosting --> CoreApp
    CoreHosting --> FFmpeg
    FFmpeg --> Ext
    VOProtocol --> Common
    VOHosting --> Common
    VOHosting --> VOProtocol
    VORuntime --> Common
    VORuntime --> CoreApp
    VORuntime --> CoreHosting
    VORuntime --> Ext
    VORuntime --> FFmpeg
    VORuntime --> VOProtocol

    GuideJson --> Common
    GuideJsonPlugin --> Common
    GuideJsonPlugin --> Ext
    GuideJsonPlugin --> GuideJson
    GuideXmltv --> Common
    GuideXmltvPlugin --> Common
    GuideXmltvPlugin --> Ext
    GuideXmltvPlugin --> GuideXmltv

    Link --> Ext
    LinkHost --> VOHosting
    LinkHost --> Link

    HdApi --> HdDevice
    HdPhysical --> Ext
    HdPhysical --> HdDevice
    HdVirtual --> Ext
    HdVirtual --> HdDevice
    HdGuide --> Ext
    HdGuide --> HdApi
    HdGuide --> HdDevice
    HdDiscoveryRuntime --> VORuntime
    HdDiscoveryRuntime --> HdVirtual
    HdDiscoveryHost --> VOHosting
    HdDiscoveryHost --> HdVirtual
    HdOutputHost --> VOHosting
    HdOutputHost --> HdVirtual

    Player --> Ext
    PlayerFFmpeg --> Ext
    PlayerFFmpeg --> FFmpeg
    PlayerFFmpeg --> Player
    PlayerHost --> VOHosting
    PlayerHost --> Player
    PlayerHost --> PlayerFFmpeg

    Web --> Ext
    Web --> CoreApp
    Web --> CoreHosting
    Web --> FFmpeg
    Web --> VORuntime
    Web --> GuideJsonPlugin
    Web --> GuideXmltvPlugin
    Web --> HdGuide
    Web --> Link
    Web --> HdPhysical
    Web --> HdVirtual
    Web --> Player
    Web --> PlayerFFmpeg
    Web --> HdDiscoveryRuntime

    Tui --> Ext
    Tui --> CoreApp
    Tui --> CoreHosting
    Tui --> FFmpeg
    Tui --> VORuntime
    Tui --> GuideJsonPlugin
    Tui --> GuideXmltvPlugin
    Tui --> HdGuide
    Tui --> Link
    Tui --> HdPhysical
    Tui --> HdVirtual
    Tui --> Player
    Tui --> HdDiscoveryRuntime
```

## Production project catalog

### Applications

| Project | Type | Purpose and usage | Direct project dependencies |
|---|---|---|---|
| `Lineup.App.Web` | Web executable | Blazor Server management UI, settings and status pages, guide browsing, Watch presentation, management-hosted virtual-output routes, and HTTP host composition. It statically composes the Core runtime and all currently shipped plugins. | `Lineup.Core.Extensibility`, `Lineup.Core`, `Lineup.Core.Hosting`, `Lineup.Core.Streaming.FFmpeg`, `Lineup.Core.VirtualOutputs.Runtime`, both guide-output plugins, SiliconDust guide/physical/virtual/discovery plugins, `Lineup.Plugin.LineupLink`, `Lineup.Plugin.WebPlayer`, `Lineup.Plugin.WebPlayer.FFmpeg` |
| `Lineup.App.Tui` | Console executable | Spectre.Console frontend for configuring and running the same Core topology, guide, streaming, plugin, and isolated-output infrastructure without the Web presentation layer. | Same host-neutral composition as Web except the TUI does not directly reference `Lineup.Plugin.WebPlayer.FFmpeg` |

### Host-neutral foundations

| Project | Type | Purpose and usage | Direct project dependencies |
|---|---|---|---|
| `Lineup.Core.Common` | Library | Dependency floor containing stable identifiers, configuration records, normalized guide and channel models, streaming DTOs, and serialized metadata shared across Core, plugins, hosts, and workers. | None |
| `Lineup.Core.Extensibility` | Library | Stable plugin boundary: tuner, guide-source, guide-output, virtual-output, stream, settings, status, and host-capability interfaces plus portable validation primitives. | `Lineup.Core.Common` |
| `Lineup.Core` | Library | Provider-neutral application behavior: topology and settings, device and guide orchestration, channel routing, capacity, shared stream multiplexing, transcoding policy, persistence, and status services. | `Lineup.Core.Common`, `Lineup.Core.Extensibility` |
| `Lineup.Core.Hosting` | Library | Trusted hosted-service composition for topology, device refresh, guide work, and shutdown; application adapters for graph leases, settings, cached channel metadata, media probing, and subtitle storage. | `Lineup.Core`, `Lineup.Core.Streaming.FFmpeg` |
| `Lineup.Core.Streaming.FFmpeg` | Library | Portable FFmpeg/FFprobe process control, stream probing, MPEG-TS transforms, immutable-profile planners, synthetic streams, and process error handling. | `Lineup.Core.Extensibility` |
| `Lineup.Core.VirtualOutputs.Protocol` | Library | Versioned, dependency-light local IPC messages and bounded framing shared by the trusted host and isolated output/discovery workers. | `Lineup.Core.Common` |
| `Lineup.Core.VirtualOutputs.Hosting` | Library | Worker-side bootstrap and activation parsing, authenticated capability-broker client, lease renewal, TLS material loading, and fail-closed worker lifecycle. | `Lineup.Core.Common`, `Lineup.Core.VirtualOutputs.Protocol` |
| `Lineup.Core.VirtualOutputs.Runtime` | Library | Trusted-host worker package verification, process activation and supervision, local IPC server, capability brokerage, enrollment, resource limits, restart policy, and runtime status. | `Lineup.Core.Common`, `Lineup.Core`, `Lineup.Core.Hosting`, `Lineup.Core.Extensibility`, `Lineup.Core.Streaming.FFmpeg`, `Lineup.Core.VirtualOutputs.Protocol` |

Portable media projects and output workers have no transitive dependency on Core application services, trusted hosting, EF, or SQLite.
`Lineup.Core.Hosting.Streaming` adapts portable media execution to application graph, settings, guide, and storage services.
There is no FFmpeg-to-Core dependency exception.

### Guide outputs

| Project | Type | Purpose and usage | Direct project dependencies |
|---|---|---|---|
| `Lineup.Plugin.GuideOutputs.Json` | Library | Public versioned JSON guide document contracts plus serialization and deserialization. It can be consumed independently of the Lineup application runtime. | `Lineup.Core.Common` |
| `Lineup.Plugin.GuideOutputs.Json.Plugin` | Library | Guide-output driver and publisher that converts normalized Core guide snapshots into the public JSON document and exposes plugin settings/registration. | `Lineup.Core.Common`, `Lineup.Core.Extensibility`, `Lineup.Plugin.GuideOutputs.Json` |
| `Lineup.Plugin.GuideOutputs.Xmltv` | Library | Public XMLTV serializer for normalized Lineup guide snapshots. It contains the format model rather than host or plugin behavior. | `Lineup.Core.Common` |
| `Lineup.Plugin.GuideOutputs.Xmltv.Plugin` | Library | XMLTV guide-output driver, global-output normalization, file publication, settings, and host registration. | `Lineup.Core.Common`, `Lineup.Core.Extensibility`, `Lineup.Plugin.GuideOutputs.Xmltv` |

### Lineup Link

| Project | Type | Purpose and usage | Direct project dependencies |
|---|---|---|---|
| `Lineup.Plugin.LineupLink` | Library | Complete remote-Lineup plugin family: versioned protocol and connection bundles, grant/token security, remote physical tuner client, virtual output provider, optional guide source, and stream/status contracts. | `Lineup.Core.Extensibility` |
| `Lineup.Plugin.LineupLink.VirtualOutputHost` | Worker executable | Dedicated HTTP/HTTPS process for one Lineup Link virtual output. It authenticates through the worker bootstrap channel and obtains bounded capabilities from the trusted host. | `Lineup.Core.VirtualOutputs.Hosting`, `Lineup.Plugin.LineupLink` |

### SiliconDust / HDHomeRun

| Project | Type | Purpose and usage | Direct project dependencies |
|---|---|---|---|
| `Lineup.Plugin.SiliconDust.HDHomeRun.Device` | Library | Low-level local HDHomeRun support: device identity, UDP discovery, lineup/status HTTP communication, and native tuner/control protocol behavior. | None |
| `Lineup.Plugin.SiliconDust.HDHomeRun.Api` | Library | Client for SiliconDust's remote API, including XMLTV guide retrieval and provider-specific API models. | `Lineup.Plugin.SiliconDust.HDHomeRun.Device` |
| `Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner` | Library | Physical tuner plugin that adapts one or more HDHomeRun devices into generic Lineup physical-device, channel, capacity, and stream-acquisition contracts. | `Lineup.Core.Extensibility`, `Lineup.Plugin.SiliconDust.HDHomeRun.Device` |
| `Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner` | Library | Virtual HDHomeRun output plugin: aggregated identity, tuner capacity, discover/lineup/device responses, stream URL construction, and advertisement snapshots. | `Lineup.Core.Extensibility`, `Lineup.Plugin.SiliconDust.HDHomeRun.Device` |
| `Lineup.Plugin.SiliconDust.GuideSource` | Library | SiliconDust guide-source plugin that combines subscribed physical devices and remote API data into normalized Lineup guide snapshots. | `Lineup.Core.Extensibility`, `Lineup.Plugin.SiliconDust.HDHomeRun.Api`, `Lineup.Plugin.SiliconDust.HDHomeRun.Device` |
| `Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime` | Library | Trusted-host reconciler that converts enabled virtual HDHomeRun topology into bounded advertisements and supervises the shared discovery worker. | `Lineup.Core.VirtualOutputs.Runtime`, `Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner` |
| `Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost` | Worker executable | Shared isolated process that owns UDP 65001 and SSDP 1900 and advertises all eligible virtual HDHomeRun devices from trusted snapshots. | `Lineup.Core.VirtualOutputs.Hosting`, `Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner` |
| `Lineup.Plugin.SiliconDust.HDHomeRun.VirtualOutputHost` | Worker executable | Dedicated HTTP/HTTPS process serving one virtual HDHomeRun's discovery metadata, lineup, device XML, status, and MPEG-TS routes. | `Lineup.Core.VirtualOutputs.Hosting`, `Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner` |

### Web Player

| Project | Type | Purpose and usage | Direct project dependencies |
|---|---|---|---|
| `Lineup.Plugin.WebPlayer` | Library | Built-in Web Player contracts, virtual-output driver, browser/session models, compatibility policy, and shared CMAF output lifecycle abstractions. | `Lineup.Core.Extensibility` |
| `Lineup.Plugin.WebPlayer.FFmpeg` | Library | Portable CMAF generation, track/subtitle planning, HLS/DASH manifest processing, and browser compatibility-test media generation. | `Lineup.Core.Extensibility`, `Lineup.Core.Streaming.FFmpeg`, `Lineup.Plugin.WebPlayer` |
| `Lineup.Plugin.WebPlayer.VirtualOutputHost` | Worker executable | Dedicated typed Web Player runtime sharing CMAF/HLS/DASH producers across independently admitted viewers and obtaining stream capabilities from the trusted host. | `Lineup.Core.VirtualOutputs.Hosting`, `Lineup.Plugin.WebPlayer`, `Lineup.Plugin.WebPlayer.FFmpeg` |

## Test dependency graph

The test graph shows direct project references from each test assembly. Production-to-production dependencies remain in the preceding graph.

```mermaid
flowchart LR
    TuiTests["Lineup.App.Tui.Tests"] --> Tui["Lineup.App.Tui"]
    WebTests["Lineup.App.Web.Tests"] --> Web["Lineup.App.Web"]
    WebTests --> Ext["Lineup.Core.Extensibility"]
    WebTests --> JsonPlugin["Lineup.Plugin.GuideOutputs.Json.Plugin"]

    CoreTests["Lineup.Core.Tests"] --> Ext
    CoreTests --> Core["Lineup.Core"]
    CoreTests --> Json["Lineup.Plugin.GuideOutputs.Json"]
    CoreTests --> JsonPlugin
    CoreTests --> Xmltv["Lineup.Plugin.GuideOutputs.Xmltv"]
    CoreTests --> XmltvPlugin["Lineup.Plugin.GuideOutputs.Xmltv.Plugin"]

    ProtocolTests["Lineup.Core.VirtualOutputs.Protocol.Tests"] --> Protocol["Lineup.Core.VirtualOutputs.Protocol"]
    RuntimeTests["Lineup.Core.VirtualOutputs.Runtime.Tests"] --> WorkerHosting["Lineup.Core.VirtualOutputs.Hosting"]
    RuntimeTests --> Runtime["Lineup.Core.VirtualOutputs.Runtime"]

    JsonTests["Lineup.Plugin.GuideOutputs.Json.Tests"] --> Common["Lineup.Core.Common"]
    JsonTests --> Json
    JsonPluginTests["Lineup.Plugin.GuideOutputs.Json.Plugin.Tests"] --> Ext
    JsonPluginTests --> JsonPlugin
    XmltvTests["Lineup.Plugin.GuideOutputs.Xmltv.Tests"] --> Common
    XmltvTests --> Xmltv
    XmltvPluginTests["Lineup.Plugin.GuideOutputs.Xmltv.Plugin.Tests"] --> Ext
    XmltvPluginTests --> XmltvPlugin

    LinkTests["Lineup.Plugin.LineupLink.Tests"] --> Ext
    LinkTests --> Link["Lineup.Plugin.LineupLink"]

    GuideTests["Lineup.Plugin.SiliconDust.GuideSource.Tests"] --> Ext
    GuideTests --> Core
    GuideTests --> Xmltv
    GuideTests --> HdGuide["Lineup.Plugin.SiliconDust.GuideSource"]
    HdApiTests["Lineup.Plugin.SiliconDust.HDHomeRun.Api.Tests"] --> HdApi["Lineup.Plugin.SiliconDust.HDHomeRun.Api"]
    HdDeviceTests["Lineup.Plugin.SiliconDust.HDHomeRun.Device.Tests"] --> HdDevice["Lineup.Plugin.SiliconDust.HDHomeRun.Device"]
    HdPhysicalTests["Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner.Tests"] --> Ext
    HdPhysicalTests --> HdPhysical["Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner"]
    HdVirtualTests["Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner.Tests"] --> Ext
    HdVirtualTests --> Core
    HdVirtualTests --> HdDevice
    HdVirtualTests --> HdVirtual["Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner"]

    PlayerTests["Lineup.Plugin.WebPlayer.Tests"] --> Ext
    PlayerTests --> Player["Lineup.Plugin.WebPlayer"]
    PlayerTests --> PlayerFFmpeg["Lineup.Plugin.WebPlayer.FFmpeg"]
```

## Test project catalog

| Project | Purpose and coverage | Direct project dependencies |
|---|---|---|
| `Lineup.App.Tui.Tests` | TUI portable-setting behavior, topology editing helpers, output-hosting derivation, and presentation-independent configuration mutations. | `Lineup.App.Tui` |
| `Lineup.App.Web.Tests` | Blazor components, controllers, settings editors, management-hosted routes, status APIs, playback/session behavior, and Web composition. | `Lineup.App.Web`, `Lineup.Core.Extensibility`, `Lineup.Plugin.GuideOutputs.Json.Plugin` |
| `Lineup.Core.Tests` | Core topology, persistence, guide normalization/publication, routing, capacity, stream sharing, policy, and startup schema behavior. | `Lineup.Core.Extensibility`, `Lineup.Core`, both JSON guide projects, both XMLTV guide projects |
| `Lineup.Core.VirtualOutputs.Protocol.Tests` | IPC message framing, versioning, size limits, and protocol serialization. | `Lineup.Core.VirtualOutputs.Protocol` |
| `Lineup.Core.VirtualOutputs.Runtime.Tests` | Worker bootstrap, authenticated IPC, capability brokerage, package validation, supervision, enrollment, leases, and resource policies. | `Lineup.Core.VirtualOutputs.Hosting`, `Lineup.Core.VirtualOutputs.Runtime` |
| `Lineup.Plugin.GuideOutputs.Json.Tests` | Public JSON guide schema and serializer/deserializer behavior. | `Lineup.Core.Common`, `Lineup.Plugin.GuideOutputs.Json` |
| `Lineup.Plugin.GuideOutputs.Json.Plugin.Tests` | JSON guide-output driver configuration and publication behavior. | `Lineup.Core.Extensibility`, `Lineup.Plugin.GuideOutputs.Json.Plugin` |
| `Lineup.Plugin.GuideOutputs.Xmltv.Tests` | XMLTV format serialization and normalized model mapping. | `Lineup.Core.Common`, `Lineup.Plugin.GuideOutputs.Xmltv` |
| `Lineup.Plugin.GuideOutputs.Xmltv.Plugin.Tests` | XMLTV plugin configuration, normalization, publication, and path behavior. | `Lineup.Core.Extensibility`, `Lineup.Plugin.GuideOutputs.Xmltv.Plugin` |
| `Lineup.Plugin.LineupLink.Tests` | Protocol clients/contracts, bundle validation, grants, authentication, remote physical/virtual behavior, guide transfer, and worker-facing routes. | `Lineup.Core.Extensibility`, `Lineup.Plugin.LineupLink` |
| `Lineup.Plugin.SiliconDust.GuideSource.Tests` | Multi-device guide acquisition, normalization, subscriptions, and XMLTV interoperability. | `Lineup.Core.Extensibility`, `Lineup.Core`, `Lineup.Plugin.GuideOutputs.Xmltv`, `Lineup.Plugin.SiliconDust.GuideSource` |
| `Lineup.Plugin.SiliconDust.HDHomeRun.Api.Tests` | Remote SiliconDust API request, response, and XMLTV parsing behavior. | `Lineup.Plugin.SiliconDust.HDHomeRun.Api` |
| `Lineup.Plugin.SiliconDust.HDHomeRun.Device.Tests` | Low-level discovery, identity, lineup, and native device protocol behavior. | `Lineup.Plugin.SiliconDust.HDHomeRun.Device` |
| `Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner.Tests` | Physical HDHomeRun adapter settings, channels, capacity, and stream acquisition. | `Lineup.Core.Extensibility`, `Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner` |
| `Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner.Tests` | Virtual HDHomeRun profile, aggregation, capacity, lineup, routing, and advertisement behavior. | `Lineup.Core.Extensibility`, `Lineup.Core`, `Lineup.Plugin.SiliconDust.HDHomeRun.Device`, `Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner` |
| `Lineup.Plugin.WebPlayer.Tests` | Web Player contracts, session lifecycle, FFmpeg plans, CMAF/HLS/DASH output, subtitles, and compatibility media. | `Lineup.Core.Extensibility`, `Lineup.Plugin.WebPlayer`, `Lineup.Plugin.WebPlayer.FFmpeg` |

## Dependency rules at a glance

- `Lineup.Core.Common` is the dependency floor and must remain free of project and package dependencies.
- `Lineup.Core.Extensibility` builds only on `Lineup.Core.Common`; plugins depend on these stable contracts rather than on application hosts.
- `Lineup.Core` owns provider-neutral behavior. Web and TUI compose it but should not duplicate its streaming or topology logic.
- Format libraries such as `Lineup.Plugin.GuideOutputs.Json` and `Lineup.Plugin.GuideOutputs.Xmltv` remain separate from their plugin adapters so their document contracts can be reused independently.
- Isolated workers depend on `Lineup.Core.VirtualOutputs.Hosting` and their own plugin family, not on Web, TUI, or the full trusted runtime.
- `Lineup.Core.VirtualOutputs.Runtime` stays in trusted hosts and never runs inside an isolated worker.
- Test projects mirror the production hierarchy and may reference extra format/plugin projects only when needed for integration coverage.
