## What changed and why

<!-- One topic per pull request. -->

## Evidence

<!-- For a finding or a protocol change: the game.elf address(es) or the test run (date, settings, log lines, report zip)
     that support it. Mark hypotheses as hypotheses. -->

## Checklist

- [ ] No game files, keys, manifests, digests or unredacted logs are included
- [ ] `dotnet test src/ps3/Sanctuary.Ps3.slnx` passes
- [ ] If what the server sends changed on purpose, the affected `*Vectors.g.cs` are updated and the reason is above
- [ ] New addresses are in `docs/re/addresses.csv`
- [ ] Docs separate observed facts from hypotheses
