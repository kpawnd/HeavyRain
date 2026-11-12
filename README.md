# Heavy Rain

A Windows app for checking Deep Freeze status and choosing whether a PC boots frozen or thawed. Built with C#, WPF and MahApps.Metro.

Made by [@kpawnd](https://github.com/kpawnd).

## Features

- Frozen, thawed and unknown status, refreshed every 30 seconds.
- Freeze or thaw on the next boot, with an option to restart immediately.
- Local Cloud policy name and Deep Freeze version information.
- Diagnostic reports you can save as JSON.
- A dark purple Metro interface with explanations when an action can't run.
- A six-second startup screen with live installation, policy and status checks.
- An original synth startup chime, embedded in the EXE.

## Using it

Copy `HeavyRain.exe` wherever you want to run it. The portable build compresses its bundled DLLs and .NET runtime; there is nothing else to install for Heavy Rain itself.

Launch it and approve the Windows administrator prompt. Checks start during the credit animation, then the log shows the loaded libraries, searched locations and query results. Heavy Rain looks for `DFC.exe` automatically. If it can't find it, open **installation** and use **Locate DFC.exe** to select the copy supplied with your Deep Freeze installation.

Enter a Deep Freeze password with command-line rights, then choose **Freeze PC** or **Thaw PC**. By default, the change applies on the next restart. Tick **Restart immediately after confirmation** if you want to restart now.

Launch with `HeavyRain.exe --silent` to mute startup and error sounds. Sound playback doesn't delay the checks. Error dialogs shake briefly after appearing, even when Windows animations are disabled. Closing a dialog stops its sound and movement.

Save your work first. Restarting a frozen computer can discard changes on protected drives.

You need Deep Freeze and a compatible `DFC.exe` installed. The Cloud agent alone isn't enough. A Windows password or Deep Freeze one-time password won't work for these commands.

## Building

Requires Windows and the .NET 10 SDK.

```powershell
dotnet build HeavyRain.sln -c Release
dotnet run --project tests/HeavyRain.Tests -c Release
```

To build a standalone Windows x64 executable:

```powershell
dotnet publish src/HeavyRain/HeavyRain.csproj -c Release -o publish
```

Use `publish/HeavyRain.exe` for distribution. The normal `bin` build folder contains separate DLLs; the published EXE bundles them. Native libraries unpack into the .NET temporary cache when needed.

Compression reduces the download size but adds some startup work and memory use. For an uncompressed single EXE with the same dependencies, add `-p:EnableCompressionInSingleFile=false` to the publish command.

Run `HeavyRain.exe --demo` for a preview with sample data. Demo mode still asks for administrator privileges, but doesn't send commands to Deep Freeze.

## A few things to know

The next-boot panel records requests made during the current session. It doesn't read back Deep Freeze's scheduled boot mode. Check the reported protection state after restarting.

Policy detection first reads the Cloud agent's registered storage path and its cached current policy in `Tasks.dat`. It uses the root policy name, not a product-specific name or queued task. This location was verified on a Cloud-managed Windows VM. Faronics registry fields and the local Cloud window are fallbacks. Hover over the policy name to see its source. The Cloud console may have newer settings than the local cache.

If the policy stays unavailable, open **activity** and use **Export diagnostics**. The report includes checked locations, recognized names and read errors. It doesn't include the raw cached policy, passwords or tokens. Detection opens the database and registry read-only.

Protection and policy refresh every 30 seconds. Installation discovery and version queries run every five minutes, or when you click **Refresh**. Automatic polling pauses while minimized and resumes when restored.

Passwords aren't saved or included in reports. DFC receives the password as a process argument, so administrator tools or Windows command-line auditing may capture it.

Deep Freeze integration still needs testing against the installed agent. The included tests use simulated responses and don't change a computer's protection.

Heavy Rain is an independent project. Faronics binaries aren't included. See the [DFC documentation](https://docs.faronics.com/deep-freeze-cloud/using-deep-freeze-cloud-console/deep-freeze-service/advanced-settings-tab/deep-freeze-command-line-syntax) and [third-party notices](THIRD-PARTY-NOTICES.txt).

The purple dragon is a recoloring of the older [LLVM dragon](https://llvm.org/Logo.html), owned by Apple. Heavy Rain isn't affiliated with LLVM or Apple.
