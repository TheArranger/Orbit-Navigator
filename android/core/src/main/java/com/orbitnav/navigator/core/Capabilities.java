package com.orbitnav.navigator.core;

/** Release gates, not UI promises. Never silently downgrade a private/account operation. */
public final class Capabilities {
    public static final String PRIVATE_REASON = "Private browsing is not available in this Android foundation. "
            + "A verified isolated, disposable engine profile is required. No normal tab will be opened instead.";
    public static final String ACCOUNT_REASON = "My Orbit linking and encrypted sync are not available on Android yet. "
            + "The provider must approve a mobile redirect, and device registration, recovery, consent and authenticated "
            + "local apply must pass cross-platform tests. This build makes no account or sync requests.";
    private Capabilities() { }
    public static void requirePrivateEngine() { throw new UnsupportedOperationException(PRIVATE_REASON); }
    public static void requireAccountSync(BrowserState.Mode mode) {
        if (mode == BrowserState.Mode.PRIVATE) throw new SecurityException("Private sessions cannot access account sync.");
        throw new UnsupportedOperationException(ACCOUNT_REASON);
    }
}
