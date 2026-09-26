# Pack retention validation

Offline, small StudioX format 1 fixtures exercise the real package import, catalog,
cleanup, bundled import and project metadata APIs. No tool downloads, firmware builds
or hardware connections are used. Windows junction fixtures are removed as links,
and their targets remain for inspection.

```powershell
dotnet run --project tools/StudioX.PackRetentionValidation -- artifacts/validation/pack-retention-new
```

The output directory must not already exist. Results are written to `result.txt`.

Cleanup is a separate explicit operation accepting an absolute installed pack root:

```powershell
dotnet run --project tools/StudioX.PackRetentionValidation -- --prune %LOCALAPPDATA%/MCUStudioX/packs
```

It prints each removed ID/version, replacement version and reclaimed bytes. Failures
retain their original diagnostics and produce a nonzero exit code. Validation does
not invoke this mode on the user's package library.
