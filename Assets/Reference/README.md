# Versioned clean GameDB references

The verified October FH6 update is also embedded as `gamedbRC.stock.661353983.sqlite.gz`: 682 drivable models, build stamp `661353983`. It matches the previously inspected official update across all 207 tables. Garage Viewer defaults explicitly to this catalog; version stamps must never be sorted to guess the newest build. The original 671-model reference remains intact for older modded DB comparisons and update merges.

Keep the original `gamedbRC.stock.sqlite.gz` when adding a future update's clean baseline. The current verified reference has `VersionInfo.database_version = -1107070983`. This is an opaque database-build identifier, not the app's version or a sortable game version.

For a future verified, unmodified full GameDB, add a gzip-compressed SQLite file named `gamedbRC.stock.<build-label>.sqlite.gz` in this folder. The project embeds these files automatically. Do not include modified databases, single-car exports, duplicate references with the same stamp, or fabricated future versions. The catalog selects by the stamp inside the database, not the filename. More than two historical versions can be retained.

`Merge updated DB` must use the old clean reference matching the **modded input**. The updated top input is the clean destination; it need not already have an embedded reference. Missing/ambiguous matches require the user to supply the old clean `.slt` or `.sqlite`. The external reference must really be unmodified: matching `VersionInfo` alone cannot establish that.

Car Editor selects a matched embedded reference on every load. An unknown or ambiguous stamp disables stock comparison/restore and reference-dependent operations; ordinary editing/export remains available. Embed the newly verified reference to enable these features for the new game version.

The merged output keeps the new clean DB's version stamp. Consequently its next update merge uses that version's clean baseline, not the earlier one. Keep original clean databases and modded backups independently of the embedded references.
