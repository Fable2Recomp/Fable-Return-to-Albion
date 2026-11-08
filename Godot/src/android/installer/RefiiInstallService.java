package com.godot.game.refii;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.util.Log;
import java.io.File;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.charset.StandardCharsets;

/** Keeps user-initiated local file processing alive when the Godot activity pauses. */
public final class RefiiInstallService extends Service {
    private static final String CHANNEL = "refii-installation";
    private static final int NOTICE = 216;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private File job;
    private PowerManager.WakeLock wake;
    private String lastProgress = "";
    private final Runnable poll = new Runnable() {
        @Override public void run() {
            if (job == null) return;
            if (new File(job, "finished").isFile()) { stopSelf(); return; }
            try {
                File progress = new File(job, "progress");
                String text = progress.isFile() && progress.length() <= 2048 ? read(progress) : "Installing Fable 2";
                if (!text.equals(lastProgress)) {
                    lastProgress = text;
                    getSystemService(NotificationManager.class).notify(NOTICE, notification(text));
                }
            } catch (IOException error) { Log.w("RefiiInstall", "Progress unavailable", error); }
            handler.postDelayed(this, 1000);
        }
    };

    @Override public void onCreate() {
        super.onCreate();
        getSystemService(NotificationManager.class).createNotificationChannel(
            new NotificationChannel(CHANNEL, "Game installation", NotificationManager.IMPORTANCE_LOW));
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) { stopSelf(); return START_NOT_STICKY; }
        String id = intent.getStringExtra("job");
        if (id == null || !id.matches("job-[0-9a-f]{32}")) { stopSelf(); return START_NOT_STICKY; }
        File requested = new File(new File(getFilesDir(), "phone-jobs"), id);
        try {
            if (Files.isSymbolicLink(requested.toPath()) || Files.isSymbolicLink(requested.getParentFile().toPath()) ||
                !requested.getCanonicalFile().getParentFile().equals(requested.getParentFile().getCanonicalFile()) ||
                !new File(requested, "owner").isFile() ||
                !read(new File(requested, "owner")).equals(id))
                throw new IOException("Unowned installation job");
            if (job != null && !job.equals(requested)) return START_NOT_STICKY;
            job = requested;
            if ("cancel".equals(intent.getAction())) {
                write(new File(job, "cancel"), "User cancelled installation");
                return START_NOT_STICKY;
            }
            startForeground(NOTICE, notification("Preparing installation"), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
            if (new File(job, "finished").isFile()) { stopSelf(); return START_NOT_STICKY; }
            if (wake == null) {
                wake = getSystemService(PowerManager.class).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Refii:installation");
                // Android's dataSync budget is bounded too; never hold a wake lock indefinitely.
                wake.acquire(6L * 60 * 60 * 1000);
            }
            write(new File(job, "ready"), "foreground service active");
            handler.removeCallbacks(poll);
            handler.post(poll);
            Log.i("RefiiInstall", "Started " + id);
        } catch (IOException | RuntimeException error) {
            Log.e("RefiiInstall", "Cannot protect installation", error);
            cancel("Android installation service failed");
            stopSelf();
        }
        // Process death never automatically restarts or duplicates a partially completed build.
        return START_NOT_STICKY;
    }

    /** The app's monochrome emblem (its themed launcher icon) for the status bar, else Android's {@code fallback}. */
    private int smallIcon(int fallback) {
        int emblem = getResources().getIdentifier("icon_monochrome", "mipmap", getPackageName());
        return emblem != 0 ? emblem : fallback;
    }

    private Notification notification(String text) {
        Intent open = getPackageManager().getLaunchIntentForPackage(getPackageName());
        PendingIntent content = open == null ? null : PendingIntent.getActivity(this, 0, open, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Intent cancel = new Intent(this, RefiiInstallService.class).setAction("cancel").putExtra("job", job == null ? "" : job.getName());
        PendingIntent action = PendingIntent.getService(this, 1, cancel, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CHANNEL).setSmallIcon(smallIcon(android.R.drawable.stat_sys_download))
            .setContentTitle("Installing Fable 2").setContentText(text).setContentIntent(content)
            .setOngoing(true).setOnlyAlertOnce(true).setProgress(0, 0, true)
            .addAction(new Notification.Action.Builder(null, "Cancel installation", action).build()).build();
    }

    private void cancel(String reason) {
        if (job != null) try { write(new File(job, "cancel"), reason); }
        catch (IOException error) { Log.e("RefiiInstall", "Cannot signal cancellation", error); }
    }

    // These APIs exist on the API31 floor; Files.readString/writeString do not.
    private static String read(File file) throws IOException { return new String(Files.readAllBytes(file.toPath()), StandardCharsets.UTF_8); }
    private static void write(File file, String text) throws IOException { Files.write(file.toPath(), text.getBytes(StandardCharsets.UTF_8)); }

    /** API 35+ dataSync deadline: cancel owned children and promptly leave foreground state. */
    @Override public void onTimeout(int startId, int type) { cancel("Android background time limit reached"); stopSelf(); }

    @Override public void onDestroy() {
        handler.removeCallbacks(poll);
        if (job != null && !new File(job, "finished").isFile()) cancel("Android installation service stopped");
        if (wake != null && wake.isHeld()) wake.release();
        stopForeground(STOP_FOREGROUND_REMOVE);
        Log.i("RefiiInstall", "Stopped " + (job == null ? "" : job.getName()));
        super.onDestroy();
    }
    @Override public IBinder onBind(Intent intent) { return null; }
}
