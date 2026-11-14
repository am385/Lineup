# GitHub Copilot Instructions

Read and follow [`CONTRIBUTING.md`](../CONTRIBUTING.md) before making code changes. It is the canonical source for Lineup's coding, testing, formatting, and documentation standards.

Before reporting completion, run the validation gate defined in [`AGENTS.md`](../AGENTS.md). Use the smallest relevant tests during development, but run the complete gate for the final code change.

For channel sweeps, streaming matrix validation, live tuner checks, or sweep report triage, follow the repository skill at [`skills/lineup-streaming-sweep/SKILL.md`](skills/lineup-streaming-sweep/SKILL.md). The hardware sweep must remain manually invoked and excluded from `Lineup.slnx` and CI/CD.
