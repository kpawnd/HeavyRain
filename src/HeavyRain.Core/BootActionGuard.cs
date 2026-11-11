namespace HeavyRain.Core;

public sealed record ActionBlocker(string Title, string Message);

public static class BootActionGuard
{
    public static ActionBlocker? Check(BootTarget target, bool busy, bool uncertain, bool administrator,
        bool hasDfc, ProtectionState current, string stateDetail, int passwordLength)
    {
        if (busy) return new("An operation is still running", "Heavy Rain is checking this computer or processing another request. Wait for it to finish, then try again.");
        if (!administrator) return new("Administrator access is required", "Close Heavy Rain, launch HeavyRain.exe and approve the Windows administrator prompt. Protection changes cannot run without elevation.");
        if (uncertain) return new("Verify the previous request first", "The last change did not return a confirmed result. It may already have been applied. Verify next-boot settings in Deep Freeze before reopening Heavy Rain and trying again.");
        if (!hasDfc) return new("Deep Freeze control is unavailable", "DFC.exe was not found. Use Locate DFC.exe to select the Faronics command-line tool that matches this installation. The Cloud agent alone does not provide freeze/thaw protection.");
        if (current == ProtectionState.Unknown) return new("Protection state is unknown", "Heavy Rain must verify the current state before changing it.\n\n" + stateDetail + "\n\nRefresh and resolve the reported problem, then try again.");
        if ((target == BootTarget.Frozen && current == ProtectionState.Frozen) || (target == BootTarget.Thawed && current == ProtectionState.Thawed))
            return new($"This PC is already {target.ToString().ToLowerInvariant()}", "DFC only supports a transition from the opposite current state. No command has been sent.\n\nThis does not verify or cancel an earlier next-boot request. Inspect those settings in the Deep Freeze console if needed.");
        if (passwordLength == 0) return new("Enter your Deep Freeze password", "Enter a Deep Freeze password with command-line rights, then try again. Your Windows password and one-time passwords cannot be used for this command.");
        if (passwordLength > 63) return new("The password is too long", "DFC supports passwords up to 63 characters. Check the password and try again.");
        return null;
    }
}
