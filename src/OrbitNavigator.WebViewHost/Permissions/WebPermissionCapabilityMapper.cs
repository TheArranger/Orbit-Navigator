using Microsoft.Web.WebView2.Core;
using OrbitNavigator.Contracts.Privacy;

namespace OrbitNavigator.WebViewHost.Permissions;

public static class WebPermissionCapabilityMapper
{
    public static WebPermissionCapability Map(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Camera => WebPermissionCapability.Camera,
        CoreWebView2PermissionKind.Microphone => WebPermissionCapability.Microphone,
        CoreWebView2PermissionKind.Geolocation => WebPermissionCapability.Geolocation,
        CoreWebView2PermissionKind.Notifications => WebPermissionCapability.Notifications,
        CoreWebView2PermissionKind.ClipboardRead => WebPermissionCapability.ClipboardRead,
        CoreWebView2PermissionKind.Autoplay => WebPermissionCapability.Autoplay,
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => WebPermissionCapability.Midi,
        _ => WebPermissionCapability.Unknown,
    };

    public static WebPermissionCapability Popup => WebPermissionCapability.Popups;
}
