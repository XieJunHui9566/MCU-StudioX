# Git for Windows

MCU StudioX bundles Portable Git for Windows 2.55.0.windows.5 in `runtime/git`, with upstream executables unchanged.
The original `LICENSE.txt`, component licenses and distribution files are retained. The official post-install preparation is run before packaging; copies of the build machine's hosts, protocols, services and networks files are omitted.
Git is licensed under GPL version 2; bundled components carry their respective licenses.

- Project: https://gitforwindows.org/
- Release and archive checksums: https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.5
- Git source: https://github.com/git-for-windows/git/tree/v2.55.0.windows.5
- Distribution build scripts and component sources: https://github.com/git-for-windows/build-extra and https://github.com/git-for-windows/MSYS2-packages
- Archive SHA-256: `5aa8a20f6e9abb2c755f0e73c91c687701a46b309ad84a0ca6509380fa4ae290`

The exact release URL, version and hash are also shipped in `runtime/git/studiox-provenance.json`.
