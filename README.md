# Heavy Rain

A Windows app for Deep Freeze and Anti-Executable. Check protection, choose the next boot state, or start a maintenance period. Built with C#, WPF and MahApps.Metro.

Made by [@kpawnd](https://github.com/kpawnd).

## Features

- Frozen, thawed and unknown status, refreshed every 30 seconds.
- Freeze or thaw on the next boot, with an option to restart immediately.
- Anti-Executable status, enable/disable controls, and one-hour maintenance.
- Local Cloud policy name and Deep Freeze version information.
- Diagnostic reports you can save as JSON.
- A dark purple Metro interface with explanations when an action can't run.
- Animated dots along the bottom while auto-detection and other checks are running.
- A six-second startup screen with live installation, policy and status checks.
- An original synth startup chime, embedded in the EXE.

## Using it

Copy `HeavyRain.exe` wherever you want to run it. The portable build compresses its bundled DLLs and .NET runtime; there is nothing else to install for Heavy Rain itself.

Launch it and approve the Windows administrator prompt. Checks start during the credit animation, then the log shows the loaded libraries, searched locations and query results. Heavy Rain looks for `DFC.exe` automatically. If it can't find it, open **installation** and use **Locate DFC.exe** to select the copy supplied with your Deep Freeze installation.

Enter a Deep Freeze password with command-line rights, then choose **Freeze PC** or **Thaw PC**. By default, the change applies on the next restart. Tick **Restart immediately after confirmation** if you want to restart now.

Launch with `HeavyRain.exe --silent` to mute startup and error sounds. Sound playback doesn't delay the checks. Error dialogs shake briefly after appearing, even when Windows animations are disabled. Closing a dialog stops its sound and movement.

Save your work first. Restarting a frozen computer can discard changes on protected drives.

You need Deep Freeze and a compatible `DFC.exe` installed. The Cloud agent alone isn't enough. A Windows password or Deep Freeze one-time password won't work for these commands.

For Anti-Executable, open **anti-executable**. Heavy Rain looks for `AEC.exe` beside the registered AE service and in Faronics installation folders. You can also select it under **installation**. Your Windows account must be authorized in Anti-Executable. Enter the additional Anti-Executable password if one is configured; it is separate from the Deep Freeze password.

**Check status** queries AEC with the password you entered. **Enable**, **Disable**, and **Start maintenance** ask for confirmation, check the current state, and verify it again after the command. Maintenance requests one hour. Disabling during Maintenance ends that mode and leaves protection off. Maintenance can add new or changed executables to the allowed list; it isn't just a temporary pause. AEC doesn't report the remaining time.

AEC can reject an unauthorized account silently, returning exit code 0 without any status. When that happens, Heavy Rain runs a separate, read-only `status` diagnostic and recognizes a small set of AEC's internal error messages. **Not authorized** means AEC classified the Windows account as an External User. In a Cloud policy, add the account under **Anti-Executable → User Settings → Trusted Users**, then apply the policy and check again. The AE password is an extra check for authorized accounts; it doesn't grant access on its own.

**Password required** means AEC is asking for its additional password. Enter the AE Administrator or AE Trusted User password named in the message, then choose **Check status**. These passwords are configured in the policy's **User Settings**, separately from Windows and Deep Freeze passwords. **Password rejected** means AEC didn't accept the password supplied with that request. Passwords are cleared after each request, so automatic checks may ask for one again.

Diagnostics are limited to a five-second status query, cached for one minute, and never receive passwords or run protection changes. Raw diagnostic messages aren't retained or exported. If no known reason is available, status stays **Unknown**. Unrecognized or conflicting responses also stay unknown.

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

Run `HeavyRain.exe --demo` for a preview with sample data. Demo mode still asks for administrator privileges, but doesn't run vendor commands. Anti-Executable controls change only the sample state.

## A few things to know

The next-boot panel records requests made during the current session. It doesn't read back Deep Freeze's scheduled boot mode. Check the reported protection state after restarting.

Policy detection first reads the Cloud agent's registered storage path and its cached current policy in `Tasks.dat`. It uses the root policy name, not a product-specific name or queued task. This location was verified on a Cloud-managed Windows VM. Faronics registry fields and the local Cloud window are fallbacks. Hover over the policy name to see its source. The Cloud console may have newer settings than the local cache.

If the policy stays unavailable, open **activity** and use **Export diagnostics**. The report includes checked locations, recognized names and read errors. It doesn't include the raw cached policy, passwords or tokens. Detection opens the database and registry read-only.

Protection and policy refresh every 30 seconds. Installation discovery and version queries run every five minutes, or when you click **Refresh**. Automatic polling pauses while minimized and resumes when restored.

Passwords aren't saved or included in reports. The vendor tools receive passwords as process arguments, so administrator tools or Windows command-line auditing may capture them. Automatic status checks use the Windows account without retaining your password.

The AEC location, version, and command syntax were checked on a Cloud-managed VM with Anti-Executable 6.11.2112.730. Heavy Rain's read-only client was also run inside that VM and identified its silent rejection as an unauthorized Windows account. Live protection changes haven't been tested; command sequencing and failure handling are covered by simulated tests. Deep Freeze integration also needs validation against the installed agent.

Heavy Rain is an independent project. Faronics binaries aren't included. See the [DFC documentation](https://docs.faronics.com/deep-freeze-cloud/using-deep-freeze-cloud-console/deep-freeze-service/advanced-settings-tab/deep-freeze-command-line-syntax), [AEC documentation](https://support.faronics.com/help/en-ca/46-anti-executable/216-what-is-the-command-line-syntax-for-faronics-anti-executable-enterprise), and [third-party notices](THIRD-PARTY-NOTICES.txt).

The purple dragon is a recoloring of the older [LLVM dragon](https://llvm.org/Logo.html), owned by Apple. Heavy Rain isn't affiliated with LLVM or Apple.
