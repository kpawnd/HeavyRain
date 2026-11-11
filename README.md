# Heavy Rain

A Windows app for checking Deep Freeze status and choosing whether a PC boots frozen or thawed. Built with C#, WPF and MahApps.Metro.

Made by [@kpawnd](https://github.com/kpawnd).

## Features

- Frozen, thawed and unknown status, refreshed every 30 seconds.
- Freeze or thaw on the next boot, with an option to restart immediately.
- Local Cloud policy name and Deep Freeze version information.
- Diagnostic reports you can save as JSON.
- A dark purple Metro interface with explanations when an action can't run.

## Using it

Launch `HeavyRain.exe` and approve the Windows administrator prompt. Heavy Rain looks for `DFC.exe` automatically. If it can't find it, use **Locate DFC.exe** to select the copy supplied with your Deep Freeze installation.

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
dotnet publish src/HeavyRain/HeavyRain.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Run `HeavyRain.exe --demo` for a preview with sample data. Demo mode still asks for administrator privileges, but doesn't send commands to Deep Freeze.

## A few things to know

The next-boot panel records requests made during the current session. It doesn't read back Deep Freeze's scheduled boot mode. Check the reported protection state after restarting.

Policy detection reads the local Cloud agent's open window or cached registry metadata. It depends on the agent version, and cached values can be out of date. If no policy name is available, Heavy Rain says so. Local changes may also need to be reconciled with your Cloud policy.

Passwords aren't saved or included in reports. DFC receives the password as a process argument, so administrator tools or Windows command-line auditing may capture it.

Deep Freeze integration still needs testing against the installed agent. The included tests use simulated responses and don't change a computer's protection.

Heavy Rain is an independent project. Faronics binaries aren't included. See the [DFC documentation](https://docs.faronics.com/deep-freeze-cloud/using-deep-freeze-cloud-console/deep-freeze-service/advanced-settings-tab/deep-freeze-command-line-syntax) and [third-party notices](THIRD-PARTY-NOTICES.txt).
