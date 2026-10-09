package com.orbitnav.navigator.core;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.UUID;

/** UI-independent local browser model. No account, cookies, WebView or platform objects. */
public final class BrowserState {
    public static final int MAX_TABS = 16;
    public static final int MAX_HISTORY = 250;
    public static final int MAX_BOOKMARKS = 250;
    public enum Mode { NORMAL, PRIVATE }
    public static final class Tab {
        public final String id;
        public String address;
        public String title;
        Tab(String id, String address, String title) { this.id = id; this.address = address; this.title = title; }
    }
    public static final class Page {
        public final String address;
        public final String title;
        public final long visitedAtMillis;
        Page(String address, String title, long visitedAtMillis) {
            this.address = address; this.title = title; this.visitedAtMillis = visitedAtMillis;
        }
    }

    public final Mode mode;
    final ArrayList<Tab> tabs = new ArrayList<>();
    final ArrayList<Page> history = new ArrayList<>();
    final ArrayList<Page> bookmarks = new ArrayList<>();
    int selected;
    private boolean ended;
    public boolean javaScript = true;
    public boolean httpsOnly = true;
    public boolean restoreTabs = true;
    public boolean darkTheme;
    public NavigationPolicy.SearchEngine searchEngine = NavigationPolicy.SearchEngine.DUCKDUCKGO;

    public BrowserState(Mode mode) {
        if (mode == null) throw new IllegalArgumentException("Mode required.");
        this.mode = mode;
        addTab();
    }
    public List<Tab> tabs() { return Collections.unmodifiableList(tabs); }
    public List<Page> history() { return Collections.unmodifiableList(history); }
    public List<Page> bookmarks() { return Collections.unmodifiableList(bookmarks); }
    public Tab current() { requireActive(); return tabs.get(selected); }
    public int selectedIndex() { return selected; }
    public Tab addTab() {
        requireActive();
        if (tabs.size() >= MAX_TABS) throw new IllegalStateException("This build supports up to 16 tabs. Close one before adding another.");
        Tab tab = new Tab(UUID.randomUUID().toString(), NavigationPolicy.HOME, "New tab");
        tabs.add(tab); selected = tabs.size() - 1;
        return tab;
    }
    public void select(String id) {
        requireActive();
        for (int i = 0; i < tabs.size(); i++) if (tabs.get(i).id.equals(id)) { selected = i; return; }
        throw new IllegalArgumentException("Unknown tab.");
    }
    public void close(String id) {
        requireActive();
        String selectedId = current().id;
        int removed = -1;
        for (int i = 0; i < tabs.size(); i++) if (tabs.get(i).id.equals(id)) { removed = i; break; }
        if (removed < 0) return;
        tabs.remove(removed);
        if (tabs.isEmpty()) {
            if (mode == Mode.PRIVATE) endPrivateSession(); else addTab();
            return;
        }
        if (selectedId.equals(id)) selected = Math.min(removed, tabs.size() - 1);
        else select(selectedId);
    }
    public void navigate(String id, String address) {
        requireActive();
        String safe = NavigationPolicy.HOME.equals(address) ? address : NavigationPolicy.requireWebAddress(address, httpsOnly);
        for (Tab tab : tabs) if (tab.id.equals(id)) { tab.address = safe; tab.title = ""; return; }
        throw new IllegalArgumentException("Unknown tab.");
    }
    public void visited(String id, String address, String title, long now) {
        if (ended) return;
        if (!NavigationPolicy.canLoad(address, httpsOnly)) return;
        Tab target = null;
        for (Tab tab : tabs) if (tab.id.equals(id)) target = tab;
        if (target == null) return; // Late callbacks from a closed renderer are ignored.
        target.address = address; target.title = safeTitle(title);
        if (mode == Mode.PRIVATE) return;
        history.removeIf(page -> page.address.equals(address));
        history.add(0, new Page(address, safeTitle(title), Math.max(0, now)));
        while (history.size() > MAX_HISTORY) history.remove(history.size() - 1);
    }
    public void bookmarkCurrent(long now) {
        if (mode != Mode.NORMAL) throw new IllegalStateException("Private data cannot become a persistent bookmark.");
        Tab tab = current();
        if (!NavigationPolicy.canLoad(tab.address, false)) throw new IllegalStateException("Open a web page first.");
        bookmarks.removeIf(page -> page.address.equals(tab.address));
        bookmarks.add(0, new Page(tab.address, safeTitle(tab.title), Math.max(0, now)));
        while (bookmarks.size() > MAX_BOOKMARKS) bookmarks.remove(bookmarks.size() - 1);
    }
    public void removeBookmark(String address) { bookmarks.removeIf(page -> page.address.equals(address)); }
    public void clearHistory() { history.clear(); }
    public void resetTabs() { requireActive(); tabs.clear(); selected = 0; addTab(); }
    /** Called when a private session ends, including the last tab closing. No serializable state survives. */
    public void endPrivateSession() {
        if (mode != Mode.PRIVATE) throw new IllegalStateException("Not a private session.");
        tabs.clear(); history.clear(); bookmarks.clear(); selected = 0; ended = true;
    }
    private void requireActive() { if (ended) throw new IllegalStateException("This private session has ended."); }
    static String safeTitle(String title) {
        if (title == null) return "";
        return title.replaceAll("[\\p{Cntrl}]", " ").substring(0, Math.min(512, title.length()));
    }
}
