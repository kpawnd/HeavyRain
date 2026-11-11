# Heavy Rain

A Windows app for checking Deep Freeze status and choosing whether a PC boots frozen or thawed. Built with C#, WPF and MahApps.Metro.

Made by [@kpawnd](https://github.com/kpawnd).

## Features

- Frozen, thawed and unknown status, refreshed every 30 seconds.
- Freeze or thaw on the next boot, with an option to restart immediately.
- Local Cloud policy name and Deep Freeze version information.
- Diagnostic reports you can save as JSON.
- A dark purple Metro interface with explanations when an action can't run.
- A 6.5-second startup screen with live installation, policy and status checks.

## Using it

Copy `HeavyRain.exe` wherever you want to run it. The portable build includes its DLLs and the .NET runtime; there is nothing else to install for Heavy Rain itself.

Launch it and approve the Windows administrator prompt. After the credit fades out, the startup log shows which libraries are loaded and the results of the local checks. Heavy Rain looks for `DFC.exe` automatically. If it can't find it, use **Locate DFC.exe** to select the copy supplied with your Deep Freeze installation.

Enter a Deep Freeze password with command-line rights, then choose **Freeze PC** or **Thaw PC**. By default, the change applies on the next restart. Tick **Restart immediately after confirmation** if you want to restart now.

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

Run `HeavyRain.exe --demo` for a preview with sample data. Demo mode still asks for administrator privileges, but doesn't send commands to Deep Freeze.

## A few things to know

The next-boot panel records requests made during the current session. It doesn't read back Deep Freeze's scheduled boot mode. Check the reported protection state after restarting.

Policy detection searches Faronics registry keys in both 32-bit and 64-bit views, then falls back to the local Cloud window if it can't find an unambiguous name. Hover over the policy name to see its source. Local metadata can be out of date, and local changes may need to be reconciled with your Cloud policy.

If the policy stays unavailable, use **Export diagnostics**. The report includes the registry paths checked, policy field names and types, recognized name candidates, and any scan errors or limits. Unrecognized fields are listed without their contents; passwords, tokens and policy IDs aren't read as policy names. Detection is read-only and doesn't modify the registry.

Passwords aren't saved or included in reports. DFC receives the password as a process argument, so administrator tools or Windows command-line auditing may capture it.

Deep Freeze integration still needs testing against the installed agent. The included tests use simulated responses and don't change a computer's protection.

Heavy Rain is an independent project. Faronics binaries aren't included. See the [DFC documentation](https://docs.faronics.com/deep-freeze-cloud/using-deep-freeze-cloud-console/deep-freeze-service/advanced-settings-tab/deep-freeze-command-line-syntax) and [third-party notices](THIRD-PARTY-NOTICES.txt).
