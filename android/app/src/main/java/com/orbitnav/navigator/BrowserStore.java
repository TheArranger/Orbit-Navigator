package com.orbitnav.navigator;

import android.content.Context;
import android.util.AtomicFile;
import com.orbitnav.navigator.core.BrowserState;
import com.orbitnav.navigator.core.StateCodec;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;

/** App-internal, atomic storage. A corrupt/unknown format is preserved until explicit reset. */
final class BrowserStore {
    private final AtomicFile file;
    private boolean blocked;
    BrowserStore(Context context) { file = new AtomicFile(new File(context.getFilesDir(), "browser-state-v1.bin")); }
    BrowserState load() {
        if (!file.getBaseFile().exists() && !new File(file.getBaseFile().getPath() + ".bak").exists())
            return new BrowserState(BrowserState.Mode.NORMAL);
        try (FileInputStream input = file.openRead(); ByteArrayOutputStream bytes = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[8192];
            int count;
            while ((count = input.read(buffer)) != -1) {
                if (bytes.size() + count > StateCodec.MAX_BYTES) throw new IOException("State limit exceeded.");
                bytes.write(buffer, 0, count);
            }
            return StateCodec.decode(bytes.toByteArray());
        } catch (IOException | RuntimeException ex) {
            blocked = true;
            return new BrowserState(BrowserState.Mode.NORMAL);
        }
    }
    boolean isBlocked() { return blocked; }
    void save(BrowserState state) throws IOException {
        // Check before serialization/storage; private data must never enter platform storage.
        if (state.mode != BrowserState.Mode.NORMAL) throw new SecurityException("Private persistence denied.");
        if (blocked) throw new IOException("Saved browser state requires an explicit reset.");
        byte[] bytes = StateCodec.encode(state);
        FileOutputStream output = null;
        try {
            output = file.startWrite(); output.write(bytes); file.finishWrite(output);
        } catch (IOException ex) { if (output != null) file.failWrite(output); throw ex; }
    }
    void reset() { file.delete(); blocked = false; }
}
