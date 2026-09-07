#if ORBIT_WPF
namespace OrbitNavigator.Presentation.Wpf;

/// <summary>
/// English fallback copy for the initial WPF shell. Keys remain stable so a
/// later resource-backed localizer can replace this catalog without changing
/// presenter contracts.
/// </summary>
public static class PresentationTextCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Text =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ui.permission.capability.camera"] = "camera",
            ["ui.permission.capability.microphone"] = "microphone",
            ["ui.permission.capability.location"] = "location",
            ["ui.permission.capability.notifications"] = "notifications",
            ["ui.permission.capability.clipboard_read"] = "clipboard reading",
            ["ui.permission.capability.clipboard_write"] = "clipboard writing",
            ["ui.permission.capability.midi"] = "MIDI devices",
            ["ui.permission.capability.serial"] = "serial devices",
            ["ui.permission.capability.usb"] = "USB devices",
            ["ui.permission.capability.popups"] = "pop-ups",
            ["ui.permission.capability.autoplay"] = "autoplay",
            ["ui.permission.capability.unknown"] = "an unsupported capability",
            ["ui.permission.keep_blocked"] = "Keep blocked",
            ["ui.permission.allow_once"] = "Allow once",
            ["ui.permission.allow_session"] = "Allow for this session",
            ["ui.permission.allow_always"] = "Always allow",
            ["ui.permission.allow"] = "Allow",
            ["ui.permission.request_expired"] = "This permission request expired.",
            ["ui.permission.request_resolved"] = "This permission request was already handled. The site remains blocked.",
            ["ui.permission.allowed"] = "Permission allowed.",
            ["ui.permission.kept_blocked"] = "Permission kept blocked.",
            ["ui.permission.dismissed"] = "Permission request dismissed.",
            ["ui.permission.request_resolved"] = "This permission request was already handled.",
            ["ui.permission.response_invalid"] = "That permission choice is not available.",
            ["ui.permission.response_not_allowed"] = "That permission choice is not allowed here. The site remains blocked.",
            ["ui.permission.response_cancelled"] = "The permission choice was cancelled. The site remains blocked.",
            ["ui.permission.response_unavailable"] = "Permission controls are temporarily unavailable. The site remains blocked.",
            ["ui.permission.response_changed"] = "This permission request changed. Review the current choices and try again.",
            ["ui.permission.response_failed"] = "Orbit could not apply that permission choice. The site remains blocked.",
            ["ui.permission.applying"] = "Applying your choice…",
        };

    public static string Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return Text.TryGetValue(key, out var value) ? value : key;
    }

    public static string ResolvePermissionFailure(string? key) => key switch
    {
        "error.permission.request_expired" => Resolve("ui.permission.request_expired"),
        "error.permission.response_invalid" => Resolve("ui.permission.response_invalid"),
        "ui.permission.request_expired" or
        "ui.permission.response_invalid" or
        "ui.permission.response_not_allowed" or
        "ui.permission.response_cancelled" or
        "ui.permission.response_unavailable" or
        "ui.permission.response_changed" or
        "ui.permission.response_failed" => Resolve(key),
        _ => Resolve("ui.permission.response_failed"),
    };

    public static string ResolvePermissionAnnouncement(string? key) => key switch
    {
        "ui.permission.request_expired" or
        "ui.permission.request_resolved" or
        "ui.permission.allowed" or
        "ui.permission.kept_blocked" or
        "ui.permission.dismissed" or
        "ui.permission.response_invalid" or
        "ui.permission.response_not_allowed" or
        "ui.permission.response_cancelled" or
        "ui.permission.response_unavailable" or
        "ui.permission.response_changed" or
        "ui.permission.response_failed" => Resolve(key),
        _ => "The permission request is closed. The site remains blocked.",
    };
}
#endif
