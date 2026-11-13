# Heavy Rain

A Windows app for Faronics Deep Freeze and Anti-Executable: check protection, freeze or thaw, and manage Anti-Executable. Built with C#, WPF and MahApps.Metro by [@kpawnd](https://github.com/kpawnd).

![Deep Freeze tab showing a Frozen protection state, Cloud policy and version details](docs/deep-freeze.png)

![Anti-Executable tab showing a Protected state confirmed by the Faronics provider](docs/anti-executable.png)

## Features

- Deep Freeze state, Cloud policy name and version, refreshed every 30 seconds.
- Freeze or thaw on the next boot, or restart into the new state now.
- Anti-Executable state, enable, disable and one-hour Maintenance.
- Anti-Executable's policy settings, and its recent activity with an option to allow a logged program.
- Diagnostic reports saved as JSON.
- An animated startup log, an original synth chime, and a dark purple Metro interface.

## Using it

Copy `HeavyRain.exe` anywhere and run it; approve the administrator prompt. It's a single portable EXE with nothing else to install. The main window shows states, settings and controls; when a state is **Unknown** the reason appears under it, and every check and request is recorded under **activity**.

`--silent` mutes the startup and error sounds. `--demo` uses sample data and runs no vendor commands.

### Deep Freeze

Needs Deep Freeze with a compatible `DFC.exe`; the Cloud agent alone isn't enough. Heavy Rain finds `DFC.exe` automatically, or use **Locate DFC.exe** under **installation**.

Enter a Deep Freeze password with command-line rights (not a Windows password or one-time password), then choose **Freeze PC** or **Thaw PC**. The change applies at the next restart unless you tick **Restart immediately after confirmation**. Restarting a frozen PC discards changes on protected drives, so save your work first.

**Last request** records what you asked for in this session; Deep Freeze's scheduled boot mode isn't read back, so check the state after restarting.

### Anti-Executable

Heavy Rain finds `AEC.exe` in the Faronics folders, or use **Locate AEC.exe**. Your Windows account must be authorized in Anti-Executable, and if the policy sets an AE password, enter it; it's separate from the Deep Freeze password.

**Enable**, **Disable** and **Start maintenance** ask for confirmation, send the command once, then check the state up to six times over ten seconds while it settles. Maintenance lasts one hour and can add new or changed programs to the allowed list; disabling during Maintenance ends it and leaves protection off.

**Policy** shows the settings saved on the PC. "Unknown programs can still run" means the policy allows and only logs unknown programs, even while protected. **Recent activity** lists the newest entries from Anti-Executable's own log; select a program and choose **Allow selected file** to add it to the allowed list. AEC doesn't report the list back, so this is shown as accepted, not verified. Hover over the list to see the log's path.

If the state shows:

- **Not authorized**: AEC treats your Windows account as an External User. Add it under **Anti-Executable → User Settings → Trusted Users** in the Cloud policy and check again. An AE password doesn't grant access on its own.
- **Password required** or **Password rejected**: enter the AE Administrator or Trusted User password named in the message, from the policy's **User Settings**, and choose **Check status**.

## Good to know

- Changes are local; a Cloud policy can apply different settings at its next check-in, and the Cloud console may have newer settings than the PC.
- Passwords are never saved, logged or exported, and are cleared after each request. The vendor tools receive them as process arguments, so command-line auditing may capture them.
- States come only from `DFC.exe`, `AEC.exe` and the Faronics provider; a registered agent or driver isn't treated as proof of protection. Unrecognized responses stay **Unknown**.
- On Anti-Executable 6.11.2112.730, `AEC.exe` reports "Unprotected" even while protected, so the state is cross-checked with the Faronics provider (`root\Faronics`, `AntiExecutable_v2`). Its codes `1105` (protected), `1102` (Maintenance) and `1101` (off) were each confirmed against Anti-Executable's log; other codes are left to `AEC.exe`.
- The Cloud policy name comes from the Cloud agent's cached `Tasks.dat`, with Faronics registry fields and the Cloud window as fallbacks. Hover over it to see the source.
- Installation and versions are rechecked every five minutes or on **Refresh**. Polling pauses while minimized.
- **Export diagnostics** includes checked locations, read errors and session activity, but not passwords, tokens, the raw policy cache or Anti-Executable's log.
- Tested on a Cloud-managed Windows 11 VM, including live Anti-Executable changes. Deep Freeze freeze and thaw haven't been tested live yet.

## Building

Requires Windows and the .NET 10 SDK.

```powershell
dotnet build HeavyRain.sln -c Release
dotnet run --project tests/HeavyRain.Tests -c Release
dotnet publish src/HeavyRain/HeavyRain.csproj -c Release -o publish
```

`publish/HeavyRain.exe` is the compressed single-file build. Add `-p:EnableCompressionInSingleFile=false` for a larger EXE that starts slightly faster.

## Credits

Heavy Rain is an independent project and doesn't include Faronics software. See the [DFC](https://docs.faronics.com/deep-freeze-cloud/using-deep-freeze-cloud-console/deep-freeze-service/advanced-settings-tab/deep-freeze-command-line-syntax) and [AEC](https://support.faronics.com/help/en-ca/46-anti-executable/216-what-is-the-command-line-syntax-for-faronics-anti-executable-enterprise) command-line documentation and the [third-party notices](THIRD-PARTY-NOTICES.txt). The purple dragon is a recoloring of the older [LLVM dragon](https://llvm.org/Logo.html), owned by Apple; Heavy Rain isn't affiliated with LLVM or Apple.
