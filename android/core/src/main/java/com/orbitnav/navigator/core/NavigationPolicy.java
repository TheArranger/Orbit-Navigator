package com.orbitnav.navigator.core;

import java.net.URI;
import java.net.URISyntaxException;
import java.net.URLEncoder;
import java.io.UnsupportedEncodingException;
import java.util.Locale;

/** The single address/security boundary for user input, restored data and WebView requests. */
public final class NavigationPolicy {
    public static final int MAX_ADDRESS_LENGTH = 8192;
    public static final String HOME = "orbit:home";
    public enum SearchEngine {
        DUCKDUCKGO("DuckDuckGo", "https://duckduckgo.com/?q="),
        GOOGLE("Google", "https://www.google.com/search?q="),
        BING("Bing", "https://www.bing.com/search?q=");
        public final String displayName;
        private final String prefix;
        SearchEngine(String displayName, String prefix) { this.displayName = displayName; this.prefix = prefix; }
        public String search(String query) {
            try { return prefix + URLEncoder.encode(query, "UTF-8"); }
            catch (UnsupportedEncodingException impossible) { throw new AssertionError(impossible); }
        }
    }

    private NavigationPolicy() { }

    public static String resolve(String input, SearchEngine engine, boolean httpsOnly) {
        if (input == null) throw new IllegalArgumentException("Enter an address or search.");
        String value = input.trim();
        if (value.isEmpty() || HOME.equals(value)) return HOME;
        if (value.length() > MAX_ADDRESS_LENGTH || hasControls(value))
            throw new IllegalArgumentException("The address is too long or contains control characters.");
        boolean hasScheme = value.matches("^[A-Za-z][A-Za-z0-9+.-]*:.*");
        boolean hostPort = value.matches("^[A-Za-z0-9.-]+:[0-9]{1,5}([/?#].*)?$");
        if (hasScheme && !hostPort) return requireWebAddress(value, httpsOnly);
        if (!value.contains(" ") && (value.contains(".") || value.startsWith("localhost")
                || value.startsWith("["))) {
            return requireWebAddress("https://" + value, httpsOnly);
        }
        return engine.search(value);
    }

    public static String requireWebAddress(String input, boolean httpsOnly) {
        if (input == null || input.length() > MAX_ADDRESS_LENGTH || hasControls(input))
            throw new IllegalArgumentException("Unsupported address.");
        try {
            URI uri = new URI(input);
            String scheme = uri.getScheme() == null ? "" : uri.getScheme().toLowerCase(Locale.ROOT);
            if (!("https".equals(scheme) || "http".equals(scheme)) || uri.getHost() == null
                    || uri.getHost().isEmpty() || uri.getRawUserInfo() != null || uri.getPort() > 65535)
                throw new IllegalArgumentException("Only HTTP and HTTPS addresses without embedded credentials are supported.");
            if (httpsOnly && !"https".equals(scheme))
                throw new IllegalArgumentException("Direct HTTP navigation is blocked by your HTTPS preference. Change this deliberately in Settings if needed.");
            return uri.toASCIIString();
        } catch (URISyntaxException ex) {
            throw new IllegalArgumentException("This address is not valid.");
        }
    }

    public static boolean canLoad(String address, boolean httpsOnly) {
        try { requireWebAddress(address, httpsOnly); return true; }
        catch (IllegalArgumentException ex) { return false; }
    }

    public static String origin(String address) {
        try {
            URI uri = new URI(requireWebAddress(address, false));
            return uri.getScheme().toLowerCase(Locale.ROOT) + "://" + uri.getHost()
                    + (uri.getPort() == -1 ? "" : ":" + uri.getPort());
        } catch (IllegalArgumentException | URISyntaxException ex) { return "Orbit home"; }
    }

    private static boolean hasControls(String value) {
        for (int i = 0; i < value.length(); i++) if (Character.isISOControl(value.charAt(i))) return true;
        return false;
    }
}
