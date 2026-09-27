# Windows PC C/C++ toolchain

StudioX bundles selected, unmodified files from the locally installed
`x86_64-13.1.0-release-win32-seh-msvcrt-rt_v11-rev1` MinGW distribution.
This is the Windows native compiler for LVGL PC preview; it does not replace
the MCU cross compilers.

The original `gcc/build-info.txt` records the distribution build configuration,
component source URLs and patches. `provenance.json` records the verified
compiler target/version, selected source fingerprint, PE imports and independent
C/C++ runtime checks. `toolset.json` hashes the entire included file tree.

## Components and notices

- GCC 13.1.0 and GNU binutils: see `gcc/licenses/gcc/` and
  `gcc/licenses/binutils/`, including `COPYING3` and GCC `COPYING.RUNTIME`.
- MinGW-w64 runtime 11 and Windows headers/import libraries: see
  `gcc/licenses/mingw-w64/COPYING.MinGW-w64.txt`,
  `COPYING.MinGW-w64-runtime.txt` and the other notices in that directory.
- Runtime/compiler dependencies retain the original distribution license tree,
  including winpthreads, GMP, MPFR, MPC, ISL, libiconv and zlib notices.

Original license texts are retained without editing. The preserved distribution
license tree also contains notices for some components whose binaries are
excluded from the smaller StudioX bundle.

## Upstream and corresponding source

- [Exact binary release](https://github.com/niXman/mingw-builds-binaries/releases/tag/13.1.0-rt_v11-rev1)
- [Distribution build scripts and patches](https://github.com/niXman/mingw-builds)
- [GCC 13.1.0 source](https://ftp.gnu.org/gnu/gcc/gcc-13.1.0/)
- [MinGW-w64 source](https://github.com/mingw-w64/mingw-w64/tree/v11.0.0)
- [GNU binutils source](https://www.gnu.org/software/binutils/)
- [GCC license and distribution terms](https://gcc.gnu.org/onlinedocs/gcc/Copying.html)
- [GCC Runtime Library Exception explanation](https://www.gnu.org/licenses/gcc-exception-3.1-faq.en.html)

The Runtime Library Exception covers eligible compiled programs; it does not
remove the corresponding source obligations for distributing GCC or its runtime
libraries as independent binaries. A public distributor must provide the
matching corresponding source, build scripts and applicable modifications using
a method permitted by the relevant license. Upstream URLs and the local binary
inventory alone are not a source offer. StudioX does not claim authorship of
these third-party components.

This preparation script reads an explicitly supplied local distribution. It does
not download SDKs or additional compilers. CMake/Ninja are reused from StudioX's
existing verified build tools and are not duplicated into this package.
