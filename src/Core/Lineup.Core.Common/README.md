# Lineup.Core.Common

`Lineup.Core.Common` is the dependency-free shared data model understood by Lineup Core, plugins, and consumer-facing serialization libraries.

It contains stable identifiers, configuration records, normalized guide models, streaming DTOs, adapter-setting data, and XMLTV metadata records under `Lineup.Core.Common.*` namespaces. It does not contain plugin interfaces, dependency injection, persistence, networking, parsing, validation, or host behavior.

Plugins normally reference `Lineup.Core.Extensibility`, which references this project. Consumers that only need published guide data can reference `Lineup.Core.Common` with the relevant `Lineup.Plugin.GuideOutputs.*` format library without taking a dependency on plugin implementation assemblies.
