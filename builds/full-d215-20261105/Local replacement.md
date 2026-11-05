# Nexus with Jungle Leopard D215

The local Windows replacement candidate is `3.1.8-beta.2-d215.1`. It uses the full Nexus beta service registration, the D215 dashboard, and the native overlay. The `payload` folder is for a deliberate Windows service installation. It contains no imported user state.

The parent commit pins the service, dashboard, overlay, and RGB source revisions. The local Native AOT publish used `-p:_NexusDevVersion=3.1.8-beta.2-d215.1` and the included `Service smoke host.patch` applied from the service checkout with `git apply --unidiff-zero`. The patch gates the staged smoke host on `NEXUS_TEST_HOST=1`. The package is kept locally and its exact file hashes are in `PAYLOAD-SHA256.txt`.

The private `settings-snapshot` folder contains the captured stock preferences, saved profile, device state, widget settings, OpenRGB configuration, and media, plus the accepted D215 landscape panel and transport mapping. The snapshot omits account and pairing credentials. The live Windows installation uses the existing `C:\ProgramData\Nexus` directory, which also retains installed widgets and history. Keep the snapshot out of the program installation folder.

## Before replacing stock

1. Check the package against `PAYLOAD-SHA256.txt`. Close all Nexus instances and verify that the registered `NexusService` is stopped.
2. Make a private rollback copy of `C:\Program Files\Nexus` and `C:\ProgramData\Nexus` while the service is stopped. Keep the stock installer available if the service registration needs to be repaired.
3. Check which controller should own the Lian Li devices. The imported stock preferences have conflict shutdown enabled and do not exempt L-Connect. The full service follows that stock choice.
4. When ready, open an elevated PowerShell in this staging folder and run `& '.\payload\Nexus.exe' --install`. Nexus copies the payload into Program Files, recreates `NexusService`, and starts it. The service reads the live data root, including any settings changed since the snapshot. Set up the D215 in **Devices** and select landscape for the tested orientation. The exact tested playlist is in the snapshot.
5. Check the running service version, stock devices and profiles, and D215 streaming. Verify **Settings → Shut down** releases the overlay and D215. If the replacement fails, stop it and restore the stock application and data from the rollback copies before starting stock again.

On the first installed run, the desktop dashboard and Y70 loaded the HTML shell but received HTTP 404 for the JavaScript. The installed web folder was missing 562 files that were present in the staged package. Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\Repair Nexus Web Files.ps1'` from this staging folder in an administrator PowerShell. The repair copies verified web files into the installed application folder, restarts `NexusService`, and checks that the service serves the dashboard JavaScript. Add `-CheckOnly` to compare files without applying the repair.

The Windows package passed the loopback smoke check. It exposed 48 device handlers, read the copied preferences and D215 landscape record, served 69 dashboard entry assets, and shut down. The check uses a disposable copy of the settings and keeps hardware workers off. A normal installed-service check is still required before relying on the replacement. To rerun the check, use `Verify-StagedNexus.ps1` with PowerShell 7.

Official account sync, cloud relay, OTA, and private vendor firmware are release-only inputs. This candidate keeps the full local application stack and uses its own version.
