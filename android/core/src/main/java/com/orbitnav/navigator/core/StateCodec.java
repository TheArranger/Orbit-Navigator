package com.orbitnav.navigator.core;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.util.HashSet;
import java.util.List;
import java.util.UUID;

/** Versioned, bounded, normal-only local format; deliberately not the My Orbit wire format. */
public final class StateCodec {
    public static final int MAX_BYTES = 8 * 1024 * 1024;
    private static final int MAGIC = 0x4F524254;
    private StateCodec() { }
    public static byte[] encode(BrowserState state) throws IOException {
        if (state.mode != BrowserState.Mode.NORMAL) throw new SecurityException("Private state must never be persisted.");
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        try (DataOutputStream out = new DataOutputStream(bytes)) {
            out.writeInt(MAGIC); out.writeInt(1);
            out.writeBoolean(state.javaScript); out.writeBoolean(state.httpsOnly);
            out.writeBoolean(state.restoreTabs); out.writeBoolean(state.darkTheme);
            out.writeUTF(state.searchEngine.name());
            out.writeInt(state.restoreTabs ? state.tabs.size() : 0);
            out.writeInt(state.restoreTabs ? state.selected : 0);
            if (state.restoreTabs) for (BrowserState.Tab tab : state.tabs) {
                out.writeUTF(tab.id); out.writeUTF(tab.address); out.writeUTF(BrowserState.safeTitle(tab.title));
            }
            writePages(out, state.history); writePages(out, state.bookmarks);
        }
        if (bytes.size() > MAX_BYTES) throw new IOException("Browser state exceeds its storage bound.");
        return bytes.toByteArray();
    }
    public static BrowserState decode(byte[] bytes) throws IOException {
        if (bytes.length > MAX_BYTES) throw new IOException("Browser state exceeds its storage bound.");
        try (DataInputStream in = new DataInputStream(new ByteArrayInputStream(bytes))) {
            if (in.readInt() != MAGIC || in.readInt() != 1) throw new IOException("Unsupported browser state format.");
            BrowserState result = new BrowserState(BrowserState.Mode.NORMAL);
            result.javaScript = in.readBoolean(); result.httpsOnly = in.readBoolean();
            result.restoreTabs = in.readBoolean(); result.darkTheme = in.readBoolean();
            result.searchEngine = NavigationPolicy.SearchEngine.valueOf(in.readUTF());
            int count = bounded(in.readInt(), BrowserState.MAX_TABS);
            int selected = in.readInt();
            if (selected < 0 || (count == 0 ? selected != 0 : selected >= count)) throw new IOException("Invalid selection.");
            if (!result.restoreTabs && count != 0) throw new IOException("Unexpected persisted tabs.");
            HashSet<String> ids = new HashSet<>();
            if (count > 0) result.tabs.clear();
            for (int i = 0; i < count; i++) {
                String id = in.readUTF();
                if (!UUID.fromString(id).toString().equals(id) || !ids.add(id)) throw new IOException("Invalid tab identity.");
                String address = in.readUTF();
                if (!NavigationPolicy.HOME.equals(address)) NavigationPolicy.requireWebAddress(address, false);
                String title = in.readUTF();
                if (title.length() > 512) throw new IOException("Invalid title length.");
                result.tabs.add(new BrowserState.Tab(id, address, BrowserState.safeTitle(title)));
            }
            result.selected = selected;
            readPages(in, result.history, BrowserState.MAX_HISTORY);
            readPages(in, result.bookmarks, BrowserState.MAX_BOOKMARKS);
            if (in.read() != -1) throw new IOException("Unexpected trailing browser state.");
            return result;
        } catch (IllegalArgumentException ex) { throw new IOException("Invalid browser state.", ex); }
    }
    private static void writePages(DataOutputStream out, List<BrowserState.Page> pages) throws IOException {
        out.writeInt(pages.size());
        for (BrowserState.Page page : pages) {
            out.writeUTF(page.address); out.writeUTF(BrowserState.safeTitle(page.title)); out.writeLong(page.visitedAtMillis);
        }
    }
    private static void readPages(DataInputStream in, List<BrowserState.Page> pages, int max) throws IOException {
        int count = bounded(in.readInt(), max);
        HashSet<String> addresses = new HashSet<>();
        for (int i = 0; i < count; i++) {
            String address = NavigationPolicy.requireWebAddress(in.readUTF(), false);
            String title = in.readUTF(); long timestamp = in.readLong();
            if (title.length() > 512 || timestamp < 0 || !addresses.add(address)) throw new IOException("Invalid page record.");
            pages.add(new BrowserState.Page(address, BrowserState.safeTitle(title), timestamp));
        }
    }
    private static int bounded(int count, int max) throws IOException {
        if (count < 0 || count > max) throw new IOException("Invalid record count.");
        return count;
    }
}
