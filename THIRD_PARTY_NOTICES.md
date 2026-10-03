# Third-party credits and notices

## Redistributed components
- Microsoft .NET / WPF: .NET Foundation and contributors, MIT; accompanying LICENSE-DOTNET/WPF and runtime THIRD-PARTY-NOTICES texts.
- Microsoft.Data.Sqlite 8.0.8: Microsoft / .NET Foundation, MIT; LICENSE-Microsoft.Data.Sqlite.txt.
- SQLitePCLRaw 2.1.6: Eric Sink / SourceGear, LLC, Apache-2.0; LICENSE-SQLitePCLRaw.txt. Upstream assemblies are unmodified.
- SQLite: its authors, public domain; SQLITE-PUBLIC-DOMAIN.txt.
- ACL, RTM and sjson-cpp: Nicholas Frechette and contributors, MIT; LICENSE-ACL/RTM/SJSON.txt. Used by the included animation helpers.
- Obfuscar 2.2.49: Obfuscar contributors, MIT; LICENSE-Obfuscar.txt. Used for obfuscation/string hiding, including generated string-lookup code. The build tool is not bundled.

## Reference-only projects
- DB Browser for SQLite 3.13.1, authors/contributors: https://github.com/sqlitebrowser/sqlitebrowser . UI/workflow reference only. Our implementation is independent WPF code: no Qt/SQLiteBrowser implementation, SQLCipher or sqlean binary is bundled. Upstream is dual MPL-2.0 / GPL-3.0-or-later. Reference license texts are in source uploads only; they do not relicense independently written application code.
- ForzaTechStudio, Nenkai / D3FEKT and contributors: https://github.com/D3FEKT/ForzaTechStudio . Method-21 ZIP format/API reference. Our archive reader/writer and XMem adapter are independent. Its MIT notice is in source only.
- Microsoft XMem / LZX runtime: xcompress64.dll is NOT redistributed. Its redistribution rights are unresolved; see NATIVE_RUNTIME.md. ForzaTechStudio's MIT source license does not license the native Microsoft binary.
- Botan / Draff's original research: earlier crypto/reference work. This release uses managed .NET cryptography, not a bundled Botan binary.

## Project credits
JXRDN: creator, UI/design, research and in-game testing. Draff: original crypto/reference and save-tool guidance. Smidge: upgrade database examples. Stalin: text-asset round-trip corrections. Codex: development/testing assistance.

