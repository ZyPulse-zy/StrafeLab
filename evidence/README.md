# Verification evidence

These files are measured hardware/runtime evidence. Device paths, serials, player identities, GSI tokens, browser history and non-WASD key reports are omitted.

- `hid-inventory.json`: all 7 collections, capabilities, report IDs and reconstructed descriptors.
- `hall-wasd-raw.csv`: controlled slow-press capture with the official debug bit enabled; only A/D/W/S A0 payloads.
- `hall-csharp-wasd.csv`: 151 samples captured by the independent C# implementation.
- `hall-verification.json`: ranges, distinct values and restoration outcomes.
- `runtime-verification.json`: final portable startup and real Demo parser checks.

Timestamps are relative to each capture, not comparable across files. See RESEARCH.md and VERIFICATION.md in the parent folder for interpretation and limits.
