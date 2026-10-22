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
import java.util.ArrayList;
import java.util.List;

/**
 * Receives a mod file - or several picked together - that another app opened or shared with the
 * game ("Open with" / "Share" on a .zip or .json) and copies it into files/phone_inbox/, where the --phone-mods patch's title
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
            List<Uri> uris = new ArrayList<>();
            if (Intent.ACTION_SEND_MULTIPLE.equals(intent.getAction())) {
                // Several files picked at once and shared together.
                ArrayList<Uri> many = intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM);
                if (many != null) {
                    for (Uri one : many) {
                        if (one != null) {
                            uris.add(one);
                        }
                    }
                }
            } else {
                Uri uri = Intent.ACTION_SEND.equals(intent.getAction())
                        ? (Uri) intent.getParcelableExtra(Intent.EXTRA_STREAM)
                        : intent.getData();
                if (uri != null) {
                    uris.add(uri);
                }
            }
            if (uris.isEmpty()) {
                toast("No file was passed to the game.");
            } else {
                File inbox = new File(getFilesDir(), "phone_inbox");
                inbox.mkdirs();
                int copied = 0;
                int skipped = 0;
                String problem = null;
                for (Uri uri : uris) {
                    String name = displayName(uri);
                    if (!isModFile(name)) {
                        skipped++;
                        continue;
                    }
                    try {
                        copy(uri, freeName(inbox, safeName(name)));
                        copied++;
                    } catch (Exception ex) {
                        problem = ex.getMessage();
                    }
                }
                if (copied == 0 && skipped > 0) {
                    toast("Only .zip and .json mod files can be added (.rar and .7z need a PC).");
                } else if (copied == 0) {
                    toast("Could not read that file: " + problem);
                } else if (skipped > 0) {
                    toast(copied + " mods received, " + skipped + " other file(s) skipped (only .zip and .json).");
                } else if (uris.size() == 1) {
                    toast("Mod received. It is added on the game's title screen.");
                } else {
                    toast(copied + " of " + uris.size() + " mods received. They are added on the game's title screen.");
                }
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

    private void copy(Uri uri, File target) throws Exception {
        try (InputStream in = getContentResolver().openInputStream(uri);
             OutputStream out = new FileOutputStream(target)) {
            byte[] buffer = new byte[65536];
            int read;
            while ((read = in.read(buffer)) > 0) {
                out.write(buffer, 0, read);
            }
        }
    }

    /** Two files shared together can have the same name; the second must not overwrite the first. */
    private static File freeName(File folder, String name) {
        File target = new File(folder, name);
        int dot = name.lastIndexOf('.');
        for (int n = 2; target.exists(); n++) {
            target = new File(folder, name.substring(0, dot) + "_" + n + name.substring(dot));
        }
        return target;
    }

    /** A name with some other ending (.rar, .7z, .png ...) is not a mod the game can open. A name
     *  with no ending at all is let through: some apps hand over downloads without one. */
    private static boolean isModFile(String name) {
        String lower = name.toLowerCase();
        int dot = lower.lastIndexOf('.');
        if (dot < 0 || dot < lower.length() - 6) {
            return true;
        }
        return lower.endsWith(".zip") || lower.endsWith(".json");
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
