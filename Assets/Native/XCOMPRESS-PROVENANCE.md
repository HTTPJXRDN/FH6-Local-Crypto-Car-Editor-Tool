# Local LZX runtime

This private working build uses the existing `xcompress64.dll` from the user's local ForzaTechStudio 0.9.2.2 tools (`ForzaTechStudio/Native/xcompress64.dll`). It provides Microsoft's XMem LZX compression/decompression API for ZIP method 21. No DLL was downloaded for this change. The app's adapter and ZIP workspace code are independent implementations; ForzaTechStudio source was not copied.

The upstream source repository's MIT license is not a representation that this Microsoft native binary is MIT-licensed. Review the native runtime's redistribution rights before distributing a public package. Do not remove this notice from private test builds.
