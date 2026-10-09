package com.orbitnav.navigator;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.net.http.SslError;
import android.os.Build;
import android.os.Bundle;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;
import android.webkit.CookieManager;
import android.webkit.GeolocationPermissions;
import android.webkit.PermissionRequest;
import android.webkit.RenderProcessGoneDetail;
import android.webkit.SafeBrowsingResponse;
import android.webkit.ServiceWorkerClient;
import android.webkit.ServiceWorkerController;
import android.webkit.SslErrorHandler;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebStorage;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.PopupMenu;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import com.orbitnav.navigator.core.BrowserState;
import com.orbitnav.navigator.core.Capabilities;
import com.orbitnav.navigator.core.NavigationPolicy;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** Native, adaptive Android shell. Only this adapter owns the platform rendering engine. */
public final class BrowserActivity extends Activity {
    private BrowserState state;
    private BrowserStore store;
    private final Map<String, WebView> engines = new HashMap<>();
    private LinearLayout root;
    private FrameLayout page;
    private EditText address;
    private ProgressBar progress;
    private Button back;
    private Button forward;
    private Button tabsButton;
    private TextView origin;
    private boolean persistenceWarningShown;
    private static volatile boolean clearingSiteData;
    private boolean rendererAvailable = true;
    private static java.lang.ref.WeakReference<BrowserActivity> currentActivity = new java.lang.ref.WeakReference<>(null);
    private static volatile boolean httpsOnly = true;
    private int ink;
    private int background;
    private int surface;

    @Override public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        currentActivity = new java.lang.ref.WeakReference<>(this);
        store = new BrowserStore(this);
        state = store.load(); httpsOnly = state.httpsOnly;
        // Dev builds never expose the remote-debugging bridge either.
        try { WebView.setWebContentsDebuggingEnabled(false); configureServiceWorkers(); }
        catch (RuntimeException ex) { rendererAvailable = false; }
        buildUi(); showSelected();
        if (savedInstanceState == null) handleIntent(getIntent());
        if (store.isBlocked()) explain("Saved data needs attention", "The saved browser state could not be read. "
                + "It has been preserved and will not be overwritten. New tab/history/settings changes cannot be saved "
                + "until you explicitly reset saved browser state in Settings. Website storage remains normal, not private.");
    }

    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); setIntent(intent); handleIntent(intent); }
    private void handleIntent(Intent intent) {
        if (Intent.ACTION_VIEW.equals(intent.getAction()) && intent.getData() != null) {
            String requested = intent.getData().toString();
            if (!NavigationPolicy.canLoad(requested, state.httpsOnly)) {
                explain("Address blocked", "This link is not an allowed web address under the current HTTPS navigation preference."); return;
            }
            if (!NavigationPolicy.HOME.equals(state.current().address)) {
                try { state.addTab(); } catch (IllegalStateException ex) { explain("Tab limit", ex.getMessage()); return; }
            }
            navigate(requested);
        }
    }

    private void buildUi() {
        setTheme(state.darkTheme ? android.R.style.Theme_Material_NoActionBar : R.style.Theme_Orbit);
        if (page != null) page.removeAllViews();
        background = Color.parseColor(state.darkTheme ? "#101A2A" : "#F4F7FC");
        surface = Color.parseColor(state.darkTheme ? "#1D2C41" : "#FFFFFF");
        ink = Color.parseColor(state.darkTheme ? "#EBF3FF" : "#172B44");
        root = column(); root.setBackgroundColor(background);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets system = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                view.setPadding(system.left, system.top, system.right, system.bottom);
            } else view.setPadding(insets.getSystemWindowInsetLeft(), insets.getSystemWindowInsetTop(),
                    insets.getSystemWindowInsetRight(), insets.getSystemWindowInsetBottom());
            return insets;
        });
        getWindow().getDecorView().setSystemUiVisibility(state.darkTheme ? 0 : View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);
        LinearLayout titleRow = row();
        TextView brand = text("ORBIT  /  NAVIGATOR", 15); brand.setTypeface(null, Typeface.BOLD);
        titleRow.addView(brand, new LinearLayout.LayoutParams(0, dp(48), 1)); brand.setGravity(Gravity.CENTER_VERTICAL);
        Button menu = button("Menu", this::showMenu); titleRow.addView(menu);
        root.addView(titleRow);
        LinearLayout addressRow = row();
        address = new EditText(this); address.setSingleLine(true); address.setTextColor(ink);
        address.setHintTextColor(Color.GRAY); address.setHint("Search or enter web address");
        address.setContentDescription("Search or enter web address");
        address.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        address.setImeOptions(EditorInfo.IME_ACTION_GO | EditorInfo.IME_FLAG_NO_PERSONALIZED_LEARNING);
        address.setSelectAllOnFocus(true); address.setBackgroundColor(surface);
        address.setOnEditorActionListener((view, action, event) -> {
            if (action == EditorInfo.IME_ACTION_GO || (event != null && event.getKeyCode() == android.view.KeyEvent.KEYCODE_ENTER
                    && event.getAction() == android.view.KeyEvent.ACTION_UP)) { submitAddress(); return true; }
            return false;
        });
        addressRow.addView(address, new LinearLayout.LayoutParams(0, dp(52), 1));
        addressRow.addView(button("Go", v -> submitAddress())); root.addView(addressRow);
        origin = text("Orbit home", 12); origin.setPadding(dp(16), dp(4), dp(16), dp(4)); root.addView(origin);
        progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal); progress.setMax(100);
        root.addView(progress, new LinearLayout.LayoutParams(-1, dp(3)));
        page = new FrameLayout(this); root.addView(page, new LinearLayout.LayoutParams(-1, 0, 1));
        LinearLayout navigation = row();
        back = nav(navigation, "Back", v -> { WebView web = active(); if (web != null && web.canGoBack()) web.goBack(); });
        forward = nav(navigation, "Next", v -> { WebView web = active(); if (web != null && web.canGoForward()) web.goForward(); });
        nav(navigation, "Reload", v -> { WebView web = active(); if (web != null) web.reload(); });
        tabsButton = nav(navigation, "Tabs", v -> showTabs());
        nav(navigation, "+", v -> newTab());
        root.addView(navigation);
        setContentView(root); root.requestApplyInsets();
    }

    private void submitAddress() {
        try { navigate(NavigationPolicy.resolve(address.getText().toString(), state.searchEngine, state.httpsOnly)); }
        catch (IllegalArgumentException ex) { explain("Address not opened", ex.getMessage()); }
    }
    private void navigate(String target) {
        if (clearingSiteData) return;
        try {
            state.navigate(state.current().id, target);
            address.clearFocus();
            ((InputMethodManager) getSystemService(INPUT_METHOD_SERVICE)).hideSoftInputFromWindow(address.getWindowToken(), 0);
            if (NavigationPolicy.HOME.equals(target)) {
                destroyEngine(state.current().id); showSelected();
            } else {
                WebView web = engines.get(state.current().id);
                if (web == null) { showSelected(); }
                else { showSelected(); web.loadUrl(target); }
            }
            persist();
        } catch (IllegalArgumentException ex) { explain("Address blocked", ex.getMessage()); }
    }
    private void showSelected() {
        for (WebView web : engines.values()) web.onPause();
        page.removeAllViews();
        if (clearingSiteData) {
            page.addView(messagePanel("Clearing cookies", "Please wait. Web storage and cache removal have also been requested."));
            return;
        }
        BrowserState.Tab selected = state.current();
        if (NavigationPolicy.HOME.equals(selected.address)) page.addView(home(), new FrameLayout.LayoutParams(-1, -1));
        else if (!NavigationPolicy.canLoad(selected.address, state.httpsOnly)) {
            page.addView(messagePanel("HTTP address is blocked", "This saved tab needs an explicit HTTPS navigation opt-out in Settings. "
                    + "Its address has not been sent to a server."));
        } else {
            WebView web = engines.get(selected.id);
            if (web == null) {
                try {
                    web = createEngine(selected.id); engines.put(selected.id, web);
                    page.addView(web, new FrameLayout.LayoutParams(-1, -1)); web.loadUrl(selected.address);
                } catch (RuntimeException ex) {
                    destroyEngine(selected.id);
                    page.addView(messagePanel("Web renderer unavailable", "Install or update Android System WebView through your device's trusted app store, then reopen Orbit."));
                }
            } else { page.addView(web, new FrameLayout.LayoutParams(-1, -1)); web.onResume(); }
        }
        refreshChrome();
    }
    private View home() {
        ScrollView scroll = new ScrollView(this); scroll.setFillViewport(true);
        LinearLayout canvas = column(); canvas.setGravity(Gravity.CENTER); canvas.setPadding(dp(24), dp(24), dp(24), dp(24));
        TextView heading = text("Your corner of the web.", getResources().getConfiguration().screenWidthDp >= 600 ? 36 : 28);
        heading.setTypeface(null, Typeface.BOLD); canvas.addView(heading);
        TextView caption = text("Browse locally. No account required.", 17); caption.setPadding(0, dp(12), 0, dp(24)); canvas.addView(caption);
        LinearLayout cards = getResources().getConfiguration().screenWidthDp >= 600 ? row() : column();
        cards.addView(homeCard("Start a search", state.searchEngine.displayName + " · Enter a query above", v -> {
            address.requestFocus(); ((InputMethodManager) getSystemService(INPUT_METHOD_SERVICE)).showSoftInput(address, InputMethodManager.SHOW_IMPLICIT);
        }));
        cards.addView(homeCard("Saved pages", state.bookmarks().size() + " bookmarks on this device", v -> showPages(true)));
        cards.addView(homeCard("Make it yours", "Search, appearance and privacy controls", v -> showSettings()));
        canvas.addView(cards);
        TextView limits = text("Android foundation · " + BuildConfig.VERSION_NAME + "\nNormal browsing only. Private mode and My Orbit sync are not enabled.", 13);
        limits.setPadding(0, dp(24), 0, 0); canvas.addView(limits);
        scroll.addView(canvas); return scroll;
    }
    private View homeCard(String heading, String caption, View.OnClickListener click) {
        LinearLayout card = column(); card.setBackgroundColor(surface); card.setPadding(dp(16), dp(20), dp(16), dp(20));
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                getResources().getConfiguration().screenWidthDp >= 600 ? 0 : -1, -2,
                getResources().getConfiguration().screenWidthDp >= 600 ? 1 : 0);
        params.setMargins(dp(4), dp(4), dp(4), dp(4)); card.setLayoutParams(params);
        TextView label = text(heading, 18); label.setTypeface(null, Typeface.BOLD); card.addView(label);
        card.addView(text(caption, 14)); card.setOnClickListener(click); card.setFocusable(true);
        card.setContentDescription(heading + ". " + caption); return card;
    }
    private View messagePanel(String title, String detail) {
        LinearLayout panel = column(); panel.setPadding(dp(24), dp(24), dp(24), dp(24));
        panel.addView(text(title, 24)); panel.addView(text(detail, 16)); return panel;
    }

    @SuppressLint("SetJavaScriptEnabled")
    private WebView createEngine(String tabId) {
        // Must never become a private fallback. Capabilities blocks that UI operation before engine creation.
        if (state.mode != BrowserState.Mode.NORMAL) throw new SecurityException("No verified private engine exists.");
        if (!rendererAvailable) throw new IllegalStateException("Android System WebView is unavailable.");
        WebView web = new WebView(this);
        web.setSaveEnabled(false); web.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO_EXCLUDE_DESCENDANTS);
        WebSettings settings = web.getSettings();
        settings.setJavaScriptEnabled(state.javaScript); settings.setDomStorageEnabled(true);
        settings.setAllowFileAccess(false); settings.setAllowContentAccess(false);
        settings.setAllowFileAccessFromFileURLs(false); settings.setAllowUniversalAccessFromFileURLs(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW); settings.setSafeBrowsingEnabled(true);
        settings.setGeolocationEnabled(false); settings.setMediaPlaybackRequiresUserGesture(true);
        settings.setJavaScriptCanOpenWindowsAutomatically(false); settings.setSupportMultipleWindows(false);
        settings.setBuiltInZoomControls(true); settings.setDisplayZoomControls(false);
        settings.setUseWideViewPort(true); settings.setLoadWithOverviewMode(true);
        CookieManager.getInstance().setAcceptThirdPartyCookies(web, false);
        web.setDownloadListener((url, userAgent, disposition, mime, length) -> explain("Download not started",
                "Downloads are not supported in this Android foundation. No file was saved or handed to another app."));
        web.setWebViewClient(new WebViewClient() {
            private boolean failed;
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                if (NavigationPolicy.canLoad(request.getUrl().toString(), httpsOnly)) return false;
                if (request.isForMainFrame()) explain("Navigation blocked", "Only allowed HTTP/HTTPS pages can open. External apps, local files and embedded credentials are not supported.");
                return true;
            }
            @Override public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
                if (clearingSiteData || !NavigationPolicy.canLoad(request.getUrl().toString(), httpsOnly))
                    return new WebResourceResponse("text/plain", "UTF-8", 403, "Blocked", new HashMap<>(), new ByteArrayInputStream(new byte[0]));
                return null;
            }
            @Override public void onPageStarted(WebView view, String url, android.graphics.Bitmap icon) {
                failed = false;
                if (!NavigationPolicy.canLoad(url, httpsOnly)) { failed = true; view.stopLoading(); return; }
                if (isSelected(tabId)) { address.setText(url); origin.setText(NavigationPolicy.origin(url)); progress.setVisibility(View.VISIBLE); }
            }
            @Override public void onPageFinished(WebView view, String url) {
                if (!failed) { state.visited(tabId, url, view.getTitle(), System.currentTimeMillis()); persist(); }
                if (isSelected(tabId)) refreshChrome();
            }
            @Override public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request.isForMainFrame()) {
                    failed = true;
                    if (isSelected(tabId)) { origin.setText(R.string.page_load_failed); progress.setVisibility(View.INVISIBLE); }
                }
            }
            @Override public void onReceivedSslError(WebView view, SslErrorHandler handler, SslError error) {
                failed = true; handler.cancel();
                if (isSelected(tabId)) explain("Secure connection failed", "The certificate could not be verified. Orbit will not bypass this protection.");
            }
            @Override public void onSafeBrowsingHit(WebView view, WebResourceRequest request, int threatType, SafeBrowsingResponse callback) {
                failed = true; callback.backToSafety(false);
            }
            @Override public boolean onRenderProcessGone(WebView view, RenderProcessGoneDetail detail) {
                // A renderer can serve multiple WebViews; release every app-owned instance.
                destroyAllEngines();
                page.removeAllViews(); page.addView(messagePanel("Web renderer stopped", "Your saved tabs remain available. Select a tab again to reopen it."));
                return true;
            }
        });
        web.setWebChromeClient(new WebChromeClient() {
            @Override public void onProgressChanged(WebView view, int value) {
                if (isSelected(tabId)) { progress.setProgress(value); progress.setVisibility(value == 100 ? View.INVISIBLE : View.VISIBLE); }
            }
            @Override public void onPermissionRequest(PermissionRequest request) {
                request.deny(); runOnUiThread(() -> toast("Camera, microphone and protected-media permissions are unavailable in this build."));
            }
            @Override public void onGeolocationPermissionsShowPrompt(String host, GeolocationPermissions.Callback callback) {
                callback.invoke(host, false, false); toast("Location access is unavailable in this build.");
            }
            @Override public boolean onShowFileChooser(WebView view, ValueCallback<android.net.Uri[]> callback, FileChooserParams params) {
                callback.onReceiveValue(null); toast("File uploads are not available in this build."); return true;
            }
            @Override public void onShowCustomView(View view, CustomViewCallback callback) {
                callback.onCustomViewHidden(); toast("Full-screen media is not available in this build.");
            }
        });
        return web;
    }

    private void configureServiceWorkers() {
        // A distinct WebView API; the ordinary WebViewClient interceptor does not cover workers.
        ServiceWorkerController workers = ServiceWorkerController.getInstance();
        workers.getServiceWorkerWebSettings().setAllowContentAccess(false);
        workers.getServiceWorkerWebSettings().setAllowFileAccess(false);
        workers.getServiceWorkerWebSettings().setBlockNetworkLoads(!state.javaScript);
        workers.setServiceWorkerClient(new ServiceWorkerClient() {
            @Override public WebResourceResponse shouldInterceptRequest(WebResourceRequest request) {
                if (clearingSiteData || !NavigationPolicy.canLoad(request.getUrl().toString(), httpsOnly))
                    return new WebResourceResponse("text/plain", "UTF-8", 403, "Blocked", new HashMap<>(), new ByteArrayInputStream(new byte[0]));
                return null;
            }
        });
    }

    private void refreshChrome() {
        BrowserState.Tab tab = state.current();
        if (!address.hasFocus()) address.setText(NavigationPolicy.HOME.equals(tab.address) ? "" : tab.address);
        origin.setText(NavigationPolicy.origin(tab.address));
        WebView web = active(); back.setEnabled(web != null && web.canGoBack()); forward.setEnabled(web != null && web.canGoForward());
        tabsButton.setText(getString(R.string.tabs_count, state.tabs().size()));
        progress.setVisibility(web != null && web.getProgress() < 100 ? View.VISIBLE : View.INVISIBLE);
    }
    private void showTabs() {
        LinearLayout items = column();
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("Tabs on this device")
                .setView(scroll(items)).setPositiveButton("New tab", (d, which) -> newTab()).setNegativeButton("Done", null).create();
        for (BrowserState.Tab tab : state.tabs()) {
            LinearLayout entry = row();
            String title = tab.title == null || tab.title.trim().isEmpty() ? NavigationPolicy.origin(tab.address) : tab.title;
            Button select = button((isSelected(tab.id) ? "Current · " : "") + title, v -> { state.select(tab.id); persist(); showSelected(); dialog.dismiss(); });
            select.setSingleLine(false); entry.addView(select, new LinearLayout.LayoutParams(0, -2, 1));
            Button close = button("Close", v -> { destroyEngine(tab.id); state.close(tab.id); persist(); showSelected(); dialog.dismiss(); showTabs(); });
            close.setContentDescription("Close tab " + title); entry.addView(close); items.addView(entry);
        }
        dialog.show();
    }
    private void newTab() {
        try { state.addTab(); persist(); showSelected(); address.requestFocus(); }
        catch (IllegalStateException ex) { explain("Tab limit", ex.getMessage()); }
    }
    private void showMenu(View anchor) {
        PopupMenu menu = new PopupMenu(this, anchor);
        String[] labels = { "Home", "Bookmark this page", "Bookmarks", "History", "Private browsing · unavailable", "My Orbit", "Settings", "Help & about", "Licenses" };
        for (int i = 0; i < labels.length; i++) menu.getMenu().add(0, i, i, labels[i]);
        menu.setOnMenuItemClickListener(item -> {
            switch (item.getItemId()) {
                case 0: navigate(NavigationPolicy.HOME); break;
                case 1:
                    try { state.bookmarkCurrent(System.currentTimeMillis()); persist(); toast("Bookmark saved on this device."); }
                    catch (IllegalStateException ex) { toast(ex.getMessage()); }
                    break;
                case 2: showPages(true); break;
                case 3: showPages(false); break;
                case 4:
                    try { Capabilities.requirePrivateEngine(); }
                    catch (UnsupportedOperationException ex) { explain("Private browsing unavailable", ex.getMessage()); }
                    break;
                case 5: explain("My Orbit · not connected", Capabilities.ACCOUNT_REASON); break;
                case 6: showSettings(); break;
                case 7: showHelp(); break;
                case 8: showLicenses(); break;
                default: return false;
            }
            return true;
        }); menu.show();
    }
    private void showPages(boolean bookmarks) {
        List<BrowserState.Page> pages = new ArrayList<>(bookmarks ? state.bookmarks() : state.history());
        if (pages.isEmpty()) { explain(bookmarks ? "Bookmarks" : "History", "No pages saved here yet. Data stays in this app on this device."); return; }
        String[] labels = new String[pages.size()];
        for (int i = 0; i < pages.size(); i++) labels[i] = (pages.get(i).title.isEmpty() ? "Untitled" : pages.get(i).title) + "\n" + pages.get(i).address;
        new AlertDialog.Builder(this).setTitle(bookmarks ? "Bookmarks" : "History · latest 250 addresses")
                .setItems(labels, (dialog, index) -> {
                    BrowserState.Page selected = pages.get(index);
                    new AlertDialog.Builder(this).setTitle(selected.title.isEmpty() ? "Saved page" : selected.title)
                            .setMessage(selected.address).setPositiveButton("Open", (d, w) -> navigate(selected.address))
                            .setNeutralButton(bookmarks ? "Remove bookmark" : "Close", (d, w) -> {
                                if (bookmarks) { state.removeBookmark(selected.address); persist(); }
                            }).setNegativeButton("Cancel", null).show();
                }).setNegativeButton("Done", null).show();
    }
    private void showSettings() {
        LinearLayout content = column(); content.setPadding(dp(20), dp(8), dp(20), dp(8));
        CheckBox dark = check(content, "Dark browser chrome", state.darkTheme);
        CheckBox js = check(content, "Allow JavaScript on websites", state.javaScript);
        CheckBox secure = check(content, "Prefer HTTPS; block direct HTTP navigation", state.httpsOnly);
        CheckBox restore = check(content, "Restore normal tabs after restart", state.restoreTabs);
        content.addView(text("Third-party cookies are blocked. Camera, microphone, location and file permissions are denied. "
                + "The HTTPS preference checks addresses and engine callbacks; it is not a whole-network firewall or a guarantee about every redirect. "
                + "Background tabs may still make website requests. Android WebView's security services are governed by its provider.", 14));
        Button search = button("Search engine: " + state.searchEngine.displayName, v -> {
            NavigationPolicy.SearchEngine[] engines = NavigationPolicy.SearchEngine.values();
            String[] names = new String[engines.length]; for (int i = 0; i < engines.length; i++) names[i] = engines[i].displayName;
            new AlertDialog.Builder(this).setTitle("Search engine").setSingleChoiceItems(names, state.searchEngine.ordinal(), (dialog, index) -> {
                state.searchEngine = engines[index]; persist(); ((Button) v).setText(getString(R.string.search_engine_name, state.searchEngine.displayName)); dialog.dismiss();
            }).setNegativeButton("Cancel", null).show();
        }); content.addView(search);
        content.addView(button("Clear browsing history", v -> new AlertDialog.Builder(this).setTitle("Clear local history?")
                .setMessage("Removes this app's saved address/title history. Cookies, site sessions, bookmarks and open tabs remain.")
                .setPositiveButton("Clear history", (d, w) -> { state.clearHistory(); persist(); toast("Local history cleared."); }).setNegativeButton("Cancel", null).show()));
        content.addView(button("Clear cookies and site data", v -> confirmClearSiteData()));
        content.addView(button("Reset saved browser state", v -> new AlertDialog.Builder(this).setTitle("Reset saved browser state?")
                .setMessage("Removes local tabs, history, bookmarks and settings. Cookies and site data are separate; clear them with their own control. This cannot be undone.")
                .setPositiveButton("Reset", (d, w) -> {
                    destroyAllEngines(); store.reset(); state = new BrowserState(BrowserState.Mode.NORMAL); httpsOnly = true;
                    persistenceWarningShown = false; persist(); buildUi(); showSelected();
                }).setNegativeButton("Cancel", null).show()));
        content.addView(text("Private browsing: unavailable\nMy Orbit: not connected; no sync requests\nNo browser-owned telemetry or automatic update checks", 14));
        new AlertDialog.Builder(this).setTitle("Settings").setView(scroll(content)).setPositiveButton("Save", (dialog, which) -> {
            boolean enginePolicyChanged = state.javaScript != js.isChecked() || state.httpsOnly != secure.isChecked();
            state.darkTheme = dark.isChecked(); state.javaScript = js.isChecked(); state.httpsOnly = secure.isChecked();
            state.restoreTabs = restore.isChecked(); httpsOnly = state.httpsOnly;
            if (enginePolicyChanged) destroyAllEngines();
            if (rendererAvailable) configureServiceWorkers();
            persist(); buildUi(); showSelected();
        }).setNegativeButton("Close", null).show();
    }
    private void confirmClearSiteData() {
        if (clearingSiteData) { toast("Cookie clearing is already in progress."); return; }
        if (!rendererAvailable) { explain("Web renderer unavailable", "Android System WebView must be available to request cookie or site-data deletion."); return; }
        new AlertDialog.Builder(this).setTitle("Clear cookies and site data?")
                .setMessage("Closes all tabs and clears WebView cookies. Requests deletion of web storage and cache, whose completion this engine API does not confirm. "
                        + "Bookmarks and Orbit history remain. This is not a comprehensive site-data erasure guarantee or private browsing.")
                .setPositiveButton("Clear site data", (dialog, which) -> {
                    clearingSiteData = true;
                    for (WebView web : engines.values()) { web.stopLoading(); web.clearCache(true); web.clearHistory(); web.clearFormData(); }
                    if (engines.isEmpty()) { WebView cleaner = new WebView(this); cleaner.clearCache(true); cleaner.clearFormData(); cleaner.destroy(); }
                    destroyAllEngines(); state.resetTabs(); persist();
                    WebStorage.getInstance().deleteAllData();
                    CookieManager.getInstance().removeAllCookies(removed -> {
                        CookieManager.getInstance().flush(); clearingSiteData = false;
                        BrowserActivity current = currentActivity.get();
                        if (current != null && !current.isDestroyed()) {
                            current.showSelected(); current.explain("Cookie clearing finished", "Cookie clearing completed. "
                                    + "Web storage and cache deletion were requested, but their completion cannot be verified by this engine API. "
                                    + "This action is not private browsing or a comprehensive data-erasure guarantee.");
                        }
                    });
                    showSelected();
                }).setNegativeButton("Cancel", null).show();
    }
    private void showHelp() {
        String help = "Orbit Navigator " + BuildConfig.VERSION_NAME + " for Android\nUnpublished native browser foundation. "
                + "Android 9+ (API 28), including resizable phone, tablet and foldable windows.\n\n"
                + "Local tabs, history, bookmarks and settings are stored inside this app. Automatic Android backup and transfer are disabled. "
                + "Do not include browsing addresses, page contents, credentials or private activity in a support report.\n\n"
                + "Limitations: no private engine, account/sync, downloads, file uploads, device permissions or full-screen media. "
                + "Engine history is in-memory per open tab; restored tabs reopen only their current address. Android System WebView "
                + "supplies website rendering and security updates. This app makes no claim to block all trackers or protect you from your network provider.\n\n"
                + "MIT licensed · Orbit Nav Pub · Maintained by Paradox. Source: github.com/TheArranger/Orbit-Navigator. "
                + "Report a problem opens a website form for you to review and submit; opening the form does not submit a report.";
        new AlertDialog.Builder(this).setTitle("Help & about").setMessage(help)
                .setPositiveButton("Done", null)
                .setNegativeButton("Report a problem", (d, w) -> {
                    try { state.addTab(); navigate("https://iamtheparadox.com/report-issue?project=orbit-navigator&platform=android"); }
                    catch (IllegalStateException ex) { explain("Tab limit", ex.getMessage()); }
                })
                .setNeutralButton("Copy safe diagnostics", (d, w) -> {
                    android.content.pm.PackageInfo webview = WebView.getCurrentWebViewPackage();
                    String diagnostic = "Orbit Navigator Android\nVersion: " + BuildConfig.VERSION_NAME
                            + "\nAndroid API: " + Build.VERSION.SDK_INT + "\nWebView: "
                            + (webview == null ? "unavailable" : webview.versionName)
                            + "\nAccount/sync: unavailable\nPrivate: unavailable\n";
                    ((ClipboardManager) getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText("Orbit support diagnostics", diagnostic));
                    toast("Copied version-only diagnostics; no addresses, device IDs or account data.");
                }).show();
    }

    private void showLicenses() {
        StringBuilder notices = new StringBuilder("Orbit Navigator uses the Android platform and the device's WebView provider. "
                + "No third-party runtime library is bundled by this Android module.\n\n");
        try {
            for (String name : new String[] { "LICENSE", "NOTICE" }) {
                try (java.io.BufferedReader reader = new java.io.BufferedReader(new java.io.InputStreamReader(
                        getAssets().open("licenses/" + name), java.nio.charset.StandardCharsets.UTF_8))) {
                    String line; while ((line = reader.readLine()) != null) notices.append(line).append('\n');
                    notices.append('\n');
                }
            }
        } catch (IOException ex) { explain("License text unavailable", "See LICENSE and NOTICE in the source repository."); return; }
        TextView body = text(notices.toString(), 14); body.setPadding(dp(20), dp(12), dp(20), dp(12)); body.setTextIsSelectable(true);
        new AlertDialog.Builder(this).setTitle("Licenses & notices").setView(scroll(body)).setPositiveButton("Done", null).show();
    }

    private void persist() {
        if (state == null || store == null) return;
        try { store.save(state); }
        catch (IOException ex) {
            if (!persistenceWarningShown) { persistenceWarningShown = true; toast("Browser changes could not be saved. See Settings if saved data needs a reset."); }
        }
    }
    private boolean isSelected(String id) { return state.current().id.equals(id); }
    private WebView active() { return engines.get(state.current().id); }
    private void destroyEngine(String id) {
        WebView web = engines.remove(id);
        if (web != null) {
            if (web.getParent() instanceof ViewGroup) ((ViewGroup) web.getParent()).removeView(web);
            web.stopLoading(); web.setWebChromeClient(null); web.setWebViewClient(new WebViewClient()); web.destroy();
        }
    }
    private void destroyAllEngines() { for (String id : new ArrayList<>(engines.keySet())) destroyEngine(id); }
    @Override protected void onPause() { persist(); for (WebView web : engines.values()) web.onPause(); super.onPause(); }
    @Override protected void onResume() { super.onResume(); WebView web = state == null ? null : active(); if (web != null) web.onResume(); }
    @Override protected void onDestroy() {
        destroyAllEngines(); if (currentActivity.get() == this) currentActivity.clear(); super.onDestroy();
    }
    @Override public void onBackPressed() { WebView web = active(); if (web != null && web.canGoBack()) web.goBack(); else super.onBackPressed(); }
    private void explain(String title, String detail) { if (!isFinishing() && !isDestroyed()) new AlertDialog.Builder(this).setTitle(title).setMessage(detail).setPositiveButton("OK", null).show(); }
    private void toast(String message) { Toast.makeText(this, message, Toast.LENGTH_LONG).show(); }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private LinearLayout column() { LinearLayout layout = new LinearLayout(this); layout.setOrientation(LinearLayout.VERTICAL); return layout; }
    private LinearLayout row() { LinearLayout layout = new LinearLayout(this); layout.setOrientation(LinearLayout.HORIZONTAL); layout.setGravity(Gravity.CENTER_VERTICAL); layout.setPadding(dp(8), 0, dp(8), 0); return layout; }
    private TextView text(String label, int size) { TextView view = new TextView(this); view.setText(label); view.setTextSize(size); view.setTextColor(ink); return view; }
    private Button button(String label, View.OnClickListener listener) { Button button = new Button(this); button.setText(label); button.setAllCaps(false); button.setMinHeight(dp(48)); button.setMinWidth(dp(48)); button.setOnClickListener(listener); return button; }
    private Button nav(LinearLayout row, String label, View.OnClickListener listener) {
        Button button = button(label, listener); button.setPadding(dp(2), 0, dp(2), 0); button.setTextSize(13);
        if ("+".equals(label)) button.setContentDescription("New tab");
        if ("Next".equals(label)) button.setContentDescription("Forward in tab history");
        row.addView(button, new LinearLayout.LayoutParams(0, dp(52), 1)); return button;
    }
    private ScrollView scroll(View content) { ScrollView scroll = new ScrollView(this); scroll.addView(content); return scroll; }
    private CheckBox check(LinearLayout parent, String label, boolean checked) { CheckBox check = new CheckBox(this); check.setText(label); check.setTextColor(ink); check.setChecked(checked); check.setMinHeight(dp(48)); parent.addView(check); return check; }
}
