# DB Browser

This is a native WPF database workspace inspired by DB Browser for SQLite 3.13.1, using the tool's dark/pink theme. It is an independent implementation, not an embedded copy of the Qt application.

## Main workflow

- Open an FH6 SLT or SQLite database, or start a new empty database. Every session uses an independent private working copy. New Motorsport additions elsewhere in the tool are not changed; unsupported Motorsport SLTs retain the existing bridge restriction.
- Database Structure lists tables, views, indexes and triggers, their columns and creation SQL. Create tables/indexes through dialogs; modify table names/columns or edit view/index/trigger definitions through SQL. Delete objects with confirmation.
- Browse Data has a table selector, sortable headers, per-column filters, global text search and page navigation. Column filters accept literal text, `=exact`, numerical `>`, `<`, `>=`, `<=`, and `NULL` / `NOT NULL`.
- Edit cells directly in the grid with a double-click, F2 or typing. Enter, Tab or clicking away saves the value to the private working copy; Esc cancels the current edit. Existing storage types are preserved; a NULL cell uses the declared column type when filled. Numeric edits accept dot or comma. Invalid values/constraint failures keep the editor open and leave database values intact. Refresh reapplies filters/sorting after edits.
- The right-hand editor remains available for long text, explicit type changes, NULL, and hexadecimal BLOB values. Use Apply cell for changes made in that panel only. New records use defaults for omitted fields. Views/generated columns and tables without a safe row identity are read-only in the grid.
- Execute SQL supports queries, modification scripts, selected-text execution, F5, Ctrl+Enter, opening/saving SQL files, and an SQL log. SQL numeric literals use dots. Commands are atomic and failed/cancelled scripts roll back together. Results display up to 1,000 rows; SQL execution has a 15-second limit.
- Export CSV writes **all records** of the selected table, not only the filtered page; generated columns are omitted. Text is quoted, SQL NULL is an unquoted empty field, and blobs are hex. Import CSV appends records to an existing table with matching headers; constraints or invalid input roll back the whole import. Imports accept comma-delimited RFC-style quoted fields and are limited to 64 MB / 100,000 records. CSV is not a full-fidelity backup of arbitrary SQLite storage classes; use SQLite or SQL dumps for that.
- Import/export SQL dumps covers schema and records, including indexes, triggers and AUTOINCREMENT counters. Common external transaction wrappers are removed because the runner supplies its own atomic operation. Imports are limited to 64 MB and the SQL execution limit. Virtual-table dumps are not supported.
- Edit Pragmas shows database properties. Only user_version and application_id are editable; storage and safety settings remain read-only.
- Write changes creates a new undo checkpoint in the working copy, not the input. Revert changes restores the previous checkpoint. Export as creates a new validated SQLite or SLT file; SQLite-to-SLT export requires an original encrypted template. Existing files are never overwritten.

## Safeguards and differences

Foreign-key enforcement remains off for the GameDB modding workflow; row deletion does not automatically delete related records. Use `PRAGMA foreign_key_check;` for diagnostics. The SQL authorizer blocks ATTACH/DETACH, extension/file access, explicit transaction commands, temporary/virtual-table creation, and unsafe PRAGMAs. SQLite integrity is checked during export, but in-game compatibility still requires testing.

This implements the main local editing/import/export workflow, not every feature of the upstream application: SQLCipher encryption, remote databases, plotting, syntax highlighting/autocompletion and multiple SQL-editor tabs are not included.

Changes in this tab are not shared automatically with Car Editor or Crypto. Export the edited database and reload that output in another tab when needed.
