# Lineup.Core.Extensibility

`Lineup.Core.Extensibility` is the stable compile-time boundary between the Lineup host and independently composable plugins.

It contains plugin-facing interfaces, portable validation behavior, and narrow host capabilities. Shared identifiers, configuration records, normalized guide models, and streaming DTOs live in `Lineup.Core.Common`.

`Lineup.Core.Extensibility` references only `Lineup.Core.Common` and does not contain persistence, catalogs, orchestration, settings services, or presentation behavior.

Production plugins may reference this project and designated support libraries in the same provider family, but must not reference `Lineup.Core`.
