namespace Sakshi.Platform.Doctor;

/// <summary>
/// One line of <c>sakshi doctor</c>: what was checked, whether it passed, and the command that fixes it.
/// <paramref name="Fix"/> is only a command, so it can be copied and pasted as it is; advice goes in
/// <paramref name="Detail"/>. <paramref name="SettingsUrl"/> opens the system settings page the check is about.
/// A check that is not <paramref name="Required"/> is advice: Sakshi records without it, so it never opens the
/// setup window by itself.
/// </summary>
public sealed record DoctorCheck(string Name, bool Ok, string Detail, string? Fix, string? SettingsUrl = null, bool Required = true);
