# Optional Motorsport LZX runtime

This public package does **not** redistribute xcompress64.dll. The adapter uses Microsoft's XMem LZX API; the locally tested binary came from the user's existing ForzaTechStudio tools. That project's MIT source license does not establish redistribution rights for this native Microsoft binary.

For supported Motorsport method-21 camera/car ZIP rebuilding and track extraction, supply your own legally obtained **x64 xcompress64.dll** and keep it in the same directory as FH6ModStudio.exe. Do not substitute a 32-bit DLL or an untrusted download. Confirm that you may use/distribute your copy before including it in a shared package.

Without this optional dependency, LZX operations fail with a setup message. FH6 ZIPs, database/asset crypto, DB Browser, Car Editor and existing animation helpers do not require it.

## Download/setup reference

Our local verification used the DLL supplied with **ForzaTechStudio V0.9.2.2**. Start at the upstream [ForzaTechStudio releases page](https://github.com/D3FEKT/ForzaTechStudio/releases/tag/V0.9.2.2), download its official release package, and locate `xcompress64.dll` in the extracted files. Its [method-21 documentation](https://github.com/D3FEKT/ForzaTechStudio/blob/Main/docs/create-zip.md) confirms that the tool ships this runtime. If you are permitted to use that copy, place only the x64 DLL beside `FH6ModStudio.exe`; you do not need to run ForzaTechStudio for this tool's LZX operations.

This link identifies the tested upstream distribution, not a grant of Microsoft DLL redistribution rights. Do not use third-party DLL download sites or assume that every similarly named binary is compatible.

For a source build, place the DLL beside the built EXE yourself. The public project deliberately does not copy/bundle the DLL. The private development build retains its existing local copy.
