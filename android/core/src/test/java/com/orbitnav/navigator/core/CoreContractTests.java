package com.orbitnav.navigator.core;

import java.io.IOException;
import java.util.Arrays;

/** Dependency-free deterministic contract suite; runs on the installed JDK without an emulator. */
public final class CoreContractTests {
    private static int assertions;
    public static void main(String[] args) throws Exception {
        navigation(); tabs(); persistence(); privateLifecycle(); corruption();
        System.out.println("PASS: " + assertions + " Android browser core assertions.");
    }
    private static void navigation() {
        same(NavigationPolicy.HOME, resolve(" "));
        same("https://example.com", resolve("example.com"));
        same("https://example.com:8443/path", resolve("example.com:8443/path"));
        same("https://localhost:8443", resolve("localhost:8443"));
        same("https://duckduckgo.com/?q=some+words+%26+symbols", resolve("some words & symbols"));
        same("https://www.google.com/search?q=orbit", NavigationPolicy.resolve("orbit", NavigationPolicy.SearchEngine.GOOGLE, true));
        same("https://www.bing.com/search?q=orbit", NavigationPolicy.resolve("orbit", NavigationPolicy.SearchEngine.BING, true));
        same("https://example.com", resolve("https://example.com"));
        same("http://example.com", NavigationPolicy.resolve("http://example.com", NavigationPolicy.SearchEngine.BING, false));
        for (String dangerous : new String[] { "javascript:alert(1)", "file:///private/data", "content://contacts", "intent://host", "data:text/html,test", "ftp://host", "http://example.com", "https://user:password@example.com", "https://example.com:99999", "https://example.com\nInjected" }) {
            rejects(IllegalArgumentException.class, () -> resolve(dangerous));
        }
        rejects(IllegalArgumentException.class, () -> resolve("a".repeat(8193)));
        truth(!NavigationPolicy.canLoad("https://user@example.com", false));
        truth(!NavigationPolicy.canLoad("//example.com", false));
        same("https://example.com:8443", NavigationPolicy.origin("https://example.com:8443/path?secret=yes"));
    }
    private static void tabs() {
        BrowserState state = normal(); String first = state.current().id;
        state.addTab(); String second = state.current().id;
        state.addTab(); String third = state.current().id;
        state.close(first); same(third, state.current().id); same(1, state.selectedIndex());
        state.close(third); same(second, state.current().id);
        state.close(second); same(1, state.tabs().size()); same(NavigationPolicy.HOME, state.current().address);
        rejects(IllegalArgumentException.class, () -> state.select("missing"));
        for (int i = 1; i < BrowserState.MAX_TABS; i++) state.addTab();
        rejects(IllegalStateException.class, state::addTab); same(16, state.tabs().size());
        state.visited("closed-tab", "https://example.com", "Ignored", 1); same(0, state.history().size());
        rejects(IllegalArgumentException.class, () -> state.navigate(state.current().id, "file:///private"));
    }
    private static void persistence() throws Exception {
        BrowserState original = normal(); String id = original.current().id;
        original.visited(id, "https://example.com", "Title Ω 🚀", 12345);
        original.bookmarkCurrent(12345); original.addTab();
        original.darkTheme = true; original.javaScript = false; original.searchEngine = NavigationPolicy.SearchEngine.BING;
        BrowserState loaded = StateCodec.decode(StateCodec.encode(original));
        same(2, loaded.tabs().size()); same(1, loaded.selectedIndex()); same(id, loaded.tabs().get(0).id);
        same("Title Ω 🚀", loaded.history().get(0).title); same(12345L, loaded.history().get(0).visitedAtMillis);
        same(1, loaded.bookmarks().size()); truth(loaded.darkTheme); truth(!loaded.javaScript);
        same(NavigationPolicy.SearchEngine.BING, loaded.searchEngine);
        original.restoreTabs = false; loaded = StateCodec.decode(StateCodec.encode(original));
        same(1, loaded.tabs().size()); same(NavigationPolicy.HOME, loaded.current().address); same(1, loaded.history().size());
        original.restoreTabs = true;
        for (int i = 0; i < 300; i++) original.visited(original.current().id, "https://fixture.invalid/" + i, "Fixture", i);
        same(250, original.history().size()); same("https://fixture.invalid/299", original.history().get(0).address);
        original.visited(original.current().id, "https://fixture.invalid/299", "Updated", 999);
        same(250, original.history().size()); same("Updated", original.history().get(0).title);
        original.clearHistory(); same(0, original.history().size()); same(1, original.bookmarks().size());
        original.removeBookmark("https://example.com"); same(0, original.bookmarks().size());
    }
    private static void privateLifecycle() {
        BrowserState privateState = new BrowserState(BrowserState.Mode.PRIVATE);
        privateState.visited(privateState.current().id, "https://fixture.invalid/private", "Never save", 100);
        same(0, privateState.history().size());
        rejects(SecurityException.class, () -> StateCodec.encode(privateState));
        rejects(IllegalStateException.class, () -> privateState.bookmarkCurrent(100));
        rejects(SecurityException.class, () -> Capabilities.requireAccountSync(BrowserState.Mode.PRIVATE));
        rejects(UnsupportedOperationException.class, Capabilities::requirePrivateEngine);
        rejects(UnsupportedOperationException.class, () -> Capabilities.requireAccountSync(BrowserState.Mode.NORMAL));
        privateState.endPrivateSession(); same(0, privateState.tabs().size()); same(0, privateState.history().size());
        rejects(IllegalStateException.class, privateState::addTab);
        BrowserState lastPrivate = new BrowserState(BrowserState.Mode.PRIVATE);
        lastPrivate.close(lastPrivate.current().id); same(0, lastPrivate.tabs().size());
        rejects(IllegalStateException.class, lastPrivate::addTab);
        lastPrivate.visited("closed", "https://fixture.invalid/late", "Late callback", 101);
        same(0, lastPrivate.history().size());
        rejects(IllegalStateException.class, () -> normal().endPrivateSession());
    }
    private static void corruption() throws Exception {
        byte[] encoded = StateCodec.encode(normal());
        rejects(IOException.class, () -> StateCodec.decode(Arrays.copyOf(encoded, 4)));
        rejects(IOException.class, () -> StateCodec.decode(Arrays.copyOf(encoded, encoded.length + 1)));
        byte[] unknown = encoded.clone(); unknown[7] = 2;
        rejects(IOException.class, () -> StateCodec.decode(unknown));
        byte[] badMagic = encoded.clone(); badMagic[0] = 0;
        rejects(IOException.class, () -> StateCodec.decode(badMagic));
        rejects(IOException.class, () -> StateCodec.decode(new byte[StateCodec.MAX_BYTES + 1]));
        BrowserState invalid = normal(); invalid.current().address = "file:///private";
        rejects(IOException.class, () -> StateCodec.decode(StateCodec.encode(invalid)));
        BrowserState duplicated = normal(); duplicated.addTab(); duplicated.tabs.set(1, duplicated.tabs.get(0));
        rejects(IOException.class, () -> StateCodec.decode(StateCodec.encode(duplicated)));
        BrowserState badSelection = normal(); badSelection.selected = 5;
        rejects(IOException.class, () -> StateCodec.decode(StateCodec.encode(badSelection)));
    }
    private static String resolve(String input) { return NavigationPolicy.resolve(input, NavigationPolicy.SearchEngine.DUCKDUCKGO, true); }
    private static BrowserState normal() { return new BrowserState(BrowserState.Mode.NORMAL); }
    private static void same(Object expected, Object actual) { assertions++; if (!expected.equals(actual)) throw new AssertionError("Expected " + expected + ", got " + actual); }
    private static void truth(boolean result) { assertions++; if (!result) throw new AssertionError("Condition failed."); }
    private interface Action { void run() throws Exception; }
    private static void rejects(Class<? extends Exception> type, Action action) {
        assertions++;
        try { action.run(); } catch (Exception ex) { if (type.isInstance(ex)) return; throw new AssertionError("Wrong exception.", ex); }
        throw new AssertionError("Expected " + type.getSimpleName());
    }
}
