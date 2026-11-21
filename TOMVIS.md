# Tomvis/Radarr fork

Branch `enhanced` = an upstream release tag (`v*`) + fork commits. The image (`docker/Dockerfile.tomvis`) overlays the fork's backend on linuxserver's image of the same version; the UI is upstream's. forksync (.120) merges new tags nightly; see homelab-stacks `forks/forks.toml` unit `radarr`.

## Dubcheck (MEDIA-39)

Dub-profile movies (Hebrew, Russian, ...) prefer releases with embedded dub audio. Radarr only sees the release title, so the fork asks the dubcheck sidecar (Tomvis/dubcheck), which also reads the indexer description, the .torrent file list and the tracker page.

- `ReleaseInfo.Description` (indexer `<description>`, max 4000 chars) and `ReleaseInfo.IndexerAttributes` (every `torznab:attr`/`newznab:attr`, first value per name). Upstream drops both. RuTracker's description lists every audio track.
- `CustomFormatInput.Release`: the release being scored (null for files, history, blocklist).
- Custom format condition **Dubcheck** (`DubcheckSpecification`): fields Language + Verdict (Has/Lacks). Matches when dubcheck's verdict for (release, language) equals Verdict; never for files. Scored by Recyclarr local CFs (`dub-*-dubcheck-{has,lacks}`).
- `DubcheckClient`: `$DUBCHECK_URL` (unset = off). Calls only for movies whose profile gives a Dubcheck CF a non-zero score, and only for those CFs' languages. 10 s timeout per check, 1 h in-memory cache per (guid, language), breaker opens 5 min after 3 consecutive failures. Every failure = Unknown = no match (fail-open).
- `DownloadDecisionMaker`: one `/v1/prefetch` per search (all releases, 45 s budget) before the per-release loop, so the CF answers from dubcheck's cache. RSS has no prefetch; per-release checks there only happen for dub-profile movies.

Touch points (merge conflicts land here): `ReleaseInfo.cs`, `RssParser.cs`, `TorznabRssParser.cs`, `NewznabRssParser.cs`, `CustomFormatInput.cs`, `CustomFormatCalculationService.cs`, `DownloadDecisionMaker.cs`. New: `Dubcheck/`, `CustomFormats/Specifications/DubcheckSpecification.cs`, tests in `NzbDrone.Core.Test/DubcheckTests/` + `TorznabFixture.should_keep_description_and_attrs`.

Known upstream test failure (clean v7.0.3.10918 too): `DownloadDecisionMakerFixture.broken_report_shouldnt_blowup_the_process`.
