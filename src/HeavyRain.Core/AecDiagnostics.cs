namespace HeavyRain.Core;

public enum AecDiagnostic { None, ExternalUser, UnsupportedEdition, ConfigurationUnavailable, PasswordRejected, PasswordRequired }

public interface IAecDiagnosticReader
{
    Task<AecDiagnostic> DiagnoseAsync(string executable, CancellationToken cancellationToken = default);
}

public static class AecDiagnostics
{
    public static AecDiagnostic Classify(string message) => message.Split('\0')[0].Trim() switch
    {
        "In CCmdParser::HandleCommandLine(). No command line for External User." => AecDiagnostic.ExternalUser,
        "In CCmdParser::HandleCommandLine(). Is not EnterpriseVersion()." => AecDiagnostic.UnsupportedEdition,
        "In CCmdParser::HandleCommandLine(). m_AeConfiguration.Initialize() Failed." => AecDiagnostic.ConfigurationUnavailable,
        "In CCmdParser::HandleCommandLine(). Password incorrect." => AecDiagnostic.PasswordRejected,
        _ => AecDiagnostic.None
    };

    public static string? Describe(AecDiagnostic diagnostic) => diagnostic switch
    {
        AecDiagnostic.ExternalUser => "AEC rejected this Windows account as an External User. Authorize the account in Anti-Executable User Settings first. For Cloud, add it under the assigned policy's Trusted Users and apply the policy. An AE password does not replace account authorization.",
        AecDiagnostic.UnsupportedEdition => "AEC reports that this installation does not support its Enterprise command-line interface. Manage protection through the installed Anti-Executable application or Cloud console.",
        AecDiagnostic.ConfigurationUnavailable => "AEC could not initialize its Anti-Executable configuration. Check the installation and its configuration in the Faronics application.",
        AecDiagnostic.PasswordRequired => "AEC requires an Anti-Executable password. Enter the AE password configured in your policy's User Settings, then choose Check status. Automatic checks do not retain passwords.",
        AecDiagnostic.PasswordRejected => "AEC did not accept the supplied Anti-Executable password. Enter the correct AE password configured in your policy's User Settings and try again.",
        _ => null
    };
}
