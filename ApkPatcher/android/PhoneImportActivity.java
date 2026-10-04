package wbmods;

import android.app.Activity;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.provider.OpenableColumns;
import android.widget.Toast;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;

/**
 * Receives a mod file that another app opened or shared with the game ("Open with" / "Share" on a
 * .zip or .json) and copies it into files/phone_inbox/, where the --phone-mods patch's title
 * screen code picks it up and installs it. Exists because the Discord phone app has no way to
 * copy a file's download link, only the message's link - but it does download the file, and a
 * downloaded file can be opened with this.
 *
 * Compiled once into ApkPatcher's embedded phone_import.dex (see build_dex.cmd) and added to the
 * APK as an extra classesN.dex; the activity is registered by AxmlEditor.
 */
public class PhoneImportActivity extends Activity {
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        try {
            Intent intent = getIntent();
            Uri uri = Intent.ACTION_SEND.equals(intent.getAction())
                    ? (Uri) intent.getParcelableExtra(Intent.EXTRA_STREAM)
                    : intent.getData();
            if (uri == null) {
                toast("No file was passed to the game.");
            } else {
                String name = safeName(displayName(uri));
                File inbox = new File(getFilesDir(), "phone_inbox");
                inbox.mkdirs();
                File target = new File(inbox, name);
                try (InputStream in = getContentResolver().openInputStream(uri);
                     OutputStream out = new FileOutputStream(target)) {
                    byte[] buffer = new byte[65536];
                    int read;
                    while ((read = in.read(buffer)) > 0) {
                        out.write(buffer, 0, read);
                    }
                }
                toast("Mod received. It is added on the game's title screen.");
            }
        } catch (Exception ex) {
            toast("Could not read that file: " + ex.getMessage());
        }

        Intent launch = getPackageManager().getLaunchIntentForPackage(getPackageName());
        if (launch != null) {
            startActivity(launch);
        }
        finish();
    }

    private String displayName(Uri uri) {
        String name = null;
        try (Cursor cursor = getContentResolver().query(uri, new String[] {OpenableColumns.DISPLAY_NAME}, null, null, null)) {
            if (cursor != null && cursor.moveToFirst()) {
                name = cursor.getString(0);
            }
        } catch (Exception ignored) {
            // Not every provider answers a query; the path is the fallback.
        }
        if (name == null || name.isEmpty()) {
            name = uri.getLastPathSegment();
        }
        return name == null ? "mod.zip" : name;
    }

    /** The game unzips anything that is not .json, so only the two endings matter. */
    private static String safeName(String name) {
        String cleaned = name.replaceAll("[\\\\/:*?\"<>|]", "_").trim();
        int slash = cleaned.lastIndexOf('/');
        if (slash >= 0) {
            cleaned = cleaned.substring(slash + 1);
        }
        if (cleaned.isEmpty()) {
            cleaned = "mod.zip";
        }
        String lower = cleaned.toLowerCase();
        if (!lower.endsWith(".zip") && !lower.endsWith(".json")) {
            cleaned += ".zip";
        }
        return cleaned;
    }

    private void toast(String message) {
        Toast.makeText(this, message, Toast.LENGTH_LONG).show();
    }
}
