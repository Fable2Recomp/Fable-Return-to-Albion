package com.godot.game.refii;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.wifi.WifiManager;
import org.godotengine.godot.Godot;
import org.godotengine.godot.plugin.GodotPlugin;
import org.godotengine.godot.plugin.UsedByGodot;

/** Starts the installer service from the Compile action or the launcher's automatic recompile after an update. */
public final class RefiiInstallPlugin extends GodotPlugin {
    private WifiManager.MulticastLock multicast;

    public RefiiInstallPlugin(Godot godot) { super(godot); }
    @Override public String getPluginName() { return "RefiiInstall"; }

    /** Returns an actionable failure instead of starting an unprotected background build. */
    @UsedByGodot public String begin(String job) {
        try {
            Activity activity = getActivity();
            if (activity == null) return "Android activity is unavailable";
            if (!job.matches("job-[0-9a-f]{32}")) return "Invalid installation job";
            activity.startForegroundService(new Intent(activity, RefiiInstallService.class).putExtra("job", job));
            return "";
        } catch (RuntimeException error) { return error.toString(); }
    }

    /**
     * RexNet finds co-op peers on the LAN through mDNS, which Android's Wi-Fi drops unless an app
     * holds a multicast lock. Held only while the game is in front, and only in a netplay export,
     * the one that declares CHANGE_WIFI_MULTICAST_STATE.
     */
    @Override public void onMainResume() {
        try {
            Activity activity = getActivity();
            if (activity == null || activity.checkSelfPermission(
                    android.Manifest.permission.CHANGE_WIFI_MULTICAST_STATE) != PackageManager.PERMISSION_GRANTED) return;
            if (multicast == null) {
                WifiManager wifi = (WifiManager) activity.getApplicationContext().getSystemService(Context.WIFI_SERVICE);
                if (wifi == null) return;
                multicast = wifi.createMulticastLock("refii-rexnet-mdns");
                multicast.setReferenceCounted(false);
            }
            multicast.acquire();
        } catch (RuntimeException ignored) {
            // Discovery falls back to direct connect; never fail the activity over it.
        }
    }

    @Override public void onMainPause() {
        if (multicast != null && multicast.isHeld()) multicast.release();
    }
}
