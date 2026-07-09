# Astrovault

A plugin for [N.I.N.A.](https://nighttime-imaging.eu/) (Nighttime Imaging 'N' Astronomy) that
**automatically backs up your captured images to Astrovault cloud storage while you image** — so every
sub reaches the cloud without you lifting a finger.

As N.I.N.A. saves each frame during a session, the plugin drops it into an upload queue and sends it to
your Astrovault account in the background. You keep imaging; the uploads take care of themselves.

## Features

- **Automatic upload when images are saved** — no manual export step; each frame uploads as soon as
  N.I.N.A. writes it to disk.
- **Persistent queue that survives restarts** — if N.I.N.A. closes or your PC reboots mid-session, the
  queue picks up right where it left off. Nothing is lost.
- **Automatic retry with exponential backoff** — temporary network hiccups are retried on their own,
  with progressively longer delays, so a brief dropout doesn't cost you a frame.
- **Non-blocking** — uploading runs quietly in the background and never slows down your imaging or
  N.I.N.A.'s interface.

The plugin supports all of N.I.N.A.'s output formats (FITS, XISF, TIFF, PNG, JPEG) and mirrors your local
folder layout (target, filter, date, and so on) in the cloud, so your captures stay organised the same way
online as they are on your PC.

## Requirements

- **N.I.N.A. 3.2.0.9001 or newer** — the plugin will not load on older versions.
- **Windows** — N.I.N.A. is Windows-only, and the plugin uses Windows' built-in DPAPI encryption to store
  your key securely.
- **An Astrovault API key** — see [Getting an API Key](#getting-an-api-key) below.

## Install (Beta)

During the controlled beta the plugin is **side-loaded** by hand — it is not yet listed in N.I.N.A.'s
built-in plugin marketplace. (One-click marketplace install will come once the beta wraps up.)

1. Download the latest **`Astrovault.<version>.zip`** from the repository's
   [Releases](https://github.com/ashanuoc/astrovault-nina-plugin/releases) page. Beta builds are marked
   **Pre-release** (the current beta is `0.9.x`).
2. Close N.I.N.A. if it is running.
3. In Windows Explorer, paste the following into the address bar and press Enter:

   ```
   %localappdata%\NINA\Plugins\3.0.0\
   ```

   (`3.0.0` is N.I.N.A.'s plugin folder for the whole 3.x series — this path is correct even on N.I.N.A. 3.2.)
4. Create a new folder there named **`Astrovault`**, then extract the contents of the ZIP into
   it so that **`Astrovault.dll`** sits directly inside:

   ```
   %localappdata%\NINA\Plugins\3.0.0\Astrovault\Astrovault.dll
   ```
5. Start N.I.N.A. and open **Options > Plugins**. You should now see **Astrovault** in the list.

## Getting an API Key

The plugin needs an **API key** to upload images to your Astrovault account.

1. Sign in to your Astrovault account at **[vault.astrospherehub.com](https://vault.astrospherehub.com/)**.
2. Open the **API Keys** page and copy your key (it starts with `av_live_`).
3. In N.I.N.A., open **Options > Plugins > Astrovault**, paste the key into the **API Key** field, and connect.

The plugin's options also include a **"Get your API key"** link that opens the API Keys page directly.

Your key is stored **encrypted on your own machine** using Windows DPAPI — it is never written to disk in
plain text, and it only leaves your PC to authenticate with Astrovault over HTTPS.

## Configuration

1. In N.I.N.A., open **Options > Plugins > Astrovault**.
2. Paste your Astrovault **API key** into the API Key field and connect.
3. That's it — the plugin begins uploading new captures automatically as your session runs.

**About the API endpoint:** the address the plugin uploads to lives behind the **Advanced** expander in the
plugin options and already defaults to the production Astrovault host over HTTPS. Most users never need to
touch it — leave it at the default unless Astrovault support specifically tells you otherwise.

## Troubleshooting

**The plugin doesn't appear in N.I.N.A.'s plugin list.**

- Make sure `Astrovault.dll` ended up inside `%localappdata%\NINA\Plugins\3.0.0\Astrovault\`
  (not left in your Downloads folder, and not still zipped up).
- Confirm you are running **N.I.N.A. 3.2.0.9001 or newer** — the version is shown under **Help > About**.
  The plugin will not load on older builds.
- Fully close and restart N.I.N.A. after copying the files.

**It's installed and configured, but images aren't reaching the cloud.**

- Double-check the API key. A typo, an expired key, or a revoked key will stop uploads — re-paste the key to
  be sure it is exactly right.
- Leave the API endpoint at its default. For your security, the plugin **refuses to send your key over an
  unencrypted (plain `http://`) connection** to a remote host, so a hand-edited non-HTTPS endpoint will never
  connect. The built-in default is already HTTPS, so this only bites if the endpoint was changed manually.
- Check that the PC running N.I.N.A. has a working internet connection.

**Uploads don't seem to start.**

- Uploads begin **after** N.I.N.A. has finished saving each frame, so expect a short delay after every
  exposure — this is normal.
- Open the plugin's status/queue panel to see the pending, uploading, completed, and failed counts. If items
  are queued but never move, it is almost always a key or connection issue (see the section above).

**Still stuck?** N.I.N.A.'s log file (**Options > General > Open Log**) records what the plugin is doing and
is the best place to look for details — and attaching it to a support request helps a great deal.

## Support

Found a bug, or need a hand? Please open an issue on the plugin's GitHub repository:

**https://github.com/ashanuoc/astrovault-nina-plugin/issues**

## License

Astrovault is released under the **Mozilla Public License 2.0 (MPL-2.0)**.
See [LICENSE.txt](LICENSE.txt) for the full text.
