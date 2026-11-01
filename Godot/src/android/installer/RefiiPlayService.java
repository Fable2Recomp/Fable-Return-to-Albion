package com.godot.game.refii;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.IBinder;
import android.util.Log;

/**
 * Keeps a running game's process ranked as a foreground service while the activity is in the background.
 * Without it Android drops the game to the previous-app rank (adj 700) the moment another app opens, and on
 * an 8 GB phone the low-memory killer takes a 4-5 GB game within seconds. No wake lock: the game itself
 * pauses while hidden. Stops with the task, so swiping the game away still ends it.
 */
public final class RefiiPlayService extends Service {
    private static final String CHANNEL = "refii-play";
    private static final int NOTICE = 217;

    @Override public void onCreate() {
        super.onCreate();
        getSystemService(NotificationManager.class).createNotificationChannel(
            new NotificationChannel(CHANNEL, "Game in progress", NotificationManager.IMPORTANCE_LOW));
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        String title = intent == null ? null : intent.getStringExtra("title");
        if (title == null || title.isEmpty() || title.length() > 64) title = "The game";
        try {
            Notification notice = notification(title);
            // specialUse exists from API 34; before it a typed start must match a manifest type the platform knows.
            if (Build.VERSION.SDK_INT >= 34) startForeground(NOTICE, notice, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
            else startForeground(NOTICE, notice);
            Log.i("RefiiPlay", "Protecting " + title);
        } catch (RuntimeException error) {
            Log.e("RefiiPlay", "Cannot keep the game in the foreground", error);
            stopSelf();
        }
        // A killed game is not resumed by restarting an empty service.
        return START_NOT_STICKY;
    }

    private Notification notification(String title) {
        Intent open = getPackageManager().getLaunchIntentForPackage(getPackageName());
        PendingIntent content = open == null ? null : PendingIntent.getActivity(this, 0, open, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CHANNEL).setSmallIcon(android.R.drawable.ic_media_play)
            .setContentTitle(title + " is running").setContentText("Kept in memory while you use other apps")
            .setContentIntent(content).setOngoing(true).setOnlyAlertOnce(true).build();
    }

    @Override public void onDestroy() {
        stopForeground(STOP_FOREGROUND_REMOVE);
        Log.i("RefiiPlay", "Stopped");
        super.onDestroy();
    }
    @Override public IBinder onBind(Intent intent) { return null; }
}
